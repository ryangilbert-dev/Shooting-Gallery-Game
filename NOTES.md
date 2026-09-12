# Shooting Gallery Duel — Editor Notes

A running reference for things that come up while working in the Unity Editor.
Claude keeps this updated as we go — if something here goes stale or a new
question comes up a lot, just ask and it'll get added/fixed.

## Finding & tuning the revolver

The gun's position/rotation live on **`PlayerPrefab`**:
```
Assets/Prefabs/Networking/PlayerPrefab.prefab
```

**To edit it directly (not live):**
1. Double-click `PlayerPrefab.prefab` in the Project window — opens it in Prefab Mode.
2. Select the root object in the Hierarchy.
3. In the Inspector, find the **`Player Weapon`** component:
   - `Revolver Local Position/Euler Offset` — third-person, on your character's hand (what other players see)
   - `Viewmodel Local Position/Euler Offset` — first-person, on your own camera (what you see)
   - `Upper Arm/Forearm Raise Euler` — currently unused (zeroed out)
   - `Viewmodel Muzzle Offset` — where the red tracer visually starts (an approximate barrel-tip
     guess, since the gun mesh has no modeled muzzle point); tune live the same way

**To tune it live while playing** (both offset pairs update every frame, so this works):
1. Press Play, get into the Gallery, press **E** to ready the revolver.
2. In the **Hierarchy** search box (magnifying glass icon, top of the panel), type `Player`.
3. Select **`PlayerPrefab(Clone)`** — this only exists once you've hosted/joined and your
   player has actually spawned.
4. Find `Player Weapon` in the Inspector and drag/type new numbers — watch it update instantly.

**Important:** changes made *during* Play mode are thrown away the moment you press Stop.
Write down the numbers that work, stop Play, reopen the prefab, and type them in for real.

**Don't** try to rotate the spawned gun object itself (the child GameObject under your
camera) directly in the Scene view — it re-applies from `Player Weapon`'s fields every
frame, so any manual tweak there snaps back almost instantly.

## Hit tracker bar & wall drop

Each lane has its own hit-tracker bar (bottom-center of your screen, gold fill on a dark
background) that fills by 10% per landed shot. At 100% it resets to empty and drops the
`DividerWall` between the two galleries for a few seconds before it rises back up.

- Tuning knobs live on **`MatchManager`** (the scene object in `Arena.unity`, not a prefab):
  `Hits Per Bar Fill` (default 10) and `Wall Drop Duration` (default 5 seconds).
- The wall's own drop distance/speed are tunable on **`DividerWall`**'s `Wall Controller`
  component.
- The bar itself is owner-only and built entirely from code at runtime (`HitTrackerHUD.cs`) -
  there's no Canvas/prefab UI to go find; it just appears once you've readied up and joined a
  lane.

## Common gotchas

- **"No cameras rendering" warning on Play**: normal if you're in `MainMenu` before hosting —
  neither `Bootstrap` nor `MainMenu` has a gameplay camera; the only one lives on `PlayerPrefab`
  and only activates once you actually spawn. It should clear the moment you click Host.
- **Pressing Play does something weird / null reference on Host**: shouldn't happen anymore —
  Play mode is now forced to always start from `Bootstrap.unity` regardless of which scene you
  had open (`PlayModeBootstrap.cs`), matching how a real build always behaves.
- **Something breaks right after connecting**: check the **Console window** (Window > General >
  Console) for red error text the moment it happens — this has been the fastest way to find the
  real cause every time so far, way faster than guessing from symptoms alone.

## Useful custom menu items

Under **Tools > Shooting Gallery** in the Editor menu bar:
- **Rebuild Arena Scene Only** / **Rebuild Main Menu Scene Only** — regenerates just that scene
  from code, without touching the other scenes or `PlayerPrefab`.
- **Setup Random Character Visuals**, **Setup Player Movement**, **Setup Revolver**, **Fix
  Character Scale** — one-shot patches for `PlayerPrefab`; safe to re-run.
- **Capture Revolver Preview** — renders the revolver prefab to a PNG for a quick visual check
  without needing to test in-game (used heavily to fix its orientation).

Most of the project is actually built/maintained through these scripted tools rather than
hand-edited in the Editor, so re-running one after a related code change is often the way
to apply it.

## Project status

See git log for the detailed history. Rough milestone state as of the last update to this file:
- ✅ Netcode connection (host/join, lane assignment)
- ✅ Random character models, correctly scaled
- ✅ WASD/mouse movement, eye-height camera
- ✅ Bar + Gallery rooms, colored, doorway sized right
- ✅ Revolver draw/holster (E), 6-shot ammo (click to fire, R to reload), correctly posed
- ✅ Six practice targets, server-validated hits, turn green and auto-reset
- ✅ Arcade shot feedback: recoil kick + red cylinder tracer from the barrel
- ✅ Hit tracker bar per lane, fills 10% per hit, drops the divider wall for 5s at 100%
- ⬜ The duel aim/dodge exchange itself (M4) not started yet - the wall currently just drops and
  rises with nothing happening while it's down
