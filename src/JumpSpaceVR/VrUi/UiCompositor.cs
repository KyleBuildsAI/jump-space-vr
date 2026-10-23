using System.Collections;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace JumpSpaceVR.VrUi;

/// <summary>How the UI panel follows the player's head.</summary>
internal enum UiFollowMode
{
    /// <summary>Stays in front of the body; only moves on recenter.</summary>
    Fixed,
    /// <summary>Stays put while you glance around, eases back in front when you look well away.</summary>
    Lazy,
    /// <summary>Follows your face (yaw and pitch) with adjustable smoothing; zero smoothing is head-locked.</summary>
    Smooth,
}

/// <summary>
/// Brings the game's flat UI (menus, HUD, dialogs) into the headset and keeps a desktop mirror.
///
/// After HDRP renders the eyes, the game's screen-space overlay UI is drawn a second time into a transparent
/// texture through the render pipeline's UI overlay renderer list. That texture is shown on a panel floating
/// in front of the player in both eyes. The UI itself never leaves screen space, so mouse, keyboard and
/// gamepad navigation keep working unchanged. At end of frame the desktop window is rebuilt from the left
/// eye plus the UI.
/// </summary>
internal sealed class UiCompositor
{
    private const int GuiZTestAlways = 8;
    private static readonly string[] PanelShaderCandidates = { "UI/Default", "Sprites/Default", "Unlit/Transparent" };

    private RenderTexture _uiTexture;
    private Material _panelMaterial;
    private Mesh _panelMesh;
    private CommandBuffer _uiCommands;
    private Matrix4x4 _panelMatrix = Matrix4x4.identity;
    private Material _cursorMaterial;
    private const float CursorDiameter = 0.018f;

    /// <summary>Where the laser pointer touches the panel (0..1 UV, origin bottom-left), or null.</summary>
    public Vector2? CursorUv { get; set; }

    /// <summary>
    /// Intersects a ray (world space) with the UI panel. Returns the hit as panel UV (0..1, origin bottom-left),
    /// which maps directly to screen pixels of the game's UI.
    /// </summary>
    public bool TryHitPanel(Vector3 origin, Vector3 direction, out Vector2 uv)
    {
        uv = default;
        if (!_running || !PanelEnabled)
            return false;

        var inverse = _panelMatrix.inverse;
        var localOrigin = inverse.MultiplyPoint3x4(origin);
        var localDirection = inverse.MultiplyVector(direction);
        if (Mathf.Abs(localDirection.z) < 1e-5f)
            return false;
        var distance = -localOrigin.z / localDirection.z;
        if (distance <= 0f)
            return false;
        var hit = localOrigin + localDirection * distance;
        if (Mathf.Abs(hit.x) > 0.5f || Mathf.Abs(hit.y) > 0.5f)
            return false;
        uv = new Vector2(hit.x + 0.5f, hit.y + 0.5f);
        return true;
    }
    private bool _hasUiFrame;
    private bool _running;
    private object _coroutine;

    public bool PanelEnabled { get; set; } = true;
    public RenderTexture UiTexture => _uiTexture;
    public RenderTexture MirrorSource { get; set; }

    private const float LazyStartAngle = 35f;
    private const float LazyStopAngle = 1.5f;
    private const float LazyEaseSeconds = 0.25f;
    private const float PanelDrop = 0.1f;

    private Quaternion _panelRotation = Quaternion.identity;
    private Vector3 _panelHeadPosition;
    private bool _lazyFollowing;
    private bool _hasPanelPose;

