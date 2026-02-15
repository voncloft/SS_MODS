using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

[BepInPlugin("von.nightshift.il2cpp", "NightShift (IL2CPP)", "2.3.18")]
public class NightShiftPlugin : BasePlugin
{
    internal static ManualLogSource L;
    internal const bool DIAG_VERBOSE = true;
    internal const bool USE_FINISH_DAY_HOOK = true;
    internal const bool ENABLE_POLLING_FALLBACK = true;
    internal static Harmony H;

    // Day detection
    internal static bool Primed;
    internal static int LastSeenDay = int.MinValue;
    internal static bool PrimedCatchUpPending;

    // Run scheduling/guard
    internal static bool Busy;
    internal static int LastProcessedDay = int.MinValue;
    internal static int PendingHookDay = int.MinValue;

    // Worker state
    internal static NightShiftWorker Worker;

    // Tunables
    internal const float RUN_DELAY_SECONDS = 6.0f;

    // Time-slice limiter
    internal const int OPS_PER_FRAME = 50;
    internal const int FRAME_BUDGET_MS = 4;

    // Hard caps
    internal const int MAX_TOTAL_MS = 120000; // 2 minutes
    internal const int MAX_OUTER_LOOPS = 12;

    // Cleanup safety caps
    internal const int MAX_RACKS_TO_CLEAN = 2500;

    public override void Load()
    {
        L = Log;

        LogI("BOOT", "==================================================");
        LogI("BOOT", "LOADED NightShift 2.3.18 @ " + DateTime.Now);
        LogI("BOOT", "No LeanPool usage. Scene-safe runtime via polling.");
        LogI("BOOT", "FRAME_BUDGET_MS=" + FRAME_BUDGET_MS + " OPS_PER_FRAME=" + OPS_PER_FRAME);
        LogI("BOOT", "Prime-immediate scheduling enabled.");
        LogI("BOOT", "Diagnostics verbose logging=" + DIAG_VERBOSE);
        LogI("BOOT", "Immediate slot data-sync enabled.");
        LogI("BOOT", "FinishTheDay hook enabled=" + USE_FINISH_DAY_HOOK);
        LogI("BOOT", "Polling fallback enabled=" + ENABLE_POLLING_FALLBACK);
        LogI("BOOT", "==================================================");

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<NightShiftRuntime>();
            var go = new GameObject("NightShiftRuntime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<NightShiftRuntime>();
            LogI("BOOT", "Runtime injected.");
        }
        catch (Exception e)
        {
            LogE("BOOT", "Runtime inject failed: " + e);
        }

        try
        {
            if (USE_FINISH_DAY_HOOK)
            {
                H = new Harmony("von.nightshift.il2cpp.harmony");
                H.PatchAll(typeof(NightShiftPatches));
                LogI("BOOT", "Harmony patches applied.");
            }
        }
        catch (Exception e)
        {
            LogE("BOOT", "Harmony patching failed: " + e);
        }
    }

    internal static void LogI(string tag, string msg) { try { L.LogInfo("[NS] [" + tag + "] " + msg); } catch { } }
    internal static void LogW(string tag, string msg) { try { L.LogWarning("[NS] [" + tag + "] " + msg); } catch { } }
    internal static void LogE(string tag, string msg) { try { L.LogError("[NS] [" + tag + "] " + msg); } catch { } }

    internal static bool InGameplay()
    {
        // We can't reliably key off scene name across versions/mod packs.
        // Instead: if these managers exist, we are in gameplay enough to touch stock/racks.
        try
        {
            if (DayCycleManager.Instance == null) return false;
            if (RackManager.Instance == null) return false;
            if (DisplayManager.Instance == null) return false;
            if (InventoryManager.Instance == null) return false;
            if (IDManager.Instance == null) return false;
            return true;
        }
        catch { return false; }
    }

    internal static int SafeGetCurrentDay()
    {
        try
        {
            var dcm = DayCycleManager.Instance;
            if (dcm != null) return dcm.CurrentDay;
        }
        catch { }
        return -1;
    }

    internal static void ResetDayDetector(string reason)
    {
        Primed = false;
        LastSeenDay = int.MinValue;
        PrimedCatchUpPending = false;
        LogI("DAY", "Reset detector (" + reason + ")");
    }

    internal static void AbortWorker(string reason)
    {
        try
        {
            if (Worker != null && Worker.State != NightShiftWorkerState.Idle)
            {
                LogW("RUN", "Abort requested: " + reason + " (state=" + Worker.State + ")");
                Worker.ForceAbort(reason);
            }
        }
        catch { }

        Busy = false;
    }


    internal static void TryScheduleForDay(int newDay, string reason)
    {
        if (!NightShiftRuntime.SceneStable)
        {
            if (string.Equals(reason, "FinishTheDayHook", StringComparison.Ordinal))
            {
                PendingHookDay = newDay;
                LogI("SCHED", "Deferred hook day=" + newDay + " until scene stable.");
            }
            LogW("SCHED", "Blocked (scene not stable). day=" + newDay + " reason=" + reason);
            return;
        }

        if (!InGameplay())
        {
            LogW("SCHED", "Blocked (not in gameplay managers). day=" + newDay + " reason=" + reason);
            return;
        }

        if (Busy) { LogW("SCHED", "Blocked (already running). newDay=" + newDay + " reason=" + reason); return; }
        if (newDay == LastProcessedDay) { LogI("SCHED", "Skip (already processed). day=" + newDay + " reason=" + reason); return; }
        if (Worker != null && Worker.State != NightShiftWorkerState.Idle) { LogW("SCHED", "Worker already active state=" + Worker.State); return; }

        Worker = new NightShiftWorker(newDay, reason, Time.unscaledTime + RUN_DELAY_SECONDS);
        if (PendingHookDay == newDay) PendingHookDay = int.MinValue;
        LogI("SCHED", "Scheduled day=" + newDay + " reason=" + reason + " runAt=" + Worker.RunAt);
    }

