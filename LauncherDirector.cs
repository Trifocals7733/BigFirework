using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Mirror;
using UnityEngine;

namespace BigFireworks;

internal enum LauncherTrigger { Off, Accompany, Replace }
internal enum LauncherShow { FlareVolley, RocketSalvo, GrandFinale }
internal enum PadAnchor { Pressed, All }

/// <summary>
/// Host-side launcher hijack. A Harmony hook on PeckSwitch.Peck(PeckContext) detects genuine
/// presses of FireworkLauncher buttons by the local host player:
/// - Accompany: the normal rocket flies AND the selected show runs alongside it.
/// - Replace: the press is swallowed (prefix returns false → no Cmd, no replication) and the
///   selected show runs instead. Guests pressing launchers are unaffected: their presses reach
///   the host via the server Cmd path, never through the host's PeckSwitch.Peck. The mod's own
///   programmatic salvo pecks are unaffected too (parameterless Peck() overload + null player
///   identity — verified live: remote presses arrive with actionNumber -1, no player).
/// Everything a show fires (RpcFire volleys, genuine salvo pecks) replicates, so modless
/// clients see the same sky. One-shot RPCs + genuine pecks leave no mod state behind.
/// </summary>
public class LauncherDirector : MonoBehaviour
{
    internal static ConfigEntry<LauncherTrigger> Trigger;
    internal static ConfigEntry<LauncherShow> Mode;
    internal static ConfigEntry<PadAnchor> PadFrom;
    internal static ConfigEntry<float> SalvoDelay;

    internal static LauncherDirector Instance;

    internal static void Bind(ConfigEntry<LauncherTrigger> trigger, ConfigEntry<LauncherShow> mode,
        ConfigEntry<PadAnchor> padFrom, ConfigEntry<float> salvoDelay)
    {
        Trigger = trigger;
        Mode = mode;
        PadFrom = padFrom;
        SalvoDelay = salvoDelay;
    }

    readonly List<PeckSwitch> _launcherSwitches = new();
    bool _running;
    Vector3 _pad;
    bool _wantPad, _wantSalvo;
    readonly List<PeckEffectParticleNetworked> _queue = new();
    int _fired, _sentTotal;
    float _nextBeat;
    readonly List<PeckSwitch> _salvo = new();
    int _salvoFired;
    float _nextSalvo;
    readonly List<Vector3> _pads = new();
    float _deadline, _diagNext;

    void Awake() { Instance = this; }
    void OnDestroy() { if (Instance == this) Instance = null; }

    void OnDisable()
    {
        if (_running)
        {
            _running = false;
            SkyDirector.Instance?.ReleaseNight();
            Plugin.Log.LogInfo($"Launcher show stopped on disable ({_sentTotal} bursts sent).");
        }
        _queue.Clear();
        _salvo.Clear();
    }

