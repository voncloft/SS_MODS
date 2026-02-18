using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.Rendering;

namespace NightShift;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "von.supermarketsim.brighterlights";
    public const string PluginName = "BrighterLights";
    public const string PluginVersion = "1.4.2";

    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<float> IntensityMultiplier = null!;
    internal static ConfigEntry<float> RangeMultiplier = null!;
    internal static ConfigEntry<float> SpotAngleMultiplier = null!;
    internal static ConfigEntry<int> ForcePixelLightCount = null!;
    internal static ConfigEntry<int> MaxQualityIndexToBoost = null!;
    internal static ConfigEntry<float> UpdateIntervalSeconds = null!;
    internal static ConfigEntry<bool> AutoScheduleEnabled = null!;
    internal static ConfigEntry<int> AutoLightsOnHour = null!;
    internal static ConfigEntry<int> AutoLightsOffHour = null!;
    internal static ConfigEntry<bool> AutoAffectStoreLights = null!;
    internal static ConfigEntry<bool> AutoAffectStorageLights = null!;
    internal static ConfigEntry<bool> ForceInteriorLightObjectsOnLow = null!;
    internal static ConfigEntry<bool> SpawnFloorFillLightsOnLow = null!;
    internal static ConfigEntry<float> FloorFillVerticalOffset = null!;
    internal static ConfigEntry<float> FloorFillRange = null!;
    internal static ConfigEntry<float> FloorFillIntensity = null!;
    internal static ConfigEntry<bool> FloorFillAffectAllLayers = null!;
    internal static ConfigEntry<bool> DisableAllShadowsOnLow = null!;
    internal static ConfigEntry<bool> SoftenShadowsOnLow = null!;
    internal static ConfigEntry<float> MaxShadowStrengthOnLow = null!;
    internal static ConfigEntry<bool> AmbientLiftOnLow = null!;
    internal static ConfigEntry<float> AmbientIntensityMinOnLow = null!;
    internal static ConfigEntry<float> AmbientGrayMinOnLow = null!;
    internal static ConfigEntry<bool> CameraFillLightOnLow = null!;
    internal static ConfigEntry<float> CameraFillLightIntensity = null!;
    internal static ConfigEntry<float> CameraFillLightRange = null!;
    internal static ConfigEntry<bool> GlobalDirectionalFillOnLow = null!;
    internal static ConfigEntry<float> GlobalDirectionalFillIntensity = null!;
    internal static ConfigEntry<float> GlobalDirectionalBackFillIntensity = null!;
    internal static ConfigEntry<bool> DisableEngineShadowsOnLow = null!;
    internal static ConfigEntry<bool> DisableOcclusionCullingOnLow = null!;
    internal static ConfigEntry<bool> ForceDynamicRendererLightingOnLow = null!;
    internal static ConfigEntry<bool> DebugLogging = null!;
    internal static ManualLogSource LogSource = null!;

    private Harmony _harmony = null!;

    public override void Load()
    {
        Enabled = Config.Bind("General", "Enabled", true, "Enable this mod.");
        IntensityMultiplier = Config.Bind("General", "IntensityMultiplier", 2.25f, "Light intensity multiplier used only on low graphics.");
        RangeMultiplier = Config.Bind("General", "RangeMultiplier", 2.0f, "Light range multiplier used only on low graphics.");
        SpotAngleMultiplier = Config.Bind("General", "SpotAngleMultiplier", 1.15f, "Spot light angle multiplier used only on low graphics.");
        ForcePixelLightCount = Config.Bind("General", "ForcePixelLightCount", 4, "Minimum Unity pixel light count on low graphics. Higher values improve floor lighting but may reduce FPS.");
        MaxQualityIndexToBoost = Config.Bind("General", "MaxQualityIndexToBoost", 0, "Apply boost when current quality index is <= this value.");
        UpdateIntervalSeconds = Config.Bind("General", "UpdateIntervalSeconds", 0.75f, "How often (seconds) to refresh light values.");

        AutoScheduleEnabled = Config.Bind("AutoLights", "Enabled", true, "Automatically toggle store/storage lights by in-game hour.");
        AutoLightsOnHour = Config.Bind("AutoLights", "OnHour", 18, "In-game hour (0-23) to turn lights on.");
        AutoLightsOffHour = Config.Bind("AutoLights", "OffHour", 7, "In-game hour (0-23) to turn lights off.");
        AutoAffectStoreLights = Config.Bind("AutoLights", "AffectStoreLights", true, "Apply auto schedule to store lights.");
        AutoAffectStorageLights = Config.Bind("AutoLights", "AffectStorageLights", true, "Apply auto schedule to storage lights.");

        ForceInteriorLightObjectsOnLow = Config.Bind("LowQualityFix", "ForceInteriorLightObjectsOnLow", true, "Force interior spotlight GameObjects active when lights should be on (low quality only).");
        SpawnFloorFillLightsOnLow = Config.Bind("LowQualityFix", "SpawnFloorFillLightsOnLow", true, "Spawn helper point lights below ceiling lamps on low quality so light reaches the floor.");
        FloorFillVerticalOffset = Config.Bind("LowQualityFix", "FloorFillVerticalOffset", 1.9f, "How far below each interior lamp to place helper light.");
        FloorFillRange = Config.Bind("LowQualityFix", "FloorFillRange", 7.5f, "Range of helper floor light.");
        FloorFillIntensity = Config.Bind("LowQualityFix", "FloorFillIntensity", 1.7f, "Intensity of helper floor light.");
        FloorFillAffectAllLayers = Config.Bind("LowQualityFix", "FloorFillAffectAllLayers", true, "If true, helper floor lights affect all layers (helps prevent black products/shelves).");
        DisableAllShadowsOnLow = Config.Bind("LowQualityFix", "DisableAllShadowsOnLow", true, "Disable dynamic shadows on boosted lights in low quality.");
        SoftenShadowsOnLow = Config.Bind("LowQualityFix", "SoftenShadowsOnLow", true, "Reduce shadow darkness on boosted lights in low quality.");
        MaxShadowStrengthOnLow = Config.Bind("LowQualityFix", "MaxShadowStrengthOnLow", 0.55f, "Maximum shadow strength allowed on boosted lights in low quality (0-1).");
        AmbientLiftOnLow = Config.Bind("LowQualityFix", "AmbientLiftOnLow", true, "Lift ambient lighting floor on low quality to avoid pitch-black shelves.");
        AmbientIntensityMinOnLow = Config.Bind("LowQualityFix", "AmbientIntensityMinOnLow", 1.25f, "Minimum RenderSettings.ambientIntensity on low quality.");
        AmbientGrayMinOnLow = Config.Bind("LowQualityFix", "AmbientGrayMinOnLow", 0.22f, "Minimum ambient gray channel floor (0-1) on low quality.");
        CameraFillLightOnLow = Config.Bind("LowQualityFix", "CameraFillLightOnLow", true, "Attach a no-shadow helper light to camera on low quality to reduce dark shelf pockets.");
        CameraFillLightIntensity = Config.Bind("LowQualityFix", "CameraFillLightIntensity", 2.1f, "Intensity of the camera helper light.");
        CameraFillLightRange = Config.Bind("LowQualityFix", "CameraFillLightRange", 24f, "Range of the camera helper light.");
        GlobalDirectionalFillOnLow = Config.Bind("LowQualityFix", "GlobalDirectionalFillOnLow", true, "Add a no-shadow directional fill light on low quality to lift dark aisles.");
        GlobalDirectionalFillIntensity = Config.Bind("LowQualityFix", "GlobalDirectionalFillIntensity", 0.8f, "Intensity of the global directional fill light.");
        GlobalDirectionalBackFillIntensity = Config.Bind("LowQualityFix", "GlobalDirectionalBackFillIntensity", 1.2f, "Intensity of opposite-direction fill to reduce orientation-based dark faces.");
        DisableEngineShadowsOnLow = Config.Bind("LowQualityFix", "DisableEngineShadowsOnLow", true, "Force Unity engine shadows off on low quality (global fallback).");
        DisableOcclusionCullingOnLow = Config.Bind("LowQualityFix", "DisableOcclusionCullingOnLow", true, "Disable camera occlusion culling on low quality.");
        ForceDynamicRendererLightingOnLow = Config.Bind("LowQualityFix", "ForceDynamicRendererLightingOnLow", true, "Disable baked lightmaps/probes on renderers in low quality so dark zones use dynamic lighting.");
        DebugLogging = Config.Bind("Debug", "DebugLogging", false, "Enable verbose logs for troubleshooting.");
        LogSource = Log;

        ClassInjector.RegisterTypeInIl2Cpp<LowQualityLightBooster>();
        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(LightStatePatches));

        var go = new GameObject("BrighterLights.Controller");
        go.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<LowQualityLightBooster>();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded");
    }

    internal static void DebugLog(string message)
    {
        if (DebugLogging.Value)
            LogSource.LogInfo($"[Debug] {message}");
    }
}

