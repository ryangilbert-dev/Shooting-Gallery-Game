using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Owner-driven revolver draw/holster, ammo tracking, and firing. E toggles readied/holstered;
    /// while readied, left click fires (one of six shots, raycast-validated against
    /// TargetController hits via a server RPC) and R reloads instantly. Ready-state/ammo aren't
    /// server-validated yet - matches PlayerMovement's "client-authoritative for now" approach -
    /// but the actual hit registration on a target IS server-authoritative, since that's shared
    /// state every client needs to agree on.
    ///
    /// Two separate revolver visuals exist: one parented to the character's hand bone (third
    /// person - what other players see on your body) and one parented to a point fixed to your
    /// own camera (first person viewmodel - what only you see, deliberately decoupled from the
    /// character rig so it doesn't depend on guessing that rig's bone-rotation conventions).
    /// </summary>
    public class PlayerWeapon : NetworkBehaviour
    {
        private const int MaxAmmo = 6;

        [SerializeField] private Camera playerCamera;
        [SerializeField] private float fireRange = 50f;
        [SerializeField] private GameObject revolverPrefab;
        [SerializeField] private string handBoneName = "CC_Base_R_Hand";

        [Header("Third-person hand attachment (what other players see)")]
        [SerializeField] private Vector3 revolverLocalPositionOffset = Vector3.zero;
        [SerializeField] private Vector3 revolverLocalEulerOffset = Vector3.zero;

        [Header("First-person viewmodel (owner-only, camera-attached)")]
        [SerializeField] private Transform viewmodelAnchor;
        [SerializeField] private Vector3 viewmodelLocalPositionOffset = Vector3.zero;
        // Screenshot showed the model viewed almost straight down its own barrel/cylinder axis
        // (foreshortened to a flat-looking blob) instead of in profile - a starting guess to swing
        // it side-on, per "rotated 90 twice" - but reapplied every frame (see Update), so nudge
        // this live in the Inspector while readied in Play mode until it actually looks right.
        [SerializeField] private Vector3 viewmodelLocalEulerOffset = new Vector3(0f, 90f, 90f);

        [Header("Arm raise pose when readied (rough guess - tune live in Play mode)")]
        [SerializeField] private string upperArmBoneName = "CC_Base_R_Upperarm";
        [SerializeField] private string forearmBoneName = "CC_Base_R_Forearm";
        // Defaulted to zero (no pose change) - a first blind guess swung the arm straight into
        // the camera (confirmed via screenshot). Tune live in the Inspector during Play mode:
        // these reapply every frame, so nudging one axis at a time while readied and watching the
        // Scene view (or your own screen) is much faster than another blind guess.
        [SerializeField] private Vector3 upperArmRaiseEuler = Vector3.zero;
        [SerializeField] private Vector3 forearmRaiseEuler = Vector3.zero;

        public readonly NetworkVariable<bool> IsReadied = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public readonly NetworkVariable<int> CurrentAmmo = new NetworkVariable<int>(
            MaxAmmo,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        private Transform currentHandBone;
        private Transform upperArmBone;
        private Transform forearmBone;
        private Quaternion upperArmBindRotation;
        private Quaternion forearmBindRotation;
        private GameObject spawnedRevolver;
        private GameObject spawnedViewmodel;

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
            // Runs for every client, not just the owner - the raised-arm pose is a third-person
            // pose everyone needs to see, and reapplying every frame is what makes the Inspector
            // fields tunable live during Play mode.
            ApplyArmPose();

            // Same reasoning as the arm pose: reapplied every frame (not just when the viewmodel
            // is first spawned) so viewmodelLocalPositionOffset/viewmodelLocalEulerOffset are
            // tunable live in the Inspector while readied, instead of needing another guess-build-
            // screenshot round trip.
            if (spawnedViewmodel != null)
            {
                spawnedViewmodel.transform.localPosition = viewmodelLocalPositionOffset;
                spawnedViewmodel.transform.localRotation = Quaternion.Euler(viewmodelLocalEulerOffset);
            }

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

            if (playerCamera == null)
            {
                return;
            }

            // Cast from the owner's own camera - immediate, local feedback. The actual hit only
            // takes effect once the server applies it (see RequestHitTargetServerRpc), so a wall
            // or another object in the way correctly blocks the shot rather than needing a
            // separate occlusion check.
            if (Physics.Raycast(playerCamera.transform.position, playerCamera.transform.forward, out RaycastHit hit, fireRange))
            {
                NetworkObject targetNetworkObject = hit.collider.GetComponentInParent<NetworkObject>();
                if (targetNetworkObject != null && targetNetworkObject.TryGetComponent(out TargetController _))
                {
                    RequestHitTargetServerRpc(new NetworkObjectReference(targetNetworkObject));
                }
            }
        }

        [ServerRpc]
        private void RequestHitTargetServerRpc(NetworkObjectReference targetRef)
        {
            if (targetRef.TryGet(out NetworkObject targetObject) && targetObject.TryGetComponent(out TargetController target))
            {
                target.ServerMarkHit();
            }
        }

        private void Reload()
        {
            CurrentAmmo.Value = MaxAmmo;
            Debug.Log("[PlayerWeapon] Reloaded.");
        }

        /// <summary>Called by PlayerController whenever the random character visual (re)spawns,
        /// since the hand/arm bones to use live inside that instance. Runs on every client, not
        /// just the owner - each client holds its own local copy of the visual hierarchy.</summary>
        public void OnCharacterVisualChanged(GameObject characterVisual)
        {
            if (characterVisual != null)
            {
                currentHandBone = FindDeepChild(characterVisual.transform, handBoneName);
                upperArmBone = FindDeepChild(characterVisual.transform, upperArmBoneName);
                forearmBone = FindDeepChild(characterVisual.transform, forearmBoneName);
                upperArmBindRotation = upperArmBone != null ? upperArmBone.localRotation : Quaternion.identity;
                forearmBindRotation = forearmBone != null ? forearmBone.localRotation : Quaternion.identity;
            }
            else
            {
                currentHandBone = null;
                upperArmBone = null;
                forearmBone = null;
            }

            RefreshRevolverAttachment();
        }

        private void HandleReadyChanged(bool previous, bool current)
        {
            RefreshRevolverAttachment();
            RefreshViewmodel();
        }

        private void ApplyArmPose()
        {
            bool readied = IsReadied.Value;

            if (upperArmBone != null)
            {
                upperArmBone.localRotation = readied
                    ? upperArmBindRotation * Quaternion.Euler(upperArmRaiseEuler)
                    : upperArmBindRotation;
            }

            if (forearmBone != null)
            {
                forearmBone.localRotation = readied
                    ? forearmBindRotation * Quaternion.Euler(forearmRaiseEuler)
                    : forearmBindRotation;
            }
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

            // The hand bone lives inside a character shrunk down by CharacterScaleFix (to ~1/6-1/8
            // its raw size), and Instantiate(prefab, parent) keeps the prefab's own baked local
            // scale - so without this correction the revolver inherits that shrink and comes out
            // ~6x too big. Counteract the parent's lossy scale so it renders at its intended size
            // regardless of which character (and therefore which shrink factor) is currently worn.
            Vector3 parentLossyScale = currentHandBone.lossyScale;
            Vector3 prefabScale = revolverPrefab.transform.localScale;
            spawnedRevolver.transform.localScale = new Vector3(
                prefabScale.x / Mathf.Max(parentLossyScale.x, 0.0001f),
                prefabScale.y / Mathf.Max(parentLossyScale.y, 0.0001f),
                prefabScale.z / Mathf.Max(parentLossyScale.z, 0.0001f));
        }

        private void RefreshViewmodel()
        {
            if (spawnedViewmodel != null)
            {
                Destroy(spawnedViewmodel);
                spawnedViewmodel = null;
            }

            // Owner-only: this exists purely so you can see your own gun. No inherited-scale
            // correction needed here - the camera/anchor chain is never scaled, unlike the hand
            // bone above.
            if (!IsOwner || !IsReadied.Value || revolverPrefab == null || viewmodelAnchor == null)
            {
                return;
            }

            spawnedViewmodel = Instantiate(revolverPrefab, viewmodelAnchor);
            spawnedViewmodel.transform.localPosition = viewmodelLocalPositionOffset;
            spawnedViewmodel.transform.localRotation = Quaternion.Euler(viewmodelLocalEulerOffset);
            spawnedViewmodel.transform.localScale = revolverPrefab.transform.localScale;
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
