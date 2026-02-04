using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using System;
using System.Reflection;
using UnityEngine;

[BepInPlugin("von.autostowbox", "Auto Stow Box", "1.6.0")]
public class AutoStowBoxPlugin : BasePlugin
{
    internal static ManualLogSource L;
    private Harmony _harmony;

    public override void Load()
    {
        L = Log;
        _harmony = new Harmony("von.autostowbox");
        _harmony.PatchAll(typeof(Patches));
        L.LogInfo("[AUTO-STOW] Loaded 1.6.0 (EZDelivery-style end: m_Box=null + PlaceBoxToRack + InteractionEnd)");
    }

    private static class Patches
    {
        [HarmonyPatch(typeof(PlayerInteraction), "Update")]
        [HarmonyPostfix]
        private static void PlayerInteraction_Update_Postfix(PlayerInteraction __instance)
        {
            if (Input.GetKeyDown(KeyCode.L))
                TryStowHeldBox(__instance);
        }
    }

    private static Box GetHeldBox(BoxInteraction bi)
    {
        try
        {
            // if available, this is best
            if (bi != null && bi.m_Box != null)
                return bi.m_Box;
        }
        catch { }

        try
        {
            // fallback: cast interactable
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

                // accept Interaction or base type
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
        // EZDelivery does Traverse.SetValue(null) on "m_Box".
        // We can do it directly if field is accessible; otherwise reflection.
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

    private static void TryStowHeldBox(PlayerInteraction pi)
    {
        try
        {
            var playerGO = GameObject.Find("Player");
            if (playerGO == null) return;

            var bi = playerGO.GetComponent<BoxInteraction>();
            if (bi == null) return;

            // EZDelivery note: BI must be enabled else "magic invisible boxes"
            if (!bi.enabled) return;

            var box = GetHeldBox(bi);
            if (box == null) return;

            // prevent repeated stow spam
            try { if (box.Racked) return; } catch { }

            var product = box.Product;
            if (product == null) return;

            int productId = product.ID;
            int boxId = box.BoxID;

            // occupied check
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
            if (rm == null) return;

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

            // Place like EZDelivery
            box.CloseBox(false);
            slot.AddBox(boxId, box);
            box.Racked = true;

            // 1) m_Box = null (critical)
            ClearBoxInteractionField(bi);

            // 2) RackManager.PlaceBoxToRack (if present)
            // In EZDelivery it's Singleton.Instance.PlaceBoxToRack().
            // In our environment, RackManager.Instance is the one we have.
            bool placedSignal = Call0(rm, "PlaceBoxToRack") || Call0(rm, "PlaceHeldBoxToRack");

            // 3) PlayerInteraction.InteractionEnd((Interaction)bi) (critical store unlock)
            bool ended = CallInteraction(pi, "InteractionEnd", (Interaction)bi)
                      || CallInteraction(pi, "EndInteraction", (Interaction)bi);

            // final cleanup (helps PocketBox not think you're still interacting)
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