    /// <summary>
    /// Places the panel. Head pose is given in the view frame (the levelled game camera), so in every mode the
    /// panel travels with the player's body; the mode decides how it follows the head.
    /// </summary>
    public void PlacePanel(Vector3 viewOrigin, Quaternion viewRotation, Vector3 headPosition, Quaternion headRotation,
        UiFollowMode mode, float smoothingSeconds, float distance, float width, bool snapToHead)
    {
        var headYawOnly = Quaternion.Euler(0f, headRotation.eulerAngles.y, 0f);
        var target = mode == UiFollowMode.Smooth ? headRotation : headYawOnly;

        if (!_hasPanelPose || snapToHead)
        {
            _panelRotation = target;
            _panelHeadPosition = headPosition;
            _hasPanelPose = true;
            _lazyFollowing = false;
        }

        var deltaTime = Time.unscaledDeltaTime;
        switch (mode)
        {
            case UiFollowMode.Fixed:
                break;

            case UiFollowMode.Lazy:
                if (Quaternion.Angle(_panelRotation, target) > LazyStartAngle)
                    _lazyFollowing = true;
                if (_lazyFollowing)
                {
                    _panelRotation = Quaternion.Slerp(_panelRotation, target, Blend(LazyEaseSeconds, deltaTime));
                    if (Quaternion.Angle(_panelRotation, target) < LazyStopAngle)
                        _lazyFollowing = false;
                }
                break;

            case UiFollowMode.Smooth:
                _panelRotation = smoothingSeconds <= 0f
                    ? target
                    : Quaternion.Slerp(_panelRotation, target, Blend(smoothingSeconds, deltaTime));
                break;
        }

        // Follow head translation smoothly too, so leaning does not leave the panel behind.
        _panelHeadPosition = mode == UiFollowMode.Fixed
            ? _panelHeadPosition
            : Vector3.Lerp(_panelHeadPosition, headPosition, Blend(Mathf.Max(smoothingSeconds, 0.05f), deltaTime));

        var localCenter = _panelHeadPosition + _panelRotation * new Vector3(0f, -PanelDrop, distance);
        var aspect = Screen.width > 0 ? (float)Screen.height / Screen.width : 9f / 16f;
        _panelMatrix = Matrix4x4.TRS(viewOrigin + viewRotation * localCenter, viewRotation * _panelRotation,
            new Vector3(width, width * aspect, 1f));
    }

    /// <summary>Frame-rate independent exponential smoothing factor.</summary>
    private static float Blend(float seconds, float deltaTime) => 1f - Mathf.Exp(-deltaTime / Mathf.Max(seconds, 0.0001f));

    public void Start()
    {
        if (_running || !EnsureResources())
            return;
        _running = true;
        _coroutine = MelonCoroutines.Start(EndOfFrameLoop());
    }

    public void Stop()
    {
        _running = false;
        if (_coroutine != null)
        {
            MelonCoroutines.Stop(_coroutine);
            _coroutine = null;
        }
        _hasUiFrame = false;
    }

    /// <summary>Draws the game's overlay UI into the transparent UI texture. Call from inside the render pipeline.</summary>
    public void RenderUi(ScriptableRenderContext renderContext, Camera camera)
    {
        if (!_running || camera == null || !EnsureResources())
            return;

        if (_uiTexture.width != Screen.width || _uiTexture.height != Screen.height)
            RecreateUiTexture();

        _uiCommands.Clear();
        _uiCommands.SetRenderTarget(new RenderTargetIdentifier(_uiTexture));
        _uiCommands.ClearRenderTarget(true, true, Color.clear);
        _uiCommands.DrawRendererList(renderContext.CreateUIOverlayRendererList(camera));
        renderContext.ExecuteCommandBuffer(_uiCommands);
        renderContext.Submit();
        _hasUiFrame = true;
    }

    /// <summary>Draws the UI panel into each eye's swapchain image.</summary>
    public void CompositeIntoEyes(CommandBuffer commands, RenderTargetIdentifier[] eyeTargets, Camera[] eyeCameras)
    {
        if (!_running || !PanelEnabled || !_hasUiFrame || !EnsureResources())
            return;

        for (var eye = 0; eye < eyeTargets.Length; eye++)
        {
            var camera = eyeCameras[eye];
            if (camera == null)
                continue;
            commands.SetRenderTarget(eyeTargets[eye]);
            commands.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
            commands.DrawMesh(_panelMesh, _panelMatrix, _panelMaterial, 0, 0);
            if (CursorUv.HasValue && _cursorMaterial != null)
                commands.DrawMesh(_panelMesh, CursorMatrix(CursorUv.Value), _cursorMaterial, 0, 0);
        }
    }

    private IEnumerator EndOfFrameLoop()
    {
        while (_running)
        {
            yield return new WaitForEndOfFrame();
            if (_running)
                DrawDesktopMirror();
        }
    }

