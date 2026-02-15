using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Lean.Pool;
using UnityEngine;

namespace OvernightStocking;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    private const string PluginGuid = "von.overnightstocking.fullshelf";
    private const string PluginName = "OvernightStocking";
    private const string PluginVersion = "1.0.2";

    internal static ManualLogSource LogSource { get; private set; }
    private Harmony _harmony;

    public override void Load()
    {
        LogSource = Log;
        VerboseFileLog.Initialize(LogSource);
        VerboseFileLog.Write("Plugin Load() start");

        ClassInjector.RegisterTypeInIl2Cpp<HotkeyRunner>();
        VerboseFileLog.Write("Registered HotkeyRunner IL2CPP type");

        var go = new GameObject("OvernightStocking.HotkeyRunner");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<HotkeyRunner>();
        VerboseFileLog.Write("Created HotkeyRunner GameObject and component");

        _harmony = new Harmony("von.overnightstocking.fullshelf.patches");
        _harmony.PatchAll(typeof(DayCyclePatches));
        VerboseFileLog.Write("Harmony patches applied");

        LogSource.LogInfo("Loaded. Press F2 to fully restock shelf slots by count.");
        VerboseFileLog.Write("Plugin Load() complete [build=rack-only-v1.0.2]");
    }
}

public sealed class HotkeyRunner : MonoBehaviour
{
    private const float HotkeyCooldownSeconds = 0.2f;
    private const int SlotsPerFrameBudget = 1;
    private const int MaxAddsPerSlotSafety = 96;

    private float _lastHotkeyTime;
    private bool _isRestocking;
    private DisplaySlot[] _activeSlots;
    private int _slotIndex;
    private int _touchedTotal;
    private int _addedTotal;
    private int _failedSlots;
    private int _unlabeledSlots;
    private static IDManager _cachedIdManager;
    private static RackManager _cachedRackManager;
    private static InventoryManager _cachedInventoryManager;
    private static HotkeyRunner _instance;
    private bool _pendingRestock;
    private static bool _pendingSceneLoadRestock;
    private int _lastSceneBuildIndex = int.MinValue;

    public HotkeyRunner(IntPtr ptr) : base(ptr)
    {
        VerboseFileLog.Write("HotkeyRunner constructed");
    }

    private void Awake()
    {
        _instance = this;
        _lastSceneBuildIndex = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        VerboseFileLog.Write("HotkeyRunner Awake()");
    }

    private void OnEnable()
    {
        VerboseFileLog.Write("HotkeyRunner OnEnable()");
    }

    private void OnDisable()
    {
        VerboseFileLog.Write("HotkeyRunner OnDisable()");
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }

