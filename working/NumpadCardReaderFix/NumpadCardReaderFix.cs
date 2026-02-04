using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NumpadCardReaderFix;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class NumpadCardReaderFixPlugin : BasePlugin
{
    public const string PluginGuid = "voncloft.supermarketsim.numpadpos";
    public const string PluginName = "Numpad -> POS Terminal";
    public const string PluginVersion = "3.0.3";

    internal static ManualLogSource LogSrc;

    public override void Load()
    {
        LogSrc = BepInEx.Logging.Logger.CreateLogSource(PluginName);
        LogSrc.LogInfo($"{PluginName} v{PluginVersion} loaded");

        // IL2CPP REQUIREMENT: register custom MonoBehaviour types before AddComponent<T>()
        ClassInjector.RegisterTypeInIl2Cpp<NumpadPosTerminalFixBehaviour>();

        var go = new UnityEngine.GameObject("NumpadPosTerminalFix");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;

        go.AddComponent<NumpadPosTerminalFixBehaviour>();

        LogSrc.LogInfo("[NPOS] Runner attached. Numpad 0-9 => AddChar(), Numpad Enter => Approve(), Numpad - => RemoveCharacter()");
    }
}

public class NumpadPosTerminalFixBehaviour : MonoBehaviour
{
    // IL2CPP REQUIREMENT: IntPtr ctor
    public NumpadPosTerminalFixBehaviour(IntPtr ptr) : base(ptr) { }

    private float _nextScanTime;
    private PosTerminal _cached;

    private void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        // refresh cached terminal periodically
        if (_cached == null || Time.unscaledTime >= _nextScanTime)
        {
            _cached = FindActivePosTerminal();
            _nextScanTime = Time.unscaledTime + 0.25f;

            if (_cached != null)
                NumpadCardReaderFixPlugin.LogSrc.LogInfo("[NPOS] Found active PosTerminal");
        }

        if (_cached == null) return;

        if (kb.numpad0Key.wasPressedThisFrame) _cached.AddChar("0");
        if (kb.numpad1Key.wasPressedThisFrame) _cached.AddChar("1");
        if (kb.numpad2Key.wasPressedThisFrame) _cached.AddChar("2");
        if (kb.numpad3Key.wasPressedThisFrame) _cached.AddChar("3");
        if (kb.numpad4Key.wasPressedThisFrame) _cached.AddChar("4");
        if (kb.numpad5Key.wasPressedThisFrame) _cached.AddChar("5");
        if (kb.numpad6Key.wasPressedThisFrame) _cached.AddChar("6");
        if (kb.numpad7Key.wasPressedThisFrame) _cached.AddChar("7");
        if (kb.numpad8Key.wasPressedThisFrame) _cached.AddChar("8");
        if (kb.numpad9Key.wasPressedThisFrame) _cached.AddChar("9");

        if (kb.numpadEnterKey.wasPressedThisFrame)
            _cached.Approve();

        if (kb.numpadMinusKey.wasPressedThisFrame)
            _cached.RemoveCharacter();
    }

    private static PosTerminal FindActivePosTerminal()
    {
        var all = UnityEngine.Object.FindObjectsOfType<PosTerminal>(true);
        if (all == null || all.Length == 0) return null;

        // Prefer active + interactable
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == null) continue;
            if (t.gameObject == null) continue;
            if (!t.gameObject.activeInHierarchy) continue;

            if (t.EnablePosInteraction)
                return t;
        }

        // Fallback: any active
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == null) continue;
            if (t.gameObject != null && t.gameObject.activeInHierarchy)
                return t;
        }

        return null;
    }
}
