using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Attaches the headshot hitbox to a real player's currently-worn character visual. Exists
    /// purely to manage that attachment (find the head bone, spawn/respawn HeadHitbox on it) -
    /// the actual "does a hit count, whose life does it cost" logic is server-side in
    /// MatchManager.RegisterHeadshot, called from PlayerWeapon's headshot ServerRpc.
    ///
    /// Not a NetworkBehaviour: nothing here needs to be synced over the network on its own - each
    /// client rebuilds the same hitbox locally the same way PlayerWeapon rebuilds the revolver,
    /// and the player's own NetworkTransform already keeps everyone's head in the right place.
    /// </summary>
    public class PlayerCombatant : MonoBehaviour
    {
        // Same CC_Base_* naming convention as PlayerWeapon's hand/arm bones (CC_Base_R_Hand,
        // CC_Base_R_Upperarm), which are already confirmed working against this character rig.
        [SerializeField] private string headBoneName = "CC_Base_Head";

        // Only used as a last resort by HeadHitbox.Attach, for a character with no separable
        // head mesh to measure instead (see its doc comment) - tune live in Play mode
        // (Hierarchy > PlayerPrefab(Clone) > Player Combatant) the same way PlayerWeapon's
        // offsets are tuned, if a character on this fallback path needs adjusting. Sized
        // generously and offset upward by default (most rigs' head bone pivot sits near the base
        // of the skull/neck, not centered on the head) for reliable hits while this is under
        // active testing - dial back once confirmed solid.
        [SerializeField] private float fallbackHeadHitboxRadius = 0.3f;
        [SerializeField] private Vector3 fallbackHeadHitboxLocalOffset = new Vector3(0f, 0.15f, 0f);

        private GameObject spawnedHeadHitbox;

        /// <summary>Called by PlayerController whenever the random character visual (re)spawns,
        /// exactly like PlayerWeapon.OnCharacterVisualChanged - the head bone to attach to only
        /// exists inside that instance.</summary>
        public void OnCharacterVisualChanged(GameObject characterVisual)
        {
            if (spawnedHeadHitbox != null)
            {
                Destroy(spawnedHeadHitbox);
                spawnedHeadHitbox = null;
            }

            if (characterVisual == null)
            {
                return;
            }

            Transform headBone = BoneFinder.FindDeepChild(characterVisual.transform, headBoneName);
            spawnedHeadHitbox = HeadHitbox.Attach(characterVisual, headBone, fallbackHeadHitboxRadius, fallbackHeadHitboxLocalOffset);
        }
    }
}
