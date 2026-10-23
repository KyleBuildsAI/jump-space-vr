using System;
using System.Runtime.InteropServices;
using System.Text;

namespace JumpSpaceVR.OpenXR;

/// <summary>
/// Developer test aid: drives simulated Oculus Touch controllers through XR_EXT_conformance_automation
/// (supported by Unity's OpenXR mock runtime), so the full controller path (bindings, sync, action state,
/// virtual gamepad) can be verified without hardware.
/// </summary>
internal sealed unsafe class ConformanceInputDriver
{
    public const string ExtensionName = "XR_EXT_conformance_automation";
    private const string TouchProfile = "/interaction_profiles/oculus/touch_controller";

    [StructLayout(LayoutKind.Sequential)]
    private struct XrVector2f
    {
        public float X;
        public float Y;
    }

    private delegate* unmanaged<ulong, byte*, ulong*, int> _stringToPath;
    private delegate* unmanaged<ulong, ulong, ulong, uint, int> _setActive;
    private delegate* unmanaged<ulong, ulong, ulong, uint, int> _setBool;
    private delegate* unmanaged<ulong, ulong, ulong, float, int> _setFloat;
    private delegate* unmanaged<ulong, ulong, ulong, XrVector2f, int> _setVector2;
    private bool _resolved;
    private bool _failed;
    private bool _reported;

    /// <summary>Sets a fixed, recognisable controller state for the current frame.</summary>
    public void Apply()
    {
        if (_failed || OpenXRHooks.Session == 0 || !Resolve())
            return;

        var instance = OpenXRHooks.Instance;
        var session = OpenXRHooks.Session;
        var profile = Path(instance, TouchProfile);
        var left = Path(instance, "/user/hand/left");
        var right = Path(instance, "/user/hand/right");

        var results = new[]
        {
            _setActive(session, profile, left, 1),
            _setActive(session, profile, right, 1),
            _setVector2(session, left, Path(instance, "/user/hand/left/input/thumbstick"), new XrVector2f { X = 0f, Y = 0.8f }),
            _setFloat(session, right, Path(instance, "/user/hand/right/input/trigger/value"), 0.75f),
            _setFloat(session, left, Path(instance, "/user/hand/left/input/squeeze/value"), 1f),
            _setBool(session, right, Path(instance, "/user/hand/right/input/b/click"), 1),
        };
        if (!_reported)
        {
            _reported = true;
            Log.Msg($"Mock controller input results: {string.Join(", ", results)}");
        }
    }

    private int _resolvedGeneration;

    private bool Resolve()
    {
        if (_resolved && _resolvedGeneration == OpenXRHooks.Generation)
            return true;
        _resolvedGeneration = OpenXRHooks.Generation;
        _resolved = false;

        _stringToPath = (delegate* unmanaged<ulong, byte*, ulong*, int>)OpenXRHooks.Resolve("xrStringToPath");
        _setActive = (delegate* unmanaged<ulong, ulong, ulong, uint, int>)OpenXRHooks.Resolve("xrSetInputDeviceActiveEXT");
        _setBool = (delegate* unmanaged<ulong, ulong, ulong, uint, int>)OpenXRHooks.Resolve("xrSetInputDeviceStateBoolEXT");
        _setFloat = (delegate* unmanaged<ulong, ulong, ulong, float, int>)OpenXRHooks.Resolve("xrSetInputDeviceStateFloatEXT");
        _setVector2 = (delegate* unmanaged<ulong, ulong, ulong, XrVector2f, int>)OpenXRHooks.Resolve("xrSetInputDeviceStateVector2fEXT");

        if (_stringToPath == null || _setActive == null || _setBool == null || _setFloat == null || _setVector2 == null)
        {
            _failed = true;
            Log.Warning($"{ExtensionName} is not available on this runtime; mock controller input disabled.");
            return false;
        }

        _resolved = true;
        Log.Msg("Mock controller input enabled (conformance automation).");
        return true;
    }

    private ulong Path(ulong instance, string path)
    {
        var bytes = Encoding.ASCII.GetBytes(path + "\0");
        ulong result = 0;
        fixed (byte* pointer = bytes)
            _stringToPath(instance, pointer, &result);
        return result;
    }
}
