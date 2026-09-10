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
            else
            {
                // Third-plus connection: not supported for 1v1 v1, leave unassigned.
                return;
            }

            PositionPlayerIfSpawned(clientId);

            // Both connected just means both are standing in the bar together now - the match
            // itself doesn't start (GalleryPhase) until they both walk through the gallery
            // doorway, handled by NotifyPlayerEnteredGallery.
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

        private void PositionPlayerIfSpawned(ulong clientId)
        {
            if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) ||
                client.PlayerObject == null)
            {
                return;
            }

            Transform spawnPoint = clientId == PlayerAClientId.Value ? barSpawnPointA : barSpawnPointB;
            if (spawnPoint == null)
            {
                return;
            }

            client.PlayerObject.transform.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);
        }

        /// <summary>
        /// Called (server-only) by GalleryEntryTrigger when a connected player's collider passes
        /// through the doorway between the bar and the gallery. Teleports them to their assigned
        /// lane; once both players have entered, starts the match (GalleryPhase).
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
            }
            else
            {
                playerBEnteredGallery = true;
            }

            if (NetworkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) &&
                client.PlayerObject != null)
            {
                Transform spawnPoint = lane == LaneSide.A ? playerASpawnPoint : playerBSpawnPoint;
                if (spawnPoint != null)
                {
                    client.PlayerObject.transform.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);
                }
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
    }
}
