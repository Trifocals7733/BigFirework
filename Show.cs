using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Mirror;
using UnityEngine;

namespace BigFireworks;

internal enum ShowTarget
{
    Self,      // every burst launches from you
    Everyone   // bursts round-robin across all players in the lobby, one anchor per beat
}

internal enum ShowStyle
{
    Sky,       // bursts spawn at LaunchHeight with the configured cone
    Fountain   // bursts spray from the anchor's feet: 0.5 m up, wide cone
}

/// <summary>
/// Host-side sky show. Each beat calls the game's own [ClientRpc]
/// `PeckEffectParticleNetworked.RpcFire(pos, rot)`; Mirror broadcasts the burst to every
/// client, so a modless lobby sees the same sky. One-shot RPCs leave no local state behind,
/// so there is nothing to restore on disable — stopping the volley is the whole cleanup.
/// v1 is host-only: only the server may invoke ClientRpcs (guests get a log line, not a show).
///
/// Verified live: RpcFire POSITION spawns the burst at the given point, and ROTATION steers
/// flight — the flare flies along the rotation's forward axis (Pitch -90 = straight up).
/// Aim is therefore a direction vector turned into a rotation with LookRotation, with the
/// spread sampled as a uniform cone (per-axis Euler jitter collapsed to one plane — gimbal
/// lock near straight-up — which biased every volley eastward).
/// Emitter color is per-instance (flare gun prop paint), so per-color counts pick instances.
/// Pomodoro cannon dispensers are deliberately excluded — they arc like cannon shots, not flares.
/// </summary>
public class Show : MonoBehaviour
{
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<int> Green;
    internal static ConfigEntry<int> Yellow;
    internal static ConfigEntry<int> Blue;
    internal static ConfigEntry<int> Red;
    internal static ConfigEntry<int> Teal;
    internal static ConfigEntry<ShowTarget> Target;
    internal static ConfigEntry<bool> Follow;
    internal static ConfigEntry<ShowStyle> Style;
    internal static ConfigEntry<bool> FlareSounds;
    internal static ConfigEntry<float> LaunchHeight;
    internal static ConfigEntry<float> SpreadAngle;
    internal static ConfigEntry<float> Interval;
    internal static ConfigEntry<bool> Diagnostics;
    internal static ConfigEntry<KeyCode> ToggleKey;

    internal static Show Instance;

    struct PendingAirBang
    {
        public float PlayTime;
        public Vector3 Position;
    }

    static readonly List<PendingAirBang> _pendingBangs = new();

    readonly List<PeckEffectParticleNetworked> _emitters = new();
    readonly List<PeckEffectParticleNetworked> _queue = new();
    readonly List<PlayerCharacter> _cast = new();
    float _revalidate;
    bool _running;
    int _fired;      // beats fired this volley
    int _sentTotal;  // bursts actually sent this volley
    float _nextBeat;
    Vector3 _anchor; // Self mode: bursts spawn at _anchor + effective height
    float _effHeight;
    float _effSpread;
    bool _loggedGuest;
    float _diagNext;
    int _lastEmitterCount = -1;

    void Awake() { Instance = this; }
    void OnDestroy() { if (Instance == this) Instance = null; }

    internal static void Bind(ConfigEntry<bool> enabled,
        ConfigEntry<int> green, ConfigEntry<int> yellow, ConfigEntry<int> blue,
        ConfigEntry<int> red, ConfigEntry<int> teal,
        ConfigEntry<ShowTarget> target, ConfigEntry<bool> follow, ConfigEntry<ShowStyle> style,
        ConfigEntry<bool> flareSounds,
        ConfigEntry<float> launchHeight, ConfigEntry<float> spreadAngle,
        ConfigEntry<float> interval, ConfigEntry<bool> diagnostics, ConfigEntry<KeyCode> toggleKey)
    {
        Enabled = enabled;
        Green = green;
        Yellow = yellow;
        Blue = blue;
        Red = red;
        Teal = teal;
        Target = target;
        Follow = follow;
        Style = style;
        FlareSounds = flareSounds;
        LaunchHeight = launchHeight;
        SpreadAngle = spreadAngle;
        Interval = interval;
        Diagnostics = diagnostics;
        ToggleKey = toggleKey;
    }

    static void ProcessPendingAirBangs()
    {
        if (_pendingBangs.Count == 0) return;
        float now = Time.time;
        for (int i = _pendingBangs.Count - 1; i >= 0; i--)
        {
            if (now >= _pendingBangs[i].PlayTime)
            {
                var bang = _pendingBangs[i];
                _pendingBangs.RemoveAt(i);
                PlayAirBang(bang.Position);
            }
        }
    }

