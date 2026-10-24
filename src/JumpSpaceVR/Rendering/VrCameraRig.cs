using JumpSpaceVR.OpenXR;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.XR;

namespace JumpSpaceVR.Rendering;

/// <summary>NVIDIA DLSS on the eye cameras (HDRP's built-in DLSS, which the game ships).</summary>
internal enum DlssMode
{
    /// <summary>Whatever the game's own graphics settings choose (both eyes kept the same).</summary>
    Game,
    Off,
    Quality,
    Balanced,
    Performance,
    UltraPerformance,
}

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
    private Transform _stableAnchor;
    private Vector3 _smoothedAnchorOffset;
    private bool _hasSmoothedOffset;
    private int _gameCullingMask;
    private bool _cullingMaskChanged;
    private float _gameFarClip;
    private bool _farClipChanged;

    // Names of the game's camera-shake layers, outermost first; the view is anchored above them.
    private static readonly string[] StableAnchorNames = { "CameraController", "CameraKnocker", "Crouch", "CameraRoot" };
    private const float AnchorOffsetSmoothingSeconds = 0.35f;
    private int _cameraInstanceId;
    private int _parentInstanceId;
    private HDAdditionalCameraData _sourceData;
    private HDAdditionalCameraData _rightEyeData;
    private int _framesUntilSettingsSync;

    private bool _offsetApplied;
    private Vector3 _baseLocalPosition;
    private Quaternion _baseLocalRotation;
    private Vector3 _appliedLocalPosition;
    private Quaternion _appliedLocalRotation;

    public Camera LeftEye => _camera;

    /// <summary>DLSS for both eyes, applied when the eyes are placed each frame.</summary>
    public DlssMode Dlss { get; set; } = DlssMode.Game;

    /// <summary>Rendering features for both eyes (frame settings), applied when it or the camera changes.</summary>
    public VrGraphicsQuality Quality { get; set; } = VrGraphicsQuality.Game;
    private readonly GraphicsQuality _graphicsQuality = new();
    private VrGraphicsQuality _appliedQuality = VrGraphicsQuality.Game;
    private HDAdditionalCameraData _appliedQualityData;
    private DlssMode _appliedDlss = DlssMode.Game;
    private HDAdditionalCameraData _appliedDlssData;
    private HDAdditionalCameraData _appliedDlssRightData;

    /// <summary>True when HDRP reports it can run DLSS for the left eye camera this frame.</summary>
    public bool DlssRunning => _sourceData != null && _sourceData.cameraCanRenderDLSS;

    /// <summary>Raised when the rig switches to a new game camera (level load, ship, cutscene).</summary>
    public event System.Action CameraChanged;

    /// <summary>Where the game's own camera was this frame (the point its first-person weapon is built around).</summary>
    public Vector3 GameCameraPosition { get; private set; }

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
        if (_cullingMaskChanged)
        {
            _camera.cullingMask = _gameCullingMask;
            _cullingMaskChanged = false;
        }
        if (_farClipChanged)
        {
            _camera.farClipPlane = _gameFarClip;
            _farClipChanged = false;
        }

        // Only undo what nothing else changed since we applied it. Position and rotation are separate: the game
        // moves the camera locally every frame (spring arm length, bob) but leaves its rotation to the parents,
        // and a leftover headset rotation would make aiming and the motion-aim loop follow the head.
        if (_cameraTransform.localPosition == _appliedLocalPosition)
            _cameraTransform.localPosition = _baseLocalPosition;
        if (_cameraTransform.localRotation == _appliedLocalRotation)
            _cameraTransform.localRotation = _baseLocalRotation;
    }

    /// <summary>
    /// Positions both eye cameras. Eye poses are relative to the recentered tracking origin, in meters.
    /// Call from the before-render hook, after all gameplay camera updates.
    /// </summary>
    public bool ApplyEyes(in EyeView left, in EyeView right, RenderTexture leftTarget, RenderTexture rightTarget, float worldScale,
        bool levelHorizon, bool stabilizeView, int extraCullingMask, float? bodyYaw = null, float viewDistance = 1f)
    {
        if (!EnsureCameras())
            return false;

        _baseLocalPosition = _cameraTransform.localPosition;
        _baseLocalRotation = _cameraTransform.localRotation;
        GameCameraPosition = _cameraTransform.position;
        var basePosition = stabilizeView ? StablePosition() : _cameraTransform.position;
        var baseRotation = _cameraTransform.rotation;
        GamePosition = basePosition;
        GameRotation = baseRotation;

        // Letting the game's camera pitch and roll the headset view is a major cause of VR sickness; with
        // levelling on, only the game's yaw turns your view and your head does all looking up and down.
        // With motion controls the body yaw is owned by the mod, so aiming the gun never turns the view.
        var viewRotation = bodyYaw.HasValue
            ? Quaternion.Euler(0f, bodyYaw.Value, 0f)
            : levelHorizon ? Quaternion.Euler(0f, baseRotation.eulerAngles.y, 0f) : baseRotation;
        ViewRotation = viewRotation;

        PlaceEye(_rightEye.transform, basePosition, viewRotation, right, worldScale);
        PlaceEye(_cameraTransform, basePosition, viewRotation, left, worldScale);
        _appliedLocalPosition = _cameraTransform.localPosition;
        _appliedLocalRotation = _cameraTransform.localRotation;
        _offsetApplied = true;

        _gameCullingMask = _camera.cullingMask;
        if (extraCullingMask != 0 && (_gameCullingMask & extraCullingMask) != extraCullingMask)
        {
            _camera.cullingMask = _gameCullingMask | extraCullingMask;
            _cullingMaskChanged = true;
        }

        // View distance: draw further than the game does (far clip plane; level of detail is scaled by the caller).
        _gameFarClip = _camera.farClipPlane;
        if (!Mathf.Approximately(viewDistance, 1f))
        {
            _camera.farClipPlane = _gameFarClip * viewDistance;
            _farClipChanged = true;
        }

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

    /// <summary>
    /// The game camera hangs below head-bob, recoil and knockback transforms. Anchoring the view on the camera
    /// controller above them, with the remaining offset low-pass filtered, keeps crouching and steady offsets
    /// but removes the high-frequency shake that is nauseating in a headset.
    /// </summary>
    private Vector3 StablePosition()
    {
        if (_stableAnchor == null)
            return _cameraTransform.position;

        var offset = _stableAnchor.InverseTransformPoint(_cameraTransform.position);
        if (!_hasSmoothedOffset)
        {
            _smoothedAnchorOffset = offset;
            _hasSmoothedOffset = true;
        }
        else
        {
            var blend = 1f - Mathf.Exp(-Time.unscaledDeltaTime / AnchorOffsetSmoothingSeconds);
            _smoothedAnchorOffset = Vector3.Lerp(_smoothedAnchorOffset, offset, blend);
        }
        return _stableAnchor.TransformPoint(_smoothedAnchorOffset);
    }

    private static Transform FindStableAnchor(Transform camera)
    {
        foreach (var name in StableAnchorNames)
        {
            for (var parent = camera.parent; parent != null; parent = parent.parent)
            {
                if (parent.name == name)
                    return parent;
            }
        }
        return null;
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
        {
            Adopt(mainCamera);
        }
        else
        {
            // The game re-parents the same camera between rigs (menu, cutscene, player, ship). That is a new
            // "forward" and a new shake anchor, just like a different camera.
            // The parent object itself may move with it, so compare the shake anchor above it instead.
            var anchor = FindStableAnchor(_cameraTransform);
            var parentId = anchor != null ? anchor.GetInstanceID() : 0;
            if (parentId != _parentInstanceId)
            {
                _parentInstanceId = parentId;
                _stableAnchor = anchor;
                _hasSmoothedOffset = false;
                Log.Msg($"Camera moved to a new rig (anchor: {(_stableAnchor != null ? _stableAnchor.name : "none")}).");
                CameraChanged?.Invoke();
            }
        }

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
        _stableAnchor = FindStableAnchor(camera.transform);
        _hasSmoothedOffset = false;
        _parentInstanceId = _stableAnchor != null ? _stableAnchor.GetInstanceID() : 0;

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
    /// <summary>
    /// Forces DLSS on (or off) for both eye cameras. DLSS renders at a lower internal resolution and reconstructs a
    /// sharp image, which is the main way to win back frame rate with two full HDRP renders per frame.
    /// </summary>
    private void ApplyDlss()
    {
        if (Dlss == DlssMode.Game || _sourceData == null)
            return;
        // Only touch the cameras when something changed: rewriting DLSS settings makes HDRP rebuild its DLSS state.
        if (Dlss == _appliedDlss && _sourceData == _appliedDlssData && _rightEyeData == _appliedDlssRightData)
            return;
        _appliedDlss = Dlss;
        _appliedDlssData = _sourceData;
        _appliedDlssRightData = _rightEyeData;
        var enabled = Dlss != DlssMode.Off;
        foreach (var data in new[] { _sourceData, _rightEyeData })
        {
            if (data == null)
                continue;
            data.allowDynamicResolution = enabled;
            data.allowDeepLearningSuperSampling = enabled;
            data.deepLearningSuperSamplingUseCustomQualitySettings = enabled;
            data.deepLearningSuperSamplingUseOptimalSettings = enabled;
            if (enabled)
                data.deepLearningSuperSamplingQuality = Dlss switch
                {
                    // HDRP's DLSS quality index: 0 performance, 1 balanced, 2 quality, 3 ultra performance.
                    DlssMode.Quality => 2u,
                    DlssMode.Balanced => 1u,
                    DlssMode.Performance => 0u,
                    _ => 3u,
                };
        }
        _camera.allowDynamicResolution = enabled;
    }

    private void ApplyQuality()
    {
        if (_sourceData == null || (Quality == _appliedQuality && _sourceData == _appliedQualityData))
            return;
        _graphicsQuality.Apply(_sourceData, Quality);
        _graphicsQuality.Apply(_rightEyeData, Quality);
        _appliedQuality = Quality;
        _appliedQualityData = _sourceData;
        Log.Msg($"VR graphics quality: {Quality}.");
    }

    private void SyncRightEyeSettings()
    {
        if (_sourceData != null && _rightEyeData != null && --_framesUntilSettingsSync <= 0)
        {
            _sourceData.CopyTo(_rightEyeData);
            _framesUntilSettingsSync = SettingsSyncIntervalFrames;
        }

        ApplyDlss();
        ApplyQuality();
        _rightEye.allowDynamicResolution = _camera.allowDynamicResolution;
        _rightEye.cullingMask = _camera.cullingMask;
        _rightEye.nearClipPlane = _camera.nearClipPlane;
        _rightEye.farClipPlane = _camera.farClipPlane;
        _rightEye.enabled = _camera.isActiveAndEnabled;
    }
}
