using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ShootingGallery.Gameplay;

/// <summary>
/// Builds one visual-only prefab per playable character from the PSX-Western Characters Pack
/// (baking the right texture(s) onto each model - single-texture characters get one material
/// applied everywhere, multi-part characters like BountyHunter/EliteCowboy2 get their Head/Body/
/// Hat/Boots/Arm(s) textures matched onto the matching renderer by name), then wires the result
/// into PlayerPrefab so MatchManager/PlayerController can pick one at random per connecting
/// player. Safe to re-run: prefabs and materials are overwritten in place, not duplicated.
/// </summary>
public static class CharacterSetup
{
    private const string CharactersRoot = "Assets/Art/PSX-Western Characters Pack/Characters";
    private const string PrefabOutputFolder = "Assets/Prefabs/Characters";
    private const string MaterialOutputFolder = "Assets/Materials/Characters";
    private const string PlayerPrefabPath = "Assets/Prefabs/Networking/PlayerPrefab.prefab";

    private struct CharacterDef
    {
        public string Name;
        public string FbxPath;
        public string TextureFolder;
    }

    private static readonly CharacterDef[] Characters =
    {
        Def("Cowboy1"), Def("Cowboy2"), Def("Cowboy3"), Def("Cowboy4"),
        Def("EliteCowboy1"), Def("EliteCowboy2"), Def("BountyHunter"), Def("Woman"),
    };

    private static CharacterDef Def(string name) => new CharacterDef
    {
        Name = name,
        FbxPath = $"{CharactersRoot}/{name}/{name}.fbx",
        TextureFolder = $"{CharactersRoot}/{name}",
    };

    [MenuItem("Tools/Shooting Gallery/Setup Random Character Visuals")]
    public static void SetupCharacters()
    {
        EnsureFolder(PrefabOutputFolder);
        EnsureFolder(MaterialOutputFolder);

        var builtPrefabs = new List<GameObject>();
        foreach (CharacterDef def in Characters)
        {
            GameObject prefab = BuildCharacterPrefab(def);
            if (prefab != null)
            {
                builtPrefabs.Add(prefab);
            }
        }

        WirePlayerPrefab(builtPrefabs);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[CharacterSetup] Built {builtPrefabs.Count}/{Characters.Length} character visual " +
                  "prefabs and wired them into PlayerPrefab.");
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

    private static GameObject BuildCharacterPrefab(CharacterDef def)
    {
        GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(def.FbxPath);
        if (fbx == null)
        {
            Debug.LogWarning($"[CharacterSetup] FBX not found for {def.Name}: {def.FbxPath}");
            return null;
        }

        var textures = new Dictionary<string, Texture2D>();
        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { def.TextureFolder }))
        {
            string texPath = AssetDatabase.GUIDToAssetPath(guid);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex != null)
            {
                textures[Path.GetFileNameWithoutExtension(texPath).ToLowerInvariant()] = tex;
            }
        }

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
        var materialCache = new Dictionary<Texture2D, Material>();

        foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            Material[] current = renderer.sharedMaterials;
            var updated = new Material[current.Length];
            bool changedAny = false;

            for (int i = 0; i < current.Length; i++)
            {
                // The FBX's own imported material for each slot is already named after the body
                // part it belongs to (e.g. "Head", "Boots") even for generically-named meshes
                // ("Cube_002") - match on that rather than the renderer's GameObject name.
                Texture2D chosenTexture = ChooseTextureForSlot(current[i], textures);
                if (chosenTexture == null)
                {
                    updated[i] = current[i];
                    continue;
                }

                if (!materialCache.TryGetValue(chosenTexture, out Material mat))
                {
                    mat = CreateOrReplaceMaterial($"{MaterialOutputFolder}/{def.Name}_{chosenTexture.name}.mat", chosenTexture);
                    materialCache[chosenTexture] = mat;
                }

                updated[i] = mat;
                changedAny = true;
            }

            if (changedAny)
            {
                renderer.sharedMaterials = updated;
            }
        }

        string prefabPath = $"{PrefabOutputFolder}/{def.Name}.prefab";
        GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
        Object.DestroyImmediate(instance);
        return savedPrefab;
    }

    private static Texture2D ChooseTextureForSlot(Material currentSlotMaterial, Dictionary<string, Texture2D> textures)
    {
        if (textures.Count == 0)
        {
            return null;
        }

        if (textures.Count == 1)
        {
            foreach (var kv in textures)
            {
                return kv.Value;
            }
        }

        if (currentSlotMaterial == null)
        {
            return null;
        }

        string matName = currentSlotMaterial.name.ToLowerInvariant();
        foreach (var kv in textures)
        {
            if (matName == kv.Key || matName.Contains(kv.Key) || kv.Key.Contains(matName))
            {
                return kv.Value;
            }
        }

        return null;
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

    private static void WirePlayerPrefab(List<GameObject> characterPrefabs)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath) == null)
        {
            Debug.LogError("[CharacterSetup] PlayerPrefab not found - run the M1 scaffold first.");
            return;
        }

        GameObject instance = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);

        Transform attachPoint = instance.transform.Find("CharacterAttachPoint");
        if (attachPoint == null)
        {
            GameObject attachGO = new GameObject("CharacterAttachPoint");
            attachGO.transform.SetParent(instance.transform, false);
            attachGO.transform.localPosition = new Vector3(0f, -1f, 0f);
            attachPoint = attachGO.transform;
        }

        var controller = instance.GetComponent<PlayerController>();
        var so = new SerializedObject(controller);
        so.FindProperty("characterAttachPoint").objectReferenceValue = attachPoint;

        SerializedProperty arrayProp = so.FindProperty("characterVisualPrefabs");
        arrayProp.arraySize = characterPrefabs.Count;
        for (int i = 0; i < characterPrefabs.Count; i++)
        {
            arrayProp.GetArrayElementAtIndex(i).objectReferenceValue = characterPrefabs[i];
        }

        Renderer capsuleRenderer = instance.GetComponent<Renderer>();
        if (capsuleRenderer != null)
        {
            so.FindProperty("placeholderBodyRenderer").objectReferenceValue = capsuleRenderer;
        }

        so.ApplyModifiedPropertiesWithoutUndo();

        if (capsuleRenderer != null)
        {
            capsuleRenderer.enabled = false;
        }

        PrefabUtility.SaveAsPrefabAsset(instance, PlayerPrefabPath);
        PrefabUtility.UnloadPrefabContents(instance);
    }
}
