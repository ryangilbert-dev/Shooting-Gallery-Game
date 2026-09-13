using Unity.Netcode;
using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Central networked authority for a match. Every other gameplay script reads state
    /// from here (via NetworkVariables) or is driven by it. This is built out incrementally
    /// across milestones - M1 only implements connection-order lane assignment and player
    /// spawn positioning. Gallery scoring (M2), the wall trigger (M3), the duel exchange loop
    /// (M4), and round/match progression (M5) are added on top of this same script rather than
    /// as separate managers, per the approved architecture plan.
    ///
    /// This manager never moves a player's transform directly - PlayerPrefab's NetworkTransform
    /// is Owner-authoritative (for responsive movement), so a server-side position write can
    /// silently lose to the owner's own authority, or simply happen before that player's object
    /// has finished spawning at all. Instead it exposes spawn-point lookups and increments a
    /// per-lane "go there now" token; each player watches for its own lane's token and moves
    /// itself (see PlayerController), which is both timing-safe and authority-consistent.
    /// </summary>
    public class MatchManager : NetworkBehaviour
    {
        public static MatchManager Instance { get; private set; }

        [Header("Lane spawn points (assigned in the Arena scene)")]
        [SerializeField] private Transform playerASpawnPoint;
        [SerializeField] private Transform playerBSpawnPoint;

        [Header("Bar (lobby) spawn points - where players land on connect")]
        [SerializeField] private Transform barSpawnPointA;
        [SerializeField] private Transform barSpawnPointB;

        [Header("Wall drop mechanic")]
        [SerializeField] private WallController dividerWall;
        [SerializeField] private int hitsPerBarFill = 10;
        [SerializeField] private float wallDropDuration = 5f;

        [Header("Duel: lives and the practice dummy")]
        [SerializeField] private GameObject dummyEnemyPrefab;
        [SerializeField] private float dummyRespawnDelay = 3f;

        // How long the win/lose screen stays up before ServerResetMatch sends everyone back to
        // the bar for a fresh match.
        [SerializeField] private float matchOverDisplayDuration = 6f;

        /// <summary>How many landed shots fill a lane's hit-tracker bar - HitTrackerHUD reads
        /// this to turn a raw hit count into a fill fraction.</summary>
        public int HitsPerBarFill => hitsPerBarFill;

        public readonly NetworkVariable<GamePhase> CurrentPhase = new NetworkVariable<GamePhase>(
            GamePhase.WaitingForPlayers,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<ulong> PlayerAClientId = new NetworkVariable<ulong>(
            ulong.MaxValue,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<ulong> PlayerBClientId = new NetworkVariable<ulong>(
            ulong.MaxValue,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        // Incremented (server-only) whenever that lane's player should move to their gallery
        // spawn point right now. A plain counter rather than a bool so it fires every time (a
        // bool that's already true wouldn't trigger OnValueChanged if set true again).
        public readonly NetworkVariable<int> PlayerAGalleryEntryToken = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> PlayerBGalleryEntryToken = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Same "incrementing counter, not a bool" pattern as the gallery entry tokens above -
        // fires once per ServerResetMatch call, telling every connected PlayerController to
        // teleport itself back to its own bar spawn point regardless of where it currently is.
        public readonly NetworkVariable<int> MatchResetToken = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Each lane's own hit-tracker bar - 0 to hitsPerBarFill, read by that player's
        // HitTrackerHUD to drive its fill amount. Resets to 0 the moment it fills (see
        // RegisterTargetHit), which is also the signal the bar was ever actually full - no
        // separate "just filled" event needed since the wall drop happens in that same instant.
        public readonly NetworkVariable<int> PlayerAHitCount = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> PlayerBHitCount = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Duel lives - decremented by RegisterHeadshot, one per landed headshot. Reaching 0 ends
        // the match (CurrentPhase -> MatchOver) and starts the countdown to ServerResetMatch,
        // which puts these back to 3 for the next one.
        public readonly NetworkVariable<int> PlayerALives = new NetworkVariable<int>(
            3, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> PlayerBLives = new NetworkVariable<int>(
            3, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Which lane the practice dummy currently fills - None means no dummy is spawned. Only
        // ever non-None when a lone player has walked into the gallery with nobody else connected
        // at all (see TrySpawnDummyForSoloTesting); a second real connection despawns it again.
        public readonly NetworkVariable<LaneSide> DummyLane = new NetworkVariable<LaneSide>(
            LaneSide.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Unlike the real players, the dummy auto-resets a few seconds after dying instead of
        // ending anything - it's a training aid, not a real opponent with a match to lose (see
        // HandleDummyLivesChanged/Update).
        public readonly NetworkVariable<int> DummyLives = new NetworkVariable<int>(
            3, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Server-only bookkeeping - no client needs to read these directly, they only ever
        // observe the CurrentPhase change that results from both being true.
        private bool playerAEnteredGallery;
        private bool playerBEnteredGallery;

        private NetworkObject spawnedDummy;
        private float dummyRespawnCountdown;
        private float matchOverCountdown;

        private void Awake()
        {
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer)
            {
                return;
            }

            NetworkManager.OnClientConnectedCallback += HandleClientConnected;
            NetworkManager.OnClientDisconnectCallback += HandleClientDisconnected;
            DummyLives.OnValueChanged += HandleDummyLivesChanged;

            // The host's own connection can complete before this scene object finishes
            // spawning, so catch up on anyone already connected.
            foreach (ulong clientId in NetworkManager.ConnectedClientsIds)
            {
                HandleClientConnected(clientId);
            }
        }

        public override void OnNetworkDespawn()
        {
            if (!IsServer)
            {
                return;
            }

            NetworkManager.OnClientConnectedCallback -= HandleClientConnected;
            NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
            DummyLives.OnValueChanged -= HandleDummyLivesChanged;
        }

        private void Update()
        {
            if (!IsServer)
            {
                return;
            }

            // Brings the practice dummy back after it's "died" - it's a training aid for testing
            // the duel loop solo, not a real opponent, so it shouldn't be a dead end. Real players
            // hitting 0 lives go through MatchOver/ServerResetMatch below instead.
            if (DummyLane.Value != LaneSide.None && DummyLives.Value == 0)
            {
                dummyRespawnCountdown -= Time.deltaTime;
                if (dummyRespawnCountdown <= 0f)
                {
                    DummyLives.Value = 3;
                }
            }

            // Holds the win/lose screen up for matchOverDisplayDuration seconds (see
            // RegisterHeadshot, which starts this countdown the instant a life hits 0) before
            // resetting everyone back to the bar for a fresh match.
            if (CurrentPhase.Value == GamePhase.MatchOver)
            {
                matchOverCountdown -= Time.deltaTime;
                if (matchOverCountdown <= 0f)
                {
                    ServerResetMatch();
                }
            }
        }

        private void HandleDummyLivesChanged(int previous, int current)
        {
            if (current == 0)
            {
                dummyRespawnCountdown = dummyRespawnDelay;
            }
        }

        private void HandleClientConnected(ulong clientId)
        {
            if (PlayerAClientId.Value == ulong.MaxValue)
            {
                PlayerAClientId.Value = clientId;
            }
            else if (PlayerBClientId.Value == ulong.MaxValue && clientId != PlayerAClientId.Value)
            {
                PlayerBClientId.Value = clientId;

                // A real second player just showed up - if a practice dummy was standing in for
                // them (solo testing), it's no longer needed.
                DespawnDummyIfActive();
            }

            // Both connected just means both are standing in the bar together now - the match
            // itself doesn't start (GalleryPhase) until they both walk through the gallery
            // doorway, handled by NotifyPlayerEnteredGallery. Bar positioning itself is handled
            // by each player positioning itself once it learns its own lane assignment (see
            // PlayerController) - not attempted here, since the player object may not exist yet
            // at the exact moment this callback fires.
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            if (clientId == PlayerAClientId.Value)
            {
                PlayerAClientId.Value = ulong.MaxValue;
            }

            if (clientId == PlayerBClientId.Value)
            {
                PlayerBClientId.Value = ulong.MaxValue;
            }

            playerAEnteredGallery = false;
            playerBEnteredGallery = false;
            CurrentPhase.Value = GamePhase.WaitingForPlayers;
        }

        /// <summary>
        /// Called (server-only) by GalleryEntryTrigger when a connected player's collider passes
        /// through the doorway between the bar and the gallery. Signals that player to teleport
        /// itself to its assigned lane; once both players have entered, starts the match
        /// (GalleryPhase).
        /// </summary>
        public void NotifyPlayerEnteredGallery(ulong clientId)
        {
            if (!IsServer || CurrentPhase.Value != GamePhase.WaitingForPlayers)
            {
                return;
            }

            LaneSide lane = GetLaneForClient(clientId);
            if (lane == LaneSide.None)
            {
                return;
            }

            if (lane == LaneSide.A)
            {
                playerAEnteredGallery = true;
                PlayerAGalleryEntryToken.Value++;
            }
            else
            {
                playerBEnteredGallery = true;
                PlayerBGalleryEntryToken.Value++;
            }

            if (TrySpawnDummyForSoloTesting(lane) || (playerAEnteredGallery && playerBEnteredGallery))
            {
                CurrentPhase.Value = GamePhase.GalleryPhase;
            }
        }

        /// <summary>Server-only: if a lone player walks into the gallery with nobody else even
        /// connected yet, spawns a random-cowboy practice dummy into the vacant lane - complete
        /// with its own 3 lives and headshottable head hitbox (DummyEnemyController) - so the
        /// whole duel mechanic (wall drop, headshot, lives) is testable solo, instead of needing a
        /// second person just to try it out. Returns true if it spawned one (meaning the match
        /// should proceed as if both lanes were filled).</summary>
        private bool TrySpawnDummyForSoloTesting(LaneSide enteredLane)
        {
            LaneSide otherLane = enteredLane == LaneSide.A ? LaneSide.B : LaneSide.A;
            ulong otherClientId = otherLane == LaneSide.A ? PlayerAClientId.Value : PlayerBClientId.Value;

            if (otherClientId != ulong.MaxValue || DummyLane.Value != LaneSide.None || dummyEnemyPrefab == null)
            {
                return false; // A real second player is connected, a dummy's already up, or nothing to spawn.
            }

            Transform spawnPoint = GetGallerySpawnPointForLane(otherLane);
            if (spawnPoint == null)
            {
                return false;
            }

            GameObject dummyGO = Instantiate(dummyEnemyPrefab, spawnPoint.position, spawnPoint.rotation);
            spawnedDummy = dummyGO.GetComponent<NetworkObject>();
            spawnedDummy.Spawn();

            DummyLane.Value = otherLane;
            DummyLives.Value = 3;
            return true;
        }

        /// <summary>Server-only: removes the practice dummy, if one is currently active. Called
        /// when a real second player connects and takes its place.</summary>
        private void DespawnDummyIfActive()
        {
            if (DummyLane.Value == LaneSide.None)
            {
                return;
            }

            if (spawnedDummy != null)
            {
                spawnedDummy.Despawn();
                spawnedDummy = null;
            }

            DummyLane.Value = LaneSide.None;
        }

        public LaneSide GetLaneForClient(ulong clientId)
        {
            if (clientId == PlayerAClientId.Value)
            {
                return LaneSide.A;
            }

            if (clientId == PlayerBClientId.Value)
            {
                return LaneSide.B;
            }

            return LaneSide.None;
        }

        /// <summary>Where this client should stand in the bar (lobby) on connect. Null if not
        /// yet assigned a lane.</summary>
        public Transform GetBarSpawnPointForClient(ulong clientId)
        {
            LaneSide lane = GetLaneForClient(clientId);
            if (lane == LaneSide.A) return barSpawnPointA;
            if (lane == LaneSide.B) return barSpawnPointB;
            return null;
        }

        /// <summary>Where this client should stand in their gallery lane. Null if not yet
        /// assigned a lane.</summary>
        public Transform GetGallerySpawnPointForClient(ulong clientId)
        {
            return GetGallerySpawnPointForLane(GetLaneForClient(clientId));
        }

        private Transform GetGallerySpawnPointForLane(LaneSide lane)
        {
            if (lane == LaneSide.A) return playerASpawnPoint;
            if (lane == LaneSide.B) return playerBSpawnPoint;
            return null;
        }

        /// <summary>Server-only: called by PlayerWeapon whenever a shot lands on a target. Bumps
        /// that shooter's lane's hit-tracker bar; once it reaches hitsPerBarFill, resets it back
        /// to 0 and drops the divider wall for wallDropDuration seconds.</summary>
        public void RegisterTargetHit(ulong shooterClientId)
        {
            if (!IsServer)
            {
                return;
            }

            LaneSide lane = GetLaneForClient(shooterClientId);
            if (lane == LaneSide.None)
            {
                return;
            }

            NetworkVariable<int> hitCount = lane == LaneSide.A ? PlayerAHitCount : PlayerBHitCount;
            hitCount.Value++;

            if (hitCount.Value >= hitsPerBarFill)
            {
                hitCount.Value = 0;
                if (dividerWall != null)
                {
                    dividerWall.ServerDropForSeconds(wallDropDuration);
                }
            }
        }

        /// <summary>Server-only: called by PlayerWeapon whenever a shot lands on a HeadHitbox (a
        /// real opponent's head, or the practice dummy's). Only counts while the divider wall is
        /// actually down, and only once per drop (WallController.ServerTryConsumeHeadshotWindow) -
        /// so a duel exchange resolves on the first hit that lands: land the shot and the wall
        /// snaps back up immediately, win or lose, before either side can trade back.</summary>
        public void RegisterHeadshot(ulong shooterClientId, NetworkObject targetObject)
        {
            if (!IsServer || dividerWall == null || targetObject == null)
            {
                return;
            }

            if (!dividerWall.ServerTryConsumeHeadshotWindow())
            {
                return;
            }

            LaneSide shooterLane = GetLaneForClient(shooterClientId);
            if (shooterLane == LaneSide.None)
            {
                return;
            }

            if (targetObject.TryGetComponent(out DummyEnemyController _))
            {
                if (DummyLane.Value == LaneSide.None || DummyLane.Value == shooterLane)
                {
                    return; // No dummy active, or somehow "shot" your own side.
                }

                DummyLives.Value = Mathf.Max(0, DummyLives.Value - 1);
                return;
            }

            LaneSide targetLane = GetLaneForClient(targetObject.OwnerClientId);
            if (targetLane == LaneSide.None || targetLane == shooterLane)
            {
                return;
            }

            NetworkVariable<int> lives = targetLane == LaneSide.A ? PlayerALives : PlayerBLives;
            lives.Value = Mathf.Max(0, lives.Value - 1);
            if (lives.Value == 0)
            {
                CurrentPhase.Value = GamePhase.MatchOver;
                matchOverCountdown = matchOverDisplayDuration;
            }
        }

        /// <summary>Server-only: called once matchOverDisplayDuration has elapsed after a match
        /// ends (see Update/RegisterHeadshot). Puts both players' gameplay state back to a fresh
        /// match - full lives, empty hit-tracker bars, wall raised (already true - the match-
        /// ending headshot itself force-raised it), no lingering dummy - and sends both players
        /// back to their bar spawn point via MatchResetToken (see PlayerController), so walking
        /// through the gallery door again starts a new match exactly like the first connection
        /// did.</summary>
        private void ServerResetMatch()
        {
            PlayerALives.Value = 3;
            PlayerBLives.Value = 3;
            PlayerAHitCount.Value = 0;
            PlayerBHitCount.Value = 0;
            playerAEnteredGallery = false;
            playerBEnteredGallery = false;

            DespawnDummyIfActive();

            CurrentPhase.Value = GamePhase.WaitingForPlayers;
            MatchResetToken.Value++;
        }
    }
}
