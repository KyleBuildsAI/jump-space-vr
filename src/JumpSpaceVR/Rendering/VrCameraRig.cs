using JumpSpaceVR.Tracking;
using UnityEngine;
using UnityEngine.XR;

namespace JumpSpaceVR.Rendering;

/// <summary>
/// Drives the game's gameplay camera from the headset.
///
/// The game keeps full control of the camera (mouse/gamepad look, ship cameras, cutscenes). Just before
/// rendering, the headset pose is layered on top of whatever the game set, and the original pose is
/// restored at the start of the next frame so gameplay code never sees the head offset ("decoupled look").
/// </summary>
internal sealed class VrCameraRig
{
    private Camera _camera;
    private Transform _cameraTransform;

    private bool _offsetApplied;
    private Vector3 _baseLocalPosition;
    private Quaternion _baseLocalRotation;
    private Vector3 _appliedLocalPosition;
    private Quaternion _appliedLocalRotation;

    public Camera Camera => _camera;

    /// <summary>Call early in the frame, before gameplay scripts read the camera transform.</summary>
    public void RestoreGamePose()
    {
        if (!_offsetApplied)
            return;

        _offsetApplied = false;
        if (_cameraTransform == null)
            return;

        // Only undo our offset if nothing else moved the camera since we applied it.
        if (_cameraTransform.localPosition == _appliedLocalPosition && _cameraTransform.localRotation == _appliedLocalRotation)
        {
            _cameraTransform.localPosition = _baseLocalPosition;
            _cameraTransform.localRotation = _baseLocalRotation;
        }
    }

    /// <summary>Call from Application.onBeforeRender, after all gameplay camera updates.</summary>
    public void ApplyHeadPose(HeadTracker head, bool positionalTracking, float worldScale)
    {
        if (!EnsureCamera() || !head.IsTracking)
            return;

        _baseLocalPosition = _cameraTransform.localPosition;
        _baseLocalRotation = _cameraTransform.localRotation;

        var position = _baseLocalPosition;
        if (positionalTracking)
        {
            var parentScale = _cameraTransform.parent != null ? _cameraTransform.parent.lossyScale.x : 1f;
            var scale = parentScale != 0f ? worldScale / parentScale : worldScale;
            position += _baseLocalRotation * (head.LocalPosition * scale);
        }

        _appliedLocalPosition = position;
        _appliedLocalRotation = _baseLocalRotation * head.LocalRotation;
        _cameraTransform.localPosition = _appliedLocalPosition;
        _cameraTransform.localRotation = _appliedLocalRotation;
        _offsetApplied = true;
    }

    /// <summary>Hands the camera back to flat-screen rendering.</summary>
    public void Release()
    {
        RestoreGamePose();
        if (_camera != null)
        {
            XRDevice.DisableAutoXRCameraTracking(_camera, false);
        }
        _camera = null;
        _cameraTransform = null;
    }

    private bool EnsureCamera()
    {
        var mainCamera = Camera.main;
        if (mainCamera == null)
            return false;

        if (mainCamera != _camera)
            Adopt(mainCamera);

        return _cameraTransform != null;
    }

    private void Adopt(Camera camera)
    {
        if (_camera != null)
            Release();

        _camera = camera;
        _cameraTransform = camera.transform;
        _offsetApplied = false;

        // We apply the head pose ourselves so the game's own camera motion is preserved.
        XRDevice.DisableAutoXRCameraTracking(camera, true);
        // stereoTargetEye defaults to Both; its setter is stripped from this build and must not be called.

        Log.Msg($"VR camera attached to '{camera.name}' (parent: {(camera.transform.parent != null ? camera.transform.parent.name : "none")}).");
    }
}
