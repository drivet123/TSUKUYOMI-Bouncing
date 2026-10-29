using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace BounceV5;

/// <summary>
/// Native in-game settings integration. Polls for ACTIVE option items
/// (UIOption_Item_Toggle): where one exists, an options screen is open, and
/// mod entries are cloned from the game's own templates and appended next to
/// the live items. Never reflects on Unity message methods of interop types -
/// GetMethod("Awake") deadlocks the main thread on Il2CppInterop wrappers.
/// </summary>
public static class NativeMenuSystem
{
    private static bool _membersReady;
    private static bool _loggedTick;

    private static Type _searchType;   // handle for FindObjectsOfType (any matching type works)
    private static Type _rtToggle;     // RUNTIME type of a live toggle item
    private static Type _soundType;
    private static Type _buttonType;
    private static Type _ioItemType;

    private static MethodInfo _miToggleSetup;   // (string, bool, Action<bool>, Action<IOptionItem>)
    private static MethodInfo _miSoundSetup;    // (string, float, UnityAction<float>, Action<IOptionItem>)
    private static MethodInfo _miButtonSetup;   // (string, string, Action, Action<IOptionItem>)
    private static MethodInfo _miSetButtonText;

    private static float _pollTimer;
    private static GameObject _builtMarker;

    public static void Tick()
    {
        try
        {
            if (!_loggedTick) { _loggedTick = true; Plugin.Logger.LogInfo("[BounceV5] NativeMenu Tick alive."); }
            PollPanels();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] NativeMenu tick failed: {ex.Message}");
        }
    }

    // Derive EVERYTHING from a LIVE toggle item's runtime type. A same-named
    // type can exist in more than one assembly; reflecting on the wrong one
    // gives "Object does not match target type" on MethodInfo.Invoke.
    private static bool EnsureRuntimeTypes(Component firstLive)
    {
        Type rt = firstLive.GetType();
        if (_membersReady && _rtToggle == rt) return true;
        try
        {
            _rtToggle = rt;
            Assembly asm = rt.Assembly;
            _soundType = asm.GetType("App.UIOption_Item_Sound", false);
            _buttonType = asm.GetType("App.UIOption_Item_Button", false);
            _ioItemType = asm.GetType("App.IOptionItem", false);
            _miToggleSetup = PickSetup(rt, 4);
            _miSoundSetup = _soundType != null ? PickSetup(_soundType, 4) : null;
            _miButtonSetup = _buttonType != null ? PickSetup(_buttonType, 4) : null;
            _miSetButtonText = _buttonType?.GetMethod("SetButtonText");
            _membersReady = _miToggleSetup != null && _miSoundSetup != null && _miButtonSetup != null;
            Plugin.Logger.LogInfo($"[BounceV5] NativeMenu runtime types from {asm.GetName().Name}: ok={_membersReady}");
            if (!_membersReady)
                Plugin.Logger.LogWarning($"[BounceV5] Setup methods: T={_miToggleSetup != null} S={_miSoundSetup != null} B={_miButtonSetup != null}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] NativeMenu runtime init failed: {ex.Message}");
        }
        return _membersReady;
    }

    private static MethodInfo PickSetup(Type t, int paramCount)
    {
        foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name == "Setup" && m.GetParameters().Length == paramCount)
                return m;
        }
        return null;
    }

    // ---------------------------------------------------------------- polling

    private static void PollPanels()
    {
        _pollTimer -= Time.unscaledDeltaTime;
        if (_pollTimer > 0f) return;
        _pollTimer = 2f;
        try
        {
            bool markerAlive = _builtMarker != null && _builtMarker.activeInHierarchy;
            if (markerAlive) return;

            _searchType ??= V5Manager.ResolveType("App.UIOption_Item_Toggle");
            if (_searchType == null) return;
            var live = V5Manager.FindAll(_searchType); // active instances only
            if (live == null || live.Length == 0) return;
            var firstLive = live[0].TryCast<Component>();
            if (firstLive == null) return;
            if (!EnsureRuntimeTypes(firstLive)) return;

            Transform container = firstLive.transform.parent;
            Transform root = firstLive.transform;
            while (root.parent != null) root = root.parent;

            Component toggleTemplate = FindItemUnder(root, _rtToggle, true) ?? firstLive;
            Component soundTemplate = FindItemUnder(root, _soundType, true) ?? FindItemUnder(root, _soundType, false);
            Component buttonTemplate = FindItemUnder(root, _buttonType, true) ?? FindItemUnder(root, _buttonType, false);
            if (toggleTemplate == null || soundTemplate == null || buttonTemplate == null)
            {
                Plugin.Logger.LogWarning("[BounceV5] option templates not found");
                return;
            }

            Plugin.Logger.LogInfo("[BounceV5] options screen visible - building native mod entries...");
            BuildModItems(container, toggleTemplate, soundTemplate, buttonTemplate);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] PollPanels failed: {ex.Message}");
        }
    }

    private static Component FindItemUnder(Transform root, Type itemType, bool inactive)
    {
        try
        {
            var arr = root.GetComponentsInChildren(Il2CppType.From(itemType), true);
            if (arr == null) return null;
            foreach (var c in arr)
            {
                if (c == null) continue;
                if (c.gameObject.activeInHierarchy != inactive) continue;
                return c;
            }
        }
        catch (Exception) { }
        return null;
    }

    // ---------------------------------------------------------------- item builders

    private static readonly MethodInfo _miConvertDelegate = typeof(Il2CppInterop.Runtime.DelegateSupport)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .FirstOrDefault(m => m.Name == "ConvertDelegate" && m.IsGenericMethodDefinition &&
                             m.GetParameters().Length == 1);

    private static object ToIl2Cpp(Type ilType, Delegate managed)
    {
        try
        {
            if (_miConvertDelegate == null) return null;
            return _miConvertDelegate.MakeGenericMethod(ilType).Invoke(null, new object[] { managed });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object IoSelectDelegate()
    {
        Type actionIo = typeof(Il2CppSystem.Action<>).MakeGenericType(_ioItemType);
        return ToIl2Cpp(actionIo, new Action<object>(_ => { }));
    }

    private static void AddToggle(Transform parent, Component template,
        string title, Func<bool> get, Action<bool> set)
    {
        try
        {
            var clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
            var comp = clone.GetComponent(Il2CppType.From(_rtToggle));
            if (comp == null) return;
            if (_builtMarker == null) _builtMarker = clone;

            object onToggle = ToIl2Cpp(typeof(Il2CppSystem.Action<bool>), new Action<bool>(v =>
            {
                set(v);
                Plugin.Config_Save();
            }));
            _miToggleSetup.Invoke(comp, new object[] { title, get(), onToggle, IoSelectDelegate() });
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] native toggle failed: {ex.Message}");
        }
    }

    private static void AddSlider(Transform parent, Component template,
        string title, Func<float> get, Action<float> set, float min, float max)
    {
        try
        {
            var clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
            var comp = clone.GetComponent(Il2CppType.From(_soundType));
            if (comp == null) return;
            if (_builtMarker == null) _builtMarker = clone;

            var onValue = Il2CppInterop.Runtime.DelegateSupport
                .ConvertDelegate<UnityAction<float>>(new Action<float>(v =>
                {
                    set(Mathf.Clamp(v, min, max));
                    Plugin.Config_Save();
                }));
            _miSoundSetup.Invoke(comp, new object[] { title, Mathf.InverseLerp(min, max, get()), onValue, IoSelectDelegate() });
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] native slider failed: {ex.Message}");
        }
    }

    private static Component _phaseButtonComp;

    private static void AddButton(Transform parent, Component template,
        string title, string btnText, Action onClick, bool trackAsPhaseButton)
    {
        try
        {
            var clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
            var comp = clone.GetComponent(Il2CppType.From(_buttonType));
            if (comp == null) return;
            if (_builtMarker == null) _builtMarker = clone;

            object onClickIl = ToIl2Cpp(typeof(Il2CppSystem.Action), onClick);
            _miButtonSetup.Invoke(comp, new object[] { title, btnText, onClickIl, IoSelectDelegate() });
            if (trackAsPhaseButton) _phaseButtonComp = comp;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] native button failed: {ex.Message}");
        }
    }

    private static string PhaseLabel()
    {
        string p = Plugin.KillFxPhase.Value;
        return p == "auto" ? "自动（未校准）" : p;
    }

    private static void CyclePhaseFromMenu()
    {
        ModUi.CyclePhasePublic();
        try
        {
            _miSetButtonText?.Invoke(_phaseButtonComp, new object[] { PhaseLabel() });
        }
        catch (Exception) { }
    }

    private static void BuildModItems(Transform parent, Component toggleTemplate, Component soundTemplate, Component buttonTemplate)
    {
        AddToggle(parent, toggleTemplate, "MOD 弹跳总开关",
            () => Plugin.Enabled.Value, v => Plugin.Enabled.Value = v);
        AddToggle(parent, toggleTemplate, "MOD DJ 音乐",
            () => Plugin.PlayMusic.Value, v => Plugin.PlayMusic.Value = v);
        AddToggle(parent, toggleTemplate, "MOD 用DJ替换游戏BGM",
            () => Plugin.ReplaceBGM.Value, v => Plugin.ReplaceBGM.Value = v);
        AddToggle(parent, toggleTemplate, "MOD 弹跳大图",
            () => Plugin.BounceAllLargeImages.Value, v => Plugin.BounceAllLargeImages.Value = v);
        AddToggle(parent, toggleTemplate, "MOD 底部锚定",
            () => Plugin.AnchorBottom.Value, v => Plugin.AnchorBottom.Value = v);
        AddToggle(parent, toggleTemplate, "MOD 击杀特效",
            () => Plugin.KillFxEnabled.Value, v => Plugin.KillFxEnabled.Value = v);

        AddSlider(parent, soundTemplate, "MOD DJ音量",
            () => Plugin.MusicVolume.Value, v => Plugin.MusicVolume.Value = v, 0f, 1f);
        AddSlider(parent, soundTemplate, "MOD 音乐节拍BPM",
            () => Plugin.MusicBPM.Value, v => Plugin.MusicBPM.Value = (float)Math.Round(v), 60f, 200f);
        AddSlider(parent, soundTemplate, "MOD 弹跳频率",
            () => Plugin.BounceFrequency.Value, v => Plugin.BounceFrequency.Value = v, 0.5f, 5f);
        AddSlider(parent, soundTemplate, "MOD 压扁强度",
            () => Plugin.SquashAmount.Value, v => Plugin.SquashAmount.Value = v, 0f, 0.4f);
        AddSlider(parent, soundTemplate, "MOD 拉伸强度",
            () => Plugin.StretchAmount.Value, v => Plugin.StretchAmount.Value = v, 0f, 0.4f);
        AddSlider(parent, soundTemplate, "MOD 弹起高度",
            () => Plugin.HopHeight.Value, v => Plugin.HopHeight.Value = v, 0f, 60f);
        AddSlider(parent, soundTemplate, "MOD 击杀音量",
            () => Plugin.KillFxVolume.Value, v => Plugin.KillFxVolume.Value = v, 0f, 1f);

        AddButton(parent, buttonTemplate, "MOD 击杀大脸形态", PhaseLabel(), CyclePhaseFromMenu, true);
        AddButton(parent, buttonTemplate, "MOD 素材编辑器", "打开", ToggleBrowser, false);
    }

    // ---------------------------------------------------------------- native asset browser

    private static GameObject _browserRoot;
    private static GameObject _browserContent;
    private static bool _browserOpen;
    private const int MaxRows = 800;

    public static bool BrowserOpen => _browserOpen;

    public static void ToggleBrowser()
    {
        try
        {
            if (_browserRoot == null) BuildBrowser();
            _browserOpen = !_browserOpen;
            _browserRoot.SetActive(_browserOpen);
            if (_browserOpen) RefreshBrowser();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] browser toggle failed: {ex.Message}");
        }
    }

    private static GameObject MakeButton(Transform parent, string text, Action onClick,
        float y, float h, float xLeft, float xRight)
    {
        var go = new GameObject("Btn_" + text);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(xLeft, y);
        rt.sizeDelta = new Vector2(xRight - xLeft, h);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.25f, 0.25f, 0.3f, 1f);
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(Il2CppInterop.Runtime.DelegateSupport
            .ConvertDelegate<UnityAction>(new Action(onClick)));

        var txtGo = new GameObject("Text");
        txtGo.AddComponent<RectTransform>();
        txtGo.transform.SetParent(go.transform, false);
        var trt = txtGo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;
        var txt = txtGo.AddComponent<Text>();
        txt.text = text;
        txt.fontSize = 20;
        txt.color = Color.white;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return go;
    }

    private static Text MakeLabel(Transform parent, string text, float y, float h, float xLeft, float xRight, int size)
    {
        var go = new GameObject("Label");
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(xLeft, y);
        rt.sizeDelta = new Vector2(xRight - xLeft, h);
        var txt = go.AddComponent<Text>();
        txt.text = text;
        txt.fontSize = size;
        txt.color = Color.white;
        txt.alignment = TextAnchor.MiddleLeft;
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return txt;
    }

    private static void BuildBrowser()
    {
        var root = new GameObject("BounceV5_Browser");
        root.AddComponent<RectTransform>();
        UnityEngine.Object.DontDestroyOnLoad(root);
        root.hideFlags = HideFlags.HideAndDontSave;

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;
        root.AddComponent<GraphicRaycaster>();

        var crt = root.GetComponent<RectTransform>();
        crt.anchorMin = Vector2.zero;
        crt.anchorMax = Vector2.one;
        crt.offsetMin = Vector2.zero;
        crt.offsetMax = Vector2.zero;

        var bg = new GameObject("Bg");
        bg.AddComponent<RectTransform>();
        bg.transform.SetParent(root.transform, false);
        var brt = bg.GetComponent<RectTransform>();
        brt.anchorMin = Vector2.zero;
        brt.anchorMax = Vector2.one;
        brt.offsetMin = Vector2.zero;
        brt.offsetMax = Vector2.zero;
        var bimg = bg.AddComponent<Image>();
        bimg.color = new Color(0f, 0f, 0f, 0.92f);
        bimg.raycastTarget = true;

        MakeLabel(root.transform, "素材编辑器（点击行切换 跳 / 不跳，改动即时保存）", -20, 40, 30, 1200, 24);
        MakeButton(root.transform, "全部跳", () => SetAll(false), -70, 40, 30, 180);
        MakeButton(root.transform, "全部不跳", () => SetAll(true), -70, 40, 190, 360);
        MakeButton(root.transform, "重新扫描", () => TargetSystem.RequestRescan(), -70, 40, 370, 530);
        MakeButton(root.transform, "关闭", ToggleBrowser, -70, 40, 1300, 1480);

        var scroll = new GameObject("Scroll");
        scroll.AddComponent<RectTransform>();
        scroll.transform.SetParent(root.transform, false);
        var srt = scroll.GetComponent<RectTransform>();
        srt.anchorMin = new Vector2(0f, 0f);
        srt.anchorMax = new Vector2(1f, 1f);
        srt.offsetMin = new Vector2(30, 30);
        srt.offsetMax = new Vector2(-30, -90);

        var viewport = new GameObject("Viewport");
        viewport.AddComponent<RectTransform>();
        viewport.transform.SetParent(scroll.transform, false);
        var vrt = viewport.GetComponent<RectTransform>();
        vrt.anchorMin = Vector2.zero;
        vrt.anchorMax = Vector2.one;
        vrt.offsetMin = Vector2.zero;
        vrt.offsetMax = Vector2.zero;
        viewport.AddComponent<RectMask2D>();

        var content = new GameObject("Content");
        content.AddComponent<RectTransform>();
        content.transform.SetParent(viewport.transform, false);
        var crt2 = content.GetComponent<RectTransform>();
        crt2.anchorMin = new Vector2(0f, 1f);
        crt2.anchorMax = new Vector2(1f, 1f);
        crt2.pivot = new Vector2(0.5f, 1f);
        crt2.offsetMin = Vector2.zero;
        crt2.offsetMax = Vector2.zero;
        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 2f;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlHeight = false;
        vlg.childControlWidth = true;
        var csf = content.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var sr = scroll.AddComponent<ScrollRect>();
        sr.content = crt2;
        sr.viewport = vrt;
        sr.horizontal = false;
        sr.scrollSensitivity = 30f;

        _browserRoot = root;
        _browserContent = content;
        root.SetActive(false);
    }

    private static void SetAll(bool exclude)
    {
        var entries = TargetSystem.Entries;
        for (int i = 0; i < entries.Count && i < MaxRows; i++)
            TargetSystem.SetEntryExcluded(entries[i], exclude);
        RefreshBrowser();
    }

    private static void RefreshBrowser()
    {
        try
        {
            TargetSystem.PruneEntries();
            var entries = TargetSystem.Entries;
            var content = _browserContent;
            for (int i = content.transform.childCount - 1; i >= 0; i--)
            {
                var child = content.transform.GetChild(i);
                UnityEngine.Object.Destroy(child.gameObject);
            }

            int rows = Math.Min(entries.Count, MaxRows);
            for (int i = 0; i < rows; i++)
            {
                var entry = entries[i];
                int idx = i;
                string label = (entry.Excluded ? "[禁] " : "[跳] ") + entry.Go.name;
                var row = MakeButton(content.transform, label, () =>
                {
                    var e = TargetSystem.Entries[idx];
                    TargetSystem.SetEntryExcluded(e, !e.Excluded);
                    RefreshBrowser();
                }, 0, 40, 0, 0);
                var rt = row.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = Vector2.zero;
            }
            Plugin.Logger.LogInfo($"[BounceV5] browser refreshed ({rows} rows).");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] browser refresh failed: {ex.Message}");
        }
    }
}
