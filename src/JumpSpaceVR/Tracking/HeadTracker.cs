using JumpSpaceVR.OpenXR;
using UnityEngine;
using UnityEngine.XR;

namespace JumpSpaceVR.Tracking;

/// <summary>
/// Reads the headset pose from the XR input subsystem and keeps a recenter reference so the
/// player's seated forward direction maps onto the game camera's forward direction.
/// </summary>
internal sealed class HeadTracker
{
    private const string PositionUsage = "CenterEyePosition";
    private const string RotationUsage = "CenterEyeRotation";
    private const string FallbackPositionUsage = "DevicePosition";
    private const string FallbackRotationUsage = "DeviceRotation";

    private Vector3 _originPosition;
    private Quaternion _originYawInverse = Quaternion.identity;
    private bool _hasOrigin;
    private Vector3 _rawPosition;

    public bool IsTracking { get; private set; }

    /// <summary>Headset pose relative to the recentered origin, in tracking-space meters.</summary>
    public Vector3 LocalPosition { get; private set; }
    public Quaternion LocalRotation { get; private set; } = Quaternion.identity;

    public void Update()
    {
        IsTracking = TryReadRawPose(out var rawPosition, out var rawRotation);
        if (!IsTracking)
            return;

        if (!_hasOrigin)
            SetOrigin(rawPosition, rawRotation);

        _rawPosition = rawPosition;
        LocalPosition = _originYawInverse * (rawPosition - _originPosition);
        LocalRotation = _originYawInverse * rawRotation;
    }

    public void Recenter()
    {
        if (TryReadRawPose(out var rawPosition, out var rawRotation))
        {
            SetOrigin(rawPosition, rawRotation);
            Log.Msg("Recentered headset.");
        }
        else
        {
            _hasOrigin = false;
        }
    }

    /// <summary>
    /// Re-expresses an eye view (tracking space) relative to the recentered origin. With positional
    /// tracking off, head translation is removed but the eye's offset from the head (IPD) is kept.
    /// </summary>
    /// <summary>
    /// Turns the recentered frame by <paramref name="degrees"/> of yaw. Paired with turning the body by the same angle,
    /// the view stays exactly where it is while the body turns underneath it (body follows head).
    /// </summary>
    public void TurnOrigin(float degrees)
    {
        if (!_hasOrigin)
            return;
        var turn = Quaternion.Euler(0f, -degrees, 0f);
        _originYawInverse = turn * _originYawInverse;
        LocalPosition = turn * LocalPosition;
        LocalRotation = turn * LocalRotation;
    }

    public EyeView Localize(in EyeView raw, bool positionalTracking)
    {
        var localized = raw;
        var trackedPosition = positionalTracking ? raw.Position : raw.Position - _rawPosition + _originPosition;
        localized.Position = _originYawInverse * (trackedPosition - _originPosition);
        localized.Rotation = _originYawInverse * raw.Rotation;
        return localized;
    }

    /// <summary>
    /// Moves the recentered origin by a distance given in the recentered (body) frame, e.g. after the character
    /// walked to where the player physically stepped.
    /// </summary>
    public void ShiftOrigin(Vector3 localDelta)
    {
        if (!_hasOrigin)
            return;
        _originPosition += Quaternion.Inverse(_originYawInverse) * localDelta;
    }

    /// <summary>Re-expresses a controller pose (tracking space) relative to the recentered origin.</summary>
    public TrackedPose Localize(in TrackedPose raw, bool positionalTracking)
    {
        if (!raw.IsValid)
            return raw;

        var localized = raw;
        // Without positional tracking the head is pinned, so hands are kept relative to the head instead.
        var trackedPosition = positionalTracking ? raw.Position : raw.Position - _rawPosition + _originPosition;
        localized.Position = _originYawInverse * (trackedPosition - _originPosition);
        localized.Rotation = _originYawInverse * raw.Rotation;
        return localized;
    }

    private void SetOrigin(Vector3 rawPosition, Quaternion rawRotation)
    {
        var yaw = rawRotation.eulerAngles.y;
        _originYawInverse = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f));
        _originPosition = rawPosition;
        _rawPosition = rawPosition;
        _hasOrigin = true;
    }

    private static bool TryReadRawPose(out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        var device = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (!device.isValid)
            return false;

        var deviceId = device.deviceId;
        var hasRotation = XrInputNative.TryGet(deviceId, RotationUsage, out rotation)
                          || XrInputNative.TryGet(deviceId, FallbackRotationUsage, out rotation);
        var hasPosition = XrInputNative.TryGet(deviceId, PositionUsage, out position)
                          || XrInputNative.TryGet(deviceId, FallbackPositionUsage, out position);

        return hasRotation && hasPosition;
    }
}
