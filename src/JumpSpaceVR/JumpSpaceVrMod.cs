using System;
using System.IO;
using JumpSpaceVR.Controls;
using JumpSpaceVR.OpenXR;
using UnityEngine.InputSystem;
using JumpSpaceVR.Rendering;
using JumpSpaceVR.Tracking;
using MelonLoader;
using UnityEngine;
using UnityEngine.XR;

[assembly: MelonInfo(typeof(JumpSpaceVR.JumpSpaceVrMod), "JumpSpaceVR", "0.12.1", "Kyle Coleman")]
[assembly: MelonGame("Keepsake Games", "Jump Space")]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace JumpSpaceVR;

/// <summary>
/// Entry point. Boots OpenXR inside the game's HDRP renderer and drives the gameplay camera from the headset.
/// </summary>
public sealed class JumpSpaceVrMod : MelonMod
{
    private const int StartupDelayFrames = 120;
    private const int AutoDiagnosticsDelayFrames = 1200;
    private const float RetryIntervalSeconds = 10f;

    private float _nextRetryTime;
    private bool _retryAnnounced;

    private ModConfig _config;
    private OpenXRRuntime _runtime;
    private readonly HeadTracker _headTracker = new();
    private readonly VrCameraRig _cameraRig = new();
    private readonly StereoRenderer _stereoRenderer = new();
    private readonly VrUi.UiCompositor _uiCompositor = new();
    private readonly VirtualGamepad _gamepad = new();
    private readonly PhysicalGamepads _pads = new(VirtualGamepad.DeviceName);
    private Testing.TestGamepad _testPad;
    private readonly VrUi.ModMenu _modMenu = new();
    private readonly MotionControls _motion = new();
    private readonly RoomscaleLocomotion _roomscale = new();
    private readonly VirtualMouse _mouse = new();
    private readonly VrUi.CinematicBars _cinematicBars = new();
    private readonly SessionReport _sessionReport = new();
    private readonly VrUi.FrameRateCounter _frameRateCounter = new();
    private readonly VrUi.CrosshairHider _crosshairHider = new();
    private readonly Tracking.HeadAudio _headAudio = new();
    private readonly HapticFeedback _haptics = new();
    private readonly ColorGrading _colorGrading = new();
    private readonly Multiplayer.HandSync _handSync = new();
    private readonly VrUi.PointerAssist _pointerAssist = new();
    private readonly Rendering.FirstPersonOverride _firstPerson = new();
    private readonly Tracking.ReferenceFrame _frame = new();
    private readonly ShadowBudget _shadowBudget = new();
    private readonly ComfortVignette _vignette = new();
    private readonly RealCrouch _realCrouch = new();
    private readonly GameInputPause _gameInput = new();
    private readonly BodyFollowsHead _bodyFollow = new();
    private bool _frameTilted;
    private float _aimDownSights;
    private Vector3 _lastSightShift;
    private ControllerState _rawControllerState;
    private Vector2? _pointerScreen;
    private bool _pointerOwnsCursor;

    // Eye height over the last ~1.5 s, so tests can measure sudden vertical view jolts (bob, steps, landing) in cm/s.
    private readonly Diagnostics.RollingSamples _eyeHeights = new(90);
    private CursorLockMode? _gameCursorLock;
    private bool _resetBodyYaw = true;
    private float _menuChordStart = -1f;
    private bool _menuChordFired;
    private const float MenuChordSeconds = 1f;
    private const float RoomscaleLostDistance = 1.2f;
    private int _cutsceneCheckFrame;
    private bool _cutscene;
    private readonly ConformanceInputDriver _mockInput = new();
    private Testing.ScenarioRunner _scenario;
    private readonly HotkeyReader _hotkeys = new();

    private bool _vrRequested;
    private bool _beforeRenderHooked;
    private int _frameCount;
    private bool _recenterPending = true;

    public override void OnInitializeMelon()
    {
        Log.Bind(LoggerInstance);
        _config = new ModConfig();
        _vrRequested = _config.StartInVr.Value;

        var pluginsDirectory = Path.Combine(Application.dataPath, "Plugins", "x86_64");
        OpenXRNative.InstallResolver(pluginsDirectory);

        if (!File.Exists(Path.Combine(pluginsDirectory, "UnityOpenXR.dll")))
            Log.Error($"UnityOpenXR.dll is missing from {pluginsDirectory}. Run scripts/install.ps1 again.");

        BuildModMenu();
        if (!string.IsNullOrWhiteSpace(_config.ScenarioFile.Value))
        {
            _scenario = new Testing.ScenarioRunner(_mockInput, ScenarioMetric, ScenarioCommand);
            if (!_scenario.Load(_config.ScenarioFile.Value))
                _scenario = null;
        }
        if (_config.DebugAimOffset.Value)
        {
            MotionControls.DebugPointerRotation = Quaternion.Euler(_config.DebugAimPitch.Value, _config.DebugAimYaw.Value, 0f);
            MotionControls.DebugHandOffset = new Vector3(0.25f, -0.3f, 0.45f);
        }
        Diagnostics.DescribeMotion = () =>
        {
            var c = _lastControllerState;
            return _motion.Describe() + $" rightHand={c.RightHand.IsValid}:{c.RightHand.Position} rightPointer={c.RightPointer.IsValid}";
        };

        // A new level or camera means a new "forward"; line the headset up with where the player is looking.
        // Entering or leaving the game's third-person mode swaps cameras too; the view stays in the head then, so
        // recentering would only jolt it.
        _cameraRig.CameraChanged += () =>
        {
            _vignette.ResetMotion();
            if (!_firstPerson.RecentlyActive)
                _recenterPending = true;
        };

        Log.Msg($"Loaded. Toggle VR: {_config.ToggleVrKey.Value}, recenter: {_config.RecenterKey.Value}, diagnostics: {_config.DiagnosticsKey.Value}.");
    }

    public override void OnUpdate()
    {
        _frameCount++;
        Diagnostics.RecordFrame();
        FrameTimings.Record();
        _cameraRig.RestoreGamePose();
        _motion.RestoreWeapon();
        _cinematicBars.Update(_runtime != null && _runtime.IsRendering && _config.HideCinematicBars.Value);
        _frameRateCounter.Update(_config.ShowFrameRate.Value);
        _colorGrading.Apply(_runtime != null && _runtime.IsRendering ? _config.ColorPreset.Value : ColorPreset.Off);
        _shadowBudget.Apply(_runtime != null && _runtime.IsRendering ? ShadowBudget.DistanceFor(_config.ShadowDistance.Value, _config.GraphicsQuality.Value) : 0f);
        // Render scale applies right away, whichever way it was changed (menu, settings file, test scenario).
        if (_runtime != null && _runtime.IsRendering && !Mathf.Approximately(XRSettings.eyeTextureResolutionScale, _config.ClampedRenderScale))
            XRSettings.eyeTextureResolutionScale = _config.ClampedRenderScale;
        _crosshairHider.Update(_runtime != null && _runtime.IsRendering && !_config.ShowCrosshair.Value);
        ApplyLodDistance(_runtime != null && _runtime.IsRendering ? _config.ClampedViewDistance : 1f);
        // After the restore, so scenario checks see the game's own camera rather than the headset view.
        _scenario?.Update();
        UpdatePhysicalGamepads();
        HandleHotkeys();
        UpdateControllers();
        if (_runtime != null && _runtime.IsRendering)
            _handSync.Update();
        if (_runtime != null && _runtime.IsRendering)
            _sessionReport.RecordFrame(Time.unscaledDeltaTime, _rawControllerState, _pointerScreen.HasValue, IsInLevel(),
                _motion.WeaponInHand, _eyeHeights.MaxSpeedChange * 100f);

        // The runtime ended the session (headset disconnected, runtime closed): tear down and retry.
        if (_runtime != null && _runtime.SessionLost)
        {
            Log.Warning("VR session ended by the runtime; waiting for the headset to come back.");
            DisableVr();
            _nextRetryTime = Time.realtimeSinceStartup + RetryIntervalSeconds;
        }

        if (_frameCount == StartupDelayFrames && _vrRequested)
            EnableVr();
        else if (_frameCount > StartupDelayFrames && _vrRequested && _runtime != null && !_runtime.IsInitialized
                 && Time.realtimeSinceStartup >= _nextRetryTime)
            EnableVr();

        // One automatic report shortly after startup makes user bug reports useful without extra steps.
        if (_frameCount == StartupDelayFrames + AutoDiagnosticsDelayFrames && _runtime != null)
        {
            Diagnostics.Dump(_runtime);
            if (_config.SnapshotOnStartup.Value)
                SaveSnapshots();
        }
    }

    /// <summary>One-click settings for mid-range cards (for example an RTX 4060/5060): every big cost turned down.</summary>
    private void ApplyLowEndPreset()
    {
        _config.GraphicsQuality.Value = VrGraphicsQuality.Low;
        _config.Dlss.Value = DlssMode.Performance;
        _config.RenderScale.Value = 1.0f;
        _config.ViewDistance.Value = 0.75f;
        MelonPreferences.Save();
        Log.Msg("Applied the slower graphics card preset (Low graphics, DLSS Performance, render scale 1.0, view distance 0.75).");
    }

    private void ResetAllSettings()
    {
        _config.ResetAll();
        XRSettings.eyeTextureResolutionScale = _config.ClampedRenderScale;
        _snapPanelToHead = true;
        Log.Msg("All Jump Space VR settings reset to their defaults.");
    }

