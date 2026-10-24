# Headset checklist

The automated scenario tests (`scenarios/`, run against Unity's OpenXR mock runtime) cover the mod's logic. These
checks need a person in a headset. Tick them off and report anything odd with `MelonLoader/Latest.log` and the `F10`
snapshots.

## Confirmed on a Quest 3 (Steam Link)

- [x] Stereo view, menus, recentering, laser pointer
- [x] Weapon drawn once, aim down sights with the right eye
- [x] Weapon in hand, two-handed grip
- [x] Gamepad switching

## Still to confirm

- [ ] Frame rate at VR graphics **Low** feels better than before 0.9 (shorter shadows, no object motion vectors)
- [ ] **Galaxy map**: shown in front of you at its angle; the sticks steer it to a destination
- [ ] **Body**: no clipping or swaying shoulders when leaning or aiming sideways
- [ ] **Gamepad + room-scale**: running with the pad, the view stays with the body
- [ ] **Ship roll**: someone flies and rolls the ship; your view stays level with the floor (log: "Level follows
      your character's floor")
- [ ] **Boosting / sitting**: you stay in first person (log: "Game camera went third person")
- [ ] **Laser**: snaps onto buttons, doesn't drift while pulling the trigger
- [ ] **Menu**: category pages, gamepad doesn't move the character while the menu is open
- [ ] **Comfort vignette** (optional): darkens only with stick movement and turning
- [ ] **Real crouch** (optional): ducking crouches, view stays at your head height; recenter while standing
- [ ] **Weapon in hand** after spawning aboard a ship (the gun on the hand socket)

## Co-op hands (beta)

- [ ] You, a friend and the host (all with the mod) turn on "Multiplayer hands (co-op)"; each sees the others'
      hands move. Send both players' `Latest.log` (lines starting "Multiplayer hands:").
- [ ] Joining a host **without** the mod: no errors, no disconnects. If both hold, co-op hands can be on by default.
