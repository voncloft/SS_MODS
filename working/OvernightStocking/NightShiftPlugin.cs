using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Lean.Pool;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;

[BepInPlugin("von.nightshift.il2cpp", "NightShift (IL2CPP)", "2.2.2")]
public class NightShiftPlugin : BasePlugin
{
    internal static ManualLogSource L;

    private static Harmony _harmony;

    // Guards
    private static bool _busy;
    private static int _lastProcessedDay = int.MinValue;

    // Day transition detector
    internal static int _lastSeenDay = int.MinValue;
    internal static bool _sawValidDay;

    // Deferred run scheduling
    internal static bool _pendingRun;
    internal static float _pendingRunAt;
    internal static string _pendingReason;

    // Logging
    private static string _logPath;
    private static readonly object _logLock = new object();
    private static int _runSeq = 0;

    public override void Load()
    {
        L = Log;

        _logPath = GetNightShiftLogPath();

        LogI("BOOT", "==================================================");
        LogI("BOOT", "LOADED NightShift 2.2.2 @ " + DateTime.Now);
        LogI("BOOT", "Mode: End-of-day only. Deferred run + pool-safe cleanup.");
        LogI("BOOT", "LogFile: " + _logPath);
        LogI("BOOT", "==================================================");

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<NightShiftRuntime>();
            var go = new GameObject("NightShiftRuntime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<NightShiftRuntime>();
            LogI("BOOT", "Runtime injected (heartbeat + day transition detector).");
        }
        catch (Exception e)
        {
            LogE("BOOT", "Runtime inject failed: " + e);
        }

        try
        {
            _harmony = new Harmony("von.nightshift.il2cpp");
            _harmony.PatchAll(typeof(NightShiftPatches));
            LogI("BOOT", "Harmony patches applied.");
        }
        catch (Exception e)
        {
            LogE("BOOT", "Harmony patch failed: " + e);
        }
    }

    private static string GetNightShiftLogPath()
    {
        try
        {
            // BepInEx sets this env var in many setups; fallback to current directory.
            string bepinexRoot = Environment.GetEnvironmentVariable("BEPINEX_ROOT_PATH");
            if (!string.IsNullOrEmpty(bepinexRoot) && Directory.Exists(bepinexRoot))
                return Path.Combine(bepinexRoot, "NightShift.log");
        }
        catch { }

        // fallback: assume current working dir contains BepInEx folder
        try
        {
            string cwd = Directory.GetCurrentDirectory();
            string candidate = Path.Combine(cwd, "BepInEx", "NightShift.log");
            return candidate;
        }
        catch { }

        return "NightShift.log";
    }

    internal static void LogI(string tag, string msg)
    {
        try { L.LogInfo("[NS] [" + tag + "] " + msg); } catch { }
        WriteFileLine("INFO", tag, msg);
    }

    internal static void LogW(string tag, string msg)
    {
        try { L.LogWarning("[NS] [" + tag + "] " + msg); } catch { }
        WriteFileLine("WARN", tag, msg);
    }

    internal static void LogE(string tag, string msg)
    {
        try { L.LogError("[NS] [" + tag + "] " + msg); } catch { }
        WriteFileLine("ERROR", tag, msg);
    }

