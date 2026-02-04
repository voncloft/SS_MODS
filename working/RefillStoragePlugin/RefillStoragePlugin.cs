using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

[BepInPlugin("von.refillstorage.topbar", "Refill Topbar Button", "4.5.0")]
public class RefillStoragePlugin : BasePlugin
{
    internal static ManualLogSource L;
    private Harmony _harmony;

    private static bool injected;

    private static readonly Vector2 BOX_SIZE = new Vector2(22f, 22f);
    private static readonly Vector2 BOX_OFFSET = new Vector2(-52f, 0f);

    private static UnityAction _uaRefill;

    public override void Load()
    {
        L = Log;
        _harmony = new Harmony("von.refillstorage.topbar");

        PatchInjectTrigger();
        L.LogInfo("[REFILL] Loaded 4.5.0");
    }

    private void PatchInjectTrigger()
    {
        try
        {
            var t = Type.GetType("__Project__.Scripts.UI.UIDropdownScroller, Assembly-CSharp")
                 ?? Type.GetType("UIDropdownScroller, Assembly-CSharp");

            if (t == null)
            {
                L.LogWarning("[REFILL] UIDropdownScroller type not found (inject trigger).");
                return;
            }

            var m = t.GetMethod("Select", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (m == null)
            {
                L.LogWarning("[REFILL] UIDropdownScroller.Select not found.");
                return;
            }

            var postfix = typeof(SelectPatch).GetMethod(nameof(SelectPatch.Postfix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            _harmony.Patch(m, postfix: new HarmonyMethod(postfix));
        }
        catch (Exception e)
        {
            L.LogWarning("[REFILL] Patch failed: " + e);
        }
    }

    internal static void TryInject()
    {
        if (injected) return;

        try
        {
            Transform cart = FindByPathContains("Screen/Market App/Taskbar/Cart Button");
            if (cart == null) return;

            var parent = cart.parent;
            if (parent == null) return;

            if (parent.Find("RefillTopbarBox") != null)
            {
                injected = true;
                return;
            }

            var go = new GameObject("RefillTopbarBox");
            go.transform.SetParent(parent, false);
            go.layer = cart.gameObject.layer;

            var rt = go.AddComponent<RectTransform>();
            var cartRT = cart.GetComponent<RectTransform>();

            if (cartRT != null)
            {
                rt.anchorMin = cartRT.anchorMin;
                rt.anchorMax = cartRT.anchorMax;
                rt.pivot = cartRT.pivot;
                rt.anchoredPosition = cartRT.anchoredPosition + BOX_OFFSET;
            }
            else
            {
                rt.anchorMin = new Vector2(1f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = BOX_OFFSET;
            }

            rt.sizeDelta = BOX_SIZE;
            rt.localScale = Vector3.one;

            var img = go.AddComponent<Image>();
            img.color = new Color(1f, 0.2f, 0.2f, 1f);
            img.raycastTarget = true;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            if (_uaRefill == null)
                _uaRefill = DelegateSupport.ConvertDelegate<UnityAction>(new Action(OnRefillClicked));

            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener(_uaRefill);

            injected = true;
            L.LogInfo("[REFILL] Injected button next to Cart Button.");
        }
        catch (Exception e)
        {
            L.LogWarning("[REFILL] Inject failed: " + e);
        }
    }

    private static void OnRefillClicked()
    {
        try
        {
            int entries = RefillToCart();
            L.LogInfo("[REFILL] Clicked. Cart entries added: " + entries);
        }
        catch (Exception e)
        {
            L.LogWarning("[REFILL] Click failed: " + e);
        }
    }

    // STRONGLY TYPED (uses your dump.cs types directly) => no reflection TargetException
    private static int RefillToCart()
    {
        // cart instance
        var carts = Resources.FindObjectsOfTypeAll<MarketShoppingCart>();
        if (carts == null || carts.Length == 0)
        {
            L.LogWarning("[REFILL] MarketShoppingCart not found (open Market App).");
            return 0;
        }
        var cart = carts[0];

        // racks
        var racks = Resources.FindObjectsOfTypeAll<Rack>();
        if (racks == null || racks.Length == 0)
        {
            L.LogWarning("[REFILL] No Rack objects found.");
            return 0;
        }

        int addedEntries = 0;

        for (int r = 0; r < racks.Length; r++)
        {
            var rack = racks[r];
            if (rack == null) continue;

            var slots = rack.RackSlots;
            if (slots == null) continue;

            for (int s = 0; s < slots.Count; s++)
            {
                var slot = slots[s];
                if (slot == null) continue;

                // ignore filtered slots
                if (slot.transform.Find("filterRack") != null)
                    continue;

                if (slot.Full)
                    continue;

                var data = slot.Data;
                if (data == null) continue;

                int productId = data.ProductID;
                if (productId == -1) continue;

                int boxCount = data.BoxCount;

                // product
                var productSO = IDManager.Instance.ProductSO(productId);
                if (productSO == null) continue;

                float basePrice = productSO.BasePrice;
                int boxSizeInt = (int)productSO.GridLayoutInBox.boxSize;

                bool half = (slot.transform.Find("filterHalfRack") != null);

                int need = CalcNeed(boxSizeInt, boxCount, half);
                if (need <= 0) continue;

                var iq = new ItemQuantity(productId, basePrice);
                iq.FirstItemCount = need;

                cart.AddProduct(iq, (SalesType)0);
                addedEntries++;
            }
        }

        return addedEntries;
    }

    // your Refill.txt mapping
    private static int CalcNeed(int size, int count, bool half)
    {
        switch (size)
        {
            case 0: return (half ? 9 : 18) - count;
            case 1: return (half ? 3 : 6) - count;
            case 2: return (half ? 1 : 2) - count;
            case 5: return (half ? 1 : 2) - count;
            case 3:
            case 4:
            case 6: return 1 - count;
            default: return 0;
        }
    }

    private static Transform FindByPathContains(string needle)
    {
        var all = Resources.FindObjectsOfTypeAll<Transform>();
        for (int i = 0; i < all.Length; i++)
        {
            var tr = all[i];
            if (tr == null) continue;
            if (PrettyPath(tr).Contains(needle))
                return tr;
        }
        return null;
    }

    private static string PrettyPath(Transform t)
    {
        string p = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            p = t.name + "/" + p;
        }
        return p;
    }
}

public static class SelectPatch
{
    public static void Postfix()
    {
        RefillStoragePlugin.TryInject();
    }
}
