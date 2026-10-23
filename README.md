# Jump Space VR

A PC VR mod for [Jump Space](https://store.steampowered.com/app/1757300/Jump_Space/) (Keepsake Games). It adds stereoscopic
6DoF head tracking, puts the game's menus and HUD on a panel in front of you, and lets you play with VR controllers.

It works with any OpenXR runtime: Virtual Desktop, SteamVR, Meta Quest Link, WMR and others.

> Status: early. Rendering, head tracking, the UI panel, controllers and the desktop mirror are implemented and
> tested against Unity's OpenXR mock runtime. Expect rough edges in specific game screens.

## Features

- **True stereo rendering.** Each eye is rendered by the game's own HDRP renderer from the headset's exact eye poses
  and field of view.
- **Decoupled head look.** The game keeps control of the body and aim (mouse, gamepad or controller sticks), and your
  head looks around freely on top of it. Ship, turret and cutscene cameras keep working.
- **Menus and HUD in VR.** All screen-space UI is drawn on a floating panel. Mouse, keyboard and gamepad navigation
  work unchanged.
- **VR controllers.** The controllers appear to the game as an Xbox-style gamepad, so every gamepad action works:
  - Sticks map to the sticks, and stick clicks to L3/R3.
  - Triggers map to the triggers.
  - Grips map to LB/RB.
  - A/B/X/Y map to A/B/X/Y.
  - The left menu button maps to Start.
- **Desktop mirror.** The monitor shows the left eye with the UI on top.
- **Headset hot-plug.** Start the game first and connect the headset whenever you like; the mod keeps retrying.

## Requirements

- Windows 10/11 and the Steam version of Jump Space.
- A PC VR headset with an OpenXR runtime set as active (Virtual Desktop, SteamVR, Oculus/Meta Link, ...).
- [.NET SDK 6.0 or newer](https://dotnet.microsoft.com/download), only when building from source.
- The game must run on **Direct3D 11**. If it starts on DirectX 12, add `-force-d3d11` to its Steam launch options.

## Install

**From a release zip (no .NET SDK needed):** extract `JumpSpaceVR-<version>.zip`, close the game and double-click
`Install.bat`. Then continue at step 4 below.

**From source:**

1. Close Jump Space.
2. Run the installer from this folder:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\install.ps1
   ```

   It finds the game through Steam and then:
   - installs [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.3 if it is missing;
   - downloads Unity's OpenXR plugin (`com.unity.xr.openxr` 1.18.0) from Unity's package registry and copies its
     native runtime into the game;
   - builds the mod and copies `JumpSpaceVR.dll` into the game's `Mods` folder.
3. **First install only:** start the game once and let MelonLoader generate its files (this takes a few minutes).
   Close it, then run the installer again so the mod can be built against those files.
4. Start your VR runtime, put on the headset, and launch Jump Space from Steam.

Pass `-GamePath "D:\path\to\JumpSpace"` if the game is not found automatically. Re-run the installer after a game
update if VR stops working.

## Controls

| Input | Action |
| --- | --- |
| `F8` | Toggle VR on/off |
| `F7`, or click both thumbsticks | Recenter (sets your seated position and forward direction) |
| `F9` | Write diagnostics to `MelonLoader/Latest.log` |
| `F10` | Save what the headset sees to `UserData/JumpSpaceVR/*.bmp` |

Mouse, keyboard and gamepads keep working while you play in VR.

## Settings

Settings live in `<game>/UserData/MelonPreferences.cfg` under `[JumpSpaceVR]`. The file is created on the first
launch with the mod.

| Setting | Default | Description |
| --- | --- | --- |
| `StartInVr` | `true` | Start VR automatically once the game has loaded. |
| `PositionalTracking` | `true` | 6DoF. Set to `false` for rotation-only tracking. |
| `WorldScale` | `1.0` | Scales your head movement. |
| `RenderScale` | `1.0` | Eye resolution multiplier (0.5–2.0). Lower it for performance. |
| `MoveUiIntoVr` | `true` | Show the UI panel in the headset. |
| `UiDistance` / `UiWidth` | `1.6` / `1.9` | UI panel distance and width, in meters. |
| `ControllersAsGamepad` | `true` | Use VR controllers as a gamepad. |
| `OpenXRRuntimeJson` | empty | Force a specific OpenXR runtime manifest instead of the system default. |
| `ToggleVrKey`, `RecenterKey`, `DiagnosticsKey`, `SnapshotKey` | F8/F7/F9/F10 | Hotkeys. |

## Troubleshooting

- **The game stays flat and the log says "VR not started".** The headset isn't connected, or no OpenXR runtime is
  active. Connect it; the mod retries every 10 seconds. `Player.log` (in
  `%USERPROFILE%\AppData\LocalLow\Keepsake Games\Jump Space`) shows the runtime's own error, for example
  `XR_ERROR_FORM_FACTOR_UNAVAILABLE`.
- **"UnityOpenXR.dll is missing".** Re-run `scripts\install.ps1`.
- **Black or wrong image in the headset.** Press `F10` and `F9` and include the BMPs and
  `MelonLoader/Latest.log` when reporting the problem.
- **Low frame rate.** Lower `RenderScale`. Each eye is a full HDRP render, so expect roughly twice the GPU cost of
  flat play.
- **Anything else.** Run `scripts\uninstall.ps1 -GamePath "<game folder>"`, then use Steam's *Verify integrity of
  game files* to return to the unmodded game.

## Known issues

- **Markers over objects and players.** These are drawn in screen space, so they sit on the UI panel and don't
  line up exactly with the 3D objects they label.
- **No controller vibration.** The game never sends rumble to gamepads, so there is nothing to forward.
- **SteamVR overlays.** The SteamVR dashboard and overlay tools such as OVR Advanced Settings can take focus at
  startup. Close the dashboard if the game appears frozen in the headset.
- **Untested on hardware.** Validation so far used Unity's OpenXR mock runtime and SteamVR's null (simulated)
  headset. Reports from real headsets are welcome; include the `F10` snapshots and `MelonLoader/Latest.log`.

## Play responsibly

Jump Space has no kernel anti-cheat, and the developers have endorsed community mods. Even so:
- The mod only changes rendering and input on your machine, but other players in co-op may not expect a modded
  client. Play solo or with friends who know.
- MelonLoader prints a "MODIFIED" banner in the game log, and crash reports sent by the game will mention it. Don't
  send modded crash reports to the developers.

## How it works

Jump Space is a Unity 6 (6000.3) IL2CPP game using HDRP, and its HDRP build has XR support compiled out. The mod
works around that:

1. **OpenXR boot.** `src/JumpSpaceVR/OpenXR` ports the lifecycle of Unity's OpenXR loader to direct calls into
   `UnityOpenXR.dll`. The managed half of Unity's OpenXR package isn't in the game, and many engine helpers were
   stripped by IL2CPP, so subsystems are created through the descriptor store and native bindings.
2. **Hooks.** `OpenXRHooks` wraps `xrGetInstanceProcAddr` to capture the session and every `xrLocateViews` result.
   The eyes are therefore rendered with exactly the poses and FOVs the compositor expects.
3. **Stereo.** `Rendering/VrCameraRig` turns the game camera into the left eye and adds a matching right-eye camera.
   `Rendering/StereoRenderer` copies both renders into the OpenXR swapchain images after HDRP finishes a frame.
4. **UI.** `VrUi/UiCompositor` draws the game's overlay UI a second time, into a transparent texture, using the
   render pipeline's UI overlay renderer list. That texture becomes the floating panel and the desktop overlay.
5. **Controllers.** `OpenXR/OpenXRControllers` creates its own OpenXR action set, and `Controls/VirtualGamepad`
   queues the values to the game as raw Input System gamepad state events.

## Development

```powershell
dotnet build src\JumpSpaceVR\JumpSpaceVR.csproj -c Release
```

`Directory.Build.props` sets the game path. Override it in `Directory.Build.local.props` or with
`-p:GamePath=...`.

Headless testing uses Unity's OpenXR **mock runtime**, so no headset is needed:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\dev\test-cycle.ps1 -Seconds 90
```

The script:
- rebuilds and deploys the mod;
- launches the game through Steam against the mock runtime, with eye snapshots turned on and simulated Touch
  controllers driven through `XR_EXT_conformance_automation`;
- prints the mod's log and any crash recorded by Windows;
- restores your normal settings.

`scripts\package.ps1` builds a release zip into `dist/`.

`-RealRuntime` tests against your active runtime instead. `-RuntimeManifest <path>` tests against a specific
runtime, for example SteamVR's `steamxr_win64.json` with its null driver enabled. That gives a real compositor
with a simulated headset.

`Jump Space VR mod research.md` contains the background feasibility research.

## Third-party components

- **MelonLoader** (Apache-2.0) is downloaded from its GitHub releases.
- **Unity OpenXR plugin** (Unity Companion License) is downloaded from Unity's package registry at install time. It
  isn't redistributed by this repository.