    private static void WriteFileLine(string level, string tag, string msg)
    {
        try
        {
            if (string.IsNullOrEmpty(_logPath)) return;

            lock (_logLock)
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    + " [" + level + "]"
                    + " [" + tag + "] "
                    + msg
                    + Environment.NewLine;

                Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                File.AppendAllText(_logPath, line);
            }
        }
        catch
        {
            // never crash on logging
        }
    }

    internal static void ScheduleNightShift(string reason, float delaySeconds)
    {
        _pendingRun = true;
        _pendingRunAt = Time.unscaledTime + delaySeconds;
        _pendingReason = reason;

        LogI("SCHED", "Scheduled reason=" + reason + " delay=" + delaySeconds + "s at t=" + _pendingRunAt);
    }

    internal static void TryRunScheduled()
    {
        if (!_pendingRun) return;
        if (Time.unscaledTime < _pendingRunAt) return;

        _pendingRun = false;
        string reason = _pendingReason ?? "Scheduled";

        RunNightShift(reason);
    }

    internal static void RunNightShift(string reason)
    {
        int runId = ++_runSeq;

        LogI("RUN", "ENTER runId=" + runId + " reason=" + reason);

        if (_busy)
        {
            LogW("RUN", "BLOCKED runId=" + runId + " already running.");
            return;
        }

        _busy = true;

        try
        {
            int day = SafeGetCurrentDay();
            LogI("RUN", "STATE runId=" + runId + " currentDay=" + day + " lastProcessedDay=" + _lastProcessedDay);

            if (day != -1 && day == _lastProcessedDay)
            {
                LogI("RUN", "SKIP runId=" + runId + " already processed this day.");
                return;
            }

            if (day != -1)
                _lastProcessedDay = day;

            var swTotal = Stopwatch.StartNew();
            LogI("RUN", "START runId=" + runId + " day=" + day);

            // Step 0 snapshot
            try
            {
                LogI("STEP0", "runId=" + runId
                    + " RackManager=" + (RackManager.Instance != null)
                    + " DisplayManager=" + (DisplayManager.Instance != null)
                    + " InventoryManager=" + (InventoryManager.Instance != null)
                    + " IDManager=" + (IDManager.Instance != null));
            }
            catch { }

            // Step 1
            LogI("STEP1", "BEGIN runId=" + runId);
            var sw1 = Stopwatch.StartNew();
            var restock = RestockDisplaysFromRacks(runId);
            sw1.Stop();
            LogI("STEP1", "END runId=" + runId
                + " movedBoxes=" + restock.MovedBoxes
                + " movedProducts=" + restock.MovedProducts
                + " rebuiltSlots=" + restock.RebuiltDisplaySlots
                + " scannedRackSlots=" + restock.ScannedRackSlots
                + " scannedDisplaySlots=" + restock.ScannedDisplaySlots
                + " loops=" + restock.OuterLoops
                + " ms=" + sw1.ElapsedMilliseconds);

            // Step 2
            LogI("STEP2", "BEGIN runId=" + runId);
            var sw2 = Stopwatch.StartNew();
            var cleanup = CleanupEmptyBoxes_RackOnly(runId);
            sw2.Stop();
            LogI("STEP2", "END runId=" + runId
                + " removedFromRackSlots=" + cleanup.RemovedFromRackSlots
                + " ms=" + sw2.ElapsedMilliseconds);

            swTotal.Stop();
            LogI("RUN", "END runId=" + runId + " total_ms=" + swTotal.ElapsedMilliseconds);
        }
        catch (Exception e)
        {
            LogE("RUN", "CRASH reason=" + reason + " ex=" + e);
        }
        finally
        {
            _busy = false;
            LogI("RUN", "EXIT runId=" + runId + " reason=" + reason);
        }
    }

    private static int SafeGetCurrentDay()
    {
        try
        {
            var dcm = DayCycleManager.Instance;
            if (dcm != null)
                return dcm.CurrentDay;
        }
        catch { }
        return -1;
    }

    private struct RestockResult
    {
        public int ScannedRackSlots;
        public int ScannedDisplaySlots;
        public int MovedBoxes;
        public int MovedProducts;
        public int RebuiltDisplaySlots;
        public int OuterLoops;
    }

    private static void SafePoolDespawn(UnityEngine.Object obj)
    {
        if (obj == null) return;
        try
        {
            if (obj is Component c)
            {
                LeanPool.Despawn(c);
                return;
            }

            if (obj is GameObject go)
            {
                LeanPool.Despawn(go);
                return;
            }
        }
        catch { }
        // never Destroy pooled objects
    }

    private static RestockResult RestockDisplaysFromRacks(int runId)
    {
        var res = new RestockResult();

        var rackMgr = RackManager.Instance;
        var dispMgr = DisplayManager.Instance;
        var invMgr = InventoryManager.Instance;
        var idMgr = IDManager.Instance;

        if (rackMgr == null || dispMgr == null || invMgr == null || idMgr == null)
        {
            LogW("STEP1", "ABORT runId=" + runId + " missing managers.");
            return res;
        }

        // Copy rack slots into managed dictionary; filter y==10 racks
        var rackSlotsByProduct = new Dictionary<int, List<RackSlot>>();
        try
        {
            foreach (var kv in rackMgr.RackSlots)
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

                rackSlotsByProduct[pid] = copy;
            }
        }
        catch (Exception e)
        {
            LogW("STEP1", "ABORT runId=" + runId + " rack copy failed: " + e);
            return res;
        }

        int rackSlotCount = 0;
        foreach (var kv in rackSlotsByProduct)
            rackSlotCount += kv.Value != null ? kv.Value.Count : 0;
        res.ScannedRackSlots = rackSlotCount;

        var cachedSlots = new Il2CppSystem.Collections.Generic.List<DisplaySlot>();

        bool didWork = true;

        while (didWork)
        {
            res.OuterLoops++;
            didWork = false;

            List<int> productIds;
            try
            {
                productIds = ((Dictionary<int, int>)(object)invMgr.Products).Keys.ToList();
            }
            catch
            {
                productIds = rackSlotsByProduct.Keys.Where(x => x != 0).ToList();
            }

            foreach (int productId in productIds)
            {
                if (productId == 0) continue;

                ProductSO product;
                try { product = idMgr.ProductSO(productId); }
                catch { continue; }
                if (product == null) continue;

                try { cachedSlots.Clear(); } catch { }

                try
                {
                    // Your build: returns int, fills cachedSlots
                    dispMgr.GetDisplaySlots(productId, false, cachedSlots);
                }
                catch
                {
                    continue;
                }

                if (cachedSlots.Count <= 0)
                    continue;

                int target = product.GridLayoutInStorage.productCount;

                for (int ds = 0; ds < cachedSlots.Count; ds++)
                {
                    var slot = cachedSlots[ds];
                    if (slot == null) continue;

                    res.ScannedDisplaySlots++;

                    while (slot.m_Products != null && slot.m_Products.Count < target)
                    {
                        int need = target - slot.m_Products.Count;

                        if (!rackSlotsByProduct.TryGetValue(productId, out var racksForProduct) ||
                            racksForProduct == null || racksForProduct.Count == 0)
                            break;

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
                            break;

                        didWork = true;

                        int movedNow = 0;

                        try
                        {
                            if (box.m_Data.ProductCount <= need)
                            {
                                int take = box.m_Data.ProductCount;

                                var data = slot.Data;
                                data.FirstItemCount = data.FirstItemCount + take;

                                slot.SpawnProduct(productId, take);
                                movedNow = take;

                                rackSlot.TakeBoxFromRack();
                                invMgr.RemoveBox(box.Data);

                                SafePoolDespawn((UnityEngine.Object)(object)((Component)box).gameObject);

                                res.MovedBoxes++;
                            }
                            else
                            {
                                int take = need;

                                var data2 = slot.Data;
                                data2.FirstItemCount = data2.FirstItemCount + take;

                                slot.SpawnProduct(productId, take);
                                movedNow = take;

                                try { box.DespawnProducts(); } catch { }
                                box.m_Data.ProductCount -= take;
                            }

                            res.MovedProducts += movedNow;
                        }
                        catch (Exception e)
                        {
                            LogW("STEP1", "runId=" + runId + " move failed productId=" + productId + " ex=" + e);
                            break;
                        }

                        try { rackSlot.SetLabel(); } catch { }
                    }

                    // Visual rebuild
                    try
                    {
                        int count = slot.m_Products.Count;

                        foreach (var p in slot.m_Products)
                        {
                            if (!((UnityEngine.Object)(object)p == (UnityEngine.Object)null))
                            {
                                SafePoolDespawn((UnityEngine.Object)(object)((Component)(object)p));
                            }
                        }

                        slot.m_Products.Clear();

                        for (int j = 0; j < count; j++)
                        {
                            var pso = idMgr.ProductSO(productId);
                            if (pso == null) break;

                            Product spawned = LeanPool.Spawn<Product>(pso.ProductPrefab, ((Component)slot).transform, false);
                            ((Component)spawned).transform.localPosition = ItemPosition.GetPosition(pso.GridLayoutInStorage, j);
                            ((Component)spawned).transform.localRotation = Quaternion.Euler(pso.GridLayoutInStorage.productAngles);
                            ((Component)spawned).transform.localScale = Vector3.one * pso.GridLayoutInStorage.scaleMultiplier;

                            slot.m_Products.Add(spawned);
                        }

                        slot.SetLabel();
                        slot.SetPriceTag();

                        res.RebuiltDisplaySlots++;
                    }
                    catch (Exception e)
                    {
                        LogW("STEP1", "runId=" + runId + " rebuild visuals failed ex=" + e);
                    }
                }
            }
        }

        return res;
    }

    private struct CleanupResult
    {
        public int RemovedFromRackSlots;
    }

    private static CleanupResult CleanupEmptyBoxes_RackOnly(int runId)
    {
        var res = new CleanupResult();

        var invMgr = InventoryManager.Instance;
        if (invMgr == null)
        {
            LogW("STEP2", "ABORT runId=" + runId + " InventoryManager missing.");
            return res;
        }

        try
        {
            foreach (var rack in Resources.FindObjectsOfTypeAll<Rack>())
            {
                if (rack == null || rack.RackSlots == null) continue;

                foreach (var slot in rack.RackSlots)
                {
                    if (slot == null || slot.m_Boxes == null || slot.m_Boxes.Count == 0) continue;

                    for (int i = slot.m_Boxes.Count - 1; i >= 0; i--)
                    {
                        var box = slot.m_Boxes[i];
                        if (box == null || box.m_Data == null) continue;
                        if (box.m_Data.ProductCount > 0) continue;

                        try { slot.m_Boxes.RemoveAt(i); } catch { }
                        try { invMgr.RemoveBox(box.Data); } catch { }

                        SafePoolDespawn((UnityEngine.Object)(object)((Component)box).gameObject);

                        res.RemovedFromRackSlots++;
                    }

                    try { slot.SetLabel(); } catch { }
                }
            }
        }
        catch (Exception e)
        {
            LogW("STEP2", "runId=" + runId + " rack cleanup failed ex=" + e);
        }

        return res;
    }
}