class LowQualityLightBooster : MonoBehaviour
{
    private struct LightState
    {
        public float Intensity;
        public float Range;
        public float SpotAngle;
        public LightRenderMode RenderMode;
        public float ShadowStrength;
    }

    private struct RendererLightingState
    {
        public int LightmapIndex;
        public Vector4 LightmapScaleOffset;
        public LightProbeUsage LightProbeUsage;
        public ReflectionProbeUsage ReflectionProbeUsage;
        public bool ReceiveShadows;
    }

    private readonly Dictionary<int, LightState> _baseState = new();
    private readonly Dictionary<int, Light> _fillLights = new();
    private readonly Dictionary<int, bool> _forcedInteriorOn = new();
    private readonly Dictionary<int, RendererLightingState> _rendererLightingState = new();

    private float _timer;
    private bool _wasLowQuality;
    private bool _lastLowQualityStateKnown;
    private bool _lastLowQualityState;

    private int _lastCheckedHour = -1;
    private bool? _lastScheduledState;
    private bool _loggedMissingDayCycle;
    private bool _loggedMissingStoreManager;
    private bool _loggedMissingStorageManager;
    private float _debugSnapshotTimer;
    private bool _ambientStateCaptured;
    private float _ambientIntensityOriginal;
    private Color _ambientLightOriginal;
    private Light _cameraFillLight;
    private Camera _cameraFillCamera;
    private Light _globalFillLight;
    private Light _globalBackFillLight;
    private Light _originalSunLight;
    private bool _sunOverridden;
    private bool _shadowSettingsCaptured;
    private ShadowQuality _originalShadowQuality;
    private float _originalShadowDistance;
    private readonly Dictionary<int, bool> _cameraOcclusionOriginal = new();

