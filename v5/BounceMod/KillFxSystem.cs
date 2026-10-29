using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BounceV5;

/// <summary>
/// Kill FX: the-rock sound + fullscreen shaking meme face. Hooks the game's
/// own BattleEventDelegate death calls; the detours are fully re-armed
/// (UnpatchID + fresh Patch) on every scene change because IL2CPP detours do
/// not survive scene switches reliably. Everything static; dedicated DDOL
/// objects for the audio source.
/// </summary>
public static class KillFxSystem
{
    public static float DisplayT => _killFxT;

    private static float _killFxT = -1f; // <0 inactive
    private const float Total = 1.8f;

    private static readonly Dictionary<string, Texture2D> _texs = new();
    private static Texture2D _texCurrent;
    private static readonly string[][] Faces =
    {
        new[] { "十六夜", "十六夜月.png" },
        new[] { "新月", "新月.png" },
        new[] { "满月", "满月.png" },
        new[] { "半月", "半月.png" },
    };

    private static AudioSource _sfx;
    private static AudioClip _clip;
    private static bool _clipAttempted;

    private static bool _hooksAttempted;
    private static int _deathLogCount;

    // Auto form detection: the killer's name is always "月读", so the current
    // form is tracked from the charaImageId the game displays (scenario
    // portraits) and from UIBoneCharaImage.ChangeChara calls, then mapped to
    // a moon phase through the KillFxCharaMap config.
    private static int _lastFormId = -1;
    private static Type _scenarioType;
    private static MethodInfo _miCurCharaImg;
    private static bool _scenarioTried;
    private static float _formPollTimer;
    private static bool _charaHookInstalled;
    private static readonly HashSet<int> _unmappedLogged = new();
    private static int _nameLogCount;

    public static void OnSceneChanged() => _hooksAttempted = false;

