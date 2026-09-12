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

        // Each lane's own hit-tracker bar - 0 to hitsPerBarFill, read by that player's
        // HitTrackerHUD to drive its fill amount. Resets to 0 the moment it fills (see
        // RegisterTargetHit), which is also the signal the bar was ever actually full - no
        // separate "just filled" event needed since the wall drop happens in that same instant.
        public readonly NetworkVariable<int> PlayerAHitCount = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> PlayerBHitCount = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Server-only bookkeeping - no client needs to read these directly, they only ever
        // observe the CurrentPhase change that results from both being true.
        private bool playerAEnteredGallery;
        private bool playerBEnteredGallery;

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

            if (playerAEnteredGallery && playerBEnteredGallery)
            {
                CurrentPhase.Value = GamePhase.GalleryPhase;
            }
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
            LaneSide lane = GetLaneForClient(clientId);
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
    }
}
