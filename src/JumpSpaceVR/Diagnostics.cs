
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

    /// <summary>The last N samples of a value, for measuring how much it wobbles.</summary>
    public sealed class RollingRange
    {
        private readonly float[] _samples;
        private int _next;
        private int _count;

        public RollingRange(int size) => _samples = new float[size];

        public void Add(float value)
        {
            _samples[_next] = value;
            _next = (_next + 1) % _samples.Length;
            _count = Mathf.Min(_count + 1, _samples.Length);
        }

        public float Range
        {
            get
            {
                if (_count == 0)
                    return 0f;
                float min = float.MaxValue, max = float.MinValue;
                for (var index = 0; index < _count; index++)
                {
                    min = Mathf.Min(min, _samples[index]);
                    max = Mathf.Max(max, _samples[index]);
                }
                return max - min;
            }
        }

        /// <summary>
        /// Largest change in per-frame movement (second difference) over the window: how sharply the value jolts.
        /// Steady climbs and slopes score near zero; bob, stair steps and shake score high.
        /// </summary>
        public float MaxSecondDifference
        {
            get
            {
                var start = _count < _samples.Length ? 0 : _next;
                var worst = 0f;
                for (var index = 2; index < _count; index++)
                {
                    var a = _samples[(start + index - 2) % _samples.Length];
                    var b = _samples[(start + index - 1) % _samples.Length];
                    var c = _samples[(start + index) % _samples.Length];
                    worst = Mathf.Max(worst, Mathf.Abs(c - 2f * b + a));
                }
                return worst;
            }
        }
    }

    /// <summary>Average frames per second over the last few seconds, or null before any frame was recorded.</summary>
    public static float? AverageFps
    {
        get
        {
            if (_frameTimeCount == 0)
                return null;
            var (average, _) = FrameTimeStats();
            return 1f / average;
        }
    }

    /// <summary>Worst single frame time (milliseconds) over the last few seconds.</summary>
    public static float? WorstFrameMs => _frameTimeCount == 0 ? null : FrameTimeStats().Worst * 1000f;

    private static (float Average, float Worst) FrameTimeStats()
    {
        float total = 0f, worst = 0f;
        for (var index = 0; index < _frameTimeCount; index++)
        {
            total += FrameTimes[index];
            worst = Mathf.Max(worst, FrameTimes[index]);
        }
        return (total / _frameTimeCount, worst);
    }

    private static string DescribeFrameTimes()
    {
        if (_frameTimeCount == 0)
            return "no samples";
        var (average, worst) = FrameTimeStats();
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

        Section(report, "Pointer", () =>
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            var module = eventSystem != null ? eventSystem.currentInputModule : null;
            var hovered = eventSystem != null && eventSystem.IsPointerOverGameObject();
            return $"cursor={Cursor.lockState}/{(Cursor.visible ? "visible" : "hidden")}, mouse={mouse?.name}@{mouse?.position.ReadValue()} " +
                   $"left={mouse?.leftButton.isPressed}, eventSystem={eventSystem?.name}, module={module?.GetIl2CppType().Name}, overUi={hovered}, " +
                   $"focused={Application.isFocused}, background={UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior}, " +
                   $"updates={UnityEngine.InputSystem.InputSystem.settings.updateMode}, selected={eventSystem?.currentSelectedGameObject?.name}, " +
                   $"inputTime={UnityEngine.InputSystem.LowLevel.InputState.currentTime:F3} offset={UnityEngine.InputSystem.LowLevel.InputRuntime.s_CurrentTimeOffsetToRealtimeSinceStartup:F3} " +
                   $"realtime={Time.realtimeSinceStartupAsDouble:F3} mouseUpdatedInternal={mouse?.m_LastUpdateTimeInternal:F3}";
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
