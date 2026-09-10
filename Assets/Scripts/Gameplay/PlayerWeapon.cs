using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Owner-driven revolver draw/holster and ammo tracking. E toggles readied/holstered; while
    /// readied, left click fires (one of six shots) and R reloads instantly. Not server-validated
    /// yet - matches PlayerMovement's "client-authoritative for now" approach; revisit once this
    /// needs to be trusted (e.g. actually hitting the other player in a duel).
    /// </summary>
    public class PlayerWeapon : NetworkBehaviour
    {
        private const int MaxAmmo = 6;

        [SerializeField] private GameObject revolverPrefab;
        [SerializeField] private Vector3 revolverLocalPositionOffset = Vector3.zero;
        [SerializeField] private Vector3 revolverLocalEulerOffset = Vector3.zero;
        [SerializeField] private string handBoneName = "CC_Base_R_Hand";

        public readonly NetworkVariable<bool> IsReadied = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public readonly NetworkVariable<int> CurrentAmmo = new NetworkVariable<int>(
            MaxAmmo,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        private Transform currentHandBone;
        private GameObject spawnedRevolver;

        public override void OnNetworkSpawn()
        {
            IsReadied.OnValueChanged += HandleReadyChanged;
        }

        public override void OnNetworkDespawn()
        {
            IsReadied.OnValueChanged -= HandleReadyChanged;
        }

        private void Update()
        {
            if (!IsOwner || Keyboard.current == null)
            {
                return;
            }

            if (Keyboard.current.eKey.wasPressedThisFrame)
            {
                IsReadied.Value = !IsReadied.Value;
            }

            if (!IsReadied.Value)
            {
                return;
            }

            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            {
                TryFire();
            }

            if (Keyboard.current.rKey.wasPressedThisFrame)
            {
                Reload();
            }
        }

        private void TryFire()
        {
            if (CurrentAmmo.Value <= 0)
            {
                Debug.Log("[PlayerWeapon] Out of ammo - press R to reload.");
                return;
            }

            CurrentAmmo.Value--;
            Debug.Log($"[PlayerWeapon] Fired - {CurrentAmmo.Value}/{MaxAmmo} shots left.");
        }

        private void Reload()
        {
            CurrentAmmo.Value = MaxAmmo;
            Debug.Log("[PlayerWeapon] Reloaded.");
        }

        /// <summary>Called by PlayerController whenever the random character visual (re)spawns,
        /// since the hand bone to attach to lives inside that instance. Runs on every client, not
        /// just the owner - each client holds its own local copy of the visual hierarchy.</summary>
        public void OnCharacterVisualChanged(GameObject characterVisual)
        {
            currentHandBone = characterVisual != null ? FindDeepChild(characterVisual.transform, handBoneName) : null;
            RefreshRevolverAttachment();
        }

        private void HandleReadyChanged(bool previous, bool current)
        {
            RefreshRevolverAttachment();
        }

        private void RefreshRevolverAttachment()
        {
            if (spawnedRevolver != null)
            {
                Destroy(spawnedRevolver);
                spawnedRevolver = null;
            }

            if (!IsReadied.Value || currentHandBone == null || revolverPrefab == null)
            {
                return;
            }

            spawnedRevolver = Instantiate(revolverPrefab, currentHandBone);
            spawnedRevolver.transform.localPosition = revolverLocalPositionOffset;
            spawnedRevolver.transform.localRotation = Quaternion.Euler(revolverLocalEulerOffset);
        }

        private static Transform FindDeepChild(Transform parent, string name)
        {
            if (parent.name == name)
            {
                return parent;
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform result = FindDeepChild(parent.GetChild(i), name);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
    }
}
