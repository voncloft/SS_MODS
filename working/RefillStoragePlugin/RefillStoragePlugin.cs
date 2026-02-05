using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

[BepInPlugin("von.refillstorage.exact", "Refill Storage (Exact)", "7.0.2")]
public class RefillStoragePlugin : BasePlugin
{
    internal static ManualLogSource L;
    private Harmony _harmony;
    private static bool injected;
    private static UnityAction _uaRefill;

    private static readonly Vector2 BOX_SIZE = new Vector2(22f, 22f);
    private static readonly Vector2 BOX_OFFSET = new Vector2(-52f, 0f);

    public override void Load()
    {
        L = Log;
        _harmony = new Harmony("von.refillstorage.exact");
        PatchInjectTrigger();
        L.LogInfo("[REFILL] Loaded EXACT refill logic");
    }

    private void PatchInjectTrigger()
    {
        var t = Type.GetType("__Project__.Scripts.UI.UIDropdownScroller, Assembly-CSharp")
             ?? Type.GetType("UIDropdownScroller, Assembly-CSharp");
        if (t == null) return;

        var m = t.GetMethod("Select", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (m == null) return;

        _harmony.Patch(m,
            postfix: new HarmonyMethod(typeof(SelectPatch).GetMethod(nameof(SelectPatch.Postfix))));
    }

    internal static void TryInject()
    {
        if (injected) return;

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

        var rt = go.AddComponent<RectTransform>();
        var crt = cart.GetComponent<RectTransform>();
        rt.anchorMin = crt.anchorMin;
        rt.anchorMax = crt.anchorMax;
        rt.pivot = crt.pivot;
        rt.anchoredPosition = crt.anchoredPosition + BOX_OFFSET;
        rt.sizeDelta = BOX_SIZE;

        var img = go.AddComponent<Image>();
        img.color = Color.red;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;

        if (_uaRefill == null)
            _uaRefill = DelegateSupport.ConvertDelegate<UnityAction>(
                new Action(OnRefillClicked));

        btn.onClick = new Button.ButtonClickedEvent();
        btn.onClick.AddListener(_uaRefill);

        injected = true;
    }

    private static void OnRefillClicked()
    {
        int added = RefillRacks_Exact();
        L.LogInfo("[REFILL] Added cart entries: " + added);
    }

    // === EXACT ENGINE LOGIC ===
    private static int RefillRacks_Exact()
    {
        var carts = Resources.FindObjectsOfTypeAll<MarketShoppingCart>();
        if (carts.Length == 0) return 0;
        var cart = carts[0];

        int added = 0;

        foreach (var rack in Resources.FindObjectsOfTypeAll<Rack>())
        {
            if (rack == null || rack.RackSlots == null) continue;

            foreach (var slot in rack.RackSlots)
            {
                if (slot == null || slot.Full) continue;

                var data = slot.Data;
                if (data == null || data.ProductID < 0) continue;

                int current = data.BoxCount;

                var product = IDManager.Instance.ProductSO(data.ProductID);
                if (product == null) continue;

                bool halfSlot = slot.transform.Find("filterHalfRack") != null;

                int capacity = GetCapacityFromBoxSize(product.GridLayoutInBox.boxSize, halfSlot);
                int need = capacity - current;

                if (need <= 0) continue;

                var iq = new ItemQuantity(data.ProductID, product.BasePrice);
                iq.FirstItemCount = need;

                cart.AddProduct(iq, (SalesType)0);
                added++;
            }
        }

        return added;
    }

    // EXACT COPY of real mod behavior + add 22x22x8 => 4 (or 2 on half)
    private static int GetCapacityFromBoxSize(BoxSize size, bool half)
    {
        // SAFE dynamic: only trigger if the enum name explicitly mentions 22x22x8.
        // (No greedy digit parsing; avoids the "59 boxes" problem.)
        string n = size.ToString();
        if (!string.IsNullOrEmpty(n))
        {
            string nn = n.ToLowerInvariant();
            // accept common separators seen in enum names
            if (nn.Contains("22x22x8") || nn.Contains("22_22_8") || nn.Contains("22-22-8"))
                return half ? 2 : 4;
        }

        // Fallback: int mapping (what the Melon mod switch does)
        switch ((int)size)
        {
            case 0: return half ? 9 : 18;
            case 1: return half ? 3 : 6;
            case 2: return half ? 1 : 2;
            case 5: return half ? 1 : 2;

            case 3:
            case 4:
            case 6:
                return 1;

            // NEW: likely the added enum value for 22x22x8 in newer builds
            case 7:
                return half ? 2 : 4;

            default:
                return 0; // unknown → buy nothing
        }
    }

    private static Transform FindByPathContains(string needle)
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t != null && PrettyPath(t).Contains(needle))
                return t;
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
