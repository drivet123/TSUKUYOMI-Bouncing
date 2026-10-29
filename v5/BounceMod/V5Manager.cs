using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BounceV5;

/// <summary>
/// The single orchestrator. All subsystem state is static (see the *System
/// classes), so behaviour can never diverge between object instances - the
/// "second battle" class of bugs is structurally impossible here.
/// A duplicate manager (if anything ever creates one) destroys itself.
/// </summary>
public class V5Manager : MonoBehaviour
{
    public V5Manager(IntPtr ptr) : base(ptr) { }

    public static V5Manager Instance { get; private set; }

    // ---- profiler / heartbeat ----
    private static readonly Stopwatch _profTotal = new();
    private static readonly Stopwatch _profSw = new();
    private static long _profUi, _profMusic, _profScan;
    private static int _slowFrameCount;
    private static float _lastSlowLog;
    private float _hb;

    private void Awake()
    {
        // Hard single-instance guarantee.
        if (Instance != null && Instance != this)
        {
            Plugin.Logger.LogWarning("[BounceV5] duplicate manager destroyed");
            Destroy(gameObject);
            return;
        }
        Instance = this;
        Plugin.Logger.LogInfo("[BounceV5] manager Awake.");
        TargetSystem.LoadExcludedPaths();
    }

    private void Update()
    {
        _profTotal.Restart();
        try
        {
            UpdateInternal();
        }
        catch (Exception ex)
        {
            LogErrThrottled("Update", ex);
        }
        long total = _profTotal.ElapsedMilliseconds;
        if (total > 150 && Time.unscaledTime - _lastSlowLog > 5f)
        {
            _lastSlowLog = Time.unscaledTime;
            _slowFrameCount++;
            Plugin.Logger.LogWarning(
                $"[BounceV5] SLOW FRAME {total}ms | ui={_profUi} music={_profMusic} targets={_profScan}");
        }
    }

    private static bool _sceneChecked;
    private static int _lastSceneHandle = -1;
    private float _sceneCheckTimer;
    private float _bgmSupTimer;

    private void UpdateInternal()
    {
        _profSw.Restart();
        _hb += Time.unscaledDeltaTime;
        if (_hb >= 60f)
        {
            _hb = 0f;
            Plugin.Logger.LogInfo(
                $"[BounceV5] heartbeat t={Time.unscaledTime:0} enabled={Plugin.Enabled.Value} " +
                $"targets={TargetSystem.AttachedCount} music={MusicSystem.MusicPlaying} " +
                $"slowFrames={_slowFrameCount} killFxT={KillFxSystem.DisplayT:0.00}");
        }

        // Hotkeys.
        if (KeyPressed(Key.F8, KeyCode.F8))
        {
            Plugin.Enabled.Value = !Plugin.Enabled.Value;
            Plugin.Config_Save();
            Plugin.Logger.LogInfo($"[BounceV5] Bouncing {(Plugin.Enabled.Value ? "ENABLED" : "DISABLED")}.");
        }
        if (KeyPressed(Key.F9, KeyCode.F9))
        {
            ModUi.PanelOpen = !ModUi.PanelOpen;
            ModUi.ApplyInputMode();
            if (!ModUi.PanelOpen) ModUi.FlushConfigSave();
        }
        if (KeyPressed(Key.F10, KeyCode.F10))
        {
            NativeMenuSystem.ToggleBrowser(); // native asset editor panel
        }

        ModUi.ForceCursorFree();

        _profUi = _profSw.ElapsedMilliseconds;
        _profSw.Restart();

        MusicSystem.Tick();
        MusicSystem.InstallPatches();

        _profMusic = _profSw.ElapsedMilliseconds;
        _profSw.Restart();

        // Scene-change detection drives hook re-arming and a rescan cycle.
        _sceneCheckTimer -= Time.unscaledDeltaTime;
        if (_sceneCheckTimer <= 0f)
        {
            _sceneCheckTimer = 2f;
            try
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (scene.handle != _lastSceneHandle)
                {
                    _lastSceneHandle = scene.handle;
                    KillFxSystem.OnSceneChanged();
                    TargetSystem.OnSceneChanged();
                }
            }
            catch (Exception) { }
        }

        TargetSystem.Tick();
        TargetSystem.InstallAttachHooks();
        NativeMenuSystem.Tick();

        _bgmSupTimer += Time.unscaledDeltaTime;
        if (_bgmSupTimer >= 0.5f)
        {
            _bgmSupTimer = 0f;
            MusicSystem.SuppressTick();
        }

        KillFxSystem.Tick();
        _profScan = _profSw.ElapsedMilliseconds;
    }

    private void OnGUI()
    {
        try
        {
            KillFxSystem.Draw();
            ModUi.DrawWindows();
        }
        catch (Exception ex)
        {
            LogErrThrottled("OnGUI", ex);
        }
    }

    private static bool KeyPressed(Key key, KeyCode legacy)
    {
        try
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                UnityEngine.InputSystem.Controls.KeyControl kc =
                    key == Key.F8 ? kb.f8Key :
                    key == Key.F9 ? kb.f9Key : kb.f10Key;
                return kc != null && kc.wasPressedThisFrame;
            }
        }
        catch (Exception) { }

        try { return Input.GetKeyDown(legacy); }
        catch (Exception) { return false; }
    }

    // ---- shared helpers ----

    private static string _lastErrKey = "";
    private static float _lastErrTime;

    public static void LogErrThrottled(string where, Exception ex)
    {
        string key = where + ex.GetType().Name + ex.Message;
        if (key == _lastErrKey && Time.unscaledTime - _lastErrTime < 5f) return;
        _lastErrKey = key;
        _lastErrTime = Time.unscaledTime;
        Plugin.Logger.LogError($"[BounceV5] {where} failed: {ex}");
    }

    public static Il2CppReferenceArray<UnityEngine.Object> FindAll(Type managed)
    {
        try
        {
            return UnityEngine.Object.FindObjectsOfType(Il2CppType.From(managed), false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Type ResolveType(string fullName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t;
            try { t = asm.GetType(fullName, false); }
            catch (Exception) { continue; }
            if (t != null) return t;
        }
        return null;
    }

    public static Type ResolveTypeBySuffix(string suffix)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (Exception) { continue; }
            foreach (Type t in types)
            {
                if (t.FullName != null && t.FullName.EndsWith(suffix, StringComparison.Ordinal))
                    return t;
            }
        }
        return null;
    }

    public static string GetTransformPath(GameObject go)
    {
        try
        {
            string path = go.name;
            Transform t = go.transform.parent;
            while (t != null)
            {
                path = t.name + "/" + path;
                t = t.parent;
            }
            return path;
        }
        catch (Exception)
        {
            return go.name;
        }
    }

    public static float CurrentFreq()
    {
        float freq = Plugin.BounceFrequency.Value;
        if (Plugin.PlayMusic.Value && MusicSystem.MusicPlaying)
            freq = Plugin.MusicBPM.Value / 60f;
        return Mathf.Max(0.01f, freq);
    }
}
