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

        // Every character is normalized to roughly the same height (CharacterScaleFix), so a
        // single computed eye height works for all of them: feet sit at CharacterAttachPoint's
        // local Y, eyes sit some fraction of the way up from there. Lowered from 0.92 to 0.85 -
        // playtesting found the camera sitting noticeably above actual eye level, consistent with
        // the original guess not accounting enough for characters wearing hats (measured bounds
        // include the hat, which sits well above actual eye level, inflating TargetHeight for
        // those characters specifically). Still a single fraction shared by every character
        // rather than a per-character measurement, so it won't be exactly right for all 8 - nudge
        // further if it's still off, or still too high/low for a specific character.
        if (cameraTransform != null)
        {
            Transform attachPoint = instance.transform.Find("CharacterAttachPoint");
            float feetY = attachPoint != null ? attachPoint.localPosition.y : -1f;
            float eyeY = feetY + CharacterScaleFix.TargetHeight * 0.85f;

            Vector3 pos = cameraTransform.localPosition;
            pos.y = eyeY;
            cameraTransform.localPosition = pos;
        }

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
