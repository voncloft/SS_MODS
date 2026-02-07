using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

namespace SkipTutorial
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class SkipTutorialPlugin : BasePlugin
    {
        public const string PLUGIN_GUID = "von.skiptutorial.il2cpp";
        public const string PLUGIN_NAME = "Skip Tutorial + Hide Objective UI (IL2CPP)";
        public const string PLUGIN_VERSION = "1.4.2";

        internal static ManualLogSource L;
        private Harmony _harmony;

        public override void Load()
        {
            L = base.Log;

            try
            {
                _harmony = new Harmony(PLUGIN_GUID);

                OnboardingPatcher.Patch(_harmony);
                ObjectiveDisplayPatcher.Patch(_harmony);

                L.LogInfo($"[{PLUGIN_NAME}] Loaded.");
            }
            catch (Exception ex)
            {
                L.LogError($"[{PLUGIN_NAME}] Load failed: {ex}");
            }
        }
    }

    // ================= Onboarding skip =================
    internal static class OnboardingPatcher
    {
        private static Type T;
        private static MethodBase mStart, mLoadSave, mLoadNet, mNext, mFinish, mGetCompleted, mSetStep;

        internal static void Patch(Harmony h)
        {
            T = AccessTools.TypeByName("OnboardingManager");
            if (T == null)
            {
                SkipTutorialPlugin.L.LogWarning("[SkipTutorial] OnboardingManager not found.");
                return;
            }

            mStart = AccessTools.Method(T, "Start");
            mLoadSave = AccessTools.Method(T, "LoadSaveProgress");
            mLoadNet = AccessTools.Method(T, "LoadProgressNetwork");
            mNext = AccessTools.Method(T, "NextStep");
            mFinish = AccessTools.Method(T, "FinishStep");
            mGetCompleted = AccessTools.Method(T, "get_Completed");
            mSetStep = AccessTools.Method(T, "set_Step", new[] { typeof(int) });

            var postfixForce = new HarmonyMethod(typeof(OnboardingPatcher).GetMethod(
                nameof(Postfix_Force), BindingFlags.Static | BindingFlags.NonPublic));

            var prefixBlock = new HarmonyMethod(typeof(OnboardingPatcher).GetMethod(
                nameof(Prefix_Block), BindingFlags.Static | BindingFlags.NonPublic));

            var postfixCompleted = new HarmonyMethod(typeof(OnboardingPatcher).GetMethod(
                nameof(Postfix_Completed), BindingFlags.Static | BindingFlags.NonPublic));

            if (mStart != null) h.Patch(mStart, postfix: postfixForce);
            if (mLoadSave != null) h.Patch(mLoadSave, postfix: postfixForce);
            if (mLoadNet != null) h.Patch(mLoadNet, postfix: postfixForce);
            if (mNext != null) h.Patch(mNext, prefix: prefixBlock);
            if (mFinish != null) h.Patch(mFinish, prefix: prefixBlock);
            if (mGetCompleted != null) h.Patch(mGetCompleted, postfix: postfixCompleted);

            SkipTutorialPlugin.L.LogWarning("[SkipTutorial] OnboardingManager patched.");
        }

        private static bool Prefix_Block() => false;
        private static void Postfix_Completed(ref bool __result) => __result = true;

        private static void Postfix_Force(object __instance)
        {
            if (__instance == null) return;

            try { mSetStep?.Invoke(__instance, new object[] { 99 }); } catch { }

            foreach (var f in T.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (f.FieldType == typeof(GameObject[]))
                {
                    var arr = f.GetValue(__instance) as GameObject[];
                    if (arr == null) continue;
                    foreach (var go in arr) if (go != null) go.SetActive(false);
                }
            }
        }
    }

    // ================= Objective UI hider =================
    internal static class ObjectiveDisplayPatcher
    {
        private static Type T;
        private static MethodBase mToggle, mSet1, mSet2;
        private static FieldInfo fTitle, fDesc, fCanvasLike;
        private static bool logged;

        internal static void Patch(Harmony h)
        {
            T = AccessTools.TypeByName("TutorialObjectiveDisplay");
            if (T == null)
            {
                SkipTutorialPlugin.L.LogWarning("[SkipTutorial] TutorialObjectiveDisplay not found.");
                return;
            }

            mToggle = AccessTools.Method(T, "Toggle", new[] { typeof(bool) });
            mSet1 = AccessTools.Method(T, "SetObjectiveData");
            mSet2 = AccessTools.Method(T, "SetObjectiveDataWithArgs");

            fTitle = T.GetField("m_TitleText", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            fDesc  = T.GetField("m_DescriptionText", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            // In your dump it's named m_Canvas; we keep it generic and reflection-based.
            fCanvasLike = T.GetField("m_Canvas", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            var prefix = new HarmonyMethod(typeof(ObjectiveDisplayPatcher).GetMethod(
                nameof(Prefix_Hide), BindingFlags.Static | BindingFlags.NonPublic));

            if (mToggle != null) h.Patch(mToggle, prefix: prefix);
            if (mSet1 != null) h.Patch(mSet1, prefix: prefix);
            if (mSet2 != null) h.Patch(mSet2, prefix: prefix);

            SkipTutorialPlugin.L.LogWarning("[SkipTutorial] TutorialObjectiveDisplay patched (UI hidden).");
        }

        private static bool Prefix_Hide(object __instance)
        {
            try
            {
                Hide(__instance);
                if (!logged)
                {
                    logged = true;
                    SkipTutorialPlugin.L.LogWarning("[SkipTutorial] Objective UI suppressed.");
                }
            }
            catch (Exception ex)
            {
                SkipTutorialPlugin.L.LogError($"[SkipTutorial] Objective hide failed: {ex}");
            }

            return false;
        }

        private static void Hide(object inst)
        {
            if (inst == null) return;

            ClearTextField(fTitle?.GetValue(inst));
            ClearTextField(fDesc?.GetValue(inst));

            // Disable canvas-like component via property reflection: .enabled = false
            DisableEnabledProperty(fCanvasLike?.GetValue(inst));

            if (inst is MonoBehaviour mb && mb.gameObject != null)
                mb.gameObject.SetActive(false);
        }

        private static void ClearTextField(object textObj)
        {
            if (textObj == null) return;

            // TMP_Text + UnityEngine.UI.Text both expose 'text'
            var prop = textObj.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            if (prop != null && prop.CanWrite)
                prop.SetValue(textObj, "");
        }

        private static void DisableEnabledProperty(object component)
        {
            if (component == null) return;

            var prop = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
                prop.SetValue(component, false);
        }
    }
}
