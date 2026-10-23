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

        Section(report, "XR settings", () =>
            $"deviceActive={XRSettings.isDeviceActive}, eyeTexture={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}, " +
            $"scale={XRSettings.eyeTextureResolutionScale}");
        Section(report, "HDRP XR", () => $"displayActive={UnityEngine.Experimental.Rendering.XRSystem.displayActive}");
        Section(report, "Head device", () =>
        {
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            return $"valid={head.isValid}, name={(head.isValid ? head.name : "-")}";
        });

        report.AppendLine("-- Cameras --");
        Section(report, "Main camera", () => Camera.main != null ? PathOf(Camera.main.transform) : "none");
        foreach (var camera in Camera.allCameras)
        {
            report.AppendLine($"  {PathOf(camera.transform)} enabled={camera.enabled} depth={camera.depth} " +
                              $"target={(camera.targetTexture != null ? camera.targetTexture.name : "screen")} " +
                              $"mask=0x{camera.cullingMask:X8} fov={camera.fieldOfView:F1} near={camera.nearClipPlane}");
        }

        foreach (var camera in Camera.allCameras)
            Section(report, $"Components on {camera.name}", () => DescribeComponents(camera.gameObject));

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

    private static void Section(StringBuilder report, string title, System.Func<string> describe)
    {
        try
        {
            report.AppendLine($"{title}: {describe()}");
        }
        catch (System.Exception exception)
        {
            report.AppendLine($"{title}: unavailable ({exception.GetType().Name})");
        }
    }

    private static string DescribeComponents(GameObject gameObject)
    {
        var names = new StringBuilder();
        foreach (var component in gameObject.GetComponents<Component>())
            names.Append(component.GetIl2CppType().Name).Append(", ");

        var hdData = gameObject.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
        if (hdData != null)
        {
            names.Append($"| volumeMask=0x{hdData.volumeLayerMask.value:X8} anchor={(hdData.volumeAnchorOverride != null ? hdData.volumeAnchorOverride.name : "-")} " +
                         $"aa={hdData.antialiasing} customFrameSettings={hdData.customRenderingSettings} dynRes={hdData.allowDynamicResolution}");
        }
        return names.ToString();
    }

    private static string PathOf(Transform transform)
    {
        var path = transform.name;
        for (var parent = transform.parent; parent != null; parent = parent.parent)
            path = parent.name + "/" + path;
        return path;
    }
}
