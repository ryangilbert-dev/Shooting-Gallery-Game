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
    private const string RoomMaterialsFolder = "Assets/Materials/Rooms";
    private const string PropPrefabFolder = "Assets/Prefabs/Props";

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

    [MenuItem("Tools/Shooting Gallery/Rebuild Main Menu Scene Only")]
    public static void RebuildMainMenuOnly()
    {
        BuildMainMenuScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[ProjectScaffolder] Main menu scene rebuilt (Bootstrap/Arena/PlayerPrefab untouched).");
    }

    private static void BuildMainMenuScene()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Neither this scene nor Bootstrap has a camera otherwise - the only one in the whole
        // game lives on PlayerPrefab, and that's disabled until a player actually spawns. Without
        // this, pressing Play just shows a "no cameras rendering" warning over blank menu UI.
        GameObject menuCameraGO = new GameObject("MenuCamera");
        Camera menuCamera = menuCameraGO.AddComponent<Camera>();
        menuCamera.clearFlags = CameraClearFlags.SolidColor;
        menuCamera.backgroundColor = new Color(0.15f, 0.15f, 0.17f);
        menuCameraGO.AddComponent<AudioListener>();

        GameObject esGO = new GameObject("EventSystem");
        esGO.AddComponent<EventSystem>();
        esGO.AddComponent<InputSystemUIInputModule>();

        GameObject canvasGO = new GameObject("Canvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Default CanvasScaler mode (Constant Pixel Size) renders every UI element at a literal
        // pixel size regardless of actual screen resolution - looks fine in the Editor's small,
        // often-shrunk Game view panel, but comically tiny at a real build's full native
        // resolution. Scale With Screen Size instead, relative to a 1920x1080 reference so
        // existing pixel-based layout numbers throughout this file stay meaningful.
        var canvasScaler = canvasGO.AddComponent<CanvasScaler>();
        canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasScaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasScaler.matchWidthOrHeight = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();

        Text status = CreateText(canvasGO.transform, "StatusText", "Click Host to start a room, or enter a room code and click Join.",
            new Vector2(0.5f, 0.8f), new Vector2(700, 60));

        // Shown once hosting actually succeeds (see MainMenuUI.SetRoomCode) - separate from
        // statusText since that gets overwritten with transient connection messages, but the
        // room code needs to stay visible/readable the whole time you're waiting for a friend.
        Text roomCode = CreateText(canvasGO.transform, "RoomCodeText", "",
            new Vector2(0.5f, 0.68f), new Vector2(600, 50));
        roomCode.fontSize = 28;
        roomCode.fontStyle = FontStyle.Bold;

        InputField codeField = CreateInputField(canvasGO.transform, "JoinCodeField", "",
            new Vector2(0.5f, 0.55f));

        Button hostButton = CreateButton(canvasGO.transform, "HostButton", "Host", new Vector2(0.35f, 0.4f));
        Button joinButton = CreateButton(canvasGO.transform, "JoinButton", "Join", new Vector2(0.65f, 0.4f));

        GameObject menuGO = new GameObject("MainMenuUI");
        var menuUI = menuGO.AddComponent<MainMenuUI>();
        var so = new SerializedObject(menuUI);
        so.FindProperty("joinCodeField").objectReferenceValue = codeField;
        so.FindProperty("hostButton").objectReferenceValue = hostButton;
        so.FindProperty("joinButton").objectReferenceValue = joinButton;
        so.FindProperty("statusText").objectReferenceValue = status;
        so.FindProperty("roomCodeDisplayText").objectReferenceValue = roomCode;
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

    [MenuItem("Tools/Shooting Gallery/Rebuild Arena Scene Only")]
    public static void RebuildArenaOnly()
    {
        BuildArenaScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[ProjectScaffolder] Arena scene rebuilt (Bootstrap/MainMenu/PlayerPrefab untouched).");
    }

    /// <summary>
    /// Two rooms: the Bar (where players spawn on connect, free to walk around) sits east of the
    /// Gallery (the shooting lanes + divider wall), joined by a single doorway. Crossing that
    /// doorway (GalleryEntryTrigger) is what actually starts the match - see
    /// MatchManager.NotifyPlayerEnteredGallery.
    /// </summary>
    private static void BuildArenaScene()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        GameObject lightGO = new GameObject("Directional Light");
        var light = lightGO.AddComponent<Light>();
        light.type = LightType.Directional;
        lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        const float roomHeight = 5f;
        const float wallThickness = 0.3f;
        const float doorWidth = 5f;
        // Characters stand ~3.33 units tall (CharacterScaleFix.TargetHeight); the old 3-unit
        // doorway had their heads/hats visibly clipping through the lintel walking through it.
        const float doorHeight = 4.2f;

        // Gallery rooms 30% larger floor-wise, in both directions equally so proportions stay
        // correct rather than stretching one axis - applied to the room footprint and everything
        // laid out within it (lane origins, the divider wall's length, wall-mounted target
        // spread, foreground prop positions). Deliberately NOT applied to roomHeight (kept equal
        // to the bar room's for a seamless shared doorway - "larger gallery" reads as more floor
        // space, not a taller ceiling), nor to door/character/target sizes, which are calibrated
        // to the character rig and bullet size respectively, not to room scale.
        const float gallerySizeScale = 1.3f;
        const float galleryHalfWidth = 30f * gallerySizeScale / 2f;
        const float galleryHalfDepth = 18f * gallerySizeScale / 2f;

        EnsureFolder(RoomMaterialsFolder);
        Material galleryFloorMat = CreateColorMaterial(RoomMaterialsFolder + "/GalleryFloor.mat", new Color(0.33f, 0.35f, 0.38f));
        Material galleryWallMat = CreateColorMaterial(RoomMaterialsFolder + "/GalleryWall.mat", new Color(0.55f, 0.57f, 0.62f));
        Material barFloorMat = CreateColorMaterial(RoomMaterialsFolder + "/BarFloor.mat", new Color(0.42f, 0.28f, 0.16f));
        Material barWallMat = CreateColorMaterial(RoomMaterialsFolder + "/BarWall.mat", new Color(0.62f, 0.48f, 0.32f));
        Material ceilingMat = CreateColorMaterial(RoomMaterialsFolder + "/Ceiling.mat", new Color(0.15f, 0.15f, 0.17f));
        Material dividerWallMat = CreateColorMaterial(RoomMaterialsFolder + "/DividerWall.mat", new Color(0.35f, 0.62f, 0.7f));
        Material counterMat = CreateColorMaterial(RoomMaterialsFolder + "/ShootingCounter.mat", new Color(0.4f, 0.26f, 0.14f));

        // --- Gallery room: two lanes either side of a divider wall, door on its east side ---
        // Cool grey-blue palette to read as distinct from the bar's warm wood tones - helps
        // orient which room you're in at a glance, per playtest feedback that flat white/grey
        // everywhere was disorienting.
        GameObject galleryRoom = new GameObject("GalleryRoom");
        BuildRoomShell(galleryRoom.transform, Vector3.zero, new Vector3(galleryHalfWidth * 2f, roomHeight, galleryHalfDepth * 2f), wallThickness,
            doorOnEast: true, doorOnWest: false, doorCenterZ: 0f, doorWidth: doorWidth, doorHeight: doorHeight,
            floorMat: galleryFloorMat, wallMat: galleryWallMat, ceilingMat: ceilingMat);

        Transform spawnA = BuildLane("GalleryLaneA", galleryRoom.transform, new Vector3(-10f * gallerySizeScale, 0f, 0f), Vector3.right);
        Transform spawnB = BuildLane("GalleryLaneB", galleryRoom.transform, new Vector3(10f * gallerySizeScale, 0f, 0f), Vector3.left);

        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "DividerWall";
        wall.transform.SetParent(galleryRoom.transform, false);
        wall.transform.position = new Vector3(0f, 2.25f, 0f);
        wall.transform.localScale = new Vector3(0.5f, 4.5f, 16f * gallerySizeScale);
        wall.GetComponent<Renderer>().sharedMaterial = dividerWallMat;

        // Networked so both clients see the same drop/raise animation at the same time - see
        // WallController. Its collider (added automatically by CreatePrimitive) moves down with
        // it, so a dropped wall stops blocking both sightlines and shots without any extra work.
        AssignUniqueGlobalObjectIdHash(wall.AddComponent<NetworkObject>());
        var wallController = wall.AddComponent<WallController>();

        // Six small medallion targets, three per lane, mounted on the divider wall's two faces
        // (like targets hung on a range wall) - not literally parented to the wall itself (a
        // NetworkObject nested under another NetworkObject doesn't reliably spawn, and the wall's
        // own wildly non-uniform localScale would distort them) but to a plain mount object that
        // just tracks the wall's position every frame instead - see WallMountedTargetMount.
        // Server-authoritative hit detection via TargetController (PlayerWeapon raycasts, server
        // validates and flips each target green on hit, auto-resetting after a couple seconds).
        Material targetDefaultMat = CreateColorMaterial(RoomMaterialsFolder + "/PracticeTarget.mat", new Color(0.75f, 0.15f, 0.1f));
        Material targetHitMat = CreateColorMaterial(RoomMaterialsFolder + "/PracticeTargetHit.mat", new Color(0.15f, 0.75f, 0.2f));

        GameObject targetMount = new GameObject("WallMedallionMount");
        targetMount.transform.SetParent(galleryRoom.transform, false);
        targetMount.transform.position = wall.transform.position;
        var mountFollower = targetMount.AddComponent<WallMountedTargetMount>();
        var mountSo = new SerializedObject(mountFollower);
        mountSo.FindProperty("wall").objectReferenceValue = wall.transform;
        mountSo.ApplyModifiedPropertiesWithoutUndo();

        BuildWallMountedTargets(targetMount.transform, wall.transform.localScale.x, wall.transform.localScale.z, targetDefaultMat, targetHitMat);

        // Permanent barriers so a player can never physically cross into the other lane, whether
        // or not DividerWall itself is currently up:
        // - End caps seal the two small gaps between the divider wall's own ends and the room's
        //   own solid north/south walls - without these, a player can just walk around the wall
        //   entirely, wall up or down, since it doesn't reach either side wall. Sized/positioned
        //   from the wall's and room's actual current half-lengths (not separately hardcoded
        //   numbers) so they can never drift out of sync with either one.
        // - A permanent low "ankle wall" spans DividerWall's exact footprint and never drops, so
        //   even during a duel window (wall down, sightlines/shots open) a player still can't
        //   just walk across the gap - only shoot across it.
        const float ankleWallHeight = 0.4f;
        float wallHalfLength = wall.transform.localScale.z / 2f;
        float endCapGap = galleryHalfDepth - wallHalfLength;
        float endCapCenterZ = wallHalfLength + endCapGap / 2f;
        BuildBox(galleryRoom.transform, "DividerEndCap (North)", new Vector3(0f, roomHeight / 2f, endCapCenterZ), new Vector3(0.5f, roomHeight, endCapGap), galleryWallMat);
        BuildBox(galleryRoom.transform, "DividerEndCap (South)", new Vector3(0f, roomHeight / 2f, -endCapCenterZ), new Vector3(0.5f, roomHeight, endCapGap), galleryWallMat);
        BuildBox(galleryRoom.transform, "DividerAnkleWall", new Vector3(0f, ankleWallHeight / 2f, 0f), new Vector3(0.5f, ankleWallHeight, wall.transform.localScale.z), galleryWallMat);

        // Foreground range decoration - a wagon and a dynamite crate per lane (mirrored), each
        // carrying a few more medallion targets, so there's something to shoot at closer than the
        // wall too. Positions/rotations are a first guess (see BuildLaneProps) - not yet visually
        // confirmed, most likely to need tweaking once actually seen in the Editor.
        BuildLaneProps(galleryRoom.transform, spawnA.position.x, gallerySizeScale, targetDefaultMat, targetHitMat);
        BuildLaneProps(galleryRoom.transform, spawnB.position.x, gallerySizeScale, targetDefaultMat, targetHitMat);

        // A wooden shooting counter at each lane's firing line, near the spawn point - purely
        // decorative range dressing inspired by real shooting-gallery photo references (a long
        // wooden rail/counter along the front of the stations), not a gameplay barrier.
        BuildShootingCounter(galleryRoom.transform, spawnA.position, galleryHalfDepth, counterMat);
        BuildShootingCounter(galleryRoom.transform, spawnB.position, galleryHalfDepth, counterMat);

        // --- Bar room: player spawn / lobby, door on its west side to meet the gallery's ---
        // Enlarged from the original plain 14x18 box (to 22x22) to comfortably fit a proper
        // greybox tavern layout - a bar counter, dining tables, entrance barrels - loosely
        // modeled on a real tavern floor-plan reference, sized relative to characters standing
        // ~3.33 units tall rather than copying the reference's own proportions directly. Still a
        // plain rectangular shell (unlike the reference's octagonal bay window alcove) - that's
        // architecture rather than a greybox furniture pass, left for later if wanted.
        const float barHalfWidth = 11f;
        const float barHalfDepth = 11f;
        GameObject barRoom = new GameObject("BarRoom");
        // Sits flush against the (now-larger) gallery room's east wall, same as it always has -
        // derived from the gallery's actual half-width instead of a separately hardcoded number
        // so the two rooms' doorways stay lined up regardless of gallerySizeScale.
        Vector3 barCenter = new Vector3(galleryHalfWidth + barHalfWidth, 0f, 0f);
        BuildRoomShell(barRoom.transform, barCenter, new Vector3(barHalfWidth * 2f, roomHeight, barHalfDepth * 2f), wallThickness,
            doorOnEast: false, doorOnWest: true, doorCenterZ: 0f, doorWidth: doorWidth, doorHeight: doorHeight,
            floorMat: barFloorMat, wallMat: barWallMat, ceilingMat: ceilingMat);

        // Just inside the door, in a clear patch of floor ahead of the furniture below.
        Transform barSpawnA = BuildSpawnPoint(barRoom.transform, barCenter + new Vector3(-barHalfWidth + 2.5f, 1f, -1.5f), Vector3.left, "BarSpawnPointA");
        Transform barSpawnB = BuildSpawnPoint(barRoom.transform, barCenter + new Vector3(-barHalfWidth + 2.5f, 1f, 1.5f), Vector3.left, "BarSpawnPointB");

        BuildBarInterior(barRoom.transform, barCenter, barHalfWidth, barHalfDepth);

        // --- Doorway trigger sitting exactly on the shared wall plane between the two rooms ---
        GameObject doorTriggerGO = new GameObject("GalleryEntryTrigger");
        doorTriggerGO.transform.position = new Vector3(galleryHalfWidth, doorHeight / 2f, 0f);
        var doorCollider = doorTriggerGO.AddComponent<BoxCollider>();
        doorCollider.isTrigger = true;
        doorCollider.size = new Vector3(wallThickness * 4f, doorHeight, doorWidth);
        doorTriggerGO.AddComponent<GalleryEntryTrigger>();

        GameObject matchManagerGO = new GameObject("MatchManager");
        AssignUniqueGlobalObjectIdHash(matchManagerGO.AddComponent<NetworkObject>());
        var matchManager = matchManagerGO.AddComponent<MatchManager>();
        var so = new SerializedObject(matchManager);
        so.FindProperty("playerASpawnPoint").objectReferenceValue = spawnA;
        so.FindProperty("playerBSpawnPoint").objectReferenceValue = spawnB;
        so.FindProperty("barSpawnPointA").objectReferenceValue = barSpawnA;
        so.FindProperty("barSpawnPointB").objectReferenceValue = barSpawnB;
        so.FindProperty("dividerWall").objectReferenceValue = wallController;
        // Only set if DuelSetup has already built it (this scaffold runs before that patch tool
        // on a from-scratch project) - re-wired here too so a later "Rebuild Arena Scene Only"
        // doesn't silently drop the reference, matching every other cross-reference this method
        // already wires.
        so.FindProperty("dummyEnemyPrefab").objectReferenceValue =
            AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Networking/DummyEnemyPrefab.prefab");
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(scene, ArenaScenePath);
    }

    /// <summary>Floor, ceiling and four walls (center.y is floor level); one east/west wall can
    /// have a doorway gap, the rest are solid.</summary>
    private static void BuildRoomShell(Transform parent, Vector3 center, Vector3 size, float wallThickness,
        bool doorOnEast, bool doorOnWest, float doorCenterZ, float doorWidth, float doorHeight,
        Material floorMat, Material wallMat, Material ceilingMat)
    {
        float hx = size.x / 2f;
        float hz = size.z / 2f;
        float h = size.y;

        BuildBox(parent, "Floor", center + new Vector3(0f, -wallThickness / 2f, 0f),
            new Vector3(size.x + wallThickness * 2f, wallThickness, size.z + wallThickness * 2f), floorMat);
        BuildBox(parent, "Ceiling", center + new Vector3(0f, h + wallThickness / 2f, 0f),
            new Vector3(size.x + wallThickness * 2f, wallThickness, size.z + wallThickness * 2f), ceilingMat);

        BuildBox(parent, "North Wall", center + new Vector3(0f, h / 2f, hz + wallThickness / 2f), new Vector3(size.x, h, wallThickness), wallMat);
        BuildBox(parent, "South Wall", center + new Vector3(0f, h / 2f, -hz - wallThickness / 2f), new Vector3(size.x, h, wallThickness), wallMat);

        if (doorOnEast)
        {
            BuildDoorWall(parent, "East Wall", center + new Vector3(hx + wallThickness / 2f, 0f, 0f), size.z, h, wallThickness, doorCenterZ, doorWidth, doorHeight, wallMat);
        }
        else
        {
            BuildBox(parent, "East Wall", center + new Vector3(hx + wallThickness / 2f, h / 2f, 0f), new Vector3(wallThickness, h, size.z), wallMat);
        }

        if (doorOnWest)
        {
            BuildDoorWall(parent, "West Wall", center + new Vector3(-hx - wallThickness / 2f, 0f, 0f), size.z, h, wallThickness, doorCenterZ, doorWidth, doorHeight, wallMat);
        }
        else
        {
            BuildBox(parent, "West Wall", center + new Vector3(-hx - wallThickness / 2f, h / 2f, 0f), new Vector3(wallThickness, h, size.z), wallMat);
        }
    }

    /// <summary>An east/west-facing wall (runs along Z) with a doorway gap cut out of it: two
    /// side segments plus a lintel above the doorway.</summary>
    private static void BuildDoorWall(Transform parent, string name, Vector3 wallCenter, float wallLength, float height,
        float thickness, float doorCenterZ, float doorWidth, float doorHeight, Material material)
    {
        float half = wallLength / 2f;
        float doorHalf = doorWidth / 2f;

        float leftLength = (doorCenterZ - doorHalf) - (-half);
        float rightLength = half - (doorCenterZ + doorHalf);

        if (leftLength > 0.01f)
        {
            float segCenterZ = -half + leftLength / 2f;
            BuildBox(parent, name + " (Left)", wallCenter + new Vector3(0f, height / 2f, segCenterZ), new Vector3(thickness, height, leftLength), material);
        }

        if (rightLength > 0.01f)
        {
            float segCenterZ = half - rightLength / 2f;
            BuildBox(parent, name + " (Right)", wallCenter + new Vector3(0f, height / 2f, segCenterZ), new Vector3(thickness, height, rightLength), material);
        }

        if (height > doorHeight)
        {
            BuildBox(parent, name + " (Lintel)",
                wallCenter + new Vector3(0f, doorHeight + (height - doorHeight) / 2f, doorCenterZ),
                new Vector3(thickness, height - doorHeight, doorWidth), material);
        }
    }

    private static void BuildBox(Transform parent, string name, Vector3 center, Vector3 size, Material material = null)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        if (material != null)
        {
            go.GetComponent<Renderer>().sharedMaterial = material;
        }
    }

    private static void BuildCylinder(Transform parent, string name, Vector3 center, float radius, float height, Material material = null)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        // Default cylinder mesh: radius 0.5, height 2 at scale 1.
        go.transform.localScale = new Vector3(radius * 2f, height / 2f, radius * 2f);
        if (material != null)
        {
            go.GetComponent<Renderer>().sharedMaterial = material;
        }
    }

    /// <summary>Greybox furniture pass for the bar - a counter with shelving and a row of stools
    /// along the east wall (opposite the gallery doorway on the west), a handful of round dining
    /// tables with stools scattered across the remaining floor, and a couple of barrels near the
    /// entrance. All simple colored primitives, no imported props - a blockout of the room's
    /// layout and scale (sized relative to characters standing ~3.33 units tall) loosely modeled
    /// on a real tavern floor-plan reference, rather than final dressing. Not yet visually
    /// confirmed - first pass at fitting all of this into the room without anything overlapping.
    /// </summary>
    private static void BuildBarInterior(Transform barRoom, Vector3 barCenter, float roomHalfWidth, float roomHalfDepth)
    {
        Material counterMat = CreateColorMaterial(RoomMaterialsFolder + "/BarCounterWood.mat", new Color(0.32f, 0.19f, 0.1f));
        Material shelfMat = CreateColorMaterial(RoomMaterialsFolder + "/BarShelfWood.mat", new Color(0.28f, 0.16f, 0.08f));
        Material tableMat = CreateColorMaterial(RoomMaterialsFolder + "/BarTableWood.mat", new Color(0.45f, 0.29f, 0.16f));
        Material stoolMat = CreateColorMaterial(RoomMaterialsFolder + "/BarStoolWood.mat", new Color(0.36f, 0.22f, 0.12f));
        Material barrelMat = CreateColorMaterial(RoomMaterialsFolder + "/BarBarrelWood.mat", new Color(0.4f, 0.26f, 0.13f));

        // --- Bar counter along the east wall (the far side from the gallery doorway) ---
        const float counterHeight = 1.2f; // Matches the gallery's own ShootingCounter - a chest/waist-height counter.
        const float counterThickness = 0.8f;
        float counterLength = roomHalfDepth * 2f - 6f; // Leaves a margin at each end before the corners.
        float counterX = barCenter.x + roomHalfWidth - 2f;
        BuildBox(barRoom, "BarCounter", new Vector3(counterX, counterHeight / 2f, barCenter.z), new Vector3(counterThickness, counterHeight, counterLength), counterMat);

        // Shelving against the wall behind the counter.
        BuildBox(barRoom, "BarShelf", new Vector3(barCenter.x + roomHalfWidth - 0.6f, 1.5f, barCenter.z), new Vector3(0.4f, 3f, counterLength), shelfMat);

        // A row of stools along the counter's room-facing side.
        float stoolZStart = -counterLength / 2f + 1.5f;
        float stoolSpacing = (counterLength - 3f) / 4f;
        for (int i = 0; i < 5; i++)
        {
            float z = barCenter.z + stoolZStart + i * stoolSpacing;
            BuildCylinder(barRoom, "BarStool", new Vector3(counterX - 1.3f, 0.5f, z), 0.3f, 1f, stoolMat);
        }

        // --- Dining area: a handful of round tables with stools, scattered across the open
        // floor between the doorway and the counter ---
        Vector3[] tableOffsetsFromCenter =
        {
            new Vector3(-5f, 0f, -5f),
            new Vector3(-5f, 0f, 4f),
            new Vector3(-1f, 0f, -2f),
            new Vector3(-1f, 0f, 6f),
        };

        foreach (Vector3 offset in tableOffsetsFromCenter)
        {
            BuildBarTable(barRoom, barCenter + offset, tableMat, stoolMat);
        }

        // --- A couple of barrels just inside the entrance ---
        BuildCylinder(barRoom, "EntranceBarrel", barCenter + new Vector3(-roomHalfWidth + 3f, 0.6f, -4f), 0.5f, 1.2f, barrelMat);
        BuildCylinder(barRoom, "EntranceBarrel", barCenter + new Vector3(-roomHalfWidth + 3f, 0.6f, 4f), 0.5f, 1.2f, barrelMat);
    }

    /// <summary>One round table with four stools around it.</summary>
    private static void BuildBarTable(Transform parent, Vector3 center, Material tableMat, Material stoolMat)
    {
        const float tableHeight = 0.9f;
        BuildCylinder(parent, "BarTable", center + new Vector3(0f, tableHeight / 2f, 0f), 0.9f, tableHeight, tableMat);

        Vector3[] stoolOffsets =
        {
            new Vector3(1.4f, 0f, 0f),
            new Vector3(-1.4f, 0f, 0f),
            new Vector3(0f, 0f, 1.4f),
            new Vector3(0f, 0f, -1.4f),
        };

        foreach (Vector3 offset in stoolOffsets)
        {
            BuildCylinder(parent, "BarTableStool", center + offset + new Vector3(0f, 0.5f, 0f), 0.3f, 1f, stoolMat);
        }
    }

    private static Material CreateColorMaterial(string path, Color color)
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

        mat.SetColor("_BaseColor", color);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static Transform BuildLane(string name, Transform parent, Vector3 origin, Vector3 facing)
    {
        GameObject lane = new GameObject(name);
        lane.transform.SetParent(parent, false);
        lane.transform.position = origin;

        GameObject spawn = new GameObject("PlayerSpawnPoint");
        spawn.transform.SetParent(lane.transform, false);
        spawn.transform.localPosition = new Vector3(0f, 1f, -5f);
        spawn.transform.rotation = Quaternion.LookRotation(facing, Vector3.up);

        return spawn.transform;
    }

    /// <summary>Six small medallion targets, three per lane, parented under the plain mount object
    /// that tracks the divider wall's position (see BuildArenaScene / WallMountedTargetMount) so
    /// they move - and drop out of reach - right along with it during a duel window, with no
    /// separate visibility logic needed.</summary>
    private static void BuildWallMountedTargets(Transform mount, float wallThickness, float wallLength, Material defaultMat, Material hitMat)
    {
        // Three times the tracer's own diameter (read live off PlayerPrefab rather than
        // hardcoded, so this stays in sync if that's ever re-tuned) - "how wide the bullet itself
        // looks" is the natural yardstick for "how small a precision target should be" here.
        float targetDiameter = GetTracerWidthFromPlayerPrefab() * 3f;
        const float targetThickness = 0.05f;

        float mountOffset = wallThickness / 2f + targetThickness / 2f;

        // Same three (height, horizontal) spots on both faces - lane A sees the wall's -X face,
        // lane B the +X face (matching GalleryLaneA/B sitting either side of the wall at x=0).
        // Horizontal spread is expressed as a fraction of the wall's own length (the original
        // -5/0/5 positions out of a 16-unit-long wall) rather than fixed units, so it stays
        // proportionally placed regardless of gallerySizeScale.
        (float y, float zFraction)[] layout = { (1.5f, -5f / 16f), (0.7f, 0f), (1.5f, 5f / 16f) };

        foreach ((float y, float zFraction) in layout)
        {
            float z = zFraction * wallLength;
            BuildMedallionTarget(mount, new Vector3(-mountOffset, y, z), targetDiameter, targetThickness, defaultMat, hitMat);
            BuildMedallionTarget(mount, new Vector3(mountOffset, y, z), targetDiameter, targetThickness, defaultMat, hitMat);
        }
    }

    /// <summary>Reads PlayerWeapon's tracerWidth straight off the already-built PlayerPrefab, so
    /// medallion sizing stays correct even if that's tuned later - falls back to PlayerWeapon's
    /// own current default if the prefab (or its PlayerWeapon/RevolverSetup) hasn't been built yet,
    /// which is the case the very first time ScaffoldM1 runs.</summary>
    private static float GetTracerWidthFromPlayerPrefab()
    {
        const float fallback = 0.05f;

        GameObject playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        PlayerWeapon weapon = playerPrefab != null ? playerPrefab.GetComponent<PlayerWeapon>() : null;
        if (weapon == null)
        {
            return fallback;
        }

        SerializedProperty tracerWidthProp = new SerializedObject(weapon).FindProperty("tracerWidth");
        return tracerWidthProp != null ? tracerWidthProp.floatValue : fallback;
    }

    /// <summary>A small flat disc "hung" on a wall face, facing outward along local X - a
    /// 90-degree rotation reorients the cylinder primitive (whose mesh axis defaults to local Y)
    /// so its two flat circular faces point along X instead, like a coin mounted on the wall
    /// rather than a can standing on the ground. Assumes parent is unscaled (true of the wall's
    /// own mount object) - see BuildMedallionTargetAtWorldPosition for the scaled-parent case.
    /// </summary>
    private static void BuildMedallionTarget(Transform parent, Vector3 localPosition, float diameter, float thickness,
        Material defaultMat, Material hitMat)
    {
        GameObject target = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        target.transform.SetParent(parent, false);
        target.transform.localPosition = localPosition;
        target.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);

        // Default cylinder mesh: radius 0.5, height 2, both along local Y - after the rotation
        // above, local Y becomes the outward-facing thickness axis and local X/Z become the
        // disc's face plane, so scale.y controls thickness and scale.x/z control diameter.
        float diameterScale = diameter; // world diameter = scale.x * 0.5 (radius) * 2
        float thicknessScale = thickness / 2f; // world thickness = scale.y * 2 (default height)
        target.transform.localScale = new Vector3(diameterScale, thicknessScale, diameterScale);

        FinalizeMedallionTarget(target, defaultMat, hitMat);
    }

    /// <summary>Same medallion as BuildMedallionTarget, but positioned/oriented directly in world
    /// space (faceNormal is the direction the disc's flat face should point) with its scale
    /// compensated for an arbitrarily (uniformly) scaled parent - for props like the wagon/crate,
    /// which get auto-scaled by RangePropsSetup, unlike the wall's dedicated scale-1 mount.
    /// </summary>
    private static void BuildMedallionTargetAtWorldPosition(Transform parent, Vector3 worldPosition, Vector3 faceNormal,
        float diameter, float thickness, Material defaultMat, Material hitMat)
    {
        GameObject target = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        target.transform.SetParent(parent, false);
        target.transform.position = worldPosition;
        target.transform.rotation = Quaternion.FromToRotation(Vector3.up, faceNormal);

        float parentScale = Mathf.Max(parent.lossyScale.x, 0.0001f);
        target.transform.localScale = new Vector3(diameter / parentScale, thickness / 2f / parentScale, diameter / parentScale);

        FinalizeMedallionTarget(target, defaultMat, hitMat);
    }

    private static void FinalizeMedallionTarget(GameObject target, Material defaultMat, Material hitMat)
    {
        target.name = "PracticeTarget";

        Renderer targetRenderer = target.GetComponent<Renderer>();
        targetRenderer.sharedMaterial = defaultMat;

        NetworkObject networkObject = target.AddComponent<NetworkObject>();
        AssignUniqueGlobalObjectIdHash(networkObject);

        var controller = target.AddComponent<TargetController>();
        var so = new SerializedObject(controller);
        so.FindProperty("targetRenderer").objectReferenceValue = targetRenderer;
        so.FindProperty("defaultMaterial").objectReferenceValue = defaultMat;
        so.FindProperty("hitMaterial").objectReferenceValue = hitMat;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>Places a wagon and a dynamite crate in one lane, each with a few medallion
    /// targets mounted on the face pointing back toward that lane's own spawn point.
    /// laneOriginX is that lane's spawn X position (+-10*gallerySizeScale - see BuildArenaScene);
    /// prop X positions are fractions of it (0.5 and 0.4, matching the original 5/4 out of 10)
    /// so they stay proportionally between the spawn and the wall regardless of gallerySizeScale,
    /// same reasoning as BuildWallMountedTargets' fractional spread. Z offsets scale directly by
    /// gallerySizeScale since they aren't derived from any other already-scaled value. Positions/
    /// rotations are still a first guess overall - not yet visually confirmed against the actual
    /// prop meshes.</summary>
    private static void BuildLaneProps(Transform parent, float laneOriginX, float gallerySizeScale, Material defaultMat, Material hitMat)
    {
        GameObject wagonPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PropPrefabFolder + "/Wagon.prefab");
        GameObject cratePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PropPrefabFolder + "/DynamiteCrate.prefab");
        if (wagonPrefab == null || cratePrefab == null)
        {
            Debug.LogWarning("[ProjectScaffolder] Wagon/DynamiteCrate prefab not found under " + PropPrefabFolder +
                              " - run 'Setup Range Props (Wagon + Dynamite Crate)' first, then rebuild the Arena scene.");
            return;
        }

        // Medallions face back toward wherever a player walking in from the door/spawn would
        // naturally be standing - the opposite of each lane's own "face the wall" spawn direction
        // (Vector3.right for lane A, Vector3.left for lane B - see BuildLane).
        float laneSign = Mathf.Sign(laneOriginX);
        Vector3 faceNormal = new Vector3(-laneSign, 0f, 0f);

        GameObject wagon = PlaceRangeProp(wagonPrefab, parent, new Vector3(laneOriginX * 0.5f, 0f, 3f * gallerySizeScale), Quaternion.identity);
        AddMedallionsOnPropFace(wagon, faceNormal, 3, defaultMat, hitMat);

        GameObject crate = PlaceRangeProp(cratePrefab, parent, new Vector3(laneOriginX * 0.4f, 0f, -3f * gallerySizeScale), Quaternion.identity);
        AddMedallionsOnPropFace(crate, faceNormal, 2, defaultMat, hitMat);
    }

    /// <summary>A wooden counter/rail at a lane's firing line, offset toward the wall from the
    /// player's own spawn point (whichever direction that is for this lane, rather than a
    /// hardcoded side, so it works the same for both mirrored lanes) - purely decorative range
    /// dressing inspired by real shooting-gallery photo references (a long wooden counter along
    /// the front of the stations, guns resting on top), not a gameplay barrier. Runs the full
    /// depth of the gallery room (galleryHalfDepth, the same measurement BuildArenaScene derives
    /// the end caps from) so its ends actually touch the room's north/south walls instead of
    /// stopping short as a disconnected-looking segment.</summary>
    private static void BuildShootingCounter(Transform parent, Vector3 spawnPosition, float galleryHalfDepth, Material material)
    {
        const float counterHeight = 1.2f;
        float towardWallSign = -Mathf.Sign(spawnPosition.x); // Wall sits at x=0; spawn is off to one side.

        Vector3 center = new Vector3(spawnPosition.x + towardWallSign * 1.2f, counterHeight / 2f, 0f);
        BuildBox(parent, "ShootingCounter", center, new Vector3(0.7f, counterHeight, galleryHalfDepth * 2f), material);
    }

    /// <summary>Instantiates a prop and places it so its lowest point sits exactly on the floor at
    /// the given position (position.y is treated as the floor, not as the prop's own pivot) -
    /// these are Blender-authored assets with no guarantee their pivot sits at the model's base
    /// like the character rigs' does, and assuming so is exactly what left them spawning partly
    /// under the floor the first time. Measuring the actual mesh bounds after placement and
    /// correcting from there works regardless of where the pivot turns out to be.</summary>
    private static GameObject PlaceRangeProp(GameObject prefab, Transform parent, Vector3 position, Quaternion rotation)
    {
        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        instance.transform.SetParent(parent, false);
        instance.transform.position = position;
        instance.transform.rotation = rotation;

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

        if (hasBounds)
        {
            float floorCorrection = position.y - bounds.min.y;
            instance.transform.position += new Vector3(0f, floorCorrection, 0f);
        }

        return instance;
    }

    /// <summary>Spreads `count` medallions across the face of `prop` whose outward normal is
    /// `faceNormal` (only supports a normal along world X, matching how BuildLaneProps lays these
    /// out) - measured from the prop's own current world-space renderer bounds, so it adapts to
    /// whatever RangePropsSetup's auto-scale produced rather than needing exact numbers guessed
    /// ahead of time. The spread axis (world Z) is still a guess about which way is "across" the
    /// prop - not yet visually confirmed.</summary>
    private static void AddMedallionsOnPropFace(GameObject prop, Vector3 faceNormal, int count, Material defaultMat, Material hitMat)
    {
        Bounds bounds = default;
        bool hasBounds = false;
        foreach (Renderer renderer in prop.GetComponentsInChildren<Renderer>(true))
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
            return;
        }

        float targetDiameter = GetTracerWidthFromPlayerPrefab() * 3f;
        const float targetThickness = 0.05f;

        // Just past the prop's measured surface on the face-normal side, so the medallion sits
        // proud of it instead of embedded.
        float faceX = bounds.center.x - faceNormal.x * (bounds.extents.x + targetThickness / 2f + 0.02f);
        float midHeight = bounds.center.y;
        float halfSpread = bounds.extents.z * 0.6f; // stay a bit inside the prop's own footprint

        for (int i = 0; i < count; i++)
        {
            float t = count > 1 ? (float)i / (count - 1) - 0.5f : 0f; // -0.5..0.5 across the spread
            Vector3 worldPos = new Vector3(faceX, midHeight, bounds.center.z + t * 2f * halfSpread);
            BuildMedallionTargetAtWorldPosition(prop.transform, worldPos, faceNormal, targetDiameter, targetThickness, defaultMat, hitMat);
        }
    }

    // Simple per-scaffold-run counter (never 0, which NGO treats as "unassigned") - see
    // AssignUniqueGlobalObjectIdHash for why this is needed at all.
    private static uint nextGlobalObjectIdHash = 1;

    /// <summary>
    /// NetworkObjects added via editor script don't reliably get their internal
    /// GlobalObjectIdHash assigned before the scene saves - that normally happens through NGO's
    /// own OnValidate, which is GUI-triggered and can be skipped entirely in this kind of
    /// scripted/batch workflow. Multiple scene-placed NetworkObjects left at the default hash of
    /// 0 collide and crash NGO's scene-object registration the moment a second one loads
    /// (confirmed via a runtime exception: "already contains the same GlobalObjectIdHash value 0").
    /// First attempt used GlobalObjectId.GetGlobalObjectIdSlow() hoping it'd already be unique per
    /// object - it isn't, for objects that haven't been serialized to disk yet: brand new,
    /// unsaved GameObjects don't have a real per-object file ID assigned, so every one of them
    /// returned the exact same placeholder value (confirmed: all 7 objects got identical hash
    /// 2930359547). A plain incrementing counter is simple and actually guarantees uniqueness
    /// within this scaffold run, which is all that's required.
    /// </summary>
    private static void AssignUniqueGlobalObjectIdHash(NetworkObject networkObject)
    {
        var so = new SerializedObject(networkObject);
        so.FindProperty("GlobalObjectIdHash").uintValue = nextGlobalObjectIdHash++;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static Transform BuildSpawnPoint(Transform parent, Vector3 worldPosition, Vector3 facing, string name)
    {
        GameObject spawn = new GameObject(name);
        spawn.transform.SetParent(parent, false);
        spawn.transform.position = worldPosition;
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
