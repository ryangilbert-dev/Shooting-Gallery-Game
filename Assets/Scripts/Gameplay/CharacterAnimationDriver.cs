using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Feeds the currently-worn character visual's Animator a normalized "Speed" parameter driven
    /// by the player's actual CharacterController velocity, so the walk cycle set up by
    /// CharacterAnimationSetup plays while moving and settles back to Idle while still. Added
    /// directly onto each character prefab by that same tool (not wired through PlayerController
    /// like PlayerWeapon/PlayerCombatant) since it needs no bone lookups of its own - it just
    /// reads its own Animator and its parent's CharacterController every frame.
    ///
    /// Harmless on the practice dummy too, which also wears these character prefabs but has no
    /// CharacterController to find - it simply never finds one and the Animator sits in its
    /// default Idle state, which is exactly right for a stationary target.
    /// </summary>
    public class CharacterAnimationDriver : MonoBehaviour
    {
        private const string SpeedParam = "Speed";

        // Roughly PlayerMovement's moveSpeed - the value at which the walk cycle should be at
        // "full speed" (Speed = 1). Re-tune here if that field ever changes.
        [SerializeField] private float speedNormalizer = 5f;

        private Animator animator;
        private CharacterController ownerController;

        private void Awake()
        {
            animator = GetComponent<Animator>();
        }

        private void Update()
        {
            // The animator.avatar/runtimeAnimatorController wiring happens in a separate editor
            // tool (CharacterAnimationSetup) from the AddComponent<Animator> that put this
            // Animator here in the first place - if that tool's Humanoid avatar validation ever
            // fails for this specific character (see its doc comment), this Animator exists but
            // has no controller assigned. SetFloat on an uninitialized Animator throws, so guard
            // it explicitly rather than spamming the Console every frame for every such character.
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                return;
            }

            if (ownerController == null)
            {
                // The character visual is parented under CharacterAttachPoint, itself a child of
                // the player root - CharacterController lives on that root (see
                // PlayerMovementSetup). Also never found on the practice dummy, which has none.
                ownerController = GetComponentInParent<CharacterController>();
                if (ownerController == null)
                {
                    return;
                }
            }

            Vector3 horizontalVelocity = ownerController.velocity;
            horizontalVelocity.y = 0f;
            float normalizedSpeed = Mathf.Clamp01(horizontalVelocity.magnitude / speedNormalizer);
            animator.SetFloat(SpeedParam, normalizedSpeed);
        }
    }
}
