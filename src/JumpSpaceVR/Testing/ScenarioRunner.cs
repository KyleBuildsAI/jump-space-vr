using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using JumpSpaceVR.OpenXR;
using UnityEngine;

namespace JumpSpaceVR.Testing;

/// <summary>
/// Runs a scripted VR test against Unity's OpenXR mock runtime: moves the simulated head and hands, presses
/// controller inputs and keys, waits, and checks the mod's state. Results are written to the MelonLoader log as
/// "[scenario] PASS/FAIL ..." lines and a final "[scenario] DONE passed=N failed=M".
///
/// Script format (one step per line, '#' starts a comment):
///   wait 2.5                                  seconds
///   waitfor inLevel == 1 120                  metric, operator, value, timeout seconds
///   head x y z pitch yaw roll                 meters / degrees, tracking space
///   target Toilet                             pick the nearest interactable of a type (onTarget, targetYaw...)
///   headlook targetYaw targetPitch            turn the head to angles the mod measures (relative to the body)
///   hand right x y z pitch yaw roll           grip and aim pose of one hand
///   input right.trigger 1                     see ConformanceInputDriver for names; sticks take two values
///   key 0x0D                                  virtual-key code sent to the game window
///   keyuntil 0x1B 3 cursorFree == 1 60        press a key every 3 s until the condition holds (timeout 60 s)
///   expect aimYawOffset == 30 3               metric, operator, value, tolerance (for ==)
///   config StabilizeView false                change a mod setting for this run only (restored when it ends)
///   pad rightstick 1 0                        scripted gamepad: leftstick/rightstick x y, lefttrigger/righttrigger v,
///                                             a/b/x/y/start/view 0|1, release (see TestGamepad)
///   print fps cpuMainMs                       log metric values without checking them
///   galaxymap                                 open the galaxy map as if the player used its terminal
///   tree PF_GUI/FullScreenGUI/PF_FirstPersonHUD    log a scene object's children (active state, depth 4)
///   eyeshot                                   save the left eye's render texture directly (eye-left-*.bmp)
///   itemtohand                                hang the held item on the right hand's socket (as some spawns do)
///   snapshot | diagnostics | recenter | log text | quit
/// </summary>
internal sealed class ScenarioRunner
{
    private readonly MockRuntimeControl _mock = new();
    private readonly ConformanceInputDriver _input;
    private readonly Func<string, float?> _metric;
    private readonly Action<string> _command;

    private readonly List<string> _steps = new();
    private int _stepIndex;
    private float _resumeTime;
    private (string Metric, string Op, float Value, float Deadline)? _waitFor;
    private byte _repeatKey;
    private float _repeatInterval;
    private float _nextRepeatTime;
    private byte _pendingKeyUp;
    private float _keyUpTime;
    private int _passed;
    private int _failed;
    private bool _finished;

    public bool IsActive => _steps.Count > 0 && !_finished;

    public ScenarioRunner(ConformanceInputDriver input, Func<string, float?> metric, Action<string> command)
    {
        _input = input;
        _metric = metric;
        _command = command;
    }