    internal static void TryManualRunNow()
    {
        if (!NightShiftRuntime.SceneStable)
        {
            LogW("MANUAL", "Blocked F2 (scene not stable).");
            return;
        }

        if (!InGameplay())
        {
            LogW("MANUAL", "Blocked F2 (not in gameplay managers).");
            return;
        }

        if (Busy || (Worker != null && Worker.State != NightShiftWorkerState.Idle))
        {
            LogW("MANUAL", "Blocked F2 (already running).");
            return;
        }

        int day = SafeGetCurrentDay();
        if (day < 0) day = LastSeenDay >= 0 ? LastSeenDay : 0;

        Worker = new NightShiftWorker(day, "ManualF2", Time.unscaledTime + 0.05f);
        LogI("MANUAL", "F2 trigger scheduled day=" + day + " runAt=" + Worker.RunAt);
    }

    internal static void OnFinishTheDayHook()
    {
        try
        {
            int current = SafeGetCurrentDay();
            int target = current >= 0 ? current + 1 : LastSeenDay + 1;
            if (target < 0) target = 0;
            LogI("DAY", "HOOK HIT FinishTheDay current=" + current + " target=" + target);
            TryScheduleForDay(target, "FinishTheDayHook");
        }
        catch (Exception e)
        {
            LogE("DAY", "FinishTheDay hook failed: " + e);
        }
    }

    // IMPORTANT: DO NOT call LeanPool.Despawn here.
    // Destroy-only is stable across Save->Menu->Continue and avoids "not spawned from a pool" problems.
    internal static void SafeRemoveBoxGameObject(Box box)
    {
        if (box == null) return;

        try
        {
            var go = ((Component)box).gameObject;
            if (go == null) return;

            UnityEngine.Object.Destroy(go);
        }
        catch { }
    }
}

public enum NightShiftWorkerState
{
    Idle = 0,
    WaitingDelay = 1,
    Init = 2,
    Restock = 3,
    Reconcile = 4,
    VisualRepair = 5,
    VisualRebuild = 6,
    Cleanup = 7,
    Done = 8,
    Aborted = 9
}

public class NightShiftWorker
{
    public int Day;
    public string Reason;
    public float RunAt;

    public NightShiftWorkerState State;

    // managers
    private RackManager _rackMgr;
    private DisplayManager _dispMgr;
    private InventoryManager _invMgr;
    private IDManager _idMgr;

    // step 1 data
    private Dictionary<int, List<RackSlot>> _rackSlotsByProduct;
    private List<int> _productIds;
    private Il2CppSystem.Collections.Generic.List<DisplaySlot> _cachedSlots;
    private Il2CppSystem.Collections.Generic.List<DisplaySlot> _cachedSlotsHasProduct;

    private int _outerLoop;
    private bool _didWork;
    private int _productIndex;
    private int _displayIndex;
    private DisplaySlot[] _restockSlots;
    private int _restockSlotIndex;
    private DisplaySlot[] _reconcileSlots;
    private int _reconcileSlotIndex;
    private DisplaySlot[] _visualRepairSlots;
    private int _visualRepairSlotIndex;
    private DisplaySlot[] _visualRebuildSlots;
    private int _visualRebuildSlotIndex;

    private int _currentProductId;
    private ProductSO _currentProduct;
    private int _targetCount;

    // stats
    public int ScannedRackSlots;
    public int ScannedDisplaySlots;
    public int MovedBoxes;
    public int MovedProducts;
    public bool StoppedByCap;
    public int ReconcileScannedDisplaySlots;
    public int ReconcileAdjustedDisplaySlots;
    public int RemovedNullDisplayEntries;
    public int VisualRepairScannedSlots;
    public int VisualRepairRespawnedProducts;
    public int VisualRepairAdjustedSlots;
    public int VisualRebuildScannedSlots;
    public int VisualRebuildRebuiltSlots;
    public int VisualRebuildRespawnedProducts;
    public int VerboseStep1Logs;
    public int VerboseReconcileLogs;

    // cleanup state
    private Rack[] _racks;
    private int _rackIdx;
    private int _slotIdx;
    private int _boxIdx;
    public int RemovedEmptyBoxes;

    // timing
    private Stopwatch _swTotal;

    // abort reason
    private string _abortReason;

    public NightShiftWorker(int day, string reason, float runAt)
    {
        Day = day;
        Reason = reason;
        RunAt = runAt;
        State = NightShiftWorkerState.WaitingDelay;
    }

    public void ForceAbort(string reason)
    {
        _abortReason = reason;
        State = NightShiftWorkerState.Aborted;
    }

    private bool ManagersStillValid()
    {
        try
        {
            if (!NightShiftRuntime.SceneStable) return false;
            if (!NightShiftPlugin.InGameplay()) return false;
            return true;
        }
        catch { return false; }
    }

