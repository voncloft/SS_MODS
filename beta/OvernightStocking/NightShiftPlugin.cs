using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using Lean.Pool;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

[BepInPlugin("von.nightshift.il2cpp", "NightShift (IL2CPP)", "2.3.2")]
public class NightShiftPlugin : BasePlugin
{
    internal static ManualLogSource L;

    // Day detection
    internal static bool Primed;
    internal static int LastSeenDay = int.MinValue;

    // Run scheduling/guard
    internal static bool Busy;
    internal static int LastProcessedDay = int.MinValue;

    // Worker state
    internal static NightShiftWorker Worker;

    // Tunables
    internal const float RUN_DELAY_SECONDS = 6.0f;
    internal const int OPS_PER_FRAME = 200;
    internal const int MAX_TOTAL_MS = 8000;
    internal const int MAX_OUTER_LOOPS = 12;

    public override void Load()
    {
        L = Log;

        LogI("BOOT", "==================================================");
        LogI("BOOT", "LOADED NightShift 2.3.2 @ " + DateTime.Now);
        LogI("BOOT", "End-of-day only via DayTransition detector (NO Harmony hooks).");
        LogI("BOOT", "Chunked worker in Update. Delay=" + RUN_DELAY_SECONDS + "s OPS_PER_FRAME=" + OPS_PER_FRAME);
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
    }

    internal static void LogI(string tag, string msg) { try { L.LogInfo("[NS] [" + tag + "] " + msg); } catch { } }
    internal static void LogW(string tag, string msg) { try { L.LogWarning("[NS] [" + tag + "] " + msg); } catch { } }
    internal static void LogE(string tag, string msg) { try { L.LogError("[NS] [" + tag + "] " + msg); } catch { } }

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

    internal static void TryScheduleForDay(int newDay, string reason)
    {
        if (Busy)
        {
            LogW("SCHED", "Blocked (already running). newDay=" + newDay + " reason=" + reason);
            return;
        }

        if (newDay == LastProcessedDay)
        {
            LogI("SCHED", "Skip (already processed). day=" + newDay + " reason=" + reason);
            return;
        }

        if (Worker != null && Worker.State != NightShiftWorkerState.Idle)
        {
            LogW("SCHED", "Worker already active state=" + Worker.State + " day=" + newDay + " reason=" + reason);
            return;
        }

        Worker = new NightShiftWorker(newDay, reason, Time.unscaledTime + RUN_DELAY_SECONDS);
        LogI("SCHED", "Scheduled day=" + newDay + " reason=" + reason + " runAt=" + Worker.RunAt);
    }

    internal static void SafePoolDespawnBox(Box box)
    {
        if (box == null) return;
        try
        {
            var go = ((Component)box).gameObject;
            if (go != null) LeanPool.Despawn(go);
        }
        catch { }
        // DO NOT Destroy pooled objects.
    }
}

public enum NightShiftWorkerState
{
    Idle = 0,
    WaitingDelay = 1,
    Init = 2,
    Restock = 3,
    Cleanup = 4,
    Done = 5,
    Aborted = 6
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

    private int _outerLoop;
    private bool _didWork;
    private int _productIndex;
    private int _displayIndex;

    private int _currentProductId;
    private ProductSO _currentProduct;
    private int _targetCount;

    // stats
    public int ScannedRackSlots;
    public int ScannedDisplaySlots;
    public int MovedBoxes;
    public int MovedProducts;
    public bool StoppedByCap;

    // cleanup state
    private Rack[] _racks;
    private int _rackIdx;
    private int _slotIdx;
    private int _boxIdx;
    public int RemovedEmptyBoxes;

    // timing
    private Stopwatch _swTotal;

    public NightShiftWorker(int day, string reason, float runAt)
    {
        Day = day;
        Reason = reason;
        RunAt = runAt;
        State = NightShiftWorkerState.WaitingDelay;
    }

