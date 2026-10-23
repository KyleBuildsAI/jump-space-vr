using UnityEngine;
using UnityEngine.UI;

namespace JumpSpaceVR.VrUi;

/// <summary>
/// The game's letterbox bars (PF_FadeToBlack top and bottom panels) frame scripted moments on a monitor, but on the
/// VR panel they become two black slabs floating in front of the player. Hides them while VR runs.
/// </summary>
internal sealed class CinematicBars
{
    private const string BarsRoot = "PF_FadeToBlackController(Clone)/PF_FadeToBlack";
    private static readonly string[] BarNames = { "TopPannel", "BottomPannel" };
    private const float LookupIntervalSeconds = 2f;

    private Graphic[] _bars = System.Array.Empty<Graphic>();
    private float _nextLookup;
    private bool _hidden;

    public void Update(bool hide)
    {
        if (hide && Time.unscaledTime >= _nextLookup)
        {
            _nextLookup = Time.unscaledTime + LookupIntervalSeconds;
            _bars = FindBars();
        }

        if (!hide && !_hidden)
            return;
        foreach (var bar in _bars)
        {
            if (bar != null)
                bar.enabled = !hide;
        }
        _hidden = hide;
    }

    private static Graphic[] FindBars()
    {
        var root = GameObject.Find(BarsRoot);
        if (root == null)
            return System.Array.Empty<Graphic>();
        var bars = new System.Collections.Generic.List<Graphic>();
        foreach (var name in BarNames)
        {
            var child = root.transform.Find(name);
            var graphic = child != null ? child.GetComponent<Graphic>() : null;
            if (graphic != null)
                bars.Add(graphic);
        }
        return bars.ToArray();
    }
}
