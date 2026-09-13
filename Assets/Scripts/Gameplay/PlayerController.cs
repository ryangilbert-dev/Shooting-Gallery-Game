using Unity.Netcode;
using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Per-player networked behaviour. Handles camera/audio ownership so each client only sees
    /// through their own eyes, picks a random cosmetic character model (server-authoritative,
    /// synced via CharacterIndex) so both clients see the same body on each player, and positions
    /// itself at its assigned bar/gallery spawn point.
    ///
    /// Self-positioning (rather than MatchManager setting this object's transform directly) is
    /// deliberate: PlayerPrefab's NetworkTransform is Owner-authoritative, so a server-side
    /// position write can silently lose to the owner's own authority once that client starts
    /// sending its own transform updates, and a one-shot attempt on connect can also just miss
    /// the window before this object finishes spawning. Watching MatchManager's NetworkVariables
    /// and moving myself once I'm actually ready sidesteps both problems.
    /// </summary>
    public class PlayerController : NetworkBehaviour
    {
        [SerializeField] private Camera playerCamera;
        [SerializeField] private AudioListener audioListener;
        [SerializeField] private Renderer placeholderBodyRenderer;

        [Header("Character visuals - server picks one at random per spawn")]
        [SerializeField] private Transform characterAttachPoint;
        [SerializeField] private GameObject[] characterVisualPrefabs;
        [SerializeField] private float characterVisualScale = 1f;

        public readonly NetworkVariable<int> CharacterIndex = new NetworkVariable<int>(
            -1,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private GameObject spawnedCharacterVisual;
        private bool hasPositionedAtBarSpawn;
        private bool hasSubscribedToMatchManager;

        public override void OnNetworkSpawn()
        {
            if (playerCamera != null)
            {
                playerCamera.enabled = IsOwner;
            }

            if (audioListener != null)
            {
                audioListener.enabled = IsOwner;
            }

            if (IsServer && characterVisualPrefabs != null && characterVisualPrefabs.Length > 0)
            {
                CharacterIndex.Value = Random.Range(0, characterVisualPrefabs.Length);
            }

            CharacterIndex.OnValueChanged += HandleCharacterIndexChanged;
            ApplyCharacterVisual(CharacterIndex.Value);

            // Everything Update() does past this point (the MatchManager bootstrap below) is
            // owner-only - a remote client watching another player has nothing left to poll for,
            // so there's no reason to keep calling into this component every frame for the rest
            // of the match. Same reasoning PlayerMovement already disables itself for non-owners.
            if (!IsOwner)
            {
                enabled = false;
            }
        }

        private void Update()
        {
            // MatchManager might not exist yet at OnNetworkSpawn time - the host's own player
            // object actually spawns while still in the MainMenu scene (before the Arena scene,
            // and therefore MatchManager, is loaded); OnNetworkSpawn only fires once, so it can't
            // just retry there. Polling here picks it up the moment it becomes available,
            // regardless of exactly when that happens for host vs. a later-joining client.
            if (!IsOwner || hasSubscribedToMatchManager || MatchManager.Instance == null)
            {
                return;
            }

            hasSubscribedToMatchManager = true;
            TryPositionAtBarSpawn();
            MatchManager.Instance.PlayerAClientId.OnValueChanged += HandleLaneAssignmentChangedForSpawn;
            MatchManager.Instance.PlayerBClientId.OnValueChanged += HandleLaneAssignmentChangedForSpawn;
            MatchManager.Instance.PlayerAGalleryEntryToken.OnValueChanged += HandlePlayerAGalleryEntryToken;
            MatchManager.Instance.PlayerBGalleryEntryToken.OnValueChanged += HandlePlayerBGalleryEntryToken;
            MatchManager.Instance.MatchResetToken.OnValueChanged += HandleMatchResetToken;

            // Nothing left for Update() to do from here on - the rest of this object's behavior
            // is entirely event-driven (the OnValueChanged subscriptions above), so there's no
            // reason to keep polling every frame for the remainder of the match.
            enabled = false;
        }

        public override void OnNetworkDespawn()
        {
            CharacterIndex.OnValueChanged -= HandleCharacterIndexChanged;

            if (MatchManager.Instance != null)
            {
                MatchManager.Instance.PlayerAClientId.OnValueChanged -= HandleLaneAssignmentChangedForSpawn;
                MatchManager.Instance.PlayerBClientId.OnValueChanged -= HandleLaneAssignmentChangedForSpawn;
                MatchManager.Instance.PlayerAGalleryEntryToken.OnValueChanged -= HandlePlayerAGalleryEntryToken;
                MatchManager.Instance.PlayerBGalleryEntryToken.OnValueChanged -= HandlePlayerBGalleryEntryToken;
                MatchManager.Instance.MatchResetToken.OnValueChanged -= HandleMatchResetToken;
            }
        }

        private void HandleCharacterIndexChanged(int previous, int current)
        {
            ApplyCharacterVisual(current);
        }

        private void ApplyCharacterVisual(int index)
        {
            if (spawnedCharacterVisual != null)
            {
                Destroy(spawnedCharacterVisual);
                spawnedCharacterVisual = null;
            }

            if (index < 0 || characterVisualPrefabs == null || index >= characterVisualPrefabs.Length ||
                characterVisualPrefabs[index] == null || characterAttachPoint == null)
            {
                GetComponent<PlayerWeapon>()?.OnCharacterVisualChanged(null);
                GetComponent<PlayerCombatant>()?.OnCharacterVisualChanged(null);
                return;
            }

            spawnedCharacterVisual = Instantiate(characterVisualPrefabs[index], characterAttachPoint);
            spawnedCharacterVisual.transform.localPosition = Vector3.zero;
            spawnedCharacterVisual.transform.localRotation = Quaternion.identity;

            // Each character prefab bakes its own corrective scale (see CharacterScaleFix) since
            // the source models weren't uniformly sized - multiply by that instead of overwriting
            // it, so characterVisualScale stays available as a global fine-tuning knob on top.
            Vector3 prefabScale = characterVisualPrefabs[index].transform.localScale;
            spawnedCharacterVisual.transform.localScale = prefabScale * characterVisualScale;

            // Corrects for the Humanoid Animator's own muscle-space rest pose landing slightly
            // off from the raw bind pose CharacterScaleFix measured - see its doc comment.
            spawnedCharacterVisual.AddComponent<CharacterFloorAlignment>();

            if (placeholderBodyRenderer != null)
            {
                placeholderBodyRenderer.enabled = false;
            }

            // The revolver attaches to a hand bone, and the headshot hitbox to the head bone,
            // inside whichever character visual is currently instantiated - both need to know
            // every time it's rebuilt.
            GetComponent<PlayerWeapon>()?.OnCharacterVisualChanged(spawnedCharacterVisual);
            GetComponent<PlayerCombatant>()?.OnCharacterVisualChanged(spawnedCharacterVisual);
        }

        private void HandleLaneAssignmentChangedForSpawn(ulong previous, ulong current)
        {
            TryPositionAtBarSpawn();
        }

        private void TryPositionAtBarSpawn()
        {
            if (hasPositionedAtBarSpawn || MatchManager.Instance == null)
            {
                return;
            }

            Transform spawnPoint = MatchManager.Instance.GetBarSpawnPointForClient(OwnerClientId);
            if (spawnPoint == null)
            {
                return; // Not assigned a lane yet - HandleLaneAssignmentChangedForSpawn will retry.
            }

            TeleportSelfTo(spawnPoint);
            hasPositionedAtBarSpawn = true;
        }

        private void HandlePlayerAGalleryEntryToken(int previous, int current)
        {
            if (MatchManager.Instance != null && MatchManager.Instance.GetLaneForClient(OwnerClientId) == LaneSide.A)
            {
                TeleportSelfTo(MatchManager.Instance.GetGallerySpawnPointForClient(OwnerClientId));
            }
        }

        private void HandlePlayerBGalleryEntryToken(int previous, int current)
        {
            if (MatchManager.Instance != null && MatchManager.Instance.GetLaneForClient(OwnerClientId) == LaneSide.B)
            {
                TeleportSelfTo(MatchManager.Instance.GetGallerySpawnPointForClient(OwnerClientId));
            }
        }

        /// <summary>Fired once per MatchManager.ServerResetMatch call - a match just ended (win or
        /// lose screen shown for a few seconds) and both players go back to the bar, unlike
        /// TryPositionAtBarSpawn's initial-connect teleport, this always fires regardless of
        /// hasPositionedAtBarSpawn, since it needs to work every time a match ends, not just once.
        /// </summary>
        private void HandleMatchResetToken(int previous, int current)
        {
            if (MatchManager.Instance != null)
            {
                TeleportSelfTo(MatchManager.Instance.GetBarSpawnPointForClient(OwnerClientId));
            }
        }

        private void TeleportSelfTo(Transform target)
        {
            if (target == null)
            {
                return;
            }

            // CharacterController caches its own position each physics step and will otherwise
            // fight a direct transform set (or just silently ignore it).
            var controller = GetComponent<CharacterController>();
            if (controller != null)
            {
                controller.enabled = false;
            }

            transform.SetPositionAndRotation(target.position, target.rotation);

            if (controller != null)
            {
                controller.enabled = true;
            }
        }
    }
}