public class NightShiftRuntime : MonoBehaviour
{
    private float _nextBeat;
    private float _nextDayCheck;

    private void Start()
    {
        NightShiftPlugin.LogI("RT", "Start. Heartbeat 10s. DayCheck 0.5s. Deferred run 2.0s.");
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

        NightShiftPlugin.TryRunScheduled();

        if (Time.unscaledTime >= _nextDayCheck)
        {
            _nextDayCheck = Time.unscaledTime + 0.5f;

            int day = -1;
            try
            {
                var dcm = DayCycleManager.Instance;
                if (dcm != null) day = dcm.CurrentDay;
            }
            catch { }

            if (day >= 0)
            {
                if (!NightShiftPlugin._sawValidDay)
                {
                    NightShiftPlugin._sawValidDay = true;
                    NightShiftPlugin._lastSeenDay = day;
                    NightShiftPlugin.LogI("DAY", "Detector primed day=" + day);
                }
                else if (day != NightShiftPlugin._lastSeenDay)
                {
                    int prev = NightShiftPlugin._lastSeenDay;
                    NightShiftPlugin._lastSeenDay = day;

                    NightShiftPlugin.LogI("DAY", "HOOK HIT DayTransition " + prev + " -> " + day);
                    NightShiftPlugin.ScheduleNightShift("DayTransitionDetector", 2.0f);
                }
            }
        }
    }
}

