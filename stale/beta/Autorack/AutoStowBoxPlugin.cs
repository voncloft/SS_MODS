using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using System;
using System.Reflection;
using UnityEngine;

[BepInPlugin("von.autostowbox", "Auto Stow Box", "1.6.6")]
public class AutoStowBoxPlugin : BasePlugin
{
    internal static ManualLogSource L;
    private Harmony _harmony;

    // If another mod disables BoxInteraction right after we stow,
    // we treat that as "danger window" and recover.
    private static float _lastStowTime = -999f;
    private const float DisableRecoveryWindowSeconds = 0.75f;

    public override void Load()
    {
        L = Log;
        _harmony = new Harmony("von.autostowbox");
        _harmony.PatchAll(typeof(Patches));
        L.LogInfo("[AUTO-STOW] Loaded 1.6.6 (fix: recover from BoxInteraction disabling after stow)");
    }

    private static class Patches
    {
        [HarmonyPatch(typeof(PlayerInteraction), "Update")]
        [HarmonyPostfix]
        private static void PlayerInteraction_Update_Postfix(PlayerInteraction __instance)
        {
            if (!Input.GetKeyDown(KeyCode.L))
                return;

            try
            {
                if (__instance == null) return;
                if (!__instance.isActiveAndEnabled) return;
                if (__instance.gameObject == null) return;
                if (!__instance.gameObject.activeInHierarchy) return;
            }
            catch { return; }

            BoxInteraction bi = null;

            try { bi = __instance.GetComponent<BoxInteraction>(); } catch { }
            if (bi == null)
            {
                try { bi = UnityEngine.Object.FindObjectOfType<BoxInteraction>(); } catch { }
            }

            if (bi == null)
            {
                L.LogDebug("[AUTO-STOW] Key pressed but BoxInteraction not found.");
                return;
            }

            TryStowHeldBox(__instance, bi);
        }

        // Critical recovery: if something disables BoxInteraction right after we stow,
        // the player can remain "locked/holding". Recover ONLY in the short window.
        [HarmonyPatch(typeof(BoxInteraction), "OnDisable")]
        [HarmonyPostfix]
        private static void BoxInteraction_OnDisable_Postfix(BoxInteraction __instance)
        {
            try
            {
                if (__instance == null) return;

                float dt = Time.time - _lastStowTime;
                if (dt < 0f || dt > DisableRecoveryWindowSeconds)
                    return;

                // Re-enable (within window only)
                try
                {
                    if (!__instance.enabled)
                        __instance.enabled = true;
                }
                catch { }

                // Find player interaction and hard reset again (this is the "unlock")
                PlayerInteraction pi = null;
                try { pi = UnityEngine.Object.FindObjectOfType<PlayerInteraction>(); } catch { }

                Box box = null;
                try { box = GetHeldBox(__instance); } catch { }

                HardResetPlayerInteraction(pi, box, __instance);

                // Clear last
                ClearBoxInteractionField(__instance);

                L.LogInfo("[AUTO-STOW] Recovery: BoxInteraction disabled right after stow; re-enabled + reset player state");
            }
            catch { }
        }
    }

    private static Box GetHeldBox(BoxInteraction bi)
    {
        try
        {
            if (bi != null && bi.m_Box != null)
                return bi.m_Box;
        }
        catch { }

        try
        {
            var interactable = ((Interaction)bi).Interactable;
            if (interactable != null)
            {
                var raw = (Il2CppObjectBase)(object)interactable;
                if (raw != null)
                {
                    var b = raw.TryCast<Box>();
                    if (b != null) return b;
                }
            }
        }
        catch { }

        return null;
    }

