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
/// Wraps xrGetInstanceProcAddr before UnityOpenXR loads its function table, to observe the OpenXR calls the
/// mod depends on: xrLocateViews (eye poses and FOVs) and xrCreateSession (the session handle used for
/// controller input). Also resolves further OpenXR functions for the mod's own use.
///
/// Every time Unity's display provider locates the eye views for a frame, the result is copied here, so the
/// mod renders with exactly the poses and FOVs the compositor will reproject against.
/// </summary>
internal static unsafe class OpenXRHooks
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateSessionDelegate(ulong instance, void* createInfo, ulong* session);

    private static readonly CreateSessionDelegate CreateSessionHook = CreateSession;
    private static readonly IntPtr CreateSessionHookPointer = Marshal.GetFunctionPointerForDelegate(CreateSessionHook);
    private static delegate* unmanaged<ulong, void*, ulong*, int> _originalCreateSession;

    private static delegate* unmanaged<ulong, byte*, IntPtr*, int> _nextGetInstanceProcAddr;

    private static long _displayTime;
    private static long _viewSpace;

    /// <summary>Predicted display time of the frame being rendered (from the latest xrLocateViews call).</summary>
    public static long DisplayTime => Volatile.Read(ref _displayTime);

    /// <summary>The XrSpace Unity locates the eye views in; controller poses are located in it too.</summary>
    public static ulong ViewSpace => (ulong)Volatile.Read(ref _viewSpace);

    /// <summary>Incremented every time OpenXR is (re)loaded. Anything cached per instance must check it.</summary>
    public static int Generation { get; private set; }

    /// <summary>The XrInstance handle Unity created (0 until known).</summary>
    public static ulong Instance { get; private set; }

    /// <summary>The XrSession handle Unity created (0 until known).</summary>
    public static ulong Session { get; private set; }

    /// <summary>Resolves an OpenXR function for the current instance, bypassing the hooks.</summary>
    public static IntPtr Resolve(string name)
    {
        if (_nextGetInstanceProcAddr == null || Instance == 0)
            return IntPtr.Zero;

        var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        IntPtr function = IntPtr.Zero;
        fixed (byte* namePointer = bytes)
        {
            return _nextGetInstanceProcAddr(Instance, namePointer, &function) == XrSuccess ? function : IntPtr.Zero;
        }
    }
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
        // Each OpenXR load is a new generation: handles may repeat numerically across reloads, but every
        // function pointer resolved for the previous generation points into an unloaded library.
        Generation++;
        Instance = 0;
        Session = 0;
        _viewSpace = 0;
        _displayTime = 0;
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
            else if (result == XrSuccess && function != null && *function != IntPtr.Zero && IsName(name, "xrCreateSession"))
            {
                _originalCreateSession = (delegate* unmanaged<ulong, void*, ulong*, int>)*function;
                *function = CreateSessionHookPointer;
            }

            if (instance != 0)
                Instance = instance;
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

        // XrViewLocateInfo: type, next, viewConfigurationType, displayTime (offset 24), space (offset 32).
        if (locateInfo != null)
        {
            Volatile.Write(ref _displayTime, *(long*)((byte*)locateInfo + 24));
            Volatile.Write(ref _viewSpace, *(long*)((byte*)locateInfo + 32));
        }

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

    private static int CreateSession(ulong instance, void* createInfo, ulong* session)
    {
        var result = _originalCreateSession(instance, createInfo, session);
        if (result == XrSuccess && session != null)
        {
            Instance = instance;
            Session = *session;
        }
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