    public void Tick(int opsBudget)
    {
        if (State == NightShiftWorkerState.WaitingDelay)
        {
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
            NightShiftPlugin.LogI("STEP0", "Snapshot RackManager=" + (_rackMgr != null)
                + " DisplayManager=" + (_dispMgr != null)
                + " InventoryManager=" + (_invMgr != null)
                + " IDManager=" + (_idMgr != null));

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
                                if (y == 10f) continue;
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

            _outerLoop = 0;
            _didWork = true;
            _productIndex = 0;
            _displayIndex = 0;

            NightShiftPlugin.LogI("STEP1", "BEGIN day=" + Day + " products=" + _productIds.Count + " rackSlots=" + ScannedRackSlots);

            State = NightShiftWorkerState.Restock;
        }

        if (State == NightShiftWorkerState.Restock)
        {
            int ops = 0;

            while (ops < opsBudget)
            {
                if (_swTotal.ElapsedMilliseconds > NightShiftPlugin.MAX_TOTAL_MS)
                {
                    StoppedByCap = true;
                    NightShiftPlugin.LogW("STEP1", "STOP cap MAX_TOTAL_MS=" + NightShiftPlugin.MAX_TOTAL_MS);
                    State = NightShiftWorkerState.Cleanup;
                    break;
                }

                if (_outerLoop > NightShiftPlugin.MAX_OUTER_LOOPS)
                {
                    StoppedByCap = true;
                    NightShiftPlugin.LogW("STEP1", "STOP cap MAX_OUTER_LOOPS=" + NightShiftPlugin.MAX_OUTER_LOOPS);
                    State = NightShiftWorkerState.Cleanup;
                    break;
                }

                if (_productIndex >= _productIds.Count)
                {
                    if (!_didWork)
                    {
                        NightShiftPlugin.LogI("STEP1", "END movedBoxes=" + MovedBoxes
                            + " movedProducts=" + MovedProducts
                            + " scannedDisplaySlots=" + ScannedDisplaySlots
                            + " loops=" + _outerLoop
                            + " stoppedByCap=" + StoppedByCap);
                        State = NightShiftWorkerState.Cleanup;
                        break;
                    }

                    _outerLoop++;
                    _didWork = false;
                    _productIndex = 0;
                    _displayIndex = 0;
                }

                _currentProductId = _productIds[_productIndex];
                if (_currentProductId == 0) { _productIndex++; continue; }

                _currentProduct = null;
                try { _currentProduct = _idMgr.ProductSO(_currentProductId); } catch { }
                if (_currentProduct == null) { _productIndex++; continue; }

                if (_displayIndex == 0)
                {
                    try { _cachedSlots.Clear(); } catch { }
                    try { _dispMgr.GetDisplaySlots(_currentProductId, false, _cachedSlots); }
                    catch { _productIndex++; continue; }

                    if (_cachedSlots.Count <= 0) { _productIndex++; continue; }
                    _targetCount = _currentProduct.GridLayoutInStorage.productCount;
                }

                if (_displayIndex >= _cachedSlots.Count)
                {
                    _productIndex++;
                    _displayIndex = 0;
                    continue;
                }

                var slot = _cachedSlots[_displayIndex];
                if (slot == null) { _displayIndex++; continue; }

                ScannedDisplaySlots++;

                if (slot.m_Products != null && slot.m_Products.Count < _targetCount)
                {
                    int need = _targetCount - slot.m_Products.Count;

                    if (_rackSlotsByProduct.TryGetValue(_currentProductId, out var racksForProduct) &&
                        racksForProduct != null && racksForProduct.Count > 0)
                    {
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

                        if (rackSlot != null && box != null)
                        {
                            _didWork = true;

                            try
                            {
                                if (box.m_Data.ProductCount <= need)
                                {
                                    int take = box.m_Data.ProductCount;

                                    var data = slot.Data;
                                    data.FirstItemCount = data.FirstItemCount + take;

                                    slot.SpawnProduct(_currentProductId, take);

                                    rackSlot.TakeBoxFromRack();
                                    _invMgr.RemoveBox(box.Data);

                                    NightShiftPlugin.SafePoolDespawnBox(box);

                                    MovedBoxes++;
                                    MovedProducts += take;
                                }
                                else
                                {
                                    int take = need;

                                    var data2 = slot.Data;
                                    data2.FirstItemCount = data2.FirstItemCount + take;

                                    slot.SpawnProduct(_currentProductId, take);

                                    try { box.DespawnProducts(); } catch { }
                                    box.m_Data.ProductCount -= take;

                                    MovedProducts += take;
                                }

                                try { rackSlot.SetLabel(); } catch { }
                                try { slot.SetLabel(); } catch { }
                                try { slot.SetPriceTag(); } catch { }
                            }
                            catch (Exception e)
                            {
                                NightShiftPlugin.LogW("STEP1", "move failed pid=" + _currentProductId + " ex=" + e);
                            }
                        }
                    }
                }

                _displayIndex++;
                ops++;
            }

            return;
        }

        if (State == NightShiftWorkerState.Cleanup)
        {
            if (_racks == null)
            {
                NightShiftPlugin.LogI("STEP2", "BEGIN");
                try { _racks = Resources.FindObjectsOfTypeAll<Rack>(); }
                catch { _racks = new Rack[0]; }

                _rackIdx = 0;
                _slotIdx = 0;
                _boxIdx = -1;
            }

            int ops = 0;

            while (ops < opsBudget)
            {
                if (_swTotal.ElapsedMilliseconds > NightShiftPlugin.MAX_TOTAL_MS)
                {
                    NightShiftPlugin.LogW("STEP2", "STOP cap MAX_TOTAL_MS=" + NightShiftPlugin.MAX_TOTAL_MS);
                    State = NightShiftWorkerState.Done;
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

                // RackSlots is List<RackSlot> in your build
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

                NightShiftPlugin.SafePoolDespawnBox(box);

                RemovedEmptyBoxes++;
                ops++;
            }

            return;
        }

        if (State == NightShiftWorkerState.Done || State == NightShiftWorkerState.Aborted)
        {
            _swTotal.Stop();

            if (State == NightShiftWorkerState.Done)
            {
                NightShiftPlugin.LastProcessedDay = Day;
                NightShiftPlugin.LogI("RUN", "END day=" + Day + " reason=" + Reason
                    + " total_ms=" + _swTotal.ElapsedMilliseconds
                    + " movedBoxes=" + MovedBoxes
                    + " movedProducts=" + MovedProducts
                    + " removedEmpty=" + RemovedEmptyBoxes
                    + " stoppedByCap=" + StoppedByCap);
            }
            else
            {
                NightShiftPlugin.LogW("RUN", "ABORTED day=" + Day + " reason=" + Reason
                    + " total_ms=" + _swTotal.ElapsedMilliseconds);
            }

            NightShiftPlugin.Busy = false;
            State = NightShiftWorkerState.Idle;
            return;
        }
    }
}

public class NightShiftRuntime : MonoBehaviour
{
    private float _nextBeat;
    private float _nextDayCheck;

