using System;
using System.Collections.Generic;
using UnityEngine;

namespace BounceV5;

/// <summary>
/// F9 settings panel + F10 asset browser + click shield. All IMGUI drawing is
/// wrapped; clicks inside the windows are consumed and, while any window is
/// open, a transparent fullscreen shield canvas intercepts game-UI raycasts
/// (the game's own EventSystem is never touched).
/// </summary>
public static class ModUi
{
    public static bool PanelOpen { get; set; }
    public static bool BrowserOpen { get; set; }

    private static Rect _winRect = new(40, 40, 560, 760);
    private static Rect _browserRect = new(60, 60, 1120, 700);
    private static bool _cfgDirty;
    private static float _cfgDirtyTimer;
    private static CursorLockMode _prevCursorLock = CursorLockMode.None;
    private static bool _prevCursorVisible = true;
    private static GameObject _clickShield;
    private static GUIStyle _stLabel, _stToggle, _stWin, _stBtn;
    private static bool _stylesInit;

    // browser state
    private static float _browserScroll;
    private static readonly List<int> _selected = new();
    private static readonly List<Rect> _cellRects = new();
    private static int _previewIndex = -1;
    private static bool _marqueeActive;
    private static Vector2 _marqueeStart, _marqueeEnd;
    private static string _browserFilter = "";

    private static readonly string[] PhaseCycle = { "auto", "十六夜", "新月", "满月", "半月" };

    public static void MarkDirty()
    {
        _cfgDirty = true;
        _cfgDirtyTimer = 0f;
    }

    public static void FlushConfigSave()
    {
        if (!_cfgDirty) return;
        _cfgDirty = false;
        Plugin.Config_Save();
    }

    public static void ApplyInputMode()
    {
        bool anyOpen = PanelOpen || BrowserOpen;
        if (anyOpen)
        {
            try
            {
                _prevCursorLock = Cursor.lockState;
                _prevCursorVisible = Cursor.visible;
            }
            catch (Exception) { }
        }
        else
        {
            try
            {
                Cursor.lockState = _prevCursorLock;
                Cursor.visible = _prevCursorVisible;
            }
            catch (Exception) { }
        }
        SetClickShield(anyOpen);
    }

