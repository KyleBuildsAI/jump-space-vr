using UnityEngine;

namespace JumpSpaceVR.Controls;

/// <summary>
/// Turns the character's body after the player's head when they physically turn (players reported having to use the
/// right stick a lot because the body kept facing where it started). Once the head is turned past a threshold from the
/// body, the body catches up at a steady rate until it faces the head again. The caller turns the tracking origin by
/// the same angle, so the view never moves: only the body (and so walking direction) turns underneath it.
/// </summary>
internal sealed class BodyFollowsHead
{
    private const float StartAngle = 35f;
    private const float StopAngle = 4f;
    private const float DegreesPerSecond = 150f;

    private bool _turning;

    /// <summary>True while the body is catching up with the head (for tests).</summary>
    public bool Turning => _turning;

    /// <summary>
    /// The body turn for this frame (degrees), given the head's yaw relative to the body.
    /// </summary>
    public float Step(float headYaw, float deltaTime)
    {
        var angle = Mathf.Abs(headYaw);
        if (!_turning && angle > StartAngle)
            _turning = true;
        else if (_turning && angle < StopAngle)
            _turning = false;
        if (!_turning)
            return 0f;
        return Mathf.Sign(headYaw) * Mathf.Min(angle, DegreesPerSecond * deltaTime);
    }

    public void Reset() => _turning = false;
}