    private static readonly FieldInfo InteriorLightObjectField = AccessTools.Field(typeof(InteriorSpotLight), "m_Light");
    private static LowQualityLightBooster _instance;

    public LowQualityLightBooster(IntPtr ptr) : base(ptr)
    {
        _instance = this;
    }

    private void Update()
    {
        if (!Plugin.Enabled.Value)
        {
            if (_wasLowQuality)
            {
                RestoreOriginalLightState();
                DisableAllFillLights();
                RestoreAmbientState();
                DisableCameraFillLight();
                DisableGlobalFillLight();
                RestoreSunOverride();
                RestoreShadowSettings();
                RestoreCameraOcclusionCulling();
                RestoreRendererLightingState();
                _wasLowQuality = false;
            }

            _baseState.Clear();
            return;
        }

        _timer += Time.unscaledDeltaTime;
        if (_timer < Mathf.Max(0.1f, Plugin.UpdateIntervalSeconds.Value))
            return;

        _timer = 0f;

        var isLowQuality = IsLowQuality();
        if (!_lastLowQualityStateKnown || _lastLowQualityState != isLowQuality)
        {
            Plugin.DebugLog($"Quality level {QualitySettings.GetQualityLevel()} => lowQuality={isLowQuality}");
            _lastLowQualityStateKnown = true;
            _lastLowQualityState = isLowQuality;
        }

        if (_wasLowQuality && !isLowQuality)
        {
            RestoreOriginalLightState();
            DisableAllFillLights();
            RestoreAmbientState();
            DisableCameraFillLight();
            DisableGlobalFillLight();
            RestoreSunOverride();
            RestoreShadowSettings();
            RestoreCameraOcclusionCulling();
            RestoreRendererLightingState();
            Plugin.DebugLog("Exited low quality mode. Restored original light state and disabled floor-fill lights.");
        }

        ApplyScheduledLights();

        if (isLowQuality)
        {
            ApplyLowQualityRenderOverrides();
            ApplyAmbientLiftOnLow();
            ApplyCameraFillLightOnLow();
            ApplyGlobalDirectionalFillOnLow();
            ApplyEngineShadowOverrideOnLow();
            ApplyOcclusionCullingOverrideOnLow();
            ApplyDynamicRendererLightingOverrideOnLow();
        }

        ProcessLights(isLowQuality);
        ProcessInteriorSpotLights(isLowQuality);
        EmitLightingSnapshot(isLowQuality);

        _wasLowQuality = isLowQuality;
    }

    private static bool IsLowQuality()
    {
        var qualityLevel = QualitySettings.GetQualityLevel();
        return qualityLevel <= Plugin.MaxQualityIndexToBoost.Value;
    }

    private void ProcessLights(bool isLowQuality)
    {
        var lights = UnityEngine.Object.FindObjectsOfType<Light>();
        var intensityMultiplier = Mathf.Max(1f, Plugin.IntensityMultiplier.Value);
        var rangeMultiplier = Mathf.Max(1f, Plugin.RangeMultiplier.Value);
        var spotAngleMultiplier = Mathf.Max(1f, Plugin.SpotAngleMultiplier.Value);

        foreach (var light in lights)
        {
            if (light == null || !light.isActiveAndEnabled)
                continue;

            var id = light.GetInstanceID();
            var currentIntensity = light.intensity;
            var currentRange = light.range;
            var currentSpotAngle = light.spotAngle;

            if (!_baseState.TryGetValue(id, out var original))
            {
                original = new LightState
                {
                    Intensity = currentIntensity,
                    Range = currentRange,
                    SpotAngle = currentSpotAngle,
                    RenderMode = light.renderMode,
                    ShadowStrength = light.shadowStrength
                };
                _baseState[id] = original;
            }

            if (!isLowQuality)
            {
                _baseState[id] = new LightState
                {
                    Intensity = currentIntensity,
                    Range = currentRange,
                    SpotAngle = currentSpotAngle,
                    RenderMode = light.renderMode,
                    ShadowStrength = light.shadowStrength
                };
                continue;
            }

            var targetIntensity = original.Intensity * intensityMultiplier;
            if (Mathf.Abs(currentIntensity - targetIntensity) > 0.01f)
                light.intensity = targetIntensity;

            if (light.type != LightType.Directional)
            {
                var targetRange = original.Range * rangeMultiplier;
                if (Mathf.Abs(currentRange - targetRange) > 0.01f)
                    light.range = targetRange;
                if (light.renderMode != LightRenderMode.ForcePixel)
                    light.renderMode = LightRenderMode.ForcePixel;
            }

            if (light.type == LightType.Spot)
            {
                var targetSpotAngle = Mathf.Clamp(original.SpotAngle * spotAngleMultiplier, 1f, 179f);
                if (Mathf.Abs(currentSpotAngle - targetSpotAngle) > 0.01f)
                    light.spotAngle = targetSpotAngle;
            }

            if (Plugin.SoftenShadowsOnLow.Value && light.shadows != LightShadows.None)
            {
                var maxStrength = Mathf.Clamp01(Plugin.MaxShadowStrengthOnLow.Value);
                if (light.shadowStrength > maxStrength)
                    light.shadowStrength = maxStrength;
            }

            if (Plugin.DisableAllShadowsOnLow.Value && isLowQuality && light.shadows != LightShadows.None)
                light.shadows = LightShadows.None;
        }

        if (Plugin.DebugLogging.Value)
        {
            Plugin.DebugLog($"ProcessLights: total={lights.Length}, lowQuality={isLowQuality}, intensityMult={intensityMultiplier:F2}, rangeMult={rangeMultiplier:F2}, spotMult={spotAngleMultiplier:F2}");
        }
    }

