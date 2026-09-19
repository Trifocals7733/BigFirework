using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using ModSettingsMenu.Api;
using UnityEngine;

namespace BigFireworks;

[BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
[BepInDependency(ModSettingsMenu.PluginInfo.PLUGIN_GUID, ">=1.1.0")]
public class Plugin : BasePlugin
{
    public const string PLUGIN_GUID = "walker.bigfireworks";
    public const string PLUGIN_NAME = "Big Firework";
    public const string PLUGIN_VERSION = "1.9.0";

    internal static new ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;

        var enabled = Config.Bind("General", "Enabled", true,
            new ConfigDescription("Master switch. Starts a volley only while this is on.",
                null, ModSettingsTags.Section("General", order: 10), ModSettingsTags.Entry(order: 10)));

        // Per-color burst counts and flare physical properties (Colors etc.)
        var green = BindMigrated("Colors etc.", "Green", "Colors", "Green", 2,
            new ConfigDescription("Green flare bursts per volley.",
                new AcceptableValueRange<int>(0, 20),
                ModSettingsTags.Section("Colors etc.", order: 20), ModSettingsTags.Entry(order: 10)));
        var yellow = BindMigrated("Colors etc.", "Yellow", "Colors", "Yellow", 2,
            new ConfigDescription("Yellow flare bursts per volley.",
                new AcceptableValueRange<int>(0, 20),
                ModSettingsTags.Entry(order: 20)));
        var blue = BindMigrated("Colors etc.", "Blue", "Colors", "Blue", 2,
            new ConfigDescription("Blue flare bursts per volley.",
                new AcceptableValueRange<int>(0, 20),
                ModSettingsTags.Entry(order: 30)));
        var red = BindMigrated("Colors etc.", "Red", "Colors", "Red", 2,
            new ConfigDescription("Red flare bursts per volley (untinted flare gun).",
                new AcceptableValueRange<int>(0, 20),
                ModSettingsTags.Entry(order: 40)));
        var teal = BindMigrated("Colors etc.", "Teal", "Colors", "Teal", 2,
            new ConfigDescription("Teal bursts per volley (fixed flare-run).",
                new AcceptableValueRange<int>(0, 20),
                ModSettingsTags.Entry(order: 50)));

        var launchHeight = BindMigratedMulti("Colors etc.", "LaunchHeight", new[] { ("Colors", "LaunchHeight"), ("Show", "LaunchHeight") }, 2f,
            new ConfigDescription("Spawn height above the anchor, in meters (Sky style only).",
                new AcceptableValueRange<float>(0f, 60f),
                ModSettingsTags.Entry(order: 60, sliderStep: 1d)));
        var spreadAngle = BindMigratedMulti("Colors etc.", "SpreadAngle", new[] { ("Colors", "SpreadAngle"), ("Show", "SpreadAngle") }, 8f,
            new ConfigDescription("Uniform cone around the aim, in degrees (Sky style only; Fountain always sprays wide).",
                new AcceptableValueRange<float>(0f, 45f),
                ModSettingsTags.Entry(order: 70, sliderStep: 1d)));
        var interval = BindMigratedMulti("Colors etc.", "Interval", new[] { ("Colors", "Interval"), ("Show", "Interval") }, 0.8f,
            new ConfigDescription("Seconds between bursts.",
                new AcceptableValueRange<float>(0.3f, 2f),
                ModSettingsTags.Entry(order: 80, sliderStep: 0.05d)));

        // Player Show settings and atmospheric lighting
        var target = BindMigrated("Player Show", "Target", "Show", "Target", ShowTarget.Self,
            new ConfigDescription("Self: every burst launches from you. Everyone: bursts round-robin across all players, one anchor per beat.",
                null, ModSettingsTags.Section("Player Show", order: 30), ModSettingsTags.Entry(order: 10)));
        var follow = Config.Bind("Player Show", "Follow", true,
            new ConfigDescription("Follow the host as they move around during the show (Self target only).",
                null, ModSettingsTags.Entry(order: 15)));
        var style = BindMigrated("Player Show", "Style", "Show", "Style", ShowStyle.Sky,
            new ConfigDescription("Sky: bursts spawn at LaunchHeight. Fountain: bursts spray from the anchor's feet with a wide cone.",
                null, ModSettingsTags.Entry(order: 20)));
        var flareSounds = Config.Bind("Player Show", "FlareSounds", true,
            new ConfigDescription("Play authentic flare explosion bang sounds when fireworks burst in the air.",
                null, ModSettingsTags.Entry(order: 25)));
        var fastNight = BindMigratedMulti("Player Show", "Fast Night", new[] { ("Player Show", "FastForwardNight"), ("Show", "FastForwardNight") }, true,
            new ConfigDescription("Fast-forward time to night when the show starts (sun sets fast, stars spin in).",
                null, ModSettingsTags.Entry(order: 30)));
        var nightHour = BindMigrated("Player Show", "NightHour", "Show", "NightHour", 0f,
            new ConfigDescription("Target night hour (0 = midnight, 21 = dusk, 22 = nightfall).",
                new AcceptableValueRange<float>(0f, 23.9f),
                ModSettingsTags.Entry(order: 40, sliderStep: 0.5d)));
        var transitionSpeed = BindMigrated("Player Show", "TransitionSpeed", "Show", "TransitionSpeed", 2.5f,
            new ConfigDescription("Seconds to fast-forward from daytime into night.",
                new AcceptableValueRange<float>(0.5f, 6.0f),
                ModSettingsTags.Entry(order: 50, sliderStep: 0.25d)));
        var restoreDaylight = BindMigrated("Player Show", "RestoreDaylight", "Show", "RestoreDaylight", true,
            new ConfigDescription("Smoothly fast-forward to sunrise / restore daytime when the show completes.",
                null, ModSettingsTags.Entry(order: 60)));
        var restoreDelay = Config.Bind("Player Show", "RestoreDelay", 5f,
            new ConfigDescription("Seconds to wait after the show completes before daylight restores.",
                new AcceptableValueRange<float>(0f, 30f),
                ModSettingsTags.Entry(order: 70, sliderStep: 0.5d)));
        var diagnostics = BindMigrated("Player Show", "Diagnostics", "Show", "Diagnostics", false,
            new ConfigDescription("Log server/emitter/burst counts once a second during a volley.",
                null, ModSettingsTags.Entry(order: 80)));

        // Firework Launcher in-world button hijack
        var launcherTrigger = BindMigrated("Firework Launcher", "Trigger", "Launcher", "Trigger", LauncherTrigger.Off,
            new ConfigDescription("What happens when YOU press a FireworkLauncher button (host only). Off: vanilla rocket. Accompany: rocket plus the selected show. Replace: show instead of the rocket (normal press suppressed).",
                null, ModSettingsTags.Section("Firework Launcher", order: 40), ModSettingsTags.Entry(order: 10)));
        var launcherShow = BindMigrated("Firework Launcher", "Show", "Launcher", "Show", LauncherShow.GrandFinale,
            new ConfigDescription("FlareVolley: flare show at the launcher. RocketSalvo: chain-fire other launchers for real. GrandFinale: both.",
                null, ModSettingsTags.Entry(order: 20)));
        var padFrom = BindMigrated("Firework Launcher", "PadFrom", "Launcher", "PadFrom", PadAnchor.Pressed,
            new ConfigDescription("Pad bursts launch from: the pressed launcher, or round-robin across all launchers.",
                null, ModSettingsTags.Entry(order: 30)));
        var salvoDelay = BindMigrated("Firework Launcher", "SalvoDelay", "Launcher", "SalvoDelay", 0.7f,
            new ConfigDescription("Seconds between salvo launches.",
                new AcceptableValueRange<float>(0.2f, 2f),
                ModSettingsTags.Entry(order: 40, sliderStep: 0.05d)));

        // Input controls placed at the bottom (order: 100)
        var toggleKey = BindMigratedMulti("Input", "Player Show", new[] { ("Input", "ToggleKey"), ("Input", "Player Show") }, KeyCode.F10,
            new ConfigDescription("Start a player firework show (host only).",
                null, ModSettingsTags.Section("Input", order: 100), ModSettingsTags.Entry(order: 10)));

        CleanupUnusedConfigs();

        ModSettingsRegistry.Register(PLUGIN_GUID, new ModSettingsModOptions
        {
            Name = "Big Firework",
            Description = "Synced sky firework show for the whole lobby (host).\n\nSpecial thanks to M4TR!X GG for the thumbnail and to Dexter for testing.",
            Version = PLUGIN_VERSION
        });

        Show.Bind(enabled, green, yellow, blue, red, teal, target, follow, style, flareSounds,
            launchHeight, spreadAngle, interval, diagnostics, toggleKey);
        LauncherDirector.Bind(launcherTrigger, launcherShow, padFrom, salvoDelay);
        SkyDirector.Bind(fastNight, nightHour, transitionSpeed, restoreDaylight, restoreDelay);

        AddComponent<Show>(); // BasePlugin.AddComponent also injects the type
        AddComponent<LauncherDirector>();
        AddComponent<SkyDirector>();
        new HarmonyLib.Harmony(PLUGIN_GUID).PatchAll();

        Log.LogInfo($"Big Firework v{PLUGIN_VERSION} loaded.");
    }

    private ConfigEntry<T> BindMigrated<T>(string section, string key, string oldSection, string oldKey, T defaultValue, ConfigDescription desc)
    {
        return BindMigratedMulti(section, key, new[] { (oldSection, oldKey) }, defaultValue, desc);
    }

    private ConfigEntry<T> BindMigratedMulti<T>(string section, string key, (string sec, string k)[] oldLocations, T defaultValue, ConfigDescription desc)
    {
        var targetDef = new ConfigDefinition(section, key);
        if (Config.ContainsKey(targetDef))
        {
            try
            {
                var entry = Config[targetDef];
                if (entry?.BoxedValue is T val) defaultValue = val;
            }
            catch { }
        }
        else if (oldLocations != null)
        {
            foreach (var (oldSec, oldKey) in oldLocations)
            {
                var oldDef = new ConfigDefinition(oldSec, oldKey);
                if (Config.ContainsKey(oldDef))
                {
                    try
                    {
                        var oldEntry = Config[oldDef];
                        if (oldEntry?.BoxedValue is T val) defaultValue = val;
                        Config.Remove(oldDef);
                        break;
                    }
                    catch { }
                }
            }
        }
        return Config.Bind(section, key, defaultValue, desc);
    }

    private void CleanupUnusedConfigs()
    {
        var stale = new[]
        {
            new ConfigDefinition("Colors", "Green"),
            new ConfigDefinition("Colors", "Yellow"),
            new ConfigDefinition("Colors", "Blue"),
            new ConfigDefinition("Colors", "Red"),
            new ConfigDefinition("Colors", "Teal"),
            new ConfigDefinition("Colors", "LaunchHeight"),
            new ConfigDefinition("Colors", "SpreadAngle"),
            new ConfigDefinition("Colors", "Interval"),
            new ConfigDefinition("Colors", "Plain"),
            new ConfigDefinition("Colors", "Rocket"),
            new ConfigDefinition("Show", "Target"),
            new ConfigDefinition("Show", "Style"),
            new ConfigDefinition("Show", "FastForwardNight"),
            new ConfigDefinition("Show", "NightHour"),
            new ConfigDefinition("Show", "TransitionSpeed"),
            new ConfigDefinition("Show", "RestoreDaylight"),
            new ConfigDefinition("Show", "LaunchHeight"),
            new ConfigDefinition("Show", "SpreadAngle"),
            new ConfigDefinition("Show", "Interval"),
            new ConfigDefinition("Show", "Diagnostics"),
            new ConfigDefinition("Show", "Pitch"),
            new ConfigDefinition("Show", "Bursts"),
            new ConfigDefinition("Show", "Height"),
            new ConfigDefinition("Show", "Spread"),
            new ConfigDefinition("Launcher", "Trigger"),
            new ConfigDefinition("Launcher", "Show"),
            new ConfigDefinition("Launcher", "SalvoDelay"),
            new ConfigDefinition("Launcher", "PadFrom"),
            new ConfigDefinition("Launcher", "ChaseTrail"),
            new ConfigDefinition("Launcher", "FinaleBursts"),
            new ConfigDefinition("Launcher", "FinaleInterval"),
            new ConfigDefinition("Launcher", "SalvoSet"),
            new ConfigDefinition("Player Show", "FastForwardNight"),
            new ConfigDefinition("Input", "ToggleKey")
        };

        foreach (var def in stale)
        {
            if (Config.ContainsKey(def))
            {
                try { Config.Remove(def); } catch { }
            }
        }
    }
}

