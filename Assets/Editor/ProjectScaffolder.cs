using System.IO;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using ShootingGallery.Gameplay;
using ShootingGallery.Networking;
using ShootingGallery.UI;

/// <summary>
/// One-shot scaffolding tool that builds the M1 scenes/prefabs (Bootstrap, MainMenu, Arena,
/// PlayerPrefab) from the approved architecture plan. Run via the menu item below, or headless
/// via `-executeMethod ProjectScaffolder.ScaffoldM1`. Safe to re-run: it overwrites its own
/// generated assets each time rather than duplicating them.
/// </summary>
public static class ProjectScaffolder
{
    private const string ScenesFolder = "Assets/Scenes";
    private const string PrefabsFolder = "Assets/Prefabs/Networking";

    private const string BootstrapScenePath = ScenesFolder + "/Bootstrap.unity";
    private const string MainMenuScenePath = ScenesFolder + "/MainMenu.unity";
    private const string ArenaScenePath = ScenesFolder + "/Arena.unity";
    private const string PlayerPrefabPath = PrefabsFolder + "/PlayerPrefab.prefab";

    [MenuItem("Tools/Shooting Gallery/Scaffold M1 (Connection Plumbing)")]
    public static void ScaffoldM1()
    {
        EnsureFolder(PrefabsFolder);

        GameObject playerPrefab = BuildPlayerPrefab();

        BuildBootstrapScene(playerPrefab);
        BuildMainMenuScene();
        BuildArenaScene();

        RegisterBuildScenes();
        RemoveSampleScene();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[ProjectScaffolder] M1 scaffold complete: Bootstrap, MainMenu, and Arena scenes " +
                   "created and registered in Build Settings; PlayerPrefab created at " + PlayerPrefabPath);
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

    private static GameObject BuildPlayerPrefab()
    {
        GameObject root = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        root.name = "PlayerPrefab";

        var networkObject = root.AddComponent<NetworkObject>();
        var networkTransform = root.AddComponent<NetworkTransform>();

        GameObject cameraGO = new GameObject("PlayerCamera");
        cameraGO.transform.SetParent(root.transform, false);
        cameraGO.transform.localPosition = new Vector3(0f, 0.6f, 0.15f);
        Camera cam = cameraGO.AddComponent<Camera>();
        AudioListener listener = cameraGO.AddComponent<AudioListener>();
        cam.enabled = false;
        listener.enabled = false;

        var controller = root.AddComponent<PlayerController>();
        var so = new SerializedObject(controller);
        so.FindProperty("playerCamera").objectReferenceValue = cam;
        so.FindProperty("audioListener").objectReferenceValue = listener;
        so.FindProperty("placeholderBodyRenderer").objectReferenceValue = root.GetComponent<Renderer>();
        so.ApplyModifiedPropertiesWithoutUndo();

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
        Object.DestroyImmediate(root);
        return prefab;
    }

    private static void BuildBootstrapScene(GameObject playerPrefab)
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        GameObject nmGO = new GameObject("NetworkManager");
        var transport = nmGO.AddComponent<UnityTransport>();
        var networkManager = nmGO.AddComponent<NetworkManager>();
        networkManager.NetworkConfig.NetworkTransport = transport;
        networkManager.NetworkConfig.PlayerPrefab = playerPrefab;
        networkManager.NetworkConfig.EnableSceneManagement = true;

        GameObject connGO = new GameObject("ConnectionManager");
        connGO.AddComponent<ConnectionManager>();

        GameObject loaderGO = new GameObject("BootstrapLoader");
        loaderGO.AddComponent<BootstrapLoader>();

