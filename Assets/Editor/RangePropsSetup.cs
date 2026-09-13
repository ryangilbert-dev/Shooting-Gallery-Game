using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds usable prefabs out of two raw, untextured, arbitrarily-scaled FBX props from the "Mini
/// PSX Western Pack" - Wagon.fbx and DynamiteCrate.fbx (the pack's stand-in for a "TNT box") -
/// for use as range decoration in the gallery. Same two problems CharacterSetup/CharacterScaleFix
/// already solved for the character models, applied here to a simpler single-texture case:
///  1. Neither FBX has a material assigned - bakes one from the matching texture in Textures/.
///  2. Neither is at a scene-appropriate scale - measures its actual mesh bounds and computes a
///     corrective uniform scale to hit a target height (same technique as CharacterScaleFix,
///     which is what "properly portioned to the scene" leans on - both target heights below are
///     a best estimate relative to a person being ~3.33 units tall (CharacterScaleFix.TargetHeight)
///     and have NOT been visually confirmed; adjust here if they look off in Play mode.
/// Also adds a BoxCollider sized to the measured bounds (present on neither FBX by default), so
/// the prop physically blocks movement and gives target medallions something solid to sit against.
/// Safe to re-run.
/// </summary>
public static class RangePropsSetup
{
    private const string PropOutputFolder = "Assets/Prefabs/Props";
    private const string MaterialOutputFolder = "Assets/Materials/Props";
    private const string PackRoot = "Assets/Art/Mini PSX Western Pack";

    [MenuItem("Tools/Shooting Gallery/Setup Range Props (Wagon + Dynamite Crate)")]
    public static void Setup()
    {
        EnsureFolder(PropOutputFolder);
        EnsureFolder(MaterialOutputFolder);

        // Best-guess target heights, unverified visually - see the doc comment above.
        BuildProp("Wagon", $"{PackRoot}/FBX/Wagon.fbx", $"{PackRoot}/Textures/wagon_texture.png", targetHeight: 2.2f);
        BuildProp("DynamiteCrate", $"{PackRoot}/FBX/DynamiteCrate.fbx", $"{PackRoot}/Textures/dynamite_crate_texture.png", targetHeight: 0.9f);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[RangePropsSetup] Wagon and DynamiteCrate prefabs built under " + PropOutputFolder + ".");
    }

    private static void BuildProp(string name, string fbxPath, string texturePath, float targetHeight)
    {
        GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (fbx == null)
        {
            Debug.LogWarning($"[RangePropsSetup] {name}: FBX not found at {fbxPath} - skipped.");
            return;
        }

        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        if (texture == null)
        {
            Debug.LogWarning($"[RangePropsSetup] {name}: texture not found at {texturePath} - skipped.");
            return;
        }

        Material material = CreateOrReplaceMaterial($"{MaterialOutputFolder}/{name}.mat", texture);

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
        instance.transform.localScale = Vector3.one;

        foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            var materials = new Material[renderer.sharedMaterials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = material;
            }
            renderer.sharedMaterials = materials;
        }

        Bounds bounds = default;
        bool hasBounds = false;
        foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
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

        if (!hasBounds)
        {
            Debug.LogWarning($"[RangePropsSetup] {name}: no renderers found in {fbxPath} - skipped.");
            Object.DestroyImmediate(instance);
            return;
        }

        float measuredHeight = bounds.size.y;
        float scale = measuredHeight > 0.001f ? targetHeight / measuredHeight : 1f;
        instance.transform.localScale = Vector3.one * scale;

        // BoxCollider in the root's LOCAL space, sized/centered from the same measured bounds
        // (divided back out of world space by the scale we just applied) - neither FBX ships with
        // any collider at all.
        var collider = instance.AddComponent<BoxCollider>();
        collider.center = (bounds.center - instance.transform.position) / Mathf.Max(scale, 0.0001f);
        collider.size = bounds.size / Mathf.Max(scale, 0.0001f);

        Debug.Log($"[RangePropsSetup] {name}: measured height {measuredHeight:F2} -> scale {scale:F3} (target {targetHeight:F2}).");

        string prefabPath = $"{PropOutputFolder}/{name}.prefab";
        PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
        Object.DestroyImmediate(instance);
    }

    private static Material CreateOrReplaceMaterial(string path, Texture2D texture)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        else
        {
            mat.shader = shader;
        }

        mat.SetTexture("_BaseMap", texture);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
        {
            return;
        }

        string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        string leaf = Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(parent))
        {
            EnsureFolder(parent);
        }

        AssetDatabase.CreateFolder(parent, leaf);
    }
}
