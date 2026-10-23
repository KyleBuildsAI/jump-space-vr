using UnityEngine;

namespace JumpSpaceVR.Controls;

/// <summary>How the right thumbstick turns the player's body while motion controls are on.</summary>
internal enum TurnMode
{
    /// <summary>Instant turns of a fixed angle (most comfortable).</summary>
    Snap,
    /// <summary>Continuous turning at a fixed speed.</summary>
    Smooth,
}

/// <summary>
/// Owns the player's body yaw while motion controls are on. The headset view is built on this yaw instead of the
/// game camera's, so pointing the gun around never rotates the world.
/// </summary>
internal sealed class BodyTurning
{
    private const float SnapThreshold = 0.75f;
    private const float SnapRearmThreshold = 0.35f;
    private const float SmoothDeadzone = 0.2f;

    private bool _snapArmed = true;

    /// <summary>World yaw of the body, in degrees.</summary>
    public float Yaw { get; private set; }

    public bool IsInitialized { get; private set; }

    public Quaternion Rotation => Quaternion.Euler(0f, Yaw, 0f);

    public void Reset(float yaw)
    {
        Yaw = yaw;
        IsInitialized = true;
        _snapArmed = true;
    }

    public void Update(float stickX, TurnMode mode, float snapAngle, float smoothSpeed, float deltaTime)
    {
        if (mode == TurnMode.Snap)
        {
            if (_snapArmed && Mathf.Abs(stickX) > SnapThreshold)
            {
                Yaw = Mathf.Repeat(Yaw + Mathf.Sign(stickX) * snapAngle, 360f);
                _snapArmed = false;
            }
            else if (Mathf.Abs(stickX) < SnapRearmThreshold)
            {
                _snapArmed = true;
            }
            return;
        }

        if (Mathf.Abs(stickX) > SmoothDeadzone)
        {
            var amount = (Mathf.Abs(stickX) - SmoothDeadzone) / (1f - SmoothDeadzone);
            Yaw = Mathf.Repeat(Yaw + Mathf.Sign(stickX) * amount * smoothSpeed * deltaTime, 360f);
        }
    }

    /// <summary>
    /// The game moves the character relative to its camera, which now points wherever the gun aims. Rotating the
    /// move stick by the difference keeps "forward" meaning forward for the body.
    /// </summary>
    public Vector2 BodyRelativeStick(Vector2 stick, float gameYaw)
    {
        var delta = Mathf.DeltaAngle(gameYaw, Yaw) * Mathf.Deg2Rad;
        var cos = Mathf.Cos(delta);
        var sin = Mathf.Sin(delta);
        // Rotate clockwise by delta (Unity yaw is clockwise when seen from above).
        return new Vector2(stick.x * cos + stick.y * sin, -stick.x * sin + stick.y * cos);
    }
}