        VerboseFileLog.Write("HotkeyRunner OnDestroy()");
    }

    private void Update()
    {
        int currentSceneBuildIndex = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        if (currentSceneBuildIndex != _lastSceneBuildIndex)
        {
            VerboseFileLog.Write($"Scene changed buildIndex={_lastSceneBuildIndex}->{currentSceneBuildIndex}");
            _lastSceneBuildIndex = currentSceneBuildIndex;

            if (_pendingSceneLoadRestock)
            {
                _pendingSceneLoadRestock = false;
                _pendingRestock = true;
                VerboseFileLog.Write($"Scene-change restock armed (buildIndex={currentSceneBuildIndex})");
            }
        }

        if (_isRestocking)
        {
            ProcessRestockFrame();
            return;
        }

        if (_pendingRestock)
        {
            _pendingRestock = false;
            VerboseFileLog.Write("Auto restock request consumed");
            BeginRestock();
            return;
        }

        if (!Input.GetKeyDown(KeyCode.F2))
        {
            return;
        }

        if (_isRestocking)
        {
            VerboseFileLog.Write("F2 ignored: restock already in progress");
            return;
        }

        if (Time.unscaledTime - _lastHotkeyTime < HotkeyCooldownSeconds)
        {
            VerboseFileLog.Write("F2 pressed but ignored due to cooldown");
            return;
        }

        _lastHotkeyTime = Time.unscaledTime;
        VerboseFileLog.Write($"F2 accepted at t={_lastHotkeyTime:F3}");
        BeginRestock();
    }

    internal static void RequestAutoRestock(string reason)
    {
        if (_instance == null)
        {
            VerboseFileLog.Write($"Auto restock request dropped: runner missing (reason={reason})");
            return;
        }

        _instance._pendingRestock = true;
        VerboseFileLog.Write($"Auto restock requested (reason={reason})");
    }

    internal static void RequestAutoRestockAfterSceneLoad(string reason)
    {
        _pendingSceneLoadRestock = true;
        VerboseFileLog.Write($"Auto restock queued for next scene load (reason={reason})");
    }

    private void BeginRestock()
    {
        _activeSlots = UnityEngine.Object.FindObjectsOfType<DisplaySlot>();
        _slotIndex = 0;
        _touchedTotal = 0;
        _addedTotal = 0;
        _failedSlots = 0;
        _unlabeledSlots = 0;
        _isRestocking = true;

        VerboseFileLog.Write($"Found DisplaySlot count={_activeSlots.Length}");
    }

    private void ProcessRestockFrame()
    {
        int processedThisFrame = 0;

        try
        {
            while (_slotIndex < _activeSlots.Length && processedThisFrame < SlotsPerFrameBudget)
            {
                int i = _slotIndex;
                _slotIndex++;
                processedThisFrame++;

                var slot = _activeSlots[i];
                if (slot == null)
                {
                    VerboseFileLog.Write($"Slot index={i} is null");
                    continue;
                }

                try
                {
                    int before = GetVisibleProductCount(slot);
                    int productId = -1;
                    bool hasLabel = false;
                    var data = slot.Data;
                    if (data != null)
                    {
                        hasLabel = data.HasLabel;
                        productId = data.FirstItemID;
                    }
                    else
                    {
                        VerboseFileLog.Write($"Slot index={i} has null Data");
                    }

                    if (!hasLabel)
                    {
                        _unlabeledSlots++;
                    }

                    VerboseFileLog.Write(
                        $"Slot index={i} begin before={before} hasLabel={hasLabel} productId={productId} full={slot.Full}");

                    if (TryFillSlot(slot, out int delta))
                    {
                        _touchedTotal++;
                        _addedTotal += delta;
                        VerboseFileLog.Write(
                            $"Slot index={i} filled delta={delta} after={GetVisibleProductCount(slot)} full={slot.Full}");
                    }
                    else
                    {
                        int after = GetVisibleProductCount(slot);
                        if (after > before)
                        {
                            _touchedTotal++;
                            _addedTotal += (after - before);
                            VerboseFileLog.Write(
                                $"Slot index={i} fallback-delta={after - before} after={after} full={slot.Full}");
                        }
                        else
                        {
                            VerboseFileLog.Write(
                                $"Slot index={i} unchanged before={before} after={after} full={slot.Full}");
                        }
                    }
                }
                catch (Exception slotEx)
                {
                    _failedSlots++;
                    VerboseFileLog.Write($"Slot index={i} failed: {slotEx}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogError($"F2 restock failed: {ex}");
            VerboseFileLog.Write($"F2 global failure: {ex}");
        }

        if (_slotIndex >= _activeSlots.Length)
        {
            Plugin.LogSource.LogInfo($"F2 restock: slots={_activeSlots.Length}, touched={_touchedTotal}, added={_addedTotal}, failed={_failedSlots}");
            VerboseFileLog.Write(
                $"F2 summary slots={_activeSlots.Length} touched={_touchedTotal} added={_addedTotal} failed={_failedSlots} unlabeled={_unlabeledSlots}");

            _isRestocking = false;
            _activeSlots = null;
            VerboseFileLog.Write("Restock routine complete");
        }
    }

    private static bool TryFillSlot(DisplaySlot slot, out int addedCount)
    {
        addedCount = 0;

        if (slot == null)
        {
            VerboseFileLog.Write("TryFillSlot abort: slot is null");
            return false;
        }

        var data = slot.Data;
        if (data == null || !data.HasLabel)
        {
            VerboseFileLog.Write($"TryFillSlot abort: invalid label state (dataNull={data == null})");
            return false;
        }

        int productId = data.FirstItemID;
        if (productId <= 0)
        {
            VerboseFileLog.Write($"TryFillSlot abort: invalid productId={productId}");
            return false;
        }

        var idManager = _cachedIdManager;
        if (idManager == null)
        {
            idManager = UnityEngine.Object.FindObjectOfType<IDManager>();
            _cachedIdManager = idManager;
        }

        if (idManager == null)
        {
            VerboseFileLog.Write("TryFillSlot abort: IDManager null");
            return false;
        }

        var productSO = idManager.ProductSO(productId);
        if (productSO == null || productSO.ProductPrefab == null || productSO.GridLayoutInStorage == null)
        {
            VerboseFileLog.Write($"TryFillSlot abort: missing ProductSO/ProductPrefab/GridLayout productId={productId}");
            return false;
        }

        int before = GetVisibleProductCount(slot);
        VerboseFileLog.Write($"TryFillSlot start productId={productId} before={before} full={slot.Full}");

        if (slot.m_Products == null)
        {
            VerboseFileLog.Write("TryFillSlot abort: m_Products list is null");
            return false;
        }

        int currentCount = slot.m_Products.Count;
        if (slot.Full)
        {
            if (data.FirstItemCount != currentCount)
            {
                data.FirstItemCount = currentCount;
            }

            VerboseFileLog.Write($"TryFillSlot already full current={currentCount}");
            return slot.Full;
        }

        data.FirstItemCount = currentCount;

        int adds = 0;
        while (!slot.Full && adds < MaxAddsPerSlotSafety)
        {
            if (!TryConsumeOneFromRacks(productId))
            {
                VerboseFileLog.Write($"TryFillSlot stop: no rack stock productId={productId}");
                break;
            }

            int i = currentCount;
            Product spawned;
            try
            {
                spawned = LeanPool.Spawn<Product>(productSO.ProductPrefab, slot.transform, false);
            }
            catch (Exception spawnEx)
            {
                VerboseFileLog.Write($"TryFillSlot spawn exception index={i}: {spawnEx}");
                break;
            }

            try
            {
                slot.m_TransformCalculator.SetProductSo(productSO);
                spawned.transform.localPosition = slot.m_TransformCalculator.GetPosition(i);
                spawned.transform.localRotation = slot.m_TransformCalculator.GetRotation(i);
                spawned.transform.localScale = Vector3.one * productSO.GridLayoutInStorage.scaleMultiplier;
                slot.m_Products.Add(spawned);
                data.FirstItemCount = currentCount + 1;
                currentCount++;
                adds++;
            }
            catch (Exception positionEx)
            {
                VerboseFileLog.Write($"TryFillSlot placement exception index={i}: {positionEx}");
                break;
            }
        }

        try
        {
            slot.SetLabel();
            slot.SetPriceTag();
        }
        catch (Exception uiEx)
        {
            VerboseFileLog.Write($"TryFillSlot label/price update exception: {uiEx}");
        }

        int after = GetVisibleProductCount(slot);
        addedCount = Math.Max(0, after - before);
        bool success = addedCount > 0 || slot.Full;
        VerboseFileLog.Write($"TryFillSlot end before={before} after={after} adds={adds} delta={addedCount} full={slot.Full} success={success}");
        return success;
    }

    private static bool TryConsumeOneFromRacks(int productId)
    {
        var rackManager = _cachedRackManager;
        if (rackManager == null)
        {
            rackManager = UnityEngine.Object.FindObjectOfType<RackManager>();
            _cachedRackManager = rackManager;
        }

        if (rackManager == null)
        {
            VerboseFileLog.Write("TryConsumeOneFromRacks abort: RackManager null");
            return false;
        }

        var racks = rackManager.m_Racks;
        if (racks == null || racks.Count == 0)
        {
            return false;
        }

        int candidateSlots = 0;

        for (int r = 0; r < racks.Count; r++)
        {
            var rack = racks[r];
            if (rack == null || rack.RackSlots == null)
            {
                continue;
            }

            for (int i = 0; i < rack.RackSlots.Count; i++)
            {
                var rackSlot = rack.RackSlots[i];
                if (rackSlot == null || !rackSlot.HasBox || rackSlot.Data == null || rackSlot.Data.ProductID != productId)
                {
                    continue;
                }

                candidateSlots++;

                try
                {
                    var box = rackSlot.GetMinBoxInRack();
                    if (box == null || box.Data == null || box.Data.ProductID != productId || box.Data.ProductCount <= 0)
                    {
                        continue;
                    }

                    if (box.Data.ProductCount > 1)
                    {
                        box.Data.ProductCount -= 1;
                        try
                        {
                            rackSlot.RefreshLabel();
                        }
                        catch
                        {
                        }

                        return true;
                    }

                    // Last product in this box: remove the box from rack and inventory.
                    var removedBox = rackSlot.TakeBoxFromRack();
                    if (removedBox != null)
                    {
                        try
                        {
                            var inventory = _cachedInventoryManager;
                            if (inventory == null)
                            {
                                inventory = UnityEngine.Object.FindObjectOfType<InventoryManager>();
                                _cachedInventoryManager = inventory;
                            }

                            if (inventory != null)
                            {
                                inventory.RemoveBox(removedBox.Data);
                            }
                        }
                        catch
                        {
                        }

                        try
                        {
                            LeanPool.Despawn(removedBox.gameObject);
                        }
                        catch
                        {
                        }

                        try
                        {
                            removedBox.ResetBox();
                        }
                        catch
                        {
                        }

                        try
                        {
                            UnityEngine.Object.Destroy(removedBox.gameObject);
                        }
                        catch
                        {
                        }

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    VerboseFileLog.Write($"TryConsumeOneFromRacks slot exception productId={productId}: {ex.Message}");
                }
            }
        }

        VerboseFileLog.Write($"TryConsumeOneFromRacks none-found productId={productId} candidateSlots={candidateSlots}");
        return false;
    }

    private static int GetVisibleProductCount(DisplaySlot slot)
    {
        if (slot == null)
        {
            return 0;
        }

        if (slot.m_Products != null)
        {
            return slot.m_Products.Count;
        }

        try
        {
            return slot.ProductCount;
        }
        catch
        {
            return 0;
        }
    }
}

[HarmonyPatch(typeof(DayCycleManager), "FinishTheDay")]
internal static class DayCyclePatches
{
    [HarmonyPrefix]
    private static void PrefixFinishTheDay()
    {
        try
        {
            HotkeyRunner.RequestAutoRestockAfterSceneLoad("FinishTheDay");
        }
        catch (Exception ex)
        {
            VerboseFileLog.Write($"FinishTheDay patch error: {ex}");
        }
    }
}

internal static class VerboseFileLog
{
    private static readonly object Sync = new();
    private static string _path;
    private static ManualLogSource _log;
    private static bool _initialized;

    internal static void Initialize(ManualLogSource log)
    {
        lock (Sync)
        {
            _log = log;
            _path = Path.Combine(Paths.BepInExRootPath, "Nightstocking.log");
            _initialized = true;
            SafeAppend($"========== Session Start {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ==========\n");
            SafeAppend($"Plugin assembly: {typeof(Plugin).Assembly.FullName}\n");
            SafeAppend($"BepInEx root: {Paths.BepInExRootPath}\n");
        }
    }

    internal static void Write(string message)
    {
        lock (Sync)
        {
            if (!_initialized)
            {
                return;
            }

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            SafeAppend(line + "\n");
            _log?.LogDebug(line);
        }
    }

    private static void SafeAppend(string text)
    {
        try
        {
            File.AppendAllText(_path, text);
        }
        catch
        {
            // If disk logging fails, avoid crashing gameplay.
        }
    }
}
