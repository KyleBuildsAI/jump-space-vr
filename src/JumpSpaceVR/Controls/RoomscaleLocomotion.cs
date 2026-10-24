using UnityEngine;

namespace JumpSpaceVR.Controls;

/// <summary>
/// Room-scale walking: when you physically step away from your character's position, the character walks after
/// you. The step is fed to the game as movement input (so collisions, slopes and networking all stay the game's),
/// and the tracking origin follows the distance the character actually covered, so your view does not move twice.
/// </summary>
internal sealed class RoomscaleLocomotion
{
    private const float Deadzone = 0.12f;
    private const float FullSpeedDistance = 0.3f;
    private const float MaxPlausibleStep = 1f;
    private const float PlayerStickThreshold = 0.1f;

    private Vector3 _lastBodyPosition;
    private bool _hasLastPosition;
    private bool _drivingLastFrame;

    /// <summary>
    /// Adds a walk input toward the head (body-frame stick: x right, y forward) to the player's own stick input.
    /// </summary>
    /// <param name="headLocal">Head position relative to the recentered origin, in the view (body) frame.</param>
    public Vector2 AddWalkInput(Vector2 playerStick, Vector3 headLocal)
    {
        var offset = new Vector2(headLocal.x, headLocal.z);
        var distance = offset.magnitude;
        _drivingLastFrame = false;
        if (distance <= Deadzone || playerStick.magnitude > PlayerStickThreshold)
            return playerStick;

        var strength = Mathf.Clamp01((distance - Deadzone) / (FullSpeedDistance - Deadzone));
        _drivingLastFrame = true;
        return Vector2.ClampMagnitude(playerStick + offset / distance * strength, 1f);
    }

    /// <summary>
    /// Returns how far the character moved since the last frame because of room-scale walking, expressed in the
    /// view frame, so the caller can move the tracking origin by the same amount.
    /// </summary>
    public Vector3 ConsumeCharacterStep(Vector3 bodyPosition, Quaternion viewRotation)
    {
        var step = Vector3.zero;
        if (_hasLastPosition && _drivingLastFrame)
        {
            var world = bodyPosition - _lastBodyPosition;
            world.y = 0f;
            if (world.magnitude < MaxPlausibleStep)
                step = Quaternion.Inverse(viewRotation) * world;
        }
        _lastBodyPosition = bodyPosition;
        _hasLastPosition = true;
        // Only the frame room-scale actually drove counts. Frames where it didn't run at all (gamepad in use, menu
        // open) must not count either: a flag left set there turned the stick's running into origin shifts, and the
        // view stayed behind while the body ran ahead.
        _drivingLastFrame = false;
        return step;
    }

    public void Reset()
    {
        _hasLastPosition = false;
        _drivingLastFrame = false;
    }
}
