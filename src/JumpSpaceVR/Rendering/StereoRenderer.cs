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
    public Camera LeftEyeCamera { get; set; }
    public Camera RightEyeCamera { get; set; }
    public VrUi.UiCompositor Ui { get; set; }

    private RenderTexture[] _captureTextures;
    private System.Action<RenderTexture, RenderTexture> _captureCallback;

    /// <summary>Copies the final swapchain images (eye render + UI panel) on the next submitted frame.</summary>
    public void CaptureSubmittedFrame(System.Action<RenderTexture, RenderTexture> onCaptured)
    {
        if (!IsReady)
            return;
        _captureTextures ??= new RenderTexture[2];
        for (var eye = 0; eye < 2; eye++)
        {
            if (_captureTextures[eye] == null || _captureTextures[eye].width != _eyeTextures[eye].width || _captureTextures[eye].height != _eyeTextures[eye].height)
                _captureTextures[eye] = new RenderTexture(_eyeTextures[eye].descriptor) { name = "JumpSpaceVR_SubmittedEye", hideFlags = HideFlags.DontUnloadUnusedAsset };
        }
        _captureCallback = onCaptured;
    }

    private readonly RenderTargetIdentifier[] _swapchainTargets = new RenderTargetIdentifier[2];
    private readonly Camera[] _eyeCameras = new Camera[2];
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
        var texture = new RenderTexture(descriptor)
        {
            name = eye == 0 ? "JumpSpaceVR_LeftEye" : "JumpSpaceVR_RightEye",
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
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

    private static void RenderPostfix(ScriptableRenderContext renderContext, Il2CppSystem.Collections.Generic.List<Camera> cameras)
    {
        var renderer = _active;
        if (renderer == null || renderer.RightEyeCamera == null || cameras == null)
            return;

        try
        {
            if (cameras.Contains(renderer.RightEyeCamera))
                renderer.SubmitEyes(renderContext);
        }
        catch (Exception exception)
        {
            Log.Error($"Eye submission failed: {exception}");
            _active = null;
        }
    }

    private void SubmitEyes(ScriptableRenderContext renderContext)
    {
        var frame = Time.frameCount;
        if (frame == _lastSubmittedFrame || !IsReady || _display == null || !_display.running)
            return;
        _lastSubmittedFrame = frame;

        Ui?.RenderUi(renderContext, LeftEyeCamera);

        _copyCommands.Clear();
        for (var eye = 0; eye < 2; eye++)
        {
            if (!XrDisplayNative.TryGetRenderPass(_display, _passForEye[eye], out var renderPass))
                return;

            var destination = renderPass.renderTarget;
            var slice = Math.Max(destination.m_DepthSlice, 0);
            _copyCommands.CopyTexture(new RenderTargetIdentifier(_eyeTextures[eye]), 0, destination, slice);
            _swapchainTargets[eye] = destination;
        }

        _eyeCameras[0] = LeftEyeCamera;
        _eyeCameras[1] = RightEyeCamera;
        Ui?.CompositeIntoEyes(_copyCommands, _swapchainTargets, _eyeCameras);

        var captureCallback = _captureCallback;
        if (captureCallback != null)
        {
            for (var eye = 0; eye < 2; eye++)
                _copyCommands.CopyTexture(_swapchainTargets[eye], Math.Max(_swapchainTargets[eye].m_DepthSlice, 0), new RenderTargetIdentifier(_captureTextures[eye]), 0);
        }

        Graphics.ExecuteCommandBuffer(_copyCommands);

        if (captureCallback != null)
        {
            _captureCallback = null;
            captureCallback(_captureTextures[0], _captureTextures[1]);
        }
    }
}
