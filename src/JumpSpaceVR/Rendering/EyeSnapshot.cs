using System;
using System.IO;
using UnityEngine;

namespace JumpSpaceVR.Rendering;

/// <summary>
/// Saves the current eye images as BMP files (UserData/JumpSpaceVR) so rendering problems can be checked,
/// and reported, without a headset. BMP is written by hand because the image encoders were stripped.
/// </summary>
internal static class EyeSnapshot
{
    public static void Save(RenderTexture left, RenderTexture right) => Save(left, right, "eye");

    public static void SaveSingle(RenderTexture texture, string label)
    {
        if (texture == null)
            return;
        var directory = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "JumpSpaceVR");
        Directory.CreateDirectory(directory);
        Write(texture, Path.Combine(directory, $"{label}-{DateTime.Now:yyyyMMdd-HHmmss}.bmp"));
    }

    /// <summary>
    /// Saves a pair of eye images. Headset captures are read back from the compositor's swapchain images, which
    /// are stored top-down, so they are flipped to read upright.
    /// </summary>
    public static void Save(RenderTexture left, RenderTexture right, string label, bool flipVertical = false)
    {
        if (left == null || right == null)
        {
            Log.Warning("No eye textures to capture yet.");
            return;
        }

        var directory = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "JumpSpaceVR");
        Directory.CreateDirectory(directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Write(left, Path.Combine(directory, $"{label}-left-{stamp}.bmp"), flipVertical);
        Write(right, Path.Combine(directory, $"{label}-right-{stamp}.bmp"), flipVertical);
        Log.Msg($"Saved eye snapshots to {directory}");
    }

    private static void Write(RenderTexture source, string path, bool flipVertical = false)
    {
        var previous = RenderTexture.active;
        var readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        try
        {
            RenderTexture.active = source;
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            readable.Apply(false, false);
            var pixels = readable.GetPixels32();
            WriteBitmap(path, source.width, source.height, pixels, flipVertical);
            LogAlphaCoverage(Path.GetFileName(path), pixels);
        }
        finally
        {
            RenderTexture.active = previous;
            UnityEngine.Object.Destroy(readable);
        }
    }

    private static void LogAlphaCoverage(string name, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> pixels)
    {
        var transparent = 0;
        var step = Math.Max(1, pixels.Length / 20000);
        var samples = 0;
        for (var index = 0; index < pixels.Length; index += step, samples++)
        {
            if (pixels[index].a < 16)
                transparent++;
        }
        Log.Msg($"{name}: {100f * transparent / Math.Max(1, samples):F1}% transparent");
    }

    private static void WriteBitmap(string path, int width, int height, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> pixels,
        bool flipVertical)
    {
        const int headerSize = 54;
        var rowSize = width * 3;
        var padding = (4 - rowSize % 4) % 4;
        var imageSize = (rowSize + padding) * height;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(headerSize + imageSize);
        writer.Write(0);
        writer.Write(headerSize);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(imageSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        // Texture rows start at the bottom, which is also BMP's native order.
        for (var outputRow = 0; outputRow < height; outputRow++)
        {
            var row = flipVertical ? height - 1 - outputRow : outputRow;
            for (var column = 0; column < width; column++)
            {
                var pixel = pixels[row * width + column];
                writer.Write(pixel.b);
                writer.Write(pixel.g);
                writer.Write(pixel.r);
            }
            for (var pad = 0; pad < padding; pad++)
                writer.Write((byte)0);
        }
    }
}