    private static void ApplyLowQualityRenderOverrides()
    {
        var minPixelLights = Mathf.Max(0, Plugin.ForcePixelLightCount.Value);
        if (QualitySettings.pixelLightCount < minPixelLights)
        {
            QualitySettings.pixelLightCount = minPixelLights;
            Plugin.DebugLog($"Raised QualitySettings.pixelLightCount to {minPixelLights}");
        }
    }

    private void ApplyAmbientLiftOnLow()
    {
        if (!Plugin.AmbientLiftOnLow.Value)
            return;

        if (!_ambientStateCaptured)
        {
            _ambientIntensityOriginal = RenderSettings.ambientIntensity;
            _ambientLightOriginal = RenderSettings.ambientLight;
            _ambientStateCaptured = true;
            Plugin.DebugLog($"Captured ambient baseline: intensity={_ambientIntensityOriginal:F2}, color={_ambientLightOriginal}");
        }

        var minIntensity = Mathf.Max(0f, Plugin.AmbientIntensityMinOnLow.Value);
        var minGray = Mathf.Clamp01(Plugin.AmbientGrayMinOnLow.Value);

        if (RenderSettings.ambientIntensity < minIntensity)
            RenderSettings.ambientIntensity = minIntensity;

        var cur = RenderSettings.ambientLight;
        var target = new Color(
            Mathf.Max(cur.r, minGray),
            Mathf.Max(cur.g, minGray),
            Mathf.Max(cur.b, minGray),
            cur.a);

        if (target != cur)
            RenderSettings.ambientLight = target;
    }

    private void RestoreAmbientState()
    {
        if (!_ambientStateCaptured)
            return;

        RenderSettings.ambientIntensity = _ambientIntensityOriginal;
        RenderSettings.ambientLight = _ambientLightOriginal;
        _ambientStateCaptured = false;
        Plugin.DebugLog("Restored ambient baseline after leaving low quality.");
    }

    private void ApplyCameraFillLightOnLow()
    {
        if (!Plugin.CameraFillLightOnLow.Value)
        {
            DisableCameraFillLight();
            return;
        }

        var cam = Camera.main;
        if (cam == null)
        {
            var allCams = UnityEngine.Object.FindObjectsOfType<Camera>();
            foreach (var c in allCams)
            {
                if (c != null && c.isActiveAndEnabled)
                {
                    cam = c;
                    break;
                }
            }
        }

        if (cam == null)
            return;

        if (_cameraFillLight == null)
        {
            var go = new GameObject("BrighterLights.CameraFill");
            go.hideFlags = HideFlags.HideAndDontSave;
            _cameraFillLight = go.AddComponent<Light>();
            _cameraFillLight.type = LightType.Point;
            _cameraFillLight.shadows = LightShadows.None;
            _cameraFillLight.renderMode = LightRenderMode.ForcePixel;
            _cameraFillLight.renderingLayerMask = -1;
            _cameraFillLight.cullingMask = ~0;
            _cameraFillLight.color = Color.white;
            Plugin.DebugLog("Created camera fill light.");
        }

        if (_cameraFillCamera != cam || _cameraFillLight.transform.parent != cam.transform)
        {
            _cameraFillCamera = cam;
            _cameraFillLight.transform.SetParent(cam.transform, false);
            _cameraFillLight.transform.localPosition = Vector3.zero;
            _cameraFillLight.transform.localRotation = Quaternion.identity;
            Plugin.DebugLog($"Attached camera fill light to camera '{cam.name}'.");
        }

        _cameraFillLight.range = Mathf.Max(1f, Plugin.CameraFillLightRange.Value);
        _cameraFillLight.intensity = Mathf.Max(0f, Plugin.CameraFillLightIntensity.Value);
        _cameraFillLight.enabled = true;
        _cameraFillLight.gameObject.SetActive(true);
    }

    private void DisableCameraFillLight()
    {
        if (_cameraFillLight == null)
            return;

        _cameraFillLight.enabled = false;
        _cameraFillLight.gameObject.SetActive(false);
    }

