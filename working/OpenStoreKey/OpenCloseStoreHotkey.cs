using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace OpenCloseStoreHotkey;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class OpenCloseStoreHotkeyPlugin : BasePlugin
{
    public const string PluginGuid = "voncloft.supermarketsim.openclosestorehotkey";
    public const string PluginName = "Open/Close Store Hotkey";
    public const string PluginVersion = "1.1.1";

    internal static ManualLogSource LogSrc;

    public override void Load()
    {
        LogSrc = BepInEx.Logging.Logger.CreateLogSource(PluginName);
        LogSrc.LogInfo($"{PluginName} v{PluginVersion} loaded");
        LogSrc.LogInfo("[OC] Press F10 anytime to toggle store (StoreStatusSign.InstantInteract)");

        // IL2CPP: must register before AddComponent<T>()
        ClassInjector.RegisterTypeInIl2Cpp<OpenCloseRunner>();

        var go = new GameObject("OpenCloseStoreHotkeyRunner");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<OpenCloseRunner>();
    }
}

public sealed class OpenCloseRunner : MonoBehaviour
{
    public OpenCloseRunner(IntPtr ptr) : base(ptr) { }

    private float _debounceUntil;
    private StoreStatusSign _sign;
    private float _nextScan;

    private void Update()
    {
        // Cache StoreStatusSign
        if (_sign == null && Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 1.0f;
            _sign = UnityEngine.Object.FindObjectOfType<StoreStatusSign>(true);
            if (_sign != null)
                OpenCloseStoreHotkeyPlugin.LogSrc.LogInfo("[OC] Found StoreStatusSign");
        }

        // F10 hotkey
        if (!UnityEngine.Input.GetKeyDown(KeyCode.F10))
            return;

        if (Time.unscaledTime < _debounceUntil)
            return;

        _debounceUntil = Time.unscaledTime + 0.25f;

        try
        {
            if (_sign != null)
            {
                bool result = _sign.InstantInteract();
                OpenCloseStoreHotkeyPlugin.LogSrc.LogInfo($"[OC] StoreStatusSign.InstantInteract() => {result}");
                return;
            }

            // Fallback (should rarely be needed)
            var pi = UnityEngine.Object.FindObjectOfType<PlayerInteraction>(true);
            if (pi != null && pi.onOpenClose != null)
            {
                pi.onOpenClose.Invoke();
                OpenCloseStoreHotkeyPlugin.LogSrc.LogWarning("[OC] StoreStatusSign not found; used PlayerInteraction.onOpenClose()");
                return;
            }

            OpenCloseStoreHotkeyPlugin.LogSrc.LogWarning("[OC] No StoreStatusSign or PlayerInteraction available yet.");
        }
        catch (Exception e)
        {
            OpenCloseStoreHotkeyPlugin.LogSrc.LogError($"[OC] Toggle failed: {e}");
        }
    }
}
