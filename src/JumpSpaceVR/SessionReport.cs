using UnityEngine;

namespace JumpSpaceVR;

/// <summary>
/// While VR runs, collects what a player would otherwise have to describe (frame rate, whether controller input and
/// laser clicks get through, whether the weapon sits in the hand, how jolty the view is) and writes a one-line
/// summary to the log every few minutes, so a play session produces numbers for bug reports.
/// </summary>
internal sealed class SessionReport
{
    private const float IntervalSeconds = 300f;
    // Longer pauses are scene loads, not stutter; they are counted separately.
    private const float LoadingPauseSeconds = 0.5f;

    private float _periodStart = -1f;
    private int _frames;
    private float _frameTimeTotal;
    private float _worstFrame;
    private int _slowFrames;
    private int _loadingPauses;
    private int _controllerFrames;
    private int _inputFrames;
    private int _triggerPulls;
    private int _laserClicks;
    private int _levelFrames;
    private int _weaponFrames;
    private float _worstJolt;
    private bool _triggerHeld;

    public void RecordFrame(float frameTime, in OpenXR.ControllerState controllers, bool laserOnUi, bool inLevel,
        bool weaponInHand, float eyeJolt)
    {
        var now = Time.unscaledTime;
        if (_periodStart < 0f)
            _periodStart = now;

        if (frameTime > LoadingPauseSeconds)
        {
            _loadingPauses++;
            return;
        }

        _frames++;
        _frameTimeTotal += frameTime;
        _worstFrame = Mathf.Max(_worstFrame, frameTime);
        if (frameTime > 1f / 45f)
            _slowFrames++;

        if (controllers.IsActive)
        {
            _controllerFrames++;
            if (controllers.LeftStick.sqrMagnitude > 0.04f || controllers.RightStick.sqrMagnitude > 0.04f ||
                controllers.RightTrigger > 0.5f || controllers.LeftTrigger > 0.5f || controllers.ButtonA || controllers.ButtonB)
                _inputFrames++;
        }

        var triggerDown = controllers.RightTrigger > 0.5f;
        if (triggerDown && !_triggerHeld)
        {
            if (laserOnUi)
                _laserClicks++;
            else
                _triggerPulls++;
        }
        _triggerHeld = triggerDown;

        if (inLevel)
        {
            _levelFrames++;
            if (weaponInHand)
                _weaponFrames++;
            _worstJolt = Mathf.Max(_worstJolt, eyeJolt);
        }

        if (now - _periodStart >= IntervalSeconds)
            Flush();
    }

    /// <summary>Writes the summary for the current period (also call when VR stops) and starts a new one.</summary>
    public void Flush()
    {
        if (_frames > 0)
        {
            var minutes = (Time.unscaledTime - _periodStart) / 60f;
            var averageFps = _frames / Mathf.Max(_frameTimeTotal, 0.001f);
            var weaponShare = _levelFrames > 0 ? 100f * _weaponFrames / _levelFrames : 0f;
            Log.Msg($"[session] {minutes:F1} min: {averageFps:F0} fps avg, worst frame {_worstFrame * 1000f:F0} ms, " +
                    $"{100f * _slowFrames / _frames:F1}% frames under 45 fps, {_loadingPauses} loading pauses | controllers tracked {100f * _controllerFrames / _frames:F0}%, " +
                    $"input in {_inputFrames} frames, {_triggerPulls} trigger pulls, {_laserClicks} laser clicks | " +
                    $"in level {100f * _levelFrames / _frames:F0}%, weapon in hand {weaponShare:F0}% of level time, " +
                    $"worst view jolt {_worstJolt:F0} cm/s");
        }

        _periodStart = Time.unscaledTime;
        _frames = _controllerFrames = _inputFrames = _triggerPulls = _laserClicks = _levelFrames = _weaponFrames = _slowFrames = 0;
        _loadingPauses = 0;
        _frameTimeTotal = _worstFrame = _worstJolt = 0f;
    }
}
