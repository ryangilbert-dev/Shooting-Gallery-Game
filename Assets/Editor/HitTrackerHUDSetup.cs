using UnityEditor;
using UnityEngine;
using ShootingGallery.UI;

/// <summary>
/// Patches PlayerPrefab with HitTrackerHUD (the hit-count fill bar). No fields to wire - the
/// whole bar is built from code at runtime - so this just makes sure the component exists.
/// Safe to re-run.
/// </summary>
public static class HitTrackerHUDSetup
{
    private const string PlayerPrefabPath = "Assets/Prefabs/Networking/PlayerPrefab.prefab";

    [MenuItem("Tools/Shooting Gallery/Setup Hit Tracker HUD")]
    public static void Setup()
    {
        GameObject instance = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);

        if (instance.GetComponent<HitTrackerHUD>() == null)
        {
            instance.AddComponent<HitTrackerHUD>();
        }

        PrefabUtility.SaveAsPrefabAsset(instance, PlayerPrefabPath);
        PrefabUtility.UnloadPrefabContents(instance);

        AssetDatabase.SaveAssets();
        Debug.Log("[HitTrackerHUDSetup] HitTrackerHUD added to PlayerPrefab.");
    }
}
