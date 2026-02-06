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
    public const string PluginVersion = "3.0.7";

    internal static ManualLogSource L;

    public override void Load()
    {
        L = BepInEx.Logging.Logger.CreateLogSource(PluginName);
        L.LogInfo($"{PluginName} v{PluginVersion} loaded");

        ClassInjector.RegisterTypeInIl2Cpp<NumpadPosTerminalFixBehaviour>();

        var go = new GameObject("NumpadPosTerminalFix");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;

        go.AddComponent<NumpadPosTerminalFixBehaviour>();

        L.LogInfo("[NPOS] Runner attached. Numpad 0-9 => AddChar(), Numpad Enter => Approve(), Numpad - => RemoveCharacter()");
    }
}

public sealed class NumpadPosTerminalFixBehaviour : MonoBehaviour
{
    public NumpadPosTerminalFixBehaviour(IntPtr ptr) : base(ptr) { }

    private PosTerminal _cached;

    // Keep scans sparse; FindObjectsOfType is expensive.
    private float _nextScanTime;

    // Rate-limit log spam
    private float _nextLostLogTime;
    private bool _foundLogged;

    private void OnEnable()
    {
        _cached = null;
        _nextScanTime = 0f;
        _nextLostLogTime = 0f;
        _foundLogged = false;
    }

    private void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        // Only invalidate cache on real "gone" conditions (destroyed / inactive scene object).
        if (!IsStillValidObject(_cached))
        {
            if (_cached != null && Time.unscaledTime >= _nextLostLogTime)
            {
                NumpadCardReaderFixPlugin.L.LogInfo("[NPOS] PosTerminal not valid/active anymore. Will rescan.");
                _nextLostLogTime = Time.unscaledTime + 10f; // at most once every 10s
            }

            _cached = null;
            _foundLogged = false;
        }

        // Only scan when we don't have a cached terminal.
        if (_cached == null && Time.unscaledTime >= _nextScanTime)
        {
            _nextScanTime = Time.unscaledTime + 2.0f; // 2s scan cadence

            _cached = FindAnyActivePosTerminal();

            if (_cached != null && !_foundLogged)
            {
                NumpadCardReaderFixPlugin.L.LogInfo("[NPOS] Found active PosTerminal");
                _foundLogged = true;
            }
        }

        if (_cached == null) return;

        // Input mapping
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

        if (kb.numpadEnterKey.wasPressedThisFrame) _cached.Approve();
        if (kb.numpadMinusKey.wasPressedThisFrame) _cached.RemoveCharacter();
    }

    // IMPORTANT: do NOT consult gameplay-state properties here (they can legitimately toggle).
    // We only care whether the object still exists and is in the active hierarchy.
    private static bool IsStillValidObject(PosTerminal t)
    {
        if (t == null) return false;

        GameObject go;
        try
        {
            go = t.gameObject;
        }
        catch
        {
            return false;
        }

        if (go == null) return false;
        if (!go.activeInHierarchy) return false;

        return true;
    }

    private static PosTerminal FindAnyActivePosTerminal()
    {
        var all = UnityEngine.Object.FindObjectsOfType<PosTerminal>(true);
        if (all == null || all.Length == 0) return null;

        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (IsStillValidObject(t))
                return t;
        }

        return null;
    }
}
