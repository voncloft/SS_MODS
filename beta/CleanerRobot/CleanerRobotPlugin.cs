using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using Il2CppInterop.Runtime.Injection;

namespace CleanerRobotPlugin
{
    [BepInPlugin("voncloft.cleanerrobot", "AlwaysCleanStore", "1.0.2")]
    public class CleanerRobotPlugin : BasePlugin
    {
        public override void Load()
        {
            try { ClassInjector.RegisterTypeInIl2Cpp<AlwaysCleanBehaviour>(); } catch { }

            GameObject go = new GameObject("AlwaysCleanBehaviour");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<AlwaysCleanBehaviour>();

            Log.LogInfo("[AlwaysCleanStore] Enabled. Store will be kept tidy 24/7.");
        }
    }

    public class AlwaysCleanBehaviour : MonoBehaviour
    {
        // ======= TUNING =======
        private const float ScanIntervalSeconds = 0.75f;
        private const int MaxCleanPerScan = 5;

        // We keep name needles focused on "mess" terms, not generic item terms.
        private static readonly string[] NameNeedles =
        {
            "trash","garbage","litter",
            "spill","stain","dirt","mess","puddle",
            "vomit","poop"
        };

        private static readonly string[] ComponentNeedles =
        {
            "trash","spill","dirt","stain","puddle","mess","litter","garbage","clean"
        };

        // Extra safety: avoid touching inventory/product-ish objects by common terms.
        private static readonly string[] NeverTouchNeedles =
        {
            "product","item","box","crate","shelf","rack","checkout","cashier",
            "customer","npc","staff","employee","cart","trolley",
            "money","bill","price","label","ui","canvas","eventsystem"
        };

        private float _nextScanAt;

        private void Update()
        {
            if (Time.unscaledTime < _nextScanAt) return;
            _nextScanAt = Time.unscaledTime + ScanIntervalSeconds;

            int cleaned = 0;

            // Only traverse active scene objects (much safer than FindObjectsOfTypeAll)
            GameObject[] roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; i < roots.Length && cleaned < MaxCleanPerScan; i++)
            {
                cleaned += WalkAndClean(roots[i], MaxCleanPerScan - cleaned);
            }
        }

        private int WalkAndClean(GameObject root, int budget)
        {
            if (root == null || budget <= 0) return 0;

            int cleaned = 0;

            // Check this object
            if (IsMessCandidate(root))
            {
                root.SetActive(false);
                cleaned++;
                if (cleaned >= budget) return cleaned;
            }

            // Recurse children
            Transform t = root.transform;
            if (t == null) return cleaned;

            int childCount = t.childCount;
            for (int i = 0; i < childCount && cleaned < budget; i++)
            {
                Transform ch = t.GetChild(i);
                if (ch == null) continue;

                GameObject go = ch.gameObject;
                if (go == null) continue;

                cleaned += WalkAndClean(go, budget - cleaned);
            }

            return cleaned;
        }

        private bool IsMessCandidate(GameObject go)
        {
            // Skip hidden/internal objects
            if (go.hideFlags != 0) return false;

            string n = SafeLower(go.name);
            if (n.Length == 0) return false;

            // Never touch obvious non-mess systems/objects
            if (MatchesAny(n, NeverTouchNeedles)) return false;

            // Mess by name
            if (MatchesAny(n, NameNeedles)) return true;

            // Mess by component type name
            Component[] comps = go.GetComponents<Component>();
            if (comps == null || comps.Length == 0) return false;

            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c == null) continue;

                string tn = SafeLower(c.GetType().Name);
                if (tn.Length == 0) continue;

                if (MatchesAny(tn, ComponentNeedles))
                    return true;
            }

            return false;
        }

        private static bool MatchesAny(string hay, string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
                if (hay.Contains(needles[i])) return true;
            return false;
        }

        private static string SafeLower(string s)
        {
            if (s == null) return "";
            return s.ToLowerInvariant();
        }
    }
}
