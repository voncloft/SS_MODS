using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

[BepInPlugin("von.ezdelivery.autorackonly", "EZDelivery AutoRack Only", "1.3.2")]
public class EZDeliveryAutoRackOnly : BasePlugin
{
    internal static ManualLogSource L;
    private Harmony _harmony;

    // Config
    private static ConfigEntry<bool> cfgAutoRack;
    private static ConfigEntry<bool> cfgRackFreeSlots;
    private static ConfigEntry<bool> cfgVerbose;

    // QuickRack
    private static ConfigEntry<bool> cfgQuickRackEnabled;
    private static ConfigEntry<KeyCode> cfgQuickRackKey;
    private static ConfigEntry<bool> cfgForceEnableBoxInteraction;

    public override void Load()
    {
        L = Log;

        cfgAutoRack = Config.Bind("General", "AutoRack", true,
            "If true, automatically racks street boxes when Delivery() happens.");

        cfgRackFreeSlots = Config.Bind("General", "RackFreeSlots", false,
            "Allow placing into unlabeled/free rack slots (may be less strict).");

        cfgVerbose = Config.Bind("Logging", "Verbose", true,
            "Very verbose logging for testing.");

        cfgQuickRackEnabled = Config.Bind("QuickRack", "Enabled", true,
            "If true, enables quick rack for the box you're holding.");

        cfgQuickRackKey = Config.Bind("QuickRack", "Key", KeyCode.L,
            "Keybind to rack the box in your hand (default L).");

        cfgForceEnableBoxInteraction = Config.Bind("QuickRack", "ForceEnableBoxInteraction", true,
            "If true, re-enables BoxInteraction if another mod disabled it.");

        _harmony = new Harmony("von.ezdelivery.autorackonly");
        _harmony.PatchAll(typeof(Patches));

        L.LogInfo("[EZD-AutoRackOnly] Loaded v1.3.2");
        L.LogInfo($"[EZD-AutoRackOnly] Config: AutoRack={cfgAutoRack.Value} RackFreeSlots={cfgRackFreeSlots.Value} Verbose={cfgVerbose.Value}");
        L.LogInfo($"[EZD-AutoRackOnly] QuickRack: Enabled={cfgQuickRackEnabled.Value} Key={cfgQuickRackKey.Value} ForceEnableBI={cfgForceEnableBoxInteraction.Value}");
        L.LogInfo("[EZD-AutoRackOnly] Config file: BepInEx/config/von.ezdelivery.autorackonly.cfg");
    }

    private static class Patches
    {
        [HarmonyPatch(typeof(DeliveryManager), "Delivery")]
        [HarmonyPostfix]
        private static void DeliveryManager_Delivery_Postfix()
        {
            if (!cfgAutoRack.Value) return;
            L.LogInfo("[EZD-AutoRackOnly] Delivery() fired -> AutoRack running...");
            AutoRack_StreetBoxes();
        }

        [HarmonyPatch(typeof(PlayerInteraction), "Update")]
        [HarmonyPostfix]
        private static void PlayerInteraction_Update_Postfix(PlayerInteraction __instance)
        {
            if (!cfgQuickRackEnabled.Value) return;

            try
            {
                if (__instance == null) return;
                if (!__instance.isActiveAndEnabled) return;
                if (__instance.gameObject == null) return;
                if (!__instance.gameObject.activeInHierarchy) return;
            }
            catch { return; }

            if (!Input.GetKeyDown(cfgQuickRackKey.Value))
                return;

            QuickRack_BoxInHand(__instance);
        }
    }

