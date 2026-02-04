using System;
using System.Reflection;
using System.Linq;
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
        public const string PluginVersion = "0.5.4";

        internal static PocketBoxMain Instance;
        internal new ManualLogSource Log => base.Log;

        internal ConfigEntry<float> RayDistance;
        internal ConfigEntry<bool> UseRightClickToSpawn;

        public override void Load()
        {
            Instance = this;

            RayDistance = Config.Bind("Tuning", "RayDistance", 10.0f, "How far the aim raycast should reach.");
            UseRightClickToSpawn = Config.Bind("Keybinds", "UseRightClickToSpawn", true,
                "If true, right-click will spawn a correct-size empty box when aiming at a label (only if not holding a box).");

            ClassInjector.RegisterTypeInIl2Cpp<SpawnBoxBehaviour>();

            var go = new GameObject("PocketBoxPlugin.Host");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent(Il2CppType.Of<SpawnBoxBehaviour>());

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
    }

    public sealed class SpawnBoxBehaviour : MonoBehaviour
    {
        public SpawnBoxBehaviour(global::System.IntPtr ptr) : base(ptr) { }

        private PlayerInteraction _interaction;
        private BoxInteraction _boxInteraction;
        private BoxGenerator _boxGen;

        private float _lastHeartbeat;

        // Input System reflection: Keyboard + Mouse
        private bool _inputInitTried;
        private bool _inputOk;

        private Type _keyboardType;
        private PropertyInfo _keyboardCurrentProp;

        private Type _mouseType;
        private PropertyInfo _mouseCurrentProp;

        private void Start()
        {
            PocketBoxMain.Instance.Log.LogInfo("[PB] Start() ran.");
        }

        private void Update()
        {
            if (Time.time - _lastHeartbeat > 3f)
            {
                _lastHeartbeat = Time.time;
                PocketBoxMain.Instance.Log.LogInfo("[PB] Heartbeat: Update running.");
            }

            if (_interaction == null)
            {
                _interaction = FindObjectOfType<PlayerInteraction>();
                if (_interaction != null)
                {
                    _boxInteraction = _interaction.GetComponent<BoxInteraction>();
                    PocketBoxMain.Instance.Log.LogInfo("[PB] Found PlayerInteraction / BoxInteraction.");
                }
            }

            if (_boxGen == null)
            {
                _boxGen = NoktaSingleton<BoxGenerator>.Instance;
                if (_boxGen != null)
                    PocketBoxMain.Instance.Log.LogInfo("[PB] Found BoxGenerator.");
            }

            if (_interaction == null || _boxGen == null)
                return;

            TryInitInputSystem();

            bool keySpawnPressed =
                IsKeyboardKeyPressedThisFrame("f8Key") ||
                IsKeyboardKeyPressedThisFrame("insertKey");

            bool rightClickPressed =
                PocketBoxMain.Instance.UseRightClickToSpawn.Value &&
                IsMouseButtonPressedThisFrame("rightButton");

            if (!keySpawnPressed && !rightClickPressed)
                return;

            // 🚫 BLOCK: player already holding a box
            if (IsHoldingBox())
            {
                PocketBoxMain.Instance.Log.LogInfo("[PB] Spawn blocked: player already holding a box.");
                return;
            }

            PocketBoxMain.Instance.Log.LogInfo(rightClickPressed
                ? "[PB] Spawn triggered by Right Click."
                : "[PB] Spawn triggered by F8/Insert.");

            var cam = Camera.main;
            if (cam == null)
            {
                PocketBoxMain.Instance.Log.LogInfo("[PB] Camera.main is NULL.");
                return;
            }

            var label = GetAimedLabelByScanningAllHits(cam);
            if (label == null)
            {
                PocketBoxMain.Instance.Log.LogInfo("[PB] No Label found under crosshair.");
                return;
            }

            int productId = ResolveProductId(label);
            if (productId <= 0)
            {
                PocketBoxMain.Instance.Log.LogInfo("[PB] ProductID resolve failed.");
                return;
            }

            var product = NoktaSingleton<IDManager>.Instance.ProductSO(productId);
            if (product == null)
            {
                PocketBoxMain.Instance.Log.LogInfo("[PB] ProductSO null for id=" + productId);
                return;
            }

            var size = product.GridLayoutInBox.boxSize;
            PocketBoxMain.Instance.Log.LogInfo("[PB] Spawning box size: " + size);

            var box = SpawnEmptyBox(size);

            NoktaSingleton<InventoryManager>.Instance.AddBox(box.Data);
            _interaction.CurrentInteractable = box.TryCast<IInteractable>();
            _interaction.Interact(false, false);

            PocketBoxMain.Instance.Log.LogInfo("[PB] Box spawned and picked up.");
        }

        private bool IsHoldingBox()
        {
            if (_boxInteraction != null && _boxInteraction.m_Box != null)
                return true;

            var current = _interaction.CurrentInteractable;
            if (current != null && current.TryCast<Box>() != null)
                return true;

            return false;
        }

        private Label GetAimedLabelByScanningAllHits(Camera cam)
        {
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            float dist = PocketBoxMain.Instance.RayDistance.Value;

            var hits = Physics.RaycastAll(ray, dist, ~0, QueryTriggerInteraction.Collide);
            if (hits == null || hits.Length == 0)
                return null;

            hits = hits.OrderBy(h => h.distance).ToArray();

            foreach (var hit in hits)
            {
                var go = hit.collider?.gameObject;
                if (go == null) continue;

                var label = go.GetComponent<Label>()
                         ?? go.GetComponentInParent<Label>()
                         ?? go.GetComponentInChildren<Label>();

                if (label != null)
                    return label;
            }

            return null;
        }

        private int ResolveProductId(Label label)
        {
            if (label.DisplaySlot != null)
                return label.DisplaySlot.ProductID;

            if (label.RackSlot != null && label.RackSlot.m_Data != null)
                return label.RackSlot.m_Data.ProductID;

            return -1;
        }

        private Box SpawnEmptyBox(BoxSize size)
        {
            var data = new BoxData();
            data.Size = size;
            return _boxGen.SpawnBox(Vector3.zero, Quaternion.identity, data);
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

            _inputOk = (_keyboardType != null && _keyboardCurrentProp != null) ||
                       (_mouseType != null && _mouseCurrentProp != null);

            PocketBoxMain.Instance.Log.LogInfo(_inputOk
                ? "[PB] Unity Input System detected."
                : "[PB] Unity Input System NOT found.");
        }

        private bool IsKeyboardKeyPressedThisFrame(string keyPropertyName)
        {
            if (_keyboardType == null || _keyboardCurrentProp == null)
                return false;

            try
            {
                object keyboard = _keyboardCurrentProp.GetValue(null);
                if (keyboard == null) return false;

                var keyProp = _keyboardType.GetProperty(keyPropertyName, BindingFlags.Instance | BindingFlags.Public);
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

                var btnProp = _mouseType.GetProperty(buttonPropertyName, BindingFlags.Instance | BindingFlags.Public);
                object btn = btnProp?.GetValue(mouse);
                if (btn == null) return false;

                var pressedProp = btn.GetType().GetProperty("wasPressedThisFrame", BindingFlags.Instance | BindingFlags.Public);
                return pressedProp != null && (bool)pressedProp.GetValue(btn);
            }
            catch { return false; }
        }
    }
}