    private void ApplyGlobalDirectionalFillOnLow()
    {
        if (!Plugin.GlobalDirectionalFillOnLow.Value)
        {
            DisableGlobalFillLight();
            return;
        }

        if (_globalFillLight == null)
        {
            var go = new GameObject("BrighterLights.GlobalDirectionalFill");
            go.hideFlags = HideFlags.HideAndDontSave;
            _globalFillLight = go.AddComponent<Light>();
            _globalFillLight.type = LightType.Directional;
            _globalFillLight.shadows = LightShadows.None;
            _globalFillLight.renderMode = LightRenderMode.ForcePixel;
            _globalFillLight.renderingLayerMask = -1;
            _globalFillLight.cullingMask = ~0;
            _globalFillLight.color = Color.white;
            go.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
            Plugin.DebugLog("Created global directional fill light.");
        }

        _globalFillLight.intensity = Mathf.Max(0f, Plugin.GlobalDirectionalFillIntensity.Value);
        _globalFillLight.enabled = true;
        _globalFillLight.gameObject.SetActive(true);

        if (_globalBackFillLight == null)
        {
            var backGo = new GameObject("BrighterLights.GlobalDirectionalBackFill");
            backGo.hideFlags = HideFlags.HideAndDontSave;
            _globalBackFillLight = backGo.AddComponent<Light>();
            _globalBackFillLight.type = LightType.Directional;
            _globalBackFillLight.shadows = LightShadows.None;
            _globalBackFillLight.renderMode = LightRenderMode.ForcePixel;
            _globalBackFillLight.renderingLayerMask = -1;
            _globalBackFillLight.cullingMask = ~0;
            _globalBackFillLight.color = Color.white;
            backGo.transform.rotation = Quaternion.Euler(35f, 145f, 0f);
            Plugin.DebugLog("Created global directional back-fill light.");
        }

        _globalBackFillLight.intensity = Mathf.Max(0f, Plugin.GlobalDirectionalBackFillIntensity.Value);
        _globalBackFillLight.enabled = true;
        _globalBackFillLight.gameObject.SetActive(true);

        if (!_sunOverridden)
        {
            _originalSunLight = RenderSettings.sun;
            _sunOverridden = true;
            Plugin.DebugLog($"Captured original RenderSettings.sun='{(_originalSunLight != null ? _originalSunLight.name : "null")}'.");
        }

        if (RenderSettings.sun != _globalFillLight)
        {
            RenderSettings.sun = _globalFillLight;
            Plugin.DebugLog("Set RenderSettings.sun to global directional fill light.");
        }
    }

    private void DisableGlobalFillLight()
    {
        if (_globalFillLight == null && _globalBackFillLight == null)
            return;

        if (_globalFillLight != null)
        {
            _globalFillLight.enabled = false;
            _globalFillLight.gameObject.SetActive(false);
        }
        if (_globalBackFillLight != null)
        {
            _globalBackFillLight.enabled = false;
            _globalBackFillLight.gameObject.SetActive(false);
        }
    }

    private void RestoreSunOverride()
    {
        if (!_sunOverridden)
            return;

        RenderSettings.sun = _originalSunLight;
        _sunOverridden = false;
        Plugin.DebugLog("Restored original RenderSettings.sun.");
    }

    private void ApplyEngineShadowOverrideOnLow()
    {
        if (!Plugin.DisableEngineShadowsOnLow.Value)
        {
            RestoreShadowSettings();
            return;
        }

        if (!_shadowSettingsCaptured)
        {
            _originalShadowQuality = QualitySettings.shadows;
            _originalShadowDistance = QualitySettings.shadowDistance;
            _shadowSettingsCaptured = true;
            Plugin.DebugLog($"Captured shadow baseline: quality={_originalShadowQuality}, distance={_originalShadowDistance:F2}");
        }

        if (QualitySettings.shadows != ShadowQuality.Disable)
            QualitySettings.shadows = ShadowQuality.Disable;
        if (QualitySettings.shadowDistance != 0f)
            QualitySettings.shadowDistance = 0f;
    }

    private void RestoreShadowSettings()
    {
        if (!_shadowSettingsCaptured)
            return;

        QualitySettings.shadows = _originalShadowQuality;
        QualitySettings.shadowDistance = _originalShadowDistance;
        _shadowSettingsCaptured = false;
        Plugin.DebugLog("Restored shadow baseline after leaving low quality.");
    }

    private void ApplyOcclusionCullingOverrideOnLow()
    {
        if (!Plugin.DisableOcclusionCullingOnLow.Value)
        {
            RestoreCameraOcclusionCulling();
            return;
        }

        var cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
        foreach (var cam in cameras)
        {
            if (cam == null)
                continue;

            var id = cam.GetInstanceID();
            if (!_cameraOcclusionOriginal.ContainsKey(id))
                _cameraOcclusionOriginal[id] = cam.useOcclusionCulling;

            if (cam.useOcclusionCulling)
                cam.useOcclusionCulling = false;
        }
    }

    private void RestoreCameraOcclusionCulling()
    {
        if (_cameraOcclusionOriginal.Count == 0)
            return;

        var toRemove = new List<int>();
        foreach (var kvp in _cameraOcclusionOriginal)
        {
            var cam = UnityEngine.Object.FindObjectFromInstanceID(kvp.Key) as Camera;
            if (cam == null)
            {
                toRemove.Add(kvp.Key);
                continue;
            }

            cam.useOcclusionCulling = kvp.Value;
            toRemove.Add(kvp.Key);
        }

        foreach (var id in toRemove)
            _cameraOcclusionOriginal.Remove(id);

        Plugin.DebugLog("Restored camera occlusion culling settings.");
    }

