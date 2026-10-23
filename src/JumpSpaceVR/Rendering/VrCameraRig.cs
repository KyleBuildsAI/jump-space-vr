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
    private const int SettingsSyncIntervalFrames = 15;

    private Camera _camera;
    private Transform _cameraTransform;
    private Camera _rightEye;
    private int _cameraInstanceId;
    private HDAdditionalCameraData _sourceData;
    private HDAdditionalCameraData _rightEyeData;
    private int _framesUntilSettingsSync;

    private bool _offsetApplied;
    private Vector3 _baseLocalPosition;
    private Quaternion _baseLocalRotation;
    private Vector3 _appliedLocalPosition;
    private Quaternion _appliedLocalRotation;

    public Camera LeftEye => _camera;

    /// <summary>Raised when the rig switches to a new game camera (level load, ship, cutscene).</summary>
    public event System.Action CameraChanged;

    /// <summary>World pose the game gave its camera this frame, before the head offset.</summary>
    public Vector3 GamePosition { get; private set; }
    public Quaternion GameRotation { get; private set; } = Quaternion.identity;

    /// <summary>Rotation the headset view is built on: the game's yaw only when the horizon is levelled.</summary>
    public Quaternion ViewRotation { get; private set; } = Quaternion.identity;
    public Camera RightEye => _rightEye;

    /// <summary>Call early in the frame, before gameplay scripts read the camera transform.</summary>
    public void RestoreGamePose()
    {
        if (!_offsetApplied)
            return;

        _offsetApplied = false;
        if (_cameraTransform == null)
            return;

        // Gameplay code (aim rays, on-screen markers, UI anchoring) must see the flat camera: screen-sized and
        // with the game's own field of view, not the eye texture and asymmetric eye projection.
        _camera.targetTexture = null;
        _camera.ResetProjectionMatrix();

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
    public bool ApplyEyes(in EyeView left, in EyeView right, RenderTexture leftTarget, RenderTexture rightTarget, float worldScale,
        bool levelHorizon)
    {
        if (!EnsureCameras())
            return false;

        _baseLocalPosition = _cameraTransform.localPosition;
        _baseLocalRotation = _cameraTransform.localRotation;
        var basePosition = _cameraTransform.position;
        var baseRotation = _cameraTransform.rotation;
        GamePosition = basePosition;
        GameRotation = baseRotation;

        // Letting the game's camera pitch and roll the headset view is a major cause of VR sickness; with
        // levelling on, only the game's yaw turns your view and your head does all looking up and down.
        var viewRotation = levelHorizon ? Quaternion.Euler(0f, baseRotation.eulerAngles.y, 0f) : baseRotation;
        ViewRotation = viewRotation;

        PlaceEye(_rightEye.transform, basePosition, viewRotation, right, worldScale);
        PlaceEye(_cameraTransform, basePosition, viewRotation, left, worldScale);
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
        _cameraInstanceId = 0;
        _sourceData = null;
        _rightEyeData = null;
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

        // Il2Cpp wrapper objects are not reference-stable, so compare engine instance IDs.
        if (mainCamera.GetInstanceID() != _cameraInstanceId || _rightEye == null)
            Adopt(mainCamera);

        return _cameraTransform != null && _rightEye != null;
    }

    private void Adopt(Camera camera)
    {
        if (_camera != null)
            Release();

        DestroyStaleEyes();

        _camera = camera;
        _cameraTransform = camera.transform;
        _cameraInstanceId = camera.GetInstanceID();
        _offsetApplied = false;

        // Unity's implicit XR camera tracking would fight the game's camera; we place the eyes ourselves.
        XRDevice.DisableAutoXRCameraTracking(camera, true);

        var rightEyeObject = new GameObject(RightEyeName);
        Object.DontDestroyOnLoad(rightEyeObject);
        _rightEye = rightEyeObject.AddComponent<Camera>();
        _rightEye.CopyFrom(camera);
        XRDevice.DisableAutoXRCameraTracking(_rightEye, true);

        _sourceData = camera.GetComponent<HDAdditionalCameraData>();
        if (_sourceData != null)
            _rightEyeData = rightEyeObject.AddComponent<HDAdditionalCameraData>();
        _framesUntilSettingsSync = 0;

        Log.Msg($"Stereo cameras attached to '{camera.name}' (parent: {(camera.transform.parent != null ? camera.transform.parent.name : "none")}).");
        CameraChanged?.Invoke();
    }

    private static void DestroyStaleEyes()
    {
        GameObject stale;
        while ((stale = GameObject.Find(RightEyeName)) != null)
            Object.DestroyImmediate(stale);
    }

    /// <summary>
    /// The game changes clip planes, culling, volume masks and anti-aliasing on its camera at runtime
    /// (menus, ship view, settings), so the right eye is kept in sync with the left.
    /// </summary>
    private void SyncRightEyeSettings()
    {
        if (_sourceData != null && _rightEyeData != null && --_framesUntilSettingsSync <= 0)
        {
            _sourceData.CopyTo(_rightEyeData);
            _framesUntilSettingsSync = SettingsSyncIntervalFrames;
        }

        _rightEye.cullingMask = _camera.cullingMask;
        _rightEye.nearClipPlane = _camera.nearClipPlane;
        _rightEye.farClipPlane = _camera.farClipPlane;
        _rightEye.enabled = _camera.isActiveAndEnabled;
    }
}
