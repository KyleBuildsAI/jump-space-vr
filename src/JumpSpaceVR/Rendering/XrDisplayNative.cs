using System;
using Il2CppInterop.Runtime;
using UnityEngine.XR;

namespace JumpSpaceVR.Rendering;

/// <summary>
/// Direct icall access to XRDisplaySubsystem render passes. The managed wrappers were stripped from the game
/// build (their BindingsMarshaller is missing), so the native entry points are resolved by name.
/// </summary>
internal static unsafe class XrDisplayNative
{
    private static readonly delegate* unmanaged<IntPtr, int> GetRenderPassCountFn =
        (delegate* unmanaged<IntPtr, int>)Resolve("GetRenderPassCount_Injected");

    private static readonly delegate* unmanaged<IntPtr, int, XRDisplaySubsystem.XRRenderPass*, byte> TryGetRenderPassFn =
        (delegate* unmanaged<IntPtr, int, XRDisplaySubsystem.XRRenderPass*, byte>)Resolve("Internal_TryGetRenderPass_Injected");

    public static int GetRenderPassCount(XRDisplaySubsystem display) =>
        GetRenderPassCountFn == null ? 0 : GetRenderPassCountFn(display.m_Ptr);

    public static bool TryGetRenderPass(XRDisplaySubsystem display, int index, out XRDisplaySubsystem.XRRenderPass renderPass)
    {
        renderPass = default;
        if (TryGetRenderPassFn == null)
            return false;

        fixed (XRDisplaySubsystem.XRRenderPass* result = &renderPass)
            return TryGetRenderPassFn(display.m_Ptr, index, result) != 0;
    }

    /// <summary>Checks that the interop struct layout matches the engine's, which the icall writes into.</summary>
    public static bool ValidateLayout()
    {
        var classPointer = Il2CppClassPointerStore<XRDisplaySubsystem.XRRenderPass>.NativeClassPtr;
        uint alignment = 0;
        var nativeSize = IL2CPP.il2cpp_class_value_size(classPointer, ref alignment);
        var managedSize = sizeof(XRDisplaySubsystem.XRRenderPass);
        if (nativeSize != managedSize)
            Log.Error($"XRRenderPass layout mismatch (native {nativeSize} bytes, managed {managedSize} bytes).");
        return nativeSize == managedSize;
    }

    private static IntPtr Resolve(string method)
    {
        var pointer = IL2CPP.il2cpp_resolve_icall("UnityEngine.XR.XRDisplaySubsystem::" + method);
        if (pointer == IntPtr.Zero)
            Log.Error($"XR display binding not found: {method}");
        return pointer;
    }
}
