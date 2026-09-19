using System;
using BepInEx.Configuration;
using Mirror;
using UnityEngine;

namespace BigFireworks;

public class SkyDirector : MonoBehaviour
{
    internal static SkyDirector Instance { get; private set; }

    internal static ConfigEntry<bool> FastNight;
    internal static ConfigEntry<float> TargetNightHour;
    internal static ConfigEntry<float> TransitionSpeed;
    internal static ConfigEntry<bool> RestoreDaylight;
    internal static ConfigEntry<float> RestoreDelay;

    private enum SkyState
    {
        Idle,
        FastForwardingToNight,
        HoldingNight,
        WaitingToRestore,
        RestoringDaylight
    }

    private SkyState _state = SkyState.Idle;
    private float _originalHour;
    private float _fromHour;
    private float _targetHour;
    private float _forwardDistance;
    private float _transitionStartTime;
    private float _transitionDuration;
    private float _restoreTime;
    private int _activeShowsCount;

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        RestoreImmediate();
    }

    private void OnDisable()
    {
        RestoreImmediate();
    }

    internal static void Bind(ConfigEntry<bool> fastNight, ConfigEntry<float> targetNightHour,
        ConfigEntry<float> transitionSpeed, ConfigEntry<bool> restoreDaylight, ConfigEntry<float> restoreDelay)
    {
        FastNight = fastNight;
        TargetNightHour = targetNightHour;
        TransitionSpeed = transitionSpeed;
        RestoreDaylight = restoreDaylight;
        RestoreDelay = restoreDelay;
    }

    public void RequestNight()
    {
        if (!NetworkServer.active) return;
        if (FastNight == null || !FastNight.Value) return;
        if (!SkyManager.initalized) return;

        _activeShowsCount++;
        if (_activeShowsCount > 1 && (_state == SkyState.FastForwardingToNight || _state == SkyState.HoldingNight))
            return;

        if (_state == SkyState.WaitingToRestore)
        {
            _state = SkyState.HoldingNight;
            return;
        }

        float currentHour = SkyManager.GetCurrentTime();
        if (_state == SkyState.Idle)
        {
            _originalHour = currentHour;
        }

        _fromHour = currentHour;
        _targetHour = Mathf.Repeat(TargetNightHour?.Value ?? 0f, 24f);

        // Circular forward distance: how many hours ahead to reach night
        float dist = (_targetHour - _fromHour);
        while (dist < 0f) dist += 24f;
        _forwardDistance = dist;

        _transitionDuration = Mathf.Max(0.5f, TransitionSpeed?.Value ?? 2.5f);
        _transitionStartTime = Time.time;
        _state = SkyState.FastForwardingToNight;

        Plugin.Log.LogInfo($"SkyDirector: Fast-forwarding time from {_fromHour:0.0}h to night {_targetHour:0.0}h ({_forwardDistance:0.0}h forward over {_transitionDuration:0.0}s).");
    }

    public void ReleaseNight()
    {
        if (!NetworkServer.active) return;
        if (_activeShowsCount > 0) _activeShowsCount--;

        if (_activeShowsCount > 0) return; // Another firework show is still active

        if (_state == SkyState.Idle) return;

        if (!SkyManager.initalized)
        {
            _state = SkyState.Idle;
            return;
        }

        if (RestoreDaylight == null || !RestoreDaylight.Value)
        {
            RestoreImmediate();
            return;
        }

        float delay = RestoreDelay != null ? Mathf.Max(0f, RestoreDelay.Value) : 0f;
        if (delay > 0f)
        {
            _restoreTime = Time.time + delay;
            _state = SkyState.WaitingToRestore;
            Plugin.Log.LogInfo($"SkyDirector: Night held for {delay:0.0}s before daylight restores.");
        }
        else
        {
            BeginRestoringDaylight();
        }
    }

    private void BeginRestoringDaylight()
    {
        if (!NetworkServer.active || !SkyManager.initalized)
        {
            RestoreImmediate();
            return;
        }

        // Fast-forward from night through sunrise back to original daylight
        float currentHour = SkyManager.GetCurrentTime();
        _fromHour = currentHour;
        _targetHour = _originalHour;

        float dist = (_targetHour - _fromHour);
        while (dist < 0f) dist += 24f;
        _forwardDistance = dist;

        _transitionDuration = Mathf.Max(0.5f, (TransitionSpeed?.Value ?? 2.5f) * 0.8f);
        _transitionStartTime = Time.time;
        _state = SkyState.RestoringDaylight;

        Plugin.Log.LogInfo($"SkyDirector: Fast-forwarding time from {_fromHour:0.0}h to daylight {_targetHour:0.0}h ({_forwardDistance:0.0}h forward over {_transitionDuration:0.0}s).");
    }

    private void Update()
    {
        if (!NetworkServer.active || !SkyManager.initalized)
        {
            if (_state != SkyState.Idle) _state = SkyState.Idle;
            return;
        }

        if (_state == SkyState.FastForwardingToNight)
        {
            float elapsed = Time.time - _transitionStartTime;
            float progress = Mathf.Clamp01(elapsed / _transitionDuration);
            // SmoothStep creates cinematic acceleration & deceleration of the sun & sky
            float eased = Mathf.SmoothStep(0f, 1f, progress);
            float hour = Mathf.Repeat(_fromHour + _forwardDistance * eased, 24f);

            SkyManager.SetFixedTime(hour);

            if (progress >= 1f)
            {
                _state = SkyState.HoldingNight;
                SkyManager.SetFixedTime(_targetHour);
                Plugin.Log.LogInfo($"SkyDirector: Night reached ({_targetHour:0.0}h locked for firework show).");
            }
        }
        else if (_state == SkyState.HoldingNight || _state == SkyState.WaitingToRestore)
        {
            SkyManager.SetFixedTime(_targetHour);

            if (_state == SkyState.WaitingToRestore && Time.time >= _restoreTime)
            {
                BeginRestoringDaylight();
            }
        }
        else if (_state == SkyState.RestoringDaylight)
        {
            float elapsed = Time.time - _transitionStartTime;
            float progress = Mathf.Clamp01(elapsed / _transitionDuration);
            float eased = Mathf.SmoothStep(0f, 1f, progress);
            float hour = Mathf.Repeat(_fromHour + _forwardDistance * eased, 24f);

            SkyManager.SetFixedTime(hour);

            if (progress >= 1f)
            {
                RestoreImmediate();
                Plugin.Log.LogInfo($"SkyDirector: Daylight restored ({_originalHour:0.0}h), natural clock resumed.");
            }
        }
    }

    public void RestoreImmediate()
    {
        _activeShowsCount = 0;
        _state = SkyState.Idle;
        _restoreTime = 0f;
        if (SkyManager.initalized)
        {
            try
            {
                SkyManager.ClearFixedTime();
            }
            catch { }
        }
    }
}
