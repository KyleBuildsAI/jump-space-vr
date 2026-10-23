using System.Text;
using JumpSpaceVR.OpenXR;
using UnityEngine;
using UnityEngine.XR;

namespace JumpSpaceVR;

/// <summary>Dumps the state needed to debug VR issues into the MelonLoader log.</summary>
internal static class Diagnostics
{
    public static void Dump(OpenXRRuntime runtime)
    {
        var report = new StringBuilder();
        report.AppendLine("==== JumpSpaceVR diagnostics ====");
        report.AppendLine($"Screen: {Screen.width}x{Screen.height}, Graphics: {SystemInfo.graphicsDeviceType}");
        report.AppendLine($"OpenXR initialized: {runtime.IsInitialized}, session state: {runtime.SessionState}, rendering: {runtime.IsRendering}");

        var display = runtime.Subsystems.Display;
        if (display != null && display.running)
        {
            report.AppendLine($"XR display: passes={display.GetRenderPassCount()}, eyeTexture={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}, " +
                              $"scale={XRSettings.eyeTextureResolutionScale}, stereoMode={XRSettings.stereoRenderingMode}");
        }

        var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        report.AppendLine($"Head device: valid={head.isValid}, name={(head.isValid ? head.name : "-")}");

        report.AppendLine("-- Cameras --");
        foreach (var camera in Camera.allCameras)
        {
            report.AppendLine($"  {PathOf(camera.transform)} enabled={camera.enabled} depth={camera.depth} " +
                              $"target={(camera.targetTexture != null ? camera.targetTexture.name : "screen")} " +
                              $"mask=0x{camera.cullingMask:X8} eye={camera.stereoTargetEye} fov={camera.fieldOfView:F1} near={camera.nearClipPlane}");
        }

        report.AppendLine("-- Canvases (root) --");
        foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
        {
            if (!canvas.isRootCanvas)
                continue;
            report.AppendLine($"  {PathOf(canvas.transform)} mode={canvas.renderMode} active={canvas.isActiveAndEnabled} " +
                              $"camera={(canvas.worldCamera != null ? canvas.worldCamera.name : "-")} order={canvas.sortingOrder} layer={canvas.gameObject.layer}");
        }

        Log.Msg(report.ToString());
    }

    private static string PathOf(Transform transform)
    {
        var path = transform.name;
        for (var parent = transform.parent; parent != null; parent = parent.parent)
            path = parent.name + "/" + path;
        return path;
    }
}