    void Update()
    {
        try
        {
            ProcessPendingAirBangs();

            if (Input.GetKeyDown(ToggleKey.Value))
            {
                if (!Enabled.Value)
                {
                    Plugin.Log.LogInfo("Fireworks volley ignored (mod disabled).");
                    return;
                }
                if (!NetworkServer.active)
                {
                    if (!_loggedGuest)
                    {
                        _loggedGuest = true;
                        Plugin.Log.LogInfo("Fireworks volley ignored (host only in v1).");
                    }
                    return;
                }
                _loggedGuest = false;
                if (_running) StopVolley("restarted");
                StartVolley();
            }

            if (!_running) return;
            if (!Enabled.Value) { StopVolley("disabled mid-show"); return; }

            if (Diagnostics.Value && Time.time >= _diagNext)
            {
                _diagNext = Time.time + 1f;
                Plugin.Log.LogInfo($"Show: server={NetworkServer.active} emitters={_emitters.Count} fired={_fired}/{_queue.Count} sent={_sentTotal}");
            }

            if (Time.time >= _nextBeat) FireBeat();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Show failed: {e}");
            if (_running) SkyDirector.Instance?.ReleaseNight();
            _running = false;
            _queue.Clear();
            _emitters.Clear();
            _cast.Clear();
            _revalidate = Time.time + 5f;
        }
    }

    void OnDisable()
    {
        // One-shot RPCs: no local state to restore. Stopping the volley is the whole cleanup.
        if (_running)
        {
            _running = false;
            SkyDirector.Instance?.ReleaseNight();
            Plugin.Log.LogInfo($"Show stopped on disable ({_sentTotal} bursts sent).");
        }
        _queue.Clear();
        _pendingBangs.Clear();
    }

    void StartVolley()
    {
        RefreshEmitters(quiet: false);
        if (_emitters.Count == 0)
        {
            Plugin.Log.LogWarning("Fireworks volley ignored (no flare emitters found).");
            return;
        }
        RefreshCast();
        _anchor = FindAnchor();
        // Fountain is a preset, not a second set of knobs: feet-level spray, wide cone.
        _effHeight = Style.Value == ShowStyle.Fountain ? 0.5f : LaunchHeight.Value;
        _effSpread = Style.Value == ShowStyle.Fountain
            ? Math.Max(SpreadAngle.Value, 25f)
            : SpreadAngle.Value;
        BuildQueue();
        if (_queue.Count == 0)
        {
            Plugin.Log.LogWarning("Fireworks volley ignored (all color counts are 0).");
            return;
        }
        _running = true;
        _fired = 0;
        _sentTotal = 0;
        _nextBeat = Time.time;
        _diagNext = Time.time + 1f;
        SkyDirector.Instance?.RequestNight();
        Plugin.Log.LogInfo($"Fireworks volley: {_queue.Count} bursts, {Style.Value} over {Target.Value} from ({_anchor.x:0},{_anchor.y:0},{_anchor.z:0}).");
    }

    void StopVolley(string reason)
    {
        if (_running)
        {
            _running = false;
            SkyDirector.Instance?.ReleaseNight();
        }
        _queue.Clear();
        Plugin.Log.LogInfo($"Fireworks volley over ({reason}, {_sentTotal} bursts sent).");
    }

