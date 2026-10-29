using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace BounceV5;

/// <summary>
/// BounceV5 entry point. All configuration lives here; the GUID is unchanged
/// from previous versions so existing config files carry over.
/// </summary>
[BepInPlugin("com.bouncev2.tsukuyomi", "BounceV5", "5.2.0")]
[BepInProcess("tsukuyomi.exe")]
public class Plugin : BasePlugin
{
    public static ManualLogSource Logger { get; private set; }
    public static Plugin Instance { get; private set; }

    public static void Config_Save() => Instance?.Config.Save();

    // ---- General ----
    public static ConfigEntry<bool> Enabled { get; private set; }
    public static ConfigEntry<float> ScanInterval { get; private set; } // legacy, unused

    // ---- Bounce ----
    public static ConfigEntry<float> BounceFrequency { get; private set; }
    public static ConfigEntry<float> SquashAmount { get; private set; }
    public static ConfigEntry<float> StretchAmount { get; private set; }
    public static ConfigEntry<float> HopHeight { get; private set; }
    public static ConfigEntry<bool> AnchorBottom { get; private set; }

    // ---- Targets ----
    public static ConfigEntry<string> TargetTypeNames { get; private set; }
    public static ConfigEntry<bool> BounceAllLargeImages { get; private set; }
    public static ConfigEntry<float> ExtraImageMinSize { get; private set; }
    public static ConfigEntry<float> MaxScreenCoverage { get; private set; }
    public static ConfigEntry<string> ExcludeNameSubstrings { get; private set; }
    public static ConfigEntry<string> ExcludedPaths { get; private set; }

    // ---- Music ----
    public static ConfigEntry<bool> PlayMusic { get; private set; }
    public static ConfigEntry<bool> ReplaceBGM { get; private set; }
    public static ConfigEntry<float> MusicBPM { get; private set; }
    public static ConfigEntry<float> MusicVolume { get; private set; }
    public static ConfigEntry<string> MusicFilePath { get; private set; }

    // ---- Kill FX ----
    public static ConfigEntry<bool> KillFxEnabled { get; private set; }
    public static ConfigEntry<string> KillFxImagePath { get; private set; }
    public static ConfigEntry<string> KillFxSoundPath { get; private set; }
    public static ConfigEntry<float> KillFxSize { get; private set; }
    public static ConfigEntry<float> KillFxVolume { get; private set; }
    public static ConfigEntry<string> KillFxPhase { get; private set; }
    public static ConfigEntry<string> KillFxCharaMap { get; private set; }

