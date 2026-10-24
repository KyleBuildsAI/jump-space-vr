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
    /// <summary>A visor display inside the helmet: locked to the head, close, wide and curved.</summary>
    Helmet,
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
    private Matrix4x4 _panelFrame = Matrix4x4.identity;
    private Vector2 _panelSize = Vector2.one;
    // Curved panel: cylinder radius in meters around the viewer (0 = flat), and the mesh built for it.
    private float _curveRadius;
    private Mesh _curvedMesh;
    private float _curvedMeshRadius;
    private Vector2 _curvedMeshSize;
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
        if (_curveRadius > 0f)
            return TryHitCurvedPanel(origin, direction, out uv);

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

    /// <summary>
    /// Curved panel: a section of a vertical cylinder whose axis passes through the viewer (radius = panel distance),
    /// intersected in the panel's unscaled frame. The hit on the far side of the cylinder is the one in front.
    /// </summary>
    private bool TryHitCurvedPanel(Vector3 origin, Vector3 direction, out Vector2 uv)
    {
        uv = default;
        var inverse = _panelFrame.inverse;
        var localOrigin = inverse.MultiplyPoint3x4(origin);
        var localDirection = inverse.MultiplyVector(direction).normalized;
        var axisX = localOrigin.x;
        var axisZ = localOrigin.z + _curveRadius;
        var a = localDirection.x * localDirection.x + localDirection.z * localDirection.z;
        if (a < 1e-8f)
            return false;
        var b = 2f * (axisX * localDirection.x + axisZ * localDirection.z);
        var c = axisX * axisX + axisZ * axisZ - _curveRadius * _curveRadius;
        var discriminant = b * b - 4f * a * c;
        if (discriminant < 0f)
            return false;
        var distance = (-b + Mathf.Sqrt(discriminant)) / (2f * a);
        if (distance <= 0f)
            return false;
        var hit = localOrigin + localDirection * distance;
        var angle = Mathf.Atan2(hit.x, hit.z + _curveRadius);
        var u = angle * _curveRadius / _panelSize.x + 0.5f;
        var v = hit.y / _panelSize.y + 0.5f;
        if (u < 0f || u > 1f || v < 0f || v > 1f)
            return false;
        uv = new Vector2(u, v);
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
        UiFollowMode mode, float smoothingSeconds, float distance, float width, bool snapToHead, bool curved)
    {
        if (mode == UiFollowMode.Helmet)
        {
            // Visor display: always in front of the eyes, lagging a little so it feels mounted in the helmet.
            distance = HelmetDistance;
            width = HelmetWidth;
            smoothingSeconds = HelmetLagSeconds;
            curved = true;
        }

        var headYawOnly = Quaternion.Euler(0f, headRotation.eulerAngles.y, 0f);
        var target = mode is UiFollowMode.Smooth or UiFollowMode.Helmet ? headRotation : headYawOnly;

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
            case UiFollowMode.Helmet:
                _panelRotation = smoothingSeconds <= 0f
                    ? target
                    : Quaternion.Slerp(_panelRotation, target, Blend(smoothingSeconds, deltaTime));
                break;
        }

        // Follow head translation smoothly too, so leaning does not leave the panel behind.
        _panelHeadPosition = mode == UiFollowMode.Fixed
            ? _panelHeadPosition
            : Vector3.Lerp(_panelHeadPosition, headPosition, Blend(Mathf.Max(smoothingSeconds, 0.05f), deltaTime));

        var drop = mode == UiFollowMode.Helmet ? 0f : PanelDrop;
        var localCenter = _panelHeadPosition + _panelRotation * new Vector3(0f, -drop, distance);
        var aspect = Screen.width > 0 ? (float)Screen.height / Screen.width : 9f / 16f;
        _panelSize = new Vector2(width, width * aspect);
        _panelFrame = Matrix4x4.TRS(viewOrigin + viewRotation * localCenter, viewRotation * _panelRotation, Vector3.one);
        _panelMatrix = _panelFrame * Matrix4x4.Scale(new Vector3(_panelSize.x, _panelSize.y, 1f));
        _curveRadius = curved ? distance : 0f;
    }

    // Helmet display: about the closest distance that stays comfortable to focus on in a headset.
    private const float HelmetDistance = 0.9f;
    private const float HelmetWidth = 1.5f;
    private const float HelmetLagSeconds = 0.04f;

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
            if (_curveRadius > 0f)
                commands.DrawMesh(CurvedMesh(), _panelFrame, _panelMaterial, 0, 0);
            else
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
        if (_curveRadius > 0f)
        {
            // On the cylinder, just in front of the panel, facing the viewer.
            var angle = (uv.x - 0.5f) * _panelSize.x / _curveRadius;
            var radius = _curveRadius - 0.002f;
            var position = new Vector3(Mathf.Sin(angle) * radius, (uv.y - 0.5f) * _panelSize.y,
                Mathf.Cos(angle) * radius - _curveRadius);
            return _panelFrame * Matrix4x4.TRS(position, Quaternion.Euler(0f, angle * Mathf.Rad2Deg, 0f),
                new Vector3(CursorDiameter, CursorDiameter, 1f));
        }

        var scale = _panelMatrix.lossyScale;
        var local = Matrix4x4.TRS(new Vector3(uv.x - 0.5f, uv.y - 0.5f, -0.002f), Quaternion.identity,
            new Vector3(CursorDiameter / Mathf.Max(scale.x, 0.01f), CursorDiameter / Mathf.Max(scale.y, 0.01f), 1f));
        return _panelMatrix * local;
    }

    /// <summary>The panel bent onto a cylinder around the viewer, in the panel's unscaled frame (meters).</summary>
    private Mesh CurvedMesh()
    {
        if (_curvedMesh != null && Mathf.Approximately(_curvedMeshRadius, _curveRadius) && _curvedMeshSize == _panelSize)
            return _curvedMesh;

        const int Columns = 32;
        var vertices = new Vector3[(Columns + 1) * 2];
        var uvs = new Vector2[vertices.Length];
        var colors = new Color[vertices.Length];
        var triangles = new int[Columns * 6];
        for (var column = 0; column <= Columns; column++)
        {
            var u = (float)column / Columns;
            var angle = (u - 0.5f) * _panelSize.x / _curveRadius;
            var x = Mathf.Sin(angle) * _curveRadius;
            var z = Mathf.Cos(angle) * _curveRadius - _curveRadius;
            vertices[column * 2] = new Vector3(x, -_panelSize.y / 2f, z);
            vertices[column * 2 + 1] = new Vector3(x, _panelSize.y / 2f, z);
            uvs[column * 2] = new Vector2(u, 0f);
            uvs[column * 2 + 1] = new Vector2(u, 1f);
            colors[column * 2] = Color.white;
            colors[column * 2 + 1] = Color.white;
            if (column == Columns)
                continue;
            var index = column * 6;
            var bottom = column * 2;
            triangles[index] = bottom;
            triangles[index + 1] = bottom + 1;
            triangles[index + 2] = bottom + 2;
            triangles[index + 3] = bottom + 1;
            triangles[index + 4] = bottom + 3;
            triangles[index + 5] = bottom + 2;
        }

        _curvedMesh ??= new Mesh { name = "JumpSpaceVR_UiPanelCurved", hideFlags = HideFlags.DontUnloadUnusedAsset };
        _curvedMesh.Clear();
        _curvedMesh.vertices = new Il2CppStructArray<Vector3>(vertices);
        _curvedMesh.uv = new Il2CppStructArray<Vector2>(uvs);
        _curvedMesh.colors = new Il2CppStructArray<Color>(colors);
        _curvedMesh.triangles = new Il2CppStructArray<int>(triangles);
        _curvedMesh.RecalculateBounds();
        _curvedMeshRadius = _curveRadius;
        _curvedMeshSize = _panelSize;
        return _curvedMesh;
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