    private void ApplyDynamicRendererLightingOverrideOnLow()
    {
        if (!Plugin.ForceDynamicRendererLightingOnLow.Value)
        {
            RestoreRendererLightingState();
            return;
        }

        var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
        var changedCount = 0;
        foreach (var renderer in renderers)
        {
            if (renderer == null)
                continue;

            var id = renderer.GetInstanceID();
            if (!_rendererLightingState.TryGetValue(id, out _))
            {
                _rendererLightingState[id] = new RendererLightingState
                {
                    LightmapIndex = renderer.lightmapIndex,
                    LightmapScaleOffset = renderer.lightmapScaleOffset,
                    LightProbeUsage = renderer.lightProbeUsage,
                    ReflectionProbeUsage = renderer.reflectionProbeUsage,
                    ReceiveShadows = renderer.receiveShadows
                };
            }

            var modified = false;
            if (renderer.lightmapIndex != -1)
            {
                renderer.lightmapIndex = -1;
                modified = true;
            }
            if (renderer.lightProbeUsage != LightProbeUsage.Off)
            {
                renderer.lightProbeUsage = LightProbeUsage.Off;
                modified = true;
            }
            if (renderer.reflectionProbeUsage != ReflectionProbeUsage.Off)
            {
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                modified = true;
            }
            if (renderer.receiveShadows)
            {
                renderer.receiveShadows = false;
                modified = true;
            }

            if (modified)
                changedCount++;
        }

        if (Plugin.DebugLogging.Value && changedCount > 0)
            Plugin.DebugLog($"DynamicRendererLightingOverride: changed={changedCount}, tracked={_rendererLightingState.Count}");
    }

    private void RestoreRendererLightingState()
    {
        if (_rendererLightingState.Count == 0)
            return;

        var toRemove = new List<int>();
        foreach (var kvp in _rendererLightingState)
        {
            var renderer = UnityEngine.Object.FindObjectFromInstanceID(kvp.Key) as Renderer;
            if (renderer == null)
            {
                toRemove.Add(kvp.Key);
                continue;
            }

            var st = kvp.Value;
            renderer.lightmapIndex = st.LightmapIndex;
            renderer.lightmapScaleOffset = st.LightmapScaleOffset;
            renderer.lightProbeUsage = st.LightProbeUsage;
            renderer.reflectionProbeUsage = st.ReflectionProbeUsage;
            renderer.receiveShadows = st.ReceiveShadows;
            toRemove.Add(kvp.Key);
        }

        foreach (var id in toRemove)
            _rendererLightingState.Remove(id);

        Plugin.DebugLog("Restored renderer baked-lighting state.");
    }

    public static void NotifyInteriorSpotState(InteriorSpotLight spot, bool on)
    {
        if (_instance == null || spot == null)
            return;

        _instance._forcedInteriorOn[spot.GetInstanceID()] = on;
        _instance.ApplyInteriorSpotImmediate(spot, on, "set_TurnOn");
    }

    private void ProcessInteriorSpotLights(bool isLowQuality)
    {
        var spots = UnityEngine.Object.FindObjectsOfType<InteriorSpotLight>();
        var seen = new HashSet<int>();
        var missingLightObjectCount = 0;
        var managersOnCount = 0;
        var forcedOnCount = 0;
        var shouldBeOnCount = 0;
        var fillEnabledCount = 0;

        foreach (var spot in spots)
        {
            if (spot == null)
                continue;

            var id = spot.GetInstanceID();
            seen.Add(id);

            var lightObj = InteriorLightObjectField?.GetValue(spot) as GameObject;
            var sourceLight = lightObj != null ? lightObj.GetComponent<Light>() : null;
            if (lightObj == null || sourceLight == null)
                missingLightObjectCount++;

            var managersSayOn =
                (StoreLightManager.Instance != null && StoreLightManager.Instance.TurnOn) ||
                (StorageLightManager.Instance != null && StorageLightManager.Instance.TurnOn);
            if (managersSayOn)
                managersOnCount++;

            var shouldBeOn =
                _forcedInteriorOn.TryGetValue(id, out var forcedOn)
                    ? forcedOn
                    : (managersSayOn || (lightObj != null && lightObj.activeInHierarchy));
            if (_forcedInteriorOn.TryGetValue(id, out var _))
                forcedOnCount++;
            if (shouldBeOn)
                shouldBeOnCount++;

            if (isLowQuality && Plugin.ForceInteriorLightObjectsOnLow.Value && shouldBeOn && lightObj != null && !lightObj.activeSelf)
            {
                lightObj.SetActive(true);
                Plugin.DebugLog($"Forced interior light object active for spotId={id}");
            }

            if (!Plugin.SpawnFloorFillLightsOnLow.Value || !isLowQuality)
            {
                SetFillLightEnabled(id, false);
                continue;
            }

            var fill = GetOrCreateFillLight(spot, id, sourceLight);
            if (fill == null)
                continue;

            fill.range = Mathf.Max(0.5f, Plugin.FloorFillRange.Value);
            fill.intensity = Mathf.Max(0f, Plugin.FloorFillIntensity.Value);
            fill.renderMode = LightRenderMode.ForcePixel;
            fill.renderingLayerMask = -1;
            fill.shadows = LightShadows.None;

            if (sourceLight != null)
            {
                fill.color = sourceLight.color;
                fill.cullingMask = Plugin.FloorFillAffectAllLayers.Value ? ~0 : sourceLight.cullingMask;
            }
            else if (Plugin.FloorFillAffectAllLayers.Value)
            {
                fill.cullingMask = ~0;
            }

            fill.enabled = shouldBeOn;
            fill.gameObject.SetActive(shouldBeOn);
            if (shouldBeOn)
                fillEnabledCount++;
        }

        CleanupRemovedSpots(seen);

        if (Plugin.DebugLogging.Value)
        {
            Plugin.DebugLog(
                $"InteriorSpots: total={spots.Length}, missingLightObjOrComponent={missingLightObjectCount}, managersOnForSpots={managersOnCount}, forcedStateSpots={forcedOnCount}, shouldBeOn={shouldBeOnCount}, fillEnabled={fillEnabledCount}, fillPool={_fillLights.Count}, lowQuality={isLowQuality}");
        }
    }

