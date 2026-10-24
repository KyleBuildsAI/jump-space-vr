# Jump Space VR

A PC VR mod for [Jump Space](https://store.steampowered.com/app/1757300/Jump_Space/) (Keepsake Games). It adds stereoscopic
6DoF head tracking, puts the game's menus and HUD on a panel in front of you, and lets you play with VR controllers.

It works with any OpenXR runtime (Steam Link / SteamVR, Virtual Desktop, Meta Quest Link, WMR and others) and with
Quest 3 / 3S / 2 / Pro Touch controllers, Valve Index, Windows Mixed Reality and HTC Vive controllers, or a gamepad.

## Download

### [⬇ Download Jump Space VR (latest)](https://github.com/KyleBuildsAI/jump-space-vr/releases/latest/download/JumpSpaceVR.zip)

1. Download the zip above and extract it anywhere (right-click > **Extract All**).
2. Close Jump Space, then double-click **`Install.bat`**. It finds the game and sets everything up.
3. Start your VR app (SteamVR / Steam Link, Virtual Desktop or Meta Quest Link), then launch Jump Space from Steam.
   The first launch takes a few extra minutes; VR starts by itself.

Uninstall: double-click `Uninstall.bat`. Older versions and release notes:
[Releases](https://github.com/KyleBuildsAI/jump-space-vr/releases). `QUICKSTART.txt` in the zip has the controls.

> Status: early access, like the game. Played on a Quest 3 over Steam Link and tested headlessly against Unity's
> OpenXR mock runtime. Expect rough edges in specific game screens; please report them in
> [Issues](https://github.com/KyleBuildsAI/jump-space-vr/issues) with your `MelonLoader/Latest.log`.

## Features

- **True stereo rendering.** Each eye is rendered by the game's own HDRP renderer from the headset's exact eye poses
  and field of view.
- **Decoupled head look.** The game keeps control of the body and aim (mouse, gamepad or controller sticks), and your
  head looks around freely on top of it. Ship, turret and cutscene cameras keep working.
- **Menus and HUD in VR.** All screen-space UI is drawn on a floating panel. Mouse, keyboard and gamepad navigation
  work unchanged.
- **Motion controls.** Aim with your right controller: the game's own look system is steered to wherever the
  controller points, so firing and co-op aim replication work natively. The right stick snap-turns (45°) or
  smooth-turns your body; the left stick moves relative to your body. The first-person weapon is placed in your
  right hand at the game's item attach point.
- **VR controllers as a gamepad.** Everything else maps to an Xbox-style gamepad, so every gamepad action works:
  - Left stick moves, and stick clicks map to L3/R3.
  - Triggers map to the triggers.
  - Grips map to LB/RB.
  - A/B/X/Y map to A/B/X/Y.
  - The left menu button maps to Start.
- **Real gamepads too.** Xbox, PlayStation (DualSense, DualShock 4), Switch Pro and anything Steam Input presents as
  a gamepad. Pick one up and the mod switches to it: the view follows the game camera, the pad aims and the motion
  controllers step aside; touch the VR controllers again and they take over. Firing rumbles the pad, the left trigger
  lines the sights up with your aiming eye, and hold View/Select (Share/Create on PlayStation) for 1 s for the VR
  menu. Set `ControllerMode` to always use one or the other.
- **Laser pointer in menus.** When the game shows a mouse cursor (menus, inventory, pause), point your right
  controller at the UI panel and pull the trigger to click. During gameplay the trigger fires as normal. The laser
  steadies itself, snaps onto the nearest button and holds still while you pull the trigger.
- **Always first person.** When the game swings its camera out to third person (boosting in space, sitting in a
  chair), your view stays in your character's head.
- **Mod menu (Insert).** An in-headset settings menu, grouped into pages so everything fits: View and comfort, UI and
  HUD, Graphics and performance, Controls, Weapon and hands, Sound and vibration.
- **UI styles.** A flat or curved panel that stays put, eases after you, or follows your face, or a **Helmet**
  display: the HUD sits on your visor (head-locked, wide and curved, slightly lagging like a helmet HUD).
- **Two-handed guns.** With "Weapon in hand" on, bring your left hand to the gun's fore-end (two blue rings show
  where) and hold grip to steady and aim the gun with both hands.
- **Feel.** Head-tracked game audio, controller vibration, colour grading presets.
- **Performance controls.** VR graphics levels and a one-click preset for mid-range cards.
- **DLSS in VR.** The game's built-in NVIDIA DLSS can be forced on for both eyes, for sharper edges or more frame
  rate at high render scales.
- **Comfort.**
  - Head bob, recoil kick and knockback shake are removed from your view.
  - The game never pitches or rolls the horizon.
  - Your view recenters on every level load and camera change.
- **Stereo weapon.** The game draws the first-person weapon with a flat overlay pass; the mod renders it with the
  eye cameras instead, so it isn't seen double.
- **Desktop mirror.** The monitor shows the left eye with the UI on top.
- **Headset hot-plug.** Start the game first and connect the headset whenever you like; the mod keeps retrying.

## Requirements

- Windows 10/11 and the Steam version of Jump Space.
- A PC VR headset with an OpenXR runtime set as active (Virtual Desktop, SteamVR, Oculus/Meta Link, ...).
- [.NET SDK 6.0 or newer](https://dotnet.microsoft.com/download), only when building from source.
- The game must run on **Direct3D 11**. If it starts on DirectX 12, add `-force-d3d11` to its Steam launch options.

## Install

**From a release zip (no .NET SDK needed):** see [Download](#download) above. Then continue at step 4 below.

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
| `Insert`, or hold the right thumbstick click for 1 s | Open/close the mod menu (arrows / left stick to navigate, Enter / A to open a page or change a setting, Esc / B to go back, B on the first page closes) |
| `F7`, hold the left menu button for 1 s, or click both thumbsticks | Recenter: line up your view with where the player is looking. Also happens automatically on level loads and camera changes. |
| `F9` | Write diagnostics to `MelonLoader/Latest.log` |
| `F10` | Save what the headset sees to `UserData/JumpSpaceVR/*.bmp` |
| `F5` / `F6` | UI panel smaller / larger (saved immediately) |
| `F3` / `F4` | UI panel closer / further (saved immediately) |

Mouse, keyboard and gamepads keep working while you play in VR.

With a gamepad (Xbox, PlayStation, Switch Pro, Steam Input):

| Pad | Action |
| --- | --- |
| Everything | The game's own gamepad controls (the right stick looks, the view follows the game camera) |
| Hold View/Select (PlayStation: Share/Create) for 1 s | Open/close the VR menu (left stick or d-pad to navigate, A/Cross to change, B/Circle to close) |
| Click both sticks | Recenter |
| Left trigger | Aim down sights; the weapon lines up with your aiming eye |
| Right trigger | Fire; the pad rumbles (turn off with "Controller vibration") |

Controller layout with motion controls on (default):

| Controller | Action |
| --- | --- |
| Right controller | Aim (point where you want to shoot) |
| Right trigger / left trigger | Fire / aim down sights (game's RT / LT) |
| Left stick | Move (relative to your body) |
| Right stick left/right | Snap or smooth turn |
| Grips | Bumpers (LB / RB) |
| Left grip near the gun's fore-end | With "Weapon in hand" on: hold to grab the gun with both hands (the grip point glows blue when your hand is close); release to let go |
| A / B / X / Y | Face buttons |

## Settings

Settings live in `<game>/UserData/MelonPreferences.cfg` under `[JumpSpaceVR]`. The file is created on the first
launch with the mod.

| Setting | Default | Description |
| --- | --- | --- |
| `StartInVr` | `true` | Start VR automatically once the game has loaded. |
| `PositionalTracking` | `true` | 6DoF. Set to `false` for rotation-only tracking. |
| `WorldScale` | `1.0` | Scales your head movement. |
| `RenderScale` | `1.0` | Eye resolution multiplier (0.5–3.0). Lower it for performance; combine high values with DLSS. |
| `MoveUiIntoVr` | `true` | Show the UI panel in the headset. |
| `UiDistance` / `UiWidth` | `1.6` / `1.9` | UI panel distance and width, in meters. |
| `ControllersAsGamepad` | `true` | Use VR controllers as a gamepad. |
| `ControllerMode` | `Auto` | `Auto` uses whatever you touched last (VR controllers or a gamepad); `MotionControllers` or `Gamepad` always uses that one. |
| `AimEye` | `Right` | While aiming down sights (left trigger), line the sights up with this eye: `Right`, `Left` or `Center`. |
| `ViewDistance` | `1.0` | Draw distance multiplier (level of detail and far clip), 0.5–4. Higher costs frame rate. |
| `ShowFrameRate` | `false` | Show the frame rate in the top-left of the UI panel. |
| `GraphicsQuality` | `Game` | VR graphics: `Game` keeps the game's settings; `High` drops motion blur, depth of field and lens effects; `Medium` also SSR/SSGI, contact shadows, volumetric clouds; `Low` also volumetric fog, ambient occlusion, refraction. The menu also has **Preset: slower graphics card** (Low, DLSS Performance, render scale 1.0, view distance 0.75). |
| `ColorPreset` | `Off` | Colour grading on top of the game's look: `Vivid`, `Cinematic`, `ColdSpace`, `Warm`, `Bright`, `Noir`. |
| `Haptics` / `HapticStrength` | `true` / `0.7` | Controller vibration when firing (both hands when holding the gun two-handed), grabbing the fore-end and clicking menus. |
| `HeadTrackedAudio` | `true` | Sound follows your head instead of the game camera. |
| `HelmetUiWidth` | `1.3` | Width of the Helmet display (m). |
| `LeftHandRoll` | `-90` | Turns the free left hand for a natural rest pose (try 90 if it looks backwards). |
| `Dlss` | `Game` | NVIDIA DLSS for both eyes: `Game` follows the game's graphics option; `Off`, `Quality`, `Balanced`, `Performance`, `UltraPerformance` force it. At high render scales `Performance` gave about +20% frame rate in testing. |
| `CurvedUi` | `false` | Bend the UI panel around you. |
| `ShowCrosshair` | `false` | Show the game's crosshair on the UI panel (it doesn't line up with a gun aimed by hand). |
| `HideCinematicBars` | `true` | Hide the game's black letterbox bars in VR, where they would float in front of you. |
| `PointerAssist` | `true` | The laser steadies itself, snaps onto the nearest button and holds still while you pull the trigger. |
| `ForceFirstPerson` | `true` | Keep the view in your character's head when the game switches to a third-person camera. |
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
- **Session report.** While VR runs, the mod writes a `[session]` line to `MelonLoader/Latest.log` every 5 minutes
  and when VR stops or the game closes: frame rate, worst frame, how much of the time controllers were tracked,
  trigger pulls and laser clicks, whether the weapon sat in your hand, and the worst view jolt. Include it in reports.
- **Low frame rate.** In the mod menu, use **Preset: slower graphics card**, or lower `RenderScale`, set `VR graphics`
  to Medium/Low and DLSS to Performance. In SteamVR / Steam Link, a 72 or 90 Hz refresh rate is easier to hold than
  120 Hz. Lower `RenderScale`. Each eye is a full HDRP render, so expect roughly twice the GPU cost of
  flat play.
- **Anything else.** Run `scripts\uninstall.ps1 -GamePath "<game folder>"`, then use Steam's *Verify integrity of
  game files* to return to the unmodded game.

## Known issues

- **Markers over objects and players.** These are drawn in screen space, so they sit on the UI panel and don't
  line up exactly with the 3D objects they label. Unity renders all overlay canvases in one pass, which rules out
  splitting the markers from the HUD cheaply. The panel follow modes and size settings help with readability.
- **Weapon in hand.** The gun moves to your right controller and the first-person arms bend to follow (the body
  stays where the game puts it; arms stretch a little when your hand is further away than the character's arm).
  It only applies while you hold an item (the hangar and missions, not the prologue opening). If the grip sits
  wrong, use the weapon offset sliders in the mod menu; turn "Weapon in hand" off to keep the game's own placement.
- **Room-scale movement.** By default, walking around your room moves your view but not your character. Turn on
  "Room-scale walking" in the mod menu (experimental) to make the character follow your physical steps.
- **Performance.** Each eye is a full HDRP render. If gameplay feels juddery, lower "Render scale" in the mod menu.
- **No controller vibration.** The game never sends rumble to gamepads, so there is nothing to forward.
- **SteamVR overlays.** The SteamVR dashboard and overlay tools such as OVR Advanced Settings can take focus at
  startup. Close the dashboard if the game appears frozen in the headset.
- **Partly tested on hardware.** The headset view, menus and recentering were tested on a Quest 3 over Steam
  Link. Automated scenario tests cover the laser pointer, motion aiming, turning and the trigger on the mock
  runtime. Weapon-in-hand placement, comfort and frame rate in long sessions still need headset play. Reports are
  welcome; include the `F10` snapshots and `MelonLoader/Latest.log`.

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

### Scripted VR tests (no headset, no person)

`scenarios\*.txt` are scripted tests. The mod plays them against the mock runtime: it moves the simulated head and
hands, presses controller inputs and keys, and checks the mod's state. Run one:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\dev\run-scenario.ps1 -Scenario scenarios\menu-and-pointer.txt
```

It prints a `PASS`/`FAIL` line per check, and the exit code is the number of failures.

Run them all (about 12 minutes; add `-IncludeSoak` for the 15-minute soak test):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\dev\run-all-scenarios.ps1
```

| Scenario | Checks |
| --- | --- |
| `smoke-hands.txt` (~30 s) | VR starts, the weapon fix is hooked, simulated hands are tracked where the script puts them |
| `menu-and-pointer.txt` (~1 min) | Main menu reached; the laser hits and snaps onto "Play Prologue"; a trigger click loads the prologue |
| `aim-and-turn.txt` (~3 min) | In the level: the camera follows controller aim (yaw and pitch); snap turn; body-relative stick; the trigger fires; at least 45 fps |
| `comfort.txt` (~3 min) | Walking stairs with "Stabilize view" on keeps vertical jolts low (about 7 cm/s vs 230+ off) |
| `weapon-in-hand.txt` (~2 min) | In the hangar: gun on the right controller, arms follow (hand error under 3 cm), the body stays under the head when leaning and aiming sideways, grab the fore-end with the left hand, steer the gun with both hands, let go |
| `mod-menu.txt` (~1 min) | Holding right stick click opens the VR settings menu on its category page; A opens a category, the stick changes settings, B goes back, then closes |
| `vr-toggle.txt` (~1 min) | F8 turns VR off and on three times; the session restarts cleanly and hands, laser and weapon fix work afterwards |
| `hangar-options.txt` (~2 min) | Enters the hangar (gun in hand): gun drawn by the eye cameras, sights move to the right eye while aiming, view distance, frame-rate counter |
| `ui-modes.txt` (~1 min) | Curved panel still takes laser clicks; the helmet display follows the head |
| `dlss.txt` (~2 min) | DLSS runs on both eye cameras when forced on (prints frame rate off / Quality / Performance at render scale 2) |
| `feel-and-look.txt` (~2 min) | In the hangar: head-tracked audio updates, vibration on firing and grabbing, colour grading on/off |
| `gamepad.txt` (~2 min) | In the hangar with a scripted gamepad: using it turns motion aim off and the view follows the camera; firing rumbles; left trigger shifts the sights; View held opens the menu; the VR controllers take over again; `Gamepad` mode keeps the pad |
| `graphics-quality.txt` (~2 min) | VR graphics Low switches volumetrics off and Game restores it; prints frame rates per level |
| `first-person.txt` (~2 min) | In the hangar: normal first-person play never triggers the third-person override; "level" is the world there |
| `soak.txt` (~18 min) | 15 minutes walking and turning in the level: frame rate above 45, no frame over 100 ms, memory printed per lap (measured 91-95 fps, worst 22 ms, 5.0-5.15 GB with no steady growth) |

The steps are documented in `src/JumpSpaceVR/Testing/ScenarioRunner.cs`. They are `wait`, `waitfor`, `head`,
`hand`, `input`, `pad` (a scripted gamepad), `key`, `keyuntil`, `expect`, `config` (changes a setting for that run only), `snapshot`,
`diagnostics`, `recenter`, `log` and `quit`. Metrics such as `cameraYaw`, `aimYawOffset`, `laserU`, `cutscene`, `fps`,
`memoryMb`, `eyeJolt` and `setting.<Name>` (any mod setting) are defined in `JumpSpaceVrMod.ScenarioMetric`.

Other simulators, for checks the mock runtime can't do:
- **SteamVR null driver**: real compositor, simulated headset (`test-cycle.ps1 -RuntimeManifest`). Add
  VirtualMotionTracker for scripted controllers.
- **Meta XR Simulator + Meta XR Operator**: Quest-like runtime controllable over MCP. It requires accepting
  Meta's developer license.

`scripts\package.ps1` builds a release zip into `dist/`.

`-RealRuntime` tests against your active runtime instead. `-RuntimeManifest <path>` tests against a specific
runtime, for example SteamVR's `steamxr_win64.json` with its null driver enabled. That gives a real compositor
with a simulated headset.

`Jump Space VR mod research.md` contains the background feasibility research.

## License

MIT, see [LICENSE](LICENSE). This is a fan-made mod, not affiliated with or endorsed by Keepsake Games. It ships no
game files.

## Third-party components

- **MelonLoader** (Apache-2.0) is downloaded from its GitHub releases.
- **Unity OpenXR plugin** (Unity Companion License) is downloaded from Unity's package registry at install time. It
  isn't redistributed by this repository.
