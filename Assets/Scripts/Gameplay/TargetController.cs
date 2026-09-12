using Unity.Netcode;
using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Server-authoritative practice target: turns green when a validated shot connects, then
    /// automatically resets after a short delay so it's endlessly re-shootable for practice.
    /// Marked hit via ServerMarkHit(), called from PlayerWeapon's fire ServerRpc once a raycast
    /// on the firing client actually connects with this target's collider.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class TargetController : NetworkBehaviour
    {
        [SerializeField] private Renderer targetRenderer;
        [SerializeField] private Material defaultMaterial;
        [SerializeField] private Material hitMaterial;
        [SerializeField] private float hitResetDelay = 1.5f;

        public readonly NetworkVariable<bool> IsHit = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private float resetTimer;

        public override void OnNetworkSpawn()
        {
            IsHit.OnValueChanged += HandleHitChanged;
            ApplyVisual(IsHit.Value);
        }

        public override void OnNetworkDespawn()
        {
            IsHit.OnValueChanged -= HandleHitChanged;
        }

        private void Update()
        {
            if (!IsServer || !IsHit.Value)
            {
                return;
            }

            resetTimer -= Time.deltaTime;
            if (resetTimer <= 0f)
            {
                IsHit.Value = false;
            }
        }

        private void HandleHitChanged(bool previous, bool current)
        {
            ApplyVisual(current);
        }

        private void ApplyVisual(bool hit)
        {
            if (targetRenderer == null)
            {
                return;
            }

            targetRenderer.sharedMaterial = hit ? hitMaterial : defaultMaterial;
        }

        /// <summary>Server-only: marks this target hit and starts its reset countdown.</summary>
        public void ServerMarkHit()
        {
            if (!IsServer)
            {
                return;
            }

            IsHit.Value = true;
            resetTimer = hitResetDelay;
        }
    }
}
