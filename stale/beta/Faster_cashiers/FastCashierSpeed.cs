using System;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace Maz.FastCashier
{
    [BepInPlugin("maz.fastcashier.speed", "Fast Cashier (Safe)", "3.4.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource LOG;
        private static Harmony H;

        // Config
        private static ConfigEntry<float> ScanIntervalMult;
        private static ConfigEntry<float> MinScanInterval;
        private static ConfigEntry<float> AnimScanSpeedMult;
        private static ConfigEntry<float> CurrentScanSpeedMult;

        private static ConfigEntry<bool> DebugLogging;
        private static ConfigEntry<int> DebugLogEveryN;

        // Debug counters (throttled)
        private static long _curScanSpeedCalls;
        private static long _animScanSpeedCalls;

        private static bool _cashierMemberHintsDumped;

        public override void Load()
        {
            // Fix: avoid UnityEngine.Logger ambiguity by fully qualifying
            LOG = BepInEx.Logging.Logger.CreateLogSource("FastCashier(Safe)");
            H = new Harmony("maz.fastcashier.speed.safe");

            // --------
            // CONFIG
            // --------
            ScanIntervalMult = Config.Bind("Speed", "ScanIntervalMultiplier", 0.35f,
                "Lower = faster. Multiplies the cashier scan interval (if the game exposes it).");
            MinScanInterval = Config.Bind("Speed", "MinimumScanInterval", 0.03f,
                "Hard floor for scan interval (seconds). Prevents zero/negative intervals.");

            AnimScanSpeedMult = Config.Bind("Speed", "AnimationScanSpeedMultiplier", 2.50f,
                "Higher = faster. Multiplies ScanAnimationHandler.ScanSpeed.");
            CurrentScanSpeedMult = Config.Bind("Speed", "CurrentScanSpeedMultiplier", 2.50f,
                "Higher = faster. Multiplies Cashier.CurrentScanSpeed.");

            DebugLogging = Config.Bind("Debug", "EnableThrottledLogs", false,
                "If enabled, logs only once every N calls for hot-path patches. OFF is recommended.");
            DebugLogEveryN = Config.Bind("Debug", "LogEveryNCalls", 2000,
                "When throttled logs are enabled, log once every N calls.");

            LOG.LogWarning("=== FastCashier(Safe) LOADED ===");

            // --------
            // PATCHES
            // --------
            PatchOne("Cashier", "Start", postfix: nameof(CashierStart_Postfix));
            PatchOne("Cashier", "get_CurrentScanSpeed", postfix: nameof(CashierGetCurrentScanSpeed_Postfix));
            PatchOne("ScanAnimationHandler", "set_ScanSpeed", prefix: nameof(ScanHandlerSetScanSpeed_Prefix));

            LOG.LogWarning("=== FastCashier(Safe) READY ===");
        }

        // ----------------------------
        // SPEED PATCHES
        // ----------------------------

        private static void CashierStart_Postfix(object __instance)
        {
            try
            {
                if (__instance == null) return;

                var t = __instance.GetType();

                // Try common patterns for scan interval on start (one-time work)
                bool didInterval = TryScaleFloatMember(
                    __instance, t, "ScanningInterval",
                    ScanIntervalMult.Value,
                    MinScanInterval.Value,
                    out float oldInt,
                    out float newInt
                );

                if (didInterval)
                    LOG.LogInfo($"[SPEED] Cashier.ScanningInterval {oldInt:0.0000} -> {newInt:0.0000}");

                bool didList = TryScaleFloatListMember(
                    __instance, t, "m_CashierScanIntervals",
                    ScanIntervalMult.Value,
                    MinScanInterval.Value,
                    out int listCount
                );

                if (didList)
                    LOG.LogInfo($"[SPEED] Cashier.m_CashierScanIntervals scaled (Count={listCount})");

                // Only dump hints once, and only if nothing matched
                if (!didInterval && !didList && !_cashierMemberHintsDumped)
                {
                    _cashierMemberHintsDumped = true;
                    DumpCashierMemberHints(t);
                }
            }
            catch (Exception e)
            {
                LOG.LogWarning($"[SPEED] CashierStart_Postfix failed: {e.GetType().Name} {e.Message}");
            }
        }

        private static void CashierGetCurrentScanSpeed_Postfix(ref float __result)
        {
            // HOT PATH: do not allocate, do not log per-call
            try
            {
                float oldVal = __result;
                float mult = CurrentScanSpeedMult.Value;

                if (mult != 1f)
                    __result = Mathf.Max(oldVal * mult, 0.01f);

                ThrottledHotLog(ref _curScanSpeedCalls, "Cashier.get_CurrentScanSpeed", oldVal, __result);
            }
            catch
            {
                // swallow: hot path
            }
        }

        private static void ScanHandlerSetScanSpeed_Prefix(ref float value)
        {
            // HOT PATH: do not allocate, do not log per-call
            try
            {
                float oldVal = value;
                float mult = AnimScanSpeedMult.Value;

                if (mult != 1f)
                    value = Mathf.Max(oldVal * mult, 0.01f);

                ThrottledHotLog(ref _animScanSpeedCalls, "ScanAnimationHandler.set_ScanSpeed", oldVal, value);
            }
            catch
            {
                // swallow: hot path
            }
        }

        private static void ThrottledHotLog(ref long counter, string name, float oldVal, float newVal)
        {
            if (!DebugLogging.Value) return;

            int n = DebugLogEveryN.Value;
            if (n <= 0) n = 2000;

            long c = Interlocked.Increment(ref counter);
            if (c % n != 0) return;

            LOG.LogInfo($"[HOT] {name} calls={c} last {oldVal:0.0000}->{newVal:0.0000}");
        }

        // ----------------------------
        // MEMBER ACCESS HELPERS
        // ----------------------------

        private static bool TryScaleFloatMember(object inst, Type t, string baseName, float mult, float floor, out float oldVal, out float newVal)
        {
            oldVal = 0f;
            newVal = 0f;

            // A) property "ScanningInterval"
            try
            {
                var prop = AccessTools.Property(t, baseName);
                if (prop != null && prop.CanRead && prop.CanWrite && prop.PropertyType == typeof(float))
                {
                    oldVal = (float)prop.GetValue(inst);
                    newVal = Mathf.Max(oldVal * mult, floor);
                    prop.SetValue(inst, newVal);
                    return true;
                }
            }
            catch { }

            // B) methods get_ScanningInterval / set_ScanningInterval
            try
            {
                var getM = AccessTools.Method(t, "get_" + baseName, Type.EmptyTypes);
                var setM = AccessTools.Method(t, "set_" + baseName, new[] { typeof(float) });
                if (getM != null && setM != null)
                {
                    var got = getM.Invoke(inst, Array.Empty<object>());
                    if (got is float f)
                    {
                        oldVal = f;
                        newVal = Mathf.Max(oldVal * mult, floor);
                        setM.Invoke(inst, new object[] { newVal });
                        return true;
                    }
                }
            }
            catch { }

            // C) direct field (last resort)
            try
            {
                var field = AccessTools.Field(t, baseName);
                if (field != null && field.FieldType == typeof(float))
                {
                    oldVal = (float)field.GetValue(inst);
                    newVal = Mathf.Max(oldVal * mult, floor);
                    field.SetValue(inst, newVal);
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool TryScaleFloatListMember(object inst, Type t, string baseName, float mult, float floor, out int countOut)
        {
            countOut = 0;
            object listObj = null;

            // A) property
            try
            {
                var prop = AccessTools.Property(t, baseName);
                if (prop != null && prop.CanRead)
                    listObj = prop.GetValue(inst);
            }
            catch { }

            // B) getter method
            if (listObj == null)
            {
                try
                {
                    var getM = AccessTools.Method(t, "get_" + baseName, Type.EmptyTypes);
                    if (getM != null)
                        listObj = getM.Invoke(inst, Array.Empty<object>());
                }
                catch { }
            }

            // C) field
            if (listObj == null)
            {
                try
                {
                    var field = AccessTools.Field(t, baseName);
                    if (field != null)
                        listObj = field.GetValue(inst);
                }
                catch { }
            }

            if (listObj == null) return false;

            // Mutate list via reflection (Il2Cpp list has Count/get_Item/set_Item)
            try
            {
                var countProp = AccessTools.Property(listObj.GetType(), "Count");
                var itemGetter = AccessTools.Method(listObj.GetType(), "get_Item", new[] { typeof(int) });
                var itemSetter = AccessTools.Method(listObj.GetType(), "set_Item", new[] { typeof(int), typeof(float) });

                if (countProp == null || itemGetter == null || itemSetter == null) return false;

                int count = (int)countProp.GetValue(listObj);
                for (int i = 0; i < count; i++)
                {
                    float v = (float)itemGetter.Invoke(listObj, new object[] { i });
                    float nv = Mathf.Max(v * mult, floor);
                    itemSetter.Invoke(listObj, new object[] { i, nv });
                }

                countOut = count;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void DumpCashierMemberHints(Type cashierType)
        {
            try
            {
                LOG.LogWarning("[HINT] Could not find ScanningInterval / m_CashierScanIntervals by name.");
                LOG.LogWarning("[HINT] Dumping Cashier members containing 'Scan' or 'Interval' (one-time)...");

                foreach (var p in cashierType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var n = p.Name ?? "";
                    if (n.IndexOf("Scan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Interval", StringComparison.OrdinalIgnoreCase) >= 0)
                        LOG.LogWarning($"[HINT] Property: {p.PropertyType?.Name} {n}");
                }

                foreach (var f in cashierType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var n = f.Name ?? "";
                    if (n.IndexOf("Scan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Interval", StringComparison.OrdinalIgnoreCase) >= 0)
                        LOG.LogWarning($"[HINT] Field: {f.FieldType?.Name} {n}");
                }

                foreach (var m in cashierType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var n = m.Name ?? "";
                    if (n.IndexOf("Scan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Interval", StringComparison.OrdinalIgnoreCase) >= 0)
                        LOG.LogWarning($"[HINT] Method: {n}");
                }
            }
            catch (Exception e)
            {
                LOG.LogWarning($"[HINT] DumpCashierMemberHints failed: {e.GetType().Name} {e.Message}");
            }
        }

        // ----------------------------
        // PATCH HELPERS
        // ----------------------------

        private static void PatchOne(string typeName, string methodName, string prefix = null, string postfix = null)
        {
            var t = AccessTools.TypeByName(typeName);
            if (t == null)
            {
                LOG.LogWarning($"[PatchOne] Type not found: {typeName}");
                return;
            }

            int patched = 0;
            foreach (var m in AccessTools.GetDeclaredMethods(t))
            {
                if (m == null) continue;
                if (!string.Equals(m.Name, methodName, StringComparison.Ordinal)) continue;
                if (m.IsAbstract) continue;

                try
                {
                    var pre = prefix == null ? null : new HarmonyMethod(typeof(Plugin), prefix);
                    var post = postfix == null ? null : new HarmonyMethod(typeof(Plugin), postfix);
                    H.Patch(m, prefix: pre, postfix: post);
                    patched++;
                }
                catch
                {
                    // don't die if one overload can't be patched
                }
            }

            LOG.LogWarning($"[PatchOne] {typeName}.{methodName} patched={patched}");
        }
    }
}