        EditorSceneManager.SaveScene(scene, BootstrapScenePath);
    }

    private static void BuildMainMenuScene()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        GameObject esGO = new GameObject("EventSystem");
        esGO.AddComponent<EventSystem>();
        esGO.AddComponent<InputSystemUIInputModule>();

        GameObject canvasGO = new GameObject("Canvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasGO.AddComponent<CanvasScaler>();
        canvasGO.AddComponent<GraphicRaycaster>();

        Text status = CreateText(canvasGO.transform, "StatusText", "Enter host IP and Join, or click Host.",
            new Vector2(0.5f, 0.75f), new Vector2(600, 60));

        InputField addressField = CreateInputField(canvasGO.transform, "JoinAddressField", "127.0.0.1",
            new Vector2(0.5f, 0.55f));

        Button hostButton = CreateButton(canvasGO.transform, "HostButton", "Host", new Vector2(0.35f, 0.4f));
        Button joinButton = CreateButton(canvasGO.transform, "JoinButton", "Join", new Vector2(0.65f, 0.4f));

        GameObject menuGO = new GameObject("MainMenuUI");
        var menuUI = menuGO.AddComponent<MainMenuUI>();
        var so = new SerializedObject(menuUI);
        so.FindProperty("joinAddressField").objectReferenceValue = addressField;
        so.FindProperty("hostButton").objectReferenceValue = hostButton;
        so.FindProperty("joinButton").objectReferenceValue = joinButton;
        so.FindProperty("statusText").objectReferenceValue = status;
        so.FindProperty("arenaSceneName").stringValue = "Arena";
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(scene, MainMenuScenePath);
    }

    private static Text CreateText(Transform parent, string name, string content, Vector2 anchor, Vector2 size)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = anchor;
        rect.sizeDelta = size;
        var text = go.AddComponent<Text>();
        text.text = content;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        return text;
    }

    private static InputField CreateInputField(Transform parent, string name, string placeholder, Vector2 anchor)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = anchor;
        rect.sizeDelta = new Vector2(300, 40);
        go.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);
        var field = go.AddComponent<InputField>();

        GameObject textGO = new GameObject("Text", typeof(RectTransform));
        textGO.transform.SetParent(go.transform, false);
        var textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(8, 4);
        textRect.offsetMax = new Vector2(-8, -4);
        var text = textGO.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.color = Color.white;
        text.supportRichText = false;
        field.textComponent = text;
        field.text = placeholder;

        return field;
    }

    private static Button CreateButton(Transform parent, string name, string label, Vector2 anchor)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = anchor;
        rect.sizeDelta = new Vector2(160, 50);
        go.AddComponent<Image>().color = new Color(0.2f, 0.2f, 0.2f, 0.9f);
        var button = go.AddComponent<Button>();

        GameObject textGO = new GameObject("Text", typeof(RectTransform));
        textGO.transform.SetParent(go.transform, false);
        var textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        var text = textGO.AddComponent<Text>();
        text.text = label;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;

        return button;
    }

    private static void BuildArenaScene()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(4f, 1f, 2f);

        GameObject lightGO = new GameObject("Directional Light");
        var light = lightGO.AddComponent<Light>();
        light.type = LightType.Directional;
        lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        Transform spawnA = BuildLane("GalleryLaneA", new Vector3(-10f, 0f, 0f), Vector3.right);
        Transform spawnB = BuildLane("GalleryLaneB", new Vector3(10f, 0f, 0f), Vector3.left);

        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "DividerWall";
        wall.transform.position = new Vector3(0f, 1.5f, 0f);
        wall.transform.localScale = new Vector3(0.5f, 3f, 12f);

        GameObject matchManagerGO = new GameObject("MatchManager");
        var networkObject = matchManagerGO.AddComponent<NetworkObject>();
        var matchManager = matchManagerGO.AddComponent<MatchManager>();
        var so = new SerializedObject(matchManager);
        so.FindProperty("playerASpawnPoint").objectReferenceValue = spawnA;
        so.FindProperty("playerBSpawnPoint").objectReferenceValue = spawnB;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(scene, ArenaScenePath);
    }

    private static Transform BuildLane(string name, Vector3 origin, Vector3 facing)
    {
        GameObject lane = new GameObject(name);
        lane.transform.position = origin;

        GameObject spawn = new GameObject("PlayerSpawnPoint");
        spawn.transform.SetParent(lane.transform, false);
        spawn.transform.localPosition = new Vector3(0f, 1f, -5f);
        spawn.transform.rotation = Quaternion.LookRotation(facing, Vector3.up);

        return spawn.transform;
    }

    private static void RegisterBuildScenes()
    {
        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(BootstrapScenePath, true),
            new EditorBuildSettingsScene(MainMenuScenePath, true),
            new EditorBuildSettingsScene(ArenaScenePath, true),
        };
    }

    private static void RemoveSampleScene()
    {
        const string sampleScenePath = ScenesFolder + "/SampleScene.unity";
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(sampleScenePath) != null)
        {
            AssetDatabase.DeleteAsset(sampleScenePath);
        }
    }
}
