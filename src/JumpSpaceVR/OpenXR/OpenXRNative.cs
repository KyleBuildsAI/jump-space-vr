using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace JumpSpaceVR.OpenXR;

/// <summary>
/// Session lifecycle events raised by the UnityOpenXR native plugin.
/// Order mirrors OpenXRFeature.NativeEvent in com.unity.xr.openxr 1.18.
/// </summary>
internal enum NativeEvent
{
    XrSetupConfigValues,
    XrSystemIdChanged,
    XrInstanceChanged,
    XrSessionChanged,
    XrBeginSession,
    XrSessionStateChanged,
    XrChangedSpaceApp,
    XrEndSession,
    XrDestroySession,
    XrDestroyInstance,
    XrIdle,
    XrReady,
    XrSynchronized,
    XrVisible,
    XrFocused,
    XrStopping,
    XrExiting,
    XrLossPending,
    XrInstanceLossPending,
    XrRestartRequested,
    XrRequestRestartLoop,
    XrRequestGetSystemLoop,
    XrExtensionsReady,
}

internal enum StereoRenderMode
{
    MultiPass = 0,
    SinglePassInstanced = 1,
}

/// <summary>
/// P/Invoke surface of UnityOpenXR.dll. This replaces the managed half of the Unity OpenXR package,
/// which is not compiled into the game.
/// </summary>
internal static class OpenXRNative
{
    private const string LibraryName = "UnityOpenXR";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ReceiveNativeEventDelegate(NativeEvent nativeEvent, ulong payload);

    private static bool _resolverInstalled;

    /// <summary>
    /// Points DllImport("UnityOpenXR") at the copy Unity loaded from the game's Plugins folder.
    /// </summary>
    public static void InstallResolver(string pluginsDirectory)
    {
        if (_resolverInstalled)
            return;

        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, assembly, searchPath) =>
        {
            if (name != LibraryName)
                return IntPtr.Zero;

            var fullPath = Path.Combine(pluginsDirectory, LibraryName + ".dll");
            return NativeLibrary.TryLoad(fullPath, out var handle) ? handle : IntPtr.Zero;
        });
        _resolverInstalled = true;
    }

    [DllImport(LibraryName, EntryPoint = "main_LoadOpenXRLibrary")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool LoadOpenXRLibrary(byte[] loaderPath);

    [DllImport(LibraryName, EntryPoint = "main_UnloadOpenXRLibrary")]
    public static extern void UnloadOpenXRLibrary();

    [DllImport(LibraryName, EntryPoint = "NativeConfig_GetProcAddressPtr")]
    public static extern IntPtr GetProcAddressPtr([MarshalAs(UnmanagedType.I1)] bool loaderDefault);

    /// <summary>Installs xrGetInstanceProcAddr and loads the pre-instance OpenXR entry points.</summary>
    [DllImport(LibraryName, EntryPoint = "NativeConfig_SetProcAddressPtrAndLoadStage1")]
    public static extern void SetProcAddressPtrAndLoadStage1(IntPtr getInstanceProcAddr);

    [DllImport(LibraryName, EntryPoint = "NativeConfig_SetCallbacks")]
    public static extern void SetCallbacks(ReceiveNativeEventDelegate callback);

    [DllImport(LibraryName, EntryPoint = "NativeConfig_SetApplicationInfo", CharSet = CharSet.Ansi)]
    public static extern void SetApplicationInfo(string applicationName, string applicationVersion, uint applicationVersionHash, string engineVersion);

    [DllImport(LibraryName, EntryPoint = "NativeConfig_SetRenderMode")]
    public static extern void SetRenderMode(StereoRenderMode renderMode);

    [DllImport(LibraryName, EntryPoint = "session_SetSuccessfullyInitialized")]
    public static extern void SetSuccessfullyInitialized([MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(LibraryName, EntryPoint = "session_InitializeSession")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool InitializeSession();

    [DllImport(LibraryName, EntryPoint = "session_CreateSessionIfNeeded")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CreateSessionIfNeeded();

    [DllImport(LibraryName, EntryPoint = "session_BeginSession")]
    public static extern void BeginSession();

    [DllImport(LibraryName, EntryPoint = "session_EndSession")]
    public static extern void EndSession();

    [DllImport(LibraryName, EntryPoint = "session_RequestExitSession")]
    public static extern void RequestExitSession();

    [DllImport(LibraryName, EntryPoint = "session_DestroySession")]
    public static extern void DestroySession();

    [DllImport(LibraryName, EntryPoint = "messagepump_PumpMessageLoop")]
    public static extern void PumpMessageLoop();

    [DllImport(LibraryName, EntryPoint = "unity_ext_RequestEnableExtensionString", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool RequestEnableExtensionString(string extensionString);

    /// <summary>The native loader expects a null-terminated UTF-16 path on Windows.</summary>
    public static byte[] ToWideString(string value) => System.Text.Encoding.Unicode.GetBytes(value + '\0');
}
