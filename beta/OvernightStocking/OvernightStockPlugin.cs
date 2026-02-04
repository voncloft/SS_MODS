using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

[BepInPlugin("von.overnightstock.beta", "Overnight Stock (Beta)", "0.1.0")]
public class OvernightStockPlugin : BasePlugin
{
    internal static ManualLogSource L;
    private Harmony _harmony;

    // Guard so we only run once per end-of-day action.
    private static bool _ranThisConfirm = false;

    public override void Load()
    {
        L = Log;
        _harmony = new Harmony("von.overnightstock.beta");

        PatchNextDayInteraction();
        L.LogInfo("[OVERNIGHT] Loaded 0.1.0 (beta skeleton)");
    }

    private void PatchNextDayInteraction()
    {
        try
        {
            // In dumps it is: public class NextDayInteraction : MonoBehaviour
            var t = Type.GetType("NextDayInteraction, Assembly-CSharp")
                 ?? Type.GetType("__Project__.Scripts.NextDayInteraction, Assembly-CSharp"); // just in case

            if (t == null)
            {
                L.LogWarning("[OVERNIGHT] NextDayInteraction type not found.");
                return;
            }

            // In your dump:
            // public void FinishTheDayOrder() { }
            var m = t.GetMethod("FinishTheDayOrder", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                L.LogWarning("[OVERNIGHT] NextDayInteraction.FinishTheDayOrder not found.");
                return;
            }

            var prefix = typeof(NextDayInteraction_FinishTheDayOrder_Patch)
                .GetMethod(nameof(NextDayInteraction_FinishTheDayOrder_Patch.Prefix), BindingFlags.Static | BindingFlags.Public);

            _harmony.Patch(m, prefix: new HarmonyMethod(prefix));
            L.LogInfo("[OVERNIGHT] Patched NextDayInteraction.FinishTheDayOrder()");
        }
        catch (Exception e)
        {
            L.LogWarning("[OVERNIGHT] Patch failed: " + e);
        }
    }

    // This is where the overnight stocking logic will go later.
    // For now it's a stub that only logs.
    internal static void RunOvernightStock()
    {
        try
        {
            L.LogInfo("[OVERNIGHT] Running overnight stock stub (no-op).");

            // TODO (later):
            // 1) Scan shelves/fridges/stalls display slots
            // 2) Compute deficits in ITEMS per productId
            // 3) Pull from rack storage (boxes/items)
            // 4) Apply to displays via game methods (preferred)
        }
        catch (Exception e)
        {
            L.LogWarning("[OVERNIGHT] Overnight stock failed: " + e);
        }
    }

    // Optional: clear guard once the next scene/day starts.
    // If you later patch DayCycleManager.OnStartedNewDay, you can reset there.
    internal static void ResetGuard()
    {
        _ranThisConfirm = false;
    }

    public static class NextDayInteraction_FinishTheDayOrder_Patch
    {
        // Prefix = run BEFORE the day transition starts (usually safest; objects still exist)
        public static void Prefix()
        {
            if (_ranThisConfirm) return;
            _ranThisConfirm = true;

            OvernightStockPlugin.RunOvernightStock();
        }
    }
}
