using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.XR;

namespace JumpSpaceVR.Rendering;

/// <summary>
/// Owns the per-eye render targets and hands finished eye images to the XR compositor.
///
/// The game's HDRP was compiled without XR support, so HDRP renders the two eye cameras as ordinary
/// cameras into these textures. After HDRP finishes a frame, the textures are copied into the swapchain
/// images that Unity's OpenXR display subsystem submits.
/// </summary>
internal sealed class StereoRenderer
{
    private static StereoRenderer _active;

    private readonly RenderTexture[] _eyeTextures = new RenderTexture[2];
    private readonly int[] _passForEye = { -1, -1 };
    private CommandBuffer _copyCommands;
    private XRDisplaySubsystem _display;
    private int _lastSubmittedFrame = -1;
    private bool _layoutChecked;

    public RenderTexture LeftTexture => _eyeTextures[0];
    public RenderTexture RightTexture => _eyeTextures[1];
    public Camera RightEyeCamera { get; set; }
    public bool IsReady => _eyeTextures[0] != null && _eyeTextures[1] != null;

    public static bool InstallHooks(HarmonyLib.Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(HDRenderPipeline), "Render",
                new[] { typeof(ScriptableRenderContext), typeof(Il2CppSystem.Collections.Generic.List<Camera>) });
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(StereoRenderer), nameof(RenderPostfix)));
            return true;
        }
        catch (Exception exception)
        {
            Log.Error($"Could not hook HDRenderPipeline.Render: {exception.Message}");
            return false;
        }
    }

    public void Activate(XRDisplaySubsystem display)
    {
        _display = display;
        _copyCommands ??= new CommandBuffer { name = "JumpSpaceVR eye copy" };
        _active = this;
    }

    public void Deactivate()
    {
        if (_active == this)
            _active = null;
        ReleaseTextures();
    }

    /// <summary>Matches the eye textures to the compositor's swapchain images. Call once per frame before rendering.</summary>
    public bool PrepareFrame()
    {
        if (_display == null || !_display.running)
            return false;

        if (!_layoutChecked)
        {
            _layoutChecked = true;
            if (!XrDisplayNative.ValidateLayout())
            {
                _display = null;
                return false;
            }
        }

        var passCount = XrDisplayNative.GetRenderPassCount(_display);
        if (passCount < 2)
            return false;

        for (var eye = 0; eye < 2; eye++)
        {
            if (!XrDisplayNative.TryGetRenderPass(_display, eye, out var renderPass))
                return false;

            _passForEye[eye] = eye;
            EnsureTexture(eye, renderPass.renderTargetDesc);
        }
        return IsReady;
    }

    private void EnsureTexture(int eye, RenderTextureDescriptor swapchainDescriptor)
    {
        var existing = _eyeTextures[eye];
        if (existing != null && existing.width == swapchainDescriptor.width && existing.height == swapchainDescriptor.height
            && existing.graphicsFormat == swapchainDescriptor.graphicsFormat)
        {
            return;
        }

        if (existing != null)
        {
            existing.Release();
            UnityEngine.Object.Destroy(existing);
        }

        var descriptor = new RenderTextureDescriptor(swapchainDescriptor.width, swapchainDescriptor.height, swapchainDescriptor.graphicsFormat, 0)
        {
            dimension = TextureDimension.Tex2D,
            volumeDepth = 1,
            msaaSamples = 1,
            useMipMap = false,
        };
        var texture = new RenderTexture(descriptor) { name = eye == 0 ? "JumpSpaceVR_LeftEye" : "JumpSpaceVR_RightEye" };
        texture.Create();
        _eyeTextures[eye] = texture;
        Log.Msg($"Eye texture {eye}: {descriptor.width}x{descriptor.height} {descriptor.graphicsFormat}");
    }

    private void ReleaseTextures()
    {
        for (var eye = 0; eye < _eyeTextures.Length; eye++)
        {
            if (_eyeTextures[eye] == null)
                continue;
            _eyeTextures[eye].Release();
            UnityEngine.Object.Destroy(_eyeTextures[eye]);
            _eyeTextures[eye] = null;
        }
    }

    private static void RenderPostfix(Il2CppSystem.Collections.Generic.List<Camera> cameras)
    {
        var renderer = _active;
        if (renderer == null || renderer.RightEyeCamera == null || cameras == null)
            return;

        try
        {
            if (cameras.Contains(renderer.RightEyeCamera))
                renderer.SubmitEyes();
        }
        catch (Exception exception)
        {
            Log.Error($"Eye submission failed: {exception}");
            _active = null;
        }
    }

    private void SubmitEyes()
    {
        var frame = Time.frameCount;
        if (frame == _lastSubmittedFrame || !IsReady || _display == null || !_display.running)
            return;
        _lastSubmittedFrame = frame;

        _copyCommands.Clear();
        for (var eye = 0; eye < 2; eye++)
        {
            if (!XrDisplayNative.TryGetRenderPass(_display, _passForEye[eye], out var renderPass))
                return;

            var destination = renderPass.renderTarget;
            var slice = Math.Max(destination.m_DepthSlice, 0);
            _copyCommands.CopyTexture(new RenderTargetIdentifier(_eyeTextures[eye]), 0, destination, slice);
        }
        Graphics.ExecuteCommandBuffer(_copyCommands);
    }
}
