# Player feedback log

Feedback collected from players, newest first. Status: **logged** (not started), **in progress**, **fixed in x.y.z**.
Work on these starts when the maintainer says to resume.

## 2026-10-24: Discord video and a player with an RTX 4080 (v0.11.x)

Source: a player recorded the mod and posted it on Discord
([video](https://www.youtube.com/watch?v=SFjZLRDXAkk)); written feedback from a player on an RTX 4080 (positive
overall), plus the maintainer's own notes.

Performance on an RTX 4080: fine, no complaints.

| # | Report | Status | First guess at the cause (unverified) |
| --- | --- | --- | --- |
| 1 | **Hard to aim and press "use/interact"** on things: pushing buttons, picking up weapons. | fixed in 0.12.0 (look to interact; confirm in a headset) | The game picks the interaction target with a ray from its camera, and with motion controls that camera follows the right controller, not the head. Pointing the gun at a small button is fiddly, and Interact is X on the left controller. Options: interaction ray from the head (gaze) instead of the hand; a visible marker on the target; a larger target radius. |
| 2 | **The in-game body doesn't follow the headset direction**, so the right stick is needed a lot to turn. | fixed in 0.12.0 (body follows head) | With motion controls the body yaw only changes with the right stick (snap or smooth turn); turning the real head or body never turns the character. Option: body slowly follows the head when it is turned past a threshold (common in VR shooters), as a setting. |
| 3 | **Interaction prompts aren't visible** (for example "Press X to grapple" when looking at a grapple point). | fixed in 0.12.0 (same cause as 1: prompts followed the gun; confirm in a headset) | Possibly hidden together with the crosshair: the mod hides `PF_FirstPersonHUD/Crosshairs` when "Show crosshair" is off, and the prompts may live in that group. Or they are drawn at screen centre where the UI panel or helmet HUD puts them out of view. Check the HUD hierarchy around a grapple point. |
| 4 | **The left shoulder isn't attached to the body** and clips into the view. | improved in 0.12.0 (body behind the eyes, shoulder follows the free hand; confirm in a headset) | Weapon-in-hand IK: the left arm is bent and stretched (up to 1.4x) toward the left controller while the body is placed under the head, so the shoulder/upper arm can swing into the camera. Check the left shoulder bone (LeftShoulder is not moved by the IK; LeftArm is) and clamp the stretch or keep the upper arm out of the eye's near zone. |
| 5 | **A black box around the picture when recording** (the desktop view people record). | fixed in 0.12.0 (desktop view crops the eye image's edges; not reproducible on the test runtime) | The desktop mirror shows one eye's image, which doesn't fill the window's aspect ratio, so the rest stays black. Options: crop/scale the eye image to fill the window, or a dedicated "spectator" camera for recording. Check the video for which it is. |

### What was found

- 1 and 3 had one cause: the game picks interaction targets and shows prompts where its camera looks, and with motion
  aiming that was the gun. The hangar's crosshair group (hidden by the mod) holds only the crosshair and hit marker.
- 4: the visible piece is the first-person outfit's left shoulder and upper arm about 27 cm from the eyes; the
  animation keeps the left clavicle raised for a two-handed grip even when the left hand is free.
- 5 did not reproduce with the test runtime (with or without DLSS); the video shows black bands at varying edges in
  a few frames, consistent with the eye image's edges reaching the window.

### Originally planned

- Watch the linked video for 3 and 5 (where the prompt should appear; what the black box looks like).
- Reproduce 1, 3 and 4 in the hangar with the scenario harness (grapple point, buttons, weapon pickup) and add
  checks for them.
