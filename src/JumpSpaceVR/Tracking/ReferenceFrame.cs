using UnityEngine;

namespace JumpSpaceVR.Tracking;

/// <summary>
/// The "level" the view is held to. On foot that is the world, but inside a ship that rolls or flips (and in
/// zero-g) the character's up is the ship floor, not the sky: levelling to world up made the interior spin around
/// the player, and an upside-down ship flipped the recenter direction. This frame follows the character's up and
/// turns with whatever the character stands on (its parent), so yaw, levelling and turning are measured on the
/// character's own floor.
/// </summary>
internal sealed class ReferenceFrame
{
    private Transform _parent;
    private Quaternion _parentRotation = Quaternion.identity;

    public Quaternion Rotation { get; private set; } = Quaternion.identity;

    /// <summary>Angle between the frame's up and world up (degrees, for tests).</summary>
    public float Tilt => Vector3.Angle(Rotation * Vector3.up, Vector3.up);

    /// <summary>Follows the character for this frame; null (menus, loading) returns to the world frame.</summary>
    public void Update(Transform character)
    {
        if (character == null)
        {
            Rotation = Quaternion.identity;
            _parent = null;
            return;
        }

        // Turn with the ship the character stands in (a parent that doesn't move changes nothing).
        var parent = character.parent;
        if (parent != null && parent == _parent)
            Rotation = parent.rotation * Quaternion.Inverse(_parentRotation) * Rotation;
        _parent = parent;
        _parentRotation = parent != null ? parent.rotation : Quaternion.identity;

        // Then tilt the smallest amount that puts the frame's up on the character's up. Frame-to-frame changes are
        // small, so this never hits the ambiguous half-turn case, even when the ship ends up upside down.
        Rotation = Quaternion.FromToRotation(Rotation * Vector3.up, character.up) * Rotation;
        Rotation = Quaternion.Normalize(Rotation);
    }

    /// <summary>Yaw of a world rotation around this frame's up, in degrees.</summary>
    public float YawOf(Quaternion world) => YawIn(Rotation, world);

    /// <summary>A rotation that faces <paramref name="yaw"/> degrees around this frame's up, level with its floor.</summary>
    public Quaternion Level(float yaw) => Rotation * Quaternion.Euler(0f, yaw, 0f);

    public static float YawIn(Quaternion frame, Quaternion world)
    {
        var local = Quaternion.Inverse(frame) * world;
        var forward = local * Vector3.forward;
        // Looking straight along the frame's up, forward says nothing about yaw; the top of the view does.
        if (forward.x * forward.x + forward.z * forward.z < 1e-4f)
            forward = local * (forward.y > 0f ? Vector3.down : Vector3.up);
        return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
    }
}
