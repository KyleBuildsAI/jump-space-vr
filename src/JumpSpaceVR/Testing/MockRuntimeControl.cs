using System;
using System.Runtime.InteropServices;
using JumpSpaceVR.OpenXR;
using UnityEngine;

namespace JumpSpaceVR.Testing;

/// <summary>
/// Drives Unity's OpenXR mock runtime (development testing only): the head pose is set through the mock's
/// own API (MockRuntime_SetView / SetReferenceSpace), which it exposes via xrGetInstanceProcAddr.
/// All values are in Unity's tracking space (meters, left-handed); the mock converts to OpenXR itself.
/// </summary>
internal sealed unsafe class MockRuntimeControl
{
    private const int ViewConfigurationPrimaryStereo = 2;
    private const int ReferenceSpaceView = 1;
    private const ulong LocationAllValidAndTracked = 0xF;
    private const float InterpupillaryDistance = 0.064f;
    private static readonly Vector4 DefaultFov = new(-0.8f, 0.8f, 0.85f, -0.85f);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnityVector3 { public float X, Y, Z; }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnityVector4 { public float X, Y, Z, W; }

    private delegate* unmanaged<int, int, UnityVector3, UnityVector4, UnityVector4, void> _setView;
    private delegate* unmanaged<int, UnityVector3, UnityVector4, ulong, void> _setReferenceSpace;
    private int _generation = -1;

    public bool IsAvailable => Resolve();

    /// <summary>Places the head (both eyes around it, plus the VIEW reference space used for head tracking).</summary>
    public void SetHead(Vector3 position, Quaternion rotation)
    {
        if (!Resolve())
            return;
        var fov = new UnityVector4 { X = DefaultFov.x, Y = DefaultFov.y, Z = DefaultFov.z, W = DefaultFov.w };
        for (var eye = 0; eye < 2; eye++)
        {
            var offset = rotation * new Vector3(eye == 0 ? -InterpupillaryDistance / 2f : InterpupillaryDistance / 2f, 0f, 0f);
            _setView(ViewConfigurationPrimaryStereo, eye, ToUnity(position + offset), ToUnity(rotation), fov);
        }
        _setReferenceSpace(ReferenceSpaceView, ToUnity(position), ToUnity(rotation), LocationAllValidAndTracked);
    }

    private bool Resolve()
    {
        if (_generation == OpenXRHooks.Generation && _setView != null)
            return true;
        if (OpenXRHooks.Instance == 0)
            return false;

        _setView = (delegate* unmanaged<int, int, UnityVector3, UnityVector4, UnityVector4, void>)OpenXRHooks.Resolve("MockRuntime_SetView");
        _setReferenceSpace = (delegate* unmanaged<int, UnityVector3, UnityVector4, ulong, void>)OpenXRHooks.Resolve("MockRuntime_SetReferenceSpace");
        _generation = OpenXRHooks.Generation;
        return _setView != null && _setReferenceSpace != null;
    }

    private static UnityVector3 ToUnity(Vector3 value) => new() { X = value.x, Y = value.y, Z = value.z };

    private static UnityVector4 ToUnity(Quaternion value) => new() { X = value.x, Y = value.y, Z = value.z, W = value.w };
}
