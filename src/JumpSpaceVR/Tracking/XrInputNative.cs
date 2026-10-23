using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace JumpSpaceVR.Tracking;

/// <summary>
/// Direct calls into Unity's XR input bindings (InputDevices.TryGetFeatureValue_*_Injected).
///
/// The Il2CppInterop wrappers for these methods fail at runtime (they marshal the usage name through a
/// ReadOnlySpan overload that does not exist in this build), so the native icalls are resolved and invoked
/// with the same calling convention Unity's generated bindings use.
/// </summary>
internal static unsafe class XrInputNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ManagedSpanWrapper
    {
        public void* Begin;
        public int Length;
    }

    private static readonly delegate* unmanaged<ulong, ManagedSpanWrapper*, Vector3*, byte> TryGetVector3 =
        (delegate* unmanaged<ulong, ManagedSpanWrapper*, Vector3*, byte>)Resolve("TryGetFeatureValue_Vector3f_Injected");

    private static readonly delegate* unmanaged<ulong, ManagedSpanWrapper*, Quaternion*, byte> TryGetQuaternion =
        (delegate* unmanaged<ulong, ManagedSpanWrapper*, Quaternion*, byte>)Resolve("TryGetFeatureValue_Quaternionf_Injected");

    private static readonly delegate* unmanaged<ulong, ManagedSpanWrapper*, Vector2*, byte> TryGetVector2 =
        (delegate* unmanaged<ulong, ManagedSpanWrapper*, Vector2*, byte>)Resolve("TryGetFeatureValue_Vector2f_Injected");

    private static readonly delegate* unmanaged<ulong, ManagedSpanWrapper*, float*, byte> TryGetFloat =
        (delegate* unmanaged<ulong, ManagedSpanWrapper*, float*, byte>)Resolve("TryGetFeatureValue_float_Injected");

    private static readonly delegate* unmanaged<ulong, ManagedSpanWrapper*, byte*, byte> TryGetBool =
        (delegate* unmanaged<ulong, ManagedSpanWrapper*, byte*, byte>)Resolve("TryGetFeatureValue_bool_Injected");

    public static bool TryGet(ulong deviceId, string usage, out Vector3 value)
    {
        value = default;
        if (TryGetVector3 == null)
            return false;
        fixed (char* chars = usage)
        fixed (Vector3* result = &value)
        {
            var span = new ManagedSpanWrapper { Begin = chars, Length = usage.Length };
            return TryGetVector3(deviceId, &span, result) != 0;
        }
    }

    public static bool TryGet(ulong deviceId, string usage, out Quaternion value)
    {
        value = Quaternion.identity;
        if (TryGetQuaternion == null)
            return false;
        fixed (char* chars = usage)
        fixed (Quaternion* result = &value)
        {
            var span = new ManagedSpanWrapper { Begin = chars, Length = usage.Length };
            return TryGetQuaternion(deviceId, &span, result) != 0;
        }
    }

    public static bool TryGet(ulong deviceId, string usage, out Vector2 value)
    {
        value = default;
        if (TryGetVector2 == null)
            return false;
        fixed (char* chars = usage)
        fixed (Vector2* result = &value)
        {
            var span = new ManagedSpanWrapper { Begin = chars, Length = usage.Length };
            return TryGetVector2(deviceId, &span, result) != 0;
        }
    }

    public static bool TryGet(ulong deviceId, string usage, out float value)
    {
        value = 0f;
        if (TryGetFloat == null)
            return false;
        fixed (char* chars = usage)
        fixed (float* result = &value)
        {
            var span = new ManagedSpanWrapper { Begin = chars, Length = usage.Length };
            return TryGetFloat(deviceId, &span, result) != 0;
        }
    }

    public static bool TryGet(ulong deviceId, string usage, out bool value)
    {
        value = false;
        if (TryGetBool == null)
            return false;
        byte raw = 0;
        fixed (char* chars = usage)
        {
            var span = new ManagedSpanWrapper { Begin = chars, Length = usage.Length };
            var found = TryGetBool(deviceId, &span, &raw) != 0;
            value = raw != 0;
            return found;
        }
    }

    private static IntPtr Resolve(string method)
    {
        var pointer = IL2CPP.il2cpp_resolve_icall("UnityEngine.XR.InputDevices::" + method);
        if (pointer == IntPtr.Zero)
            Log.Error($"XR input binding not found: {method}");
        return pointer;
    }
}
