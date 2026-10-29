using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BounceV5;

/// <summary>
/// Discovers bounce targets and keeps the driver registry. Two mechanisms:
/// 1. one-shot scan cycles (startup / scene change / manual rescan), one
///    FindObjectsOfType step per tick so heavy scenes never hitch;
/// 2. Harmony hooks on Image/RawImage OnEnable bind objects the moment they
///    appear - no ongoing scanning during gameplay.
/// All state is static.
/// </summary>
public static class TargetSystem
{
    public class TargetEntry
    {
        public int Id;
        public GameObject Go;
        public string Path;
        public bool Excluded;
    }

    public static int AttachedCount => _attached.Count;

    private static readonly HashSet<int> _attached = new();
    private static readonly List<TargetEntry> _entries = new();
    private static readonly HashSet<int> _excludedIds = new();
    private static readonly HashSet<string> _excludedPaths = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, Type> _typeCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PropertyInfo> _propCache = new(StringComparer.Ordinal);
    private static List<string> _scanSteps = new();
    private static int _scanStepIndex;
    private static string _scanStepsKey = "";

    private static float _scanDueTimer = 3f;
    private static int _startupRescans = 2;
    private static int _scanCycleRemaining;
    private static float _scanTickTimer;

    // explore-map guard
    private static bool _exploreTypesResolved;
    private static readonly List<Type> _exploreTypes = new();

    public static bool IsExcluded(int id) => _excludedIds.Contains(id);

    public static void LoadExcludedPaths()
    {
        _excludedPaths.Clear();
        _excludedIds.Clear();
        try
        {
            foreach (string line in (Plugin.ExcludedPaths.Value ?? "").Split('\n'))
            {
                string p = line.Trim();
                if (p.Length > 0) _excludedPaths.Add(p);
            }
        }
        catch (Exception) { }
    }

    public static void OnSceneChanged()
    {
        if (_scanCycleRemaining == 0)
            _scanDueTimer = 2f;
    }

    public static void RequestRescan()
    {
        if (_scanCycleRemaining == 0)
            StartScanCycle();
    }

