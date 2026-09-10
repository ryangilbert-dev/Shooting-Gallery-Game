using UnityEditor;
using UnityEngine;

/// <summary>
/// Measures each character prefab's actual mesh bounds and bakes a corrective scale into its
/// root transform so it comes out to roughly TargetHeight tall - about a third shorter than the
/// 5-unit room ceiling (see ProjectScaffolder.BuildArenaScene), per playtest feedback that the
/// default FBX import scale made characters comically huge. Idempotent: always re-measures at
/// scale 1 first, so re-running after changing TargetHeight just re-corrects.
/// </summary>
public static class CharacterScaleFix
{
    private const string PrefabFolder = "Assets/Prefabs/Characters";
    private const float TargetHeight = 5f * (2f / 3f);

    [MenuItem("Tools/Shooting Gallery/Fix Character Scale")]
    public static void FixScale()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject instance = PrefabUtility.LoadPrefabContents(path);
            instance.transform.localScale = Vector3.one;

            bool hasBounds = false;
            Bounds bounds = default;
            foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (!hasBounds)
                {
                    bounds = r.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }

            if (!hasBounds)
            {
                Debug.LogWarning($"[CharacterScaleFix] No renderers found in {path}, skipping.");
                PrefabUtility.UnloadPrefabContents(instance);
                continue;
            }

            float measuredHeight = bounds.size.y;
            float scale = measuredHeight > 0.001f ? TargetHeight / measuredHeight : 1f;
            instance.transform.localScale = Vector3.one * scale;

            Debug.Log($"[CharacterScaleFix] {path}: measured height {measuredHeight:F2} -> scale {scale:F3} (target {TargetHeight:F2})");

            PrefabUtility.SaveAsPrefabAsset(instance, path);
            PrefabUtility.UnloadPrefabContents(instance);
        }

        AssetDatabase.SaveAssets();
    }
}
