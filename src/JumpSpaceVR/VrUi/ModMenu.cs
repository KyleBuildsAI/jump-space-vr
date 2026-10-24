using System;
using System.Collections.Generic;
using System.Text;
using Il2CppTMPro;
using JumpSpaceVR.OpenXR;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace JumpSpaceVR.VrUi;

/// <summary>
/// In-game settings menu (Insert key, or hold the right thumbstick click). It is an ordinary screen-space UGUI
/// canvas, so it shows on the VR UI panel and on the desktop like the game's own menus.
///
/// Navigation: arrow keys / left stick up-down to select, left-right to change, Enter / A to activate,
/// Insert / Escape / B to close. While it is open, controller input goes to the menu instead of the game.
/// </summary>
internal sealed class ModMenu
{
    private const string RootName = "JumpSpaceVR_ModMenu";
    private const float StickThreshold = 0.6f;
    // Holding a direction repeats it: after a short delay, then faster, and after FastAfter seconds numbers move in
    // bigger steps, so long ranges don't need dozens of presses.
    private const float RepeatDelay = 0.4f;
    private const float RepeatInterval = 0.12f;
    private const float FastRepeatInterval = 0.05f;
    private const float FastAfter = 1.5f;
    private const float BigStepsAfter = 3f;
    private const int BigStep = 5;

    private sealed class Item
    {
        public string Label;
        public Func<string> Value;
        public Action<int> Change;
        public Action Activate;
        public bool Repeats;
    }

    private readonly List<Item> _items = new();
    private GameObject _root;
    private TextMeshProUGUI _text;
    private int _selected;
    private bool _dirty = true;

    private int _heldDirection;
    private float _heldSince;
    private float _nextRepeatTime;
    private bool _confirmHeld;
    private bool _backHeld;

    public bool IsOpen { get; private set; }

    public event Action Closed;

    // ---- building the item list ----

    public void AddChoice<T>(string label, MelonPreferences_Entry<T> entry, Action<T> onChanged = null) where T : struct, Enum
    {
        var values = (T[])Enum.GetValues(typeof(T));
        _items.Add(new Item
        {
            Label = label,
            Value = () => entry.Value.ToString(),
            Change = direction =>
            {
                var index = Array.IndexOf(values, entry.Value);
                entry.Value = values[(index + Math.Sign(direction) + values.Length) % values.Length];
                onChanged?.Invoke(entry.Value);
            },
        });
    }

    public void AddToggle(string label, MelonPreferences_Entry<bool> entry)
    {
        _items.Add(new Item
        {
            Label = label,
            Value = () => entry.Value ? "On" : "Off",
            Change = _ => entry.Value = !entry.Value,
            Activate = () => entry.Value = !entry.Value,
        });
    }

    public void AddNumber(string label, MelonPreferences_Entry<float> entry, float step, float min, float max, string format,
        Action<float> onChanged = null)
    {
        _items.Add(new Item
        {
            Label = label,
            Value = () => entry.Value.ToString(format),
            Repeats = true,
            Change = direction =>
            {
                entry.Value = Mathf.Clamp((float)Math.Round(entry.Value + direction * step, 3), min, max);
                onChanged?.Invoke(entry.Value);
            },
        });
    }

    public void AddAction(string label, Action action)
    {
        _items.Add(new Item { Label = label, Value = () => "", Activate = action });
    }

    /// <summary>An action that only runs when activated twice within a few seconds (for destructive actions).</summary>
    public void AddConfirmedAction(string label, Action action)
    {
        var armedUntil = 0f;
        _items.Add(new Item
        {
            Label = label,
            Value = () => Time.unscaledTime < armedUntil ? "press again to confirm" : "",
            Activate = () =>
            {
                if (Time.unscaledTime < armedUntil)
                {
                    armedUntil = 0f;
                    action();
                }
                else
                {
                    armedUntil = Time.unscaledTime + ConfirmSeconds;
                }
            },
        });
    }

    private const float ConfirmSeconds = 4f;

    // ---- lifecycle ----

    public void Toggle()
    {
        if (IsOpen)
            Close();
        else
            Open();
    }

    public void Open()
    {
        if (!EnsureCanvas())
            return;
        IsOpen = true;
        _dirty = true;
        _root.SetActive(true);
        _heldDirection = 0;
        _confirmHeld = true; // ignore the press that opened the menu
        _backHeld = true;
        Render();
    }

    public void Close()
    {
        IsOpen = false;
        if (_root != null)
            _root.SetActive(false);
        MelonPreferences.Save();
        Closed?.Invoke();
    }

    /// <summary>Handles keyboard and controller navigation. Call once per frame.</summary>
    public void Update(Controls.HotkeyReader keys, in ControllerState controllers)
    {
        if (!IsOpen)
            return;
        if (_root == null)
        {
            // The canvas was destroyed (scene change); rebuild it.
            IsOpen = false;
            Open();
            return;
        }

        if (keys.WasPressed(KeyCode.Escape) || keys.WasPressed(KeyCode.Backspace))
        {
            Close();
            return;
        }
        if (keys.WasPressed(KeyCode.Return))
            Activate();

        HandleDirection(KeyboardDirection(keys), StickDirection(controllers));
        HandleButtons(controllers);

        if (_dirty)
            Render();
    }

