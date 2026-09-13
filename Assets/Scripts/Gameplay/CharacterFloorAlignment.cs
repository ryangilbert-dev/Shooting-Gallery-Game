using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// One-time corrective rescale + vertical nudge for a character visual. The Humanoid Animator
    /// added by CharacterAnimationSetup repositions the skeleton via its own muscle-space
    /// calibration the moment it starts evaluating - including for the empty "Idle" state, which
    /// has no motion clip assigned and so just holds the avatar's own reconstructed rest pose.
    /// That measured rest pose can be both a different overall height AND a different floor
    /// contact point than the raw imported bind pose CharacterScaleFix originally measured to
    /// place characters on the floor - a pure position fix wasn't enough on its own, since the
    /// head could still land short of (or past) where the camera expects it even once the feet
    /// were correctly grounded.
    ///
    /// Fixes both, in LateUpdate so this always runs after that frame's Animator evaluation:
    ///  1. Rescales the whole visual so its measured height matches the target height implied by
    ///     the player's own camera - reading the camera's actual (already-correct, fixed at
    ///     prefab-build-time by PlayerMovementSetup) eye position directly, rather than assuming
    ///     it, so this stays correct even if that eye-height formula ever changes. Falls back to
    ///     a fixed constant (matching CharacterScaleFix.TargetHeight, which isn't referenceable
    ///     from a runtime script) for the practice dummy, which has no camera of its own.
    ///  2. Re-measures after rescaling (scale moved every point, including the feet, relative to
    ///     the pivot) and nudges the whole visual up or down so its lowest point sits exactly on
    ///     the floor (CharacterAttachPoint's own world Y, by design).
    ///
    /// A one-time correction rather than continuous re-measurement, since the avatar's own
    /// calibration offset is constant regardless of pose (the walk cycle's own bob rides on top
    /// of this, unaffected) - continuously rescaling/repositioning every frame would fight the
    /// animation instead. Disables itself once applied.
    /// </summary>
    public class CharacterFloorAlignment : MonoBehaviour
    {
        // Matches CharacterScaleFix.TargetHeight and PlayerMovementSetup's own eye-height
        // fraction - duplicated here because those are Editor-only scripts, not referenceable
        // from this runtime one. Only used as a fallback when no camera is found (the dummy).
        private const float FallbackTargetHeight = 5f * (2f / 3f);
        private const float EyeHeightFraction = 0.92f;

        private void LateUpdate()
        {
            if (!TryMeasureBounds(out Bounds bounds) || transform.parent == null)
            {
                return; // Renderers might not exist yet this exact frame - try again next frame.
            }

            float floorY = transform.parent.position.y;
            float targetHeight = DetermineTargetHeight(floorY);

            float measuredHeight = bounds.size.y;
            if (measuredHeight > 0.001f)
            {
                transform.localScale *= targetHeight / measuredHeight;

                // Rescaling moved every point (including the feet) relative to the pivot, so the
                // bounds measured above are now stale - re-measure before positioning below.
                if (!TryMeasureBounds(out bounds))
                {
                    return;
                }
            }

            float correction = floorY - bounds.min.y;
            transform.position += new Vector3(0f, correction, 0f);

            enabled = false; // One-time correction - nothing left to do.
        }

        private bool TryMeasureBounds(out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds;
        }

        /// <summary>Prefers the owning player's actual camera - its eye height is already correct
        /// and fixed, so reading it directly ties this character's target height to exactly what
        /// the camera assumes, rather than a separately guessed constant. The camera lives on the
        /// player root, a sibling of CharacterAttachPoint (this object's parent) rather than an
        /// ancestor, hence searching from there instead of GetComponentInParent.</summary>
        private float DetermineTargetHeight(float floorY)
        {
            Transform root = transform.parent.parent;
            Camera ownerCamera = root != null ? root.GetComponentInChildren<Camera>(true) : null;
            if (ownerCamera == null)
            {
                return FallbackTargetHeight; // The practice dummy has no camera of its own.
            }

            return (ownerCamera.transform.position.y - floorY) / EyeHeightFraction;
        }
    }
}
