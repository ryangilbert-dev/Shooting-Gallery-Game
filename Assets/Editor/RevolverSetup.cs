using System.IO;
using UnityEditor;
using UnityEngine;
using ShootingGallery.Gameplay;

/// <summary>
/// Builds a properly-textured, correctly-scaled Revolver prefab from the PSX Revolver pack and
/// wires it into PlayerPrefab's PlayerWeapon component. Safe to re-run.
/// </summary>
public static class RevolverSetup
{
    private const string FbxPath = "Assets/Art/PSX Revolver/PSX Revolver/Revolver.fbx";
    private const string TexturePath = "Assets/Art/PSX Revolver/PSX Revolver/RevolverNaturalColors.png";
    private const string MaterialPath = "Assets/Materials/Revolver.mat";
    private const string PrefabFolder = "Assets/Prefabs/Weapons";
    private const string PrefabPath = PrefabFolder + "/Revolver.prefab";
    private const string PlayerPrefabPath = "Assets/Prefabs/Networking/PlayerPrefab.prefab";

    // A single-action revolver like a Colt Peacemaker is roughly this long, barrel to grip.
    private const float TargetLength = 0.3f;

    [MenuItem("Tools/Shooting Gallery/Setup Revolver")]
    public static void Setup()
    {
        GameObject revolverPrefab = BuildRevolverPrefab();
        WirePlayerPrefab(revolverPrefab);
        AssetDatabase.SaveAssets();
        Debug.Log("[RevolverSetup] Revolver prefab built and wired into PlayerPrefab's PlayerWeapon.");
    }

    private static GameObject BuildRevolverPrefab()
    {
        EnsureFolder(PrefabFolder);

        GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        if (fbx == null)
        {
            Debug.LogError("[RevolverSetup] Revolver FBX not found at " + FbxPath);
            return null;
        }

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);

        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
        if (texture != null)
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            mat.SetTexture("_BaseMap", texture);
            EditorUtility.SetDirty(mat);

            foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int i = 0; i < mats.Length; i++)
                {
                    mats[i] = mat;
                }
                r.sharedMaterials = mats;
            }
        }

        // Measure and correct scale rather than guessing - the character models taught us that
        // lesson (they came in at 17-22 units tall before CharacterScaleFix).
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

        if (hasBounds)
        {
            float largestDimension = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            float scale = largestDimension > 0.001f ? TargetLength / largestDimension : 1f;
            instance.transform.localScale = Vector3.one * scale;
            Debug.Log($"[RevolverSetup] Measured largest dimension {largestDimension:F2} -> scale {scale:F4} (target {TargetLength:F2})");
        }

        // It's a hand-attached cosmetic prop, not something that should physically collide.
        foreach (Collider c in instance.GetComponentsInChildren<Collider>(true))
        {
            Object.DestroyImmediate(c);
        }

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
        Object.DestroyImmediate(instance);
        return prefab;
    }

    private static void WirePlayerPrefab(GameObject revolverPrefab)
    {
        if (revolverPrefab == null)
        {
            return;
        }

        GameObject instance = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);

        var weapon = instance.GetComponent<PlayerWeapon>();
        if (weapon == null)
        {
            weapon = instance.AddComponent<PlayerWeapon>();
        }

        Transform cameraTransform = instance.transform.Find("PlayerCamera");
        Transform viewmodelAnchor = cameraTransform != null ? cameraTransform.Find("ViewmodelAnchor") : null;
        if (cameraTransform != null && viewmodelAnchor == null)
        {
            var anchorGO = new GameObject("ViewmodelAnchor");
            anchorGO.transform.SetParent(cameraTransform, false);
            viewmodelAnchor = anchorGO.transform;
        }

        if (viewmodelAnchor != null)
        {
            // 0.4 was only ~0.1 units past the camera's 0.3-unit near clip plane - part of the
            // ~0.3-unit-long revolver mesh was falling closer than that and getting clipped away
            // entirely, which looked like disconnected/scrambled pieces (confirmed via
            // screenshot). Pushed out to comfortably clear it regardless of where the model's own
            // pivot sits within its bounds. Always reapplied (not just on first creation) so
            // re-running this tool actually fixes an existing, already-wrong anchor.
            viewmodelAnchor.localPosition = new Vector3(0.2f, -0.2f, 0.6f);
        }

        var so = new SerializedObject(weapon);
        so.FindProperty("revolverPrefab").objectReferenceValue = revolverPrefab;
        so.FindProperty("playerCamera").objectReferenceValue = cameraTransform != null ? cameraTransform.GetComponent<Camera>() : null;
        so.FindProperty("viewmodelAnchor").objectReferenceValue = viewmodelAnchor;

        // The revolver mesh's own baked orientation needed this correction (found by rendering
        // rotation candidates to PNG and comparing directly - see RevolverPreviewCapture): base
        // profile fix, then a further 90-degree roll (composed via quaternion multiplication, not
        // simple Euler addition) for the natural "holding it" pose - grip low, barrel up and away.
        // Setting a C# field default doesn't retroactively update it once already serialized on
        // this existing component, so write it explicitly here too.
        var orientationCorrection = new Vector3(0f, 90f, 270f);
        so.FindProperty("revolverLocalEulerOffset").vector3Value = orientationCorrection;
        so.FindProperty("viewmodelLocalEulerOffset").vector3Value = orientationCorrection;
        so.ApplyModifiedPropertiesWithoutUndo();

        PrefabUtility.SaveAsPrefabAsset(instance, PlayerPrefabPath);
        PrefabUtility.UnloadPrefabContents(instance);
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
