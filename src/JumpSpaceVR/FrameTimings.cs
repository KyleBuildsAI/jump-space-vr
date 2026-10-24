using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace JumpSpaceVR;

/// <summary>
/// Where each frame's time goes (CPU main thread, CPU render thread, GPU), averaged over about a second, from Unity's
/// FrameTimingManager. A frame rate that doesn't improve when the render scale drops is CPU bound; this says which
/// thread. Unavailable (null) when the build has frame timing stats switched off.
/// </summary>
internal static class FrameTimings
{
    private const int Window = 90;

    private static readonly Il2CppStructArray<FrameTiming> Latest = new(1);
    private static readonly double[] Main = new double[Window];
    private static readonly double[] Render = new double[Window];
    private static readonly double[] Gpu = new double[Window];
    private static int _count;
    private static int _next;

    public static float? CpuMainMs => Average(Main);
    public static float? CpuRenderMs => Average(Render);
    public static float? GpuMs => Average(Gpu);

    /// <summary>Call once per frame.</summary>
    public static void Record()
    {
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, Latest) < 1)
            return;
        var timing = Latest[0];
        if (timing.cpuFrameTime <= 0d)
            return;
        Main[_next] = timing.cpuMainThreadFrameTime;
        Render[_next] = timing.cpuRenderThreadFrameTime;
        Gpu[_next] = timing.gpuFrameTime;
        _next = (_next + 1) % Window;
        if (_count < Window)
            _count++;
    }

    /// <summary>"cpu main 12.3 ms, render 20.1 ms, gpu 9.8 ms", or empty when unavailable.</summary>
    public static string Describe() =>
        CpuMainMs.HasValue
            ? $"cpu main {CpuMainMs:F1} ms, render thread {CpuRenderMs:F1} ms, gpu {GpuMs:F1} ms"
            : "";

    private static float? Average(double[] samples)
    {
        if (_count == 0)
            return null;
        double sum = 0;
        for (var index = 0; index < _count; index++)
            sum += samples[index];
        return (float)(sum / _count);
    }
}
