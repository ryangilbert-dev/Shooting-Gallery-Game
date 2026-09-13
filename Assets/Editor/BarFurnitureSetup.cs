using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds usable prefabs out of three raw, untextured, arbitrarily-scaled FBX props from the
/// "Mini PSX Western Pack" - PokerTable.fbx, Chair.fbx, and LogStump.fbx - for use as real bar
/// furniture, replacing the greybox cylinders `ProjectScaffolder.BuildBarInterior` originally
/// used as a stand-in (see NOTES.md). Same two problems `RangePropsSetup`/`CharacterScaleFix`
/// already solved for the range props and character models, applied here to the bar's furniture:
///  1. None of the three FBX files have a material assigned - bakes one from the matching texture
///     in Textures/, same as RangePropsSetup does for Wagon/DynamiteCrate.
///  2. None are at a scene-appropriate scale - measures each one's actual mesh bounds and computes
///     a corrective uniform scale to hit a target height (same technique as CharacterScaleFix and
///     RangePropsSetup). All three target heights below are a best estimate relative to a person
///     being ~3.33 units tall (CharacterScaleFix.TargetHeight) and have NOT been visually
///     confirmed; adjust here if they look off in Play mode.
/// Also adds a BoxCollider sized to the measured bounds (present on none of the three FBX files by
/// default), so each piece of furniture physically blocks movement.
///
/// A separate tool from RangePropsSetup (rather than folding these in there) since that tool's own
/// name/menu item is specifically scoped to the shooting range's Wagon/DynamiteCrate, not the bar -
/// keeping them separate mirrors how the rest of this project splits gallery vs. bar concerns.
/// Safe to re-run.
/// </summary>
public static class BarFurnitureSetup
{
    private const string PropOutputFolder = "Assets/Prefabs/Props";
    private const string MaterialOutputFolder = "Assets/Materials/Props";
    private const string PackRoot = "Assets/Art/Mini PSX Western Pack";

    [MenuItem("Tools/Shooting Gallery/Setup Bar Furniture (Poker Table, Chair, Log Stump)")]
    public static void Setup()
    {
        EnsureFolder(PropOutputFolder);
        EnsureFolder(MaterialOutputFolder);

        // Best-guess target heights, unverified visually - see the doc comment above. Chosen
        // relative to the greybox cylinders they're replacing: BuildBarTable's own table cylinder
        // was 0.9 tall, its stools/BarInterior's counter stools were 1.0 - LogStump is
        // deliberately shorter than a proper stool (a stump makes a lower, more rustic seat).
        BuildProp("PokerTable", $"{PackRoot}/FBX/PokerTable.fbx", $"{PackRoot}/Textures/poker_table_texture.png", targetHeight: 0.9f);
        BuildProp("Chair", $"{PackRoot}/FBX/Chair.fbx", $"{PackRoot}/Textures/chair_texture.png", targetHeight: 1.0f);
        BuildProp("LogStump", $"{PackRoot}/FBX/LogStump.fbx", $"{PackRoot}/Textures/log_stump_texture.png", targetHeight: 0.6f);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[BarFurnitureSetup] PokerTable, Chair, and LogStump prefabs built under " + PropOutputFolder + ".");
    }

    private static void BuildProp(string name, string fbxPath, string texturePath, float targetHeight)
    {
        GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (fbx == null)
        {
            Debug.LogWarning($"[BarFurnitureSetup] {name}: FBX not found at {fbxPath} - skipped.");
            return;
        }

        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        if (texture == null)
        {
            Debug.LogWarning($"[BarFurnitureSetup] {name}: texture not found at {texturePath} - skipped.");
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
            Debug.LogWarning($"[BarFurnitureSetup] {name}: no renderers found in {fbxPath} - skipped.");
            Object.DestroyImmediate(instance);
            return;
        }

        float measuredHeight = bounds.size.y;
        float scale = measuredHeight > 0.001f ? targetHeight / measuredHeight : 1f;
        instance.transform.localScale = Vector3.one * scale;

        // BoxCollider in the root's LOCAL space, sized/centered from the same measured bounds
        // (divided back out of world space by the scale we just applied) - none of the three FBX
        // files ship with any collider at all.
        var collider = instance.AddComponent<BoxCollider>();
        collider.center = (bounds.center - instance.transform.position) / Mathf.Max(scale, 0.0001f);
        collider.size = bounds.size / Mathf.Max(scale, 0.0001f);

        Debug.Log($"[BarFurnitureSetup] {name}: measured height {measuredHeight:F2} -> scale {scale:F3} (target {targetHeight:F2}).");

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
