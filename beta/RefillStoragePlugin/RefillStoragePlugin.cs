using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[BepInPlugin("von.refillstorage.exact", "Refill Storage (Exact)", "7.0.3")]
public class RefillStoragePlugin : BasePlugin
{
    internal static ManualLogSource L;

    private Harmony _harmony;

    private static bool injected;
    private static UnityAction _uaRefill;

    private static GameObject _runnerGO;
    private static float _lastTryT;

    private static readonly Vector2 BOX_SIZE = new Vector2(22f, 22f);
    private static readonly Vector2 BOX_OFFSET = new Vector2(-52f, 0f);

    public override void Load()
    {
        L = Log;

        _harmony = new Harmony("von.refillstorage.exact");
        PatchInjectTrigger();

        EnsureRunner();

        // Scene reloads are what kills your UI injection. Reset and reinject.
        SceneManager.sceneLoaded += (Action<Scene, LoadSceneMode>)OnSceneLoaded;

        L.LogInfo("[REFILL] Loaded (Exact) w/ scene-safe reinject.");
    }

    private static void OnSceneLoaded(Scene s, LoadSceneMode mode)
    {
        injected = false;
        _lastTryT = 0f;
        L.LogInfo($"[REFILL] SceneLoaded name='{s.name}' mode={mode} -> reset injected=false");
    }

    private void PatchInjectTrigger()
    {
        var t = Type.GetType("__Project__.Scripts.UI.UIDropdownScroller, Assembly-CSharp")
             ?? Type.GetType("UIDropdownScroller, Assembly-CSharp");
        if (t == null)
        {
            L.LogWarning("[REFILL] UIDropdownScroller type not found; relying on runner only.");
            return;
        }

        var m = t.GetMethod("Select", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (m == null)
        {
            L.LogWarning("[REFILL] UIDropdownScroller.Select not found; relying on runner only.");
            return;
        }

        _harmony.Patch(m,
            postfix: new HarmonyMethod(typeof(SelectPatch).GetMethod(nameof(SelectPatch.Postfix))));

        L.LogInfo("[REFILL] Patched UIDropdownScroller.Select for inject trigger.");
    }

    private static void EnsureRunner()
    {
        try
        {
            // Register once for IL2CPP
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp(typeof(RefillRunner)))
                ClassInjector.RegisterTypeInIl2Cpp<RefillRunner>();

            if (_runnerGO != null) return;

            _runnerGO = new GameObject("RefillStorageRunner");
            UnityEngine.Object.DontDestroyOnLoad(_runnerGO);
            _runnerGO.hideFlags = HideFlags.HideAndDontSave;
            _runnerGO.AddComponent<RefillRunner>();

            L.LogInfo("[REFILL] Runner created (DontDestroyOnLoad).");
        }
        catch (Exception e)
        {
            L.LogError("[REFILL] EnsureRunner failed: " + e);
        }
    }

    internal static void TryInject()
    {
        // throttle spam
        if (Time.realtimeSinceStartup - _lastTryT < 0.5f)
            return;
        _lastTryT = Time.realtimeSinceStartup;

        try
        {
            // If we think we're injected but the button isn't present anymore (scene reload),
            // clear the flag so we can re-inject.
            if (injected)
            {
                var existing = FindExistingButton();
                if (existing == null)
                {
                    injected = false;
                    L.LogInfo("[REFILL] Button missing but injected=true -> reset injected=false");
                }
                else
                {
                    return; // still there, all good
                }
            }

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
            L.LogInfo("[REFILL] Injected button next to Cart Button.");
        }
        catch (Exception e)
        {
            L.LogError("[REFILL] TryInject exception: " + e);
        }
    }

    private static Transform FindExistingButton()
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (t == null) continue;
            if (t.name == "RefillTopbarBox") return t;
        }
        return null;
    }

    private static void OnRefillClicked()
    {
        try
        {
            int added = RefillRacks_Exact();
            L.LogInfo("[REFILL] Added cart entries: " + added);
        }
        catch (Exception e)
        {
            L.LogError("[REFILL] OnRefillClicked exception: " + e);
        }
    }

    // === EXACT ENGINE LOGIC ===
    private static int RefillRacks_Exact()
    {
        var carts = Resources.FindObjectsOfTypeAll<MarketShoppingCart>();
        if (carts == null || carts.Length == 0) return 0;

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

                var idm = IDManager.Instance;
                if (idm == null) continue;

                var product = idm.ProductSO(data.ProductID);
                if (product == null) continue;

                bool halfSlot = slot.transform != null && slot.transform.Find("filterHalfRack") != null;

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
        string n = size.ToString();
        if (!string.IsNullOrEmpty(n))
        {
            string nn = n.ToLowerInvariant();
            if (nn.Contains("22x22x8") || nn.Contains("22_22_8") || nn.Contains("22-22-8"))
                return half ? 2 : 4;
        }

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

            case 7:
                return half ? 2 : 4;

            default:
                return 0;
        }
    }

    private static Transform FindByPathContains(string needle)
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (t == null) continue;
            var p = PrettyPath(t);
            if (p.Contains(needle)) return t;
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

public class RefillRunner : MonoBehaviour
{
    private float _t;

    public void Update()
    {
        _t += Time.unscaledDeltaTime;

        // Every ~1s: attempt injection (covers Continue/menu rebuilds without needing dropdown activity)
        if (_t >= 1.0f)
        {
            _t = 0f;
            RefillStoragePlugin.TryInject();
        }
    }
}
