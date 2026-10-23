using System;
using UnityEngine;
using UnityEngine.SubsystemsImplementation;
using UnityEngine.XR;

namespace JumpSpaceVR.OpenXR;

/// <summary>
/// Creates and owns the OpenXR display and input subsystems that UnityOpenXR registers at engine start.
///
/// The convenient managed APIs (SubsystemManager.GetAllSubsystemDescriptors, IntegratedSubsystemDescriptor&lt;T&gt;.Create)
/// were stripped from the game's IL2CPP build, so this goes through the descriptor store and the native
/// binding directly.
/// </summary>
internal sealed class XrSubsystems
{
    private const string DisplayId = "OpenXR Display";
    private const string InputId = "OpenXR Input";

    public XRDisplaySubsystem Display { get; private set; }
    public XRInputSubsystem Input { get; private set; }

    public bool DisplayRunning => Display != null && Display.running;
    public bool InputRunning => Input != null && Input.running;

    public void Create()
    {
        var displayDescriptor = FindDescriptor(DisplayId);
        var inputDescriptor = FindDescriptor(InputId);

        if (displayDescriptor == null || inputDescriptor == null)
        {
            throw new InvalidOperationException(
                "UnityOpenXR subsystem descriptors not found. Check that Jump Space_Data/UnitySubsystems/UnityOpenXR/UnitySubsystemsManifest.json " +
                "and Jump Space_Data/Plugins/x86_64/UnityOpenXR.dll are installed.");
        }

        Display ??= CreateSubsystem(displayDescriptor)?.TryCast<XRDisplaySubsystem>();
        Input ??= CreateSubsystem(inputDescriptor)?.TryCast<XRInputSubsystem>();

        if (Display == null || Input == null)
            throw new InvalidOperationException("Failed to create OpenXR subsystems.");
    }

    public void StartDisplay()
    {
        if (Display != null && !Display.running)
            Display.Start();
    }

    public void StartInput()
    {
        if (Input != null && !Input.running)
            Input.Start();
    }

    public void Stop()
    {
        if (InputRunning)
            Input.Stop();
        if (DisplayRunning)
            Display.Stop();
    }

    public void Destroy()
    {
        Input?.Destroy();
        Display?.Destroy();
        Input = null;
        Display = null;
    }

    private static IntegratedSubsystemDescriptor FindDescriptor(string id)
    {
        var descriptors = SubsystemDescriptorStore.s_IntegratedDescriptors;
        if (descriptors == null)
            return null;

        for (var index = 0; index < descriptors.Count; index++)
        {
            var descriptor = descriptors[index];
            if (descriptor != null && descriptor.id == id)
                return descriptor;
        }
        return null;
    }

    private static IntegratedSubsystem CreateSubsystem(IntegratedSubsystemDescriptor descriptor)
    {
        var nativePtr = SubsystemDescriptorBindings.Create(descriptor.m_Ptr);
        if (nativePtr == IntPtr.Zero)
            return null;

        var subsystem = FindIntegratedSubsystem(nativePtr);
        if (subsystem == null)
        {
            Log.Error($"'{descriptor.id}' was created natively but has no managed wrapper.");
            return null;
        }

        subsystem.m_SubsystemDescriptor = descriptor.Cast<ISubsystemDescriptor>();
        return subsystem;
    }

    private static IntegratedSubsystem FindIntegratedSubsystem(IntPtr nativePtr)
    {
        var subsystems = SubsystemManager.s_IntegratedSubsystems;
        if (subsystems == null)
            return null;

        for (var index = 0; index < subsystems.Count; index++)
        {
            var subsystem = subsystems[index];
            if (subsystem != null && subsystem.m_Ptr == nativePtr)
                return subsystem;
        }
        return null;
    }
}
