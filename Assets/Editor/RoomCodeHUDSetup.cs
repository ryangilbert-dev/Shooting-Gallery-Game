using UnityEditor;
using UnityEngine;
using ShootingGallery.UI;

/// <summary>
/// Patches PlayerPrefab with RoomCodeHUD (the host's on-screen Relay room code reminder while
/// still in the bar). No fields to wire - the whole thing is built from code at runtime, same as
/// HitTrackerHUD - so this just makes sure the component exists. Safe to re-run.
/// </summary>
public static class RoomCodeHUDSetup
{
    private const string PlayerPrefabPath = "Assets/Prefabs/Networking/PlayerPrefab.prefab";

    [MenuItem("Tools/Shooting Gallery/Setup Room Code HUD")]
    public static void Setup()
    {
        GameObject instance = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);

        if (instance.GetComponent<RoomCodeHUD>() == null)
        {
            instance.AddComponent<RoomCodeHUD>();
        }

        PrefabUtility.SaveAsPrefabAsset(instance, PlayerPrefabPath);
        PrefabUtility.UnloadPrefabContents(instance);

        AssetDatabase.SaveAssets();
        Debug.Log("[RoomCodeHUDSetup] RoomCodeHUD added to PlayerPrefab.");
    }
}
