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

            if (PlayerAClientId.Value != ulong.MaxValue && PlayerBClientId.Value != ulong.MaxValue)
            {
                CurrentPhase.Value = GamePhase.GalleryPhase;
            }
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

            CurrentPhase.Value = GamePhase.WaitingForPlayers;
        }

        private void PositionPlayerIfSpawned(ulong clientId)
        {
            if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) ||
                client.PlayerObject == null)
            {
                return;
            }

            Transform spawnPoint = clientId == PlayerAClientId.Value ? playerASpawnPoint : playerBSpawnPoint;
            if (spawnPoint == null)
            {
                return;
            }

            client.PlayerObject.transform.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);
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
