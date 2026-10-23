using System;
using System.Threading;
using System.Runtime.InteropServices;
using UnityEngine;

namespace JumpSpaceVR.OpenXR;

/// <summary>Per-eye pose (Unity tracking space, meters) and field of view (radians).</summary>
internal struct EyeView
{
    public Vector3 Position;
    public Quaternion Rotation;
    public float AngleLeft;
    public float AngleRight;
    public float AngleUp;
    public float AngleDown;

    public Matrix4x4 ProjectionMatrix(float near, float far)
    {
        var left = Mathf.Tan(AngleLeft) * near;
        var right = Mathf.Tan(AngleRight) * near;
        var up = Mathf.Tan(AngleUp) * near;
        var down = Mathf.Tan(AngleDown) * near;
        return Matrix4x4.Frustum(left, right, down, up, near, far);
    }
}

/// <summary>
/// Intercepts xrLocateViews by wrapping xrGetInstanceProcAddr before UnityOpenXR loads its function table.
/// Every time Unity's display provider locates the eye views for a frame, the result is copied here, so the
/// mod renders with exactly the poses and FOVs the compositor will reproject against.
/// </summary>
internal static unsafe class OpenXRViewTracker
{
    private const int XrSuccess = 0;
    private const ulong OrientationValidBit = 0x1;
    private const ulong PositionValidBit = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct XrViewState
    {
        public int Type;
        public void* Next;
        public ulong ViewStateFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrView
    {
        public int Type;
        public void* Next;
        public float OrientationX, OrientationY, OrientationZ, OrientationW;
        public float PositionX, PositionY, PositionZ;
        public float AngleLeft, AngleRight, AngleUp, AngleDown;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetInstanceProcAddrDelegate(ulong instance, byte* name, IntPtr* function);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LocateViewsDelegate(ulong session, void* locateInfo, XrViewState* viewState, uint capacity, uint* countOutput, XrView* views);

    // Static fields keep the marshalled delegates alive for as long as native code holds their pointers.
    private static readonly GetInstanceProcAddrDelegate GetInstanceProcAddrHook = GetInstanceProcAddr;
    private static readonly LocateViewsDelegate LocateViewsHook = LocateViews;
    private static readonly IntPtr LocateViewsHookPointer = Marshal.GetFunctionPointerForDelegate(LocateViewsHook);

    private static delegate* unmanaged<ulong, byte*, IntPtr*, int> _nextGetInstanceProcAddr;
    private static delegate* unmanaged<ulong, void*, XrViewState*, uint, uint*, XrView*, int> _originalLocateViews;

    private static readonly object Sync = new();
    private static readonly EyeView[] Views = new EyeView[2];
    private static int _frameStamp;

    /// <summary>Incremented on every successful xrLocateViews call with two valid stereo views.</summary>
    public static int FrameStamp => Volatile.Read(ref _frameStamp);

    public static bool HasViews => FrameStamp > 0;

    /// <summary>Wraps the loader's xrGetInstanceProcAddr; pass the returned pointer to UnityOpenXR.</summary>
    public static IntPtr Wrap(IntPtr getInstanceProcAddr)
    {
        _nextGetInstanceProcAddr = (delegate* unmanaged<ulong, byte*, IntPtr*, int>)getInstanceProcAddr;
        return Marshal.GetFunctionPointerForDelegate(GetInstanceProcAddrHook);
    }

    public static void GetViews(out EyeView left, out EyeView right)
    {
        lock (Sync)
        {
            left = Views[0];
            right = Views[1];
        }
    }

    private static int GetInstanceProcAddr(ulong instance, byte* name, IntPtr* function)
    {
        var result = _nextGetInstanceProcAddr(instance, name, function);
        try
        {
            if (result == XrSuccess && function != null && *function != IntPtr.Zero && IsName(name, "xrLocateViews"))
            {
                _originalLocateViews = (delegate* unmanaged<ulong, void*, XrViewState*, uint, uint*, XrView*, int>)*function;
                *function = LocateViewsHookPointer;
            }
        }
        catch
        {
            // Never let a managed exception unwind into native OpenXR code; the original pointer stays in place.
        }
        return result;
    }

    private static int LocateViews(ulong session, void* locateInfo, XrViewState* viewState, uint capacity, uint* countOutput, XrView* views)
    {
        var result = _originalLocateViews(session, locateInfo, viewState, capacity, countOutput, views);
        if (result != XrSuccess || views == null || capacity < 2 || countOutput == null || *countOutput < 2 || viewState == null)
            return result;

        const ulong required = OrientationValidBit | PositionValidBit;
        if ((viewState->ViewStateFlags & required) != required)
            return result;

        lock (Sync)
        {
            Views[0] = ToEyeView(views[0]);
            Views[1] = ToEyeView(views[1]);
        }
        Interlocked.Increment(ref _frameStamp);
        return result;
    }

    /// <summary>OpenXR is right-handed (-Z forward); Unity is left-handed (+Z forward).</summary>
    private static EyeView ToEyeView(in XrView view) => new()
    {
        Position = new Vector3(view.PositionX, view.PositionY, -view.PositionZ),
        Rotation = new Quaternion(-view.OrientationX, -view.OrientationY, view.OrientationZ, view.OrientationW),
        AngleLeft = view.AngleLeft,
        AngleRight = view.AngleRight,
        AngleUp = view.AngleUp,
        AngleDown = view.AngleDown,
    };

    private static bool IsName(byte* name, string expected)
    {
        for (var index = 0; index < expected.Length; index++)
        {
            if (name[index] != expected[index])
                return false;
        }
        return name[expected.Length] == 0;
    }
}
