# Shooting Gallery Duel — Editor Notes

A running reference for things that come up while working in the Unity Editor.
Claude keeps this updated as we go — if something here goes stale or a new
question comes up a lot, just ask and it'll get added/fixed.

## Joining over the internet with a room code (Unity Lobby + Relay) - needs one-time setup

Hosting/joining used to be direct-IP only, which only works on the same network (or with manual
router port-forwarding, which most home networks/ISPs make impractical - see the git conversation
this came out of). Rebuilt on **Unity Relay** for NAT traversal, wrapped in **Unity Lobby** for the
actual code a human types in (see "Lobby wraps Relay" below for why both, not just Relay alone) -
the host creates a room and gets a short 6-character code (letters+numbers, e.g. `K3F9P2` - not
purely numeric, a deliberate simplification over building a custom numeric-code layer on top); a
friend types that code into Join and everything else (NAT traversal, exchanging the real Relay
connection details) happens automatically - no IP address, no port forwarding, works over the real
internet. `ConnectionManager.StartHost()`/`StartClient(ip)` (direct-IP, LAN-only) still exist
unchanged alongside `StartHostWithLobbyAsync()`/`StartClientWithLobbyAsync(code)` (what the menu
actually calls) and the lower-level `StartHostWithRelayAsync()`/`StartClientWithRelayAsync(code)`
they're built on top of - `WallDropDiagnostic` still uses the direct-IP path for its own
single-machine automated test.

**This needs setup only you can do before any of it will even compile:**
1. Sign into Unity Hub with a Unity ID (a free account is fine).
2. In the Editor: **Edit > Project Settings > Services** - link this project to a Unity Cloud
   project (creates one on the free tier if you don't have one yet; no credit card needed for
   Relay/Lobby at this scale).
3. **Window > Package Manager** - switch the dropdown to "Unity Registry", search for and install
   **Authentication** and **Multiplayer** (`com.unity.services.multiplayer` - this single package
   bundles Relay, Lobby, and `com.unity.services.core` together; there's no separate standalone
   "Relay"/"Lobby" package to hunt for anymore). `ConnectionManager.cs`/`MainMenuUI.cs` reference
   `Unity.Services.*` namespaces that don't exist until this is installed - **the project won't
   compile until you've done this.**
4. **Tools > Shooting Gallery > Rebuild Main Menu Scene Only** - regenerates the menu with the
   room-code display text, renamed join field (was a plain IP text box originally), and the newer
   **Practice Solo** button (see below).

**How it plays**: click Host - after a moment (signing into UGS + creating the room) a room code
appears on screen (also copied to the clipboard); read/share it however (voice chat, text).
Whoever's joining types that code into the Join field and clicks Join - no IP needed either way.
Click **Practice Solo** instead to skip all of this and jump straight into the gallery alone
against the practice dummy (see "Practice Solo" below).

**Already hit and fixed**: `SetHostRelayData`/`SetClientRelayData`'s last argument turned out to
be a plain `bool` (`isSecure`) in the installed package version, not the `string` connection-type
identifier ("dtls"/"udp") originally guessed from Unity's general Netcode-for-Relay documentation
pattern - a real `CS1503 cannot convert from 'string' to 'bool'` compile error, not a hypothetical
one. Fixed (`ConnectionManager.UseSecureRelayConnection`, `true`). If installing on a different
package version ever produces a *different* signature mismatch, it'll show up the same way -
isolated to those two calls specifically, not spread across the file.

