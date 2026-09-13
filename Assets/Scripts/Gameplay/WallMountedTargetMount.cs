using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Carries the wall-mounted medallion targets along with DividerWall's drop/raise animation,
    /// without actually being a child of the wall's own GameObject. Two reasons that matters:
    /// Netcode for GameObjects doesn't reliably support a NetworkObject (the medallions) nested
    /// directly under another NetworkObject (the wall); and the wall's own localScale is
    /// wildly non-uniform (0.5, 4.5, 16 - it's a stretched cube), which would badly distort any
    /// child's explicit localScale if it inherited that instead of a plain (1,1,1) parent.
    ///
    /// Not a NetworkBehaviour and has no synced state of its own - it just mirrors the wall
    /// Transform's current world position every frame, and every client already runs
    /// WallController's own drop/raise animation identically off the same IsDropped
    /// NetworkVariable, so copying its position is automatically in sync everywhere for free.
    /// </summary>
    public class WallMountedTargetMount : MonoBehaviour
    {
        [SerializeField] private Transform wall;

        private void Update()
        {
            if (wall == null || transform.position == wall.position)
            {
                return;
            }

            transform.position = wall.position;
        }
    }
}