    // ---------------------------
    // QuickRack (held box) - robust teardown
    // ---------------------------
    private static void QuickRack_BoxInHand(PlayerInteraction pi)
    {
        try
        {
            BoxInteraction bi = null;
            try { bi = pi.GetComponent<BoxInteraction>(); } catch { bi = null; }
            if (bi == null)
            {
                L.LogInfo("[EZD-AutoRackOnly] QuickRack: BoxInteraction not found on player.");
                return;
            }

            if (!bi.enabled)
            {
                if (cfgForceEnableBoxInteraction.Value)
                {
                    try { bi.enabled = true; } catch { }
                    if (cfgVerbose.Value) L.LogInfo("[EZD-AutoRackOnly] QuickRack: BoxInteraction was disabled -> forced enabled.");
                }
                else
                {
                    L.LogInfo("[EZD-AutoRackOnly] QuickRack SKIP: BoxInteraction.enabled == false");
                    return;
                }
            }

            // Held interactable -> Box via IL2CPP cast
            Box box = null;
            try
            {
                var interactable = ((Interaction)bi).Interactable;
                if (interactable != null)
                {
                    var raw = (Il2CppObjectBase)(object)interactable;
                    if (raw != null) box = raw.TryCast<Box>();
                }
            }
            catch { box = null; }

            if (box == null)
            {
                L.LogInfo("[EZD-AutoRackOnly] QuickRack: not holding a box.");
                return;
            }

            if (SafeGet(() => box.Racked, false))
            {
                if (cfgVerbose.Value) L.LogInfo("[EZD-AutoRackOnly] QuickRack: held box already racked.");
                return;
            }

            if (box.Product == null)
            {
                if (cfgVerbose.Value) L.LogInfo("[EZD-AutoRackOnly] QuickRack: held box has no product.");
                return;
            }

            int productId = SafeGet(() => box.Product.ID, -1);
            int boxId = SafeGet(() => box.BoxID, -1);

            // Occupied check using EmployeeManager (same as your other plugin)
            if (IsProductOccupied(productId))
            {
                RaiseInteractionWarning();
                L.LogInfo("[EZD-AutoRackOnly] QuickRack: occupied by restocker -> warning.");
                return;
            }

            RackManager rm = GetRackManager();
            if (rm == null)
            {
                L.LogWarning("[EZD-AutoRackOnly] QuickRack aborted: RackManager missing.");
                return;
            }

            Restocker restocker = null;
            try
            {
                restocker = new Restocker();
                RestockerManagementData data = new RestockerManagementData();
                data.UseUnlabeledRacks = cfgRackFreeSlots.Value;
                restocker.SetRestockerManagementData(data);
            }
            catch { restocker = null; }

            RackSlot slot = null;
            try { slot = rm.GetRackSlotThatHasSpaceFor(productId, boxId, restocker); }
            catch { slot = null; }

            if (slot == null)
            {
                RaiseInteractionWarning();
                L.LogInfo("[EZD-AutoRackOnly] QuickRack: no rack space -> warning.");
                return;
            }

            // Close box
            try { box.CloseBox(); } catch { try { box.CloseBox(false); } catch { } }

            // Physics safety (avoid kinematic velocity warnings: zero velocity BEFORE kinematic)
            ApplyBoxPhysicsSafety(box);

            // Add to rack
            slot.AddBox(boxId, box);
            try { slot.EnableBoxColliders = true; } catch { }
            try { box.gameObject.layer = LayerMask.NameToLayer("Interactable"); } catch { }
            try { box.Racked = true; } catch { }

            ApplyBoxPhysicsSafety(box);

            // --- TEARDOWN (this is what stops "still holding") ---
            // 1) Ask RackManager to finalize placement/end in the most robust way possible
            int rmCalls = InvokeRackManagerTeardown(rm, bi);

            // 2) End interaction on player side (broad)
            int piCalls = InvokeZeroArgByNameContains(pi, "stop", "end", "cancel", "reset", "release", "drop", "clear", "interaction");

            // 3) Hard reset PlayerInteraction fields/flags
            HardResetPlayerInteraction(pi, box, bi);

            // 4) Clear BoxInteraction held reference LAST
            ClearBIHeldBox(bi);

            L.LogInfo($"[EZD-AutoRackOnly] QuickRack: RACKED productId={productId} boxId={boxId} rmTeardownCalls={rmCalls} playerCalls={piCalls}");
        }
        catch (Exception e)
        {
            L.LogWarning("[EZD-AutoRackOnly] QuickRack exception: " + e);
        }
    }

