using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using System;
using UnityEngine;

[BepInPlugin("von.autostoweligible", "Auto Stow Eligible (Z)", "1.0.0")]
public class AutoStowEligiblePlugin : BasePlugin
{
    internal static ManualLogSource L;

    private const int MAX_STOW_PER_PRESS = 250;

    public override void Load()
    {
        L = Log;

        ClassInjector.RegisterTypeInIl2Cpp<AutoStowEligibleHotkey>();

        var go = new GameObject("AutoStowEligibleHotkey");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<AutoStowEligibleHotkey>();

        L.LogInfo("[AUTO-STOW-ELIGIBLE] Loaded v1.0.0 (Z = stow eligible boxes globally; no label/no space => leave alone)");
    }

    public class AutoStowEligibleHotkey : MonoBehaviour
    {
        public AutoStowEligibleHotkey(IntPtr ptr) : base(ptr) { }

        void Update()
        {
            // Z only (with string fallback)
            bool pressZ = false;
            try
            {
                pressZ = Input.GetKeyDown(KeyCode.Z) || Input.GetKeyDown("z") || Input.GetKeyDown("Z");
            }
            catch
            {
                pressZ = Input.GetKeyDown(KeyCode.Z);
            }

            if (!pressZ)
                return;

            TryAutoStowAllEligible();
        }

        private static Restocker BuildRestocker_LabelRulesOnly()
        {
            var restocker = new Restocker();
            var mgmt = new RestockerManagementData();

            // Enforce: "no label no stow"
            mgmt.UseUnlabeledRacks = false;

            restocker.SetRestockerManagementData(mgmt);
            return restocker;
        }

        private static void TryAutoStowAllEligible()
        {
            try
            {
                RackManager rm = null;
                try { rm = RackManager.Instance; } catch { }
                if (rm == null)
                {
                    AutoStowEligiblePlugin.L.LogDebug("[AUTO-STOW-ELIGIBLE] RackManager.Instance null (menu/loading?)");
                    return;
                }

                Box[] boxes;
                try { boxes = UnityEngine.Object.FindObjectsOfType<Box>(); }
                catch
                {
                    AutoStowEligiblePlugin.L.LogDebug("[AUTO-STOW-ELIGIBLE] FindObjectsOfType<Box>() failed");
                    return;
                }

                if (boxes == null || boxes.Length == 0)
                {
                    AutoStowEligiblePlugin.L.LogInfo("[AUTO-STOW-ELIGIBLE] Z: no boxes found");
                    return;
                }

                var restocker = BuildRestocker_LabelRulesOnly();

                int stowed = 0;
                int considered = 0;

                for (int i = 0; i < boxes.Length; i++)
                {
                    if (stowed >= MAX_STOW_PER_PRESS)
                        break;

                    var box = boxes[i];
                    if (box == null) continue;

                    try
                    {
                        if (box.Racked) continue;
                        if (box.gameObject == null || !box.gameObject.activeInHierarchy) continue;

                        var product = box.Product;
                        if (product == null) continue;

                        int productId = product.ID;
                        int boxId = box.BoxID;

                        considered++;

                        // If no labeled slot with space, leave it alone (no warnings)
                        var slot = rm.GetRackSlotThatHasSpaceFor(productId, boxId, restocker);
                        if (slot == null)
                            continue;

                        // Stow
                        box.CloseBox(false);
                        slot.AddBox(boxId, box);
                        box.Racked = true;

                        stowed++;
                    }
                    catch
                    {
                        // Leave it alone if anything is weird
                        continue;
                    }
                }

                AutoStowEligiblePlugin.L.LogInfo("[AUTO-STOW-ELIGIBLE] Z: stowed=" + stowed + " considered=" + considered + " (left the rest alone)");
            }
            catch (Exception e)
            {
                AutoStowEligiblePlugin.L.LogWarning("[AUTO-STOW-ELIGIBLE] Z failed: " + e);
            }
        }
    }
}
