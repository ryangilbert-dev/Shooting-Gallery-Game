using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using ShootingGallery.Gameplay;
using ShootingGallery.UI;

/// <summary>
/// One-shot patch tool for the duel exchange (lives + headshot-only hitbox + practice dummy):
/// - PlayerPrefab: moves its body collider onto the built-in "Ignore Raycast" layer (so a shot
///   passes through the body and can only ever land on the HeadHitbox - see HeadHitbox.cs) and
///   adds PlayerCombatant + LivesHUD.
/// - Builds DummyEnemyPrefab: a stationary, random-cowboy target dummy with the same headshot
///   hitbox, spawned by MatchManager into an empty lane for solo testing.
/// - Wires DummyEnemyPrefab into the MatchManager instance already sitting in Arena.unity.
/// Safe to re-run, same as every other Tools > Shooting Gallery patch tool. Run this from the
/// open Editor (not headless/batch) - see NOTES.md's "corrupted cross-reference" gotcha.
/// </summary>
public static class DuelSetup
{
    private const string PlayerPrefabPath = "Assets/Prefabs/Networking/PlayerPrefab.prefab";
    private const string DummyEnemyPrefabPath = "Assets/Prefabs/Networking/DummyEnemyPrefab.prefab";
    private const string CharacterPrefabsFolder = "Assets/Prefabs/Characters";
    private const string ArenaScenePath = "Assets/Scenes/Arena.unity";

    // Unity's built-in "Ignore Raycast" layer - excluded from Physics.DefaultRaycastLayers, which
    // is what every Physics.Raycast call in this project uses (explicitly, as of PlayerWeapon's
    // headshot changes). Putting a combatant's body collider here is what makes shots pass
    // straight through it and only ever land on the (Default-layer) HeadHitbox.
    private const int IgnoreRaycastLayer = 2;

    [MenuItem("Tools/Shooting Gallery/Setup Duel Combat (Lives + Dummy Enemy)")]
    public static void Setup()
    {
        PatchPlayerPrefab();
        GameObject dummyPrefab = BuildDummyEnemyPrefab();
        WireArenaMatchManager(dummyPrefab);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[DuelSetup] PlayerPrefab patched (headshot hitbox layer, PlayerCombatant, LivesHUD); " +
                  "DummyEnemyPrefab built; MatchManager wired with it.");
    }

    private static void PatchPlayerPrefab()
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath) == null)
        {
            Debug.LogError("[DuelSetup] PlayerPrefab not found - run the M1 scaffold first.");
            return;
        }

        GameObject instance = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);

        // The CharacterController (added by PlayerMovementSetup) lives on this same root object -
        // moving the whole root to Ignore Raycast is what keeps a shot from ever hitting the body.
        instance.layer = IgnoreRaycastLayer;

        if (instance.GetComponent<PlayerCombatant>() == null)
        {
            instance.AddComponent<PlayerCombatant>();
        }

        if (instance.GetComponent<LivesHUD>() == null)
        {
            instance.AddComponent<LivesHUD>();
        }

        PrefabUtility.SaveAsPrefabAsset(instance, PlayerPrefabPath);
        PrefabUtility.UnloadPrefabContents(instance);
    }

    private static GameObject BuildDummyEnemyPrefab()
    {
        EnsureFolder("Assets/Prefabs");
        EnsureFolder("Assets/Prefabs/Networking");

        var characterPrefabs = new List<GameObject>();
        if (AssetDatabase.IsValidFolder(CharacterPrefabsFolder))
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { CharacterPrefabsFolder }))
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (prefab != null)
                {
                    characterPrefabs.Add(prefab);
                }
            }
        }

        if (characterPrefabs.Count == 0)
        {
            Debug.LogWarning("[DuelSetup] No character prefabs found under " + CharacterPrefabsFolder +
                              " - run 'Setup Random Character Visuals' first. DummyEnemyPrefab will have no visual.");
        }

        GameObject root = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        root.name = "DummyEnemyPrefab";
        root.layer = IgnoreRaycastLayer;
        root.GetComponent<Renderer>().enabled = false; // Real body comes from the character visual below.

        var networkObject = root.AddComponent<NetworkObject>();

        GameObject attachGO = new GameObject("CharacterAttachPoint");
        attachGO.transform.SetParent(root.transform, false);
        attachGO.transform.localPosition = new Vector3(0f, -1f, 0f);

        var controller = root.AddComponent<DummyEnemyController>();
        var so = new SerializedObject(controller);
        so.FindProperty("characterAttachPoint").objectReferenceValue = attachGO.transform;

        SerializedProperty arrayProp = so.FindProperty("characterVisualPrefabs");
        arrayProp.arraySize = characterPrefabs.Count;
        for (int i = 0; i < characterPrefabs.Count; i++)
        {
            arrayProp.GetArrayElementAtIndex(i).objectReferenceValue = characterPrefabs[i];
        }

        so.ApplyModifiedPropertiesWithoutUndo();

        GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, DummyEnemyPrefabPath);
        Object.DestroyImmediate(root);

        // NetworkObject.GlobalObjectIdHash matters for scene-placed NetworkObjects (see
        // ProjectScaffolder.AssignUniqueGlobalObjectIdHash) - this one is a prefab asset spawned
        // at runtime via Instantiate+Spawn, identified instead by DefaultNetworkPrefabs.asset
        // (Netcode's project-wide auto-list of every prefab with a NetworkObject), which picks it
        // up automatically now that it's a saved asset - no extra registration step needed.
        _ = networkObject;

        return savedPrefab;
    }

    private static void WireArenaMatchManager(GameObject dummyPrefab)
    {
        if (dummyPrefab == null || !AssetDatabase.LoadAssetAtPath<SceneAsset>(ArenaScenePath))
        {
            return;
        }

        Scene openedScene = EditorSceneManager.OpenScene(ArenaScenePath, OpenSceneMode.Single);
        var matchManager = Object.FindFirstObjectByType<MatchManager>();
        if (matchManager == null)
        {
            Debug.LogError("[DuelSetup] No MatchManager found in Arena.unity - run the M1 scaffold first.");
            return;
        }

        var so = new SerializedObject(matchManager);
        so.FindProperty("dummyEnemyPrefab").objectReferenceValue = dummyPrefab;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(openedScene);
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
