using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace JumpSpaceVR.OpenXR;

/// <summary>
/// Minimal port of the Unity OpenXR loader lifecycle (com.unity.xr.openxr OpenXRLoader) for an IL2CPP
/// game that ships without the managed OpenXR package.
/// </summary>
internal sealed class OpenXRRuntime
{
    private const string LoaderLibraryName = "openxr_loader";
    private const float IdlePollIntervalSeconds = 0.1f;

    // Held in a static field so the GC never collects the delegate the native plugin calls into.
    private static OpenXRNative.ReceiveNativeEventDelegate _nativeEventCallback;
    private static OpenXRRuntime _instance;

    private readonly XrSubsystems _subsystems = new();
    private NativeEvent _sessionState = NativeEvent.XrIdle;
    private float _lastPollTime;
    private bool _initialized;
    private bool _startRequested;

    public bool IsInitialized => _initialized;
    public bool IsRendering => _subsystems.DisplayRunning;
    public NativeEvent SessionState => _sessionState;
    public XrSubsystems Subsystems => _subsystems;

    /// <summary>Suppresses repeated failure messages while the mod retries in the background.</summary>
    public bool QuietFailures { get; set; }
    public OpenXRControllers Controllers { get; } = new();

    public event Action<NativeEvent> SessionStateChanged;

    public bool Initialize(StereoRenderMode renderMode)
    {
        if (_initialized)
            return true;

        _instance = this;
        OpenXRNative.SetSuccessfullyInitialized(false);

        if (!OpenXRNative.LoadOpenXRLibrary(OpenXRNative.ToWideString(LoaderLibraryName)))
        {
            Log.Error("Failed to load the OpenXR loader (openxr_loader.dll).");
            return false;
        }

        OpenXRNative.SetProcAddressPtrAndLoadStage1(OpenXRHooks.Wrap(OpenXRNative.GetProcAddressPtr(true)));

        if (!OpenXRNative.InitializeSession())
        {
            Log.Error("OpenXR instance creation failed. Is a VR runtime (SteamVR, Virtual Desktop, Oculus) running and set as the active OpenXR runtime?");
            OpenXRNative.UnloadOpenXRLibrary();
            return false;
        }

        _nativeEventCallback = ReceiveNativeEvent;
        OpenXRNative.SetCallbacks(_nativeEventCallback);
        OpenXRNative.SetRenderMode(renderMode);
        SetApplicationInfo();

        try
        {
            _subsystems.Create();
        }
        catch (Exception exception)
        {
            if (!QuietFailures)
                Log.Msg($"OpenXR display unavailable ({exception.Message}). See Player.log for the runtime's reason; " +
                    "XR_ERROR_FORM_FACTOR_UNAVAILABLE means the headset is not connected.");
            Shutdown();
            return false;
        }

        _initialized = true;
        Log.Msg($"OpenXR initialized (render mode: {renderMode}).");
        return true;
    }

    /// <summary>Requests the session. Rendering begins once the runtime reports XR_SESSION_STATE_READY.</summary>
    public bool Start()
    {
        if (!_initialized)
            return false;

        _startRequested = true;
        return StartInternal();
    }

    public void Stop()
    {
        _startRequested = false;
        StopInternal();
    }

    public void Shutdown()
    {
        Stop();
        OpenXRNative.RequestExitSession();
        PumpMessages(force: true);
        _subsystems.Destroy();
        OpenXRNative.DestroySession();
        PumpMessages(force: true);
        OpenXRNative.UnloadOpenXRLibrary();
        _initialized = false;
        if (_instance == this)
            _instance = null;
    }

    /// <summary>Must be called once per frame (before rendering) while initialized.</summary>
    public void PumpMessages(bool force = false)
    {
        if (!force && IsIdleState(_sessionState))
        {
            var now = Time.realtimeSinceStartup;
            if (now - _lastPollTime < IdlePollIntervalSeconds)
                return;
            _lastPollTime = now;
        }

        OpenXRNative.PumpMessageLoop();
    }

    private bool StartInternal()
    {
        if (!OpenXRNative.CreateSessionIfNeeded())
        {
            Log.Error("Failed to create the OpenXR session.");
            return false;
        }

        // Action sets must be attached before the session begins, and before Unity could attach its own.
        Controllers.Setup();

        if (_sessionState != NativeEvent.XrReady || !_startRequested)
            return true;

        // Display must start before xrBeginSession and before Input, matching the Unity loader.
        _subsystems.StartDisplay();
        if (!_subsystems.DisplayRunning)
        {
            Log.Error("OpenXR display subsystem failed to start.");
            return false;
        }

        OpenXRNative.BeginSession();
        _subsystems.StartInput();

        Log.Msg($"OpenXR session started (display: {_subsystems.DisplayRunning}, input: {_subsystems.InputRunning}).");
        return _subsystems.DisplayRunning && _subsystems.InputRunning;
    }

    private void StopInternal()
    {
        var wasRunning = _subsystems.DisplayRunning || _subsystems.InputRunning;
        _subsystems.Stop();
        if (wasRunning)
        {
            OpenXRNative.EndSession();
            PumpMessages(force: true);
        }
    }

    private static void SetApplicationInfo()
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(Application.version));
        if (BitConverter.IsLittleEndian)
            Array.Reverse(hash);
        var versionHash = BitConverter.ToUInt32(hash, 0);
        OpenXRNative.SetApplicationInfo(Application.productName, Application.version, versionHash, Application.unityVersion);
    }

    private static bool IsIdleState(NativeEvent state) =>
        state is NativeEvent.XrIdle or NativeEvent.XrStopping or NativeEvent.XrExiting
            or NativeEvent.XrLossPending or NativeEvent.XrInstanceLossPending;

    private static void ReceiveNativeEvent(NativeEvent nativeEvent, ulong payload)
    {
        var runtime = _instance;
        if (runtime == null)
            return;

        try
        {
            runtime.HandleNativeEvent(nativeEvent);
        }
        catch (Exception exception)
        {
            Log.Error($"Error handling OpenXR event {nativeEvent}: {exception}");
        }
    }

    private void HandleNativeEvent(NativeEvent nativeEvent)
    {
        if (nativeEvent is >= NativeEvent.XrIdle and <= NativeEvent.XrInstanceLossPending)
        {
            _sessionState = nativeEvent;
            Log.Msg($"OpenXR session state: {nativeEvent}");
            SessionStateChanged?.Invoke(nativeEvent);
        }

        switch (nativeEvent)
        {
            case NativeEvent.XrReady:
                StartInternal();
                break;
            case NativeEvent.XrStopping:
                StopInternal();
                break;
            case NativeEvent.XrExiting:
            case NativeEvent.XrLossPending:
            case NativeEvent.XrInstanceLossPending:
                Log.Warning($"OpenXR runtime requested shutdown ({nativeEvent}). Press the VR toggle key to restart.");
                break;
            case NativeEvent.XrRequestRestartLoop:
            case NativeEvent.XrRequestGetSystemLoop:
                Log.Warning("Headset not available yet. Put the headset on / start the VR runtime, then press the VR toggle key.");
                break;
        }
    }
}
