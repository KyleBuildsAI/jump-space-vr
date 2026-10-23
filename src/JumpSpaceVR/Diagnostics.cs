
using System.Text;
using JumpSpaceVR.OpenXR;
using UnityEngine;
using UnityEngine.XR;

namespace JumpSpaceVR;

/// <summary>Dumps the state needed to debug VR issues into the MelonLoader log.</summary>
internal static partial class Diagnostics
{
    private const int FrameTimeSamples = 240;
    private static readonly float[] FrameTimes = new float[FrameTimeSamples];
    private static int _frameTimeIndex;
    private static int _frameTimeCount;

    /// <summary>Records the last frame's duration; call once per frame.</summary>
    public static void RecordFrame()
    {
        FrameTimes[_frameTimeIndex] = Time.unscaledDeltaTime;
        _frameTimeIndex = (_frameTimeIndex + 1) % FrameTimeSamples;
        _frameTimeCount = Mathf.Min(_frameTimeCount + 1, FrameTimeSamples);
    }

    private static string DescribeFrameTimes()
    {
        if (_frameTimeCount == 0)
            return "no samples";
        float total = 0f, worst = 0f;
        for (var index = 0; index < _frameTimeCount; index++)
        {
            total += FrameTimes[index];
            worst = Mathf.Max(worst, FrameTimes[index]);
        }
        var average = total / _frameTimeCount;
        return $"avg {average * 1000f:F1} ms ({1f / average:F0} fps), worst {worst * 1000f:F1} ms over {_frameTimeCount} frames";
    }

    public static System.Func<string> DescribeMotion;

    public static void Dump(OpenXRRuntime runtime)
    {
        var report = new StringBuilder();
        report.AppendLine("==== JumpSpaceVR diagnostics ====");
        report.AppendLine($"Screen: {Screen.width}x{Screen.height}, Graphics: {SystemInfo.graphicsDeviceType}");
        report.AppendLine($"Frame time: {DescribeFrameTimes()}");
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

        Section(report, "Controllers", () =>
        {
            var state = runtime.Controllers.Read();
            return $"ready={runtime.Controllers.IsReady}, active={state.IsActive}, sticks={state.LeftStick}/{state.RightStick}, " +
                   $"triggers={state.LeftTrigger:F2}/{state.RightTrigger:F2}";
        });

        Section(report, "Virtual gamepad", () =>
        {
            foreach (var device in UnityEngine.InputSystem.InputSystem.devices)
            {
                if (device.name != "JumpSpaceVR Controllers")
                    continue;
                var gamepad = device.TryCast<UnityEngine.InputSystem.Gamepad>();
                return gamepad == null ? "present (not a gamepad?)" :
                    $"leftStick={gamepad.leftStick.ReadValue()}, rightTrigger={gamepad.rightTrigger.ReadValue():F2}, " +
                    $"south={gamepad.buttonSouth.isPressed}, current={UnityEngine.InputSystem.Gamepad.current?.name}";
            }
            return "not created";
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

        Section(report, "Look state", DescribeLookState);
        if (DescribeMotion != null)
            Section(report, "Motion", DescribeMotion);
        Section(report, "First-person layer", JumpSpaceVR.Rendering.FirstPersonRenderFix.DescribeFirstPersonObjects);

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