    // true = run the original press. Only Replace mode + genuine host press swallows it.
    internal static bool OnPressPrefix(PeckSwitch sw, PeckContext ctx)
    {
        try
        {
            if (!NetworkServer.active) return true;
            if (Show.Enabled == null || !Show.Enabled.Value) return true;
            if (Trigger == null || Trigger.Value != LauncherTrigger.Replace) return true;
            if (!IsLauncherSwitch(sw)) return true;
            if (!IsLocalPresser(ctx)) return true;
            var d = Instance;
            if (d == null) return true;
            d.TriggerFromPress(sw);
            return false;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Launcher hook failed (original kept): {e.Message}"); return true; }
    }

    internal static void OnPressPostfix(PeckSwitch sw, PeckContext ctx)
    {
        try
        {
            if (!NetworkServer.active) return;
            if (Show.Enabled == null || !Show.Enabled.Value) return;
            if (Trigger == null || Trigger.Value != LauncherTrigger.Accompany) return;
            if (!IsLauncherSwitch(sw)) return;
            if (!IsLocalPresser(ctx)) return;
            Instance?.TriggerFromPress(sw);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Launcher hook failed: {e.Message}"); }
    }

    static bool IsLauncherSwitch(PeckSwitch sw)
    {
        try
        {
            var t = sw?.transform;
            for (var d = 0; d < 6 && t != null; d++, t = t.parent)
                if ((t.name ?? "").Contains("FireworkLauncher")) return true;
        }
        catch { }
        return false;
    }

    static bool IsLocalPresser(PeckContext ctx)
    {
        try
        {
            var pc = ctx.GetPlayerCharacter();
            if (pc == null) return false;
            var local = WorldManager.localPlayerCharacter;
            if (local != null && pc == local) return true;
            var net = pc.playerNetworking;
            if (net == null) return false;
            return net.isLocalPlayer;
        }
        catch { return false; }
    }

    internal void TriggerFromPress(PeckSwitch sw)
    {
        try
        {
            if (_running) StopShow("restarted");
            RefreshLaunchers();
            try { _pad = sw.transform.position; }
            catch { _pad = Vector3.zero; }
            var mode = Mode.Value;
            _wantPad = mode == LauncherShow.FlareVolley || mode == LauncherShow.GrandFinale;
            _wantSalvo = mode == LauncherShow.RocketSalvo || mode == LauncherShow.GrandFinale;
            _queue.Clear();
            _queue.AddRange(Show.BuildColorQueue(Show.ScanFlareEmitters(out _)));
            _salvo.Clear();
            _salvoFired = 0;
            if (_wantSalvo) BuildSalvo(sw);
            _pads.Clear();
            _pads.Add(_pad);
            foreach (var s in _launcherSwitches)
            {
                try
                {
                    if (s == null) continue;
                    var p = s.transform.position;
                    if ((p - _pad).sqrMagnitude > 1f) _pads.Add(p);
                }
                catch { }
            }
            if (_queue.Count == 0 && _salvo.Count == 0)
            {
                Plugin.Log.LogWarning("Launcher show ignored (no colors, no salvo).");
                return;
            }
            _running = true;
            _fired = 0;
            _sentTotal = 0;
            _nextBeat = Time.time;
            _nextSalvo = Time.time + 0.5f;
            _diagNext = Time.time + 1f;
            _deadline = Time.time + 25f;
            SkyDirector.Instance?.RequestNight();
            Plugin.Log.LogInfo($"Launcher show: {mode} at ({_pad.x:0},{_pad.y:0},{_pad.z:0}) pad={_queue.Count} salvo={_salvo.Count}.");
        }
        catch (Exception e) { Plugin.Log.LogError($"Launcher show failed to start: {e.Message}"); _running = false; }
    }

    void StopShow(string reason)
    {
        if (_running)
        {
            _running = false;
            SkyDirector.Instance?.ReleaseNight();
        }
        _queue.Clear();
        _salvo.Clear();
        Plugin.Log.LogInfo($"Launcher show over ({reason}, {_sentTotal} bursts sent).");
    }

    void RefreshLaunchers()
    {
        if (_launcherSwitches.Count > 0)
        {
            bool allAlive = true;
            for (int i = 0; i < _launcherSwitches.Count; i++)
            {
                var s = _launcherSwitches[i];
                if (s == null) { allAlive = false; break; }
                try { if (s.WasCollected) { allAlive = false; break; } } catch { allAlive = false; break; }
            }
            if (allAlive) return;
        }

        _launcherSwitches.Clear();
        try
        {
            foreach (var sw in UnityEngine.Object.FindObjectsOfType<PeckSwitch>())
            {
                if (sw == null) continue;
                try { if (sw.WasCollected) continue; } catch { continue; }
                if (IsLauncherSwitch(sw)) _launcherSwitches.Add(sw);
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Launcher scan failed: {e.Message}"); }
    }

    void BuildSalvo(PeckSwitch pressed)
    {
        // Pressed launcher fires first, then the rest radiating outward — salvo covers all of them.
        try
        {
            try { _salvo.Add(pressed); }
            catch (Exception e) { Plugin.Log.LogWarning($"Salvo pressed add failed: {e.Message}"); }
            var rest = new List<PeckSwitch>();
            foreach (var sw in _launcherSwitches)
            {
                if (sw == null) continue;
                try { if (sw.WasCollected) continue; } catch { continue; }
                try { if (sw == pressed) continue; } catch { continue; }
                rest.Add(sw);
            }
            rest.Sort((a, b) => SalvoDist2(a).CompareTo(SalvoDist2(b)));
            _salvo.AddRange(rest);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Salvo build failed: {e.Message}"); }
    }

    float SalvoDist2(PeckSwitch sw)
    {
        try { var d = sw.transform.position - _pad; return d.sqrMagnitude; }
        catch { return float.MaxValue; }
    }

    void Update()
    {
        try
        {
            if (!_running) return;
            if (!Show.Enabled.Value) { StopShow("disabled"); return; }
            if (Time.time > _deadline) { StopShow("timeout"); return; }
            if (Show.Diagnostics.Value && Time.time >= _diagNext)
            {
                _diagNext = Time.time + 1f;
                Plugin.Log.LogInfo($"Launcher show: fired={_fired}/{_queue.Count} sent={_sentTotal} salvo={_salvoFired}/{_salvo.Count}");
            }
            if (_salvoFired < _salvo.Count && Time.time >= _nextSalvo)
            {
                _nextSalvo = Time.time + SalvoDelay.Value;
                var sw = _salvo[_salvoFired++];
                try { sw.Peck(); }
                catch (Exception e) { Plugin.Log.LogWarning($"Salvo peck failed: {e.Message}"); }
            }
            if (Time.time >= _nextBeat)
            {
                _nextBeat = Time.time + Show.Interval.Value;
                var beat = _fired++;
                if (_wantPad && beat < _queue.Count) FirePad(beat);
                if (_fired >= _queue.Count && _salvoFired >= _salvo.Count)
                    StopShow("complete");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Launcher show failed: {e}");
            if (_running) SkyDirector.Instance?.ReleaseNight();
            _running = false;
        }
    }

    void FirePad(int beat)
    {
        if (beat >= _queue.Count) return;
        var anchor = PadFrom.Value == PadAnchor.All && _pads.Count > 0 ? _pads[beat % _pads.Count] : _pad;
        var pos = anchor + new Vector3(
            UnityEngine.Random.Range(-0.75f, 0.75f),
            Show.LaunchHeight.Value,
            UnityEngine.Random.Range(-0.75f, 0.75f));
        FireWith(_queue[beat], pos, beat);
    }

    void FireWith(PeckEffectParticleNetworked e, Vector3 pos, int n)
    {
        var rot = Show.Aim(-90f, Show.SpreadAngle.Value);
        try
        {
            if (e == null) throw new NullReferenceException("emitter gone");
            e.RpcFire(pos, rot);
            Show.ScheduleAirBang(pos, rot, e);
            _sentTotal++;
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Show burst {n} failed: {ex.Message}"); }
    }
}

[HarmonyLib.HarmonyPatch(typeof(PeckSwitch), "Peck", new System.Type[] { typeof(PeckContext) })]
static class LauncherPressPatch
{
    [HarmonyLib.HarmonyPrefix]
    static bool Prefix(PeckSwitch __instance, PeckContext peckContext)
        => LauncherDirector.OnPressPrefix(__instance, peckContext);

    [HarmonyLib.HarmonyPostfix]
    static void Postfix(PeckSwitch __instance, PeckContext peckContext)
        => LauncherDirector.OnPressPostfix(__instance, peckContext);
}
