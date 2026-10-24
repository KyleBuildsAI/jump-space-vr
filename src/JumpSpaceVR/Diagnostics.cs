
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

    /// <summary>The last N timed samples of a value, for measuring how abruptly it changes speed.</summary>
    public sealed class RollingSamples
    {
        private readonly float[] _values;
        private readonly float[] _times;
        private int _next;
        private int _count;

        /// <summary>Per-frame jumps beyond this (in the sampled unit, meters for eye height) are teleports.</summary>
        public const float TeleportDistance = 0.5f;

        public RollingSamples(int size)
        {
            _values = new float[size];
            _times = new float[size];
        }

        public void Add(float value, float time)
        {
            _values[_next] = value;
            _times[_next] = time;
            _next = (_next + 1) % _values.Length;
            _count = Mathf.Min(_count + 1, _values.Length);
        }

        /// <summary>
        /// Largest change in speed between consecutive frames (units per second). Steady climbs, slopes and uneven
        /// frame times score low; bob, stair steps and shake score high. Jumps larger than
        /// <see cref="TeleportDistance"/> in one frame (level loads, respawns, camera rig changes) are skipped.
        /// </summary>
        public float MaxSpeedChange
        {
            get
            {
                var start = _count < _values.Length ? 0 : _next;
                var worst = 0f;
                float? previousSpeed = null;
                for (var index = 1; index < _count; index++)
                {
                    var current = (start + index) % _values.Length;
                    var previous = (start + index - 1) % _values.Length;
                    var elapsed = _times[current] - _times[previous];
                    if (elapsed <= 0f)
                        continue;
                    var step = _values[current] - _values[previous];
                    if (Mathf.Abs(step) > TeleportDistance)
                    {
                        previousSpeed = null;
                        continue;
                    }
                    var speed = step / elapsed;
                    if (previousSpeed.HasValue)
                        worst = Mathf.Max(worst, Mathf.Abs(speed - previousSpeed.Value));
                    previousSpeed = speed;
                }
                return worst;
            }
        }
    }

    private static float CanvasAlpha(Canvas canvas)
    {
        var group = canvas.GetComponent<CanvasGroup>();
        return group != null ? group.alpha : 1f;
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
                              $"camera={(canvas.worldCamera != null ? canvas.worldCamera.name : "-")} order={canvas.sortingOrder} layer={canvas.gameObject.layer} display={canvas.targetDisplay} alpha={CanvasAlpha(canvas):F2}");
        }

        // Wide, dark, opaque UI graphics: letterbox bars and fades that would cover the view on the VR panel.
        report.AppendLine("-- Large opaque UI graphics --");
        foreach (var graphic in Object.FindObjectsByType<UnityEngine.UI.Graphic>(FindObjectsSortMode.None))
        {
            if (!graphic.isActiveAndEnabled || graphic.color.a < 0.5f || graphic.canvas == null || graphic.canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                continue;
            var rect = graphic.rectTransform.rect;
            var scale = graphic.canvas.scaleFactor;
            if (rect.width * scale < Screen.width * 0.8f)
                continue;
            report.AppendLine($"  {PathOf(graphic.transform)} size={rect.width * scale:F0}x{rect.height * scale:F0} color={graphic.color} " +
                              $"canvasAlpha={CanvasAlpha(graphic.canvas.rootCanvas):F2}");
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
