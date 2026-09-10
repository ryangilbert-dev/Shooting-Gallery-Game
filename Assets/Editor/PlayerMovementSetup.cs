using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;
using ShootingGallery.Gameplay;

/// <summary>
/// Patches PlayerPrefab with a CharacterController + PlayerMovement and switches its
/// NetworkTransform to Owner authority (so each client drives their own movement locally instead
/// of round-tripping through the server). Safe to re-run.
/// </summary>
public static class PlayerMovementSetup
{
    private const string PlayerPrefabPath = "Assets/Prefabs/Networking/PlayerPrefab.prefab";

    [MenuItem("Tools/Shooting Gallery/Setup Player Movement")]
    public static void Setup()
    {
        GameObject instance = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);

        var controller = instance.GetComponent<CharacterController>();
        if (controller == null)
        {
            controller = instance.AddComponent<CharacterController>();
        }
        controller.center = Vector3.zero;
        controller.height = 2f;
        controller.radius = 0.5f;

        var movement = instance.GetComponent<PlayerMovement>();
        if (movement == null)
        {
            movement = instance.AddComponent<PlayerMovement>();
        }

        Transform cameraTransform = instance.transform.Find("PlayerCamera");
        var so = new SerializedObject(movement);
        so.FindProperty("cameraPivot").objectReferenceValue = cameraTransform;
        so.ApplyModifiedPropertiesWithoutUndo();

        var networkTransform = instance.GetComponent<NetworkTransform>();
        if (networkTransform != null)
        {
            networkTransform.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
        }

        PrefabUtility.SaveAsPrefabAsset(instance, PlayerPrefabPath);
        PrefabUtility.UnloadPrefabContents(instance);

        AssetDatabase.SaveAssets();
        Debug.Log("[PlayerMovementSetup] CharacterController + PlayerMovement added; NetworkTransform set to Owner authority.");
    }
}