    private void EmitLightingSnapshot(bool isLowQuality)
    {
        if (!Plugin.DebugLogging.Value)
            return;

        _debugSnapshotTimer += Time.unscaledDeltaTime;
        if (_debugSnapshotTimer < 5f)
            return;

        _debugSnapshotTimer = 0f;

        var lights = UnityEngine.Object.FindObjectsOfType<Light>();
        var activeAndEnabled = 0;
        var disabled = 0;
        var withShadows = 0;
        var forcePixel = 0;

        foreach (var light in lights)
        {
            if (light == null)
                continue;

            if (light.isActiveAndEnabled)
                activeAndEnabled++;
            else
                disabled++;

            if (light.shadows != LightShadows.None)
                withShadows++;
            if (light.renderMode == LightRenderMode.ForcePixel)
                forcePixel++;
        }

        Plugin.DebugLog(
            $"Snapshot: quality={QualitySettings.GetQualityLevel()}, lowQuality={isLowQuality}, lightsTotal={lights.Length}, activeEnabled={activeAndEnabled}, disabled={disabled}, shadowsOn={withShadows}, forcePixel={forcePixel}, pixelLightCount={QualitySettings.pixelLightCount}");
    }

    private void ApplyInteriorSpotImmediate(InteriorSpotLight spot, bool shouldBeOn, string reason)
    {
        if (!IsLowQuality())
            return;

        var lightObj = InteriorLightObjectField?.GetValue(spot) as GameObject;
        if (lightObj == null)
        {
            Plugin.DebugLog($"Interior spot immediate apply skipped ({reason}): m_Light object missing.");
            return;
        }

        if (Plugin.ForceInteriorLightObjectsOnLow.Value && shouldBeOn && !lightObj.activeSelf)
            lightObj.SetActive(true);

        var sourceLight = lightObj.GetComponent<Light>();
        if (sourceLight == null)
        {
            Plugin.DebugLog($"Interior spot immediate apply skipped ({reason}): Light component missing.");
            return;
        }

        var intensityMultiplier = Mathf.Max(1f, Plugin.IntensityMultiplier.Value);
        var rangeMultiplier = Mathf.Max(1f, Plugin.RangeMultiplier.Value);
        var spotAngleMultiplier = Mathf.Max(1f, Plugin.SpotAngleMultiplier.Value);

        sourceLight.intensity *= intensityMultiplier;
        sourceLight.range *= rangeMultiplier;
        sourceLight.spotAngle = Mathf.Clamp(sourceLight.spotAngle * spotAngleMultiplier, 1f, 179f);
        sourceLight.renderMode = LightRenderMode.ForcePixel;

        if (Plugin.SoftenShadowsOnLow.Value && sourceLight.shadows != LightShadows.None)
            sourceLight.shadowStrength = Mathf.Min(sourceLight.shadowStrength, Mathf.Clamp01(Plugin.MaxShadowStrengthOnLow.Value));

        if (shouldBeOn)
            sourceLight.enabled = true;

        var id = spot.GetInstanceID();
        if (Plugin.SpawnFloorFillLightsOnLow.Value)
        {
            var fill = GetOrCreateFillLight(spot, id, sourceLight);
            if (fill != null)
            {
                fill.range = Mathf.Max(0.5f, Plugin.FloorFillRange.Value);
                fill.intensity = Mathf.Max(0f, Plugin.FloorFillIntensity.Value);
                fill.renderMode = LightRenderMode.ForcePixel;
                fill.renderingLayerMask = -1;
                fill.enabled = shouldBeOn;
                fill.gameObject.SetActive(shouldBeOn);
            }
        }

        Plugin.DebugLog($"Applied interior immediate light tweak ({reason}) spotId={id} on={shouldBeOn}");
    }

    private Light GetOrCreateFillLight(InteriorSpotLight spot, int id, Light sourceLight)
    {
        // Anchor under the *actual lamp Light* when available.
        // Place in world-down space so lamp rotation does not push fill light sideways.
        var parentTf = sourceLight != null ? sourceLight.transform : spot.transform;
        var offset = Mathf.Max(0.1f, Plugin.FloorFillVerticalOffset.Value);

        if (_fillLights.TryGetValue(id, out var existing) && existing != null)
        {
            var tf = existing.transform;
            tf.SetParent(parentTf, true);
            tf.position = parentTf.position + Vector3.down * offset;
            tf.rotation = Quaternion.identity;
            return existing;
        }

        var go = new GameObject("BrighterLights.FloorFill");
        go.hideFlags = HideFlags.HideAndDontSave;
        go.transform.SetParent(parentTf, true);
        go.transform.position = parentTf.position + Vector3.down * offset;
        go.transform.rotation = Quaternion.identity;

        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = Mathf.Max(0.5f, Plugin.FloorFillRange.Value);
        light.intensity = Mathf.Max(0f, Plugin.FloorFillIntensity.Value);
        light.renderMode = LightRenderMode.ForcePixel;
        light.renderingLayerMask = -1;
        light.shadows = LightShadows.None;
        light.color = sourceLight != null ? sourceLight.color : Color.white;
        light.cullingMask = Plugin.FloorFillAffectAllLayers.Value
            ? ~0
            : (sourceLight != null ? sourceLight.cullingMask : ~0);

        _fillLights[id] = light;
        return light;
    }

