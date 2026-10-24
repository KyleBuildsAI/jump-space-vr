using UnityEngine;

namespace JumpSpaceVR.VrUi;

/// <summary>
/// Hides the game's crosshair (PF_FirstPersonHUD/Crosshairs) while VR runs and the setting is off. On the VR panel the
/// crosshair floats in front of the view and doesn't line up with a gun aimed by hand. The whole group is scaled to
/// zero rather than deactivated, so the game can keep swapping crosshairs per weapon without fighting the mod.
/// </summary>
internal sealed class CrosshairHider
{
    private const string CrosshairsPath = "PF_GUI/FullScreenGUI/PF_FirstPersonHUD/Crosshairs";
    private const float LookupIntervalSeconds = 2f;

    private Transform _crosshairs;
    private Vector3 _originalScale = Vector3.one;
    private float _nextLookup;
    private bool _hidden;

    public void Update(bool hide)
    {
        if (hide && (_crosshairs == null || Time.unscaledTime >= _nextLookup))
        {
            _nextLookup = Time.unscaledTime + LookupIntervalSeconds;
            var found = GameObject.Find(CrosshairsPath)?.transform;
            if (found != _crosshairs)
            {
                Show();
                _crosshairs = found;
            }
        }

        if (hide)
            Hide();
        else
            Show();
    }

    private void Hide()
    {
        if (_crosshairs == null || _hidden)
            return;
        _originalScale = _crosshairs.localScale;
        _crosshairs.localScale = Vector3.zero;
        _hidden = true;
    }

    private void Show()
    {
        if (!_hidden)
            return;
        if (_crosshairs != null)
            _crosshairs.localScale = _originalScale;
        _hidden = false;
    }
}
