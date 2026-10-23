using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace JumpSpaceVR.VrUi;

/// <summary>
/// The game's letterbox bars frame scripted moments on a monitor (PF_FadeToBlack's top and bottom panels during the
/// prologue walk, LetterBox_Upper/Lower in cutscenes), but on the VR panel they become black slabs floating in front
/// of the player. Hides them while VR runs.
/// </summary>
internal sealed class CinematicBars
{
    private static readonly string[] BarNames = { "TopPannel", "BottomPannel", "LetterBox_Upper", "LetterBox_Lower" };
    private const float LookupIntervalSeconds = 2f;

    private Graphic[] _bars = Array.Empty<Graphic>();
    private float _nextLookup;
    private bool _hidden;

    public void Update(bool hide)
    {
        if (hide && Time.unscaledTime >= _nextLookup)
        {
            _nextLookup = Time.unscaledTime + LookupIntervalSeconds;
            Show(_bars, true);
            _bars = FindBars();
        }

        if (!hide && !_hidden)
            return;
        Show(_bars, !hide);
        _hidden = hide;
    }

    private static void Show(Graphic[] bars, bool visible)
    {
        foreach (var bar in bars)
        {
            if (bar != null)
                bar.enabled = visible;
        }
    }

    private static Graphic[] FindBars()
    {
        var bars = new List<Graphic>();
        foreach (var graphic in UnityEngine.Object.FindObjectsByType<Graphic>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (Array.IndexOf(BarNames, graphic.gameObject.name) >= 0)
                bars.Add(graphic);
        }
        return bars.ToArray();
    }
}
