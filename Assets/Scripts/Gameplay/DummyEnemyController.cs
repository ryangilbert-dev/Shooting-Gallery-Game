using Unity.Netcode;
using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Marker + visual/hitbox setup for the practice dummy that MatchManager spawns into an empty
    /// lane so a solo player can test the duel exchange without a second person (see
    /// MatchManager.TrySpawnDummyForSoloTesting). Deliberately stationary - it never moves or
    /// shoots back; its whole job is to stand there wearing a random cowboy body and expose a
    /// headshottable HeadHitbox, mirroring how a real opponent's PlayerCombatant does the same.
    /// Lives (MatchManager.DummyLives) and the actual duel-window/damage logic live on
    /// MatchManager, same as the real players' - this component only owns the cosmetic body.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class DummyEnemyController : NetworkBehaviour
    {
        [SerializeField] private Transform characterAttachPoint;
        [SerializeField] private GameObject[] characterVisualPrefabs;
        [SerializeField] private float characterVisualScale = 1f;

        [SerializeField] private string headBoneName = "CC_Base_Head";

        // Only used as a last resort by HeadHitbox.Attach, for a character with no separable
        // head mesh to measure instead - see its doc comment. Sized generously and offset upward
        // by default (see PlayerCombatant's matching fields) for reliable hits while this is
        // under active testing.
        [SerializeField] private float fallbackHeadHitboxRadius = 0.3f;
        [SerializeField] private Vector3 fallbackHeadHitboxLocalOffset = new Vector3(0f, 0.15f, 0f);

        public readonly NetworkVariable<int> CharacterIndex = new NetworkVariable<int>(
            -1,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private GameObject spawnedCharacterVisual;
        private GameObject spawnedHeadHitbox;

        public override void OnNetworkSpawn()
        {
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

            if (spawnedHeadHitbox != null)
            {
                Destroy(spawnedHeadHitbox);
                spawnedHeadHitbox = null;
            }

            if (index < 0 || characterVisualPrefabs == null || index >= characterVisualPrefabs.Length ||
                characterVisualPrefabs[index] == null || characterAttachPoint == null)
            {
                return;
            }

            spawnedCharacterVisual = Instantiate(characterVisualPrefabs[index], characterAttachPoint);
            spawnedCharacterVisual.transform.localPosition = Vector3.zero;
            spawnedCharacterVisual.transform.localRotation = Quaternion.identity;

            Vector3 prefabScale = characterVisualPrefabs[index].transform.localScale;
            spawnedCharacterVisual.transform.localScale = prefabScale * characterVisualScale;

            // Corrects for the Humanoid Animator's own muscle-space rest pose landing slightly
            // off from the raw bind pose CharacterScaleFix measured - see its doc comment.
            spawnedCharacterVisual.AddComponent<CharacterFloorAlignment>();

            Transform headBone = BoneFinder.FindDeepChild(spawnedCharacterVisual.transform, headBoneName);
            spawnedHeadHitbox = HeadHitbox.Attach(spawnedCharacterVisual, headBone, fallbackHeadHitboxRadius, fallbackHeadHitboxLocalOffset);
        }
    }
}
