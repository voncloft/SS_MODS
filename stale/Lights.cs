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

namespace NightShift;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "von.supermarketsim.brighterlights";
    public const string PluginName = "BrighterLights";
    public const string PluginVersion = "1.4.1";

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
    internal static ConfigEntry<bool> SoftenShadowsOnLow = null!;
    internal static ConfigEntry<float> MaxShadowStrengthOnLow = null!;
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
        SoftenShadowsOnLow = Config.Bind("LowQualityFix", "SoftenShadowsOnLow", true, "Reduce shadow darkness on boosted lights in low quality.");
        MaxShadowStrengthOnLow = Config.Bind("LowQualityFix", "MaxShadowStrengthOnLow", 0.55f, "Maximum shadow strength allowed on boosted lights in low quality (0-1).");
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

public sealed class LowQualityLightBooster : MonoBehaviour
{
    private struct LightState
    {
        public float Intensity;
        public float Range;
        public float SpotAngle;
        public LightRenderMode RenderMode;
        public float ShadowStrength;
    }

    private readonly Dictionary<int, LightState> _baseState = new();
    private readonly Dictionary<int, Light> _fillLights = new();
    private readonly Dictionary<int, bool> _forcedInteriorOn = new();
    private float _timer;
    private bool _wasLowQuality;
    private bool _lastLowQualityStateKnown;
    private bool _lastLowQualityState;
    private int _lastCheckedHour = -1;
    private bool? _lastScheduledState;
    private bool _loggedMissingDayCycle;
    private bool _loggedMissingStoreManager;
    private bool _loggedMissingStorageManager;
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
            Plugin.DebugLog("Exited low quality mode. Restored original light state and disabled floor-fill lights.");
        }

        ApplyScheduledLights();

        if (isLowQuality)
            ApplyLowQualityRenderOverrides();

        ProcessLights(isLowQuality);
        ProcessInteriorSpotLights(isLowQuality);
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

    public static void NotifyInteriorSpotState(InteriorSpotLight spot, bool on)
    {
        if (_instance == null || spot == null)
            return;
        _instance._forcedInteriorOn[spot.GetInstanceID()] = on;
        _instance.ApplyInteriorSpotImmediate(spot, on, "set_TurnOn");
    }

    public static void NotifyInteriorSpotAdded(InteriorSpotLight spot)
    {
        if (_instance == null || spot == null)
            return;
        _instance.ApplyInteriorSpotImmediate(spot, true, "AddToManager");
    }

    private void ProcessInteriorSpotLights(bool isLowQuality)
    {
        var spots = UnityEngine.Object.FindObjectsOfType<InteriorSpotLight>();
        var seen = new HashSet<int>();

        foreach (var spot in spots)
        {
            if (spot == null)
                continue;

            var id = spot.GetInstanceID();
            seen.Add(id);

            var lightObj = InteriorLightObjectField?.GetValue(spot) as GameObject;
            var sourceLight = lightObj != null ? lightObj.GetComponent<Light>() : null;

            var shouldBeOn = _forcedInteriorOn.TryGetValue(id, out var forcedOn) ? forcedOn : (lightObj != null && lightObj.activeInHierarchy);
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
            fill.shadows = LightShadows.None;
            if (sourceLight != null)
            {
                fill.color = sourceLight.color;
                fill.cullingMask = sourceLight.cullingMask;
            }

            fill.enabled = shouldBeOn;
            fill.gameObject.SetActive(shouldBeOn);
        }

        CleanupRemovedSpots(seen);
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
                fill.enabled = shouldBeOn;
                fill.gameObject.SetActive(shouldBeOn);
            }
        }

        Plugin.DebugLog($"Applied interior immediate light tweak ({reason}) spotId={id} on={shouldBeOn}");
    }

    private Light GetOrCreateFillLight(InteriorSpotLight spot, int id, Light sourceLight)
    {
        if (_fillLights.TryGetValue(id, out var existing) && existing != null)
        {
            var existingTf = existing.transform;
            existingTf.SetParent(spot.transform, false);
            existingTf.localPosition = new Vector3(0f, -Mathf.Max(0.1f, Plugin.FloorFillVerticalOffset.Value), 0f);
            existingTf.localRotation = Quaternion.identity;
            return existing;
        }

        var go = new GameObject("BrighterLights.FloorFill");
        go.hideFlags = HideFlags.HideAndDontSave;
        go.transform.SetParent(spot.transform, false);
        go.transform.localPosition = new Vector3(0f, -Mathf.Max(0.1f, Plugin.FloorFillVerticalOffset.Value), 0f);
        go.transform.localRotation = Quaternion.identity;

        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = Mathf.Max(0.5f, Plugin.FloorFillRange.Value);
        light.intensity = Mathf.Max(0f, Plugin.FloorFillIntensity.Value);
        light.renderMode = LightRenderMode.ForcePixel;
        light.shadows = LightShadows.None;
        light.color = sourceLight != null ? sourceLight.color : Color.white;
        if (sourceLight != null)
            light.cullingMask = sourceLight.cullingMask;

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

        // Only cache hour/state after we have at least one target manager available.
        if (appliedAny)
        {
            _lastCheckedHour = currentHour;
            _lastScheduledState = shouldBeOn;
        }
    }

    private static bool ShouldLightsBeOn(int currentHour, int onHour, int offHour)
    {
        if (onHour == offHour)
            return true;

        if (onHour < offHour)
            return currentHour >= onHour && currentHour < offHour;

        return currentHour >= onHour || currentHour < offHour;
    }

    private static int GetCurrentHour24(DayCycleManager dayCycle)
    {
        // Some game builds expose CurrentHour in 12-hour form with AM/PM flag.
        var rawHour = dayCycle.CurrentHour;
        var am = dayCycle.AM;

        if (rawHour is >= 0 and <= 23)
        {
            // If AM/PM is provided and hour looks 12-hour, normalize to 24-hour.
            if (rawHour is >= 1 and <= 12)
            {
                var hour12 = rawHour % 12;
                return am ? hour12 : hour12 + 12;
            }

            return rawHour;
        }

        return Mathf.Clamp(rawHour, 0, 23);
    }

    private void RestoreOriginalLightState()
    {
        var lights = UnityEngine.Object.FindObjectsOfType<Light>();

        foreach (var light in lights)
        {
            if (light == null)
                continue;

            var id = light.GetInstanceID();
            if (!_baseState.TryGetValue(id, out var original))
                continue;

            light.intensity = original.Intensity;
            if (light.type != LightType.Directional)
                light.range = original.Range;
            if (light.type == LightType.Spot)
                light.spotAngle = original.SpotAngle;
            light.renderMode = original.RenderMode;
            light.shadowStrength = original.ShadowStrength;
        }
    }
}

internal static class LightStatePatches
{
    [HarmonyPatch(typeof(InteriorSpotLight), "AddToManager")]
    [HarmonyPostfix]
    private static void InteriorSpotAddToManagerPostfix(InteriorSpotLight __instance)
    {
        LowQualityLightBooster.NotifyInteriorSpotAdded(__instance);
    }

    [HarmonyPatch(typeof(InteriorSpotLight), "set_TurnOn")]
    [HarmonyPostfix]
    private static void InteriorSpotTurnOnPostfix(InteriorSpotLight __instance, bool value)
    {
        LowQualityLightBooster.NotifyInteriorSpotState(__instance, value);
    }
}