**Join can now actually fail visibly, instead of hanging on "waiting for host" forever**: a real
join attempt got stuck exactly like that in testing - traced to `MainMenuUI.OnJoinClicked` only
ever proving the join code resolved to a *live Relay allocation*, not that Netcode's own handshake
with the host went on to actually succeed. (Briefly suspected the practice dummy taking the
second player's slot instead - it doesn't; `TrySpawnDummyForSoloTesting` only ever touches
`MatchManager.DummyLane`, never `PlayerBClientId`, and downstream `MatchManager` logic like that
doesn't even run until after this connection already succeeds.) Fixed by watching
`NetworkManager.OnClientConnectedCallback`/`OnClientDisconnectCallback` for our own client ID once
the Relay-level call returns, plus a `Join Timeout Seconds` (default 15) fallback if neither ever
fires - now a dead/expired host or a version/prefab mismatch (`ForceSamePrefabs` silently rejecting
a client running different code than the host) surfaces as a real "Failed to join - ..." message
instead of a silent hang. **Not yet playtested** - written from the code alone.

**A lone Relay host only gets ~60 seconds to get a friend connected before Relay tears the
allocation down - a hard Unity server-side policy, confirmed via a Unity staff reply on the Unity
Discussions forum, not adjustable from any client-side setting.** Hit in real testing: a friend
tried to join and the code no longer worked. That's an unreasonable race against a human reading a
6-character code out of a chat message and typing it in - so rather than fight the timeout, **Lobby
wraps Relay** to remove the human from that race entirely:
- `ConnectionManager.StartHostWithLobbyAsync` creates a **Lobby** first (kept alive indefinitely by
  an ordinary 15-second heartbeat timer in code - trivial to satisfy no matter how long the human
  side takes, unlike a person's typing speed) and only creates the actual Relay allocation the
  instant a second player has already joined that lobby, ready to consume it near-instantly - the
  Relay code itself never gets shown to, or typed by, a human at all anymore; it's exchanged
  automatically through the lobby's own data (`relayJoinCode`, visible to lobby members).
  `StartClientWithLobbyAsync` mirrors this on the joining side: join the lobby by its code, poll
  for the host to publish that Relay code, then hand off to the existing
  `StartClientWithRelayAsync` unchanged.
- The code a human actually shares/types now is the **Lobby code**, which doesn't race any
  60-second cutoff. `MainMenuUI.OnHostClicked` still copies it to the clipboard
  (`GUIUtility.systemCopyBuffer`) as a convenience for pasting into chat/voice text, but that's no
  longer a race against a timer, just a nicety.
- Generous outer timeouts exist so nobody's left waiting forever with no feedback if the other side
  never shows up: 5 minutes for the host waiting for a lobby join, 30 seconds for the client waiting
  for the host to publish the Relay code once it's joined the lobby - both surface as a normal
  "Failed to..." message via the existing catch blocks, not a silent hang.
- Sources confirming the original 60-second figure this was built to route around:
  [Relay client timeouts](https://docs.unity.com/ugs/en-us/manual/relay/manual/client-timeouts),
  [Unity Discussions - Relay connection failure after exactly one minute](https://discussions.unity.com/t/relay-connection-failure-after-exactly-one-minute/1624125)
  (Unity staff: "The default time to live is set to be 60 seconds when the host is alone").
- **Already hit and fixed**: the very first version of this had the code never appear anywhere
  during the wait at all - `MainMenuUI.OnHostClicked` only displayed/copied the code from
  `StartHostWithLobbyAsync`'s *return value*, but that method deliberately doesn't return until a
  second player has already joined, which is exactly the period a host needs to see and share the
  code. Fixed with a separate `onRoomCodeReady` callback, fired the instant the lobby (and
  therefore the code) actually exists, well before the method returns - `onStatusUpdate`'s
  "Waiting for a player to join..." message and the room code display are two independent UI
  elements now updating independently, not one waiting on the other.
- **Confirmed working in a real two-machine internet test** - the Lobby APIs (`CreateLobbyAsync`,
  `JoinLobbyByCodeAsync`, `SendHeartbeatPingAsync`, etc.) came from the same already-installed
  `com.unity.services.multiplayer` package Relay does, and no new package/project setup was needed
  beyond what Relay already required, exactly as inferred from reading the package source ahead of
  time. The join code no longer racing a 60-second cutoff was confirmed in practice, not just in
  theory - see "Debugging network/Relay/Lobby issues" below for the fuller story of what else got
  found and fixed along the way to a working end-to-end connection.

**"Practice Solo" - a side effect of the Lobby change worth knowing about**: the old Host button
used to load straight into the Arena scene immediately, which incidentally doubled as the easy way
to reach the practice dummy solo (see "Duel: lives, headshot-only hitbox, and the practice dummy"
below). Since `StartHostWithLobbyAsync` now deliberately blocks until a real second player has
joined the lobby, that shortcut disappeared - so a **third button, "Practice Solo"**, was added
alongside Host/Join specifically to keep it. It calls the old plain `ConnectionManager.StartHost()`
(direct-IP, no Relay/Lobby round trip at all, since nobody else is ever going to connect) and loads
Arena immediately, exactly like the original Host button used to. Needs **Rebuild Main Menu Scene
Only** to appear (see setup steps above) - **not yet visually confirmed** in the Editor.

**Room code stays visible in-game, not just on the menu**: `MainMenuUI`'s own room code display
only exists for the few seconds before the Arena scene loads - once you're actually standing in
the bar, that menu screen (and its Text object) is gone. `ConnectionManager.LastHostJoinCode`
holds onto the code from the most recent successful host flow (it's on a `DontDestroyOnLoad`
object, so it survives the scene switch), and a `RoomCodeHUD` component (same runtime-built-HUD
pattern as `HitTrackerHUD`/`LivesHUD`) shows it top-left of the screen - host-only, and only while
`MatchManager.CurrentPhase` is `WaitingForPlayers` *and* nobody's connected as the second player
yet (i.e. still genuinely alone in the bar; it hides itself once a second player connects or both
walk into the gallery, and reappears if `ServerResetMatch` sends everyone back). Blank for a
joining client, and blank for "Practice Solo" hosting, since neither ever sets `LastHostJoinCode`.
In practice, since `StartHostWithLobbyAsync` doesn't even load this scene until a second player has
already joined, this component rarely has long to show anything now - by design, reaching the bar
as a Lobby host usually means that player's Netcode handshake is already close behind.
- **One-time setup step**: run **Tools > Shooting Gallery > Setup Room Code HUD** once - patches
  `PlayerPrefab` with the new component. Safe to re-run.
- **Not yet visually confirmed** - written from the code alone, not seen in a real Play session
  yet.

Also: if the whole **`Tools` menu vanishes** from the Editor's menu bar, that's not a separate
bug - `Tools` is entirely custom, generated by this project's own `[MenuItem]`-attributed editor
scripts, and Unity only builds it when *all* of them compile cleanly. It disappearing is actually
the most useful signal there's a compile error worth checking the Console for (exactly how this
one was caught).

**Confirmed working end-to-end** - a real two-machine host/join over the internet via the
Lobby-wrapped flow, after tracking down and fixing the Relay connection failure detailed just
below.

**Real two-machine test hit a genuine Relay connection failure - separate from anything Lobby
touches - and it's now fixed.** The Lobby half actually worked correctly that run (the joining
player did get into the lobby, since the host's wait loop detected them and moved on) - what
failed was the *next* step, the host's own `NetworkManager.Singleton.StartHost()` call inside
`StartHostWithRelayAsync`, which threw a native "Failed to establish connection with the Relay
server." followed by "Transport failure! Relay allocation needs to be recreated" and a host-side
shutdown. `ConnectionManager.HandleTransportFailure` did its job throughout this - it caught the
failure and returned the host to the main menu instead of leaving anyone stuck - but the
underlying connection itself wasn't working, so retrying kept failing the same way until the real
cause was found.

This is a well-known Unity Relay error message with **more than one documented root cause**, found
searching real reports of the identical text - it is not something the Lobby work above could have
caused, and the fix confirms it wasn't the Lobby side at fault:
- **Confirmed root cause for this project: the DTLS (encrypted) handshake.** Flipping
  `ConnectionManager.UseSecureRelayConnection` from `true` to `false` (plain UDP through the same
  Relay servers instead) fixed a real two-machine connection on the very next test - this specific
  network/environment combination could complete a plain UDP relay connection but not a DTLS one.
  Left `false` going forward; see the comment on that constant for what would need re-testing if
  DTLS ever needs to come back.
- Other documented causes, ruled out here but worth knowing about if this error resurfaces somewhere
  DTLS isn't the culprit: **a firewall/router blocking the actual Relay data port** (as opposed to
  the fixed port 7778 Unity's QoS region-selection ping uses beforehand to pick the nearest server -
  which is why that QoS step, visible in the log as a burst of "QosJob: send to X:7778" lines,
  ~278ms, 55/55 responses, can succeed completely while the *real* connection right after it fails);
  confirmed as the cause in [this Unity Discussions thread](https://discussions.unity.com/t/failed-to-establish-connection-with-the-relay-server/940680).
  Also reported elsewhere: a `com.unity.transport` package version/install issue, fixed by
  reinstalling/pinning a different Transport version in
  [this thread](https://discussions.unity.com/threads/failed-to-establish-connection-with-the-relay-server.1549967/).

**This is also the real explanation for a confusing-looking symptom that showed up along the way,
not a separate bug**: while this was still unresolved, a
retried host attempt (after their Relay connection failed and `HandleTransportFailure` quietly
dropped them back to the menu) creates a **brand-new Lobby with a brand-new code** -
`StartHostWithLobbyAsync` has no memory of the previous attempt. A player still waiting on the
*old* code is waiting on a lobby nobody is heartbeating or publishing a Relay code into anymore -
from their side it just looks like being permanently stuck, not like "the host had to restart."
`HandleTransportFailure` now also deletes that abandoned lobby immediately (previously it was only
implicitly cleaned up later, once its heartbeat lapsed) so a stale lobby doesn't linger looking
current for even a little while - but the underlying confusion (host silently gets a new code,
the other player has no way to know) is a real UX gap that only the Relay connection itself
actually working reliably first-try, or some kind of "the host is back, here's the new code"
signal, would fully close. Worth a look later if retries stay common even once the Relay
connection issue itself is resolved.

**A downstream symptom of the same root failure, now fixed on its own merits**: once the host's
connection died, the joining player's console showed two different Lobby errors from retrying
Join - `player is already a member of the lobby` (409 Conflict, from clicking Join again with the
same identity after an earlier attempt had already added them to the lobby) and later `lobby not
found` (404, once the abandoned lobby's heartbeat lapsed and Unity's servers cleaned it up for
real). The 404 is accurate and expected once the lobby is genuinely gone - nothing to fix there,
the host needs to re-host. The 409 was a real gap though: `StartClientWithLobbyAsync` now catches
that specific conflict and recovers by looking up the lobby it's apparently already a member of
(`GetJoinedLobbiesAsync`) instead of just failing - so a retry after some other failure doesn't get
permanently stuck on "you're already here" for an identity that persists across attempts (Unity's
anonymous auth caches the same player ID locally). **Not yet playtested** - written from the code
and the exact error text/reason codes seen in this session's testing.

**Confirmed in an actual play session: Relay allocations don't last forever.** After a while
(a real session, not a hypothetical), hosting failed with "Failed to establish connection with
the Relay server" / "Transport failure! Relay allocation needs to be recreated" and the host shut
down, stranding whoever was connected. That's a known Unity Relay characteristic (allocations have
a limited lifetime) rather than a bug - the gap was that nothing recovered from it. Fixed with a
`NetworkManager.OnTransportFailure` handler in `ConnectionManager` that shuts down cleanly and
drops back to the main menu instead of leaving the game stuck - hosting/joining again just creates
a fresh allocation (no need to re-sign-in). **Same signature-uncertainty caveat as
`SetHostRelayData` above**: `OnTransportFailure`'s exact delegate shape is assumed from the
package's own log message telling you to use it, not verified against source - if it doesn't
compile, the fix will be isolated to that one handler.

**What actually keeps this from happening in the first place, not just recovering from it**:
the same session's log also showed continuous active play (shots fired, reloads) right up until
the disconnect - not an idle timeout, which points at a specific, fixable cause rather than a hard
"Relay sessions just expire" wall. Unity throttles an unfocused window's update rate hard by
default; if you're testing host + client as two instances on one PC, whichever window doesn't
have focus gets starved enough to miss the Relay connection's keep-alive entirely, even while the
*other* instance is actively playing. `ConnectionManager` now forces `Application.runInBackground
= true` in code (not just the Player Settings checkbox, which is easy to forget/toggle back off)
specifically to close this off. **Be honest about the limit here**: this fixes the most likely
practical cause for a same-PC test setup, but it can't guarantee zero disconnects ever - a real
network hiccup or an actual hard Relay allocation lifetime limit would still end a session, and
recovering from *that* seamlessly (auto-reconnect without both players needing to manually
host/join again) would need real infrastructure on top of this (a persistent Lobby the game stays
connected to independently of the game session, to hand out a fresh join code without a human in
the loop) - out of scope unless it turns out to matter in practice.

**Also cleaned up**: the "There are 2 audio listeners in the scene" warning spam right after
hosting/joining - a real (if harmless) transient overlap between the menu's own `AudioListener`
and the newly-spawned player's own, since Netcode's scene sync takes a few frames to unload the
menu scene, and `PlayerController` enables the new listener immediately on spawn. Fixed by having
`MainMenuUI` disable the menu's listener itself the instant Host/Join is clicked, rather than
waiting on the scene unload to get to it.

## Debugging network/Relay/Lobby issues - a process that's worked so far

Getting real internet play working took several rounds of hitting a real error, tracking it down,
and fixing it - the same handful of techniques kept being what actually moved things forward each
time, worth reusing whenever more networking issues show up (which, being real internet
infrastructure outside this project's own control, they will):

1. **Figure out which layer an error is actually coming from before guessing at a fix.** A Unity
   Relay/Lobby session has several distinct layers that fail in different ways and need completely
   different fixes: application code (a plain C# exception, e.g. `NullReferenceException`), the
   Lobby service (a `LobbyServiceException` with a `Reason`, e.g. `Conflict` for a 409), and the
   actual Relay/transport connection (native Burst-compiled log lines like "Failed to establish
   connection with the Relay server", unrelated to anything Lobby-side). Treating these as one
   undifferentiated "networking is broken" bucket wastes time chasing the wrong fix - e.g. briefly
   suspecting the practice dummy for a stuck join that was actually a missing Netcode-handshake
   timeout, or almost treating the Lobby "already a member"/"lobby not found" errors as their own
   bug when they were actually just downstream symptoms of the Relay layer failing underneath.
2. **A generic exception message never tells you *where* - always get the real stack trace before
   fixing anything.** `NullReferenceException.Message` is always the exact same "Object reference
   not set to an instance of an object" string no matter which line threw it. Guessing from the
   message alone risks fixing a real-but-wrong gap (which is still worth fixing, just not
   necessarily *the* fix) - the actual line only comes from clicking the error open in the Unity
   Console (or the equivalent in a log file) to see its full stack trace.
3. **Cross-check assumptions against the actually-installed package source, not general knowledge
   of "how Unity Relay/Lobby usually works."** This project's Relay/Lobby packages are bundled
   inside `com.unity.services.multiplayer` under `Library/PackageCache/` - real, readable C# source,
   not a black box. Reading it directly (rather than trusting an API's name/docs to imply its
   behavior) is what caught: `SetHostRelayData`'s final argument being a plain `bool` instead of
   the `string` an older doc pattern suggested (a real `CS1503` compile error); `GetJoinedLobbiesAsync`
   returning a raw, possibly-`null` result instead of an empty list; and that the SDK's own
   internal conflict-retry logic only covers one specific Lobby error reason, not the generic HTTP
   409 this project's own retry code needed to handle itself.
4. **When behavior looks like a real Unity/Relay/Lobby limitation rather than a bug in this
   project's own code, search for the *exact* error text rather than reasoning from general
   knowledge.** These are real, widely-used Unity services - other developers have hit and posted
   about the same exact error strings, sometimes with a Unity staff reply giving the precise
   number/cause (this is how the 60-second "host alone" Relay timeout was confirmed as a real,
   hard-coded server policy rather than a guess, and how "Failed to establish connection with the
   Relay server" turned up more than one documented real cause). Always note the source link in
   NOTES.md alongside the finding, not just the conclusion, so it can be re-checked later.
5. **Distinguish a genuinely new bug from a downstream symptom of an already-diagnosed root
   cause.** The Lobby "already a member"/"lobby not found" errors, and later "player 2 stuck on an
   old code while player 1 loads in with a new one," both turned out to be consequences of the
   same still-unresolved Relay connection failure cascading through retries - not separate bugs
   needing their own fixes. Tracing the actual causal chain (what called what, in what order,
   given what's already known to fail) before writing a fix avoids solving symptoms one at a time
   forever instead of the actual cause.
6. **Prefer cheap, clearly-labeled, easily-reversible diagnostic changes over large speculative
   rewrites when the suspected cause is a real-world network/environment condition that can't be
   verified without an actual live test.** Flipping `ConnectionManager.UseSecureRelayConnection`
   (DTLS on/off) - a one-line, well-commented toggle - to test a specific documented cause of the
   Relay connection failure, is the concrete example: cheap to try, cheap to revert, and it
   confirmed the real cause on the very next test instead of a much larger, unproven change.
7. **Write down what was found (including dead ends) as it's found, not just the final fix** -
   several sections in this file above are exactly that trail, and it's what made it possible to
   correctly recognize step 5's "same root cause, different symptom" pattern instead of re-diagnosing
   from scratch each time a new-looking error showed up.

**Confirmed working end-to-end**: a real two-machine internet host/join, after setting
`UseSecureRelayConnection` to `false` - the DTLS handshake was the actual cause of "Failed to
establish connection with the Relay server" for this specific test. Left `false` for now since
it's confirmed to matter here; revisit re-enabling DTLS (`true`) only if encryption of the relay
traffic itself becomes a real requirement, since turning it back on is exactly what would need
re-testing against this same failure.

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

**Reload now takes `Reload Duration` seconds (default 3)** instead of being instant - you can't
fire again until it finishes, and mashing R while already reloading doesn't restart or stack the
countdown. While reloading, the revolver eases into a tilted-down pose (`Reload Tilt Angle`/
`Reload Tilt Speed`) - synced via `PlayerWeapon.IsReloading`, so it shows on the third-person
model for whoever's watching you too, not just your own viewmodel. **The tilt's exact direction
isn't confirmed** - same "guessed pending a live look" situation as the arm-raise pose above; if
it tilts the barrel up instead of down, flip `Reload Tilt Angle`'s sign.

## Character walk animation

Each character in the PSX-Western pack ships with a separate `{Name}Anim.fbx` file, but nothing
used to play it - characters just glided around in a static pose. Now wired up via **Tools >
Shooting Gallery > Setup Character Walk Animation** (run once; safe to re-run).

The reason this needed a whole tool instead of just dropping an Animator on: the base model
(`{Name}.fbx`) is rigged with Reallusion's `CC_Base_*` bone names, but the separate animation file
uses Mixamo's `mixamorig:*` names - a completely different skeleton. A plain Generic-rig Animator
can only ever play a clip on the exact skeleton it was authored for, so the fix is Humanoid
retargeting: both FBX files get their own Humanoid avatar, which bakes the animation into
skeleton-independent data that replays correctly on the *other* rig.

**First attempt used Unity's automatic bone-name mapper for both avatars, and it got the
`CC_Base_*` one wrong** - confirmed by playtesting: characters walked on all fours (arms driving
as legs) and sank into the ground (both point at leg/hip bones landing on the wrong Humanoid
slots, which also throws off the hip-height scaling retargeting depends on). Fixed by hand-writing
an explicit bone mapping for that rig instead of trusting auto-detection (`BuildCCBaseHumanBones`
in the tool) - the `mixamorig:*` side is left auto-mapped, since Mixamo's naming is a convention
Unity's mapper handles reliably. The tool still logs a clear OK or warning per character in case
one of the 8 turns out to not share the exact same skeleton the mapping assumes.

**If a character still looks off after this fix**: the weakest guess in that explicit mapping is
`Neck` - this rig has no bone plainly named "Neck", only `NeckTwist01`/`02`, and `NeckTwist01` was
picked without being able to see it in motion. That's the first thing to try adjusting
(`BuildCCBaseHumanBones` in `CharacterAnimationSetup.cs`) if head/neck motion looks wrong even
though the walk cycle itself is fine.

**Also hit and fixed**: repeated re-runs of this tool left a character's Animator with a
`Controller` reading "Missing (Runtime Animator Controller)" (Avatar still fine) - the underlying
`.controller` asset had gone missing/orphaned. Cause: `AnimatorController.CreateAnimatorControllerAtPath`
silently renames instead of overwriting when a file already exists at that path, so re-running the
tool without a clean slate piled up orphaned duplicate controller files
(`{Name}Locomotion 1.controller`, `2.controller`, ...) until something in that churn broke a
prefab's reference. Fixed by having `Setup()` wipe `Assets/Animations` entirely and rebuild it
fresh on every run - re-run the tool once more to pick this up.

**Also hit and fixed (twice): players/the dummy spawning sunk into the floor.** Cause: the "Idle"
Animator state has no motion clip assigned (there's only ever one clip per character - see above),
and an Animator holding a state with no clip just reconstructs the avatar's own muscle-space rest
pose - which can differ from the raw imported bind pose `CharacterScaleFix` originally measured to
place characters on the floor. Fixed with a new `CharacterFloorAlignment` component, added to
every instantiated character visual (`PlayerController`/`DummyEnemyController`), running in
`LateUpdate` so it always sees that frame's already-evaluated Animator pose:

- First attempt only corrected *position* (measured the mesh's lowest point, nudged the whole
  visual up/down to the floor) - still not enough, because the rest pose can also measure a
  *different overall height*, not just a different floor contact point, so the head could still
  land short of where the camera expects it even with the feet correctly grounded.
- Now also corrects *scale* first: rescales the whole visual so its measured height matches a
  target height read directly from the owning player's own camera (its eye position is already
  correct and fixed by `PlayerMovementSetup` at prefab-build-time) - tying this to the camera
  itself rather than a separately guessed number. Falls back to a fixed constant (matching
  `CharacterScaleFix.TargetHeight`, not referenceable from this runtime script) for the practice
  dummy, which has no camera of its own. *Then* re-measures and corrects position, same as before.

One-time correction both times, not continuous per-frame - re-measuring/rescaling every frame
during the walk cycle would fight the animation and jitter. Should hold throughout the walk cycle
too (the avatar's calibration offset is a constant bias; the walk cycle's own bob rides on top of
it) - not yet confirmed in a playtest while actually walking around.

Also still unconfirmed: whether the single clip in each `{Name}Anim.fbx` (it's literally just one -
Mixamo names it "mixamo.com" by default, not descriptively) is actually a walk cycle as opposed to
some other motion.

- Adds an `Animator` + `CharacterAnimationDriver` to each character prefab under
  `Assets/Prefabs/Characters/`, and a generated `{Name}Locomotion.controller` under
  `Assets/Animations/` (two states, Idle/Walk, driven by a `Speed` float param).
- `CharacterAnimationDriver` reads the player's own `CharacterController.velocity` every frame -
  no wiring needed elsewhere, and it's harmless on the practice dummy (no CharacterController to
  find there, so its Animator just sits in Idle, correct for a stationary target).
- **Known rough edge, not addressed here**: `PlayerWeapon`'s arm-raise pose (drawn revolver)
  writes to the same arm bones the walk cycle drives, so the two can visually fight if you walk
  while aiming. Fixing that cleanly needs an upper/lower-body split via an `AvatarMask` - worth
  doing only if it actually bothers anyone in testing.

## Camera eye height

`PlayerMovementSetup` positions the first-person camera at a single computed eye height shared by
every character (they're all normalized to roughly the same height via `CharacterScaleFix`): feet
at `CharacterAttachPoint`'s local Y, eyes some fraction of `CharacterScaleFix.TargetHeight` above
that. Originally guessed at 0.92 (close to a real person's actual eye-to-height ratio), but
playtesting found it sitting noticeably above actual eye level - lowered to **0.85**. The likely
reason 0.92 read as too high: `TargetHeight` comes from a *measured* mesh bounds that includes a
hat for characters wearing one, inflating the "total height" those characters get normalized to,
which pushes 92% of it well above where their eyes actually are. Still one fraction shared by all
8 characters rather than a per-character measurement, so it's an approximation, not exact for
every one of them - nudge the `0.85f` in `PlayerMovementSetup.cs` further if it's still off, or
notably better/worse for one specific character than the others. **Not yet re-confirmed after this
change** - re-run **Tools > Shooting Gallery > Setup Player Movement** to pick it up (it's baked
into `PlayerPrefab` at setup time, not computed live - see "Finding & tuning the revolver" above
for the same live-vs-baked distinction on the weapon offsets).

## Duel: lives, headshot-only hitbox, and the practice dummy

The first slice of the actual duel exchange (M4): both players have 3 lives (shown as a
floating pip readout, top-center of your screen - deliberately a placeholder, see "Project
status" below). While the divider wall is down, a shot only counts if it lands on the
opponent's **head** - body shots are harmless. The first headshot to land ends the exchange
immediately: the wall snaps back up early (instead of waiting out its normal timer) and no
further hit registers until it drops fresh again from a refilled hit-tracker bar.

- **One-time setup step**: run **Tools > Shooting Gallery > Setup Duel Combat (Lives + Dummy
  Enemy)** once (after "Setup Random Character Visuals" has already been run, so there are
  character prefabs for the dummy to wear). Safe to re-run. It patches `PlayerPrefab` (adds
  `PlayerCombatant` + `LivesHUD`, moves its body collider to Unity's built-in "Ignore Raycast"
  layer so shots pass through it), builds `DummyEnemyPrefab`, and wires that prefab into the
  `MatchManager` already in `Arena.unity`.
- **How the headshot-only hitbox works**: a player's body collider sits on the "Ignore
  Raycast" layer, so `PlayerWeapon`'s shot raycast passes straight through it; the only thing
  it can actually land on is a trigger sphere (`HeadHitbox`) built at runtime by
  `HeadHitbox.Attach` (see its doc comment for the full fallback chain). It's sized to the
  actual head mesh where one exists as a separate piece - confirmed true for `BountyHunter`,
  `EliteCowboy1`, `EliteCowboy2` - and falls back to a sphere on the `CC_Base_Head` bone only
  for characters that turned out to be one continuous body mesh with no separable head piece
  (so far: `Cowboy1-4`, `Woman`). If headshots feel off on one of those, `PlayerCombatant`'s
  Inspector fields are what to tune, the same way the revolver's hand bone offset is tuned.
- **Made deliberately generous and animation-safe for reliable testing** (win/lose flow,
  headshot feel) rather than demanding pixel-precise aim: the mesh-based hitbox re-measures its
  source renderer's live world-space bounds *every frame* (`HeadHitbox.ApplyLiveBounds`) instead
  of only once at attach time - a one-time snapshot worked fine while characters stood still,
  but now that the walk cycle is live, a skinned head visibly moves relative to its own
  renderer's Transform as the clip plays, and a fixed snapshot would drift out of sync with
  where the head actually renders. Both the mesh-based and fallback-sphere hitboxes are also
  padded well beyond a tight fit (`HeadHitbox.BoundsPadding` = 1.35x; fallback default radius
  bumped to 0.3 with a slight upward offset, guessing most rigs' head bone pivot sits near the
  neck rather than centered on the head). Dial these back down once the underlying win/lose
  mechanic is confirmed solid and precision matters more than reliability.
- **Practice dummy**: if you walk into the gallery alone (nobody else has even connected),
  `MatchManager` spawns a random-cowboy dummy into the empty lane so you can test the whole
  loop solo - same 3 lives, same head hitbox, but stationary and it doesn't shoot back. It
  respawns a few seconds after "dying" instead of ending anything, so it's reusable for
  testing. If a real second player connects mid-solo-test, the dummy despawns, but the match
  won't re-run the normal "both walk through the door" handshake to pick up that second player
  cleanly yet - just restart the host if you hit that.
- Tuning knobs: `MatchManager`'s `Dummy Respawn Delay`; `PlayerCombatant`/`DummyEnemyController`'s
  `Fallback Head Hitbox Radius`/`Fallback Head Hitbox Local Offset` (only used for characters with
  no separable head mesh - see above).

**Win/lose screen and match reset**: the moment a real player's lives hit 0, `CurrentPhase`
becomes `MatchOver` and every client shows a full-screen colored overlay (green "YOU WIN" / red
"YOU LOSE", built by `LivesHUD.BuildResultOverlay`) with a "Returning to the bar..." subtitle.
After `Match Over Display Duration` seconds (default 6, tunable on `MatchManager`),
`ServerResetMatch` runs automatically: both players' lives/hit-tracker bars reset, any lingering
practice dummy despawns, `CurrentPhase` goes back to `WaitingForPlayers`, and both players are
teleported back to their bar spawn point (`MatchResetToken`, the same "increment a counter,
PlayerController watches for it and moves itself" pattern as gallery entry) - walking through the
gallery door again starts a fresh match exactly like the very first connection did. No input is
needed to dismiss the overlay; it just disappears once `CurrentPhase` moves on. **Not yet
playtested** - the whole chain (lives hit 0 -> overlay -> auto-reset -> back in the bar -> can
re-enter and play again) needs a real run-through to confirm.

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

**The six practice targets are now small medallions hung on `DividerWall`** (three per face, one
face per lane) instead of free-standing posts scattered around each lane - deliberately small
(diameter = 3x `PlayerWeapon`'s tracer width, read live off `PlayerPrefab` so it stays in sync if
that's retuned), a precision target rather than an easy one.

They're **not** literally parented to the wall's own GameObject - a `NetworkObject` (the
medallions) nested under another `NetworkObject` (the wall) doesn't reliably spawn, and the wall's
own wildly non-uniform `localScale` (0.5, 4.5, 16 - it's a stretched cube) would badly distort a
child's scale anyway. Instead there's a plain, non-networked **`WallMedallionMount`** object (see
it in the Hierarchy under `GalleryRoom`) that just copies the wall's position every frame
(`WallMountedTargetMount.cs`) - the medallions are parented under *that*, so they still drop out of
reach right along with the wall during a duel window (a side effect that actually fits: you're not
meant to be plinking practice targets while a duel is live), without the nesting problem.

Rebuilding requires **Tools > Shooting Gallery > Rebuild Arena Scene Only** to regenerate
`Arena.unity` with the new layout (the code change alone doesn't touch the already-saved scene).
Target size/thickness/layout positions are all in `ProjectScaffolder.BuildWallMountedTargets`.

## Lane barriers and foreground range props

**Permanent lane separation** (independent of whatever `DividerWall` is doing): the divider wall
only spans part of the gallery room's depth, leaving a 1-unit gap at each end before the room's
own north/south walls - without anything else, a player could just walk around the wall entirely,
wall up or down. Two fixes, both plain static geometry (not `WallController`-driven, never move):
- **`DividerEndCap (North/South)`** - full-height walls sealing those two end gaps.
- **`DividerAnkleWall`** - a permanent low (0.4-unit) wall spanning the *same* footprint as
  `DividerWall` itself, that never drops. So even during a duel window (tall wall down,
  sightlines/shots open) a player still can't just walk across to the other lane - only shoot
  across it. All three are built in `ProjectScaffolder.BuildArenaScene`, right after the wall.

**Gallery room is 30% larger** (`gallerySizeScale` in `ProjectScaffolder.BuildArenaScene`) -
applied uniformly to the floor plan (both X and Z) and most things positioned within it (lane
origins, the divider wall's length, wall-mounted target spread), so proportions stay correct
rather than one axis stretching. **Exception**: the wagon/crate/decoration cluster below is now
hand-placed at fixed absolute coordinates rather than a `gallerySizeScale`-derived formula, so it
won't automatically stay proportional if `gallerySizeScale` changes again - re-placing (eyeball in
the Editor, update the numbers in `BuildLaneProps`/`BuildLaneDecoration`, mirror is automatic) would
be needed at that point. The end caps/ankle wall/bar room
position/door trigger are all derived from the gallery's actual current half-width/half-depth and
the wall's actual current length (not separately hardcoded numbers), specifically so a future
change to `gallerySizeScale` can't leave any of them out of sync with the others the way plain
hardcoded copies would. Deliberately NOT scaled: room height (kept equal to the bar room's, for a
seamless shared doorway - more floor space reads as "larger gallery," not a taller ceiling), and
anything calibrated to the character rig or bullet size (door dimensions, target diameter, wall
thickness) rather than to room scale.

**Foreground range props**: a `Wagon` and a `DynamiteCrate` (the pack's stand-in for a "TNT
box" - see `Assets/Art/Mini PSX Western Pack`) in each lane, mirrored, positioned between the
spawn point and the divider wall. The wagon carries 3 more medallion targets, the crate 2, mounted
on the face pointing back toward that lane's spawn (same medallion sizing/mechanic as the
wall-mounted ones).

**Wagon rotated so its 3 medallions actually fit** - playtesting found the wagon's long axis
sitting along world X, the same axis `AddMedallionsOnPropFace` measures the mounting face's
*depth* from, which left the 3 medallions spread across its short axis (world Z) instead and
cramped together. Turning the wagon roughly a quarter-turn fixed it - no change needed to the
medallion placement code itself, since it re-measures the prop's live (post-rotation) world
bounds on every call rather than assuming a fixed orientation, so it automatically spreads across
whichever axis is actually long once the prop itself turns. The initial guess (`Quaternion.Euler(0,
90, 0)`) was then hand-tuned further in the Editor once actually visible (see "Wagon/crate
position and decoration are now hand-placed, and mirrored across lanes" just below) - both the fit
and which face ends up visible are confirmed now, not a guess.

**Wagon/crate position and decoration are now hand-placed, and mirrored across lanes**: rather
than reasoning about fractions of `laneOriginX` for where the wagon/crate should sit, their
position and rotation are now hand-placed values (tuned directly in the Editor once the auto-scaled
props were actually visible) for Lane B, plus a scattering of decorative clutter around them -
`GoldNugget`, `RockSmall1`, `RockLarge2`, two `Boulder1`s, `Pickaxe`, `GoldCrate`, `Campfire` (raw
`Assets/Art/Mini PSX Western Pack/FBX` files instantiated directly via `BuildLaneDecoration`/
`PlaceDecorationFbx` - these came in with their own materials already, unlike Wagon/DynamiteCrate,
so no `RangePropsSetup` bake/scale/collider pass was needed for them). Lane A gets the same layout
automatically via `MirrorXForLane`/`MirrorRotationForLane` - a true left-right reflection across
the x=0 divider wall (negate the X position; negate a rotation quaternion's y/z components, which
holds for *any* rotation, not just a simple yaw, which is why the campfire's compound tilt mirrors
correctly too) - rather than being hand-placed a second time. **Practical implication**: retuning
Lane B's layout in the Editor no longer updates Lane A - the new correct workflow is tune Lane B's
numbers in `BuildLaneProps`/`BuildLaneDecoration` (in code) and re-run **Rebuild Arena Scene Only**,
which regenerates *both* lanes from the same hand-placed-for-B values. Hand-editing objects
directly in the Editor again (rather than through the scaffolding code) would only affect whichever
lane was touched, and would be silently lost/overwritten on the next rebuild - same caveat as every
other scaffolded object in this project.

**Shooting counter**: a wooden counter/rail (`ShootingCounter`, `ProjectScaffolder.BuildShootingCounter`)
at each lane's firing line, right where the player spawns - inspired by real shooting-gallery
photo references (a long wooden counter along the front of the stations). Purely decorative range
dressing, not a gameplay barrier - built from a plain colored box (a wood-brown material), since
there's no long-counter/rail asset in the project to use instead. A textured asset could replace
it later if one gets added.

- **One-time setup step**: run **Tools > Shooting Gallery > Setup Range Props (Wagon + Dynamite
  Crate)** once - builds `Assets/Prefabs/Props/Wagon.prefab` and `DynamiteCrate.prefab` (bakes a
  material from each one's texture, adds a `BoxCollider` since neither FBX ships with one, and
  auto-scales each to a target height using the same measure-then-scale technique as
  `CharacterScaleFix`). Then **Rebuild Arena Scene Only** to actually place them.
- **Not yet visually confirmed - flagging clearly rather than pretending certainty**: the target
  heights in `RangePropsSetup.cs` (wagon 2.2 units, crate 0.9 units - guessed relative to a
  person being ~3.33 units tall), both props' position/rotation in `BuildLaneProps`, and which
  world axis is actually "across" each prop's face for `AddMedallionsOnPropFace`'s target spread.
  All of this is a first pass reasoned from the code alone, not from seeing the actual meshes -
  expect to need to eyeball and adjust position/rotation/scale numbers once they're visible in
  the Editor. The auto-scale and bounds-relative medallion placement *technique* should hold up
  regardless (it measures the real mesh rather than guessing fixed numbers), even if the target
  heights/positions themselves need retuning.
- **First playtest: both props spawned partly under the floor** - `PlaceRangeProp` originally
  placed them by their own pivot, assuming (wrongly) that it sits at the model's base like the
  character rigs' does. These are Blender-authored assets with no such guarantee. Fixed by
  measuring the actual mesh bounds after placement and shifting up so the lowest point sits
  exactly on the floor, regardless of where the pivot turns out to be - re-run **Rebuild Arena
  Scene Only** to pick this up.
- The shooting counter's exact size/offset-from-spawn (`BuildShootingCounter`) is likewise a
  first guess, not yet seen in the Editor.

## Bar interior (furniture pass)

The bar room went from a plain empty 14x18 box to a furnished 22x22 tavern layout
(`ProjectScaffolder.BuildBarInterior`), loosely modeled on a real tavern floor-plan reference
photo: a bar counter with shelving along the east wall (the far side from the gallery doorway on
the west) with a row of seats, four poker tables each with four chairs scattered across the
remaining floor, and a couple of barrels just inside the entrance. Everything's sized relative to
characters standing ~3.33 units tall (counter/tables/chairs all use the same height reasoning as
the gallery's `ShootingCounter`) rather than copied directly from the reference's own proportions.

**Real furniture, not greybox anymore**: the original pass used plain colored cylinders for every
table and stool as a stand-in while getting the room's layout/scale right. Once that was confirmed
to fit, they were swapped for real `Assets/Art/Mini PSX Western Pack` props via a new
`BarFurnitureSetup` tool (same bake-material/auto-scale/add-collider recipe as `RangePropsSetup`
uses for the gallery's Wagon/DynamiteCrate) - dining tables are now `PokerTable`, their stools are
now `Chair`, and the counter's own row of stools are `LogStump` instead (a shorter, more rustic
seat, deliberately different from the dining chairs). The counter/shelf/barrels themselves stay
plain colored primitives - no equivalent imported asset was worth building a pipeline for those.
- **One-time setup step**: run **Tools > Shooting Gallery > Setup Bar Furniture (Poker Table,
  Chair, Log Stump)** once (builds the three prefabs, same pattern as **Setup Range Props**).
  Then **Rebuild Arena Scene Only** to actually place them.
- **Chairs and log stumps are turned to face the table/counter** they belong to -
  `Quaternion.LookRotation` toward the table center (chairs) or toward the counter (stumps).
  **Which local axis each FBX actually considers its own "front" is an unconfirmed guess** - if
  they're all facing backwards once visible, flip the direction passed to `LookRotation` in
  `BuildBarTable`/`BuildBarInterior` (same class of guess as the wagon's original rotation, before
  that got hand-tuned - see "Lane barriers and foreground range props" above).
- Target heights (`BarFurnitureSetup.cs`: PokerTable 0.9, Chair 1.0, LogStump 0.6) are a first
  guess, same caveat as the range props' own target heights - not yet visually confirmed.

Also **not** attempted: the reference's octagonal bay-window alcove - that's a room-shape/
architecture change, not furniture, and the room stays a plain rectangular shell for now.

The bar's spawn points moved to just inside the door (a clear patch of floor ahead of the
furniture) to make room for everything - rebuilding requires **Tools > Shooting Gallery > Rebuild
Arena Scene Only**. **Not yet visually confirmed** - first pass at fitting a counter, four tables,
and their chairs into the room without anything overlapping; expect some repositioning once it's
actually visible.

## Common gotchas

- **"No cameras rendering" warning on Play**: normal if you're in `MainMenu` before hosting —
  neither `Bootstrap` nor `MainMenu` has a gameplay camera; the only one lives on `PlayerPrefab`
  and only activates once you actually spawn. It should clear the moment you click Host.
- **Pressing Play does something weird / null reference on Host**: shouldn't happen anymore —
  Play mode is now forced to always start from `Bootstrap.unity` regardless of which scene you
  had open (`PlayModeBootstrap.cs`), matching how a real build always behaves.
- **A real build looks "way different" from the Editor's Play mode - UI comically tiny**: every
  `CanvasScaler` in the project (menu, hit-tracker bar, lives HUD) was left on Unity's default
  "Constant Pixel Size" mode, which renders every UI element at a literal pixel size regardless of
  actual screen resolution. That looks fine in the Editor's small, often-shrunk Game view panel,
  but tiny/wrong at a real build's full native resolution - the Editor and a build were never
  actually seeing the same UI scale. Fixed: all three now use "Scale With Screen Size" against a
  1920x1080 reference, so the existing pixel-based layout numbers throughout the project stay
  meaningful at any real resolution. The menu's canvas is built by `ProjectScaffolder` (scene
  scaffolding, needs **Rebuild Main Menu Scene Only** to pick this up); the hit-tracker
  bar/lives HUD are built at runtime by their own scripts, so those apply automatically on next
  Play/build with no rebuild needed. **If a build still looks meaningfully different after this**,
  that's a sign there's a second, different cause still to find - worth a screenshot to compare.
- **Something breaks right after connecting**: check the **Console window** (Window > General >
  Console) for red error text the moment it happens — this has been the fastest way to find the
  real cause every time so far, way faster than guessing from symptoms alone.
- **A cross-object reference (like `MatchManager`'s `Divider Wall` field) reads back null at
  runtime even though it's clearly set correctly in the scene/prefab file**: has happened twice
  now, both times traced to that scene or prefab having last been rebuilt through a Unity batch
  run against the project's 8.3 short path (`SHOOTI~1`) instead of its real path — some editor
  cache gets keyed to the wrong path and silently corrupts references for anything more complex
  than a plain field. Fix is to just rebuild that scene/prefab again (the relevant
  `Tools > Shooting Gallery` menu item) — this only happens from scripted/batch edits, never from
  using the Editor normally, so it's not something you'll hit from your own testing.

## Useful custom menu items

Under **Tools > Shooting Gallery** in the Editor menu bar:
- **Rebuild Arena Scene Only** / **Rebuild Main Menu Scene Only** — regenerates just that scene
  from code, without touching the other scenes or `PlayerPrefab`.
- **Setup Random Character Visuals**, **Setup Player Movement**, **Setup Revolver**, **Fix
  Character Scale** — one-shot patches for `PlayerPrefab`; safe to re-run.
- **Setup Character Walk Animation** — Humanoid-retargets each character's separate animation
  FBX onto its own rig and wires up an Idle/Walk Animator Controller (see "Character walk
  animation" above). Run after "Setup Random Character Visuals"; logs a warning per character
  whose avatar auto-mapping needs a manual look.
- **Setup Duel Combat (Lives + Dummy Enemy)** — see "Duel: lives, headshot-only hitbox, and the
  practice dummy" above.
- **Setup Room Code HUD** — patches `PlayerPrefab` with the host's in-bar room code reminder, see
  "Joining over the internet with a room code" above.
- **Setup Range Props (Wagon + Dynamite Crate)** — see "Lane barriers and foreground range props"
  above. Run before rebuilding the Arena scene, same as the character-visual tools.
- **Setup Bar Furniture (Poker Table, Chair, Log Stump)** — see "Bar interior (furniture pass)"
  above. Run before rebuilding the Arena scene, same as the range props tool.
- **Capture Revolver Preview** — renders the revolver prefab to a PNG for a quick visual check
  without needing to test in-game (used heavily to fix its orientation).
- **Diagnostics > Run Wall Drop Diagnostic** — hosts a session and directly forces 10 hits to
  check the tracker-bar-to-wall-drop wiring end to end, without needing to actually aim/shoot or
  a second player. Logs PASS/FAIL to the Console and exits Play mode on its own when done.

Most of the project is actually built/maintained through these scripted tools rather than
hand-edited in the Editor, so re-running one after a related code change is often the way
to apply it.

## Project status

See git log for the detailed history. Rough milestone state as of the last update to this file:
- ✅ Netcode connection (host/join, lane assignment)
- ✅ Join-by-room-code over the internet via Unity Lobby + Relay, for playtesting with friends
  off-LAN - **confirmed working in a real two-machine internet test**. See "Joining over the
  internet with a room code" and "Debugging network/Relay/Lobby issues" above for the full trail:
  a compile-error signature mismatch, a join attempt that hung forever on "waiting for host", a
  real 60-second lone-host Relay cutoff (avoided by wrapping Relay in Lobby), and finally a DTLS
  handshake failure (`UseSecureRelayConnection` now `false`) were each hit in actual testing and
  fixed in turn. A "Practice Solo" button keeps solo dummy-testing working now that Host waits for
  a real second player.
- ✅ Random character models, correctly scaled
- ✅ WASD/mouse movement, eye-height camera
- ✅ Bar + Gallery rooms, colored, doorway sized right
- ✅ Revolver draw/holster (E), 6-shot ammo (click to fire, R to reload), correctly posed
- ✅ Six practice targets, server-validated hits, turn green and auto-reset
- ✅ Arcade shot feedback: recoil kick + red cylinder tracer from the barrel
- 🟡 Reload takes 3 seconds now (was instant) and tilts the revolver down, synced so both
  players see it - not yet visually confirmed which way the tilt actually goes.
- ✅ Hit tracker bar per lane, fills 10% per hit, drops the divider wall for 5s at 100%
- 🟡 Character walk animation wired up (see "Character walk animation" above). First playtest
  found real breakage (walking quadrupedal, sunk into the ground) traced to Unity's automatic
  bone mapping getting the `CC_Base_*` rig wrong - fixed with an explicit hand-written mapping.
  Second playtest then hit a "Missing (Runtime Animator Controller)" reference, traced to the
  setup tool accumulating orphaned duplicate controller files across re-runs - fixed by having it
  wipe and rebuild its whole output folder every run. Needs the tool re-run once more and a fresh
  playtest to confirm it's actually clean now.
- 🟡 Permanent lane-separation barriers (end caps + a low ankle wall along `DividerWall`'s
  footprint), a 30%-larger gallery room, a wooden shooting counter per lane, and foreground range
  props (a wagon + dynamite crate per lane, each carrying more medallion targets) added - see
  "Lane barriers and foreground range props" above. Needs both new setup steps run, the Arena
  scene rebuilt, and a look in the Editor - the props' and counter's exact size/position/rotation
  are an unconfirmed first guess (the room-scale math itself is on firmer footing - derived from
  actual measurements rather than hardcoded per-object numbers).
- 🟡 Bar room enlarged (14x18 -> 22x22) and furnished with a greybox tavern layout - counter,
  dining tables, entrance barrels - see "Bar interior (greybox furniture pass)" above. Needs the
  Arena scene rebuilt and a look in the Editor; layout is an unconfirmed first pass.
- 🟡 Duel exchange (M4), first slice: 3 lives each, headshot-only hits, wall raises early on a
  landed headshot, solo-testable via an auto-spawned practice dummy - see "Duel: lives,
  headshot-only hitbox, and the practice dummy" above. Confirmed working end to end in a first
  playtest; the headshot hitbox was originally a fixed-size sphere on the head bone and felt
  too small in that test, so it now sizes itself from the actual head mesh where one exists as
  a separate piece (falls back to the bone sphere only for characters without one) - re-verify
  hit-feel across a few different characters next session.
- 🟡 Win/lose screen + auto-reset to the bar added (see "Win/lose screen and match reset" above) -
  not yet playtested end to end.
- ⬜ Still missing from the duel itself: any dodge/reaction mechanic beyond "aim and click first",
  a real "who drew first" cue, and tying the floating lives UI to physical objects in the room
  (explicitly requested, deferred for now).