public static class NightShiftPatches
{
    [HarmonyPatch(typeof(DayCycleManager), nameof(DayCycleManager.FinishTheDay))]
    public static class DayCycleManager_FinishTheDay_Patch
    {
        public static void Prefix()
        {
            NightShiftPlugin.LogI("HOOK", "HIT DayCycleManager.FinishTheDay");
            NightShiftPlugin.ScheduleNightShift("DayCycleManager.FinishTheDay", 2.0f);
        }
    }

    [HarmonyPatch(typeof(NextDayInteraction), nameof(NextDayInteraction.FinishTheDayOrder))]
    public static class NextDayInteraction_FinishTheDayOrder_Patch
    {
        public static void Prefix()
        {
            NightShiftPlugin.LogI("HOOK", "HIT NextDayInteraction.FinishTheDayOrder");
            NightShiftPlugin.ScheduleNightShift("NextDayInteraction.FinishTheDayOrder", 2.0f);
        }
    }

    [HarmonyPatch(typeof(DayCycleManager), "EndDay")]
    public static class DayCycleManager_EndDay_Patch
    {
        public static void Prefix()
        {
            NightShiftPlugin.LogI("HOOK", "HIT DayCycleManager.EndDay");
            NightShiftPlugin.ScheduleNightShift("DayCycleManager.EndDay", 2.0f);
        }
    }

    [HarmonyPatch(typeof(DayCycleManager), "StartNewDay")]
    public static class DayCycleManager_StartNewDay_Patch
    {
        public static void Prefix()
        {
            NightShiftPlugin.LogI("HOOK", "HIT DayCycleManager.StartNewDay");
            NightShiftPlugin.ScheduleNightShift("DayCycleManager.StartNewDay", 2.0f);
        }
    }
}
