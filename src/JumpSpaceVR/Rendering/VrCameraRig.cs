using JumpSpaceVR.OpenXR;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.XR;

namespace JumpSpaceVR.Rendering;

/// <summary>
/// Turns the game's gameplay camera into a stereo pair.
///
/// The game keeps full control of its camera (mouse/gamepad look, ship cameras, cutscenes). Just before
/// rendering, the camera is moved to the left eye pose layered on top of whatever the game set, and a
/// companion camera is placed at the right eye. The original pose is restored at the start of the next
/// frame so gameplay code never sees the head offset ("decoupled look").
/// </summary>
internal sealed class VrCameraRig
{
    private const string RightEyeName = "JumpSpaceVR_RightEye";

    private Camera _camera;
    private Transform _cameraTransform;
    private Camera _rightEye;

    private bool _offsetApplied;
    private Vector3 _baseLocalPosition;
    private Quaternion _baseLocalRotation;
    private Vector3 _appliedLocalPosition;
    private Quaternion _appliedLocalRotation;

    public Camera LeftEye => _camera;
    public Camera RightEye => _rightEye;

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

    /// <summary>
    /// Positions both eye cameras. Eye poses are relative to the recentered tracking origin, in meters.
    /// Call from the before-render hook, after all gameplay camera updates.
    /// </summary>
    public bool ApplyEyes(in EyeView left, in EyeView right, RenderTexture leftTarget, RenderTexture rightTarget, float worldScale)
    {
        if (!EnsureCameras())
            return false;

        _baseLocalPosition = _cameraTransform.localPosition;
        _baseLocalRotation = _cameraTransform.localRotation;
        var basePosition = _cameraTransform.position;
        var baseRotation = _cameraTransform.rotation;

        PlaceEye(_rightEye.transform, basePosition, baseRotation, right, worldScale);
        PlaceEye(_cameraTransform, basePosition, baseRotation, left, worldScale);
        _appliedLocalPosition = _cameraTransform.localPosition;
        _appliedLocalRotation = _cameraTransform.localRotation;
        _offsetApplied = true;

        SyncRightEyeSettings();
        _camera.targetTexture = leftTarget;
        _rightEye.targetTexture = rightTarget;
        _camera.projectionMatrix = left.ProjectionMatrix(_camera.nearClipPlane, _camera.farClipPlane);
        _rightEye.projectionMatrix = right.ProjectionMatrix(_camera.nearClipPlane, _camera.farClipPlane);
        return true;
    }

    /// <summary>Hands the camera back to flat-screen rendering.</summary>
    public void Release()
    {
        RestoreGamePose();
        if (_camera != null)
        {
            _camera.targetTexture = null;
            _camera.ResetProjectionMatrix();
            XRDevice.DisableAutoXRCameraTracking(_camera, false);
        }
        if (_rightEye != null)
            Object.Destroy(_rightEye.gameObject);

        _camera = null;
        _cameraTransform = null;
        _rightEye = null;
    }

    private static void PlaceEye(Transform eye, Vector3 basePosition, Quaternion baseRotation, in EyeView view, float worldScale)
    {
        eye.SetPositionAndRotation(basePosition + baseRotation * (view.Position * worldScale), baseRotation * view.Rotation);
    }

    private bool EnsureCameras()
    {
        var mainCamera = Camera.main;
        if (mainCamera == null)
            return false;

        if (mainCamera != _camera || _rightEye == null)
            Adopt(mainCamera);

        return _cameraTransform != null && _rightEye != null;
    }

    private void Adopt(Camera camera)
    {
        if (_camera != null)
            Release();

        _camera = camera;
        _cameraTransform = camera.transform;
        _offsetApplied = false;

        // Unity's implicit XR camera tracking would fight the game's camera; we place the eyes ourselves.
        XRDevice.DisableAutoXRCameraTracking(camera, true);

        var rightEyeObject = new GameObject(RightEyeName);
        Object.DontDestroyOnLoad(rightEyeObject);
        _rightEye = rightEyeObject.AddComponent<Camera>();
        _rightEye.CopyFrom(camera);
        XRDevice.DisableAutoXRCameraTracking(_rightEye, true);

        var sourceData = camera.GetComponent<HDAdditionalCameraData>();
        if (sourceData != null)
        {
            var rightData = rightEyeObject.AddComponent<HDAdditionalCameraData>();
            sourceData.CopyTo(rightData);
        }

        Log.Msg($"Stereo cameras attached to '{camera.name}' (parent: {(camera.transform.parent != null ? camera.transform.parent.name : "none")}).");
    }

    /// <summary>The game changes FOV, clip planes and culling at runtime (zoom, ship view, menus).</summary>
    private void SyncRightEyeSettings()
    {
        _rightEye.cullingMask = _camera.cullingMask;
        _rightEye.nearClipPlane = _camera.nearClipPlane;
        _rightEye.farClipPlane = _camera.farClipPlane;
        _rightEye.enabled = _camera.isActiveAndEnabled;
    }
}
