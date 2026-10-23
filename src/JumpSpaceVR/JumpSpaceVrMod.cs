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

[assembly: MelonInfo(typeof(JumpSpaceVR.JumpSpaceVrMod), "JumpSpaceVR", "0.1.4", "Kyle Coleman")]
[assembly: MelonGame("Keepsake Games", "Jump Space")]

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
    private readonly VrUi.ModMenu _modMenu = new();
    private float _menuChordStart = -1f;
    private bool _menuChordFired;
    private const float MenuChordSeconds = 1f;
    private readonly ConformanceInputDriver _mockInput = new();
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

        ApplyRuntimeOverride();

        var pluginsDirectory = Path.Combine(Application.dataPath, "Plugins", "x86_64");
        OpenXRNative.InstallResolver(pluginsDirectory);

        if (!File.Exists(Path.Combine(pluginsDirectory, "UnityOpenXR.dll")))
            Log.Error($"UnityOpenXR.dll is missing from {pluginsDirectory}. Run scripts/install.ps1 again.");

        BuildModMenu();

        // A new level or camera means a new "forward"; line the headset up with where the player is looking.
        _cameraRig.CameraChanged += () => _recenterPending = true;

        Log.Msg($"Loaded. Toggle VR: {_config.ToggleVrKey.Value}, recenter: {_config.RecenterKey.Value}, diagnostics: {_config.DiagnosticsKey.Value}.");
    }

    private void ApplyRuntimeOverride()
    {
        var runtimeJson = _config.OpenXRRuntimeJson.Value;
        if (string.IsNullOrWhiteSpace(runtimeJson))
            return;

        if (!File.Exists(runtimeJson))
        {
            Log.Warning($"OpenXR runtime override not found: {runtimeJson}. Using the system runtime.");
            return;
        }

        Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", runtimeJson);
        Log.Msg($"Using OpenXR runtime override: {runtimeJson}");
    }

    public override void OnUpdate()
    {
        _frameCount++;
        Diagnostics.RecordFrame();
        _cameraRig.RestoreGamePose();
        HandleHotkeys();
        UpdateControllers();

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

    private void BuildModMenu()
    {
        _modMenu.AddChoice("UI follow mode", _config.UiFollow, _ => _snapPanelToHead = true);
        _modMenu.AddNumber("UI smoothing (s)", _config.UiSmoothing, 0.02f, 0f, 1f, "0.00");
        _modMenu.AddNumber("UI size (m)", _config.UiWidth, 0.1f, 0.6f, 6f, "0.0");
        _modMenu.AddNumber("UI distance (m)", _config.UiDistance, 0.1f, 0.5f, 5f, "0.0");
        _modMenu.AddToggle("Level horizon (comfort)", _config.LevelHorizon);
        _modMenu.AddToggle("Remove head bob and shake", _config.StabilizeView);
        _modMenu.AddToggle("Stereo weapon", _config.StereoWeapon);
        _modMenu.AddToggle("Positional tracking (6DoF)", _config.PositionalTracking);
        _modMenu.AddNumber("World scale", _config.WorldScale, 0.05f, 0.5f, 3f, "0.00");
        _modMenu.AddNumber("Render scale", _config.RenderScale, 0.1f, 0.5f, 2f, "0.0",
            value => XRSettings.eyeTextureResolutionScale = Mathf.Clamp(value, 0.5f, 2f));
        _modMenu.AddToggle("VR controllers as gamepad", _config.ControllersAsGamepad);
        _modMenu.AddAction("Recenter view", () => _recenterPending = true);
        _modMenu.AddAction("Close", () => _modMenu.Close());
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
        _stereoRenderer.CaptureSubmittedFrame((left, right) => EyeSnapshot.Save(left, right, "headset"));
    }

    /// <summary>Feeds VR controllers to the game as a gamepad. Both stick clicks together recenters.</summary>
    private void UpdateControllers()
    {
        if (!_vrRequested || _runtime == null || !_runtime.IsRendering || !_config.ControllersAsGamepad.Value)
            return;

        if (_config.MockControllerInput.Value)
            _mockInput.Apply();

        var state = _runtime.Controllers.Read();
        _lastControllerState = state;
        if (!state.IsActive && _config.SyntheticControllers.Value)
            state = new ControllerState { IsActive = true, LeftStick = new Vector2(0.5f, 0f), RightTrigger = 1f, ButtonA = true };

        state.Menu = ProcessMenuButton(state.Menu);

        // Right stick click: a tap still reaches the game (briefly, on release); holding it opens the mod menu.
        var rightClickTap = ProcessMenuChord(state.RightStickClick && !state.LeftStickClick);
        state.RightStickClick = rightClickTap || (state.RightStickClick && state.LeftStickClick);

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
        _modMenu.Update(_hotkeys, _lastControllerState);

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
            _runtime ??= new OpenXRRuntime { RequestConformanceAutomation = _config.MockControllerInput.Value };
            var renderMode = _config.SinglePassInstanced.Value ? StereoRenderMode.SinglePassInstanced : StereoRenderMode.MultiPass;
            if (!_runtime.IsInitialized && !_runtime.Initialize(renderMode))
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

    private void DisableVr()
    {
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
                _snapPanelToHead = true;
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
            if (_cameraRig.ApplyEyes(left, right, _stereoRenderer.LeftTexture, _stereoRenderer.RightTexture, _config.WorldScale.Value,
                    _config.LevelHorizon.Value, _config.StabilizeView.Value, FirstPersonRenderFix.LayerMask))
            {
                _stereoRenderer.LeftEyeCamera = _cameraRig.LeftEye;
                _stereoRenderer.RightEyeCamera = _cameraRig.RightEye;
                _uiCompositor.MirrorSource = _stereoRenderer.LeftTexture;
                var headPosition = (left.Position + right.Position) * 0.5f * _config.WorldScale.Value;
                _uiCompositor.PlacePanel(_cameraRig.GamePosition, _cameraRig.ViewRotation, headPosition, left.Rotation,
                    _config.UiFollow.Value, _config.UiSmoothing.Value, _config.UiDistance.Value, _config.UiWidth.Value,
                    _snapPanelToHead);
                _snapPanelToHead = false;
            }
        }
        catch (Exception exception)
        {
            Log.Error($"OnBeforeRender failed: {exception}");
        }
    }
}
