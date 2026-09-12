using Unity.Netcode;
using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Server-authoritative divider wall between the two gallery lanes. Sinks straight down
    /// through the floor when dropped (its collider moves with it, so it stops blocking both
    /// sightlines and shots the moment it's out of the way) and rises back after a fixed delay.
    /// Trigger it via ServerDropForSeconds() - currently called from MatchManager once a lane's
    /// hit-tracker bar fills. Runs its own countdown rather than exposing a timer NetworkVariable,
    /// since no client needs to see the remaining time yet (just the wall itself moving).
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class WallController : NetworkBehaviour
    {
        [SerializeField] private float dropDistance = 4.5f;
        [SerializeField] private float moveSpeed = 9f;

        public readonly NetworkVariable<bool> IsDropped = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private Vector3 raisedLocalPosition;
        private Vector3 loweredLocalPosition;
        private float serverDropCountdown;

        private void Awake()
        {
            raisedLocalPosition = transform.localPosition;
            loweredLocalPosition = raisedLocalPosition + Vector3.down * dropDistance;
        }

        private void Update()
        {
            // Every client (not just the server) eases the wall toward whatever IsDropped
            // currently says, so the animation plays identically everywhere without needing its
            // own synced position.
            Vector3 target = IsDropped.Value ? loweredLocalPosition : raisedLocalPosition;
            transform.localPosition = Vector3.MoveTowards(transform.localPosition, target, moveSpeed * Time.deltaTime);

            if (!IsServer || !IsDropped.Value)
            {
                return;
            }

            serverDropCountdown -= Time.deltaTime;
            if (serverDropCountdown <= 0f)
            {
                IsDropped.Value = false;
            }
        }

        /// <summary>Server-only: drops the wall now (or extends the drop if it's already down)
        /// and raises it again after the given number of seconds.</summary>
        public void ServerDropForSeconds(float seconds)
        {
            if (!IsServer)
            {
                return;
            }

            serverDropCountdown = seconds;
            IsDropped.Value = true;
        }
    }
}
