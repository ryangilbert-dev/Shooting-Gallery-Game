using Unity.Netcode;
using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Per-player networked behaviour. Handles camera/audio ownership so each client only sees
    /// through their own eyes, and picks a random cosmetic character model (server-authoritative,
    /// synced via CharacterIndex) so both clients see the same body on each player. Aim input,
    /// shooting, and dodge movement are added in later milestones.
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

        public override void OnNetworkDespawn()
        {
            CharacterIndex.OnValueChanged -= HandleCharacterIndexChanged;
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
    }
}