    public bool Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (!File.Exists(path))
        {
            Log.Error($"[scenario] file not found: {path}");
            return false;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length > 0)
                _steps.Add(line);
        }
        Log.Msg($"[scenario] loaded {Path.GetFileName(path)} ({_steps.Count} steps)");
        return _steps.Count > 0;
    }

    /// <summary>
    /// Advances the script. Call once per frame, also while VR is off, so scenarios can toggle VR; steps that move
    /// the simulated head do nothing until the mock runtime is running.
    /// </summary>
    public void Update()
    {
        if (!IsActive)
            return;

        var now = Time.unscaledTime;
        if (_pendingKeyUp != 0 && now >= _keyUpTime)
        {
            keybd_event(_pendingKeyUp, ScanCode(_pendingKeyUp), KeyEventKeyUp, UIntPtr.Zero);
            _pendingKeyUp = 0;
        }

        if (_waitFor.HasValue)
        {
            var (metric, op, value, deadline) = _waitFor.Value;
            var current = _metric(metric);
            if (current.HasValue && Compare(current.Value, op, value, 0.001f))
            {
                Log.Msg($"[scenario] waitfor {metric} {op} {value} satisfied ({current.Value:0.###})");
                _waitFor = null;
            }
            else if (now > deadline)
            {
                Fail($"waitfor {metric} {op} {value} timed out (last {Format(current)})");
                // Record where things stood, so an intermittent failure explains itself afterwards.
                _command("diagnostics");
                _command("snapshot");
                _waitFor = null;
            }
            else
            {
                if (_repeatKey != 0 && now >= _nextRepeatTime && _pendingKeyUp == 0)
                {
                    PressKey(_repeatKey, now);
                    _nextRepeatTime = now + _repeatInterval;
                }
                return;
            }
            _repeatKey = 0;
        }

        while (_stepIndex < _steps.Count && now >= _resumeTime && !_waitFor.HasValue && !_finished)
            Execute(_steps[_stepIndex++], now);

        if (_stepIndex >= _steps.Count && now >= _resumeTime && !_waitFor.HasValue && !_finished)
            Finish();
    }

    private void Execute(string step, float now)
    {
        var parts = step.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "wait":
                    _resumeTime = now + Number(parts[1]);
                    break;
                case "waitfor":
                    _waitFor = (parts[1], parts[2], Number(parts[3]), now + (parts.Length > 4 ? Number(parts[4]) : 60f));
                    break;
                case "head":
                    _mock.SetHead(Vector(parts, 1), Rotation(parts, 4));
                    break;
                case "headlook":
                {
                    // Turn the head toward something the mod measures: headlook <yawMetric> <pitchMetric> (degrees).
                    var yaw = _metric(parts[1]) ?? throw new ArgumentException($"no value for {parts[1]}");
                    var pitch = _metric(parts[2]) ?? throw new ArgumentException($"no value for {parts[2]}");
                    _mock.SetHead(new Vector3(0f, 1.6f, 0f), Quaternion.Euler(pitch, yaw, 0f));
                    Log.Msg($"[scenario] headlook yaw {yaw:0.#} pitch {pitch:0.#}");
                    break;
                }
                case "hand":
                    var right = parts[1].Equals("right", StringComparison.OrdinalIgnoreCase);
                    var position = Vector(parts, 2);
                    var rotation = Rotation(parts, 5);
                    _input.SetHandPose(right, position, rotation);
                    break;
                case "input":
                    if (!ConformanceInputDriver.IsKnownInput(parts[1]))
                        throw new ArgumentException($"unknown input '{parts[1]}'");
                    _input.SetInput(parts[1], Number(parts[2]), parts.Length > 3 ? Number(parts[3]) : 0f);
                    break;
                case "key":
                    PressKey(Convert.ToByte(parts[1], 16), now);
                    _resumeTime = Mathf.Max(_resumeTime, _keyUpTime + 0.05f);
                    break;
                case "keyuntil":
                    _repeatKey = Convert.ToByte(parts[1], 16);
                    _repeatInterval = Number(parts[2]);
                    _nextRepeatTime = now;
                    _waitFor = (parts[3], parts[4], Number(parts[5]), now + (parts.Length > 6 ? Number(parts[6]) : 60f));
                    break;
                case "expect":
                    Expect(parts[1], parts[2], Number(parts[3]), parts.Length > 4 ? Number(parts[4]) : 0.001f);
                    break;
                case "print":
                    // Records measurements without judging them (frame rates, timings).
                    for (var index = 1; index < parts.Length; index++)
                        Log.Msg($"[scenario] {parts[index]} = {Format(_metric(parts[index]))}");
                    break;
                case "log":
                    Log.Msg("[scenario] " + step.Substring(4).Trim());
                    break;
                case "config":
                    _command($"config {parts[1]} {parts[2]}");
                    break;
                case "pad":
                    _command($"pad {parts[1]} {(parts.Length > 2 ? parts[2] : "0")} {(parts.Length > 3 ? parts[3] : "0")}");
                    break;
                case "target":
                    _command($"target {parts[1]}");
                    break;
                case "hide":
                    _command($"hide {step.Substring(5).Trim()}");
                    break;
                case "tree":
                    _command($"tree {step.Substring(5).Trim()}");
                    break;
                case "interactables":
                case "near":
                case "eyeshot":
                case "itemtohand":
                case "galaxymap":
                case "rig":
                case "snapshot":
                case "diagnostics":
                case "recenter":
                    _command(parts[0].ToLowerInvariant());
                    break;
                case "quit":
                    Finish();
                    Application.Quit();
                    break;
                default:
                    throw new ArgumentException($"unknown step '{parts[0]}'");
            }
        }
        catch (Exception exception)
        {
            Fail($"step '{step}': {exception.Message}");
        }
    }

    /// <summary>Presses a key; it is released a few frames later so the game's Input System sees a full press.</summary>
    private void PressKey(byte virtualKey, float now)
    {
        _pendingKeyUp = virtualKey;
        FocusGameWindow();
        keybd_event(virtualKey, ScanCode(virtualKey), 0, UIntPtr.Zero);
        _keyUpTime = now + 0.15f;
    }

    private void Expect(string metric, string op, float value, float tolerance)
    {
        var current = _metric(metric);
        if (current.HasValue && Compare(current.Value, op, value, tolerance))
        {
            _passed++;
            Log.Msg($"[scenario] PASS expect {metric} {op} {value} (got {current.Value:0.###})");
        }
        else
        {
            Fail($"expect {metric} {op} {value} ±{tolerance} (got {Format(current)})");
        }
    }

    private void Fail(string message)
    {
        _failed++;
        Log.Error("[scenario] FAIL " + message);
    }

    private void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        _command("restore-config");
        Log.Msg($"[scenario] DONE passed={_passed} failed={_failed}");
    }

    private static bool Compare(float current, string op, float value, float tolerance) => op switch
    {
        "==" => Mathf.Abs(current - value) <= tolerance,
        "!=" => Mathf.Abs(current - value) > tolerance,
        "<" => current < value,
        ">" => current > value,
        "<=" => current <= value,
        ">=" => current >= value,
        _ => throw new ArgumentException($"unknown operator '{op}'"),
    };

    private static float Number(string text) => float.Parse(text, CultureInfo.InvariantCulture);

    private static Vector3 Vector(string[] parts, int start) =>
        new(Number(parts[start]), Number(parts[start + 1]), Number(parts[start + 2]));

    private static Quaternion Rotation(string[] parts, int start) =>
        Quaternion.Euler(Number(parts[start]), Number(parts[start + 1]), Number(parts[start + 2]));

    private static string Format(float? value) => value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "n/a";

    /// <summary>
    /// Synthetic key events go to the foreground window, so bring the game forward first. The Alt tap lets a
    /// background process take the foreground (Windows otherwise refuses SetForegroundWindow).
    /// </summary>
    private static void FocusGameWindow()
    {
        var window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        if (window == IntPtr.Zero || GetForegroundWindow() == window)
            return;
        keybd_event(VirtualKeyAlt, 0, 0, UIntPtr.Zero);
        keybd_event(VirtualKeyAlt, 0, KeyEventKeyUp, UIntPtr.Zero);
        ShowWindow(window, ShowWindowRestore);
        if (!SetForegroundWindow(window))
            Log.Warning("[scenario] could not focus the game window; key steps may not reach the game");
    }

    private static byte ScanCode(byte virtualKey) => (byte)MapVirtualKey(virtualKey, 0);

    private const uint KeyEventKeyUp = 2;
    private const byte VirtualKeyAlt = 0x12;
    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
