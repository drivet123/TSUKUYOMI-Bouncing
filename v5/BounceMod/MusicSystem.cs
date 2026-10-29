using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NAudio.Wave;
using UnityEngine;

namespace BounceV5;

/// <summary>
/// DJ music (NAudio-decoded, looped) with game-BGM replacement and volume
/// sync. The AudioSource lives on its own DontDestroyOnLoad object; the BGM
/// suppression calls the game's SoundController via reflection and stops each
/// BGM at most once (hammering it broke the audio state machine in v3).
/// All state is static.
/// </summary>
public static class MusicSystem
{
    public static bool MusicPlaying { get; private set; }

    private static AudioSource _audio;
    private static AudioClip _musicClip;
    private static bool _musicLoadAttempted;

    private static float _gameBgmVolume = 1f;
    private static bool _replaceActive;
    private static string _suppressedBgm;
    private static string _lastStoppedBgm;
    private static Type _scType;
    private static object _scInstance;
    private static MethodInfo _miGetPlayingBGM;
    private static MethodInfo _miStopBGMFade;
    private static MethodInfo _miPlayBGMStr;
    private static MethodInfo _miStopNovelBGM;
    private static bool _scLogged;
    private static bool _patchesAttempted;

    public static void Tick()
    {
        try
        {
            bool shouldPlay = Plugin.Enabled.Value && Plugin.PlayMusic.Value;
            bool replace = shouldPlay && Plugin.ReplaceBGM.Value;

            if (!replace && _replaceActive)
                RestoreGameBgm();
            _replaceActive = replace;

            if (shouldPlay)
            {
                if (!MusicPlaying)
                    StartMusic();
                else if (_audio != null)
                    _audio.volume = Mathf.Clamp01(Plugin.MusicVolume.Value) * _gameBgmVolume;
            }
            else if (MusicPlaying)
            {
                StopMusic();
            }
        }
        catch (Exception ex)
        {
            V5Manager.LogErrThrottled("MusicTick", ex);
        }
    }

    public static void SuppressTick()
    {
        try
        {
            if (!_replaceActive) return;
            object sc = GetSoundController();
            if (sc == null) return;

            string current = _miGetPlayingBGM.Invoke(sc, null) as string;
            if (string.IsNullOrEmpty(current))
            {
                _lastStoppedBgm = null;
                return;
            }
            if (current == _lastStoppedBgm) return;
            _lastStoppedBgm = current;

            _miStopNovelBGM?.Invoke(sc, null);
            _suppressedBgm ??= current;
            _miStopBGMFade.Invoke(sc, new object[] { 0.3f });
            Plugin.Logger.LogInfo($"[BounceV5] Suppressed game BGM '{current}'.");
        }
        catch (Exception) { }
    }