    public override void Load()
    {
        Logger = Log;
        Instance = this;

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Toggled in-game with F8.");
        ScanInterval = Config.Bind("General", "ScanInterval", 0.2f, "Legacy, unused.");

        BounceFrequency = Config.Bind("Bounce", "BounceFrequency", 2.0f,
            "Bounces per second when music is OFF.");
        SquashAmount = Config.Bind("Bounce", "SquashAmount", 0.15f,
            "Vertical flatten at landing (0-0.5).");
        StretchAmount = Config.Bind("Bounce", "StretchAmount", 0.12f,
            "Vertical stretch at apex (0-0.5).");
        HopHeight = Config.Bind("Bounce", "HopHeight", 18f,
            "Vertical hop offset in local units.");
        AnchorBottom = Config.Bind("Bounce", "AnchorBottom", true,
            "Pin the bottom edge in place while squashing.");

        TargetTypeNames = Config.Bind("Targets", "TargetTypeNames",
            "App.Card3D, App.Enemy3DImage, App.UIBoneCharaImage, App.UIScenarioItemImage, App.UICardPrint2, App.UIScenarioImageArea>m_charaImage",
            "Comma-separated target entries. 'App.SomeType' or 'App.SomeType>m_field'.");
        BounceAllLargeImages = Config.Bind("Targets", "BounceAllLargeImages", true,
            "Also bounce every large UI Image/RawImage.");
        ExtraImageMinSize = Config.Bind("Targets", "ExtraImageMinSize", 160f,
            "Minimum width AND height (rect units) for an Image to be picked up.");
        MaxScreenCoverage = Config.Bind("Targets", "MaxScreenCoverage", 0.7f,
            "Images covering more than this fraction in BOTH dims are skipped.");
        ExcludeNameSubstrings = Config.Bind("Targets", "ExcludeNameSubstrings",
            "bg,background,shadow,frame,line,mask,icon,map,minimap,exploremap,map3d,floor3d,grid,tile",
            "Comma-separated lowercase substrings; matching image names are skipped.");
        ExcludedPaths = Config.Bind("Targets", "ExcludedPaths", "",
            "User-excluded asset paths (managed by the F10 editor; one per line).");

        string assetsDir = Path.Combine(Paths.PluginPath, "BounceV5", "assets");
        string musicPath = Path.Combine(assetsDir, "DJ.mp3");
        string killFxImagePath = Path.Combine(assetsDir, "image", "killfx.png");
        string killFxSoundPath = Path.Combine(assetsDir, "the-rock-sound-effect.mp3");

        PlayMusic = Config.Bind("Music", "PlayMusic", true, "Play DJ music while bouncing.");
        ReplaceBGM = Config.Bind("Music", "ReplaceBGM", true,
            "Suppress the game's own BGM while DJ music plays.");
        MusicBPM = Config.Bind("Music", "MusicBPM", 128f, "BPM of the DJ track.");
        MusicVolume = Config.Bind("Music", "MusicVolume", 0.8f, "DJ volume (0-1).");
        MusicFilePath = Config.Bind("Music", "MusicFilePath", musicPath,
            "Absolute path to the DJ music file (mp3).");

        KillFxEnabled = Config.Bind("KillFX", "KillFxEnabled", true,
            "On player kill: play the sound and flash the meme face.");
        KillFxImagePath = Config.Bind("KillFX", "KillFxImagePath", killFxImagePath,
            "Meme face image (png). If missing, the newest png in the image folder is used.");
        KillFxSoundPath = Config.Bind("KillFX", "KillFxSoundPath", killFxSoundPath,
            "Kill sound file (mp3/wav).");
        KillFxSize = Config.Bind("KillFX", "KillFxSize", 1.0f,
            "Face height as a fraction of the screen height (1.0 = full screen).");
        KillFxVolume = Config.Bind("KillFX", "KillFxVolume", 1.0f, "Kill sound volume (0-1).");
        KillFxPhase = Config.Bind("KillFX", "KillFxPhase", "auto",
            "Which face to show on kill: auto / 十六夜 / 新月 / 满月 / 半月 (cycled in F9 or the options menu).");
        KillFxCharaMap = Config.Bind("KillFX", "KillFxCharaMap", "",
            "Auto-form calibration: pairs like \"3=满月;7=新月\" (form id -> phase). " +
            "Play each form once and read the 'kill form id=N' log lines to fill this.");

        bool migrated = MigrateLegacyPath(MusicFilePath,
            @"D:\program\steamapps\common\TSUKUYOMI\bouncingmod\DJ.mp3", musicPath);
        migrated |= MigrateLegacyPath(KillFxImagePath,
            @"D:\program\steamapps\common\TSUKUYOMI\bouncingmod\image\killfx.png", killFxImagePath);
        migrated |= MigrateLegacyPath(KillFxSoundPath,
            @"D:\program\steamapps\common\TSUKUYOMI\bouncingmod\the-rock-sound-effect.mp3", killFxSoundPath);
        if (migrated) Config.Save();

        ClassInjector.RegisterTypeInIl2Cpp<BounceDriver>();
        ClassInjector.RegisterTypeInIl2Cpp<V5Manager>();

        var go = new GameObject("BounceV5_Manager");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<V5Manager>();

        Logger.LogInfo("BounceV5 loaded.");
    }

    private static bool MigrateLegacyPath(ConfigEntry<string> entry, string legacyPath, string portablePath)
    {
        if (!string.Equals(entry.Value, legacyPath, StringComparison.OrdinalIgnoreCase))
            return false;
        entry.Value = portablePath;
        return true;
    }
}