    private static void ApplyBoxPhysicsSafety(Box box)
    {
        if (box == null) return;

        try
        {
            foreach (Collider c in box.gameObject.GetComponentsInChildren<Collider>())
                c.isTrigger = false;
        }
        catch { }

        try
        {
            if (box.TryGetComponent<Rigidbody>(out var rb))
            {
                // avoid warning: set velocity before isKinematic
                try { rb.velocity = Vector3.zero; } catch { }
                rb.interpolation = RigidbodyInterpolation.None;
                rb.isKinematic = true;
            }
        }
        catch { }
    }

    private static void ClearBIHeldBox(BoxInteraction bi)
    {
        try { bi.m_Box = null; return; } catch { }

        try
        {
            var fi = typeof(BoxInteraction).GetField("m_Box", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi != null) fi.SetValue(bi, null);
        }
        catch { }
    }

    // ---------------------------
    // RackManager teardown sweep (don't rely on exact method names)
    // ---------------------------
    private static int InvokeRackManagerTeardown(RackManager rm, BoxInteraction bi)
    {
        if (rm == null || bi == null) return 0;

        int calls = 0;
        Interaction it = null;
        try { it = (Interaction)bi; } catch { it = null; }

        // Try known zero-arg "place" methods first
        if (Call0(rm, "PlaceBoxToRack")) calls++;
        if (Call0(rm, "PlaceHeldBoxToRack")) calls++;

        // Then invoke any 1-arg method that looks like it ends an interaction
        // with signature (Interaction)
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var mis = rm.GetType().GetMethods(flags);

            for (int i = 0; i < mis.Length; i++)
            {
                var mi = mis[i];
                if (mi == null) continue;

                var ps = mi.GetParameters();
                if (ps == null || ps.Length != 1) continue;

                // must accept Interaction or base of it
                bool acceptsInteraction =
                    it != null &&
                    (ps[0].ParameterType == typeof(Interaction) || ps[0].ParameterType.IsAssignableFrom(typeof(Interaction)));

                if (!acceptsInteraction) continue;

                // name token match
                var n = (mi.Name ?? "").ToLowerInvariant();
                if (!(n.Contains("interaction") || n.Contains("end") || n.Contains("stop") || n.Contains("cancel") || n.Contains("finish") || n.Contains("complete")))
                    continue;

                try
                {
                    mi.Invoke(rm, new object[] { it });
                    calls++;
                    if (cfgVerbose.Value) L.LogInfo("[EZD-AutoRackOnly] RackManager teardown call: " + mi.Name);
                }
                catch { }
            }
        }
        catch { }

