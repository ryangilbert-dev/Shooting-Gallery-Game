using Unity.Netcode;
using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Doorway trigger between the bar (spawn) room and the gallery room. Server-authoritative:
    /// only the server acts on it, marking the crossing player as having entered the gallery and
    /// teleporting them to their assigned lane via MatchManager. Purely a physical scene trigger -
    /// not a NetworkObject itself.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class GalleryEntryTrigger : MonoBehaviour
    {
        private void OnTriggerEnter(Collider other)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            {
                return;
            }

            NetworkObject networkObject = other.GetComponentInParent<NetworkObject>();
            if (networkObject == null || MatchManager.Instance == null)
            {
                return;
            }

            MatchManager.Instance.NotifyPlayerEnteredGallery(networkObject.OwnerClientId);
        }
    }
}
