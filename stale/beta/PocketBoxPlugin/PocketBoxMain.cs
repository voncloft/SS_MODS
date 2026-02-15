using System;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
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
        public const string PluginVersion = "0.5.15";

        internal static PocketBoxMain Instance;
        internal new ManualLogSource Log => base.Log;

        internal ConfigEntry<float> RayDistance;

        // NEW: main keybind uses InputSystem keyboard key property name (e.g. "semicolonKey")
        internal ConfigEntry<string> SpawnKeyboardKey;
        internal ConfigEntry<bool> AllowF8Insert;
        internal ConfigEntry<bool> UseRightClickToSpawn;
        internal ConfigEntry<bool> DebugLogs;
        internal ConfigEntry<float> SpawnCooldownSeconds;
        private Harmony _harmony;

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
            SpawnCooldownSeconds = Config.Bind("Tuning", "SpawnCooldownSeconds", 0.8f,
                "Minimum seconds between spawns to prevent repeat-trigger spam. Default: 0.8");

            ClassInjector.RegisterTypeInIl2Cpp<SpawnBoxBehaviour>();

            var host = GameObject.Find("PocketBoxPlugin.Host");
            if (host == null)
            {
                host = new GameObject("PocketBoxPlugin.Host");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.hideFlags = HideFlags.HideAndDontSave;
            }
            else
            {
                host.hideFlags = HideFlags.HideAndDontSave;
            }

            if (host.GetComponent(Il2CppType.Of<SpawnBoxBehaviour>()) == null)
                host.AddComponent(Il2CppType.Of<SpawnBoxBehaviour>());

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(PocketBoxPatches));

            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Default spawn key: semicolon (;). Config: BepInEx/config/PocketBoxPlugin.cfg");
            Log.LogInfo("[PB] Build marker: 0.5.15 single-host guard; removed throw/spawn Harmony overrides.");
        }

        private static class PocketBoxPatches
        {
            [HarmonyPatch(typeof(Label), "ClearLabel")]
            [HarmonyPrefix]
            private static bool Label_ClearLabel_Prefix(Label __instance)
            {
                return !HasProducts(__instance);
            }

            [HarmonyPatch(typeof(Label), "ClearLabelNetwork")]
            [HarmonyPrefix]
            private static bool Label_ClearLabelNetwork_Prefix(Label __instance)
            {
                return !HasProducts(__instance);
            }

            [HarmonyPatch(typeof(RackSlot), "ClearLabel")]
            [HarmonyPrefix]
            private static bool RackSlot_ClearLabel_Prefix(RackSlot __instance)
            {
                return !HasProducts(__instance);
            }

            [HarmonyPatch(typeof(RackSlot), "ClearLabelNetwork")]
            [HarmonyPrefix]
            private static bool RackSlot_ClearLabelNetwork_Prefix(RackSlot __instance)
            {
                return !HasProducts(__instance);
            }

            private static bool HasProducts(Label label)
            {
                if (label == null) return false;
                try
                {
                    var ds = label.DisplaySlot;
                    if (ds != null)
                    {
                        try { if (ds.ProductCount > 0) return true; } catch { }
                        try { if (ds.HasProduct) return true; } catch { }
                    }

                    var rs = label.RackSlot;
                    if (rs != null)
                    {
                        try { if (rs.ProductCount > 0) return true; } catch { }
                        try { if (rs.HasProduct) return true; } catch { }
                    }
                }
                catch { }
                return false;
            }

            private static bool HasProducts(RackSlot rs)
            {
                if (rs == null) return false;
                try
                {
                    try { if (rs.ProductCount > 0) return true; } catch { }
                    try { if (rs.HasProduct) return true; } catch { }
                    var data = rs.Data;
                    if (data != null)
                    {
                        try { if (data.TotalProductCount > 0) return true; } catch { }
                    }
                }
                catch { }
                return false;
            }
        }
    }

    public sealed class SpawnBoxBehaviour : MonoBehaviour
    {
        public SpawnBoxBehaviour(IntPtr ptr) : base(ptr) { }

        private PlayerInteraction _interaction;
        private BoxInteraction _boxInteraction;
        private BoxGenerator _boxGen;

        private float _lastHeartbeat;
        private float _nextSpawnAt;

        // Input System reflection: Keyboard + Mouse
        private bool _inputInitTried;

        private Type _keyboardType;
        private PropertyInfo _keyboardCurrentProp;
        private Type _keyEnumType;
        private PropertyInfo _keyboardIndexerProp;

        private Type _mouseType;
        private PropertyInfo _mouseCurrentProp;
        private float _nextInputRetryAt;

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

            // === INPUTS ===
            string configuredKey = PocketBoxMain.Instance.SpawnKeyboardKey.Value;
            string normalizedKey = null;
            bool inputSystemConfiguredPressed = false;
            bool legacySemicolonPressed = false;
            bool legacyColonPressed = false;

            if (!string.IsNullOrEmpty(configuredKey))
            {
                normalizedKey = NormalizeKeyboardKeyName(configuredKey);
                inputSystemConfiguredPressed = IsKeyboardKeyPressedThisFrame(normalizedKey);
            }

            // Legacy fallback for layouts / InputSystem edge cases.
            if (!inputSystemConfiguredPressed)
            {
                try
                {
                    legacySemicolonPressed = Input.GetKeyDown(KeyCode.Semicolon);
                    legacyColonPressed = Input.GetKeyDown(KeyCode.Colon);
                }
                catch { }
            }
            bool spawnByConfiguredKey = inputSystemConfiguredPressed || legacySemicolonPressed || legacyColonPressed;

            bool spawnByLegacy =
                PocketBoxMain.Instance.AllowF8Insert.Value &&
                (IsKeyboardKeyPressedThisFrame("f8Key") || IsKeyboardKeyPressedThisFrame("insertKey"));

            bool spawnByRightClick =
                PocketBoxMain.Instance.UseRightClickToSpawn.Value &&
                IsMouseButtonPressedThisFrame("rightButton");

            if (PocketBoxMain.Instance.DebugLogs.Value)
            {
                bool anyKeyDown = false;
                try { anyKeyDown = Input.anyKeyDown; } catch { }

                if (spawnByConfiguredKey || spawnByLegacy || spawnByRightClick || anyKeyDown)
                {
                    PocketBoxMain.Instance.Log.LogInfo("[PB] InputCheck"
                        + " anyKeyDown=" + anyKeyDown
                        + " configuredRaw=" + (configuredKey ?? "(null)")
                        + " configuredNorm=" + (normalizedKey ?? "(null)")
                        + " inputSystemConfiguredPressed=" + inputSystemConfiguredPressed
                        + " legacySemicolonPressed=" + legacySemicolonPressed
                        + " legacyColonPressed=" + legacyColonPressed
                        + " spawnByLegacy(F8/Insert)=" + spawnByLegacy
                        + " spawnByRightClick=" + spawnByRightClick);
                }
            }

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

            Label label = GetAimedLabelByScanningAllHits(cam);
            if (label == null)
            {
                if (PocketBoxMain.Instance.DebugLogs.Value)
                    PocketBoxMain.Instance.Log.LogInfo("[PB] No Label found under crosshair.");
                return;
            }
            else if (PocketBoxMain.Instance.DebugLogs.Value)
            {
                PocketBoxMain.Instance.Log.LogInfo("[PB] Label acquired via Raycast.");
            }

            int productId = ResolveProductId(label);
            if (productId <= 0)
                productId = ResolveProductIdFromRaycast(cam);
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
            float cooldown = 0.8f;
            try { cooldown = PocketBoxMain.Instance.SpawnCooldownSeconds.Value; } catch { }
            if (cooldown < 0.05f) cooldown = 0.05f;

            if (Time.unscaledTime < _nextSpawnAt)
                return;
            _nextSpawnAt = Time.unscaledTime + cooldown;

            Vector3 spawnPos = cam.transform.position + (cam.transform.forward * 0.9f) + Vector3.up * 0.30f;
            try
            {
                if (_interaction != null)
                {
                    var pt = ((Component)_interaction).transform;
                    if (pt != null)
                        spawnPos = pt.position + (pt.forward * 0.9f) + Vector3.up * 1.05f;
                }
            }
            catch { }
            Quaternion spawnRot = Quaternion.identity;

            var box = SpawnEmptyBox(size, productId, spawnPos, spawnRot);
            if (box == null) return;

            if (PocketBoxMain.Instance.DebugLogs.Value)
                PocketBoxMain.Instance.Log.LogInfo("[PB] Box spawned near player. pid=" + productId + " size=" + size + " pos=" + spawnPos);
        }

        private bool IsHoldingBox()
        {
            try
            {
                if (_boxInteraction != null && _boxInteraction.m_Box != null)
                {
                    var box = _boxInteraction.m_Box;
                    if (!IsAliveBox(box))
                    {
                        try { _boxInteraction.m_Box = null; } catch { }
                        return false;
                    }

                    try
                    {
                        var c = (Component)box;
                        if (c == null || c.gameObject == null || !c.gameObject.activeInHierarchy)
                        {
                            try { _boxInteraction.m_Box = null; } catch { }
                            return false;
                        }

                        // Stale hold guard: if the tracked "held" box is far from player camera,
                        // assume stale reference and clear so controls (including L) are not locked.
                        var cam = Camera.main;
                        if (cam != null)
                        {
                            float d = Vector3.Distance(cam.transform.position, c.transform.position);
                            if (d > 4.0f)
                            {
                                if (PocketBoxMain.Instance.DebugLogs.Value)
                                    PocketBoxMain.Instance.Log.LogInfo("[PB] Cleared stale held box ref (distance=" + d + ").");
                                try { _boxInteraction.m_Box = null; } catch { }
                                return false;
                            }
                        }
                    }
                    catch { }

                    return true;
                }
            }
            catch { }

            return false;
        }

        private bool IsAliveBox(Box box)
        {
            try
            {
                if (box == null) return false;
                var c = (Component)box;
                if (c == null) return false;
                return c.gameObject != null;
            }
            catch { return false; }
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
                {
                    var ds = label.DisplaySlot;
                    try
                    {
                        int pid = ds.ProductID;
                        if (pid > 0)
                        {
                            if (PocketBoxMain.Instance.DebugLogs.Value) PocketBoxMain.Instance.Log.LogInfo("[PB] ProductID from DisplaySlot.ProductID=" + pid);
                            return pid;
                        }
                    }
                    catch { }

                    try
                    {
                        var data = ds.Data;
                        if (data != null)
                        {
                            int pid = data.FirstItemID;
                            if (pid > 0)
                            {
                                if (PocketBoxMain.Instance.DebugLogs.Value) PocketBoxMain.Instance.Log.LogInfo("[PB] ProductID from DisplaySlot.Data.FirstItemID=" + pid);
                                return pid;
                            }

                            var products = (Dictionary<int, int>)(object)data.Products;
                            if (products != null)
                            {
                                foreach (var kv in products)
                                {
                                    if (kv.Key > 0)
                                    {
                                        if (PocketBoxMain.Instance.DebugLogs.Value) PocketBoxMain.Instance.Log.LogInfo("[PB] ProductID from DisplaySlot.Data.Products key=" + kv.Key + " count=" + kv.Value);
                                        return kv.Key;
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    try
                    {
                        if (ds.m_Products != null && ds.m_Products.Count > 0)
                        {
                            var p = ds.m_Products[0];
                            if (p != null && p.ProductSO != null && p.ProductSO.ID > 0)
                            {
                                int pid = p.ProductSO.ID;
                                if (PocketBoxMain.Instance.DebugLogs.Value) PocketBoxMain.Instance.Log.LogInfo("[PB] ProductID from DisplaySlot.m_Products[0].ProductSO.ID=" + pid);
                                return pid;
                            }
                        }
                    }
                    catch { }
                }

                if (label.RackSlot != null)
                {
                    var rs = label.RackSlot;
                    try
                    {
                        var data = rs.Data;
                        if (data != null && data.ProductID > 0)
                        {
                            if (PocketBoxMain.Instance.DebugLogs.Value) PocketBoxMain.Instance.Log.LogInfo("[PB] ProductID from RackSlot.Data.ProductID=" + data.ProductID);
                            return data.ProductID;
                        }
                    }
                    catch { }

                    try
                    {
                        if (rs.m_Boxes != null && rs.m_Boxes.Count > 0)
                        {
                            var box = rs.m_Boxes[rs.m_Boxes.Count - 1];
                            if (box != null && box.m_Data != null && box.m_Data.ProductID > 0)
                            {
                                if (PocketBoxMain.Instance.DebugLogs.Value) PocketBoxMain.Instance.Log.LogInfo("[PB] ProductID from RackSlot.BoxData.ProductID=" + box.m_Data.ProductID);
                                return box.m_Data.ProductID;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (PocketBoxMain.Instance.DebugLogs.Value)
            {
                try
                {
                    bool hasDisplay = label != null && label.DisplaySlot != null;
                    bool hasRack = label != null && label.RackSlot != null;
                    int displayPid = -1;
                    int displayFirstItemId = -1;
                    int rackPid = -1;
                    int rackProductCount = -1;

                    if (hasDisplay)
                    {
                        try { displayPid = label.DisplaySlot.ProductID; } catch { }
                        try { if (label.DisplaySlot.Data != null) displayFirstItemId = label.DisplaySlot.Data.FirstItemID; } catch { }
                    }

                    if (hasRack)
                    {
                        try { if (label.RackSlot.Data != null) rackPid = label.RackSlot.Data.ProductID; } catch { }
                        try { rackProductCount = label.RackSlot.ProductCount; } catch { }
                    }

                    PocketBoxMain.Instance.Log.LogInfo("[PB] ResolveProductId failed"
                        + " hasDisplay=" + hasDisplay
                        + " display.ProductID=" + displayPid
                        + " display.FirstItemID=" + displayFirstItemId
                        + " hasRack=" + hasRack
                        + " rack.Data.ProductID=" + rackPid
                        + " rack.ProductCount=" + rackProductCount);
                }
                catch { }
            }
            return -1;
        }

        private int ResolveProductIdFromRaycast(Camera cam)
        {
            if (cam == null) return -1;
            try
            {
                var ray = new Ray(cam.transform.position, cam.transform.forward);
                float dist = PocketBoxMain.Instance.RayDistance.Value;
                RaycastHit[] hits = null;
                try { hits = Physics.RaycastAll(ray, dist, ~0, QueryTriggerInteraction.Collide); } catch { hits = null; }
                if (hits == null || hits.Length == 0) return -1;

                hits = hits.OrderBy(h => h.distance).ToArray();
                foreach (var hit in hits)
                {
                    var go = hit.collider != null ? hit.collider.gameObject : null;
                    if (go == null) continue;

                    try
                    {
                        var product = go.GetComponent<Product>() ?? go.GetComponentInParent<Product>() ?? go.GetComponentInChildren<Product>();
                        if (product != null && product.ProductSO != null && product.ProductSO.ID > 0)
                        {
                            int pid = product.ProductSO.ID;
                            if (PocketBoxMain.Instance.DebugLogs.Value) PocketBoxMain.Instance.Log.LogInfo("[PB] ProductID from Raycast Product.ProductSO.ID=" + pid);
                            return pid;
                        }
                    }
                    catch { }

                    try
                    {
                        var ds = go.GetComponent<DisplaySlot>() ?? go.GetComponentInParent<DisplaySlot>() ?? go.GetComponentInChildren<DisplaySlot>();
                        if (ds != null)
                        {
                            int pid = -1;
                            try { pid = ds.ProductID; } catch { }
                            if (pid > 0)
                            {
                                if (PocketBoxMain.Instance.DebugLogs.Value) PocketBoxMain.Instance.Log.LogInfo("[PB] ProductID from Raycast DisplaySlot.ProductID=" + pid);
                                return pid;
                            }
                            try
                            {
                                if (ds.Data != null && ds.Data.FirstItemID > 0)
                                {
                                    pid = ds.Data.FirstItemID;
                                    if (PocketBoxMain.Instance.DebugLogs.Value) PocketBoxMain.Instance.Log.LogInfo("[PB] ProductID from Raycast DisplaySlot.Data.FirstItemID=" + pid);
                                    return pid;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    try
                    {
                        var rs = go.GetComponent<RackSlot>() ?? go.GetComponentInParent<RackSlot>() ?? go.GetComponentInChildren<RackSlot>();
                        if (rs != null && rs.Data != null && rs.Data.ProductID > 0)
                        {
                            int pid = rs.Data.ProductID;
                            if (PocketBoxMain.Instance.DebugLogs.Value) PocketBoxMain.Instance.Log.LogInfo("[PB] ProductID from Raycast RackSlot.Data.ProductID=" + pid);
                            return pid;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return -1;
        }

        private Box SpawnEmptyBox(BoxSize size, int productId, Vector3 position, Quaternion rotation)
        {
            try
            {
                var data = new BoxData();
                data.Size = size;
                data.ProductID = productId;
                data.ProductCount = 0;
                data.IsOpen = true;
                return _boxGen.SpawnBox(position, rotation, data);
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
            if (_inputInitTried && Time.unscaledTime < _nextInputRetryAt &&
                (_keyboardType != null || _mouseType != null))
                return;

            _inputInitTried = true;
            _nextInputRetryAt = Time.unscaledTime + 2f;

            _keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
            _keyboardCurrentProp = _keyboardType?.GetProperty("current", BindingFlags.Static | BindingFlags.Public);
            _keyEnumType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
            _keyboardIndexerProp = _keyboardType?.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                ?.FirstOrDefault(p =>
                {
                    try
                    {
                        var idx = p.GetIndexParameters();
                        return idx != null && idx.Length == 1 && _keyEnumType != null && idx[0].ParameterType == _keyEnumType;
                    }
                    catch { return false; }
                });

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
                    if (PocketBoxMain.Instance.DebugLogs.Value && keyProp == null)
                        PocketBoxMain.Instance.Log.LogInfo("[PB] InputSystem key property missing: " + keyPropertyName);
                }

                object key = keyProp?.GetValue(keyboard);
                if (key == null && string.Equals(keyPropertyName, "semicolonKey", StringComparison.OrdinalIgnoreCase))
                {
                    key = GetKeyboardControlByEnumIndex(keyboard, "Semicolon");
                    if (PocketBoxMain.Instance.DebugLogs.Value && key == null)
                        PocketBoxMain.Instance.Log.LogInfo("[PB] InputSystem semicolon enum fallback failed.");
                }
                if (key == null) return false;

                bool p1 = ReadControlBool(key, "wasPressedThisFrame");
                bool p2 = ReadControlBool(key, "wasPressedThisFrameInternal");
                bool pressed = p1 || p2;
                if (pressed && PocketBoxMain.Instance.DebugLogs.Value)
                    PocketBoxMain.Instance.Log.LogInfo("[PB] InputSystem key pressed: " + keyPropertyName + " [wasPressedThisFrame=" + p1 + ", internal=" + p2 + "]");
                return pressed;
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

                if (ReadControlBool(btn, "wasPressedThisFrame")) return true;
                if (ReadControlBool(btn, "wasPressedThisFrameInternal")) return true;
                return false;
            }
            catch { return false; }
        }

        private object GetKeyboardControlByEnumIndex(object keyboard, string keyEnumName)
        {
            if (keyboard == null || _keyEnumType == null || _keyboardIndexerProp == null) return null;
            try
            {
                object keyEnum = Enum.Parse(_keyEnumType, keyEnumName, true);
                if (keyEnum == null) return null;
                return _keyboardIndexerProp.GetValue(keyboard, new[] { keyEnum });
            }
            catch { return null; }
        }

        private bool ReadControlBool(object control, string propertyName)
        {
            if (control == null || string.IsNullOrEmpty(propertyName)) return false;
            try
            {
                var prop = control.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (prop == null) return false;
                object value = prop.GetValue(control);
                if (value == null) return false;

                if (value is bool b) return b;
                if (value is Il2CppSystem.Boolean ib) return ib.ToString().Equals("True", StringComparison.OrdinalIgnoreCase);
                return Convert.ToBoolean(value);
            }
            catch { return false; }
        }

        private string NormalizeKeyboardKeyName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "semicolonKey";

            string s = raw.Trim();
            if (string.Equals(s, ";", StringComparison.Ordinal)) return "semicolonKey";
            if (string.Equals(s, "semicolon", StringComparison.OrdinalIgnoreCase)) return "semicolonKey";
            if (string.Equals(s, "semicolonkey", StringComparison.OrdinalIgnoreCase)) return "semicolonKey";

            if (s.EndsWith("Key", StringComparison.OrdinalIgnoreCase))
                return char.ToLowerInvariant(s[0]) + s.Substring(1, s.Length - 1);

            return char.ToLowerInvariant(s[0]) + s.Substring(1) + "Key";
        }
    }
}