    public void Tick(int opsBudget)
    {
        if (!ManagersStillValid() && State != NightShiftWorkerState.WaitingDelay && State != NightShiftWorkerState.Idle)
        {
            NightShiftPlugin.LogW("RUN", "ABORT scene/manager instability detected. state=" + State);
            State = NightShiftWorkerState.Aborted;
        }

        if (_swTotal != null && _swTotal.ElapsedMilliseconds > NightShiftPlugin.MAX_TOTAL_MS)
        {
            NightShiftPlugin.LogW("RUN", "ABORT hard cap MAX_TOTAL_MS=" + NightShiftPlugin.MAX_TOTAL_MS);
            State = NightShiftWorkerState.Aborted;
        }

        if (State == NightShiftWorkerState.WaitingDelay)
        {
            if (!ManagersStillValid()) return;
            if (Time.unscaledTime < RunAt) return;
            State = NightShiftWorkerState.Init;
        }

        if (State == NightShiftWorkerState.Init)
        {
            NightShiftPlugin.Busy = true;

            _rackMgr = RackManager.Instance;
            _dispMgr = DisplayManager.Instance;
            _invMgr = InventoryManager.Instance;
            _idMgr = IDManager.Instance;

            _swTotal = Stopwatch.StartNew();

            NightShiftPlugin.LogI("RUN", "ENTER day=" + Day + " reason=" + Reason);

            if (_rackMgr == null || _dispMgr == null || _invMgr == null || _idMgr == null)
            {
                NightShiftPlugin.LogW("RUN", "ABORT missing managers.");
                State = NightShiftWorkerState.Aborted;
                return;
            }

            _rackSlotsByProduct = new Dictionary<int, List<RackSlot>>();

            try
            {
                foreach (var kv in _rackMgr.RackSlots)
                {
                    int pid = kv.Key;
                    var list = kv.Value;
                    var copy = new List<RackSlot>();

                    if (list != null)
                    {
                        foreach (var s in list)
                        {
                            if (s == null) continue;
                            try
                            {
                                var rack = s.m_Rack;
                                if (rack == null) continue;
                                float y = ((Component)rack).transform.position.y;
                                if (y == 10f) continue; // skip "warehouse" racks
                            }
                            catch { continue; }

                            copy.Add(s);
                        }
                    }

                    _rackSlotsByProduct[pid] = copy;
                }
            }
            catch (Exception e)
            {
                NightShiftPlugin.LogW("RUN", "ABORT rack copy failed ex=" + e);
                State = NightShiftWorkerState.Aborted;
                return;
            }

            int rackSlotCount = 0;
            foreach (var kv in _rackSlotsByProduct)
                rackSlotCount += kv.Value != null ? kv.Value.Count : 0;
            ScannedRackSlots = rackSlotCount;

            try { _productIds = ((Dictionary<int, int>)(object)_invMgr.Products).Keys.ToList(); }
            catch { _productIds = _rackSlotsByProduct.Keys.Where(x => x != 0).ToList(); }

            _cachedSlots = new Il2CppSystem.Collections.Generic.List<DisplaySlot>();
            _cachedSlotsHasProduct = new Il2CppSystem.Collections.Generic.List<DisplaySlot>();

            _outerLoop = 0;
            _didWork = true;
            _productIndex = 0;
            _displayIndex = 0;
            _restockSlots = null;
            _restockSlotIndex = 0;
            _reconcileSlots = null;
            _reconcileSlotIndex = 0;
            _visualRepairSlots = null;
            _visualRepairSlotIndex = 0;
            _visualRebuildSlots = null;
            _visualRebuildSlotIndex = 0;

            NightShiftPlugin.LogI("STEP1", "BEGIN day=" + Day + " products=" + _productIds.Count + " rackSlots=" + ScannedRackSlots);
            State = NightShiftWorkerState.Restock;
        }

        var swFrame = Stopwatch.StartNew();

        if (State == NightShiftWorkerState.Restock)
        {
            if (_restockSlots == null)
            {
                try { _restockSlots = UnityEngine.Object.FindObjectsOfType<DisplaySlot>(); }
                catch { _restockSlots = new DisplaySlot[0]; }
                _restockSlotIndex = 0;
                NightShiftPlugin.LogI("STEP1", "Shelf-first restock scan slots=" + _restockSlots.Length);
            }

            int ops = 0;

            while (ops < opsBudget)
            {
                if (swFrame.ElapsedMilliseconds >= NightShiftPlugin.FRAME_BUDGET_MS)
                    return;

                if (!ManagersStillValid())
                {
                    NightShiftPlugin.LogW("STEP1", "ABORT managers invalid mid-run.");
                    State = NightShiftWorkerState.Aborted;
                    break;
                }

                if (_restockSlotIndex >= _restockSlots.Length)
                {
                    NightShiftPlugin.LogI("STEP1", "END movedBoxes=" + MovedBoxes
                        + " movedProducts=" + MovedProducts
                        + " scannedDisplaySlots=" + ScannedDisplaySlots
                        + " verboseStep1Logs=" + VerboseStep1Logs
                        + " loops=1"
                        + " stoppedByCap=" + StoppedByCap);
                    State = NightShiftWorkerState.Reconcile;
                    break;
                }

                var slot = _restockSlots[_restockSlotIndex];
                _restockSlotIndex++;
                ops++;
                if (slot == null) continue;

                ScannedDisplaySlots++;
                int slotPid;
                if (!TryGetSlotProductId(slot, out slotPid) || slotPid <= 0) continue;

                _currentProductId = slotPid;
                _currentProduct = null;
                try { _currentProduct = _idMgr.ProductSO(_currentProductId); } catch { }
                if (_currentProduct == null) continue;

                int targetCount = 0;
                try { targetCount = _currentProduct.GridLayoutInStorage.productCount; } catch { }
                if (targetCount <= 0) continue;

                int currentDisplayCount = CompactDisplayProducts(slot);
                int dataCount = -1;
                TryGetSlotDataCount(slot, out dataCount);
                bool slotFullFlag = false;
                try { slotFullFlag = slot.Full; } catch { }

                if (currentDisplayCount < targetCount)
                {
                    int need = targetCount - currentDisplayCount;
                    if (NightShiftPlugin.DIAG_VERBOSE)
                    {
                        VerboseStep1Logs++;
                        NightShiftPlugin.LogI("STEP1.DBG", "pid=" + _currentProductId
                            + " slotScanIdx=" + _restockSlotIndex + "/" + _restockSlots.Length
                            + " curVisible=" + currentDisplayCount
                            + " dataCount=" + dataCount
                            + " target=" + targetCount
                            + " need=" + need
                            + " slotFull=" + slotFullFlag);
                    }

                    if (_rackSlotsByProduct.TryGetValue(_currentProductId, out var racksForProduct) &&
                        racksForProduct != null && racksForProduct.Count > 0)
                    {
                        int guard = 0;
                        while (need > 0 && guard < 20)
                        {
                            guard++;
                            RackSlot rackSlot = null;
                            Box box = null;

                            for (int tries = 0; tries < 10 && (rackSlot == null || box == null); tries++)
                            {
                                var candidate = racksForProduct[UnityEngine.Random.Range(0, racksForProduct.Count)];
                                if (candidate == null) continue;
                                var boxes = candidate.m_Boxes;
                                if (boxes == null || boxes.Count == 0) continue;
                                if (!candidate.HasProduct) continue;
                                var last = boxes[boxes.Count - 1];
                                if (last == null || last.m_Data == null) continue;
                                if (last.m_Data.ProductCount <= 0) continue;
                                rackSlot = candidate;
                                box = last;
                            }

                            if (rackSlot == null || box == null)
                            {
                                if (NightShiftPlugin.DIAG_VERBOSE)
                                    NightShiftPlugin.LogI("STEP1.DBG", "pid=" + _currentProductId + " no eligible rack/box for remaining need=" + need);
                                break;
                            }

                            _didWork = true;

                            try
                            {
                                int beforeVisible = currentDisplayCount;
                                int beforeData = dataCount;
                                int beforeBoxCount = box.m_Data.ProductCount;
                                int take = box.m_Data.ProductCount <= need ? box.m_Data.ProductCount : need;

                                slot.SpawnProduct(_currentProductId, take);

                                if (box.m_Data.ProductCount <= take)
                                {
                                    rackSlot.TakeBoxFromRack();
                                    _invMgr.RemoveBox(box.Data);
                                    NightShiftPlugin.SafeRemoveBoxGameObject(box);
                                    MovedBoxes++;
                                }
                                else
                                {
                                    try { box.DespawnProducts(); } catch { }
                                    box.m_Data.ProductCount -= take;
                                }

                                MovedProducts += take;
                                need -= take;

                                currentDisplayCount = CompactDisplayProducts(slot);
                                TryGetSlotDataCount(slot, out dataCount);
                                TrySetSlotDataCount(slot, currentDisplayCount);
                                TryGetSlotDataCount(slot, out dataCount);

                                if (NightShiftPlugin.DIAG_VERBOSE)
                                {
                                    int afterBoxCount = -1;
                                    try { if (box != null && box.m_Data != null) afterBoxCount = box.m_Data.ProductCount; } catch { }
                                    NightShiftPlugin.LogI("STEP1.DBG", "move pid=" + _currentProductId
                                        + " take=" + take
                                        + " needRemaining=" + need
                                        + " boxBefore=" + beforeBoxCount + " boxAfter=" + afterBoxCount
                                        + " visBefore=" + beforeVisible + " visAfter=" + currentDisplayCount
                                        + " dataBefore=" + beforeData + " dataAfter=" + dataCount);
                                }

                                try { rackSlot.SetLabel(); } catch { }
                                try { slot.SetLabel(); } catch { }
                                try { slot.SetPriceTag(); } catch { }
                            }
                            catch (Exception e)
                            {
                                NightShiftPlugin.LogW("STEP1", "move failed pid=" + _currentProductId + " ex=" + e);
                                break;
                            }
                        }
                    }
                    else if (NightShiftPlugin.DIAG_VERBOSE)
                    {
                        int rackCount = 0;
                        try { if (_rackSlotsByProduct.TryGetValue(_currentProductId, out var r) && r != null) rackCount = r.Count; } catch { }
                        NightShiftPlugin.LogI("STEP1.DBG", "pid=" + _currentProductId + " no rack slots list (count=" + rackCount + ")");
                    }
                }
                else if (NightShiftPlugin.DIAG_VERBOSE)
                {
                    TrySetSlotDataCount(slot, currentDisplayCount);
                    TryGetSlotDataCount(slot, out dataCount);
                    NightShiftPlugin.LogI("STEP1.DBG", "pid=" + _currentProductId
                        + " slotScanIdx=" + _restockSlotIndex + " skipped-full"
                        + " curVisible=" + currentDisplayCount
                        + " dataCount=" + dataCount
                        + " target=" + targetCount
                        + " slotFull=" + slotFullFlag);
                }
            }

            return;
        }

        if (State == NightShiftWorkerState.Reconcile)
        {
            if (_reconcileSlots == null)
            {
                NightShiftPlugin.LogI("STEP1.5", "BEGIN");
                try { _reconcileSlots = UnityEngine.Object.FindObjectsOfType<DisplaySlot>(); }
                catch { _reconcileSlots = new DisplaySlot[0]; }
                _reconcileSlotIndex = 0;
            }

            int ops = 0;

            while (ops < opsBudget)
            {
                if (swFrame.ElapsedMilliseconds >= NightShiftPlugin.FRAME_BUDGET_MS)
                    return;

                if (!ManagersStillValid())
                {
                    NightShiftPlugin.LogW("STEP1.5", "ABORT managers invalid during reconciliation.");
                    State = NightShiftWorkerState.Aborted;
                    break;
                }

                if (_reconcileSlotIndex >= _reconcileSlots.Length)
                {
                    NightShiftPlugin.LogI("STEP1.5", "END scannedDisplaySlots=" + ReconcileScannedDisplaySlots
                        + " adjustedDisplaySlots=" + ReconcileAdjustedDisplaySlots
                        + " verboseReconcileLogs=" + VerboseReconcileLogs);
                    State = NightShiftWorkerState.VisualRepair;
                    break;
                }

                var slot = _reconcileSlots[_reconcileSlotIndex];
                _reconcileSlotIndex++;
                ops++;

                if (slot == null) continue;

                ReconcileScannedDisplaySlots++;
                int reconcilePid;
                if (!TryGetSlotProductId(slot, out reconcilePid) || reconcilePid <= 0) continue;

                int visibleCount = CompactDisplayProducts(slot);

                int beforeData = -1;
                bool hasDataCount = TryGetSlotDataCount(slot, out beforeData);
                bool mismatch = !hasDataCount || beforeData != visibleCount;
                if (TrySetSlotDataCount(slot, visibleCount))
                {
                    if (mismatch) ReconcileAdjustedDisplaySlots++;
                    if (NightShiftPlugin.DIAG_VERBOSE && mismatch)
                    {
                        VerboseReconcileLogs++;
                        int pid = -1;
                        bool fullFlag = false;
                        int productCount = -1;
                        try { pid = slot.ProductID; } catch { }
                        try { fullFlag = slot.Full; } catch { }
                        try { productCount = slot.ProductCount; } catch { }
                        NightShiftPlugin.LogI("STEP1.5.DBG", "slotMismatch pid=" + pid
                            + " dataBefore=" + beforeData
                            + " dataAfter=" + visibleCount
                            + " visible=" + visibleCount
                            + " productCountProp=" + productCount
                            + " full=" + fullFlag);
                    }
                    try { slot.SetLabel(); } catch { }
                    try { slot.SetPriceTag(); } catch { }
                }
                else if (NightShiftPlugin.DIAG_VERBOSE && mismatch)
                {
                    int pid = -1;
                    try { pid = slot.ProductID; } catch { }
                    NightShiftPlugin.LogW("STEP1.5.DBG", "failed-set-data-count pid=" + pid + " visible=" + visibleCount + " beforeData=" + beforeData);
                }
                else if (NightShiftPlugin.DIAG_VERBOSE && !hasDataCount)
                {
                    int pid = -1;
                    try { pid = slot.ProductID; } catch { }
                    NightShiftPlugin.LogW("STEP1.5.DBG", "failed-normalize-data-count pid=" + pid + " visible=" + visibleCount);
                }
            }

            return;
        }

        if (State == NightShiftWorkerState.VisualRepair)
        {
            if (_visualRepairSlots == null)
            {
                NightShiftPlugin.LogI("STEP1.75", "BEGIN");
                try { _visualRepairSlots = UnityEngine.Object.FindObjectsOfType<DisplaySlot>(); }
                catch { _visualRepairSlots = new DisplaySlot[0]; }
                _visualRepairSlotIndex = 0;
            }

            int ops = 0;
            while (ops < opsBudget)
            {
                if (swFrame.ElapsedMilliseconds >= NightShiftPlugin.FRAME_BUDGET_MS)
                    return;

                if (!ManagersStillValid())
                {
                    NightShiftPlugin.LogW("STEP1.75", "ABORT managers invalid during visual repair.");
                    State = NightShiftWorkerState.Aborted;
                    break;
                }

                if (_visualRepairSlotIndex >= _visualRepairSlots.Length)
                {
                    NightShiftPlugin.LogI("STEP1.75", "END scannedSlots=" + VisualRepairScannedSlots
                        + " adjustedSlots=" + VisualRepairAdjustedSlots
                        + " respawnedProducts=" + VisualRepairRespawnedProducts);
                    if (string.Equals(Reason, "ManualF2", StringComparison.Ordinal))
                    {
                        NightShiftPlugin.LogI("STEP1.75", "Manual run detected; skipping STEP1.9 visual rebuild.");
                        State = NightShiftWorkerState.Cleanup;
                    }
                    else
                    {
                        State = NightShiftWorkerState.VisualRebuild;
                    }
                    break;
                }

                var slot = _visualRepairSlots[_visualRepairSlotIndex];
                _visualRepairSlotIndex++;
                ops++;

                if (slot == null) continue;
                VisualRepairScannedSlots++;

                int pid;
                if (!TryGetSlotProductId(slot, out pid) || pid <= 0) continue;

                int dataCount;
                if (!TryGetSlotDataCount(slot, out dataCount)) continue;
                if (dataCount <= 0) continue;

                int activeCount = CountActiveDisplayProducts(slot);
                if (activeCount >= dataCount) continue;

                int missing = dataCount - activeCount;
                if (missing <= 0) continue;

                // Temporarily align data to currently active visuals so SpawnProduct can create missing visuals.
                if (!TrySetSlotDataCount(slot, activeCount)) continue;
                try { slot.SpawnProduct(pid, missing); } catch { }
                TrySetSlotDataCount(slot, dataCount);

                VisualRepairAdjustedSlots++;
                VisualRepairRespawnedProducts += missing;

                try { slot.SetLabel(); } catch { }
                try { slot.SetPriceTag(); } catch { }

                if (NightShiftPlugin.DIAG_VERBOSE)
                    NightShiftPlugin.LogI("STEP1.75.DBG", "pid=" + pid + " data=" + dataCount + " activeBefore=" + activeCount + " respawned=" + missing);
            }

            return;
        }

        if (State == NightShiftWorkerState.VisualRebuild)
        {
            if (_visualRebuildSlots == null)
            {
                NightShiftPlugin.LogI("STEP1.9", "BEGIN");
                try { _visualRebuildSlots = UnityEngine.Object.FindObjectsOfType<DisplaySlot>(); }
                catch { _visualRebuildSlots = new DisplaySlot[0]; }
                _visualRebuildSlotIndex = 0;
            }

            int ops = 0;
            while (ops < opsBudget)
            {
                if (swFrame.ElapsedMilliseconds >= NightShiftPlugin.FRAME_BUDGET_MS)
                    return;

                if (!ManagersStillValid())
                {
                    NightShiftPlugin.LogW("STEP1.9", "ABORT managers invalid during visual rebuild.");
                    State = NightShiftWorkerState.Aborted;
                    break;
                }

                if (_visualRebuildSlotIndex >= _visualRebuildSlots.Length)
                {
                    NightShiftPlugin.LogI("STEP1.9", "END scannedSlots=" + VisualRebuildScannedSlots
                        + " rebuiltSlots=" + VisualRebuildRebuiltSlots
                        + " respawnedProducts=" + VisualRebuildRespawnedProducts);
                    State = NightShiftWorkerState.Cleanup;
                    break;
                }

                var slot = _visualRebuildSlots[_visualRebuildSlotIndex];
                _visualRebuildSlotIndex++;
                ops++;

                if (slot == null) continue;
                VisualRebuildScannedSlots++;

                int pid;
                if (!TryGetSlotProductId(slot, out pid) || pid <= 0) continue;

                int dataCount;
                if (!TryGetSlotDataCount(slot, out dataCount)) continue;
                if (dataCount <= 0) continue;

                int logicalCount = -1;
                try { logicalCount = slot.ProductCount; } catch { }
                if (logicalCount < 0) logicalCount = dataCount;

                int targetFromSo = 0;
                try
                {
                    var pso = _idMgr != null ? _idMgr.ProductSO(pid) : null;
                    if (pso != null && pso.GridLayoutInStorage != null)
                        targetFromSo = pso.GridLayoutInStorage.productCount;
                }
                catch { }

                int desiredCount = targetFromSo > 0 ? targetFromSo : (dataCount > 0 ? dataCount : logicalCount);
                if (desiredCount <= 0) continue;

                int activeBefore = CountActiveDisplayProducts(slot);

                int cleared = 0;
                try
                {
                    var products = slot.m_Products;
                    if (products != null) cleared = products.Count;
                }
                catch { }

                try { slot.Clear(); } catch { continue; }
                TrySetSlotDataCount(slot, 0);

                try { slot.SpawnProduct(pid, desiredCount); } catch { }

                int finalVisible = CompactDisplayProducts(slot);
                if (finalVisible < desiredCount)
                {
                    int retryMissing = desiredCount - finalVisible;
                    TrySetSlotDataCount(slot, finalVisible);
                    try { slot.SpawnProduct(pid, retryMissing); } catch { }
                    finalVisible = CompactDisplayProducts(slot);
                }

                TrySetSlotDataCount(slot, finalVisible);

                VisualRebuildRebuiltSlots++;
                VisualRebuildRespawnedProducts += finalVisible;

                try { slot.SetLabel(); } catch { }
                try { slot.SetPriceTag(); } catch { }

                if (NightShiftPlugin.DIAG_VERBOSE)
                    NightShiftPlugin.LogI("STEP1.9.DBG", "pid=" + pid
                        + " data=" + dataCount
                        + " logical=" + logicalCount
                        + " activeBefore=" + activeBefore
                        + " cleared=" + cleared
                        + " desired=" + desiredCount
                        + " finalVisible=" + finalVisible);
            }

            return;
        }

        if (State == NightShiftWorkerState.Cleanup)
        {
            if (!ManagersStillValid())
            {
                NightShiftPlugin.LogW("STEP2", "ABORT managers invalid entering cleanup.");
                State = NightShiftWorkerState.Aborted;
            }

            if (_racks == null)
            {
                NightShiftPlugin.LogI("STEP2", "BEGIN");

                Rack[] found;
                try { found = UnityEngine.Object.FindObjectsOfType<Rack>(); }
                catch { found = new Rack[0]; }

                if (found != null && found.Length > NightShiftPlugin.MAX_RACKS_TO_CLEAN)
                {
                    NightShiftPlugin.LogW("STEP2", "Skip cleanup: too many racks found=" + found.Length + " cap=" + NightShiftPlugin.MAX_RACKS_TO_CLEAN);
                    _racks = new Rack[0];
                }
                else
                {
                    _racks = found ?? new Rack[0];
                }

                _rackIdx = 0;
                _slotIdx = 0;
                _boxIdx = -1;
            }

            int ops = 0;

            while (ops < opsBudget)
            {
                if (swFrame.ElapsedMilliseconds >= NightShiftPlugin.FRAME_BUDGET_MS)
                    return;

                if (!ManagersStillValid())
                {
                    NightShiftPlugin.LogW("STEP2", "ABORT managers invalid mid-cleanup.");
                    State = NightShiftWorkerState.Aborted;
                    break;
                }

                if (_rackIdx >= _racks.Length)
                {
                    NightShiftPlugin.LogI("STEP2", "END removedEmptyBoxes=" + RemovedEmptyBoxes);
                    State = NightShiftWorkerState.Done;
                    break;
                }

                var rack = _racks[_rackIdx];
                if (rack == null || rack.RackSlots == null)
                {
                    _rackIdx++;
                    _slotIdx = 0;
                    _boxIdx = -1;
                    continue;
                }

                if (_slotIdx >= rack.RackSlots.Count)
                {
                    _rackIdx++;
                    _slotIdx = 0;
                    _boxIdx = -1;
                    continue;
                }

                var slot = rack.RackSlots[_slotIdx];
                if (slot == null || slot.m_Boxes == null || slot.m_Boxes.Count == 0)
                {
                    _slotIdx++;
                    _boxIdx = -1;
                    continue;
                }

                if (_boxIdx == -1)
                    _boxIdx = slot.m_Boxes.Count - 1;

                if (_boxIdx < 0)
                {
                    try { slot.SetLabel(); } catch { }
                    _slotIdx++;
                    _boxIdx = -1;
                    continue;
                }

                var box = slot.m_Boxes[_boxIdx];
                _boxIdx--;

                if (box == null || box.m_Data == null) { ops++; continue; }
                if (box.m_Data.ProductCount > 0) { ops++; continue; }

                try { slot.m_Boxes.Remove(box); } catch { }
                try { _invMgr.RemoveBox(box.Data); } catch { }

                NightShiftPlugin.SafeRemoveBoxGameObject(box);

                RemovedEmptyBoxes++;
                ops++;
            }

            return;
        }

        if (State == NightShiftWorkerState.Done || State == NightShiftWorkerState.Aborted)
        {
            try { if (_swTotal != null) _swTotal.Stop(); } catch { }

            if (State == NightShiftWorkerState.Done)
            {
                NightShiftPlugin.LastProcessedDay = Day;
                NightShiftPlugin.LogI("RUN", "END day=" + Day + " reason=" + Reason
                    + " total_ms=" + (_swTotal != null ? _swTotal.ElapsedMilliseconds : -1)
                    + " movedBoxes=" + MovedBoxes
                    + " movedProducts=" + MovedProducts
                    + " reconciledSlots=" + ReconcileAdjustedDisplaySlots
                    + " visualRepairSlots=" + VisualRepairAdjustedSlots
                    + " visualRepairRespawned=" + VisualRepairRespawnedProducts
                    + " visualRebuildSlots=" + VisualRebuildRebuiltSlots
                    + " visualRebuildRespawned=" + VisualRebuildRespawnedProducts
                    + " removedNullDisplayEntries=" + RemovedNullDisplayEntries
                    + " verboseStep1Logs=" + VerboseStep1Logs
                    + " verboseReconcileLogs=" + VerboseReconcileLogs
                    + " removedEmpty=" + RemovedEmptyBoxes
                    + " stoppedByCap=" + StoppedByCap);
            }
            else
            {
                NightShiftPlugin.LogW("RUN", "ABORTED day=" + Day + " reason=" + Reason
                    + " abortReason=" + (_abortReason ?? "(none)")
                    + " total_ms=" + (_swTotal != null ? _swTotal.ElapsedMilliseconds : -1));
            }

            NightShiftPlugin.Busy = false;
            State = NightShiftWorkerState.Idle;
            return;
        }
    }

