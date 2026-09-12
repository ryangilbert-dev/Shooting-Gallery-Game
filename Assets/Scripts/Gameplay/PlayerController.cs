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

            if (placeholderBodyRenderer != null)
            {
                placeholderBodyRenderer.enabled = false;
            }

            // The revolver attaches to a hand bone inside whichever character visual is currently
            // instantiated, so PlayerWeapon needs to know every time it's rebuilt.
            GetComponent<PlayerWeapon>()?.OnCharacterVisualChanged(spawnedCharacterVisual);
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