    private void Start()
    {
        NightShiftPlugin.LogI("RT", "Start. Heartbeat 10s. DayCheck 0.5s.");
        _nextBeat = Time.unscaledTime + 10f;
        _nextDayCheck = Time.unscaledTime + 0.5f;
    }

    private void Update()
    {
        if (Time.unscaledTime >= _nextBeat)
        {
            _nextBeat = Time.unscaledTime + 10f;
            NightShiftPlugin.LogI("RT", "Heartbeat t=" + Time.unscaledTime);
        }

        if (NightShiftPlugin.Worker != null && NightShiftPlugin.Worker.State != NightShiftWorkerState.Idle)
        {
            NightShiftPlugin.Worker.Tick(NightShiftPlugin.OPS_PER_FRAME);
        }

        if (Time.unscaledTime >= _nextDayCheck)
        {
            _nextDayCheck = Time.unscaledTime + 0.5f;

            int day = NightShiftPlugin.SafeGetCurrentDay();
            if (day < 0) return;

            if (!NightShiftPlugin.Primed)
            {
                NightShiftPlugin.Primed = true;
                NightShiftPlugin.LastSeenDay = day;
                NightShiftPlugin.LogI("DAY", "Primed day=" + day);
                return;
            }

            if (day != NightShiftPlugin.LastSeenDay)
            {
                int prev = NightShiftPlugin.LastSeenDay;
                NightShiftPlugin.LastSeenDay = day;

                NightShiftPlugin.LogI("DAY", "Day changed " + prev + " -> " + day);

                NightShiftPlugin.TryScheduleForDay(day, "DayTransitionDetector");
            }
        }
    }
}
