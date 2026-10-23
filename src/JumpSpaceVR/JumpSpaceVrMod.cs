using System;
using System.IO;
using JumpSpaceVR.Controls;
using JumpSpaceVR.OpenXR;
using JumpSpaceVR.Rendering;
using JumpSpaceVR.Tracking;
using MelonLoader;
using UnityEngine;
using UnityEngine.XR;

[assembly: MelonInfo(typeof(JumpSpaceVR.JumpSpaceVrMod), "JumpSpaceVR", "0.1.0", "Kyle Coleman")]
[assembly: MelonGame("Keepsake Games", "Jump Space")]

namespace JumpSpaceVR;

/// <summary>
/// Entry point. Boots OpenXR inside the game's HDRP renderer and drives the gameplay camera from the headset.
/// </summary>
public sealed class JumpSpaceVrMod : MelonMod
{
    private const int StartupDelayFrames = 120;
    private const int AutoDiagnosticsDelayFrames = 1200;

    private ModConfig _config;
    private OpenXRRuntime _runtime;
    private readonly HeadTracker _headTracker = new();
    private readonly VrCameraRig _cameraRig = new();
    private readonly StereoRenderer _stereoRenderer = new();
    private readonly VrUi.UiCompositor _uiCompositor = new();
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
        _cameraRig.RestoreGamePose();
        HandleHotkeys();

        if (_frameCount == StartupDelayFrames && _vrRequested)
            EnableVr();

        // One automatic report shortly after startup makes user bug reports useful without extra steps.
        if (_frameCount == StartupDelayFrames + AutoDiagnosticsDelayFrames && _runtime != null)
        {
            Diagnostics.Dump(_runtime);
            if (_config.SnapshotOnStartup.Value)
                SaveSnapshots();
        }
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
            _runtime ??= new OpenXRRuntime();
            var renderMode = _config.SinglePassInstanced.Value ? StereoRenderMode.SinglePassInstanced : StereoRenderMode.MultiPass;
            if (!_runtime.IsInitialized && !_runtime.Initialize(renderMode))
            {
                _vrRequested = false;
                return;
            }

            Log.Msg("Hooking render loop...");
            HookBeforeRender();
            XRSettings.eyeTextureResolutionScale = _config.ClampedRenderScale;
            _recenterPending = true;

            Log.Msg("Starting OpenXR session...");
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
        _uiCompositor.Stop();
        _stereoRenderer.Deactivate();
        _stereoRenderer.LeftEyeCamera = null;
        _stereoRenderer.RightEyeCamera = null;
        _cameraRig.Release();
        _runtime?.Stop();
        Log.Msg("VR stopped.");
    }

    private void HookBeforeRender()
    {
        if (_beforeRenderHooked)
            return;

        _beforeRenderHooked = BeforeRenderHook.Install(HarmonyInstance, OnBeforeRender)
                              && StereoRenderer.InstallHooks(HarmonyInstance);
    }

    private int _traceFrames = 3;

    private void Trace(string step)
    {
        if (_traceFrames > 0)
            Log.Msg($"[trace] {step}");
    }

    private void OnBeforeRender()
    {
        try
        {
            if (_runtime == null || !_runtime.IsInitialized)
                return;

            Trace("pump");
            _runtime.PumpMessages();
            if (!_vrRequested || !_runtime.IsRendering)
                return;

            if (_recenterPending)
            {
                Trace("recenter");
                _headTracker.Recenter();
                _recenterPending = false;
            }

            Trace("head");
            _headTracker.Update();
            if (!_stereoRenderer.PrepareFrame() || !OpenXRViewTracker.HasViews)
                return;

            Trace("eyes");
            OpenXRViewTracker.GetViews(out var leftView, out var rightView);
            var positional = _config.PositionalTracking.Value;
            var left = _headTracker.Localize(leftView, positional);
            var right = _headTracker.Localize(rightView, positional);
            if (_cameraRig.ApplyEyes(left, right, _stereoRenderer.LeftTexture, _stereoRenderer.RightTexture, _config.WorldScale.Value))
            {
                _stereoRenderer.LeftEyeCamera = _cameraRig.LeftEye;
                _stereoRenderer.RightEyeCamera = _cameraRig.RightEye;
                _uiCompositor.MirrorSource = _stereoRenderer.LeftTexture;
                _uiCompositor.PlacePanel(_cameraRig.GamePosition, _cameraRig.GameRotation, _config.UiDistance.Value, _config.UiWidth.Value);
            }
            Trace("done");
            _traceFrames--;
        }
        catch (Exception exception)
        {
            Log.Error($"OnBeforeRender failed: {exception}");
        }
    }
}