    private static int KeyboardDirection(Controls.HotkeyReader keys)
    {
        if (keys.IsDown(KeyCode.UpArrow)) return -1;
        if (keys.IsDown(KeyCode.DownArrow)) return 1;
        if (keys.IsDown(KeyCode.RightArrow)) return 2;
        if (keys.IsDown(KeyCode.LeftArrow)) return -2;
        return 0;
    }

    private static int StickDirection(in ControllerState controllers)
    {
        if (!controllers.IsActive)
            return 0;
        var stick = controllers.LeftStick;
        if (stick.y > StickThreshold) return -1;
        if (stick.y < -StickThreshold) return 1;
        if (stick.x > StickThreshold) return 2;
        if (stick.x < -StickThreshold) return -2;
        return 0;
    }

    /// <summary>Up/down moves the selection, left/right changes the value; holding repeats and speeds up.</summary>
    private void HandleDirection(int keyboard, int stick)
    {
        var direction = keyboard != 0 ? keyboard : stick;
        var now = Time.unscaledTime;
        if (direction != _heldDirection)
        {
            _heldDirection = direction;
            _heldSince = now;
            if (direction == 0)
                return;
            Step(direction, 1);
            _nextRepeatTime = now + RepeatDelay;
            return;
        }

        if (direction == 0 || now < _nextRepeatTime)
            return;
        // Holding left/right only repeats on numbers; on toggles and choices it would flip back and forth.
        if (Math.Abs(direction) == 2 && !_items[_selected].Repeats)
            return;
        var held = now - _heldSince;
        _nextRepeatTime = now + (held >= FastAfter ? FastRepeatInterval : RepeatInterval);
        Step(direction, held >= BigStepsAfter ? BigStep : 1);
    }

    private void Step(int direction, int size)
    {
        if (Math.Abs(direction) == 1)
            Move(direction);
        else
            Change(Math.Sign(direction) * size);
    }

    private void HandleButtons(in ControllerState controllers)
    {
        if (!controllers.IsActive)
            return;

        if (controllers.ButtonA && !_confirmHeld)
            Activate();
        _confirmHeld = controllers.ButtonA;

        if (controllers.ButtonB && !_backHeld)
            Close();
        _backHeld = controllers.ButtonB;
    }

    private void Move(int delta)
    {
        _selected = (_selected + delta + _items.Count) % _items.Count;
        _dirty = true;
    }

    private void Change(int direction)
    {
        var item = _items[_selected];
        if (item.Change == null)
            return;
        item.Change(direction);
        _dirty = true;
    }

    private void Activate()
    {
        var item = _items[_selected];
        if (item.Activate != null)
            item.Activate();
        else
            item.Change?.Invoke(1);
        _dirty = true;
    }

    // ---- presentation ----

    private void Render()
    {
        _dirty = false;
        if (_text == null)
            return;

        var builder = new StringBuilder();
        builder.Append("<size=120%><b>JUMP SPACE VR</b></size>\n");
        builder.Append("<size=70%><color=#8FA3B8>Arrows / left stick: select and change  ·  Enter / A: activate  ·  Insert / B: close</color></size>\n\n");
        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            var selected = index == _selected;
            var value = item.Value();
            builder.Append(selected ? "<color=#FFD24A>▶ " : "<color=#E6EDF3>   ");
            builder.Append(item.Label);
            if (value.Length > 0)
                builder.Append(selected ? $"   <b>◀ {value} ▶</b>" : $"   <color=#9FB4C8>{value}</color>");
            builder.Append("</color>\n");
        }
        _text.text = builder.ToString();
    }

    private bool EnsureCanvas()
    {
        if (_root != null)
            return true;

        try
        {
            _root = new GameObject(RootName);
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var panel = new GameObject("Panel");
            panel.transform.SetParent(_root.transform, false);
            var background = panel.AddComponent<Image>();
            background.color = new Color(0.03f, 0.06f, 0.09f, 0.88f);
            background.raycastTarget = false;
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(900f, 1060f);

            var label = new GameObject("Text");
            label.transform.SetParent(panel.transform, false);
            _text = label.AddComponent<TextMeshProUGUI>();
            var font = TMP_Settings.defaultFontAsset;
            if (font != null)
                _text.font = font;
            _text.fontSize = 23f;
            _text.richText = true;
            _text.raycastTarget = false;
            _text.color = Color.white;
            var textRect = label.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(40f, 30f);
            textRect.offsetMax = new Vector2(-40f, -30f);

            _root.SetActive(false);
            return true;
        }
        catch (Exception exception)
        {
            Log.Error($"Could not build the mod menu: {exception.Message}");
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
            return false;
        }
    }
}
