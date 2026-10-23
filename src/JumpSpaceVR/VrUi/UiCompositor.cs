using System.Collections;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace JumpSpaceVR.VrUi;

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
    private bool _hasUiFrame;
    private bool _running;
    private object _coroutine;

    public bool PanelEnabled { get; set; } = true;
    public RenderTexture UiTexture => _uiTexture;
    public RenderTexture MirrorSource { get; set; }

    private const float FollowStartAngle = 40f;
    private const float FollowStopAngle = 2f;
    private const float FollowSpeedDegrees = 220f;
    private const float PanelDrop = 0.1f;

    private float _panelYaw;
    private bool _following;
    private bool _hasPanelYaw;

    /// <summary>
    /// Places the panel in front of the player. It stays where it is while you glance around, and swings
    /// back in front of you (yaw only) once you look more than <see cref="FollowStartAngle"/> degrees away.
    /// Head pose is given in the view frame (the levelled game camera), so the panel also travels with the
    /// player and turns with the game's camera.
    /// </summary>
    public void PlacePanel(Vector3 viewOrigin, Quaternion viewRotation, Vector3 headPosition, Quaternion headRotation,
        float distance, float width, bool snapToHead)
    {
        var headYaw = headRotation.eulerAngles.y;
        if (!_hasPanelYaw || snapToHead)
        {
            _panelYaw = headYaw;
            _hasPanelYaw = true;
        }

        var offset = Mathf.Abs(Mathf.DeltaAngle(_panelYaw, headYaw));
        if (offset > FollowStartAngle)
            _following = true;
        if (_following)
        {
            _panelYaw = Mathf.MoveTowardsAngle(_panelYaw, headYaw, FollowSpeedDegrees * Time.unscaledDeltaTime);
            if (Mathf.Abs(Mathf.DeltaAngle(_panelYaw, headYaw)) < FollowStopAngle)
                _following = false;
        }

        var panelRotation = Quaternion.Euler(0f, _panelYaw, 0f);
        var localCenter = headPosition + panelRotation * new Vector3(0f, -PanelDrop, distance);
        var aspect = Screen.width > 0 ? (float)Screen.height / Screen.width : 9f / 16f;
        _panelMatrix = Matrix4x4.TRS(viewOrigin + viewRotation * localCenter, viewRotation * panelRotation,
            new Vector3(width, width * aspect, 1f));
    }

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
