using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

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
    private delegate* unmanaged<ulong, ulong, ulong, ulong, XrPosef, int> _setLocation;
    private bool _resolved;
    private bool _failed;
    private bool _reported;

    [StructLayout(LayoutKind.Sequential)]
    private struct XrPosef
    {
        public float OrientationX, OrientationY, OrientationZ, OrientationW;
        public float PositionX, PositionY, PositionZ;
    }

    private enum InputKind { Float, Bool, Vector2 }

    /// <summary>Short input names a test scenario can use, mapped to Touch controller input paths.</summary>
    private static readonly Dictionary<string, (string Path, InputKind Kind)> InputNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["left.stick"] = ("/user/hand/left/input/thumbstick", InputKind.Vector2),
        ["right.stick"] = ("/user/hand/right/input/thumbstick", InputKind.Vector2),
        ["left.trigger"] = ("/user/hand/left/input/trigger/value", InputKind.Float),
        ["right.trigger"] = ("/user/hand/right/input/trigger/value", InputKind.Float),
        ["left.grip"] = ("/user/hand/left/input/squeeze/value", InputKind.Float),
        ["right.grip"] = ("/user/hand/right/input/squeeze/value", InputKind.Float),
        ["right.a"] = ("/user/hand/right/input/a/click", InputKind.Bool),
        ["right.b"] = ("/user/hand/right/input/b/click", InputKind.Bool),
        ["left.x"] = ("/user/hand/left/input/x/click", InputKind.Bool),
        ["left.y"] = ("/user/hand/left/input/y/click", InputKind.Bool),
        ["left.menu"] = ("/user/hand/left/input/menu/click", InputKind.Bool),
        ["left.stickclick"] = ("/user/hand/left/input/thumbstick/click", InputKind.Bool),
        ["right.stickclick"] = ("/user/hand/right/input/thumbstick/click", InputKind.Bool),
    };

    private readonly Dictionary<string, Vector2> _scripted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<bool, XrPosef> _handPoses = new();

    /// <summary>True once a test scenario has taken over the inputs (the default trigger pulse stops).</summary>
    public bool IsScripted { get; private set; }

    public static bool IsKnownInput(string name) => InputNames.ContainsKey(name);

    /// <summary>Sets an input for all following frames (x = value; y is the second axis of a stick).</summary>
    public void SetInput(string name, float x, float y = 0f)
    {
        BeginScripting();
        _scripted[name] = new Vector2(x, y);
    }

    /// <summary>
    /// Places one hand (grip and aim pose) in tracking space, in Unity coordinates. The mock runtime reports the
    /// location of a bound pose action from these values; MockRuntime_SetActionSpace is ignored once bindings exist.
    /// </summary>
    public void SetHandPose(bool rightHand, Vector3 position, Quaternion rotation)
    {
        BeginScripting();
        _handPoses[rightHand] = new XrPosef
        {
            OrientationX = -rotation.x,
            OrientationY = -rotation.y,
            OrientationZ = rotation.z,
            OrientationW = rotation.w,
            PositionX = position.x,
            PositionY = position.y,
            PositionZ = -position.z,
        };
    }

    /// <summary>Stops the default trigger pulse and releases the trigger it may have left pressed.</summary>
    private void BeginScripting()
    {
        if (IsScripted)
            return;
        IsScripted = true;
        _scripted["right.trigger"] = Vector2.zero;
    }

    /// <summary>Pushes this frame's controller state to the mock runtime.</summary>
    public void Apply()
    {
        if (_failed || OpenXRHooks.Session == 0 || !Resolve())
            return;

        var instance = OpenXRHooks.Instance;
        var session = OpenXRHooks.Session;
        var profile = Path(instance, TouchProfile);
        var left = Path(instance, "/user/hand/left");
        var right = Path(instance, "/user/hand/right");
        _setActive(session, profile, left, 1);
        _setActive(session, profile, right, 1);

        if (!IsScripted)
        {
            // Default: only a pulsing trigger, enough to drive the gamepad path and laser-pointer clicks without a
            // held stick or button fighting menu navigation.
            _setFloat(session, right, Path(instance, "/user/hand/right/input/trigger/value"),
                Mathf.Repeat(Time.unscaledTime, 1.2f) < 0.6f ? 0.75f : 0f);
            return;
        }

        foreach (var (rightHand, pose) in _handPoses)
        {
            var handName = rightHand ? "right" : "left";
            var hand = rightHand ? right : left;
            foreach (var source in new[] { "grip", "aim" })
            {
                var result = _setLocation(session, hand, Path(instance, $"/user/hand/{handName}/input/{source}/pose"), OpenXRHooks.ViewSpace, pose);
                ReportRejected($"{handName}.{source} pose", result);
            }
        }

        foreach (var (name, value) in _scripted)
        {
            var (path, kind) = InputNames[name];
            var hand = name.StartsWith("left", StringComparison.OrdinalIgnoreCase) ? left : right;
            var pathId = Path(instance, path);
            var result = kind switch
            {
                InputKind.Float => _setFloat(session, hand, pathId, value.x),
                InputKind.Bool => _setBool(session, hand, pathId, value.x > 0.5f ? 1u : 0u),
                _ => _setVector2(session, hand, pathId, new XrVector2f { X = value.x, Y = value.y }),
            };
            ReportRejected(name, result);
        }
    }

    private void ReportRejected(string input, int result)
    {
        if (result == 0 || _reported)
            return;
        _reported = true;
        Log.Warning($"Mock input {input} rejected ({result}).");
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
        _setLocation = (delegate* unmanaged<ulong, ulong, ulong, ulong, XrPosef, int>)OpenXRHooks.Resolve("xrSetInputDeviceLocationEXT");

        if (_stringToPath == null || _setActive == null || _setBool == null || _setFloat == null || _setVector2 == null || _setLocation == null)
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
