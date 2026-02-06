using System;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace PocketBoxPlugin
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class PocketBoxMain : BasePlugin
    {
        public const string PluginGuid = "PocketBoxPlugin";
        public const string PluginName = "PocketBoxPlugin";
        public const string PluginVersion = "0.5.5";

        internal static PocketBoxMain Instance;
        internal new ManualLogSource Log => base.Log;

        internal ConfigEntry<float> RayDistance;

        // NEW: main keybind uses InputSystem keyboard key property name (e.g. "semicolonKey")
        internal ConfigEntry<string> SpawnKeyboardKey;
        internal ConfigEntry<bool> AllowF8Insert;
        internal ConfigEntry<bool> UseRightClickToSpawn;
        internal ConfigEntry<bool> DebugLogs;

        public override void Load()
        {
            Instance = this;

            RayDistance = Config.Bind("Tuning", "RayDistance", 10.0f, "How far the aim raycast should reach.");

            // Semicolon is the default requested behavior.
            // This uses the Unity Input System Keyboard key property name (NOT KeyCode).
            // Examples: semicolonKey, f8Key, insertKey, spaceKey, etc.
            SpawnKeyboardKey = Config.Bind("Keybinds", "SpawnKeyboardKey", "semicolonKey",
                "Keyboard key to spawn a correct-size empty box when aiming at a label (InputSystem Keyboard property name). Default: semicolonKey");

            AllowF8Insert = Config.Bind("Keybinds", "AllowF8Insert", true,
                "If true, F8 and Insert will also trigger spawn (legacy hotkeys).");

            UseRightClickToSpawn = Config.Bind("Keybinds", "UseRightClickToSpawn", false,
                "If true, right-click will ALSO spawn a correct-size empty box when aiming at a label (only if not holding a box). Default: false");

            DebugLogs = Config.Bind("Debug", "DebugLogs", false,
                "If true, prints extra diagnostic logs. Default: false");

            ClassInjector.RegisterTypeInIl2Cpp<SpawnBoxBehaviour>();

            var go = new GameObject("PocketBoxPlugin.Host");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent(Il2CppType.Of<SpawnBoxBehaviour>());

            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Default spawn key: semicolon (;). Config: BepInEx/config/PocketBoxPlugin.cfg");
        }
    }

    public sealed class SpawnBoxBehaviour : MonoBehaviour
    {
        public SpawnBoxBehaviour(IntPtr ptr) : base(ptr) { }

        private PlayerInteraction _interaction;
        private BoxInteraction _boxInteraction;
        private BoxGenerator _boxGen;

        private float _lastHeartbeat;

        // Input System reflection: Keyboard + Mouse
        private bool _inputInitTried;

        private Type _keyboardType;
        private PropertyInfo _keyboardCurrentProp;

        private Type _mouseType;
        private PropertyInfo _mouseCurrentProp;

        // Cache per-key property infos to avoid allocating reflection objects every frame.
        private readonly Dictionary<string, PropertyInfo> _keyboardKeyPropCache = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, PropertyInfo> _mouseBtnPropCache = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        private void Start()
        {
            if (PocketBoxMain.Instance.DebugLogs.Value)
                PocketBoxMain.Instance.Log.LogInfo("[PB] Start() ran.");
        }

        private void Update()
        {
            // Heartbeat only when DebugLogs is on (prevents log spam / perf hit)
            if (PocketBoxMain.Instance.DebugLogs.Value && Time.time - _lastHeartbeat > 5f)
            {
                _lastHeartbeat = Time.time;
                PocketBoxMain.Instance.Log.LogInfo("[PB] Heartbeat: Update running.");
            }

            // Lazy-resolve gameplay references
            if (_interaction == null)
            {
                _interaction = FindObjectOfType<PlayerInteraction>();
                if (_interaction != null)
                {
                    _boxInteraction = _interaction.GetComponent<BoxInteraction>();
                    if (PocketBoxMain.Instance.DebugLogs.Value)
                        PocketBoxMain.Instance.Log.LogInfo("[PB] Found PlayerInteraction / BoxInteraction.");
                }
            }

            if (_boxGen == null)
            {
                try { _boxGen = NoktaSingleton<BoxGenerator>.Instance; } catch { _boxGen = null; }
                if (_boxGen != null && PocketBoxMain.Instance.DebugLogs.Value)
                    PocketBoxMain.Instance.Log.LogInfo("[PB] Found BoxGenerator.");
            }

            if (_interaction == null || _boxGen == null)
                return;

            TryInitInputSystem();

            // If input system isn't present, do nothing (prevents null reflection loops)
            if (_keyboardType == null && _mouseType == null)
                return;

            // === INPUTS ===
            bool spawnByConfiguredKey = false;
            string configuredKey = PocketBoxMain.Instance.SpawnKeyboardKey.Value;

            if (!string.IsNullOrEmpty(configuredKey))
                spawnByConfiguredKey = IsKeyboardKeyPressedThisFrame(configuredKey.Trim());

            bool spawnByLegacy =
                PocketBoxMain.Instance.AllowF8Insert.Value &&
                (IsKeyboardKeyPressedThisFrame("f8Key") || IsKeyboardKeyPressedThisFrame("insertKey"));

            bool spawnByRightClick =
                PocketBoxMain.Instance.UseRightClickToSpawn.Value &&
                IsMouseButtonPressedThisFrame("rightButton");

            if (!spawnByConfiguredKey && !spawnByLegacy && !spawnByRightClick)
                return;

            // 🚫 BLOCK: player already holding a box
            if (IsHoldingBox())
            {
                if (PocketBoxMain.Instance.DebugLogs.Value)
                    PocketBoxMain.Instance.Log.LogInfo("[PB] Spawn blocked: player already holding a box.");
                return;
            }

            if (PocketBoxMain.Instance.DebugLogs.Value)
            {
                if (spawnByRightClick) PocketBoxMain.Instance.Log.LogInfo("[PB] Spawn triggered by Right Click.");
                else if (spawnByConfiguredKey) PocketBoxMain.Instance.Log.LogInfo("[PB] Spawn triggered by " + configuredKey + ".");
                else PocketBoxMain.Instance.Log.LogInfo("[PB] Spawn triggered by F8/Insert.");
            }

            var cam = Camera.main;
            if (cam == null)
            {
                if (PocketBoxMain.Instance.DebugLogs.Value)
                    PocketBoxMain.Instance.Log.LogInfo("[PB] Camera.main is NULL.");
                return;
            }

            var label = GetAimedLabelByScanningAllHits(cam);
            if (label == null)
            {
                if (PocketBoxMain.Instance.DebugLogs.Value)
                    PocketBoxMain.Instance.Log.LogInfo("[PB] No Label found under crosshair.");
                return;
            }

            int productId = ResolveProductId(label);
            if (productId <= 0)
            {
                if (PocketBoxMain.Instance.DebugLogs.Value)
                    PocketBoxMain.Instance.Log.LogInfo("[PB] ProductID resolve failed.");
                return;
            }

            IDManager idm = null;
            try { idm = NoktaSingleton<IDManager>.Instance; } catch { idm = null; }
            if (idm == null) return;

            var product = idm.ProductSO(productId);
            if (product == null)
            {
                if (PocketBoxMain.Instance.DebugLogs.Value)
                    PocketBoxMain.Instance.Log.LogInfo("[PB] ProductSO null for id=" + productId);
                return;
            }

            var size = product.GridLayoutInBox.boxSize;

            var box = SpawnEmptyBox(size);
            if (box == null) return;

            InventoryManager inv = null;
            try { inv = NoktaSingleton<InventoryManager>.Instance; } catch { inv = null; }
            if (inv != null)
            {
                try { inv.AddBox(box.Data); } catch { }
            }

            try
            {
                _interaction.CurrentInteractable = box.TryCast<IInteractable>();
                _interaction.Interact(false, false);
            }
            catch { }

            if (PocketBoxMain.Instance.DebugLogs.Value)
                PocketBoxMain.Instance.Log.LogInfo("[PB] Box spawned and picked up.");
        }

        private bool IsHoldingBox()
        {
            try
            {
                if (_boxInteraction != null && _boxInteraction.m_Box != null)
                    return true;

                var current = _interaction.CurrentInteractable;
                if (current != null && current.TryCast<Box>() != null)
                    return true;
            }
            catch { }

            return false;
        }

        private Label GetAimedLabelByScanningAllHits(Camera cam)
        {
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            float dist = PocketBoxMain.Instance.RayDistance.Value;

            RaycastHit[] hits = null;
            try { hits = Physics.RaycastAll(ray, dist, ~0, QueryTriggerInteraction.Collide); }
            catch { hits = null; }

            if (hits == null || hits.Length == 0)
                return null;

            hits = hits.OrderBy(h => h.distance).ToArray();

            foreach (var hit in hits)
            {
                var go = hit.collider != null ? hit.collider.gameObject : null;
                if (go == null) continue;

                Label label = null;
                try
                {
                    label = go.GetComponent<Label>()
                         ?? go.GetComponentInParent<Label>()
                         ?? go.GetComponentInChildren<Label>();
                }
                catch { label = null; }

                if (label != null)
                    return label;
            }

            return null;
        }

        private int ResolveProductId(Label label)
        {
            try
            {
                if (label.DisplaySlot != null)
                    return label.DisplaySlot.ProductID;

                if (label.RackSlot != null && label.RackSlot.m_Data != null)
                    return label.RackSlot.m_Data.ProductID;
            }
            catch { }

            return -1;
        }

        private Box SpawnEmptyBox(BoxSize size)
        {
            try
            {
                var data = new BoxData();
                data.Size = size;
                return _boxGen.SpawnBox(Vector3.zero, Quaternion.identity, data);
            }
            catch
            {
                return null;
            }
        }

        // -------------------------
        // New Input System (reflect)
        // -------------------------

        private void TryInitInputSystem()
        {
            if (_inputInitTried)
                return;

            _inputInitTried = true;

            _keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
            _keyboardCurrentProp = _keyboardType?.GetProperty("current", BindingFlags.Static | BindingFlags.Public);

            _mouseType = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
            _mouseCurrentProp = _mouseType?.GetProperty("current", BindingFlags.Static | BindingFlags.Public);

            if (PocketBoxMain.Instance.DebugLogs.Value)
            {
                bool ok = (_keyboardType != null && _keyboardCurrentProp != null) ||
                          (_mouseType != null && _mouseCurrentProp != null);

                PocketBoxMain.Instance.Log.LogInfo(ok
                    ? "[PB] Unity Input System detected."
                    : "[PB] Unity Input System NOT found.");
            }
        }

        private bool IsKeyboardKeyPressedThisFrame(string keyPropertyName)
        {
            if (_keyboardType == null || _keyboardCurrentProp == null)
                return false;

            try
            {
                object keyboard = _keyboardCurrentProp.GetValue(null);
                if (keyboard == null) return false;

                if (!_keyboardKeyPropCache.TryGetValue(keyPropertyName, out var keyProp) || keyProp == null)
                {
                    keyProp = _keyboardType.GetProperty(keyPropertyName, BindingFlags.Instance | BindingFlags.Public);
                    _keyboardKeyPropCache[keyPropertyName] = keyProp;
                }

                object key = keyProp?.GetValue(keyboard);
                if (key == null) return false;

                var pressedProp = key.GetType().GetProperty("wasPressedThisFrame", BindingFlags.Instance | BindingFlags.Public);
                return pressedProp != null && (bool)pressedProp.GetValue(key);
            }
            catch { return false; }
        }

        private bool IsMouseButtonPressedThisFrame(string buttonPropertyName)
        {
            if (_mouseType == null || _mouseCurrentProp == null)
                return false;

            try
            {
                object mouse = _mouseCurrentProp.GetValue(null);
                if (mouse == null) return false;

                if (!_mouseBtnPropCache.TryGetValue(buttonPropertyName, out var btnProp) || btnProp == null)
                {
                    btnProp = _mouseType.GetProperty(buttonPropertyName, BindingFlags.Instance | BindingFlags.Public);
                    _mouseBtnPropCache[buttonPropertyName] = btnProp;
                }

                object btn = btnProp?.GetValue(mouse);
                if (btn == null) return false;

                var pressedProp = btn.GetType().GetProperty("wasPressedThisFrame", BindingFlags.Instance | BindingFlags.Public);
                return pressedProp != null && (bool)pressedProp.GetValue(btn);
            }
            catch { return false; }
        }
    }
}
