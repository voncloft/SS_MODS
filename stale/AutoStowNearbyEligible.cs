using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using System;
using UnityEngine;

[BepInPlugin("von.autostoweligible", "Auto Stow Eligible (Z)", "1.0.2")]
public class AutoStowEligiblePlugin : BasePlugin
{
    internal static ManualLogSource L;

    // Safety: keep it local and cap the expensive slot lookup to prevent freezes.
    private const int MAX_STOW_PER_PRESS = 250;
    private const int MAX_SLOT_LOOKUPS_PER_PRESS = 800;
    private const float STOW_RADIUS_METERS = 20f;

    public override void Load()
    {
        L = Log;

        ClassInjector.RegisterTypeInIl2Cpp<AutoStowEligibleHotkey>();

        var go = new GameObject("AutoStowEligibleHotkey");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<AutoStowEligibleHotkey>();

        L.LogInfo("[AUTO-STOW-ELIGIBLE] Loaded v1.0.2 (Z = stow nearby eligible boxes; labeled racks only; snap+lock)");
    }

    public class AutoStowEligibleHotkey : MonoBehaviour
    {
        public AutoStowEligibleHotkey(IntPtr ptr) : base(ptr) { }

        void Update()
        {
            bool pressZ;
            try { pressZ = Input.GetKeyDown(KeyCode.Z) || Input.GetKeyDown("z") || Input.GetKeyDown("Z"); }
            catch { pressZ = Input.GetKeyDown(KeyCode.Z); }

            if (!pressZ) return;
            TryAutoStowNearbyEligible();
        }

        private static Restocker BuildRestocker_LabelRulesOnly()
        {
            var restocker = new Restocker();
            var mgmt = new RestockerManagementData();

            // Enforce: unlabeled racks are NOT allowed.
            mgmt.UseUnlabeledRacks = false;

            restocker.SetRestockerManagementData(mgmt);
            return restocker;
        }

        private static Vector3 GetScopePosition()
        {
            try
            {
                var cam = Camera.main;
                if (cam != null) return cam.transform.position;
            }
            catch { }
            return Vector3.zero;
        }

        private static void LockBoxToSlot(Box box, Component slotComponent)
        {
            // 1) Kill physics so it can’t fall through/under shelves
            try
            {
                var rb = box.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.isKinematic = true;
                    rb.detectCollisions = false;
                }
            }
            catch { }

            try
            {
                var cols = box.GetComponentsInChildren<Collider>(true);
                if (cols != null)
                {
                    for (int i = 0; i < cols.Length; i++)
                    {
                        if (cols[i] != null) cols[i].enabled = false;
                    }
                }
            }
            catch { }

            // 2) Snap + parent to the slot transform so visuals match registration
            try
            {
                if (slotComponent != null)
                {
                    var st = slotComponent.transform;
                    if (st != null)
                    {
                        box.transform.position = st.position;
                        box.transform.rotation = st.rotation;
                        box.transform.SetParent(st, true);
                    }
                }
            }
            catch { }
        }

        private static void TryAutoStowNearbyEligible()
        {
            try
            {
                RackManager rm = null;
                try { rm = RackManager.Instance; } catch { }
                if (rm == null) return;

                Box[] boxes;
                try { boxes = UnityEngine.Object.FindObjectsOfType<Box>(); }
                catch { return; }

                if (boxes == null || boxes.Length == 0) return;

                var restocker = BuildRestocker_LabelRulesOnly();
                var scopePos = GetScopePosition();
                float r2 = STOW_RADIUS_METERS * STOW_RADIUS_METERS;

                int stowed = 0;
                int considered = 0;
                int slotLookups = 0;

                for (int i = 0; i < boxes.Length; i++)
                {
                    if (stowed >= MAX_STOW_PER_PRESS) break;
                    if (slotLookups >= MAX_SLOT_LOOKUPS_PER_PRESS) break;

                    var box = boxes[i];
                    if (box == null) continue;

                    try
                    {
                        if (box.Racked) continue;
                        if (box.gameObject == null || !box.gameObject.activeInHierarchy) continue;

                        var pos = box.transform != null ? box.transform.position : Vector3.zero;
                        if ((pos - scopePos).sqrMagnitude > r2) continue;

                        var product = box.Product;
                        if (product == null) continue;

                        int productId = product.ID;
                        int boxId = box.BoxID;

                        considered++;

                        // Expensive call: cap it to avoid freezing.
                        slotLookups++;
                        var slot = rm.GetRackSlotThatHasSpaceFor(productId, boxId, restocker);
                        if (slot == null) continue;

                        // Stow + lock
                        box.CloseBox(false);
                        slot.AddBox(boxId, box);
                        box.Racked = true;

                        // Prevent “beneath shelf” cases by snapping + disabling physics/colliders
                        LockBoxToSlot(box, slot);

                        stowed++;
                    }
                    catch
                    {
                        continue;
                    }
                }

                AutoStowEligiblePlugin.L.LogInfo("[AUTO-STOW-ELIGIBLE] Z: stowed=" + stowed + " considered=" + considered + " slotLookups=" + slotLookups);
            }
            catch (Exception e)
            {
                AutoStowEligiblePlugin.L.LogWarning("[AUTO-STOW-ELIGIBLE] Z failed: " + e);
            }
        }
    }
}