    private void BuildModMenu()
    {
        _modMenu.BeginPage("View and comfort");
        _modMenu.AddToggle("Level horizon (comfort)", _config.LevelHorizon);
        _modMenu.AddToggle("Remove head bob and shake", _config.StabilizeView);
        _modMenu.AddToggle("Positional tracking (6DoF)", _config.PositionalTracking);
        _modMenu.AddToggle("Room-scale walking", _config.RoomscaleWalking);
        _modMenu.AddNumber("World scale", _config.WorldScale, 0.05f, 0.5f, 3f, "0.00");
        _modMenu.AddToggle("Hide cinematic bars", _config.HideCinematicBars);
        _modMenu.AddChoice("Comfort vignette", _config.ComfortVignette);
        _modMenu.AddToggle("Real crouch (duck to crouch)", _config.RealCrouch);
        _modMenu.AddToggle("Always first person", _config.ForceFirstPerson);
        _modMenu.EndPage();

        _modMenu.BeginPage("UI and HUD");
        _modMenu.AddChoice("UI follow mode", _config.UiFollow, _ => _snapPanelToHead = true);
        _modMenu.AddToggle("Curved UI", _config.CurvedUi);
        _modMenu.AddNumber("Helmet HUD size (m)", _config.HelmetUiWidth, 0.05f, 0.6f, 2.4f, "0.00");
        _modMenu.AddNumber("UI smoothing (s)", _config.UiSmoothing, 0.02f, 0f, 1f, "0.00");
        _modMenu.AddNumber("UI size (m)", _config.UiWidth, 0.1f, 0.6f, 6f, "0.0");
        _modMenu.AddNumber("UI distance (m)", _config.UiDistance, 0.1f, 0.5f, 5f, "0.0");
        _modMenu.AddToggle("Show frame rate", _config.ShowFrameRate);
        _modMenu.AddToggle("Show crosshair", _config.ShowCrosshair);
        _modMenu.EndPage();

        _modMenu.BeginPage("Graphics and performance");
        _modMenu.AddAction("Preset: slower graphics card", ApplyLowEndPreset);
        _modMenu.AddNumber("Render scale", _config.RenderScale, 0.1f, 0.5f, 3f, "0.0");
        _modMenu.AddChoice("VR graphics", _config.GraphicsQuality);
        _modMenu.AddChoice("DLSS (VR)", _config.Dlss);
        _modMenu.AddNumber("View distance", _config.ViewDistance, 0.25f, 0.5f, 4f, "0.00x");
        _modMenu.AddNumber("Shadow distance (m, 0 = auto)", _config.ShadowDistance, 10f, 0f, 300f, "0");
        _modMenu.AddChoice("Colour grading", _config.ColorPreset);
        _modMenu.AddToggle("Stereo weapon", _config.StereoWeapon);
        _modMenu.AddChoice("VR runtime (F8 twice to apply)", _config.Runtime);
        _modMenu.EndPage();

        _modMenu.BeginPage("Controls");
        _modMenu.AddChoice("Controls (VR controllers or gamepad)", _config.ControllerMode);
        _modMenu.AddToggle("Motion controls (aim with hand)", _config.MotionControls);
        _modMenu.AddChoice("Turn mode", _config.TurnMode);
        _modMenu.AddToggle("Body follows head", _config.BodyFollowsHead);
        _modMenu.AddToggle("Look to interact (gun lowered)", _config.LookToInteract);
        _modMenu.AddNumber("Snap turn angle", _config.SnapTurnAngle, 15f, 15f, 90f, "0");
        _modMenu.AddNumber("Smooth turn speed", _config.SmoothTurnSpeed, 15f, 30f, 360f, "0");
        _modMenu.AddToggle("Laser pointer in menus", _config.LaserPointer);
        _modMenu.AddToggle("Laser snaps to buttons", _config.PointerAssist);
        _modMenu.AddToggle("VR controllers as gamepad", _config.ControllersAsGamepad);
        _modMenu.EndPage();

        _modMenu.BeginPage("Weapon and hands");
        _modMenu.AddToggle("Weapon in hand", _config.WeaponInHand);
        _modMenu.AddChoice("Aim down sights with eye", _config.AimEye);
        _modMenu.AddNumber("Left hand roll (degrees)", _config.LeftHandRoll, 15f, -180f, 180f, "0");
        _modMenu.AddToggle("Multiplayer hands (co-op)", _config.ShareHands);
        _modMenu.AddNumber("Weapon offset X (right)", _config.WeaponOffsetX, 0.01f, -0.5f, 0.5f, "0.00");
        _modMenu.AddNumber("Weapon offset Y (up)", _config.WeaponOffsetY, 0.01f, -0.6f, 0.4f, "0.00");
        _modMenu.AddNumber("Weapon offset Z (forward)", _config.WeaponOffsetZ, 0.01f, -0.3f, 0.8f, "0.00");
        _modMenu.EndPage();

        _modMenu.BeginPage("Sound and vibration");
        _modMenu.AddToggle("Head-tracked audio", _config.HeadTrackedAudio);
        _modMenu.AddToggle("Controller vibration", _config.Haptics);
        _modMenu.AddNumber("Vibration strength", _config.HapticStrength, 0.1f, 0f, 1f, "0.0");
        _modMenu.EndPage();

        _modMenu.AddAction("Recenter view", () => _recenterPending = true);
        _modMenu.AddAction("Close", () => _modMenu.Close());
        _modMenu.AddConfirmedAction("Reset all settings", ResetAllSettings);
    }

    /// <summary>
    /// Real gamepads: decides whether one is in use (see <see cref="ControllerMode"/>), and handles its shortcuts and
    /// fire rumble. Runs whether or not the VR controllers feed the game.
    /// </summary>
    private void UpdatePhysicalGamepads()
    {
        _testPad?.Update();
        var rendering = _runtime != null && _runtime.IsRendering;
        var wasActive = _pads.GamepadActive;
        _pads.Update(rendering ? _config.ControllerMode.Value : ControllerMode.MotionControllers, _rawControllerState);
        if (_pads.GamepadActive != wasActive)
            Log.Msg(_pads.GamepadActive ? $"Gamepad in use: {_pads.Current?.displayName ?? "gamepad"}." : "VR controllers in use.");
        if (!rendering)
        {
            _pads.StopRumble();
            return;
        }
        if (_pads.RecenterRequested)
            _recenterPending = true;
        if (_pads.MenuToggleRequested)
            _modMenu.Toggle();
        _pads.UpdateFireRumble(_config.Haptics.Value, _config.HapticStrength.Value,
            firing: _pads.GamepadActive && !_modMenu.IsOpen && IsInLevel() && _motion.HoldsItem);
    }

    /// <summary>Hold the right thumbstick click for a second to open or close the mod menu.</summary>
    private bool ProcessMenuChord(bool rightStickClick)
    {
        if (!rightStickClick)
        {
            var wasTap = _menuChordStart >= 0f && !_menuChordFired;
            _menuChordStart = -1f;
            _menuChordFired = false;
            return wasTap;
        }

        if (_menuChordStart < 0f)
            _menuChordStart = Time.unscaledTime;
        else if (!_menuChordFired && Time.unscaledTime - _menuChordStart >= MenuChordSeconds)
        {
            _menuChordFired = true;
            _modMenu.Toggle();
        }
        return false;
    }

    public override void OnSceneWasInitialized(int buildIndex, string sceneName)
    {
        if (_vrRequested)
            _recenterPending = true;
    }

    public override void OnApplicationQuit()
    {
        DisableVr();
        _runtime?.Shutdown();
    }

    /// <summary>Saves what the headset receives (eye + UI panel), plus the raw UI capture.</summary>
    private void SaveSnapshots()
    {
        EyeSnapshot.SaveSingle(_uiCompositor.UiTexture, "ui");
        // The eye images as rendered (the swapchain capture below can lag behind on some runtimes).
        if (_stereoRenderer.LeftTexture != null && _stereoRenderer.RightTexture != null)
            EyeSnapshot.Save(_stereoRenderer.LeftTexture, _stereoRenderer.RightTexture, "eye");
        _stereoRenderer.CaptureSubmittedFrame((left, right) => EyeSnapshot.Save(left, right, "headset", flipVertical: true));
    }

    /// <summary>Feeds VR controllers to the game as a gamepad. Both stick clicks together recenters.</summary>
    private void UpdateControllers()
    {
        if (!_vrRequested || _runtime == null || !_runtime.IsRendering || !_config.ControllersAsGamepad.Value)
        {
            // No fresh controller input this frame: don't let the menu or tests act on a stale held stick.
            _lastControllerState = default;
            _rawControllerState = default;
            return;
        }

        if (_config.MockControllerInput.Value || _scenario != null)
            _mockInput.Apply();

        var state = _runtime.Controllers.Read();
        _lastControllerState = state;
        _rawControllerState = state;
        if (!state.IsActive && _config.SyntheticControllers.Value)
            state = new ControllerState { IsActive = true, LeftStick = new Vector2(0.5f, 0f), RightTrigger = 1f, ButtonA = true };

        state.Menu = ProcessMenuButton(state.Menu);

        // Right stick click: a tap still reaches the game (briefly, on release); holding it opens the mod menu.
        var rightClickTap = ProcessMenuChord(state.RightStickClick && !state.LeftStickClick);
        state.RightStickClick = rightClickTap || (state.RightStickClick && state.LeftStickClick);

        // Laser pointer: while it touches the UI panel in a menu, the right trigger clicks instead of firing.
        if (_pointerScreen.HasValue)
        {
            // The game locks the cursor whenever the gamepad was used last, and the UI input module ignores a
            // locked pointer. Outside a level the laser owns the pointer, so keep it unlocked there.
            if (_pointerOwnsCursor && Cursor.lockState != CursorLockMode.None)
            {
                _gameCursorLock = Cursor.lockState;
                Cursor.lockState = CursorLockMode.None;
            }
            _mouse.Send(_pointerScreen.Value, state.RightTrigger > 0.5f);
            state.RightTrigger = 0f;
        }

        // Fire only counts in gameplay with something in hand (not while the laser clicks UI or the menu is open).
        _haptics.Update(_runtime.Controllers, _rawControllerState, _config.Haptics.Value, _config.HapticStrength.Value,
            firing: !_pointerScreen.HasValue && !_modMenu.IsOpen && IsInLevel() && _motion.HoldsItem,
            laserClick: _pointerScreen.HasValue, twoHanded: _motion.Arms.TwoHanded);

        if (_pads.GamepadActive)
        {
            // A real gamepad drives the game directly: no motion aim, so the view follows the game camera, and the
            // virtual gamepad stays released so it doesn't fight the real one.
            _motion.ProcessInput(ref state, false, _config.TurnMode.Value, _config.SnapTurnAngle.Value, _config.SmoothTurnSpeed.Value);
            _lastControllerState = state;
            _gamepad.Send(new ControllerState { IsActive = state.IsActive });
            return;
        }

        // The left grip grabs the weapon's fore-end; don't also press the game's shoulder button with it.
        if (_motion.Arms.TwoHanded || _motion.Arms.CanGrab)
            state.LeftGrip = 0f;

        if (_config.RoomscaleWalking.Value && _config.PositionalTracking.Value && !_modMenu.IsOpen)
            state.LeftStick = _roomscale.AddWalkInput(state.LeftStick, _headTracker.LocalPosition);

        // The galaxy map is steered with the sticks (it selects what is under its centre reticle), so motion aim and
        // stick turning step aside there.
        _motion.ProcessInput(ref state, _config.MotionControls.Value && !_modMenu.IsOpen && !_mapView, _config.TurnMode.Value,
            _config.SnapTurnAngle.Value, _config.SmoothTurnSpeed.Value);
        _lastControllerState = state;

        if (_modMenu.IsOpen)
        {
            // The menu owns the controllers while it is open; the game sees a released gamepad.
            _gamepad.Send(new ControllerState { IsActive = state.IsActive });
            return;
        }

        var recenterChord = state.LeftStickClick && state.RightStickClick;
        if (recenterChord && !_recenterChordHeld)
            _recenterPending = true;
        _recenterChordHeld = recenterChord;

        if (recenterChord)
        {
            state.LeftStickClick = false;
            state.RightStickClick = false;
        }
        if (_realCrouch.Press(_config.RealCrouch.Value && _config.PositionalTracking.Value, _headTracker.LocalPosition.y,
                RealCrouchAllowed(), LocalPlayerCrouching(), Il2Cpp.PlayerController.m_ToggleCrouch))
            state.ButtonB = true;
        _gamepad.Send(state);
    }