    /// <summary>Desktop view: centre crop of the left eye with the UI on top.</summary>
    private void DrawDesktopMirror()
    {
        var leftEye = MirrorSource;
        if (leftEye == null || !EnsureResources())
            return;

        var screenAspect = (float)Screen.width / Mathf.Max(1, Screen.height);
        var eyeAspect = (float)leftEye.width / Mathf.Max(1, leftEye.height);
        var visibleHeight = Mathf.Clamp01(eyeAspect / screenAspect);
        Graphics.Blit(leftEye, (RenderTexture)null, new Vector2(1f, visibleHeight), new Vector2(0f, (1f - visibleHeight) * 0.5f));
        if (_hasUiFrame)
            Graphics.Blit(_uiTexture, (RenderTexture)null, _panelMaterial);
    }

    private bool EnsureResources()
    {
        if (_panelMaterial == null)
        {
            Shader shader = null;
            foreach (var name in PanelShaderCandidates)
            {
                shader = Shader.Find(name);
                if (shader != null)
                    break;
            }
            if (shader == null)
            {
                Log.Error("No UI shader available; VR UI panel disabled.");
                return false;
            }

            _panelMaterial = new Material(shader) { name = "JumpSpaceVR_UiPanel" };
            _panelMaterial.SetInt("unity_GUIZTestMode", GuiZTestAlways);
            _panelMaterial.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _cursorMaterial = new Material(_panelMaterial)
            {
                name = "JumpSpaceVR_Cursor",
                hideFlags = HideFlags.DontUnloadUnusedAsset,
                mainTexture = CreateCursorTexture(),
            };
            Log.Msg($"VR UI panel uses shader '{shader.name}'.");
        }

        if (_panelMesh == null)
            _panelMesh = CreatePanelMesh();
        _uiCommands ??= new CommandBuffer { name = "JumpSpaceVR UI" };
        if (_uiTexture == null)
            RecreateUiTexture();
        return true;
    }

    private void RecreateUiTexture()
    {
        if (_uiTexture != null)
        {
            _uiTexture.Release();
            Object.Destroy(_uiTexture);
        }
        _uiTexture = new RenderTexture(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height), 24, RenderTextureFormat.ARGB32)
        {
            name = "JumpSpaceVR_Ui",
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        _uiTexture.Create();
        _panelMaterial.mainTexture = _uiTexture;
        _hasUiFrame = false;
    }

    private Matrix4x4 CursorMatrix(Vector2 uv)
    {
        var scale = _panelMatrix.lossyScale;
        var local = Matrix4x4.TRS(new Vector3(uv.x - 0.5f, uv.y - 0.5f, -0.002f), Quaternion.identity,
            new Vector3(CursorDiameter / Mathf.Max(scale.x, 0.01f), CursorDiameter / Mathf.Max(scale.y, 0.01f), 1f));
        return _panelMatrix * local;
    }

    /// <summary>A soft white dot with a dark outline, readable on any background.</summary>
    private static Texture2D CreateCursorTexture()
    {
        const int size = 32;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "JumpSpaceVR_CursorDot",
            hideFlags = HideFlags.DontUnloadUnusedAsset,
            wrapMode = TextureWrapMode.Clamp,
        };
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var distance = new Vector2(x + 0.5f - size / 2f, y + 0.5f - size / 2f).magnitude / (size / 2f);
                var color = distance < 0.7f ? Color.white
                    : distance < 0.95f ? new Color(0.05f, 0.08f, 0.1f, 1f)
                    : Color.clear;
                texture.SetPixel(x, y, color);
            }
        }
        texture.Apply(false, false);
        return texture;
    }

    /// <summary>Unit quad centred on the origin, facing the camera that looks along +Z.</summary>
    private static Mesh CreatePanelMesh()
    {
        var mesh = new Mesh { name = "JumpSpaceVR_UiPanel", hideFlags = HideFlags.DontUnloadUnusedAsset };
        mesh.vertices = new Il2CppStructArray<Vector3>(new[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
        });
        mesh.uv = new Il2CppStructArray<Vector2>(new[]
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
        });
        mesh.colors = new Il2CppStructArray<Color>(new[] { Color.white, Color.white, Color.white, Color.white });
        mesh.triangles = new Il2CppStructArray<int>(new[] { 0, 2, 1, 2, 3, 1 });
        mesh.RecalculateBounds();
        return mesh;
    }
}