    private int CompactDisplayProducts(DisplaySlot slot)
    {
        if (slot == null) return 0;

        var products = slot.m_Products;
        if (products == null) return 0;

        int removed = 0;
        for (int i = products.Count - 1; i >= 0; i--)
        {
            bool invalid = !IsValidVisibleProduct(products[i]);

            if (invalid)
            {
                try { products.RemoveAt(i); removed++; } catch { }
            }
        }

        if (removed > 0)
        {
            RemovedNullDisplayEntries += removed;
            if (NightShiftPlugin.DIAG_VERBOSE)
            {
                int pid = -1;
                try { pid = slot.ProductID; } catch { }
                NightShiftPlugin.LogI("STEP1.5.DBG", "compact removedNullEntries=" + removed + " pid=" + pid + " remaining=" + products.Count);
            }
        }

        return products.Count;
    }

    private bool TryGetSlotDataCount(DisplaySlot slot, out int count)
    {
        count = -1;
        if (slot == null) return false;

        ItemQuantity data = null;
        try { data = slot.Data; } catch { }
        if (data == null) return false;

        try
        {
            count = data.FirstItemCount;
            return true;
        }
        catch { }

        try
        {
            var products = (Dictionary<int, int>)(object)data.Products;
            if (products == null) return false;

            int pid = -1;
            try { pid = slot.ProductID; } catch { }

            if (pid > 0 && products.TryGetValue(pid, out var byPid))
            {
                count = byPid;
                return true;
            }

            if (products.TryGetValue(0, out var byZero))
            {
                count = byZero;
                return true;
            }

            if (products.Count > 0)
            {
                count = products.Values.First();
                return true;
            }

            count = 0;
            return true;
        }
        catch { }

        return false;
    }