        return calls;
    }

    private static int InvokeZeroArgByNameContains(object obj, params string[] tokens)
    {
        if (obj == null) return 0;
        int called = 0;

        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var mis = obj.GetType().GetMethods(flags);

            for (int i = 0; i < mis.Length; i++)
            {
                var mi = mis[i];
                if (mi == null) continue;
                if (mi.GetParameters().Length != 0) continue;

                var n = (mi.Name ?? "").ToLowerInvariant();
                bool hit = false;
                for (int t = 0; t < tokens.Length; t++)
                {
                    if (n.Contains(tokens[t])) { hit = true; break; }
                }
                if (!hit) continue;

                try { mi.Invoke(obj, null); called++; } catch { }
            }
        }
        catch { }

        return called;
    }

    private static void HardResetPlayerInteraction(PlayerInteraction pi, Box box, BoxInteraction bi)
    {
        if (pi == null) return;

        // obvious
        try { pi.CurrentInteractable = null; } catch { }

        // common method names
        Call0(pi, "ResetInteraction");
        Call0(pi, "StopInteraction");
        Call0(pi, "EndInteraction");
        Call0(pi, "InteractionEnd");
        Call0(pi, "DropHeldObject");
        Call0(pi, "ReleaseHeldObject");
        Call0(pi, "ClearHeldObject");
        Call0(pi, "ClearInteractable");

        // scrub fields + booleans
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var fields = pi.GetType().GetFields(flags);

            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f == null) continue;

                object v = null;
                try { v = f.GetValue(pi); } catch { }

                // null refs that still point to our box/interaction
                try
                {
                    if (v != null)
                    {
                        if (box != null && ReferenceEquals(v, box))
                        {
                            f.SetValue(pi, null);
                            continue;
                        }
                        if (bi != null && ReferenceEquals(v, bi))
                        {
                            f.SetValue(pi, null);
                            continue;
                        }
                    }
                }
                catch { }

                // flip relevant bools off
                try
                {
                    if (f.FieldType == typeof(bool))
                    {
                        var n = (f.Name ?? "").ToLowerInvariant();
                        if (n.Contains("hold") || n.Contains("carry") || n.Contains("pick") ||
                            n.Contains("interact") || n.Contains("busy") || n.Contains("lock") ||
                            n.Contains("grab") || n.Contains("using") || n.Contains("isbox") || n.Contains("hasbox"))
                        {
                            f.SetValue(pi, false);
                            continue;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        // last resort: call anything that looks like stop/end/reset
        InvokeZeroArgByNameContains(pi, "stop", "end", "cancel", "reset", "release", "drop", "clear");
    }

    private static bool Call0(object obj, string name)
    {
        if (obj == null) return false;
        try
        {
            var mi = obj.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) return false;
            if (mi.GetParameters().Length != 0) return false;
            mi.Invoke(obj, null);
            return true;
        }
        catch { return false; }
    }

    // ---------------------------
    // EmployeeManager / WarningSystem helpers
    // ---------------------------
    private static bool IsProductOccupied(int productId)
    {
        try
        {
            var emp = GetSingleton_Instance(typeof(EmployeeManager)) ?? GetNoktaSingleton_Instance(typeof(EmployeeManager));
            if (emp == null) return false;

            var miOcc = emp.GetType().GetMethod("IsProductOccupied", BindingFlags.Public | BindingFlags.Instance);
            if (miOcc == null) return false;

            return (bool)miOcc.Invoke(emp, new object[] { productId });
        }
        catch { return false; }
    }

    private static void RaiseInteractionWarning()
    {
        try
        {
            var warning = GetSingleton_Instance(typeof(WarningSystem)) ?? GetNoktaSingleton_Instance(typeof(WarningSystem));
            if (warning != null)
            {
                var mi = warning.GetType().GetMethod("RaiseInteractionWarning", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(warning, new object[] { (InteractionWarningType)8, null });
            }
        }
        catch { }
    }

    private static object GetSingleton_Instance(Type t)
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var open = asm.GetType("Singleton`1", false);
                if (open == null) continue;

                var closed = open.MakeGenericType(t);
                var mi = closed.GetMethod("get_Instance", BindingFlags.Public | BindingFlags.Static);
                if (mi == null) continue;

                var inst = mi.Invoke(null, null);
                if (inst != null) return inst;
            }
        }
        catch { }
        return null;
    }

    private static object GetNoktaSingleton_Instance(Type t)
    {
        try
        {
            var open = typeof(NoktaSingleton<>);
            var closed = open.MakeGenericType(t);
            var pi = closed.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (pi == null) return null;
            return pi.GetValue(null, null);
        }
        catch { return null; }
    }

    // ---------------------------
    // Auto rack street boxes (your original)
    // ---------------------------
    private static void AutoRack_StreetBoxes()
    {
        try
        {
            var street = GetStorageStreet();
            var rm = GetRackManager();
            if (street == null || rm == null)
            {
                L.LogWarning("[EZD-AutoRackOnly] AutoRack aborted: StorageStreet or RackManager missing.");
                return;
            }

            var boxes = street.GetAllBoxesFromStreet();
            if (boxes == null)
            {
                if (cfgVerbose.Value) L.LogInfo("[EZD-AutoRackOnly] No street boxes found (null list).");
                return;
            }

            var list = new List<Box>();
            foreach (var b in boxes)
            {
                if (b == null) continue;
                int pc = SafeGet(() => b.ProductCount, 0);
                if (pc > 0) list.Add(b);
            }

            int racked = 0;
            int skipped = 0;
            int scanned = list.Count;

            if (cfgVerbose.Value)
                L.LogInfo($"[EZD-AutoRackOnly] AutoRack scan: scanned={scanned} RackFreeSlots={cfgRackFreeSlots.Value}");

            foreach (var box in list)
            {
                if (box == null || box.Product == null) { skipped++; continue; }

                int productId = SafeGet(() => box.Product.ID, -1);
                int boxId = SafeGet(() => box.BoxID, -1);

                Restocker restocker = null;
                try
                {
                    restocker = new Restocker();
                    RestockerManagementData data = new RestockerManagementData();
                    data.UseUnlabeledRacks = cfgRackFreeSlots.Value;
                    restocker.SetRestockerManagementData(data);
                }
                catch { restocker = null; }

                RackSlot slot = null;
                try { slot = rm.GetRackSlotThatHasSpaceFor(productId, boxId, restocker); }
                catch { slot = null; }

                if (slot == null) { skipped++; continue; }

                bool hasLabel = SafeGet(() => slot.HasLabel, false);

                bool accept =
                    (hasLabel && !cfgRackFreeSlots.Value) ||
                    (!hasLabel && cfgRackFreeSlots.Value);

                if (!accept) { skipped++; continue; }

                try { box.CloseBox(false); } catch { }

                ApplyBoxPhysicsSafety(box);

                try
                {
                    slot.AddBox(boxId, box);
                    try { slot.EnableBoxColliders = true; } catch { }
                    try { box.gameObject.layer = LayerMask.NameToLayer("Interactable"); } catch { }
                    try { box.Racked = true; } catch { }

                    ApplyBoxPhysicsSafety(box);

                    racked++;

                    if (cfgVerbose.Value)
                        L.LogInfo($"[EZD-AutoRackOnly] RACKED productId={productId} boxId={boxId} slot={SafeName(slot)} labeled={hasLabel}");
                }
                catch
                {
                    skipped++;
                }
            }

            L.LogInfo($"[EZD-AutoRackOnly] AutoRack done: scanned={scanned} racked={racked} skipped={skipped} freeSlots={cfgRackFreeSlots.Value}");
        }
        catch (Exception e)
        {
            L.LogWarning("[EZD-AutoRackOnly] AutoRack exception: " + e);
        }
    }

    // ---------------------------
    // Scene discovery
    // ---------------------------
    private static RackManager GetRackManager()
    {
        try
        {
            var rm = UnityEngine.Object.FindObjectOfType<RackManager>();
            if (rm != null) return rm;
        }
        catch { }

        try
        {
            var all = Resources.FindObjectsOfTypeAll<RackManager>();
            if (all != null && all.Length > 0) return all[0];
        }
        catch { }

        return null;
    }

    private static StorageStreet GetStorageStreet()
    {
        try
        {
            var s = UnityEngine.Object.FindObjectOfType<StorageStreet>();
            if (s != null) return s;
        }
        catch { }

        try
        {
            var all = Resources.FindObjectsOfTypeAll<StorageStreet>();
            if (all != null && all.Length > 0) return all[0];
        }
        catch { }

        return null;
    }

    // ---------------------------
    // Utility
    // ---------------------------
    private static T SafeGet<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }

    private static string SafeName(object o)
    {
        if (o == null) return "null";
        try { if (o is UnityEngine.Object uo) return uo.name; } catch { }
        return o.GetType().Name;
    }
}