    /// <summary>Self mode: the local player, so the show follows you. Fall back to the first
    /// emitter's position when the player isn't found (menu / loading).</summary>
    Vector3 FindAnchor()
    {
        try
        {
            var local = WorldManager.localPlayerCharacter;
            if (local != null && local.transform != null) return local.transform.position;
        }
        catch { }

        try
        {
            var list = PlayerCharacter.allPlayerCharacters;
            if (list != null && list.Count > 0)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var pc = list[i];
                    if (pc == null) continue;
                    var net = pc.playerNetworking;
                    if (net != null && net.isLocalPlayer && pc.transform != null)
                        return pc.transform.position;
                }
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Anchor scan fast-path failed: {e.Message}"); }

        try
        {
            foreach (var pc in UnityEngine.Object.FindObjectsOfType<PlayerCharacter>())
            {
                var net = pc?.playerNetworking;
                if (net == null || !net.isLocalPlayer) continue;
                return pc.transform.position;
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Anchor scan fallback failed: {e.Message}"); }

        try
        {
            if (_emitters.Count > 0 && _emitters[0] != null) return _emitters[0].transform.position;
        }
        catch { /* fall through to origin */ }
        return Vector3.zero;
    }

    void RefreshCast()
    {
        _cast.Clear();
        try
        {
            var list = PlayerCharacter.allPlayerCharacters;
            if (list != null && list.Count > 0)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var pc = list[i];
                    if (pc == null) continue;
                    try { if (pc.WasCollected) continue; } catch { continue; }
                    try { if (pc.transform == null) continue; } catch { continue; }
                    _cast.Add(pc);
                }
                _cast.Sort((a, b) => string.CompareOrdinal(SafeName(a), SafeName(b)));
                return;
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Cast scan fast-path failed: {e.Message}"); }

        try
        {
            foreach (var pc in UnityEngine.Object.FindObjectsOfType<PlayerCharacter>())
            {
                if (pc == null) continue;
                try { if (pc.WasCollected) continue; } catch { continue; }
                try { if (pc.transform == null) continue; } catch { continue; }
                _cast.Add(pc);
            }
            _cast.Sort((a, b) => string.CompareOrdinal(SafeName(a), SafeName(b)));
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Cast scan fallback failed: {e.Message}"); }
    }

    static string SafeName(PlayerCharacter pc)
    {
        try { return pc?.name ?? ""; } catch { return ""; }
    }

    /// <summary>Beat anchor: Self mode follows the host (when Follow is on) or stays anchored
    /// at start. Everyone mode round-robins across the lobby so each friend gets bursts over
    /// their head. Dead entries are pruned, not trusted.</summary>
    Vector3 BeatAnchor(int beat)
    {
        if (Target.Value == ShowTarget.Everyone)
        {
            try
            {
                for (var i = _cast.Count - 1; i >= 0; i--)
                {
                    var pc = _cast[i];
                    bool dead = pc == null;
                    if (!dead) try { dead = pc.WasCollected || pc.transform == null; } catch { dead = true; }
                    if (dead) _cast.RemoveAt(i);
                }
                if (_cast.Count > 0)
                    return _cast[beat % _cast.Count].transform.position;
            }
            catch { }
        }

        // Self mode: if Follow is on, track host's live position as they walk around
        if (Follow != null && Follow.Value)
        {
            var live = FindAnchor();
            if (live != Vector3.zero) _anchor = live;
        }

        return _anchor;
    }

    /// <summary>Color is per emitter instance (flare gun prop paint), matched by walking up
    /// the parent chain: FlareGunPropGreen/Blue/Yellow, plain FlareGunProp (= red),
    /// FixedFlare rocket run (= teal). Pomodoro dispensers map to Tomato and are never
    /// collected (see RefreshEmitters).</summary>
    internal static string Bucket(PeckEffectParticleNetworked e)
    {
        try
        {
            var t = e?.transform;
            for (var d = 0; d < 4 && t != null; d++, t = t.parent)
            {
                var n = t.name ?? "";
                if (n.Contains("Pomodoro")) return "Tomato";
                if (n.Contains("FixedFlare") || n.Contains("Rocket")) return "Teal";
                if (n.Contains("Green")) return "Green";
                if (n.Contains("Blue")) return "Blue";
                if (n.Contains("Yellow")) return "Yellow";
            }
            t = e?.transform;
            for (var d = 0; d < 4 && t != null; d++, t = t.parent)
                if ((t.name ?? "").Contains("FlareGunProp")) return "Red";
        }
        catch { }
        return "Other";
    }

    /// <summary>Shared firing order: per-color counts, round-robin interleaved. Volley + director.</summary>
    internal static List<PeckEffectParticleNetworked> BuildColorQueue(List<PeckEffectParticleNetworked> emitters)
    {
        var queue = new List<PeckEffectParticleNetworked>();
        var byBucket = new Dictionary<string, List<PeckEffectParticleNetworked>>();
        foreach (var e in emitters)
        {
            var b = Bucket(e);
            if (!byBucket.TryGetValue(b, out var list)) byBucket[b] = list = new List<PeckEffectParticleNetworked>();
            list.Add(e);
        }
        var takes = new List<KeyValuePair<List<PeckEffectParticleNetworked>, int>>();
        Take(takes, byBucket, "Green", Green.Value);
        Take(takes, byBucket, "Yellow", Yellow.Value);
        Take(takes, byBucket, "Blue", Blue.Value);
        Take(takes, byBucket, "Red", Red.Value);
        Take(takes, byBucket, "Teal", Teal.Value);
        var remaining = new int[takes.Count];
        for (var k = 0; k < takes.Count; k++) remaining[k] = takes[k].Value;
        bool added;
        do
        {
            added = false;
            // index loop on purpose: the queue mutation below must not run inside a foreach
            // over takes (that threw InvalidOperationException: collection was modified).
            for (var k = 0; k < takes.Count; k++)
            {
                if (remaining[k] <= 0) continue;
                var list = takes[k].Key;
                if (list.Count == 0) continue;
                // cycle through that color's instances so repeat bursts don't hammer one gun
                var e = list[0];
                list.RemoveAt(0);
                list.Add(e);
                remaining[k]--;
                queue.Add(e);
                added = true;
            }
        } while (added);
        return queue;
    }

    void BuildQueue()
    {
        _queue.Clear();
        _queue.AddRange(BuildColorQueue(_emitters));
    }

    static void Take(List<KeyValuePair<List<PeckEffectParticleNetworked>, int>> takes,
        Dictionary<string, List<PeckEffectParticleNetworked>> byBucket, string bucket, int n)
    {
        if (n > 0 && byBucket.TryGetValue(bucket, out var list) && list.Count > 0)
            takes.Add(new KeyValuePair<List<PeckEffectParticleNetworked>, int>(list, n));
    }

    void FireBeat()
    {
        if (_fired >= _queue.Count) { StopVolley("complete"); return; }
        _nextBeat = Time.time + Interval.Value;
        var beat = _fired++;
        var e = _queue[beat];

        // Spawn at the beat anchor (+effective height, small pad jitter). Aim is fixed
        // straight up; the burst flies along the rotation's forward axis, so the rotation
        // is built with LookRotation around a uniform cone sample.
        var pos = BeatAnchor(beat) + new Vector3(
            UnityEngine.Random.Range(-0.75f, 0.75f),
            _effHeight,
            UnityEngine.Random.Range(-0.75f, 0.75f));
        var rot = Aim(-90f, _effSpread);
        try
        {
            if (e == null) throw new NullReferenceException("emitter gone");
            e.RpcFire(pos, rot);
            ScheduleAirBang(pos, rot, e);
            _sentTotal++;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Burst {beat} failed, dropping emitter: {ex.Message}");
            _emitters.Remove(e);
        }
    }

    static AudioAsset _cachedBangAsset;

    static Vector3 GetListenerPosition()
    {
        try
        {
            if (Camera.main != null) return Camera.main.transform.position;
            var local = WorldManager.localPlayerCharacter;
            if (local != null && local.transform != null) return local.transform.position;
        }
        catch { }
        return Vector3.zero;
    }

    internal static ParticleOneShotSound FindParticleOneShotSound(PeckEffectParticleNetworked e)
    {
        if (e == null) return null;
        try
        {
            if (e.targetParticleSystem != null)
            {
                var poss = e.targetParticleSystem.GetComponent<ParticleOneShotSound>()
                    ?? e.targetParticleSystem.GetComponentInChildren<ParticleOneShotSound>(true);
                if (poss != null) return poss;
            }
            return e.GetComponent<ParticleOneShotSound>()
                ?? e.GetComponentInChildren<ParticleOneShotSound>(true)
                ?? e.GetComponentInParent<ParticleOneShotSound>(true);
        }
        catch { }
        return null;
    }

    static void DiscoverBangAsset(PeckEffectParticleNetworked e)
    {
        if (_cachedBangAsset != null) return;

        var poss = FindParticleOneShotSound(e);
        if (poss?.Assets != null && poss.Assets.Length > 0)
        {
            for (int i = 0; i < poss.Assets.Length; i++)
            {
                var a = poss.Assets[i];
                if (a != null && !a.name.Contains("close", StringComparison.OrdinalIgnoreCase))
                {
                    _cachedBangAsset = a;
                    Plugin.Log.LogInfo($"Big Firework: Discovered native flare explosion AudioAsset '{a.name}' from {poss.gameObject.name}");
                    return;
                }
            }
            _cachedBangAsset = poss.Assets[0];
            Plugin.Log.LogInfo($"Big Firework: Discovered native flare explosion AudioAsset '{_cachedBangAsset.name}' from {poss.gameObject.name}");
        }
    }

    /// <summary>Schedules an authentic flare BANG sound to detonate in the air at the apex of the rocket's flight.</summary>
    internal static void ScheduleAirBang(Vector3 pos, Quaternion rot, PeckEffectParticleNetworked e)
    {
        if (FlareSounds == null || !FlareSounds.Value) return;

        if (_cachedBangAsset == null)
        {
            DiscoverBangAsset(e);
        }

        try
        {
            // Flare flight lifetime and trajectory (zero allocation, pure math)
            const float flightTime = 1.75f;
            const float flightSpeed = 24f;

            // Flare travels along the launch forward axis into the sky
            Vector3 airPos = pos + (rot * Vector3.forward) * (flightSpeed * flightTime * 0.85f);

            // Acoustic travel time: sound travels at 343 m/s (speed of sound)
            Vector3 listenerPos = GetListenerPosition();
            float distance = Vector3.Distance(listenerPos, airPos);
            float acousticDelay = distance / 343f;

            // Explosion happens at Time.time + flightTime; sound arrives after acoustic travel delay
            _pendingBangs.Add(new PendingAirBang
            {
                PlayTime = Time.time + flightTime + acousticDelay,
                Position = airPos
            });
        }
        catch (Exception ex)
        {
            if (Diagnostics != null && Diagnostics.Value)
                Plugin.Log.LogWarning($"ScheduleAirBang error: {ex.Message}");
        }
    }

    /// <summary>Plays the authentic flare explosion bang in the air at the burst coordinates.</summary>
    internal static void PlayAirBang(Vector3 airPos)
    {
        if (FlareSounds == null || !FlareSounds.Value || _cachedBangAsset == null) return;

        try
        {
            AudioPlayHelper.CreateEventAndPlay(_cachedBangAsset, airPos, null, null, null, true, -1f, null, null, false);
        }
        catch (Exception ex)
        {
            if (Diagnostics != null && Diagnostics.Value)
                Plugin.Log.LogWarning($"PlayAirBang error: {ex.Message}");
        }
    }


    /// <summary>Aim rotation from pitch degrees (0 = horizontal as the gun aims, -90 = straight
    /// up) plus a uniform cone sample. Shared by the F10 volley and the launcher show.</summary>
    internal static Quaternion Aim(float pitchDeg, float spreadDeg)
    {
        var el = -pitchDeg * Mathf.Deg2Rad;
        var center = new Vector3(0f, Mathf.Sin(el), Mathf.Cos(el));
        return Quaternion.LookRotation(ConeSample(center, spreadDeg));
    }

    static readonly List<PeckEffectParticleNetworked> _cachedEmitters = new();
    static float _lastEmitterScanTime = -100f;
    static int _lastSkippedTomatoes = 0;

    /// <summary>Shared scanner: all live flare emitters, pomodoros excluded. Volley + director.</summary>
    internal static List<PeckEffectParticleNetworked> ScanFlareEmitters(out int skippedTomatoes, bool forceRescan = false)
    {
        if (!forceRescan && _cachedEmitters.Count > 0 && Time.time - _lastEmitterScanTime < 15f)
        {
            var first = _cachedEmitters[0];
            if (first != null)
            {
                bool dead = false;
                try { dead = first.WasCollected; } catch { dead = true; }
                if (!dead)
                {
                    skippedTomatoes = _lastSkippedTomatoes;
                    return _cachedEmitters;
                }
            }
        }

        _cachedEmitters.Clear();
        skippedTomatoes = 0;
        try
        {
            foreach (var e in UnityEngine.Object.FindObjectsOfType<PeckEffectParticleNetworked>())
            {
                if (e == null) continue;
                try { if (e.WasCollected) continue; } catch { continue; }
                // Pomodoro cannon dispensers arc like cannon shots, not sky flares — out.
                if (Bucket(e) == "Tomato") { skippedTomatoes++; continue; }
                _cachedEmitters.Add(e);
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Emitter scan failed: {e.Message}"); }

        _lastEmitterScanTime = Time.time;
        _lastSkippedTomatoes = skippedTomatoes;
        return _cachedEmitters;
    }

    /// <summary>Uniform random direction inside a cone around center (unit vector).</summary>
    internal static Vector3 ConeSample(Vector3 center, float degrees)
    {
        if (degrees <= 0f) return center;
        var rad = degrees * Mathf.Deg2Rad;
        var refUp = Mathf.Abs(center.y) > 0.99f ? Vector3.forward : Vector3.up;
        var u = Vector3.Cross(refUp, center).normalized;
        var v = Vector3.Cross(center, u);
        var r = Mathf.Sqrt(UnityEngine.Random.Range(0f, 1f)) * Mathf.Tan(rad);
        var a = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        return (center + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r).normalized;
    }

    void RefreshEmitters(bool quiet)
    {
        _emitters.Clear();
        _emitters.AddRange(ScanFlareEmitters(out var skippedTomatoes));
        _revalidate = Time.time + 5f;
        // Mid-volley rescans used to spam this line every 5s; log only on change or when asked.
        if (!quiet && (Diagnostics.Value || _emitters.Count != _lastEmitterCount))
            Plugin.Log.LogInfo($"Found {_emitters.Count} flare emitters ({skippedTomatoes} pomodoro dispensers skipped).");
        _lastEmitterCount = _emitters.Count;
    }
}