    private static bool Call0_OnType(object obj, Type t, string name)
    {
        if (obj == null || t == null) return false;
        try
        {
            var mi = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi == null) return false;
            if (mi.GetParameters().Length != 0) return false;
            mi.Invoke(obj, null);
            return true;
        }
        catch { return false; }
    }

    private static bool Call0(object obj, string name)
    {
        if (obj == null) return false;
        try
        {
            var mi = obj.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi == null) return false;
            if (mi.GetParameters().Length != 0) return false;
            mi.Invoke(obj, null);
            return true;
        }
        catch { return false; }
    }

    private static int InvokeZeroArgByNameContains(object obj, params string[] tokens)
    {
        if (obj == null) return 0;
        int called = 0;
        try
        {
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;
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
                    if (n.Contains(tokens[t]))
                    {
                        hit = true;
                        break;
                    }
                }
                if (!hit) continue;

                try
                {
                    mi.Invoke(obj, null);
                    called++;
                }
                catch { }
            }
        }
        catch { }
        return called;
    }

    private static bool CallInteraction(object obj, string name, Interaction interaction)
    {
        if (obj == null || interaction == null) return false;
        try
        {
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;
            var mis = obj.GetType().GetMethods(flags);

            for (int i = 0; i < mis.Length; i++)
            {
                var mi = mis[i];
                if (mi == null) continue;
                if (mi.Name != name) continue;

                var ps = mi.GetParameters();
                if (ps == null || ps.Length != 1) continue;

                if (ps[0].ParameterType == typeof(Interaction) ||
                    ps[0].ParameterType.IsAssignableFrom(typeof(Interaction)))
                {
                    mi.Invoke(obj, new object[] { interaction });
                    return true;
                }
            }
        }
        catch { }
        return false;
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

    private static void ClearBoxInteractionField(BoxInteraction bi)
    {
        try { bi.m_Box = null; return; } catch { }

        try
        {
            var fi = typeof(BoxInteraction).GetField("m_Box", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi != null) fi.SetValue(bi, null);
        }
        catch { }
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

    private static void HardResetPlayerInteraction(PlayerInteraction pi, Box box, BoxInteraction bi)
    {
        if (pi == null) return;

        try { pi.CurrentInteractable = null; } catch { }

        Call0(pi, "ResetInteraction");
        Call0(pi, "StopInteraction");
        Call0(pi, "EndInteraction");
        Call0(pi, "InteractionEnd");
        Call0(pi, "DropHeldObject");
        Call0(pi, "ReleaseHeldObject");
        Call0(pi, "ClearHeldObject");
        Call0(pi, "ClearInteractable");

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

                try
                {
                    if (v != null && (ReferenceEquals(v, box) || ReferenceEquals(v, bi)))
                    {
                        f.SetValue(pi, null);
                        continue;
                    }
                }
                catch { }

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

        InvokeZeroArgByNameContains(pi, "stop", "end", "cancel", "reset", "release", "drop", "clear");
    }

    private static bool ForceEndInteractionChain(PlayerInteraction pi, BoxInteraction bi)
    {
        bool ended = false;

        ended = Call0(bi, "InteractionEnd") || ended;
        ended = Call0(bi, "EndInteraction") || ended;
        ended = Call0(bi, "StopInteraction") || ended;

        try
        {
            Interaction it = (Interaction)bi;
            ended = Call0_OnType(it, typeof(Interaction), "InteractionEnd") || ended;
            ended = Call0_OnType(it, typeof(Interaction), "EndInteraction") || ended;
            ended = Call0_OnType(it, typeof(Interaction), "StopInteraction") || ended;
        }
        catch { }

        if (!ended)
        {
            int c = InvokeZeroArgByNameContains(bi, "interactionend", "endinteraction", "stopinteraction", "end", "stop", "cancel");
            ended = c > 0;
        }

        try
        {
            ended = CallInteraction(pi, "InteractionEnd", (Interaction)bi) || ended;
            ended = CallInteraction(pi, "EndInteraction", (Interaction)bi) || ended;
        }
        catch { }

        ended = Call0(pi, "InteractionEnd") || ended;
        ended = Call0(pi, "EndInteraction") || ended;
        ended = Call0(pi, "StopInteraction") || ended;

        return ended;
    }

    private static void TryStowHeldBox(PlayerInteraction pi, BoxInteraction bi)
    {
        try
        {
            var box = GetHeldBox(bi);
            if (box == null)
            {
                L.LogDebug("[AUTO-STOW] No held box detected.");
                return;
            }

            try { if (box.Racked) return; } catch { }

            var product = box.Product;
            if (product == null)
            {
                L.LogDebug("[AUTO-STOW] Held box has no product.");
                return;
            }

            int productId = product.ID;
            int boxId = box.BoxID;

            var emp = GetSingleton_Instance(typeof(EmployeeManager)) ?? GetNoktaSingleton_Instance(typeof(EmployeeManager));
            if (emp != null)
            {
                var miOcc = emp.GetType().GetMethod("IsProductOccupied", BindingFlags.Public | BindingFlags.Instance);
                if (miOcc != null)
                {
                    bool occupied = (bool)miOcc.Invoke(emp, new object[] { productId });
                    if (occupied)
                    {
                        RaiseInteractionWarning();
                        return;
                    }
                }
            }

            RackManager rm = null;
            try { rm = RackManager.Instance; } catch { }
            if (rm == null)
            {
                L.LogDebug("[AUTO-STOW] RackManager.Instance was null.");
                return;
            }

            var restocker = new Restocker();
            var mgmt = new RestockerManagementData();
            mgmt.UseUnlabeledRacks = true;
            restocker.SetRestockerManagementData(mgmt);

            var slot = rm.GetRackSlotThatHasSpaceFor(productId, boxId, restocker);
            if (slot == null)
            {
                RaiseInteractionWarning();
                return;
            }

            box.CloseBox(false);
            slot.AddBox(boxId, box);
            box.Racked = true;

            bool chainEnded = ForceEndInteractionChain(pi, bi);

            // Mark: we just stowed, so if BoxInteraction gets disabled we recover.
            _lastStowTime = Time.time;

            // Unlock player immediately
            HardResetPlayerInteraction(pi, box, bi);

            // Clear BoxInteraction LAST
            ClearBoxInteractionField(bi);

            L.LogInfo("[AUTO-STOW] Stowed ProductID=" + productId + " BoxID=" + boxId + " (ChainEnded=" + chainEnded + ")");
        }
        catch (Exception e)
        {
            L.LogWarning("[AUTO-STOW] Failed: " + e);
        }
    }
}
