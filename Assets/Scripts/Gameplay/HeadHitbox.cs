using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Marker component on the collider that represents "the head" for headshot detection. Never
    /// authored on a prefab directly - spawned at runtime via Attach() any time a combatant's
    /// character visual (re)spawns, by PlayerCombatant (real players) and DummyEnemyController
    /// (the practice dummy).
    ///
    /// The actual "does this hit count, whose life does it cost" logic is entirely server-side in
    /// MatchManager.RegisterHeadshot, reached via PlayerWeapon's headshot ServerRpc once a
    /// shooting client's own raycast reports hitting one of these - this component only owns
    /// finding/sizing/tracking the hitbox itself.
    ///
    /// When it's built from a real head mesh (see Attach), it re-measures that renderer's live
    /// world-space bounds every frame instead of only once at attach time - a one-time snapshot
    /// used to work fine back when characters stood still, but now that walk animation is live,
    /// a skinned head visibly moves/deforms relative to its own renderer's Transform as the clip
    /// plays, and a fixed offset would drift out of sync with where the head actually renders.
    /// Re-measuring every frame instead means it always matches the current pose exactly,
    /// regardless of animation, retargeting quirks, or anything else that moves the mesh.
    /// </summary>
    public class HeadHitbox : MonoBehaviour
    {
        // How much bigger than the head mesh's own bounding sphere to make the hitbox - a
        // deliberately generous fudge factor so hits register reliably while this is actively
        // being tested (win/lose flow, headshot feel), rather than demanding pixel-precise aim.
        // Dial back toward 1.0 later once the underlying mechanic is confirmed solid.
        private const float BoundsPadding = 1.35f;

        private Renderer trackedRenderer;
        private SphereCollider sphereCollider;

        /// <summary>
        /// Builds a headshot hitbox sized to the actual head geometry where one can be found,
        /// rather than a fixed guessed radius. Tries, in order:
        ///  1. A renderer parented under the head bone in the rig hierarchy - some of this game's
        ///     character models (BountyHunter, EliteCowboy1/2) have a distinct head piece rigidly
        ///     attached there (so hats/heads could in principle be swapped); when one exists, its
        ///     own mesh bounds make a precise, self-sizing hitbox that matches what's actually
        ///     drawn on screen, re-measured every frame (see class doc comment).
        ///  2. A renderer anywhere on the character whose material name contains "head" (the name
        ///     CharacterSetup gives a body part's material when it finds a texture matching that
        ///     part - see CharacterSetup.BuildCharacterPrefab) - catches a distinct head mesh that
        ///     for some reason isn't parented under the bone.
        ///  3. A sphere anchored to the head bone with a guessed radius/offset - the fallback for
        ///     characters with no separable head geometry at all (a single continuous body mesh,
        ///     e.g. the basic Cowboy1-4/Woman models), which simply have nothing else to measure
        ///     from. Since this rides on the bone's own Transform (already correctly animated by
        ///     the Humanoid rig), it doesn't need per-frame tracking of its own.
        ///
        /// For this to work as "only a headshot counts" in every case, the bone's own body
        /// collider (CharacterController or CapsuleCollider) must be on the built-in "Ignore
        /// Raycast" layer (see DuelSetup) - otherwise a shot aimed at the head would hit the
        /// body's own collider first and never reach whichever hitbox this produces.
        /// </summary>
        public static GameObject Attach(GameObject characterVisual, Transform headBone, float fallbackRadius, Vector3 fallbackLocalOffset)
        {
            if (headBone == null)
            {
                return null;
            }

            Renderer headRenderer = FindHeadBoneChildRenderer(headBone) ?? FindHeadNamedRenderer(characterVisual);
            return headRenderer != null
                ? AttachTrackingRendererBounds(headRenderer)
                : AttachFallbackSphere(headBone, fallbackRadius, fallbackLocalOffset);
        }

        private static Renderer FindHeadBoneChildRenderer(Transform headBone)
        {
            return headBone.GetComponentInChildren<Renderer>();
        }

        private static Renderer FindHeadNamedRenderer(GameObject characterVisual)
        {
            if (characterVisual == null)
            {
                return null;
            }

            foreach (Renderer renderer in characterVisual.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.sharedMaterial != null &&
                    renderer.sharedMaterial.name.IndexOf("head", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return renderer;
                }
            }

            return null;
        }

        private static GameObject AttachTrackingRendererBounds(Renderer headRenderer)
        {
            GameObject hitboxGO = new GameObject("HeadHitbox");
            hitboxGO.layer = 0; // Default - must stay on a layer Physics.Raycast's default mask includes.
            hitboxGO.transform.SetParent(headRenderer.transform, false);

            var collider = hitboxGO.AddComponent<SphereCollider>();
            collider.isTrigger = true;

            var hitbox = hitboxGO.AddComponent<HeadHitbox>();
            hitbox.trackedRenderer = headRenderer;
            hitbox.sphereCollider = collider;
            hitbox.ApplyLiveBounds(); // Set an initial size/position immediately, not just next frame.

            return hitboxGO;
        }

        private static GameObject AttachFallbackSphere(Transform headBone, float radius, Vector3 localOffset)
        {
            GameObject hitboxGO = new GameObject("HeadHitbox");
            hitboxGO.layer = 0;
            hitboxGO.transform.SetParent(headBone, false);
            hitboxGO.transform.localPosition = localOffset;

            var collider = hitboxGO.AddComponent<SphereCollider>();
            collider.radius = radius;
            collider.isTrigger = true;

            hitboxGO.AddComponent<HeadHitbox>();
            return hitboxGO;
        }

        private void Update()
        {
            if (trackedRenderer == null)
            {
                return; // Fallback-sphere case - already tracks correctly via bone parenting alone.
            }

            ApplyLiveBounds();
        }

        private void ApplyLiveBounds()
        {
            // Renderer.bounds is a world-space AABB reflecting the mesh's actual current size and
            // shape for THIS frame's pose - re-reading it every frame is what keeps this in sync
            // with a moving/animating character instead of a one-time snapshot drifting out of
            // date. A sphere (rather than a box matching the AABB) sidesteps having to reason
            // about the renderer transform's own rotation when converting a world-space size into
            // the collider's local space - its half-diagonal length is the smallest sphere
            // guaranteed to fully contain the head's bounding box, padded further by
            // BoundsPadding for reliable hit registration while this is under active testing.
            Bounds worldBounds = trackedRenderer.bounds;
            transform.position = worldBounds.center;
            float worldRadius = worldBounds.extents.magnitude * BoundsPadding;

            // SphereCollider.radius is local-space and gets multiplied by the GameObject's own
            // lossy scale to find its actual world-space size - counteract that the same way
            // PlayerWeapon.RefreshRevolverAttachment corrects for the character rig's own scale,
            // so the sphere ends up worldRadius regardless of how the character happens to be
            // scaled.
            float parentScale = Mathf.Max(transform.lossyScale.x, 0.0001f);
            sphereCollider.radius = worldRadius / parentScale;
        }
    }
}
