using UnityEngine;

namespace JumpSpaceVR.Controls;

/// <summary>
/// Real crouching: ducking in the room makes the character crouch (gamepad B, the game's crouch), and standing up
/// stands it up, in either of the game's crouch modes (hold or toggle). While the game crouches, its camera drops on
/// top of the player's own ducking; <see cref="Lift"/> gives that drop back so the view stays at the real head height.
/// </summary>
internal sealed class RealCrouch
{
    // Below standing (the recentered head height): crouch at 30 cm down, stand again above 20 cm down.
    private const float CrouchDepth = 0.3f;
    private const float StandDepth = 0.2f;
    private const float TapSeconds = 0.15f;
    // After a toggle tap, give the game time to change state before judging whether another tap is needed.
    private const float TapCooldownSeconds = 0.5f;
    private const float HeightSmoothingSeconds = 0.15f;

    private float _tapUntil;
    private float _nextTap;
    private bool _hasStanding;
    private float _standingHeight;
    private float _cameraHeight;
    private bool _hasCameraHeight;

    /// <summary>True while the player is ducked low enough to crouch.</summary>
    public bool Wants { get; private set; }

    /// <summary>How far to raise the view (meters) to undo the game's crouch camera drop.</summary>
    public float Lift { get; private set; }

    /// <summary>
    /// Decides whether to press crouch this frame. <paramref name="headDrop"/> is the head's height relative to the
    /// recentered (standing) height; <paramref name="allowed"/> is false in menus, seats and vehicles, where the
    /// crouch button means something else.
    /// </summary>
    public bool Press(bool enabled, float headDrop, bool allowed, bool gameCrouching, bool toggleMode)
    {
        if (!enabled || !allowed)
        {
            Wants = false;
            _tapUntil = 0f;
            return false;
        }

        if (headDrop < -CrouchDepth)
            Wants = true;
        else if (headDrop > -StandDepth)
            Wants = false;

        if (!toggleMode)
            return Wants;

        // Toggle mode: a short tap whenever the game's state differs from what the player's body is doing.
        var now = Time.unscaledTime;
        if (now < _tapUntil)
            return true;
        if (Wants != gameCrouching && now >= _nextTap)
        {
            _tapUntil = now + TapSeconds;
            _nextTap = now + TapSeconds + TapCooldownSeconds;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Tracks the game camera's height above the character's feet: remembered while standing, and while the game
    /// crouches because the player ducked, the difference becomes <see cref="Lift"/>. Call before rendering.
    /// </summary>
    public void UpdateLift(bool enabled, float cameraHeightAboveFeet, bool gameCrouching)
    {
        var now = Time.unscaledTime;
        var blend = 1f - Mathf.Exp(-Time.unscaledDeltaTime / HeightSmoothingSeconds);
        _cameraHeight = _hasCameraHeight ? Mathf.Lerp(_cameraHeight, cameraHeightAboveFeet, blend) : cameraHeightAboveFeet;
        _hasCameraHeight = true;

        // Only undo crouches the player made with their body (a crouch pressed on a button lowers the view as usual),
        // and keep doing so for a moment after standing, while the game's camera rises again.
        if (enabled && gameCrouching && Wants)
            _liftUntil = now + StandUpSeconds;
        if (!gameCrouching)
        {
            // The standing height follows rises at once and drops only slowly (and not while the camera is still
            // rising from a crouch), so a crouch's own camera drop never becomes the reference.
            if (!_hasStanding || _cameraHeight > _standingHeight)
                _standingHeight = _cameraHeight;
            else if (now > _liftUntil)
                _standingHeight = Mathf.Lerp(_standingHeight, _cameraHeight, 1f - Mathf.Exp(-Time.unscaledDeltaTime / ReferenceDecaySeconds));
            _hasStanding = true;
        }
        Lift = enabled && _hasStanding && now < _liftUntil ? Mathf.Max(0f, _standingHeight - _cameraHeight) : 0f;
    }

    private const float StandUpSeconds = 1f;
    private const float ReferenceDecaySeconds = 2f;
    private float _liftUntil;
}
