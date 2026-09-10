using Unity.Netcode;
using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Per-player networked behaviour. M1 scope only: camera/audio ownership so each
    /// client only sees through their own eyes, plus a lane-colored tint so two Editor
    /// instances can visually confirm they were assigned to opposite lanes. Aim input,
    /// shooting, and dodge movement are added in later milestones.
    /// </summary>
    public class PlayerController : NetworkBehaviour
    {
        [SerializeField] private Camera playerCamera;
        [SerializeField] private AudioListener audioListener;
        [SerializeField] private Renderer bodyRenderer;
        [SerializeField] private Material laneAMaterial;
        [SerializeField] private Material laneBMaterial;

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

            if (MatchManager.Instance != null)
            {
                RefreshLaneTint();
                MatchManager.Instance.PlayerAClientId.OnValueChanged += HandleLaneAssignmentChanged;
                MatchManager.Instance.PlayerBClientId.OnValueChanged += HandleLaneAssignmentChanged;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (MatchManager.Instance != null)
            {
                MatchManager.Instance.PlayerAClientId.OnValueChanged -= HandleLaneAssignmentChanged;
                MatchManager.Instance.PlayerBClientId.OnValueChanged -= HandleLaneAssignmentChanged;
            }
        }

        private void HandleLaneAssignmentChanged(ulong previous, ulong current)
        {
            RefreshLaneTint();
        }

        private void RefreshLaneTint()
        {
            if (bodyRenderer == null || MatchManager.Instance == null)
            {
                return;
            }

            switch (MatchManager.Instance.GetLaneForClient(OwnerClientId))
            {
                case LaneSide.A:
                    if (laneAMaterial != null)
                    {
                        bodyRenderer.material = laneAMaterial;
                    }
                    break;
                case LaneSide.B:
                    if (laneBMaterial != null)
                    {
                        bodyRenderer.material = laneBMaterial;
                    }
                    break;
            }
        }
    }
}
