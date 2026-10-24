using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JumpSpaceVR.VrUi;

/// <summary>
/// Optional frame-rate readout in the top-left corner of the UI panel. It is an ordinary overlay canvas, so the VR UI
/// capture shows it in the headset.
/// </summary>
internal sealed class FrameRateCounter
{
    private const string RootName = "JumpSpaceVR_FrameRate";
    private const float RefreshSeconds = 0.5f;

    private GameObject _root;
    private TextMeshProUGUI _text;
    private float _nextRefresh;
    private int _frames;
    private float _elapsed;
    private bool _failed;

    public void Update(bool visible)
    {
        if (!visible)
        {
            if (_root != null)
                _root.SetActive(false);
            return;
        }
        if (!EnsureCanvas())
            return;
        _root.SetActive(true);

        _frames++;
        _elapsed += Time.unscaledDeltaTime;
        var now = Time.unscaledTime;
        if (now < _nextRefresh)
            return;
        _nextRefresh = now + RefreshSeconds;
        var fps = _elapsed > 0f ? _frames / _elapsed : 0f;
        _text.text = $"{fps:0} fps  {1000f / Mathf.Max(fps, 1f):0.0} ms";
        _text.color = fps >= 72f ? new Color(0.55f, 1f, 0.55f) : fps >= 45f ? new Color(1f, 0.85f, 0.35f) : new Color(1f, 0.45f, 0.4f);
        _frames = 0;
        _elapsed = 0f;
    }

    private bool EnsureCanvas()
    {
        if (_root != null)
            return true;
        if (_failed)
            return false;
        try
        {
            _root = new GameObject(RootName);
            Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 31999;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var label = new GameObject("Text");
            label.transform.SetParent(_root.transform, false);
            _text = label.AddComponent<TextMeshProUGUI>();
            var font = TMP_Settings.defaultFontAsset;
            if (font != null)
                _text.font = font;
            _text.fontSize = 30f;
            _text.raycastTarget = false;
            var rect = label.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(260f, -150f);
            rect.sizeDelta = new Vector2(420f, 50f);
            return true;
        }
        catch (System.Exception exception)
        {
            Log.Warning($"Could not create the frame-rate counter: {exception.Message}");
            if (_root != null)
                Object.Destroy(_root);
            _root = null;
            _failed = true;
            return false;
        }
    }
}
