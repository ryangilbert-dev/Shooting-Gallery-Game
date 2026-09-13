using UnityEngine;

namespace ShootingGallery.Gameplay
{
    /// <summary>
    /// Shared helper for finding a named bone anywhere in a character rig's hierarchy - used by
    /// anything that needs to attach something to a specific bone (the revolver's hand attachment
    /// in PlayerWeapon, the headshot hitbox in PlayerCombatant/DummyEnemyController).
    /// </summary>
    public static class BoneFinder
    {
        public static Transform FindDeepChild(Transform parent, string name)
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
