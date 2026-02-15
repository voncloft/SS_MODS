using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using System;
using System.Reflection;
using UnityEngine;

[BepInPlugin("von.autostowbox", "Auto Stow Box", "1.6.2")]
public class AutoStowBoxPlugin : BasePlugin
{
    internal static ManualLogSource L;
    private Harmony _harmony;

    public override void Load()
    {
        L = Log;
        _harmony = new Harmony("von.autostowbox");
        _harmony.PatchAll(typeof(Patches));
        L.LogInfo("[AUTO-STOW] Loaded 1.6.2 (keypress-only, no timers; object-gated)");
    }

    private static class Patches
    {
        [HarmonyPatch(typeof(PlayerInteraction), "Update")]
        [HarmonyPostfix]
        private static void PlayerInteraction_Update_Postfix(PlayerInteraction __instance)
        {
            // strictly tied to keypress
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

            // Instead of scene-name heuristics, only proceed if a BoxInteraction exists.
            BoxInteraction bi = null;

            try { bi = __instance.GetComponent<BoxInteraction>(); } catch { }
            if (bi == null)
            {
                try { bi = UnityEngine.Object.FindObjectOfType<BoxInteraction>(); } catch { }
            }

            if (bi == null || !bi.enabled)
            {
                L.LogDebug("[AUTO-STOW] Key pressed but no enabled BoxInteraction found (likely menu/transition).");
                return;
            }

            TryStowHeldBox(__instance, bi);
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

    private static void TryStowHeldBox(PlayerInteraction pi, BoxInteraction bi)
    {
        try
        {
            var box = GetHeldBox(bi);
            if (box == null)
            {
                L.LogDebug("[AUTO-STOW] Key pressed but no held box detected.");
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

            ClearBoxInteractionField(bi);

            bool placedSignal = Call0(rm, "PlaceBoxToRack") || Call0(rm, "PlaceHeldBoxToRack");

            bool ended = CallInteraction(pi, "InteractionEnd", (Interaction)bi)
                      || CallInteraction(pi, "EndInteraction", (Interaction)bi);

            try { pi.CurrentInteractable = null; } catch { }

            L.LogInfo("[AUTO-STOW] Stowed ProductID=" + productId + " BoxID=" + boxId +
                      " (PlaceBoxToRack=" + placedSignal + ", InteractionEnd=" + ended + ")");
        }
        catch (Exception e)
        {
            L.LogWarning("[AUTO-STOW] Failed: " + e);
        }
    }
}