    public static void ForceCursorFree()
    {
        if (!PanelOpen && !BrowserOpen) return;
        try
        {
            if (Cursor.lockState != CursorLockMode.None)
                Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible)
                Cursor.visible = true;
        }
        catch (Exception) { }
    }

    private static void SetClickShield(bool on)
    {
        try
        {
            if (on)
            {
                if (_clickShield == null)
                {
                    var go = new GameObject("BounceV5_ClickShield");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.HideAndDontSave;

                    var canvas = go.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = 32767;

                    go.AddComponent<UnityEngine.UI.GraphicRaycaster>();

                    var imgGo = new GameObject("ShieldImage");
                    imgGo.transform.SetParent(go.transform, false);
                    var img = imgGo.AddComponent<UnityEngine.UI.Image>();
                    img.color = new Color(0f, 0f, 0f, 0f); // invisible but raycastable
                    img.raycastTarget = true;
                    RectTransform rt = img.rectTransform;
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;

                    _clickShield = go;
                }
                if (!_clickShield.activeSelf)
                    _clickShield.SetActive(true);
            }
            else if (_clickShield != null && _clickShield.activeSelf)
            {
                _clickShield.SetActive(false);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] Click shield unavailable: {ex.Message}");
        }
    }

    public static void DrawWindows()
    {
        if (!PanelOpen && !BrowserOpen) return;
        EnsureStyles();

        Event ev = Event.current;
        if (ev != null &&
            ((PanelOpen && _winRect.Contains(ev.mousePosition)) ||
             (BrowserOpen && _browserRect.Contains(ev.mousePosition))) &&
            (ev.type == EventType.MouseDown || ev.type == EventType.MouseUp ||
             ev.type == EventType.ScrollWheel || ev.type == EventType.MouseDrag))
        {
            ev.Use();
        }

        if (PanelOpen)
        {
            _winRect = GUI.Window(730921, _winRect,
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<GUI.WindowFunction>(new Action<int>(DrawPanel)),
                "BounceV5 设置  [F9]", _stWin);
        }
        if (BrowserOpen)
        {
            _browserRect = GUI.Window(730922, _browserRect,
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<GUI.WindowFunction>(new Action<int>(DrawBrowser)),
                "BounceV5 素材编辑器  [F10]  左键框选/点选，Shift 加选", _stWin);
        }
    }

    private static void EnsureStyles()
    {
        if (_stylesInit) return;
        _stylesInit = true;
        try
        {
            _stLabel = new GUIStyle(GUI.skin.label) { fontSize = 20 };
            _stToggle = new GUIStyle(GUI.skin.toggle) { fontSize = 20 };
            _stWin = new GUIStyle(GUI.skin.window) { fontSize = 22 };
            _stBtn = new GUIStyle(GUI.skin.button) { fontSize = 14, alignment = TextAnchor.MiddleLeft, wordWrap = false };
        }
        catch (Exception)
        {
            _stLabel = GUI.skin.label;
            _stToggle = GUI.skin.toggle;
            _stWin = GUI.skin.window;
            _stBtn = GUI.skin.button;
        }
    }

    // ---------------------------------------------------------------- F9 panel

    private static void DrawPanel(int id)
    {
        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));

        ToggleRow(" 弹跳总开关 (F8)", Plugin.Enabled.Value, v => { Plugin.Enabled.Value = v; Plugin.Config_Save(); });
        ToggleRow(" DJ 音乐", Plugin.PlayMusic.Value, v => { Plugin.PlayMusic.Value = v; MarkDirty(); });
        ToggleRow(" 用 DJ 替换游戏 BGM", Plugin.ReplaceBGM.Value, v => { Plugin.ReplaceBGM.Value = v; MarkDirty(); });
        ToggleRow(" 弹跳大图（图鉴/章节/剧情）", Plugin.BounceAllLargeImages.Value, v => { Plugin.BounceAllLargeImages.Value = v; MarkDirty(); });
        ToggleRow(" 底部锚定（脚底着地弹跳）", Plugin.AnchorBottom.Value, v => { Plugin.AnchorBottom.Value = v; MarkDirty(); });
        ToggleRow(" 击杀特效 (音效+大脸)", Plugin.KillFxEnabled.Value, v => { Plugin.KillFxEnabled.Value = v; MarkDirty(); });
        if (GUILayout.Button($" 击杀大脸形态：{PhaseLabel()}（点击切换）", _stBtn, GUILayout.Height(30)))
            CyclePhase();

        GUILayout.Space(10);

        SliderRow("DJ 音量", () => Plugin.MusicVolume.Value, v => Plugin.MusicVolume.Value = v, 0f, 1f);
        SliderRow("音乐节拍 BPM（弹跳同步）", () => Plugin.MusicBPM.Value, v => Plugin.MusicBPM.Value = (float)Math.Round(v), 60f, 200f, "0");
        SliderRow("弹跳频率（无音乐时）", () => Plugin.BounceFrequency.Value, v => Plugin.BounceFrequency.Value = v, 0.5f, 5f);
        SliderRow("压扁强度", () => Plugin.SquashAmount.Value, v => Plugin.SquashAmount.Value = v, 0f, 0.4f);
        SliderRow("拉伸强度", () => Plugin.StretchAmount.Value, v => Plugin.StretchAmount.Value = v, 0f, 0.4f);
        SliderRow("弹起高度", () => Plugin.HopHeight.Value, v => Plugin.HopHeight.Value = v, 0f, 60f);
        SliderRow("大图最小尺寸", () => Plugin.ExtraImageMinSize.Value, v => Plugin.ExtraImageMinSize.Value = v, 60f, 400f);
        SliderRow("全屏覆盖率上限", () => Plugin.MaxScreenCoverage.Value, v => Plugin.MaxScreenCoverage.Value = v, 0.3f, 1f);
        SliderRow("击杀音效音量", () => Plugin.KillFxVolume.Value, v => Plugin.KillFxVolume.Value = v, 0f, 1f);

        GUILayout.EndVertical();
        GUI.DragWindow();

        if (_cfgDirty)
        {
            _cfgDirtyTimer += Time.unscaledDeltaTime;
            if (_cfgDirtyTimer > 1.5f)
            {
                _cfgDirty = false;
                Plugin.Config_Save();
            }
        }
    }

    private static string PhaseLabel()
    {
        string p = Plugin.KillFxPhase.Value;
        return p == "auto" ? "自动（按名字，未校准）" : p;
    }

    private static void CyclePhase()
    {
        int i = Array.IndexOf(PhaseCycle, Plugin.KillFxPhase.Value);
        if (i < 0) i = 0;
        Plugin.KillFxPhase.Value = PhaseCycle[(i + 1) % PhaseCycle.Length];
        Plugin.Config_Save();
    }

    /// <summary>Used by the native options menu button.</summary>
    public static void CyclePhasePublic() => CyclePhase();

    private static void ToggleRow(string label, bool val, Action<bool> onChange)
    {
        bool nv = GUILayout.Toggle(val, label, _stToggle, GUILayout.Height(32));
        if (nv != val) onChange(nv);
    }

    private static void SliderRow(string label, Func<float> get, Action<float> set, float min, float max, string fmt = "0.##")
    {
        GUILayout.Label($"{label}: {get().ToString(fmt)}", _stLabel, GUILayout.Height(28));
        float nv = GUILayout.HorizontalSlider(get(), min, max, GUILayout.Width(490));
        if (Mathf.Abs(nv - get()) > 0.0001f) { set(nv); MarkDirty(); }
    }

    // ---------------------------------------------------------------- F10 browser

    private static void DrawBrowser(int id)
    {
        TargetSystem.PruneEntries();
        var entries = TargetSystem.Entries;
        Event ev = Event.current;

        GUILayout.BeginVertical();

        GUILayout.BeginHorizontal();
        int total = entries.Count;
        int banned = 0;
        foreach (var e in entries) if (e.Excluded) banned++;
        GUILayout.Label($"素材 {total} 个 | 弹跳 {total - banned} | 不跳 {banned} | 选中 {_selected.Count}", _stLabel, GUILayout.Height(28));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("刷新", GUILayout.Width(90), GUILayout.Height(30))) TargetSystem.PruneEntries();
        if (GUILayout.Button("重新扫描", GUILayout.Width(110), GUILayout.Height(30))) TargetSystem.RequestRescan();
        if (GUILayout.Button("清除选择", GUILayout.Width(110), GUILayout.Height(30))) _selected.Clear();
        if (GUILayout.Button("选中项：跳", GUILayout.Width(120), GUILayout.Height(30))) ApplySelection(false);
        if (GUILayout.Button("选中项：不跳", GUILayout.Width(130), GUILayout.Height(30))) ApplySelection(true);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("筛选：", _stLabel, GUILayout.Width(70), GUILayout.Height(28));
        _browserFilter = GUILayout.TextField(_browserFilter ?? "", GUILayout.Width(360), GUILayout.Height(28));
        if (GUILayout.Button("X", GUILayout.Width(30), GUILayout.Height(30))) _browserFilter = "";
        GUILayout.Label("（名字包含即可；清空显示全部）", _stLabel, GUILayout.Height(28));
        GUILayout.EndHorizontal();

        Rect gridRect = GUILayoutUtility.GetRect(1040, 400, GUILayout.ExpandWidth(true));
        DrawBrowserGrid(gridRect, ev, entries);

        DrawPreviewPane(entries);

        GUILayout.EndVertical();
        GUI.DragWindow();
    }

    private static void ApplySelection(bool exclude)
    {
        var entries = TargetSystem.Entries;
        foreach (int idx in _selected)
        {
            if (idx >= 0 && idx < entries.Count)
                TargetSystem.SetEntryExcluded(entries[idx], exclude);
        }
    }

    private static void DrawBrowserGrid(Rect gridRect, Event ev, IReadOnlyList<TargetSystem.TargetEntry> entries)
    {
        string filter = (_browserFilter ?? "").Trim().ToLowerInvariant();
        _cellRects.Clear();
        for (int i = 0; i < entries.Count; i++) _cellRects.Add(new Rect(-1, -1, 0, 0));

        const int perRow = 4;
        const float rowH = 46f;
        float cellW = (gridRect.width - 10f * (perRow - 1)) / perRow;

        var visible = new List<int>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (filter.Length == 0 || entries[i].Go.name.ToLowerInvariant().Contains(filter))
                visible.Add(i);
        }

        float contentH = Mathf.Max(gridRect.height, Mathf.Ceil(visible.Count / (float)perRow) * rowH);
        _browserScroll = GUI.VerticalScrollbar(
            new Rect(gridRect.x + gridRect.width - 18, gridRect.y, 18, gridRect.height),
            _browserScroll, gridRect.height, 0, contentH);

        if (ev != null && gridRect.Contains(ev.mousePosition) && ev.type == EventType.ScrollWheel)
        {
            _browserScroll += ev.delta.y * 25f;
            ev.Use();
        }
        _browserScroll = Mathf.Clamp(_browserScroll, 0, Mathf.Max(0, contentH - gridRect.height));

        Rect clip = new Rect(gridRect.x, gridRect.y, gridRect.width - 20, gridRect.height);
        GUI.BeginGroup(clip);

        int firstRow = Mathf.Max(0, (int)(_browserScroll / rowH) - 1);
        int lastRow = (int)((_browserScroll + clip.height) / rowH) + 1;
        for (int r = firstRow; r <= lastRow; r++)
        {
            for (int c = 0; c < perRow; c++)
            {
                int vi = r * perRow + c;
                if (vi >= visible.Count) break;
                int idx = visible[vi];
                var entry = entries[idx];

                Rect cell = new Rect(c * (cellW + 10), r * rowH - _browserScroll, cellW, rowH - 6);
                _cellRects[idx] = new Rect(cell.x + clip.x, cell.y + clip.y, cell.width, cell.height);

                bool selected = _selected.Contains(idx);
                Color oldBg = GUI.backgroundColor;
                GUI.backgroundColor = selected ? Color.yellow :
                    entry.Excluded ? new Color(0.9f, 0.4f, 0.4f) : new Color(0.5f, 0.9f, 0.5f);
                string mark = entry.Excluded ? "[禁] " : "[跳] ";
                if (GUI.Button(cell, mark + entry.Go.name, _stBtn))
                {
                    if (ev != null && ev.shift) { if (!_selected.Remove(idx)) _selected.Add(idx); }
                    else { _selected.Clear(); _selected.Add(idx); }
                    _previewIndex = idx;
                }
                GUI.backgroundColor = oldBg;
            }
        }

        Vector2 mpos = ev != null ? ev.mousePosition : Vector2.zero;
        Rect localGrid = new Rect(clip.x, clip.y, clip.width, clip.height);
        if (ev != null && ev.type == EventType.MouseDown && ev.button == 0 && localGrid.Contains(mpos))
        {
            _marqueeActive = true;
            _marqueeStart = mpos;
            _marqueeEnd = mpos;
        }
        if (_marqueeActive && ev != null && (ev.type == EventType.MouseDrag || ev.type == EventType.MouseDown))
        {
            _marqueeEnd = mpos;
        }
        if (_marqueeActive && ev != null && ev.type == EventType.MouseUp)
        {
            _marqueeActive = false;
            Rect sel = NormalizedRect(_marqueeStart, _marqueeEnd);
            if (sel.width > 4 && sel.height > 4)
            {
                if (!(ev.shift)) _selected.Clear();
                for (int i = 0; i < _cellRects.Count; i++)
                {
                    if (_cellRects[i].width > 0 && sel.Overlaps(_cellRects[i]) &&
                        (filter.Length == 0 || entries[i].Go.name.ToLowerInvariant().Contains(filter)))
                    {
                        if (!_selected.Contains(i)) _selected.Add(i);
                    }
                }
            }
        }
        if (_marqueeActive)
        {
            Rect mr = NormalizedRect(_marqueeStart, _marqueeEnd);
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 0f, 0.35f);
            GUI.DrawTexture(new Rect(mr.x - clip.x, mr.y - clip.y, mr.width, mr.height), Texture2D.whiteTexture);
            GUI.color = old;
        }

        GUI.EndGroup();
    }

    private static Rect NormalizedRect(Vector2 a, Vector2 b)
    {
        return new Rect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
            Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
    }

    private static void DrawPreviewPane(IReadOnlyList<TargetSystem.TargetEntry> entries)
    {
        GUILayout.BeginHorizontal();

        Rect previewRect = GUILayoutUtility.GetRect(300, 220);
        GUI.Box(previewRect, "");
        if (_previewIndex >= 0 && _previewIndex < entries.Count)
        {
            var entry = entries[_previewIndex];
            Texture tex = TryGetEntryTexture(entry.Go);
            if (tex != null)
            {
                float tw = tex.width, th = tex.height;
                float scale = Mathf.Min((previewRect.width - 10) / tw, (previewRect.height - 10) / th, 1f);
                GUI.DrawTexture(new Rect(previewRect.x + 5, previewRect.y + 5, tw * scale, th * scale), tex, ScaleMode.StretchToFill);
            }
            else
            {
                GUI.Label(new Rect(previewRect.x + 10, previewRect.y + 10, 280, 30), "（无预览图）", _stLabel);
            }
        }

        GUILayout.BeginVertical(GUILayout.Width(560));
        if (_previewIndex >= 0 && _previewIndex < entries.Count)
        {
            var entry = entries[_previewIndex];
            GUILayout.Label($"名称：{entry.Go.name}", _stLabel, GUILayout.Height(26));
            GUILayout.Label($"路径：{entry.Path}", _stLabel, GUILayout.Height(26));
            GUILayout.Label(entry.Excluded ? "当前：不弹跳" : "当前：弹跳中", _stLabel, GUILayout.Height(26));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("此素材：跳", GUILayout.Width(140), GUILayout.Height(34))) TargetSystem.SetEntryExcluded(entry, false);
            if (GUILayout.Button("此素材：不跳", GUILayout.Width(140), GUILayout.Height(34))) TargetSystem.SetEntryExcluded(entry, true);
            GUILayout.EndHorizontal();
        }
        else
        {
            GUILayout.Label("点击或框选素材后可预览", _stLabel, GUILayout.Height(26));
        }
        GUILayout.Label("提示：绿色=弹跳 红色=不跳 黄色=选中；", _stLabel, GUILayout.Height(24));
        GUILayout.Label("空白处按住左键拖动 = 框选", _stLabel, GUILayout.Height(24));
        GUILayout.EndVertical();

        GUILayout.EndHorizontal();
    }

    private static Texture TryGetEntryTexture(GameObject go)
    {
        try
        {
            var img = go.GetComponent<UnityEngine.UI.Image>();
            if (img != null && img.sprite != null) return img.sprite.texture;
        }
        catch (Exception) { }
        try
        {
            var raw = go.GetComponent<UnityEngine.UI.RawImage>();
            if (raw != null) return raw.texture;
        }
        catch (Exception) { }
        return null;
    }
}
