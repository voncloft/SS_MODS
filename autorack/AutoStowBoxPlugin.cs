using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using System;
using System.Reflection;
using UnityEngine;

[BepInPlugin("von.autostowbox", "Auto Stow Box", "1.4.2")]
public class AutoStowBoxPlugin : BasePlugin
{
    internal static ManualLogSource L;
    private Harmony _harmony;

    private static FieldInfo[] BI_BoxLikeFields;

    // delayed cleanup without coroutines
    private static int _pendingEndFrames = 0;
    private static BoxInteraction _pendingBI = null;

    public override void Load()
    {
        L = Log;
        _harmony = new Harmony("von.autostowbox");
        _harmony.PatchAll(typeof(Patches));

        BI_BoxLikeFields = FindBoxLikeFieldsOnBoxInteraction();
        L.LogInfo("[AUTO-STOW] Loaded 1.4.2 (Press L while holding a box)");
        L.LogInfo("[AUTO-STOW] BoxInteraction fields cleared: " + (BI_BoxLikeFields == null ? 0 : BI_BoxLikeFields.Length));
    }

    private static class Patches
    {
        [HarmonyPatch(typeof(PlayerInteraction), "Update")]
        [HarmonyPostfix]
        private static void PlayerInteraction_Update_Postfix()
        {
            // run delayed cleanup for a couple frames after stowing
            if (_pendingEndFrames > 0)
            {
                _pendingEndFrames--;

                try
                {
                    if (_pendingBI != null)
                    {
                        var pInteract = GetSingleton_Instance(typeof(PlayerInteraction)) ?? GetNoktaSingleton_Instance(typeof(PlayerInteraction));
                        CallIfExists(pInteract, "InteractionEnd", new object[] { _pendingBI });
                    }
                }
                catch { }

                if (_pendingEndFrames <= 0)
                    _pendingBI = null;
            }

            if (Input.GetKeyDown(KeyCode.L))
                TryStowHeldBox();
        }
    }

    private static FieldInfo[] FindBoxLikeFieldsOnBoxInteraction()
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var fis = typeof(BoxInteraction).GetFields(flags);
            var list = new System.Collections.Generic.List<FieldInfo>();

            for (int i = 0; i < fis.Length; i++)
            {
                var fi = fis[i];
                if (fi == null) continue;

                if (fi.FieldType == typeof(Box))
                    list.Add(fi);

                if (typeof(Il2CppObjectBase).IsAssignableFrom(fi.FieldType))
                {
                    var n = fi.Name.ToLowerInvariant();
                    if (n.Contains("box") || n.Contains("m_box"))
                        list.Add(fi);
                }
            }

            return list.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static Box TryGetHeldBox(BoxInteraction bi)
    {
        // 1) Try via Interaction.Interactable -> IL2CPP TryCast<Box>
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

        // 2) Try via known fields
        try
        {
            if (BI_BoxLikeFields != null)
            {
                for (int i = 0; i < BI_BoxLikeFields.Length; i++)
                {
                    var fi = BI_BoxLikeFields[i];
                    var v = fi.GetValue(bi);
                    if (v == null) continue;

                    var b = v as Box;
                    if (b != null) return b;

                    var raw = v as Il2CppObjectBase;
                    if (raw != null)
                    {
                        var bb = raw.TryCast<Box>();
                        if (bb != null) return bb;
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private static void ClearHeldState(BoxInteraction bi)
    {
        // Clear any box-like fields
        try
        {
            if (BI_BoxLikeFields != null)
            {
                for (int i = 0; i < BI_BoxLikeFields.Length; i++)
                {
                    try { BI_BoxLikeFields[i].SetValue(bi, null); } catch { }
                }
            }
        }
        catch { }

        // Force cleanup paths
        try
        {
            bi.enabled = false;
            bi.enabled = true;
        }
        catch { }
    }

    private static object GetSingleton_Instance(Type t) // Singleton<T>.get_Instance()
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

    private static object GetNoktaSingleton_Instance(Type t) // NoktaSingleton<T>.Instance
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

    private static void CallIfExists(object obj, string methodName, object[] args)
    {
        if (obj == null) return;
        try
        {
            var mi = obj.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi != null) mi.Invoke(obj, args);
        }
        catch { }
    }

    private static void TryStowHeldBox()
    {
        try
        {
            var playerGO = GameObject.Find("Player");
            if (playerGO == null)
            {
                L.LogWarning("[AUTO-STOW] Player GameObject not found.");
                return;
            }

            var bi = playerGO.GetComponent<BoxInteraction>();
            if (bi == null)
            {
                L.LogWarning("[AUTO-STOW] BoxInteraction not found on Player.");
                return;
            }

            if (!bi.enabled)
            {
                L.LogWarning("[AUTO-STOW] BoxInteraction disabled.");
                return;
            }

            var box = TryGetHeldBox(bi);
            if (box == null)
            {
                L.LogWarning("[AUTO-STOW] Held Box not resolved.");
                return;
            }

            var product = box.Product;
            if (product == null)
            {
                L.LogWarning("[AUTO-STOW] Box.Product is null.");
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
                        RaiseInteractionWarning("Occupied by Restocker");
                        return;
                    }
                }
            }

            RackManager rm = null;
            try { rm = RackManager.Instance; } catch { }
            if (rm == null)
            {
                L.LogWarning("[AUTO-STOW] RackManager.Instance is null.");
                return;
            }

            var restocker = new Restocker();
            var mgmt = new RestockerManagementData();
            mgmt.UseUnlabeledRacks = true;
            restocker.SetRestockerManagementData(mgmt);

            var slot = rm.GetRackSlotThatHasSpaceFor(productId, boxId, restocker);
            if (slot == null)
            {
                RaiseInteractionWarning("No rack space");
                return;
            }

            // place like EZDelivery
            box.CloseBox(false);
            slot.AddBox(boxId, box);
            box.Racked = true;

            var holder = GetSingleton_Instance(typeof(PlayerObjectHolder)) ?? GetNoktaSingleton_Instance(typeof(PlayerObjectHolder));
            var pInteract = GetSingleton_Instance(typeof(PlayerInteraction)) ?? GetNoktaSingleton_Instance(typeof(PlayerInteraction));

            ClearHeldState(bi);

            CallIfExists(holder, "PlaceBoxToRack", null);
            CallIfExists(pInteract, "InteractionEnd", new object[] { bi });

            // next-frame (actually next 2 frames) cleanup without coroutine
            _pendingBI = bi;
            _pendingEndFrames = 2;

            L.LogInfo("[AUTO-STOW] Stowed ProductID=" + productId + " BoxID=" + boxId);
        }
        catch (Exception e)
        {
            L.LogWarning("[AUTO-STOW] Failed: " + e);
        }
    }

    private static void RaiseInteractionWarning(string msg)
    {
        try
        {
            var warning = GetSingleton_Instance(typeof(WarningSystem)) ?? GetNoktaSingleton_Instance(typeof(WarningSystem));
            CallIfExists(warning, "RaiseInteractionWarning", new object[] { (InteractionWarningType)8, null });
        }
        catch { }

        L.LogWarning("[AUTO-STOW] " + msg);
    }
}
