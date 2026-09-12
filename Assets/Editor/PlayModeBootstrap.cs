using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// Forces every Play-mode session in the Editor to start from Bootstrap.unity, regardless of
/// which scene is currently open in the Hierarchy. Without this, pressing Play while editing e.g.
/// MainMenu or Arena skips Bootstrap entirely - ConnectionManager (created there) never exists,
/// so anything that calls ConnectionManager.Instance throws a NullReferenceException. A built
/// player never has this problem since it always starts from Build Settings scene 0; this makes
/// the Editor behave the same way.
/// </summary>
[InitializeOnLoad]
public static class PlayModeBootstrap
{
    private const string BootstrapScenePath = "Assets/Scenes/Bootstrap.unity";

    static PlayModeBootstrap()
    {
        EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(BootstrapScenePath);
    }
}
