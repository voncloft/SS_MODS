using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace Maz.FastCashier
{
    [BepInPlugin("maz.fastcashier.speedtrace", "Fast Cashier (Speed+Trace)", "3.3.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource LOG;
        private static Harmony H;

        // ----------------------------
        // SPEED TUNABLES
        // ----------------------------
        private const float SCAN_INTERVAL_MULT = 0.35f;      // lower=faster
        private const float MIN_SCAN_INTERVAL  = 0.03f;      // floor
        private const float ANIM_SCAN_SPEED_MULT = 2.50f;    // higher=faster
        private const float CURRENT_SCAN_SPEED_MULT = 2.50f; // higher=faster

        // ----------------------------
        // TRACE SETTINGS (RUNTIME)
        // ----------------------------
        private static bool TraceEnabled = false;
        private static readonly KeyCode TRACE_TOGGLE_KEY = KeyCode.F9;

        private static int LOG_SLOW_CALL_MS = 10;
        private static int SAMPLE_EVERY_N_CALLS = 200;
        private static bool LOG_ARG_VALUES = true;
        private static bool LOG_STACK_ON_SLOW = false;

        private static readonly Dictionary<string, long> CallCounts = new();
        private static readonly object CallCountsLock = new();

        private static readonly HashSet<string> TraceWhitelist = new(StringComparer.Ordinal)
        {
            "Cashier.Scan",
            "Cashier.ScanItem",
            "Cashier.TryScan",
            "Cashier.StartScanning",
            "Cashier.StopScanning",
            "Cashier.UpdateScan",
            "Cashier.get_CurrentScanSpeed",

            "Checkout.ScanProduct",
            "Checkout.TryScanProduct",
            "Checkout.AddScannedProduct",
            "Checkout.OnProductScanned",
            "Checkout.StartCheckout",
            "Checkout.FinishCheckout",
            "Checkout.ReceivePayment",
            "Checkout.PayByCash",
            "Checkout.PayByCard",

            "Customer.GoToCheckout",
            "Customer.HandPayment",
            "Customer.Pay",
        };

        private static bool CashierMemberHintsDumped = false;

        public override void Load()
        {
            LOG = BepInEx.Logging.Logger.CreateLogSource("FastCashier(SpeedTrace)");
            H = new Harmony("maz.fastcashier.speedtrace");

            LOG.LogWarning("=== FastCashier(SpeedTrace) LOADED ===");

            // SPEED PATCHES
            PatchOne("Cashier", "Start", prefix: null, postfix: nameof(CashierStart_Postfix));
            PatchOne("Cashier", "get_CurrentScanSpeed", prefix: null, postfix: nameof(CashierGetCurrentScanSpeed_Postfix));
            PatchOne("ScanAnimationHandler", "set_ScanSpeed", prefix: nameof(ScanHandlerSetScanSpeed_Prefix), postfix: null);

            // TARGETED TRACE
            PatchWhitelistedTrace("Cashier");
            PatchWhitelistedTrace("Checkout");
            PatchWhitelistedTrace("Customer");

            // Host hotkey
            try { ClassInjector.RegisterTypeInIl2Cpp<Host>(); } catch { }
            var go = new GameObject("FastCashierHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            try { go.AddComponent<Host>(); } catch { go.AddComponent(Il2CppType.From(typeof(Host))); }

            LOG.LogWarning("=== FastCashier(SpeedTrace) READY ===");
            LOG.LogWarning("Press F9 in-game to toggle trace ON/OFF (starts OFF).");
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

                // 1) Try to scale ScanningInterval via property/method/field
                bool didInterval = TryScaleFloatMember(__instance, t, "ScanningInterval", SCAN_INTERVAL_MULT, MIN_SCAN_INTERVAL, out float oldInt, out float newInt);
                if (didInterval)
                    LOG.LogWarning($"[SPEED] Cashier.ScanningInterval {oldInt:0.0000} -> {newInt:0.0000}");

                // 2) Try to scale "m_CashierScanIntervals" list via get_ method (field accessor methods exist in IL2CPP)
                bool didList = TryScaleFloatListMember(__instance, t, "m_CashierScanIntervals", SCAN_INTERVAL_MULT, MIN_SCAN_INTERVAL, out int listCount);
                if (didList)
                    LOG.LogWarning($"[SPEED] Cashier.m_CashierScanIntervals scaled (Count={listCount})");

                // If neither worked, dump hints once so we can lock onto real names
                if (!didInterval && !didList && !CashierMemberHintsDumped)
                {
                    CashierMemberHintsDumped = true;
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
            try
            {
                float oldVal = __result;
                __result = Mathf.Max(__result * CURRENT_SCAN_SPEED_MULT, 0.01f);

                if (Mathf.Abs(__result - oldVal) > 0.001f)
                    LOG.LogInfo($"[SPEED] Cashier.CurrentScanSpeed {oldVal:0.0000} -> {__result:0.0000}");
            }
            catch { }
        }

        private static void ScanHandlerSetScanSpeed_Prefix(ref float value)
        {
            try
            {
                float oldVal = value;
                value = Mathf.Max(value * ANIM_SCAN_SPEED_MULT, 0.01f);

                if (Mathf.Abs(value - oldVal) > 0.001f)
                    LOG.LogInfo($"[SPEED] ScanAnimationHandler.ScanSpeed {oldVal:0.0000} -> {value:0.0000}");
            }
            catch { }
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

            // B) getter method get_m_CashierScanIntervals
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

            // Mutate list via reflection (works for Il2CppSystem.Collections.Generic.List<float>)
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
                LOG.LogWarning("[HINT] Dumping Cashier members containing 'Scan' or 'Interval' to help identify real names...");

                foreach (var p in cashierType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var n = p.Name ?? "";
                    if (n.IndexOf("Scan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Interval", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        LOG.LogWarning($"[HINT] Property: {p.PropertyType?.Name} {n}");
                    }
                }

                foreach (var f in cashierType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var n = f.Name ?? "";
                    if (n.IndexOf("Scan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Interval", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        LOG.LogWarning($"[HINT] Field: {f.FieldType?.Name} {n}");
                    }
                }

                foreach (var m in cashierType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var n = m.Name ?? "";
                    if (n.IndexOf("Scan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Interval", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        LOG.LogWarning($"[HINT] Method: {n}");
                    }
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

        private static void PatchOne(string typeName, string methodName, string prefix, string postfix)
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
                catch { }
            }

            LOG.LogWarning($"[PatchOne] {typeName}.{methodName} patched={patched}");
        }

        private static void PatchWhitelistedTrace(string typeName)
        {
            var t = AccessTools.TypeByName(typeName);
            if (t == null)
            {
                LOG.LogWarning($"[Trace] Type not found: {typeName}");
                return;
            }

            int patched = 0;

            foreach (var m in AccessTools.GetDeclaredMethods(t))
            {
                if (m == null) continue;
                if (m.IsConstructor || m.IsAbstract || m.IsGenericMethod) continue;

                string full = $"{typeName}.{m.Name}";
                if (!TraceWhitelist.Contains(full))
                    continue;

                try
                {
                    H.Patch(
                        m,
                        prefix: new HarmonyMethod(typeof(Plugin), nameof(TracePrefix)),
                        postfix: new HarmonyMethod(typeof(Plugin), nameof(TracePostfix)),
                        finalizer: new HarmonyMethod(typeof(Plugin), nameof(TraceFinalizer))
                    );
                    patched++;
                }
                catch { }
            }

            LOG.LogWarning($"[Trace] {typeName} whitelisted methods patched={patched}");
        }

        // ----------------------------
        // TRACE
        // ----------------------------

        private sealed class CallState
        {
            public Stopwatch Sw;
            public string Name;
            public long Seq;
            public bool LoggedEntry;
        }

        private static void TracePrefix(MethodBase __originalMethod, object[] __args, ref CallState __state)
        {
            if (!TraceEnabled)
            {
                __state = null;
                return;
            }

            try
            {
                string decl = __originalMethod.DeclaringType?.Name ?? "<?>";

                string name = $"{decl}.{__originalMethod.Name}";

                long seq;
                lock (CallCountsLock)
                {
                    if (!CallCounts.TryGetValue(name, out var c)) c = 0;
                    c++;
                    CallCounts[name] = c;
                    seq = c;
                }

                bool sample = (SAMPLE_EVERY_N_CALLS <= 1) || (seq % SAMPLE_EVERY_N_CALLS == 0);

                __state = new CallState
                {
                    Sw = Stopwatch.StartNew(),
                    Name = name,
                    Seq = seq,
                    LoggedEntry = sample
                };

                if (sample)
                    LOG.LogInfo($"[CALL->] {name} #{seq} args={FormatArgs(__args)}");
            }
            catch
            {
                __state = null;
            }
        }

        private static void TracePostfix(MethodBase __originalMethod, CallState __state)
        {
            if (__state == null) return;

            try
            {
                __state.Sw.Stop();
                long ms = __state.Sw.ElapsedMilliseconds;

                bool slow = ms >= LOG_SLOW_CALL_MS;

                if (__state.LoggedEntry || slow)
                {
                    LOG.LogInfo($"[<-RET] {__state.Name} #{__state.Seq} {ms}ms");

                    if (slow && LOG_STACK_ON_SLOW)
                        LOG.LogInfo($"[STACK] {__state.Name} #{__state.Seq}\n{Environment.StackTrace}");
                }
            }
            catch { }
        }

        private static Exception TraceFinalizer(MethodBase __originalMethod, Exception __exception, CallState __state)
        {
            if (__exception == null) return null;

            try
            {
                string decl = __originalMethod.DeclaringType?.Name ?? "<?>";
                string name = $"{decl}.{__originalMethod.Name}";
                long ms = -1;

                if (__state != null)
                {
                    try { __state.Sw?.Stop(); ms = __state.Sw?.ElapsedMilliseconds ?? -1; } catch { }
                }

                LOG.LogError($"[EXC] {name} #{__state?.Seq} after {ms}ms :: {__exception.GetType().Name}: {__exception.Message}\n{__exception.StackTrace}");
            }
            catch { }

            return __exception;
        }

        // ----------------------------
        // HOST: HOTKEY TOGGLE
        // ----------------------------
        private sealed class Host : MonoBehaviour
        {
            public Host(IntPtr ptr) : base(ptr) { }

            private void Update()
            {
                try
                {
                    if (Input.GetKeyDown(TRACE_TOGGLE_KEY))
                    {
                        TraceEnabled = !TraceEnabled;
                        LOG.LogWarning($"[TRACE] TraceEnabled={(TraceEnabled ? "ON" : "OFF")} (F9)");
                    }
                }
                catch { }
            }
        }

        // ----------------------------
        // ARG FORMAT
        // ----------------------------
        private static string FormatArgs(object[] args)
        {
            try
            {
                if (!LOG_ARG_VALUES) return $"[{(args == null ? 0 : args.Length)} args]";
                if (args == null) return "[]";
                if (args.Length == 0) return "[]";

                var parts = new List<string>(args.Length);
                for (int i = 0; i < args.Length; i++)
                    parts.Add(FormatValue(args[i]));

                return "[" + string.Join(", ", parts) + "]";
            }
            catch
            {
                return "[<err>]";
            }
        }

        private static string FormatValue(object o)
        {
            if (o == null) return "null";

            try
            {
                var t = o.GetType();
                string tn = t.Name;

                if (o is UnityEngine.Object uo)
                    return $"{tn}({uo.name})";

                if (o is string s)
                    return $"\"{Trim(s, 96)}\"";
                if (o is bool b)
                    return b ? "true" : "false";
                if (o is int || o is long || o is float || o is double || o is decimal || o is short || o is byte)
                    return $"{tn}({o})";

                var nameProp = t.GetProperty("name", BindingFlags.Instance | BindingFlags.Public);
                if (nameProp != null)
                {
                    var nv = nameProp.GetValue(o);
                    if (nv != null)
                        return $"{tn}({Trim(nv.ToString(), 64)})";
                }

                return tn;
            }
            catch
            {
                return o.ToString();
            }
        }

        private static string Trim(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }
    }
}