    public static void Tick()
    {
        try
        {
            if (_scanDueTimer > 0f)
            {
                _scanDueTimer -= Time.unscaledDeltaTime;
                if (_scanDueTimer <= 0f) StartScanCycle();
            }
            if (_scanCycleRemaining > 0)
            {
                _scanTickTimer += Time.unscaledDeltaTime;
                if (_scanTickTimer >= 0.25f)
                {
                    _scanTickTimer = 0f;
                    RunOneScanStep();
                    _scanCycleRemaining--;
                    if (_scanCycleRemaining == 0)
                    {
                        Plugin.Logger.LogInfo($"[BounceV5] scan cycle complete: {_attached.Count} targets tracked.");
                        if (_startupRescans > 0)
                        {
                            _startupRescans--;
                            _scanDueTimer = 12f;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            V5Manager.LogErrThrottled("TargetTick", ex);
        }
    }

    private static void StartScanCycle()
    {
        try
        {
            PruneAll();
            RebuildScanSteps();
            _scanStepIndex = 0;
            _scanCycleRemaining = _scanSteps.Count;
            Plugin.Logger.LogInfo($"[BounceV5] scan cycle started ({_scanSteps.Count} steps).");
        }
        catch (Exception ex)
        {
            V5Manager.LogErrThrottled("StartScanCycle", ex);
        }
    }

    private static void PruneAll()
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Go == null)
            {
                _attached.Remove(_entries[i].Id);
                _excludedIds.Remove(_entries[i].Id);
                _entries.RemoveAt(i);
            }
        }
    }

    private static void RebuildScanSteps()
    {
        string key = Plugin.TargetTypeNames.Value + "|" + Plugin.BounceAllLargeImages.Value;
        if (key == _scanStepsKey) return;
        _scanStepsKey = key;
        _scanSteps = new List<string>();
        _scanStepIndex = 0;
        foreach (string rawName in Plugin.TargetTypeNames.Value.Split(','))
        {
            string name = rawName.Trim();
            if (name.Length > 0) _scanSteps.Add(name);
        }
        if (Plugin.BounceAllLargeImages.Value)
        {
            _scanSteps.Add("img:UnityEngine.UI.Image");
            _scanSteps.Add("img:UnityEngine.UI.RawImage");
        }
    }

    private static void RunOneScanStep()
    {
        RebuildScanSteps();
        if (_scanSteps.Count == 0) return;
        if (_scanStepIndex >= _scanSteps.Count) _scanStepIndex = 0;

        string step = _scanSteps[_scanStepIndex++];
        int added = step.StartsWith("img:") ? ScanImageType(step.Substring(4)) : ScanTypeEntry(step);
        if (added > 0)
            Plugin.Logger.LogInfo($"[BounceV5] {_attached.Count} bounce targets tracked (+{added}).");
    }

    private static int ScanTypeEntry(string entry)
    {
        string typeName = entry;
        string fieldName = null;
        int sep = entry.IndexOf('>');
        if (sep > 0)
        {
            typeName = entry.Substring(0, sep).Trim();
            fieldName = entry.Substring(sep + 1).Trim();
        }

        Type managed = ResolveTypeCached(typeName);
        if (managed == null) return 0;

        Il2CppReferenceArray<UnityEngine.Object> found = V5Manager.FindAll(managed);
        if (found == null) return 0;

        int added = 0;
        foreach (UnityEngine.Object obj in found)
        {
            GameObject target = ResolveTarget(obj, managed, fieldName, entry);
            if (target == null) continue;
            added += TryAttach(target);
        }
        return added;
    }

    private static int ScanImageType(string typeName)
    {
        int count = 0;
        Type managed = ResolveTypeCached(typeName);
        if (managed == null) return 0;

        Il2CppReferenceArray<UnityEngine.Object> found = V5Manager.FindAll(managed);
        if (found == null) return 0;

        foreach (UnityEngine.Object obj in found)
        {
            Component comp = obj.TryCast<Component>();
            if (comp == null) continue;
            if (TryAttachImageCandidate(comp)) count++;
        }
        return count;
    }

    // Shared candidate filter for scan cycles and the OnEnable hook.
    public static bool TryAttachImageCandidate(Component comp)
    {
        try
        {
            if (comp == null) return false;

            RectTransform rt = comp.transform.TryCast<RectTransform>();
            if (rt == null) return false;

            Rect rect = rt.rect;
            if (rect.width < Plugin.ExtraImageMinSize.Value || rect.height < Plugin.ExtraImageMinSize.Value)
                return false;

            if (CoversScreen(comp, rect)) return false;
            if (IsUnderExploreMap(comp)) return false;

            string goName = comp.gameObject.name.ToLowerInvariant();
            foreach (string s in Plugin.ExcludeNameSubstrings.Value.Split(','))
            {
                string e = s.Trim().ToLowerInvariant();
                if (e.Length > 0 && goName.Contains(e)) return false;
            }

            return TryAttach(comp.gameObject) > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CoversScreen(Component comp, Rect rect)
    {
        try
        {
            Canvas canvas = comp.GetComponentInParent<Canvas>();
            float scale = 1f;
            if (canvas != null && canvas.scaleFactor > 0.001f)
                scale = canvas.scaleFactor;

            float cov = Plugin.MaxScreenCoverage.Value;
            return rect.width * scale > Screen.width * cov &&
                   rect.height * scale > Screen.height * cov;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsUnderExploreMap(Component comp)
    {
        try
        {
            if (!_exploreTypesResolved)
            {
                _exploreTypesResolved = true;
                foreach (string n in new[] { "App.UIExploreMap3D", "App.MapFloor3D", "App.ExploreMapInputHandler" })
                {
                    Type t = V5Manager.ResolveType(n);
                    if (t != null) _exploreTypes.Add(t);
                }
            }
            foreach (Type t in _exploreTypes)
            {
                if (comp.GetComponentInParent(Il2CppType.From(t)) != null)
                    return true;
            }

            Transform p = comp.transform;
            while (p != null)
            {
                string n = (p.name ?? "").ToLowerInvariant();
                if (n.Contains("exploremap") || n.Contains("mapfloor") ||
                    n.Contains("explore") || n.Contains("minimap") ||
                    n.Contains("uimap") || n.Contains("dungeonmap"))
                    return true;
                p = p.parent;
            }
        }
        catch (Exception) { }
        return false;
    }

    public static int TryAttach(GameObject target)
    {
        try
        {
            int id = target.GetInstanceID();
            if (_attached.Contains(id)) return 0;
            if (HasDriverInHierarchy(target.transform)) return 0;
            if (target.GetComponentsInChildren<BounceDriver>(true).Length > 0) return 0;

            target.AddComponent<BounceDriver>();
            _attached.Add(id);

            var entry = new TargetEntry
            {
                Id = id,
                Go = target,
                Path = V5Manager.GetTransformPath(target),
            };
            if (_excludedPaths.Contains(entry.Path))
            {
                entry.Excluded = true;
                _excludedIds.Add(id);
            }
            _entries.Add(entry);
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool HasDriverInHierarchy(Transform t)
    {
        while (t != null)
        {
            if (t.GetComponent<BounceDriver>() != null)
                return true;
            t = t.parent;
        }
        return false;
    }

    private static GameObject ResolveTarget(UnityEngine.Object obj, Type managed, string fieldName, string entryKey)
    {
        if (fieldName == null)
        {
            Component self = obj.TryCast<Component>();
            return self == null ? null : self.gameObject;
        }

        if (!_propCache.TryGetValue(entryKey, out PropertyInfo prop))
        {
            prop = managed.GetProperty(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _propCache[entryKey] = prop;
        }
        if (prop == null) return null;

        try
        {
            Component fieldComp = prop.GetValue(obj) as Component;
            return fieldComp == null ? null : fieldComp.gameObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Type ResolveTypeCached(string fullName)
    {
        if (_typeCache.TryGetValue(fullName, out Type cached))
            return cached;
        Type t = V5Manager.ResolveType(fullName);
        if (t != null) _typeCache[fullName] = t;
        return t;
    }

    // ---- user exclusions (F10 editor) ----

    public static IReadOnlyList<TargetEntry> Entries => _entries;

    public static void PruneEntries()
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Go == null)
                _entries.RemoveAt(i);
        }
    }

    public static void SetEntryExcluded(TargetEntry e, bool excluded)
    {
        e.Excluded = excluded;
        if (excluded)
        {
            _excludedIds.Add(e.Id);
            _excludedPaths.Add(e.Path);
        }
        else
        {
            _excludedIds.Remove(e.Id);
            _excludedPaths.Remove(e.Path);
        }
        Plugin.ExcludedPaths.Value = string.Join("\n", _excludedPaths);
        ModUi.MarkDirty();
    }

    // ---- event-driven attach hooks ----

    private static bool _attachHooksAttempted;

    public static void InstallAttachHooks()
    {
        if (_attachHooksAttempted) return;
        Type img = V5Manager.ResolveType("UnityEngine.UI.Image");
        Type raw = V5Manager.ResolveType("UnityEngine.UI.RawImage");
        if (img == null || raw == null) return; // interop assemblies not ready yet
        _attachHooksAttempted = true;

        try
        {
            var harmony = new Harmony("com.bouncev5.attach");
            var postfix = new HarmonyMethod(
                typeof(TargetSystem).GetMethod(nameof(ImageOnEnablePostfix),
                    BindingFlags.Static | BindingFlags.NonPublic));

            MethodInfo m1 = img.GetMethod("OnEnable",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo m2 = raw.GetMethod("OnEnable",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m1 != null) harmony.Patch(m1, postfix: postfix);
            if (m2 != null) harmony.Patch(m2, postfix: postfix);
            Plugin.Logger.LogInfo("[BounceV5] Attach hooks installed (Image/RawImage OnEnable).");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] Attach hooks failed ({ex.Message}).");
        }
    }

    private static void ImageOnEnablePostfix(object __instance)
    {
        try
        {
            var comp = __instance as Component;
            if (comp == null) return;
            TryAttachImageCandidate(comp);
        }
        catch (Exception) { }
    }
}