    private void SetFillLightEnabled(int id, bool enabled)
    {
        if (!_fillLights.TryGetValue(id, out var fill) || fill == null)
            return;

        fill.enabled = enabled;
        fill.gameObject.SetActive(enabled);
    }

    private void CleanupRemovedSpots(HashSet<int> seen)
    {
        var toRemove = new List<int>();
        foreach (var kvp in _fillLights)
        {
            if (!seen.Contains(kvp.Key))
            {
                if (kvp.Value != null)
                    UnityEngine.Object.Destroy(kvp.Value.gameObject);
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var id in toRemove)
        {
            _fillLights.Remove(id);
            _forcedInteriorOn.Remove(id);
        }
    }

    private void DisableAllFillLights()
    {
        foreach (var kvp in _fillLights)
        {
            if (kvp.Value == null)
                continue;

            kvp.Value.enabled = false;
            kvp.Value.gameObject.SetActive(false);
        }
    }

    private void ApplyScheduledLights()
    {
        if (!Plugin.AutoScheduleEnabled.Value)
        {
            _lastCheckedHour = -1;
            _lastScheduledState = null;
            return;
        }

        var dayCycle = DayCycleManager.Instance;
        if (dayCycle == null)
        {
            if (!_loggedMissingDayCycle)
            {
                Plugin.DebugLog("DayCycleManager.Instance is null; auto schedule waiting.");
                _loggedMissingDayCycle = true;
            }
            return;
        }
        _loggedMissingDayCycle = false;

        var currentHour = GetCurrentHour24(dayCycle);
        var shouldBeOn = ShouldLightsBeOn(
            currentHour,
            Mathf.Clamp(Plugin.AutoLightsOnHour.Value, 0, 23),
            Mathf.Clamp(Plugin.AutoLightsOffHour.Value, 0, 23));

        if (_lastCheckedHour == currentHour && _lastScheduledState.HasValue && _lastScheduledState.Value == shouldBeOn)
            return;

        Plugin.DebugLog($"Scheduler state change: rawHour={dayCycle.CurrentHour}, am={dayCycle.AM}, hour24={currentHour}, shouldBeOn={shouldBeOn}");

        var appliedAny = false;

        if (Plugin.AutoAffectStoreLights.Value)
        {
            var store = StoreLightManager.Instance;
            if (store != null)
            {
                if (store.TurnOn != shouldBeOn)
                {
                    store.TurnOn = shouldBeOn;
                    Plugin.DebugLog($"Set StoreLightManager.TurnOn={shouldBeOn} at hour={currentHour}");
                }
                appliedAny = true;
                _loggedMissingStoreManager = false;
            }
            else if (!_loggedMissingStoreManager)
            {
                Plugin.DebugLog("StoreLightManager.Instance is null; auto schedule waiting.");
                _loggedMissingStoreManager = true;
            }
        }

        if (Plugin.AutoAffectStorageLights.Value)
        {
            var storage = StorageLightManager.Instance;
            if (storage != null)
            {
                if (storage.TurnOn != shouldBeOn)
                {
                    storage.TurnOn = shouldBeOn;
                    Plugin.DebugLog($"Set StorageLightManager.TurnOn={shouldBeOn} at hour={currentHour}");
                }
                appliedAny = true;
                _loggedMissingStorageManager = false;
            }
            else if (!_loggedMissingStorageManager)
            {
                Plugin.DebugLog("StorageLightManager.Instance is null; auto schedule waiting.");
                _loggedMissingStorageManager = true;
            }
        }

        if (appliedAny)
        {
            _lastCheckedHour = currentHour;
            _lastScheduledState = shouldBeOn;
        }
    }

    private static int GetCurrentHour24(DayCycleManager dayCycle)
    {
        var hour = dayCycle.CurrentHour;
        if (hour == 12)
            hour = 0;
        if (!dayCycle.AM)
            hour += 12;
        return Mathf.Clamp(hour, 0, 23);
    }

    private static bool ShouldLightsBeOn(int hour24, int onHour, int offHour)
    {
        if (onHour == offHour)
            return true;

        if (onHour < offHour)
            return hour24 >= onHour && hour24 < offHour;

        return hour24 >= onHour || hour24 < offHour;
    }

    private void RestoreOriginalLightState()
    {
        foreach (var kvp in _baseState)
        {
            var id = kvp.Key;
            var st = kvp.Value;
            var obj = UnityEngine.Object.FindObjectFromInstanceID(id) as Light;
            if (obj == null)
                continue;

            obj.intensity = st.Intensity;
            obj.range = st.Range;
            obj.spotAngle = st.SpotAngle;
            obj.renderMode = st.RenderMode;
            obj.shadowStrength = st.ShadowStrength;
        }
    }
}

[HarmonyPatch]
static class LightStatePatches
{
    [HarmonyPatch(typeof(InteriorSpotLight), "set_TurnOn")]
    [HarmonyPostfix]
    static void InteriorSpotLight_set_TurnOn_Postfix(InteriorSpotLight __instance, bool value)
    {
        LowQualityLightBooster.NotifyInteriorSpotState(__instance, value);
    }
}