    public static void InstallHooks()
    {
        // Called from Tick so a failed assembly lookup retries next frame.
        if (_hooksAttempted) return;
        Type t = V5Manager.ResolveTypeBySuffix("BattleEventDelegate");
        if (t == null) return;
        _hooksAttempted = true;

        try
        {
            var harmony = new Harmony("com.bouncev5.killfx");
            Harmony.UnpatchID("com.bouncev5.killfx"); // fresh detours, never a stale no-op

            var postfix = new HarmonyMethod(
                typeof(KillFxSystem).GetMethod(nameof(OnDeathPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic));
            var prePostfix = new HarmonyMethod(
                typeof(KillFxSystem).GetMethod(nameof(OnPreDeathPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic));

            MethodInfo m1 = t.GetMethod("CallOnDeath");
            if (m1 != null) harmony.Patch(m1, postfix: postfix);
            MethodInfo m2 = t.GetMethod("CallOnPreDeath");
            if (m2 != null) harmony.Patch(m2, postfix: prePostfix);
            Plugin.Logger.LogInfo("[BounceV5] Kill hooks installed (CallOnDeath/CallOnPreDeath).");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] Kill hooks failed: {ex.Message}");
        }
    }

    // CallOnDeath(killer, killerCard, death, overkillDmg)
    private static void OnDeathPostfix(object __0, object __2)
    {
        try
        {
            int kType = IntProp(__0, "get_PlayerType"); // 1=Player 2=Enemy 3=Minion 4=Orb
            int dType = IntProp(__2, "get_PlayerType");
            if (_deathLogCount < 20)
            {
                _deathLogCount++;
                Plugin.Logger.LogInfo(
                    $"[BounceV5] death evt: kType={kType} dType={dType} formId={_lastFormId} killer='{Name(__0)}' death='{Name(__2)}'");
            }
            if (!Plugin.KillFxEnabled.Value) return;
            if (dType != 2) return;  // the dead one must be an enemy
            if (kType == 2) return;  // enemy-on-enemy: skip

            string phase = Plugin.KillFxPhase.Value;
            if (phase == "auto") phase = ResolveAutoPhase(Name(__0));
            Trigger(phase);
        }
        catch (Exception) { }
    }

    // CallOnPreDeath(killer, death, overkillDmg) - backup trigger source.
    private static void OnPreDeathPostfix(object __0, object __1)
    {
        try
        {
            if (!Plugin.KillFxEnabled.Value) return;
            if (IntProp(__1, "get_PlayerType") != 2) return;
            if (IntProp(__0, "get_PlayerType") == 2) return;
            string phase = Plugin.KillFxPhase.Value;
            if (phase == "auto") phase = ResolveAutoPhase(Name(__0));
            Trigger(phase);
        }
        catch (Exception) { }
    }

    private static string ResolvePhase(string killerName)
    {
        if (!string.IsNullOrEmpty(killerName))
        {
            if (killerName.Contains("十六夜")) return "十六夜";
            if (killerName.Contains("新月")) return "新月";
            if (killerName.Contains("满月") || killerName.Contains("満月")) return "满月";
            if (killerName.Contains("半月")) return "半月";
        }
        return null;
    }

    private static void Trigger(string phase)
    {
        Plugin.Logger.LogInfo($"[BounceV5] TriggerKillFx phase='{phase ?? "random"}' t={_killFxT:0.00}");
        if (_killFxT >= 0f && _killFxT < 0.6f)
        {
            Plugin.Logger.LogInfo("[BounceV5] TriggerKillFx skipped: cooldown");
            return;
        }
        Texture2D tex = GetTexture(phase);
        if (tex == null)
        {
            Plugin.Logger.LogWarning($"[BounceV5] TriggerKillFx aborted: no texture for '{phase ?? "random"}'");
            return;
        }
        _texCurrent = tex;
        _killFxT = 0f;
        Plugin.Logger.LogInfo($"[BounceV5] KILL FX phase='{phase ?? "random"}'");
        PlaySound();
    }

    public static void Tick()
    {
        InstallHooks();
        InstallCharaHook();

        _formPollTimer -= Time.unscaledDeltaTime;
        if (_formPollTimer <= 0f)
        {
            _formPollTimer = 1f;
            PollFormId();
        }

        if (_killFxT < 0f) return;
        _killFxT += Time.unscaledDeltaTime;
        if (_killFxT >= Total) _killFxT = -1f;
    }

    private static void InstallCharaHook()
    {
        if (_charaHookInstalled) return;
        Type bone = V5Manager.ResolveType("App.UIBoneCharaImage");
        Type area = V5Manager.ResolveType("App.UIScenarioImageArea");
        if (bone == null && area == null) return;

        try
        {
            var harmony = new Harmony("com.bouncev5.formlog");
            bool any = false;

            // Static scenario CGs: ShowCharaImage(int imageId, ...)
            MethodInfo show = area?.GetMethod("ShowCharaImage");
            if (show != null)
            {
                harmony.Patch(show, postfix: new HarmonyMethod(
                    typeof(KillFxSystem).GetMethod(nameof(CharaImagePostfix),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                any = true;
            }

            // Live bone-chara portraits (most in-game dialogs): ChangeChara(int)
            MethodInfo change = bone?.GetMethod("ChangeChara");
            if (change != null)
            {
                harmony.Patch(change, postfix: new HarmonyMethod(
                    typeof(KillFxSystem).GetMethod(nameof(CharaChangePostfix),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                any = true;
            }

            // Initial load: LoadCharas(List<int> charaIds, int page)
            MethodInfo load = bone?.GetMethod("LoadCharas");
            if (load != null)
            {
                harmony.Patch(load, postfix: new HarmonyMethod(
                    typeof(KillFxSystem).GetMethod(nameof(LoadCharasPostfix),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                any = true;
            }

            if (any)
            {
                _charaHookInstalled = true;
                Plugin.Logger.LogInfo("[BounceV5] Form-id hooks installed (scenario + bone chara).");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] Form-id hook failed: {ex.Message}");
        }
    }

    // LoadCharas(List<int> charaIds, int page): log the id batch once.
    private static bool _loggedLoadCharas;

    private static void LoadCharasPostfix(object __0, int __1)
    {
        try
        {
            var list = __0 as Il2CppSystem.Collections.Generic.List<int>;
            if (list == null) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count && i < 8; i++)
            {
                int id = list[i];
                sb.Append(id);
                if (i + 1 < list.Count) sb.Append(',');
                if (PhaseFromFormId(id) != null && id != _lastFormId)
                {
                    _lastFormId = id;
                    Plugin.Logger.LogInfo($"[BounceV5] protagonist form id={id} (LoadCharas)");
                }
            }
            if (!_loggedLoadCharas)
            {
                _loggedLoadCharas = true;
                Plugin.Logger.LogInfo($"[BounceV5] LoadCharas ids: {sb} page={__1}");
            }
        }
        catch (Exception) { }
    }

    // ShowCharaImage(int imageId, float duration, ImageAdjustment)
    // Only ids present in KillFxCharaMap count - NPC portraits must never
    // overwrite the tracked protagonist form.
    private static void CharaImagePostfix(int __0)
    {
        try
        {
            if (__0 <= 0) return;
            if (PhaseFromFormId(__0) == null) return;
            if (__0 != _lastFormId)
            {
                _lastFormId = __0;
                Plugin.Logger.LogInfo($"[BounceV5] protagonist form id={__0}");
            }
        }
        catch (Exception) { }
    }

    // ChangeChara(int charaId): bone-chara portraits.
    private static void CharaChangePostfix(int __0)
    {
        try
        {
            if (__0 <= 0) return;
            if (PhaseFromFormId(__0) == null) return;
            if (__0 != _lastFormId)
            {
                _lastFormId = __0;
                Plugin.Logger.LogInfo($"[BounceV5] protagonist form id={__0} (ChangeChara)");
            }
        }
        catch (Exception) { }
    }

    private static void PollFormId()
    {
        try
        {
            if (!_scenarioTried)
            {
                _scenarioTried = true;
                _scenarioType = V5Manager.ResolveType("App.UIScenarioImageArea");
                if (_scenarioType != null)
                    _miCurCharaImg = _scenarioType.GetMethod("get_CurrentCharaImageId");
            }
            if (_scenarioType == null || _miCurCharaImg == null) return;

            var arr = V5Manager.FindAll(_scenarioType);
            if (arr == null) return;
            foreach (UnityEngine.Object o in arr)
            {
                try
                {
                    int id = (int)_miCurCharaImg.Invoke(o, null);
                    if (id > 0 && id != _lastFormId)
                    {
                        _lastFormId = id;
                        Plugin.Logger.LogInfo($"[BounceV5] scenario form id={id}");
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    private static string PhaseFromFormId(int id)
    {
        try
        {
            foreach (string pair in (Plugin.KillFxCharaMap.Value ?? "").Split(';'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (int.TryParse(pair.Substring(0, eq).Trim(), out int pid) && pid == id)
                    return pair.Substring(eq + 1).Trim();
            }
        }
        catch (Exception) { }
        return null;
    }

    // auto mode: form id (mapped) -> name matching -> random fallback
    private static string ResolveAutoPhase(string killerName)
    {
        PollFormId(); // fresh read at kill time
        if (_lastFormId > 0)
        {
            string phase = PhaseFromFormId(_lastFormId);
            if (phase != null) return phase;
            if (_unmappedLogged.Add(_lastFormId))
                Plugin.Logger.LogInfo(
                    $"[BounceV5] kill form id={_lastFormId} unmapped - add it to KillFxCharaMap (e.g. \"{_lastFormId}=满月\")");
        }
        return ResolvePhase(killerName);
    }

    private static Texture2D GetTexture(string phase)
    {
        string key = phase ?? "随机";
        if (_texs.TryGetValue(key, out Texture2D cached) && cached != null)
            return cached;
        try
        {
            string folder = System.IO.Path.GetDirectoryName(Plugin.KillFxImagePath.Value);
            string path = null;
            if (phase != null && System.IO.Directory.Exists(folder))
            {
                foreach (string[] entry in Faces)
                {
                    if (entry[0] == phase)
                    {
                        string candidate = System.IO.Path.Combine(folder, entry[1]);
                        if (System.IO.File.Exists(candidate)) { path = candidate; break; }
                    }
                }
            }
            if (path == null && System.IO.Directory.Exists(folder))
            {
                var pngs = new System.IO.DirectoryInfo(folder).GetFiles("*.png");
                if (pngs.Length > 0)
                {
                    Array.Sort(pngs, (a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
                    path = pngs[0].FullName;
                }
            }
            if (path == null || !System.IO.File.Exists(path))
            {
                Plugin.Logger.LogWarning($"[BounceV5] KillFx image missing for '{key}' (folder: {folder})");
                return null;
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            UnityEngine.ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(path));
            _texs[key] = tex;
            Plugin.Logger.LogInfo($"[BounceV5] KillFx image for '{key}': {path} ({tex.width}x{tex.height})");
            return tex;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] KillFx texture load failed for '{key}': {ex.Message}");
            return null;
        }
    }

    private static void PlaySound()
    {
        try
        {
            if (_sfx == null)
            {
                var go = new GameObject("BounceV5_Sfx");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _sfx = go.AddComponent<AudioSource>();
                _sfx.playOnAwake = false;
                _sfx.spatialBlend = 0f;
            }
            if (_clip == null && !_clipAttempted)
            {
                _clipAttempted = true;
                string path = Plugin.KillFxSoundPath.Value;
                if (System.IO.File.Exists(path))
                    _clip = MusicSystem.LoadMp3AsClip(path);
                else
                    Plugin.Logger.LogWarning($"[BounceV5] Kill sound not found: {path}");
            }
            if (_clip != null)
                _sfx.PlayOneShot(_clip, Mathf.Clamp01(Plugin.KillFxVolume.Value));
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] Kill sound failed: {ex.Message}");
        }
    }

    public static void Draw()
    {
        if (_killFxT < 0f) return;
        Texture2D tex = _texCurrent;
        if (tex == null) return;

        float t = _killFxT;
        float w = Screen.width, h = Screen.height;
        float drawH = h * Mathf.Clamp(Plugin.KillFxSize.Value, 0.1f, 2f) * 1.15f;
        float drawW = drawH * ((float)tex.width / Mathf.Max(1, tex.height));
        float cx = w * 0.5f, cy = h * 0.5f;

        float alpha;
        if (t < 0.12f) alpha = t / 0.12f;
        else if (t > 1.15f) alpha = Mathf.Clamp01(1f - (t - 1.15f) / (Total - 1.15f));
        else alpha = 1f;

        float scale = 1f;
        if (t < 0.18f)
        {
            float p = t / 0.18f;
            scale = Mathf.Lerp(1.7f, 1f, Mathf.Sqrt(p));
        }

        float rot = 0f, jx = 0f, jy = 0f;
        if (t >= 0.18f && t < 1.15f)
        {
            float st = (t - 0.18f) * 25f;
            float damp = Mathf.Exp(-(t - 0.18f) * 2.2f);
            rot = Mathf.Sin(st) * 9f * damp;
            jx = Mathf.Sin(st * 1.31f) * 14f * damp;
            jy = Mathf.Cos(st * 1.7f) * 10f * damp;
        }

        Color oldColor = GUI.color;
        Matrix4x4 oldMat = GUI.matrix;
        GUI.color = new Color(1f, 1f, 1f, alpha * 0.8f);
        GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), new Vector2(cx, cy));
        GUIUtility.RotateAroundPivot(rot, new Vector2(cx, cy));
        GUI.DrawTexture(new Rect(cx - drawW / 2f + jx, cy - drawH / 2f + jy, drawW, drawH),
            tex, ScaleMode.StretchToFill, true);
        GUI.matrix = oldMat;
        GUI.color = oldColor;
    }

    private static string Name(object obj)
    {
        try
        {
            if (obj == null) return null;
            MethodInfo mi = obj.GetType().GetMethod("get_Name");
            return mi?.Invoke(obj, null) as string;
        }
        catch (Exception) { return null; }
    }

    private static int IntProp(object obj, string getter)
    {
        try
        {
            if (obj == null) return -1;
            MethodInfo mi = obj.GetType().GetMethod(getter);
            if (mi == null) return -1;
            return Convert.ToInt32(mi.Invoke(obj, null));
        }
        catch (Exception) { return -1; }
    }
}
