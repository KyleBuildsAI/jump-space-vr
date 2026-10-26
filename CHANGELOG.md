# Changelog

All notable changes to Jump Space VR. Versions follow [semantic versioning](https://semver.org/); downloads are on
the [Releases](https://github.com/KyleBuildsAI/jump-space-vr/releases) page.

## 0.12.0

From player feedback on the first public video (thanks!):

- **Look to interact**: lower the gun and the game looks where you look, so "press X to grapple" and other prompts,
  pickups and buttons follow your eyes instead of the gun. Raise the gun or pull the trigger to aim with it again.
- **Body follows head**: turning your head well to one side turns your character with you; walking goes where you
  face and the right stick is needed far less. The view itself never moves.
- **Left shoulder**: the body sits a little behind and below the eyes, and a free left hand lowers its shoulder
  instead of keeping it raised in the two-handed gun pose, so it no longer hunches into the view.
- **Recording**: the desktop window crops the outer edges of the eye image, where black bands showed in recordings.
- F10 snapshots also save the eye images as rendered.

## 0.11.2

- Weapon in hand works when the game hangs the gun on the right hand's socket instead of the item locator (some
  spawns). Before, the mod saw no gun there: no weapon in hand, no firing vibration or rumble.
- While the VR menu is open, the game's own input is held back, so a real gamepad's A/B/sticks no longer also jump,
  crouch or walk.
- Test harness: a timed-out wait saves diagnostics and a snapshot, and every scenario's log is archived.

## 0.11.1

Fixes from a code review of 0.8–0.11:

- Turning VR off (F8) or switching cameras gives the game camera its own DLSS and graphics settings back.
- "Controls: Gamepad" without a connected pad no longer locks out the VR controllers and the menu.
- The first-person body stands on the ship floor in a rolled ship.
- Real crouch: the lift is cleared without a player or with VR off, and measured along the character's up.
- Co-op hands: departed players are dropped, missing bones are retried every 5 s, one bad remote can't stop the
  others, nothing is received while off.
- Comfort vignette ignores room-scale steps and camera swaps; laser snapping does less work per frame.

## 0.11.0

- Optional **real crouch**: duck in the room to crouch in the game; the view stays at your real head height.

## 0.10.0

- Optional **comfort vignette** while the stick moves or turns you.

## 0.9.3

- VR graphics Low skips per-object motion vectors (+~10% frame rate in the hangar).

## 0.9.2

- **Galaxy map**: shown at its camera's downward angle; the sticks steer it.

## 0.9.1

- Shorter sun shadows in VR at Low (40 m) and Medium (80 m), plus a Shadow distance setting (+~10% frame rate).
- Co-op hands made opt-in (beta).

## 0.9.0 (pre-release)

- **Co-op hands**: players with the mod see each other's VR hands (Unity Netcode named messages, host relays).

## 0.8.1

- The laser no longer clicks a neighbouring button it swept across while a menu animated in.

## 0.8.0

- Camera no longer left behind while the body runs ahead (room-scale walking with a gamepad).
- First-person body stands under the head (no swaying shoulders or clipping); left hand rests the right way.
- View stays level with the ship floor when a ship rolls; recentering works upside down.
- First person kept when the game swings to third person (boosting, chairs).
- VR menu grouped into category pages; laser snaps to buttons and holds still while clicking.
- Windows Mixed Reality and HTC Vive controllers.

## 0.7.0

- Real gamepads (Xbox, PlayStation, Switch Pro, Steam Input) next to motion controllers.
- Haptics, head-tracked audio, colour grading presets, VR graphics levels, curved UI, helmet HUD, DLSS for both eyes.
- Fixes from headset play for weapon in hand.

## 0.6.0

- Weapon in hand with arm IK and a two-handed grip on the fore-end.

## 0.5.0

- Aim-down-sights eye, view distance, frame-rate counter, menu key repeat and reset.

## 0.4.x

- Letterbox bars hidden in VR; stereo weapon pass fixes; comfort measurement fixes.

## 0.3.x

- Laser pointer clicks reliably; weapon drawn once; one-click friend installer; automatic OpenXR runtime choice
  (Steam Link/SteamVR, Virtual Desktop, Quest Link).

## 0.2.x

- Motion controls (aim with the controller, body turning), mod menu, UI follow modes, view stabilization.

## 0.1.x

- First stereo VR build: OpenXR injected into the game, 6DoF head tracking, UI on a panel, desktop mirror.
