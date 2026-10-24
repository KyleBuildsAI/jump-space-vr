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

[assembly: MelonInfo(typeof(JumpSpaceVR.JumpSpaceVrMod), "JumpSpaceVR", "0.8.0", "Kyle Coleman")]
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
            if (!_firstPerson.RecentlyActive)
                _recenterPending = true;
        };

        Log.Msg($"Loaded. Toggle VR: {_config.ToggleVrKey.Value}, recenter: {_config.RecenterKey.Value}, diagnostics: {_config.DiagnosticsKey.Value}.");
    }

    public override void OnUpdate()
    {
        _frameCount++;
        Diagnostics.RecordFrame();
        _cameraRig.RestoreGamePose();
        _motion.RestoreWeapon();
        _cinematicBars.Update(_runtime != null && _runtime.IsRendering && _config.HideCinematicBars.Value);
        _frameRateCounter.Update(_config.ShowFrameRate.Value);
        _colorGrading.Apply(_runtime != null && _runtime.IsRendering ? _config.ColorPreset.Value : ColorPreset.Off);
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
        _modMenu.AddChoice("Colour grading", _config.ColorPreset);
        _modMenu.AddToggle("Stereo weapon", _config.StereoWeapon);
        _modMenu.AddChoice("VR runtime (F8 twice to apply)", _config.Runtime);
        _modMenu.EndPage();

        _modMenu.BeginPage("Controls");
        _modMenu.AddChoice("Controls (VR controllers or gamepad)", _config.ControllerMode);
        _modMenu.AddToggle("Motion controls (aim with hand)", _config.MotionControls);
        _modMenu.AddChoice("Turn mode", _config.TurnMode);
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

        _motion.ProcessInput(ref state, _config.MotionControls.Value && !_modMenu.IsOpen, _config.TurnMode.Value,
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
        _gamepad.Send(state);
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
            float? bodyYaw = _motion.IsActive ? _motion.Body.Yaw : null;

            _cameraRig.Dlss = _config.Dlss.Value;
            _cameraRig.Quality = _config.GraphicsQuality.Value;
            _headAudio.Enabled = _config.HeadTrackedAudio.Value;
            if (Time.frameCount >= _cutsceneCheckFrame)
            {
                _cutsceneCheckFrame = Time.frameCount + InLevelCheckIntervalFrames;
                _cutscene = IsCutscenePlaying();
            }
            var firstPersonHead = _firstPerson.Update(_config.ForceFirstPerson.Value, Camera.main, _cutscene);
            if (_cameraRig.ApplyEyes(left, right, _stereoRenderer.LeftTexture, _stereoRenderer.RightTexture, _config.WorldScale.Value,
                    _config.LevelHorizon.Value, _config.StabilizeView.Value, FirstPersonRenderFix.LayerMask, bodyYaw,
                    _config.ClampedViewDistance, firstPersonHead, _frame.Rotation))
            {
                _stereoRenderer.LeftEyeCamera = _cameraRig.LeftEye;
                _stereoRenderer.RightEyeCamera = _cameraRig.RightEye;
                _headAudio.UpdateHeadPose(_cameraRig.LeftEye, _cameraRig.RightEye);
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
                    (_cameraRig.LeftEye.transform.position + _cameraRig.RightEye.transform.position) * 0.5f);
                ShareHands(controllers, positional);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"OnBeforeRender failed: {exception}");
        }
    }
}