    /// <summary>
    /// Real crouching only on foot in first person: in seats, the pilot chair, the galaxy map and menus the crouch
    /// button (B) means exit or cancel.
    /// </summary>
    private bool RealCrouchAllowed()
    {
        var camera = Camera.main;
        return !_pointerScreen.HasValue && !_mapView && !_firstPerson.Active && IsInLevel() && camera != null &&
               camera.GetComponentInParent<Il2Cpp.CameraController_Character>() != null;
    }

    private static bool LocalPlayerCrouching()
    {
        var handler = Il2Cpp.PlayerHandler.GetPlayerHandler;
        var player = handler != null ? handler.localPlayerController : null;
        var blackboard = player != null ? player.BlackboardData : null;
        return blackboard != null && blackboard.m_IsCrouching.Value;
    }

    private bool _recenterChordHeld;

    /// <summary>Values a test scenario can wait for and check.</summary>
    private float? ScenarioMetric(string name)
    {
        var camera = Camera.main;
        float? cameraYaw = camera != null ? camera.transform.eulerAngles.y : null;
        float? cameraPitch = camera != null ? -Mathf.DeltaAngle(0f, camera.transform.eulerAngles.x) : null;
        return name switch
        {
            "fps" => Diagnostics.AverageFps,
            "eyeWidth" => XRSettings.eyeTextureWidth,
            "headAudioUpdates" => Tracking.HeadAudio.Updates,
            "colorGradingOn" => GameObject.Find("JumpSpaceVR_ColorGrading")?.GetComponent<UnityEngine.Rendering.Volume>() is { } volume && volume.enabled ? 1f : 0f,
            "hapticPulses" => _runtime?.Controllers.HapticPulses ?? 0,
            "gamepadActive" => _pads.GamepadActive ? 1f : 0f,
            "remoteHands" => _handSync.RemotesShown,
            "remoteHandErrorCm" => _handSync.RemotesShown > 0 ? _handSync.LastHandError * 100f : null,
            "handFramesSent" => _handSync.FramesSent,
            "handFramesReceived" => _handSync.FramesReceived,
            "firstPersonOverride" => _firstPerson.Active ? 1f : 0f,
            "mapView" => _mapView ? 1f : 0f,
            "vignette" => _vignette.Intensity,
            "realCrouch" => _realCrouch.Wants ? 1f : 0f,
            "gameCrouching" => LocalPlayerCrouching() ? 1f : 0f,
            "crouchLiftCm" => _realCrouch.Lift * 100f,
            "eyeY" => _cameraRig.LeftEye != null ? _cameraRig.LeftEye.transform.position.y : null,
            "planarProbes" => UnityEngine.Object.FindObjectsByType<UnityEngine.Rendering.HighDefinition.PlanarReflectionProbe>(FindObjectsSortMode.None).Length,
            "realtimeProbes" => CountRealtimeProbes(),
            "cameraX" => camera != null ? camera.transform.position.x : null,
            "cameraZ" => camera != null ? camera.transform.position.z : null,
            "viewPitch" => -Mathf.DeltaAngle(0f, _cameraRig.ViewRotation.eulerAngles.x),
            "frameTiltDeg" => _frame.Tilt,
            "cameraHeadDistanceCm" => _firstPerson.CameraDistance * 100f,
            "pointerTargets" => _pointerAssist.TargetCount,
            "pointerSnapped" => _pointerAssist.Snapped ? 1f : 0f,
            "bodyHeadCm" => _motion.WeaponInHand ? _motion.Arms.BodyHeadDistance * 100f : null,
            "bodyYawError" => _motion.WeaponInHand ? _motion.Arms.BodyYawError : null,
            // Horizontal distance of the head from the recentered origin (room-scale keeps it small).
            "headLocalCm" => new Vector2(_headTracker.LocalPosition.x, _headTracker.LocalPosition.z).magnitude * 100f,
            // How far the view's yaw is from the game camera's (motion aim turns the camera, not the view).
            "viewYawOffset" => Camera.main != null
                ? Mathf.DeltaAngle(_cameraRig.ViewRotation.eulerAngles.y, Camera.main.transform.eulerAngles.y)
                : null,
            "padRumbles" => _pads.RumblePulses,
            "eyeVolumetrics" => _cameraRig.LeftEye != null &&
                _cameraRig.LeftEye.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>() is { } eyeData &&
                eyeData.m_RenderingPathCustomFrameSettings.IsEnabled(UnityEngine.Rendering.HighDefinition.FrameSettingsField.Volumetrics) ? 1f : 0f,
            // The game camera itself (also with VR off), to check what the mod leaves behind.
            "gameVolumetrics" => camera != null &&
                camera.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>() is { } gameData &&
                gameData.m_RenderingPathCustomFrameSettings.IsEnabled(UnityEngine.Rendering.HighDefinition.FrameSettingsField.Volumetrics) ? 1f : 0f,
            "dlssRunning" => _cameraRig.DlssRunning ? 1f : 0f,
            "eyeFarClip" => _cameraRig.RightEye != null ? _cameraRig.RightEye.farClipPlane : null,
            "lodBias" => QualitySettings.lodBias,
            "sightShiftCm" => _lastSightShift.magnitude * 100f,
            "crosshairVisible" => GameObject.Find("PF_GUI/FullScreenGUI/PF_FirstPersonHUD/Crosshairs") is { } crosshairs &&
                crosshairs.transform.localScale.sqrMagnitude > 0f ? 1f : 0f,
            "frameRateShown" => GameObject.Find("JumpSpaceVR_FrameRate") != null ? 1f : 0f,
            "memoryMb" => System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024f * 1024f),
            "eyeJolt" => _eyeHeights.MaxSpeedChange * 100f,
            "worstFrameMs" => Diagnostics.WorstFrameMs,
            "cpuMainMs" => FrameTimings.CpuMainMs,
            "cpuRenderMs" => FrameTimings.CpuRenderMs,
            "gpuMs" => FrameTimings.GpuMs,
            "vrRendering" => _runtime != null && _runtime.IsRendering ? 1f : 0f,
            "inLevel" => IsInLevel() ? 1f : 0f,
            "cameraYaw" => cameraYaw,
            "cameraPitch" => cameraPitch,
            "bodyYaw" => _motion.Body.Yaw,
            "aimYawOffset" => cameraYaw.HasValue ? Mathf.DeltaAngle(_motion.Body.Yaw, cameraYaw.Value) : null,
            "motionActive" => _motion.IsActive ? 1f : 0f,
            "letterboxVisible" => IsLetterboxVisible() ? 1f : 0f,
            "gripSpanX" => _motion.Arms.GripSpan.x,
            "gripSpanY" => _motion.Arms.GripSpan.y,
            "gripSpanZ" => _motion.Arms.GripSpan.z,
            "barrelYaw" => _motion.Arms.Barrel.HasValue
                ? Mathf.DeltaAngle(_cameraRig.ViewRotation.eulerAngles.y, Quaternion.LookRotation(_motion.Arms.Barrel.Value.direction).eulerAngles.y)
                : null,
            "gunYaw" => _motion.Arms.HeldItemForward is { } gunForward
                ? Mathf.DeltaAngle(_cameraRig.ViewRotation.eulerAngles.y, Quaternion.LookRotation(gunForward).eulerAngles.y)
                : null,
            "locatorChildren" => _motion.ItemSlots.Locator,
            "socketChildren" => _motion.ItemSlots.Socket,
            "shoulderTiltCm" => _motion.WeaponInHand ? _motion.Arms.ShoulderTilt * 100f : null,
            "shoulderClearanceCm" => _motion.WeaponInHand ? _motion.Arms.ShoulderClearance * 100f : null,
            "itemPlaceErrorCm" => _motion.WeaponInHand ? _motion.Arms.ItemPlacementError * 100f : null,
            "gripHighlight" => GameObject.Find("JumpSpaceVR_GripHighlight") != null ? 1f : 0f,
            "twoHanded" => _motion.Arms.TwoHanded ? 1f : 0f,
            "canGrab" => _motion.Arms.CanGrab ? 1f : 0f,
            "rightHandErrorCm" => _motion.Arms.RightHandError * 100f,
            "leftHandErrorCm" => _motion.Arms.LeftHandError * 100f,
            "aimHookActive" => MotionControls.IsHookedInstance(_motion) ? 1f : 0f,
            "holdsItem" => _motion.HoldsItem ? 1f : 0f,
            "weaponInHand" => _motion.WeaponInHand ? 1f : 0f,
            "itemLocator" => _motion.HasItemLocator ? 1f : 0f,
            "gripErrorCm" => _motion.LastGripError * 100f,
            "weaponLayerInEyes" => _cameraRig.RightEye != null && FirstPersonRenderFix.Enabled &&
                (_cameraRig.RightEye.cullingMask & (1 << 29)) != 0 ? 1f : 0f,
            "flatWeaponPassSkips" => FirstPersonRenderFix.SkippedPassCount("FirstPersonCamera") +
                FirstPersonRenderFix.SkippedPassCount("FirstPersonCameraPrePass") + FirstPersonRenderFix.SkippedPassCount("FirstPersonCustomPass"),
            "stereoWeapon" => FirstPersonRenderFix.Enabled ? 1f : 0f,
            "modMenuOpen" => _modMenu.IsOpen ? 1f : 0f,
            "modMenuOnPage" => _modMenu.PageTitle != null ? 1f : 0f,
            "gazeAim" => _motion.GazeWeight,
            // The interactable picked with "target <type>": whether the game would use it now, and where it is
            // relative to the body (for headlook).
            "onTarget" => _testInteractable != null && InteractionTarget() is { } current &&
                current.GetInstanceID() == _testInteractable.GetInstanceID() ? 1f : 0f,
            "grappleAimed" => _testGrapple != null && AimedGrapplePoint() is { } aimed &&
                aimed.GetInstanceID() == _testGrapple.GetInstanceID() ? 1f : 0f,
            "anyGrappleAimed" => AimedGrapplePoint() != null ? 1f : 0f,
            "playerX" => LocalPlayerPosition().x,
            "playerY" => LocalPlayerPosition().y,
            "playerZ" => LocalPlayerPosition().z,
            "hasInteractTarget" => InteractionTarget() != null ? 1f : 0f,
            "targetYaw" => TargetDirection() is { } toTarget
                ? Mathf.DeltaAngle(_motion.Body.Yaw, _frame.YawOf(Quaternion.LookRotation(toTarget)))
                : null,
            "targetDistance" => TargetDirection()?.magnitude,
            "targetPitch" => TargetDirection() is { } toTarget2
                ? -Mathf.Asin(Mathf.Clamp(toTarget2.normalized.y, -1f, 1f)) * Mathf.Rad2Deg
                : null,
            // The right eye keeps its rendered (headset) pose between frames; the left eye is the game camera.
            "eyeYaw" => _cameraRig.RightEye != null ? _cameraRig.RightEye.transform.eulerAngles.y : null,
            "bodyFollowing" => _bodyFollow.Turning ? 1f : 0f,
            "headYawLocal" => Mathf.DeltaAngle(0f, _headTracker.LocalRotation.eulerAngles.y),
            "gameActionsEnabled" => GameInputPause.EnabledActionCount,
            "cursorFree" => Cursor.lockState != CursorLockMode.Locked ? 1f : 0f,
            "cutscene" => IsCutscenePlaying() ? 1f : 0f,
            "mainMenu" => GameObject.Find("PF_MainMenu/Canvas") != null ? 1f : 0f,
            "laserOnPanel" => _uiCompositor.CursorUv.HasValue ? 1f : 0f,
            "virtualMouseX" => _mouse.DevicePosition.x,
            "virtualMouseLeft" => _mouse.DeviceLeftPressed ? 1f : 0f,
            "pointerActive" => _pointerScreen.HasValue ? 1f : 0f,
            "panelYaw" => _uiCompositor.PanelYaw,
            "laserU" => _uiCompositor.CursorUv?.x,
            "laserV" => _uiCompositor.CursorUv?.y,
            "gamepadLeftX" => _lastControllerState.LeftStick.x,
            "gamepadLeftY" => _lastControllerState.LeftStick.y,
            "rightTrigger" => _lastControllerState.RightTrigger,
            "rightHandValid" => _lastControllerState.RightHand.IsValid ? 1f : 0f,
            "leftHandValid" => _lastControllerState.LeftHand.IsValid ? 1f : 0f,
            "leftGrip" => _rawControllerState.LeftGrip,
            "rightHandX" => _lastControllerState.RightHand.Position.x,
            "rightHandY" => _lastControllerState.RightHand.Position.y,
            "rightHandZ" => _lastControllerState.RightHand.Position.z,
            _ when name.StartsWith("setting.", StringComparison.Ordinal) => _config.ReadAsNumber(name.Substring("setting.".Length)),
            _ => null,
        };
    }

    private static bool IsLetterboxVisible()
    {
        foreach (var graphic in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Graphic>(FindObjectsSortMode.None))
        {
            var name = graphic.gameObject.name;
            if ((name.StartsWith("LetterBox", StringComparison.Ordinal) || name is "TopPannel" or "BottomPannel") &&
                graphic.isActiveAndEnabled && graphic.rectTransform.rect.height > 1f && graphic.color.a > 0.5f)
                return true;
        }
        return false;
    }

    private static bool IsCutscenePlaying()
    {
        foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
        {
            if (!canvas.isActiveAndEnabled)
                continue;
            for (var parent = canvas.transform; parent != null; parent = parent.parent)
            {
                if (parent.name.StartsWith("PF_Cutscene", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Test scenarios: uses the hangar's galaxy map terminal for the local player.</summary>
    private static void OpenGalaxyMap()
    {
        var handler = Il2Cpp.PlayerHandler.GetPlayerHandler;
        var player = handler != null ? handler.localPlayerController : null;
        var terminals = UnityEngine.Object.FindObjectsByType<Il2CppKeepsake.GameplayFeatures.GalaxyMap.GalaxyMapInteraction>(FindObjectsSortMode.None);
        if (player == null || terminals.Length == 0)
            throw new InvalidOperationException("no local player or galaxy map terminal");
        terminals[0].OnInteractionPredicted(player);
    }

    /// <summary>Test scenarios: logs a scene object's children (name, active, scale) to find HUD parts.</summary>
    private static void LogTree(string path)
    {
        var root = GameObject.Find(path);
        if (root == null)
            throw new InvalidOperationException($"no object '{path}'");
        var text = new System.Text.StringBuilder($"Tree of {path}:");
        AppendTree(text, root.transform, 0);
        Log.Msg(text.ToString());
    }

    private static void AppendTree(System.Text.StringBuilder text, Transform node, int depth)
    {
        text.AppendLine().Append(' ', depth * 2).Append(node.name)
            .Append(node.gameObject.activeSelf ? "" : " (inactive)")
            .Append(node.localScale == Vector3.one ? "" : $" scale={node.localScale}");
        if (depth >= 4)
            return;
        for (var index = 0; index < node.childCount; index++)
            AppendTree(text, node.GetChild(index), depth + 1);
    }

    /// <summary>Test scenarios: logs the visible renderers within 40 cm of the eyes (what can clip into the view).</summary>
    private void LogRenderersNearEyes()
    {
        var eye = _cameraRig.RightEye != null ? _cameraRig.RightEye.transform.position : Vector3.zero;
        var text = new System.Text.StringBuilder($"Renderers near the eyes at {eye}:");
        var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        for (var index = 0; index < renderers.Length; index++)
        {
            var renderer = renderers[index];
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                continue;
            var bounds = renderer.bounds;
            var distance = Vector3.Distance(bounds.ClosestPoint(eye), eye);
            if (distance > 0.6f)
                continue;
            var path = Diagnostics.PathOf(renderer.transform);
            if (!path.Contains("PF_PlayerCharacter", StringComparison.Ordinal))
                continue;
            var shadows = (int)renderer.shadowCastingMode;
            text.AppendLine().Append("  ").Append(distance.ToString("F2")).Append(" m layer=")
                .Append(renderer.gameObject.layer).Append(" shadowMode=").Append(shadows).Append(' ')
                .Append(path.Length > 90 ? path.Substring(path.Length - 90) : path);
        }
        Log.Msg(text.ToString());
    }

    /// <summary>Test scenarios: switches off visible renderers whose path contains the text (to identify geometry).</summary>
    private static void HideRenderers(string pathPart)
    {
        var count = 0;
        var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        for (var index = 0; index < renderers.Length; index++)
        {
            var renderer = renderers[index];
            if (renderer != null && renderer.enabled && Diagnostics.PathOf(renderer.transform).Contains(pathPart, StringComparison.Ordinal))
            {
                renderer.enabled = false;
                count++;
            }
        }
        Log.Msg($"Hid {count} renderer(s) containing '{pathPart}'.");
    }

    private static Il2Cpp.InteractionController _interaction;

    /// <summary>What the game would interact with now ("use"), or null.</summary>
    private static Il2CppKeepsake.Interactable_Base InteractionTarget()
    {
        var handler = Il2Cpp.PlayerHandler.GetPlayerHandler;
        var player = handler != null ? handler.localPlayerController : null;
        if (player == null)
            return null;
        if (_interaction == null || _interaction.m_PlayerController != player)
            _interaction = player.GetComponentInChildren<Il2Cpp.InteractionController>(true);
        return _interaction != null ? _interaction.BestEligibleInteraction : null;
    }

    // Test scenarios: the object picked with "target", as a transform and (when it is one) an interactable or a grapple point.
    private Transform _testTarget;
    private Il2CppKeepsake.Interactable_Base _testInteractable;
    private Il2CppKeepsake.GrapplingPoint _testGrapple;
    // How high up the target's collider to look, from 0 (bottom) to 1 (top): "aimat 0.8" for a panel's screen.
    private float _testAimHeight = 0.5f;

    /// <summary>
    /// Test scenarios: picks the nearest grapple point ("target GrapplingPoint") or interactable whose type name
    /// contains the text ("target AirlockPanel", "target GalaxyMap"), optionally only objects whose name contains
    /// <paramref name="objectName"/> ("target AirlockPanel PF_AirlockPanel_02").
    /// </summary>
    private void PickTarget(string typeName, string objectName)
    {
        _testTarget = null;
        _testInteractable = null;
        _testGrapple = null;
        _testAimHeight = 0.5f;
        var best = float.MaxValue;
        var from = LocalPlayerPosition();
        if (typeName.Equals("GrapplingPoint", StringComparison.OrdinalIgnoreCase))
        {
            var points = UnityEngine.Object.FindObjectsByType<Il2CppKeepsake.GrapplingPoint>(FindObjectsSortMode.None);
            for (var index = 0; index < points.Length; index++)
            {
                var point = points[index];
                if (point == null || !point.isActiveAndEnabled ||
                    !point.name.Contains(objectName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var distance = Vector3.Distance(point.transform.position, from);
                if (distance < best)
                {
                    best = distance;
                    _testGrapple = point;
                    _testTarget = point.transform;
                }
            }
        }
        else
        {
            var all = UnityEngine.Object.FindObjectsByType<Il2CppKeepsake.Interactable_Base>(FindObjectsSortMode.None);
            for (var index = 0; index < all.Length; index++)
            {
                var interactable = all[index];
                if (interactable == null || !interactable.isActiveAndEnabled ||
                    !interactable.GetIl2CppType().Name.Contains(typeName, StringComparison.OrdinalIgnoreCase) ||
                    !interactable.name.Contains(objectName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var distance = Vector3.Distance(interactable.transform.position, from);
                if (distance < best)
                {
                    best = distance;
                    _testInteractable = interactable;
                    _testTarget = interactable.transform;
                }
            }
        }
        if (_testTarget == null)
            throw new InvalidOperationException($"no '{typeName}' in this scene");
        Log.Msg($"Target: {typeName} '{_testTarget.name}' at {best:F1} m ({Diagnostics.PathOf(_testTarget)}).");
    }

    private static Il2CppKinematicCharacterController.KinematicCharacterMotor LocalPlayerMotor()
    {
        var handler = Il2Cpp.PlayerHandler.GetPlayerHandler;
        var player = handler != null ? handler.localPlayerController : null;
        return player != null
            ? player.GetComponentInChildren<Il2CppKinematicCharacterController.KinematicCharacterMotor>(true)
            : null;
    }

    private static Vector3 LocalPlayerPosition()
    {
        var handler = Il2Cpp.PlayerHandler.GetPlayerHandler;
        var player = handler != null ? handler.localPlayerController : null;
        return player != null ? player.transform.position : Vector3.zero;
    }

    /// <summary>
    /// Test scenarios: moves the local player <paramref name="distance"/> meters from the target: on the floor on the
    /// side they are already on, in front of or behind its face at eye height ("front", "back"), or at the nearest
    /// free floor with a clear view of it ("spot").
    /// </summary>
    private void GoToTarget(float distance, string side)
    {
        var handler = Il2Cpp.PlayerHandler.GetPlayerHandler;
        var player = handler != null ? handler.localPlayerController : null;
        if (player == null || _testTarget == null)
            throw new InvalidOperationException("no player or target");
        var target = TargetPoint();
        Vector3 stand;
        if (side == "spot")
        {
            stand = FindStandingSpot(target, distance) ??
                throw new InvalidOperationException($"no free floor with a view of the target near {target}");
        }
        else if (side == "front" || side == "back")
        {
            // In front of (or behind) the target's face, with the eyes at its height: for wall panels, also out in
            // zero-g where there is no floor to stand on. The target's own up is that of the ship or room it is in.
            var facing = side == "front" ? _testTarget.forward : -_testTarget.forward;
            stand = target + facing.normalized * distance - _testTarget.up * 1.55f;
        }
        else
        {
            var away = player.transform.position - target;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
                away = _testTarget.forward;
            away.y = 0f;
            stand = target + away.normalized * distance;
            if (Physics.Raycast(stand + Vector3.up * 1.5f, Vector3.down, out var hit, 10f, ~0, QueryTriggerInteraction.Ignore))
                stand = hit.point + Vector3.up * 0.02f;
            else
                stand.y = player.transform.position.y;
        }
        // The character motor keeps its own position and moves the transform back to it, so move the motor.
        var motor = LocalPlayerMotor();
        if (motor != null)
            motor.SetPosition(stand, true);
        else
            player.transform.position = stand;
        // Near or inside a ship the character must ignore the ship's hull volume, as it does after walking up to an
        // airlock; otherwise the motor pushes it straight out of the hull onto the roof.
        Physics.SyncTransforms();
        var movement = player.GetComponentInChildren<Il2Cpp.Player_MovementHandler>(true);
        var hull = movement != null ? movement.m_SpaceShipIgnoreCollider : null;
        if (hull != null && motor != null)
            hull.IgnoreOverlappingSpaceShipColliders(motor.Capsule);
        var entered = EnterParentTriggersAt(player, stand + player.transform.up);
        _recenterPending = true;
        Log.Msg($"Moved the player to {stand} ({distance:F1} m from the target)" +
                (hull != null && hull.IsInsideAnyShipCollider ? ", inside a ship's hull volume" : "") +
                (entered.Length > 0 ? $", inside {entered}." : "."));
    }

    /// <summary>
    /// Test scenarios: a floor spot about <paramref name="distance"/> from the target with room for the character and
    /// a clear line of sight from eye height, trying straight in front of it first and then further round each side.
    /// </summary>
    private Vector3? FindStandingSpot(Vector3 target, float distance)
    {
        // Ships and rooms keep their own up; most are level, so snap to world up when the target is close to it.
        var up = Vector3.Angle(_testTarget.up, Vector3.up) < 30f ? Vector3.up : _testTarget.up;
        var front = Vector3.ProjectOnPlane(_testTarget.forward, up);
        if (front.sqrMagnitude < 0.01f)
            front = Vector3.ProjectOnPlane(_testTarget.right, up);
        front.Normalize();
        var targetCollider = _testTarget.GetComponentInChildren<Collider>(true);
        // Only what the character collides with counts as floor or as in the way.
        var motor = LocalPlayerMotor();
        var solid = motor != null ? motor.CollidableLayers.value : ~0;
        // Look for the floor from just above the middle of the target, below anything overhanging the floor near it.
        var floorSearch = (targetCollider != null ? targetCollider.bounds.center : target) + up * 0.5f;
        var rejected = new System.Text.StringBuilder();
        foreach (var range in new[] { distance, distance * 0.75f, distance * 1.4f })
        {
            for (var step = 0; step < 24; step++)
            {
                var angle = (step + 1) / 2 * 15f * (step % 2 == 0 ? 1f : -1f);
                var direction = Quaternion.AngleAxis(angle, up) * front;
                if (!Physics.Raycast(floorSearch + direction * range, -up, out var floor, 3f, solid,
                        QueryTriggerInteraction.Ignore))
                {
                    rejected.Append($" {angle:F0}°:no floor");
                    continue;
                }
                var feet = floor.point + up * 0.05f;
                var height = Vector3.Dot(target - feet, up);
                if (height < 0.3f || height > 2.2f)
                {
                    rejected.Append($" {angle:F0}°:height {height:F2}");
                    continue;
                }
                // The character's core must be free; the motor pushes it out of small overlaps on its own.
                var inside = FirstRealOverlap(Physics.OverlapCapsule(feet + up * 0.5f, feet + up * 1.5f, 0.15f, solid,
                    QueryTriggerInteraction.Ignore));
                if (inside != null)
                {
                    rejected.Append($" {angle:F0}°:inside {inside.name}");
                    continue;
                }
                if (Physics.Linecast(feet + up * 1.6f, target, out var blocker, ~0, QueryTriggerInteraction.Ignore) &&
                    !blocker.transform.IsChildOf(_testTarget))
                {
                    rejected.Append($" {angle:F0}°:view blocked by {blocker.collider.name}");
                    continue;
                }
                Log.Msg($"Standing spot {angle:F0}° round from the target's front, {range:F1} m away.");
                return feet;
            }
        }
        Log.Warning("No standing spot:" + rejected);
        return null;
    }

    /// <summary>
    /// The first collider the character would really be stuck in, skipping solid volumes that enclose a whole ship (its
    /// hull for collisions in space, which the character ignores while inside it).
    /// </summary>
    private static Collider FirstRealOverlap(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider> overlaps)
    {
        for (var index = 0; index < overlaps.Length; index++)
        {
            if (!IsEnclosingSolid(overlaps[index]))
                return overlaps[index];
        }
        return null;
    }

    private static bool IsEnclosingSolid(Collider collider)
    {
        var size = collider.bounds.size;
        var hollow = collider.TryCast<MeshCollider>() is { convex: false };
        return !hollow && size.x > 10f && size.y > 10f && size.z > 10f;
    }

    /// <summary>
    /// Tells the game's player-parent triggers (ship interiors, gravity zones) that contain the point that the player
    /// entered them, as their trigger volumes would if the player had walked in. Returns their names.
    /// </summary>
    private static string EnterParentTriggersAt(Il2Cpp.PlayerController player, Vector3 point)
    {
        var names = new System.Text.StringBuilder();
        var triggers = UnityEngine.Object.FindObjectsByType<Il2CppKeepsake.PlayerParentTrigger>(FindObjectsSortMode.None);
        for (var index = 0; index < triggers.Length; index++)
        {
            var trigger = triggers[index];
            var volume = trigger.m_PlayerTrigger_Base;
            if (volume == null || !Contains(volume.gameObject, point))
                continue;
            trigger.PlayerTrigger_BaseOnOnPlayerEntered(player, volume);
            names.Append(names.Length > 0 ? ", " : "").Append(Diagnostics.PathOf(trigger.transform));
        }
        return names.ToString();
    }

    private static bool Contains(GameObject volume, Vector3 point)
    {
        var colliders = volume.GetComponentsInChildren<Collider>(true);
        for (var index = 0; index < colliders.Length; index++)
        {
            var collider = colliders[index];
            if (collider.TryCast<MeshCollider>() is { convex: false })
            {
                if (collider.bounds.Contains(point))
                    return true;
            }
            else if ((collider.ClosestPoint(point) - point).sqrMagnitude < 0.0001f)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The grapple point the game currently shows the grapple prompt for, or null.</summary>
    private static Il2CppKeepsake.GrapplingPoint AimedGrapplePoint()
    {
        var handler = Il2Cpp.PlayerHandler.GetPlayerHandler;
        var player = handler != null ? handler.localPlayerController : null;
        var icons = player != null ? player.GetComponentInChildren<Il2CppKeepsake.GrapplingHookIconHandler>(true) : null;
        return icons != null ? icons.AimedAtPoint : null;
    }

    private Vector3? TargetDirection()
    {
        if (_testTarget == null || _cameraRig.RightEye == null)
            return null;
        return TargetPoint() - _cameraRig.RightEye.transform.position;
    }

    /// <summary>
    /// Where to look at the target: a grapple point's hook point; where the game draws a prompt icon (airlock panels);
    /// else its collider (a terminal's interaction volume) at the height set with "aimat", the centre by default; else
    /// its pivot, which for some objects sits on the floor.
    /// </summary>
    private Vector3 TargetPoint()
    {
        if (_testGrapple != null)
            return _testGrapple.HookPointPosition;
        var icon = _testTarget.Find("IconPosition");
        if (icon != null)
            return icon.position;
        var collider = _testTarget.GetComponentInChildren<Collider>(true);
        if (collider == null)
            return _testTarget.position;
        var bounds = collider.bounds;
        return bounds.center + Vector3.up * (bounds.size.y * (_testAimHeight - 0.5f));
    }

    /// <summary>Test scenarios: logs where the target and its colliders are relative to the player.</summary>
    private void LogTargetInfo()
    {
        if (_testTarget == null)
            throw new InvalidOperationException("no target");
        var handler = Il2Cpp.PlayerHandler.GetPlayerHandler;
        var player = handler != null ? handler.localPlayerController : null;
        var text = new System.Text.StringBuilder($"Target '{_testTarget.name}': position {_testTarget.position}, ");
        text.Append($"forward {_testTarget.forward}, up {_testTarget.up}");
        if (player != null)
            text.AppendLine().Append($"  player at {player.transform.position}, up {player.transform.up}, in ")
                .Append(player.transform.parent != null ? Diagnostics.PathOf(player.transform.parent) : "the scene root");
        if (_cameraRig.RightEye != null)
            text.AppendLine().Append($"  eye at {_cameraRig.RightEye.transform.position}");
        var colliders = _testTarget.GetComponentsInChildren<Collider>(true);
        for (var index = 0; index < colliders.Length; index++)
        {
            var collider = colliders[index];
            text.AppendLine().Append($"  collider {collider.name} trigger={collider.isTrigger} enabled={collider.enabled} ");
            text.Append($"center {collider.bounds.center} size {collider.bounds.size}");
        }
        if (_testGrapple != null)
        {
            var point = _testGrapple;
            text.AppendLine().Append($"  grapple: hook point {point.HookPointPosition}, aimed {AimedGrapplePoint()?.name ?? "none"}, ")
                .Append($"distance {point.GrappleDistance:F1} m, visible {point.MaxVisibleDistance:F1} m, ")
                .Append($"closest {Mathf.Sqrt(point.ClosestGrappleDistanceSq):F1} m, angle limit {point.AngleLimit:F0}° ")
                .Append($"towards {point.AngleLimitDirection} (inverted {point.InvertAngeLimitDirection}), ")
                .Append($"cone {point.ConeHorizontalAngle:F0}°x{point.ConeVerticalAngle:F0}°, enabled {point.IsEnabled}, ")
                .Append($"eligible {point.IsEligible}, can grapple {point.CanGrapple}, ")
                .Append($"good angle and distance {point.PlayerIsWithinGoodAngleAndDistanceToGrapple}");
            if (_cameraRig.RightEye != null)
            {
                var eye = _cameraRig.RightEye.transform.position;
                text.Append($", valid angle from the eye {point.IsWithinValidAngle(ref eye)}");
            }
            var handlers = UnityEngine.Object.FindObjectsByType<Il2CppKeepsake.GrapplingHookIconHandler>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (var index = 0; index < handlers.Length; index++)
            {
                var icons = handlers[index];
                var eligible = icons.m_EligibleGrapplingPoints;
                text.AppendLine().Append($"  icon handler {Diagnostics.PathOf(icons.transform)} active={icons.isActiveAndEnabled} ")
                    .Append($"aimed {(icons.AimedAtPoint != null ? icons.AimedAtPoint.name : "none")} ")
                    .Append($"eligible {(eligible != null ? eligible.Count : -1)}");
            }
        }
        // What surrounds it: the nearest solid surface in each direction from its centre.
        var centre = TargetPoint();
        var forward = Vector3.ProjectOnPlane(_testTarget.forward, Vector3.up).normalized;
        for (var degrees = 0; degrees < 360; degrees += 45)
            AppendProbe(text, $"{degrees}°", centre, Quaternion.AngleAxis(degrees, Vector3.up) * forward);
        AppendProbe(text, "up", centre, Vector3.up);
        AppendProbe(text, "down", centre, Vector3.down);
        Log.Msg(text.ToString());
    }

    private static void AppendProbe(System.Text.StringBuilder text, string label, Vector3 from, Vector3 direction)
    {
        text.AppendLine().Append($"  probe {label}: ");
        if (Physics.Raycast(from, direction, out var hit, 6f, ~0, QueryTriggerInteraction.Ignore))
            text.Append($"{hit.distance:F2} m {Diagnostics.PathOf(hit.collider.transform)}");
        else
            text.Append("nothing within 6 m");
    }

    /// <summary>Test scenarios: lists interactable objects within 12 m of the eyes, nearest first.</summary>
    private void LogInteractables()
    {
        var eye = _cameraRig.RightEye != null ? _cameraRig.RightEye.transform.position : Vector3.zero;
        var found = new System.Collections.Generic.List<(float Distance, string Text)>();
        var all = UnityEngine.Object.FindObjectsByType<Il2CppKeepsake.Interactable_Base>(FindObjectsSortMode.None);
        for (var index = 0; index < all.Length; index++)
        {
            var interactable = all[index];
            if (interactable == null || !interactable.isActiveAndEnabled)
                continue;
            var distance = Vector3.Distance(interactable.transform.position, eye);
            if (distance < 12f)
                found.Add((distance, $"{distance:F1} m {interactable.GetIl2CppType().Name} {Diagnostics.PathOf(interactable.transform)}"));
        }
        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var text = new System.Text.StringBuilder("Interactables near the player:");
        foreach (var (_, line) in found)
            text.AppendLine().Append("  ").Append(line);
        Log.Msg(text.ToString());
    }

    private void ScenarioCommand(string command)
    {
        switch (command)
        {
            case "snapshot":
                SaveSnapshots();
                break;
            case "diagnostics":
                if (_runtime != null)
                    Diagnostics.Dump(_runtime);
                break;
            case "recenter":
                _recenterPending = true;
                break;
            case "galaxymap":
                OpenGalaxyMap();
                break;
            case "interactables":
                LogInteractables();
                break;
            case "targetinfo":
                LogTargetInfo();
                break;
            case "face":
                if (TargetDirection() is not { } toTarget)
                    throw new InvalidOperationException("no target");
                _motion.Body.Turn(Mathf.DeltaAngle(_motion.Body.Yaw, _frame.YawOf(Quaternion.LookRotation(toTarget))));
                break;
            case "near":
                LogRenderersNearEyes();
                break;
            case "eyeshot":
                if (_stereoRenderer.LeftTexture != null)
                    EyeSnapshot.SaveSingle(_stereoRenderer.LeftTexture, "eye-left");
                break;
            case "itemtohand":
                if (!_motion.SimulateSocketSpawn())
                    throw new InvalidOperationException("no held item or hand socket");
                break;
            case "itemframes":
                _motion.Arms.LogAnimatedFramesOnce();
                break;
            case "rig":
                Log.Msg("First-person rig hierarchy:" + Environment.NewLine + _motion.DescribeRigHierarchy());
                break;
            case "restore-config":
                _config.RestoreTemporary();
                break;
            default:
                var parts = command.Split(' ', 3);
                if (parts.Length == 3 && parts[0] == "config")
                    _config.SetTemporarily(parts[1], parts[2]);
                else if (parts.Length >= 2 && parts[0] == "target")
                    PickTarget(parts[1], parts.Length >= 3 ? parts[2] : "");
                else if (parts.Length >= 2 && parts[0] == "aimat")
                    _testAimHeight = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                else if (parts.Length >= 2 && parts[0] == "goto")
                {
                    var gotoArgs = command.Substring(5).Trim().Split(' ');
                    GoToTarget(float.Parse(gotoArgs[0], System.Globalization.CultureInfo.InvariantCulture),
                        gotoArgs.Length > 1 ? gotoArgs[1] : "");
                }
                else if (parts.Length >= 2 && parts[0] == "hide")
                    HideRenderers(command.Substring(5).Trim());
                else if (parts.Length >= 2 && parts[0] == "tree")
                    LogTree(command.Substring(5).Trim());
                else if (parts.Length == 3 && parts[0] == "pad")
                {
                    var values = parts[2].Split(' ');
                    (_testPad ??= new Testing.TestGamepad()).Set(parts[1], values[0], values.Length > 1 ? values[1] : "0");
                }
                else
                    throw new ArgumentException($"unknown command '{command}'");
                break;
        }
    }

    /// <summary>
    /// Points the right controller at the UI panel while the game shows a free mouse cursor (menus, inventory,
    /// pause). The hit drives a virtual mouse; gameplay (locked cursor) is unaffected.
    /// </summary>
    private void UpdateLaserPointer(in TrackedPose pointer)
    {
        _pointerScreen = null;
        _uiCompositor.CursorUv = null;
        var inLevel = IsInLevel();
        var ownedCursor = _pointerOwnsCursor;
        _pointerOwnsCursor = false;
        AimLaserPointer(pointer, inLevel);

        // Hand the cursor back the way the game had it once the laser no longer owns it (left the panel or entered
        // a level); the game doesn't re-lock it by itself.
        if (ownedCursor && !_pointerOwnsCursor && _gameCursorLock.HasValue)
        {
            if (Cursor.lockState == CursorLockMode.None)
                Cursor.lockState = _gameCursorLock.Value;
            _gameCursorLock = null;
        }
    }

    private void AimLaserPointer(in TrackedPose pointer, bool inLevel)
    {
        if (!_config.LaserPointer.Value || !pointer.IsValid || (inLevel && Cursor.lockState == CursorLockMode.Locked))
            return;

        var origin = _cameraRig.GamePosition + _cameraRig.ViewRotation * (pointer.Position * _config.WorldScale.Value);
        var direction = _cameraRig.ViewRotation * pointer.Rotation * Vector3.forward;
        if (!_uiCompositor.TryHitPanel(origin, direction, out var uv))
        {
            _pointerAssist.Reset();
            return;
        }

        var raw = new Vector2(uv.x * Screen.width, uv.y * Screen.height);
        var assisted = _config.PointerAssist.Value ? _pointerAssist.Assist(raw, _rawControllerState.RightTrigger) : raw;
        _uiCompositor.CursorUv = new Vector2(assisted.x / Screen.width, assisted.y / Screen.height);
        _pointerScreen = assisted;
        _pointerOwnsCursor = !inLevel;
    }

    private static int CountRealtimeProbes()
    {
        var count = 0;
        foreach (var probe in UnityEngine.Object.FindObjectsByType<UnityEngine.Rendering.HighDefinition.HDProbe>(FindObjectsSortMode.None))
        {
            if (probe != null && probe.isActiveAndEnabled && probe.mode == UnityEngine.Rendering.HighDefinition.ProbeSettings.Mode.Realtime)
                count++;
        }
        return count;
    }

    private bool _mapView;
    private int _mapCheckFrame;
    private const int MapCheckIntervalFrames = 15;

    /// <summary>
    /// Notices when the game shows the galaxy map. It doesn't switch cameras for that: it moves the main camera's
    /// root under the map's rig, so the camera's ancestors are checked a few times a second.
    /// </summary>
    private void UpdateMapView(Camera camera)
    {
        if (Time.frameCount < _mapCheckFrame)
            return;
        _mapCheckFrame = Time.frameCount + MapCheckIntervalFrames;
        var wasMap = _mapView;
        _mapView = camera != null && IsGalaxyMapCamera(camera.transform);
        if (_mapView != wasMap)
            Log.Msg(_mapView ? "Galaxy map: shown at its camera's angle; the sticks steer it." : "Left the galaxy map.");
    }

    private static bool IsGalaxyMapCamera(Transform camera)
    {
        if (camera.GetComponentInParent<Il2CppKeepsake.GameplayFeatures.GalaxyMap.GalaxyMapCamera>() != null)
            return true;
        for (var parent = camera; parent != null; parent = parent.parent)
        {
            if (parent.name.StartsWith("PF_GalaxyMap", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private float? _gameLodBias;
    private float _appliedLodScale = 1f;

    /// <summary>Scales the level-of-detail distances with the view distance setting; 1 restores the game's value.</summary>
    private void ApplyLodDistance(float scale)
    {
        if (Mathf.Approximately(scale, _appliedLodScale))
            return;
        // If the game changed its own setting meanwhile (graphics menu), take that as the new base.
        if (_gameLodBias.HasValue && !Mathf.Approximately(QualitySettings.lodBias, _gameLodBias.Value * _appliedLodScale))
            _gameLodBias = QualitySettings.lodBias / _appliedLodScale;
        _gameLodBias ??= QualitySettings.lodBias;
        QualitySettings.lodBias = _gameLodBias.Value * scale;
        _appliedLodScale = scale;
        if (Mathf.Approximately(scale, 1f))
            _gameLodBias = null;
    }

    private const float AimDownSightsBlendSeconds = 0.15f;

    /// <summary>
    /// The game centres the sights on its camera, which in VR sits between the eyes, so aiming down sights makes you
    /// go cross-eyed. While aiming (left trigger), move the weapon so the sights line up with the chosen eye.
    /// </summary>
    /// <summary>
    /// Shares this frame's hands with other modded players (the hand bones while the weapon is in hand, otherwise the
    /// controllers) and shows theirs. Call before rendering, after the local arms are placed.
    /// </summary>
    private void ShareHands(in ControllerState controllers, bool positional)
    {
        _handSync.Enabled = _config.ShareHands.Value;
        _handSync.Loopback = _config.HandSyncLoopback.Value;
        if (_pads.GamepadActive)
            _handSync.CaptureLocal(null, null, false);
        else if (_motion.WeaponInHand && _motion.Arms.TryGetHandBones(out var leftBone, out var rightBone))
            _handSync.CaptureLocal(leftBone, rightBone, rotations: true);
        else
            _handSync.CaptureLocal(WorldHand(controllers.LeftHand, positional), WorldHand(controllers.RightHand, positional), false);
        _handSync.ApplyRemotes();
    }

    private HandPose? WorldHand(in TrackedPose raw, bool positional)
    {
        var pose = _headTracker.Localize(raw, positional);
        if (!pose.IsValid)
            return null;
        return new HandPose(_cameraRig.GamePosition + _cameraRig.ViewRotation * (pose.Position * _config.WorldScale.Value),
            _cameraRig.ViewRotation * pose.Rotation);
    }

    private Vector3 SightShift()
    {
        var aimTrigger = _pads.GamepadActive ? _pads.LeftTrigger : _rawControllerState.LeftTrigger;
        var aiming = aimTrigger > 0.5f ? 1f : 0f;
        _aimDownSights = Mathf.MoveTowards(_aimDownSights, aiming, Time.unscaledDeltaTime / AimDownSightsBlendSeconds);
        _lastSightShift = Vector3.zero;
        if (_aimDownSights <= 0f || _config.AimEye.Value == AimEye.Center)
            return Vector3.zero;
        var eye = _config.AimEye.Value == AimEye.Right ? _cameraRig.RightEye : _cameraRig.LeftEye;
        if (eye == null)
            return Vector3.zero;
        _lastSightShift = (eye.transform.position - _cameraRig.GameCameraPosition) * _aimDownSights;
        return _lastSightShift;
    }

    /// <summary>
    /// True while a player character exists. In a level the laser only works when the game itself frees the cursor
    /// (pause menu, terminals); outside one it always works, even with the cursor locked, because the game locks it
    /// whenever the (virtual) gamepad is the active device. The camera lives under the camera manager, not the
    /// character, so this looks for the character's camera controller (refreshed twice a second).
    /// </summary>
    private bool IsInLevel()
    {
        if (Time.frameCount >= _inLevelCheckFrame)
        {
            _inLevelCheckFrame = Time.frameCount + InLevelCheckIntervalFrames;
            _inLevel = UnityEngine.Object.FindObjectsByType<Il2Cpp.CameraController_Character>(FindObjectsSortMode.None).Length > 0;
        }
        return _inLevel;
    }

    private const int InLevelCheckIntervalFrames = 45;
    private int _inLevelCheckFrame;
    private bool _inLevel;

    private ControllerState _lastControllerState;
    private float _menuPressedAt = -1f;
    private bool _menuHoldTriggered;
    private float _menuTapReleaseUntil;
    private const float MenuHoldSeconds = 1f;

    /// <summary>
    /// Holding the menu button recenters; a short tap still reaches the game as Start (sent briefly on release,
    /// since a hold must not open the game's pause menu).
    /// </summary>
    private bool ProcessMenuButton(bool pressed)
    {
        var now = Time.unscaledTime;
        if (pressed)
        {
            if (_menuPressedAt < 0f)
            {
                _menuPressedAt = now;
                _menuHoldTriggered = false;
            }
            else if (!_menuHoldTriggered && now - _menuPressedAt >= MenuHoldSeconds)
            {
                _menuHoldTriggered = true;
                _recenterPending = true;
            }
            return false;
        }

        if (_menuPressedAt >= 0f && !_menuHoldTriggered)
            _menuTapReleaseUntil = now + 0.1f;
        _menuPressedAt = -1f;
        return now < _menuTapReleaseUntil;
    }
    private bool _snapPanelToHead = true;

    private void HandleUiAdjustKeys()
    {
        const float step = 1.1f;
        var changed = true;
        if (_hotkeys.WasPressed(_config.UiLargerKey.Value))
            _config.Adjust(_config.UiWidth, step, 0.4f, 6f);
        else if (_hotkeys.WasPressed(_config.UiSmallerKey.Value))
            _config.Adjust(_config.UiWidth, 1f / step, 0.4f, 6f);
        else if (_hotkeys.WasPressed(_config.UiFurtherKey.Value))
            _config.Adjust(_config.UiDistance, step, 0.4f, 6f);
        else if (_hotkeys.WasPressed(_config.UiCloserKey.Value))
            _config.Adjust(_config.UiDistance, 1f / step, 0.4f, 6f);
        else
            changed = false;

        if (changed)
            Log.Msg($"UI panel: {_config.UiWidth.Value:F2} m wide at {_config.UiDistance.Value:F2} m.");
    }

    private void HandleHotkeys()
    {
        if (_hotkeys.WasPressed(_config.ToggleVrKey.Value))
        {
            _vrRequested = !_vrRequested;
            if (_vrRequested)
                EnableVr();
            else
                DisableVr();
        }

        if (_hotkeys.WasPressed(_config.RecenterKey.Value))
            _recenterPending = true;

        HandleUiAdjustKeys();

        if (_hotkeys.WasPressed(_config.ModMenuKey.Value))
            _modMenu.Toggle();
        _modMenu.Update(_hotkeys, _pads.GamepadActive ? _pads.MenuInput() : _lastControllerState);
        // A real gamepad's buttons reach the game directly: hold the game's input back while the menu is open.
        _gameInput.Update(_modMenu.IsOpen);

        if (_hotkeys.WasPressed(_config.SnapshotKey.Value))
            SaveSnapshots();

        if (_hotkeys.WasPressed(_config.DiagnosticsKey.Value))
        {
            if (_runtime != null)
                Diagnostics.Dump(_runtime);
            else
                Log.Msg("OpenXR has not been started yet.");
        }
    }

    private void EnableVr()
    {
        try
        {
            _runtime ??= new OpenXRRuntime { RequestConformanceAutomation = _config.MockControllerInput.Value || _scenario != null };
            var renderMode = _config.SinglePassInstanced.Value ? StereoRenderMode.SinglePassInstanced : StereoRenderMode.MultiPass;
            if (!_runtime.IsInitialized && !InitializeFirstWorkingRuntime(renderMode))
            {
                // Usually the headset is not connected yet; keep trying so it can be connected after launch.
                _nextRetryTime = Time.realtimeSinceStartup + RetryIntervalSeconds;
                if (!_retryAnnounced)
                {
                    Log.Warning($"VR not started. Connect your headset (Virtual Desktop / SteamVR / Quest Link); retrying every {RetryIntervalSeconds:F0}s. Press {_config.ToggleVrKey.Value} to stop.");
                    _retryAnnounced = true;
                    _runtime.QuietFailures = true;
                }
                return;
            }
            if (_retryAnnounced)
                Log.Msg("Headset connected.");
            _retryAnnounced = false;
            _runtime.QuietFailures = false;

            HookBeforeRender();
            // Every time VR starts (also after F8 off/on), not only when the hooks are first installed.
            _motion.Activate();
            _headAudio.Activate();
            XRSettings.eyeTextureResolutionScale = _config.ClampedRenderScale;
            _recenterPending = true;

            if (!_runtime.Start())
            {
                _vrRequested = false;
                return;
            }
            _stereoRenderer.Activate(_runtime.Subsystems.Display);
            _stereoRenderer.Ui = _uiCompositor;
            _uiCompositor.PanelEnabled = _config.MoveUiIntoVr.Value;
            _uiCompositor.Start();
        }
        catch (Exception exception)
        {
            _vrRequested = false;
            Log.Error($"Failed to start VR: {exception}");
        }
    }

    /// <summary>
    /// Tries each candidate runtime (the VR app that is running first, then the Windows default) until one has a
    /// headset ready. Works for Steam Link and SteamVR, Virtual Desktop (VDXR or SteamVR mode) and Quest Link.
    /// </summary>
    private bool InitializeFirstWorkingRuntime(StereoRenderMode renderMode)
    {
        var candidates = RuntimeSelector.Candidates(_config.Runtime.Value, _config.OpenXRRuntimeJson.Value);
        foreach (var candidate in candidates)
        {
            Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", candidate.ManifestPath);
            if (_runtime.Initialize(renderMode))
            {
                Log.Msg($"Connected to OpenXR runtime: {candidate.Name}.");
                return true;
            }
            if (!_runtime.QuietFailures)
                Log.Msg($"No headset on {candidate.Name}.");
        }
        return false;
    }

    private void DisableVr()
    {
        _sessionReport.Flush();
        _mouse.Remove();
        _pointerScreen = null;
        _motion.Deactivate();
        _handSync.Reset();
        _firstPerson.Release();
        _vignette.Clear();
        _realCrouch.UpdateLift(false, 0f, false);
        FirstPersonRenderFix.Apply(false);
        _gamepad.Remove();
        _uiCompositor.Stop();
        _stereoRenderer.Deactivate();
        _stereoRenderer.LeftEyeCamera = null;
        _stereoRenderer.RightEyeCamera = null;
        _cameraRig.Release();
        _runtime?.Shutdown();
        Log.Msg("VR stopped.");
    }

    private void HookBeforeRender()
    {
        if (_beforeRenderHooked)
            return;

        _beforeRenderHooked = BeforeRenderHook.Install(HarmonyInstance, OnBeforeRender)
                              && StereoRenderer.InstallHooks(HarmonyInstance);
        FirstPersonRenderFix.InstallHooks(HarmonyInstance);
        MotionControls.InstallHooks(HarmonyInstance);
        Tracking.HeadAudio.InstallHooks(HarmonyInstance);
    }


    private void OnBeforeRender()
    {
        try
        {
            if (_runtime == null || !_runtime.IsInitialized)
                return;

            _runtime.PumpMessages();
            if (!_vrRequested || !_runtime.IsRendering)
                return;

            if (_recenterPending)
            {
                _roomscale.Reset();
                _snapPanelToHead = true;
                _resetBodyYaw = true;
                _headTracker.Recenter();
                _recenterPending = false;
            }

            _headTracker.Update();
            FirstPersonRenderFix.Apply(_config.StereoWeapon.Value);
            if (!_stereoRenderer.PrepareFrame() || !OpenXRHooks.HasViews)
                return;

            OpenXRHooks.GetViews(out var leftView, out var rightView);
            var positional = _config.PositionalTracking.Value;
            var left = _headTracker.Localize(leftView, positional);
            var right = _headTracker.Localize(rightView, positional);
            var playerHandler = Il2Cpp.PlayerHandler.GetPlayerHandler;
            var localPlayer = playerHandler != null ? playerHandler.localPlayerController : null;
            _frame.Update(localPlayer != null ? localPlayer.transform : null);
            // Height above the feet along the character's own up (a rolled ship tilts it). No player (menus,
            // loading): no lift, so a crouch held while leaving a level doesn't leave the view raised.
            if (localPlayer != null && Camera.main != null)
                _realCrouch.UpdateLift(_config.RealCrouch.Value,
                    Vector3.Dot(Camera.main.transform.position - localPlayer.transform.position, _frame.Rotation * Vector3.up),
                    LocalPlayerCrouching());
            else
                _realCrouch.UpdateLift(false, 0f, false);
            _motion.Frame = _frame.Rotation;
            if (_frame.Tilt > 30f != _frameTilted)
            {
                _frameTilted = !_frameTilted;
                Log.Msg(_frameTilted
                    ? $"Level follows your character's floor (tilted {_frame.Tilt:F0} degrees from the world)."
                    : "Level is the world again.");
            }
            if (_resetBodyYaw && Camera.main != null)
            {
                _motion.Body.Reset(_frame.YawOf(Camera.main.transform.rotation));
                _resetBodyYaw = false;
            }
            // Body follows head: when the player turns their head well away from the body, the body turns after it
            // (and the tracking origin with it, so the view doesn't move), so walking goes where they face.
            if (_motion.IsActive && _config.BodyFollowsHead.Value && !_mapView)
            {
                var headYaw = Mathf.DeltaAngle(0f, left.Rotation.eulerAngles.y);
                var bodyTurn = _bodyFollow.Step(headYaw, Time.unscaledDeltaTime);
                if (bodyTurn != 0f)
                {
                    _motion.Body.Turn(bodyTurn);
                    _headTracker.TurnOrigin(bodyTurn);
                    left = _headTracker.Localize(leftView, positional);
                    right = _headTracker.Localize(rightView, positional);
                }
            }
            else
                _bodyFollow.Reset();
            float? bodyYaw = _motion.IsActive ? _motion.Body.Yaw : null;

            _cameraRig.Dlss = _config.Dlss.Value;
            _cameraRig.Quality = _config.GraphicsQuality.Value;
            _cameraRig.ExtraFrameSettingsOff = _config.DebugFrameSettingsOff.Value;
            _headAudio.Enabled = _config.HeadTrackedAudio.Value;
            if (Time.frameCount >= _cutsceneCheckFrame)
            {
                _cutsceneCheckFrame = Time.frameCount + InLevelCheckIntervalFrames;
                _cutscene = IsCutscenePlaying();
            }
            UpdateMapView(Camera.main);
            var firstPersonHead = _firstPerson.Update(_config.ForceFirstPerson.Value, Camera.main, _cutscene);
            if (_cameraRig.ApplyEyes(left, right, _stereoRenderer.LeftTexture, _stereoRenderer.RightTexture, _config.WorldScale.Value,
                    _config.LevelHorizon.Value, _config.StabilizeView.Value, FirstPersonRenderFix.LayerMask, bodyYaw,
                    _config.ClampedViewDistance, firstPersonHead, _frame.Rotation, fullRotation: _mapView, lift: _realCrouch.Lift))
            {
                _stereoRenderer.LeftEyeCamera = _cameraRig.LeftEye;
                _stereoRenderer.RightEyeCamera = _cameraRig.RightEye;
                _headAudio.UpdateHeadPose(_cameraRig.LeftEye, _cameraRig.RightEye);
                _vignette.Update(_config.ComfortVignette.Value, _cameraRig.GamePosition, _cameraRig.ViewRotation, _roomscale.Driving);
                _eyeHeights.Add(_cameraRig.LeftEye.transform.position.y, Time.unscaledTime);
                _uiCompositor.MirrorSource = _stereoRenderer.LeftTexture;
                var headPosition = (left.Position + right.Position) * 0.5f * _config.WorldScale.Value;
                _uiCompositor.PlacePanel(_cameraRig.GamePosition, _cameraRig.ViewRotation, headPosition, left.Rotation,
                    _config.UiFollow.Value, _config.UiSmoothing.Value, _config.UiDistance.Value, _config.UiWidth.Value,
                    _snapPanelToHead, _config.CurvedUi.Value, _config.HelmetUiWidth.Value);
                _snapPanelToHead = false;
                var roomscaleStep = _roomscale.ConsumeCharacterStep(_cameraRig.GamePosition, _cameraRig.ViewRotation);
                if (roomscaleStep != Vector3.zero)
                    _headTracker.ShiftOrigin(roomscaleStep / Mathf.Max(0.01f, _config.WorldScale.Value));
                // Room-scale keeps the character under the player, so a head this far from it means the view and the
                // body lost each other (the view left behind while the body runs on): recenter instead of staying lost.
                var headLocal = new Vector2(_headTracker.LocalPosition.x, _headTracker.LocalPosition.z);
                if (_config.RoomscaleWalking.Value && !_pads.GamepadActive && headLocal.magnitude > RoomscaleLostDistance)
                {
                    Log.Warning($"View was {headLocal.magnitude:F1} m from the character; recentering.");
                    _recenterPending = true;
                }

                var controllers = _lastControllerState;
                var laserPointer = _headTracker.Localize(controllers.RightPointer, positional);
                laserPointer.Rotation *= MotionControls.DebugPointerRotation;
                // No laser while a gamepad is in use: the motion controllers are put down.
                UpdateLaserPointer(_pads.GamepadActive ? default : laserPointer);
                _motion.UpdatePose(_cameraRig.GamePosition, _cameraRig.ViewRotation,
                    _headTracker.Localize(controllers.RightHand, positional), _headTracker.Localize(controllers.RightPointer, positional),
                    _headTracker.Localize(controllers.LeftHand, positional), _headTracker.Localize(controllers.LeftPointer, positional),
                    _rawControllerState.LeftGrip,
                    _config.WorldScale.Value,
                    new Vector3(_config.WeaponOffsetX.Value, _config.WeaponOffsetY.Value, _config.WeaponOffsetZ.Value),
                    _config.WeaponInHand.Value, SightShift(), _config.LeftHandRoll.Value,
                    (_cameraRig.LeftEye.transform.position + _cameraRig.RightEye.transform.position) * 0.5f,
                    _cameraRig.LeftEye.transform.rotation, _rawControllerState.RightTrigger, _config.LookToInteract.Value);
                ShareHands(controllers, positional);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"OnBeforeRender failed: {exception}");
        }
    }
}