    private bool TrySetSlotDataCount(DisplaySlot slot, int value)
    {
        if (slot == null) return false;

        ItemQuantity data = null;
        try { data = slot.Data; } catch { }
        if (data == null) return false;

        try
        {
            data.FirstItemCount = value;
            return true;
        }
        catch { }

        try
        {
            var products = (Dictionary<int, int>)(object)data.Products;
            if (products == null) return false;

            int pid = -1;
            try { pid = slot.ProductID; } catch { }
            if (pid > 0)
            {
                if (products.ContainsKey(pid)) products[pid] = value;
                else products.Add(pid, value);
                return true;
            }

            if (products.Count > 0)
            {
                int key = products.Keys.First();
                products[key] = value;
                return true;
            }
        }
        catch { }

        return false;
    }

    private bool TryGetSlotProductId(DisplaySlot slot, out int productId)
    {
        productId = 0;
        if (slot == null) return false;

        try
        {
            productId = slot.ProductID;
            if (productId > 0) return true;
        }
        catch { }

        try
        {
            var data = slot.Data;
            if (data != null)
            {
                int fromData = data.FirstItemID;
                if (fromData > 0) { productId = fromData; return true; }
            }
        }
        catch { }

        try
        {
            var data = slot.Data;
            var products = data != null ? (Dictionary<int, int>)(object)data.Products : null;
            if (products != null)
            {
                foreach (var kv in products)
                {
                    if (kv.Key > 0)
                    {
                        productId = kv.Key;
                        return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private int CountActiveDisplayProducts(DisplaySlot slot)
    {
        if (slot == null) return 0;
        var products = slot.m_Products;
        if (products == null) return 0;

        int count = 0;
        for (int i = 0; i < products.Count; i++)
        {
            if (IsValidVisibleProduct(products[i])) count++;
        }
        return count;
    }

    private bool IsValidVisibleProduct(Product p)
    {
        if (p == null) return false;

        try
        {
            var c = (Component)p;
            if (c == null) return false;
            var go = c.gameObject;
            if (go == null) return false;

            // Do not require active/renderer-enabled here.
            // Product renderer state can be temporarily disabled by culling/streaming
            // while the slot data is still valid and should remain counted.
            return true;
        }
        catch { return false; }
    }
}

public class NightShiftRuntime : MonoBehaviour
{
    private float _nextBeat;
    private float _nextDayCheck;

    // Scene stability gate: pause worker + day detector briefly after changes.
    internal static bool SceneStable;
    internal static float StableAt;

    // Polling-based scene change detection (IL2CPP-safe)
    private int _lastActiveSceneHandle;
    private string _lastActiveSceneName;
    private int _lastSceneCount;

    private void Start()
    {
        NightShiftPlugin.LogI("RT", "Start. Heartbeat 10s. DayCheck 0.5s.");

        _nextBeat = Time.unscaledTime + 10f;
        _nextDayCheck = Time.unscaledTime + 0.5f;

        var s = SceneManager.GetActiveScene();
        _lastActiveSceneHandle = s.handle;
        _lastActiveSceneName = s.name;
        _lastSceneCount = SceneManager.sceneCount;

        // Start unstable briefly so we don't touch managers during initial load.
        SceneStable = false;
        StableAt = Time.unscaledTime + 3.0f;
        NightShiftPlugin.LogI("SCENE", "Boot unstable. StableAt=" + StableAt + " active=" + (_lastActiveSceneName ?? "(null)"));
    }

    private void MarkSceneUnstable(string why)
    {
        NightShiftPlugin.AbortWorker("Scene transition: " + why);
        NightShiftPlugin.ResetDayDetector("Scene transition: " + why);

        SceneStable = false;
        StableAt = Time.unscaledTime + 3.0f;
        NightShiftPlugin.LogI("SCENE", "Unstable (" + why + "). StableAt=" + StableAt);
    }

    private void Update()
    {
        try
        {
            bool pressed = Input.GetKeyDown(KeyCode.F2)
                || Input.GetKeyDown(KeyCode.F9)
                || (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.R))
                || (Input.GetKey(KeyCode.RightControl) && Input.GetKeyDown(KeyCode.R));

            if (pressed)
            {
                NightShiftPlugin.TryManualRunNow();
            }
        }
        catch { }

        // Detect scene changes without subscribing to SceneManager events.
        try
        {
            var s = SceneManager.GetActiveScene();
            int scCount = SceneManager.sceneCount;

            if (s.handle != _lastActiveSceneHandle)
            {
                string oldName = _lastActiveSceneName ?? "(null)";
                string newName = s.name ?? "(null)";
                _lastActiveSceneHandle = s.handle;
                _lastActiveSceneName = newName;

                MarkSceneUnstable("activeScene handle change " + oldName + " -> " + newName);
            }
            else if (scCount != _lastSceneCount)
            {
                int prev = _lastSceneCount;
                _lastSceneCount = scCount;
                MarkSceneUnstable("sceneCount change " + prev + " -> " + scCount + " active=" + (_lastActiveSceneName ?? "(null)"));
            }
        }
        catch
        {
            MarkSceneUnstable("exception while polling scenes");
        }

        if (!SceneStable && Time.unscaledTime >= StableAt)
        {
            SceneStable = true;
            NightShiftPlugin.LogI("SCENE", "Stable now. t=" + Time.unscaledTime + " active=" + SceneManager.GetActiveScene().name);
        }

        if (SceneStable && NightShiftPlugin.PendingHookDay != int.MinValue && NightShiftPlugin.InGameplay())
        {
            int pending = NightShiftPlugin.PendingHookDay;
            NightShiftPlugin.PendingHookDay = int.MinValue;
            NightShiftPlugin.LogI("DAY", "Applying deferred end-day hook schedule for day=" + pending);
            NightShiftPlugin.TryScheduleForDay(pending, "FinishTheDayHookDeferred");
        }

        if (Time.unscaledTime >= _nextBeat)
        {
            _nextBeat = Time.unscaledTime + 10f;
            NightShiftPlugin.LogI("RT", "Heartbeat t=" + Time.unscaledTime + " stable=" + SceneStable + " scene=" + SceneManager.GetActiveScene().name);
        }

        // Run worker only when stable and gameplay managers exist.
        if (SceneStable && NightShiftPlugin.InGameplay() &&
            NightShiftPlugin.Worker != null && NightShiftPlugin.Worker.State != NightShiftWorkerState.Idle)
        {
            NightShiftPlugin.Worker.Tick(NightShiftPlugin.OPS_PER_FRAME);
        }

        if (Time.unscaledTime >= _nextDayCheck)
        {
            _nextDayCheck = Time.unscaledTime + 0.5f;

            if (!SceneStable) return;
            if (!NightShiftPlugin.InGameplay()) return;

            int day = NightShiftPlugin.SafeGetCurrentDay();
            if (day < 0) return;

            if (!NightShiftPlugin.Primed)
            {
                NightShiftPlugin.Primed = true;
                NightShiftPlugin.LastSeenDay = day;
                NightShiftPlugin.PrimedCatchUpPending = true;
                NightShiftPlugin.LogI("DAY", "Primed day=" + day);
                return;
            }

            if (day != NightShiftPlugin.LastSeenDay)
            {
                int prev = NightShiftPlugin.LastSeenDay;
                NightShiftPlugin.LastSeenDay = day;
                NightShiftPlugin.PrimedCatchUpPending = false;

                NightShiftPlugin.LogI("DAY", "Day changed " + prev + " -> " + day);
                NightShiftPlugin.TryScheduleForDay(day, "DayTransitionDetector");
            }
        }
    }

}

[HarmonyPatch]
public static class NightShiftPatches
{
    [HarmonyPatch(typeof(DayCycleManager), "FinishTheDay")]
    [HarmonyPrefix]
    public static void PrefixFinishTheDay()
    {
        NightShiftPlugin.OnFinishTheDayHook();
    }
}