    public static void InstallPatches()
    {
        if (_patchesAttempted) return;
        _scType = V5Manager.ResolveType("App.SoundController");
        if (_scType == null) return; // assemblies not ready yet
        _patchesAttempted = true;

        try
        {
            MethodInfo target = null;
            foreach (MethodInfo m in _scType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "ChangeVolume" && m.GetParameters().Length == 2)
                {
                    target = m;
                    break;
                }
            }
            if (target == null) return;

            var harmony = new Harmony("com.bouncev5.volumesync");
            var postfix = new HarmonyMethod(
                typeof(MusicSystem).GetMethod(nameof(ChangeVolumePostfix),
                    BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(target, postfix: postfix);
            Plugin.Logger.LogInfo("[BounceV5] Volume sync patch installed.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[BounceV5] Volume sync failed: {ex.Message}");
        }
    }

    private static void ChangeVolumePostfix(object __0, float __1)
    {
        try
        {
            if (Convert.ToInt32(__0) == 1) // OptionTypes.BGM == 1
                _gameBgmVolume = Mathf.Clamp01(__1);
        }
        catch (Exception) { }
    }

    private static void StartMusic()
    {
        try
        {
            if (_audio == null)
            {
                var go = new GameObject("BounceV5_Music");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _audio = go.AddComponent<AudioSource>();
                _audio.playOnAwake = false;
                _audio.loop = true;
                _audio.spatialBlend = 0f;
            }

            if (_musicClip == null && !_musicLoadAttempted)
            {
                _musicLoadAttempted = true;
                _musicClip = LoadMp3AsClip(Plugin.MusicFilePath.Value);
            }

            if (_musicClip == null)
            {
                Plugin.Logger.LogWarning("[BounceV5] Music clip unavailable; bouncing without music.");
                MusicPlaying = false;
                return;
            }

            _audio.clip = _musicClip;
            _audio.volume = Mathf.Clamp01(Plugin.MusicVolume.Value) * _gameBgmVolume;
            _audio.Play();
            MusicPlaying = true;
            Plugin.Logger.LogInfo("[BounceV5] DJ music started.");
        }
        catch (Exception ex)
        {
            MusicPlaying = false;
            Plugin.Logger.LogError($"[BounceV5] Failed to start music: {ex}");
        }
    }

    private static void StopMusic()
    {
        MusicPlaying = false;
        if (_audio != null && _audio.isPlaying)
            _audio.Stop();
    }

    public static AudioClip LoadMp3AsClip(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            Plugin.Logger.LogWarning($"[BounceV5] Music file not found: {path}");
            return null;
        }

        using WaveStream reader = CreateReader(path);
        ISampleProvider samples = reader.ToSampleProvider();
        WaveFormat fmt = samples.WaveFormat;

        long totalFloats = reader.Length / 4;
        var data = new float[totalFloats];
        int offset = 0;
        var buffer = new float[fmt.SampleRate * fmt.Channels];
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (offset + read > data.Length)
                Array.Resize(ref data, offset + read);
            Array.Copy(buffer, 0, data, offset, read);
            offset += read;
        }

        int lengthSamples = offset / fmt.Channels;
        var clip = AudioClip.Create("BounceV5_Track", lengthSamples, fmt.Channels, fmt.SampleRate, false);
        clip.SetData(data, 0);
        Plugin.Logger.LogInfo($"[BounceV5] Loaded music '{path}' ({fmt.SampleRate}Hz x{fmt.Channels}, {lengthSamples} samples).");
        return clip;
    }

    private static WaveStream CreateReader(string path)
    {
        try
        {
            return new AudioFileReader(path);
        }
        catch (Exception)
        {
            return new Mp3FileReader(path);
        }
    }

    private static object GetSoundController()
    {
        if (_scInstance != null)
        {
            var uo = _scInstance as UnityEngine.Object;
            if (uo == null)
                _scInstance = null;
        }

        if (_scInstance == null)
        {
            _scType ??= V5Manager.ResolveType("App.SoundController");
            if (_scType == null) return null;

            if (_miGetPlayingBGM == null)
            {
                _miGetPlayingBGM = _scType.GetMethod("GetPlayingBGM", Type.EmptyTypes);
                _miStopBGMFade = _scType.GetMethod("StopBGM", new[] { typeof(float) });
                _miPlayBGMStr = _scType.GetMethod("PlayBGM", new[] { typeof(string) });
                _miStopNovelBGM = _scType.GetMethod("StopNovelBGM", Type.EmptyTypes);
                if (_miGetPlayingBGM == null || _miStopBGMFade == null || _miPlayBGMStr == null)
                {
                    Plugin.Logger.LogWarning("[BounceV5] SoundController BGM methods not found; BGM replacement disabled.");
                    return null;
                }
            }

            Il2CppReferenceArray<UnityEngine.Object> found = V5Manager.FindAll(_scType);
            _scInstance = found != null && found.Length > 0 ? found[0] : null;
            if (_scInstance != null && !_scLogged)
            {
                _scLogged = true;
                Plugin.Logger.LogInfo("[BounceV5] SoundController instance found; BGM replacement ready.");
            }
        }

        return _scInstance;
    }

    private static void RestoreGameBgm()
    {
        try
        {
            object sc = GetSoundController();
            if (sc != null && !string.IsNullOrEmpty(_suppressedBgm))
            {
                _miPlayBGMStr.Invoke(sc, new object[] { _suppressedBgm });
                Plugin.Logger.LogInfo($"[BounceV5] Restored game BGM '{_suppressedBgm}'.");
            }
        }
        catch (Exception) { }
        _suppressedBgm = null;
        _lastStoppedBgm = null;
    }
}
