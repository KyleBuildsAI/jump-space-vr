using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;

namespace JumpSpaceVR.VrUi;

/// <summary>
/// Makes the laser pointer easy to click with: a hand held out at arm's length shakes by more than the game's small
/// buttons, and pulling the trigger moves the controller. Three helpers, in screen pixels:
/// the pointer is smoothed, it snaps onto the nearest clickable UI element within reach (and stays there until the
/// laser moves clearly away), and it holds still from the moment the trigger starts to move until it is released.
/// </summary>
internal sealed class PointerAssist
{
    // Distances at 1080 lines; scaled with the screen height.
    private const float SnapRadius = 45f;
    private const float ReleaseRadius = 75f;
    private const float JumpDistance = 250f;
    private const float EdgeInset = 4f;
    private const float SmoothingSeconds = 0.05f;
    private const float TriggerLock = 0.12f;
    private const float TriggerUnlock = 0.06f;
    private const float RefreshSeconds = 0.5f;
    private const float MinVisibleAlpha = 0.05f;

    private readonly List<(Selectable Selectable, RectTransform Rect, Camera Camera)> _targets = new();
    private readonly Il2CppStructArray<Vector3> _corners = new(4);
    private float _nextRefresh;
    private Vector2 _smoothed;
    private bool _hasSmoothed;
    private Vector2? _locked;
    private Selectable _snapped;

    /// <summary>Clickable elements found at the last refresh (for tests).</summary>
    public int TargetCount => _targets.Count;

    /// <summary>True while the pointer sits on a snapped element (for tests).</summary>
    public bool Snapped => _snapped != null;

    /// <summary>Turns the raw laser point into the point the virtual mouse should use.</summary>
    public Vector2 Assist(Vector2 raw, float trigger)
    {
        var scale = Screen.height / 1080f;
        Refresh();

        // While the trigger is travelling, the press itself would drag the pointer off small buttons.
        if (_locked.HasValue)
        {
            if (trigger > TriggerUnlock)
                return _locked.Value;
            _locked = null;
        }

        var blend = 1f - Mathf.Exp(-Time.unscaledDeltaTime / SmoothingSeconds);
        _smoothed = !_hasSmoothed || (raw - _smoothed).magnitude > JumpDistance * scale
            ? raw
            : Vector2.Lerp(_smoothed, raw, blend);
        _hasSmoothed = true;

        var point = Snap(_smoothed, scale);
        if (trigger > TriggerLock)
            _locked = point;
        return point;
    }

    /// <summary>The laser left the panel: start fresh next time.</summary>
    public void Reset()
    {
        _hasSmoothed = false;
        _locked = null;
        _snapped = null;
    }

    private Vector2 Snap(Vector2 point, float scale)
    {
        // Stay on the current element until the laser moves clearly away, so it doesn't flicker between neighbours.
        if (_snapped != null && TryRect(_snapped, out var current) && Distance(current, point) < ReleaseRadius * scale)
            return Inside(current, point, scale);

        _snapped = null;
        var best = SnapRadius * scale;
        Rect bestRect = default;
        foreach (var (selectable, rect, camera) in _targets)
        {
            if (!Usable(selectable) || !ScreenRect(rect, camera, out var screenRect))
                continue;
            var distance = Distance(screenRect, point);
            if (distance < best)
            {
                best = distance;
                bestRect = screenRect;
                _snapped = selectable;
            }
        }
        return _snapped != null ? Inside(bestRect, point, scale) : point;
    }

    /// <summary>Nearest point inside the element (a little in from its edge), so wide buttons don't pull to their centre.</summary>
    private static Vector2 Inside(Rect rect, Vector2 point, float scale)
    {
        var insetX = Mathf.Min(EdgeInset * scale, rect.width * 0.5f);
        var insetY = Mathf.Min(EdgeInset * scale, rect.height * 0.5f);
        return new Vector2(Mathf.Clamp(point.x, rect.xMin + insetX, rect.xMax - insetX),
            Mathf.Clamp(point.y, rect.yMin + insetY, rect.yMax - insetY));
    }

    private static float Distance(Rect rect, Vector2 point)
    {
        var dx = Mathf.Max(rect.xMin - point.x, 0f, point.x - rect.xMax);
        var dy = Mathf.Max(rect.yMin - point.y, 0f, point.y - rect.yMax);
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private bool TryRect(Selectable selectable, out Rect rect)
    {
        rect = default;
        if (!Usable(selectable))
            return false;
        foreach (var (candidate, rectTransform, camera) in _targets)
        {
            if (candidate == selectable)
                return ScreenRect(rectTransform, camera, out rect);
        }
        return false;
    }

    private static bool Usable(Selectable selectable)
    {
        if (selectable == null || !selectable.isActiveAndEnabled || !selectable.IsInteractable())
            return false;
        // Faded-out menus keep their buttons enabled; only snap to what can be seen.
        var graphic = selectable.targetGraphic;
        return graphic == null || graphic.canvasRenderer.GetInheritedAlpha() > MinVisibleAlpha;
    }

    private bool ScreenRect(RectTransform rect, Camera camera, out Rect screenRect)
    {
        screenRect = default;
        if (rect == null)
            return false;
        rect.GetWorldCorners(_corners);
        var min = RectTransformUtility.WorldToScreenPoint(camera, _corners[0]);
        var max = RectTransformUtility.WorldToScreenPoint(camera, _corners[2]);
        screenRect = Rect.MinMaxRect(Mathf.Min(min.x, max.x), Mathf.Min(min.y, max.y), Mathf.Max(min.x, max.x), Mathf.Max(min.y, max.y));
        return screenRect.width > 1f && screenRect.height > 1f &&
               screenRect.xMax > 0f && screenRect.yMax > 0f && screenRect.xMin < Screen.width && screenRect.yMin < Screen.height;
    }

    private void Refresh()
    {
        var now = Time.unscaledTime;
        if (now < _nextRefresh)
            return;
        _nextRefresh = now + RefreshSeconds;
        _targets.Clear();
        foreach (var selectable in Selectable.allSelectablesArray)
        {
            if (selectable == null)
                continue;
            var rect = selectable.GetComponent<RectTransform>();
            var canvas = selectable.GetComponentInParent<Canvas>();
            if (rect == null || canvas == null)
                continue;
            var root = canvas.rootCanvas;
            var camera = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
            _targets.Add((selectable, rect, camera));
        }
    }
}
