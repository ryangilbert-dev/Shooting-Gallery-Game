using UnityEditor;
using UnityEngine;

/// <summary>
/// Renders the Revolver prefab (optionally several rotation candidates side by side) to a PNG for
/// direct visual inspection - this is how we found that the source FBX's own orientation put its
/// "flat" resting plane in world XZ, and confirmed Euler(-90, 0, 0) as the fix, without needing an
/// in-game screenshot round trip for every guess. Kept around as a reusable tool for verifying any
/// future model's proportions/orientation the same way. Requires running WITHOUT -nographics
/// (needs a real graphics device to render).
/// </summary>
public static class RevolverPreviewCapture
{
    private const string PrefabPath = "Assets/Prefabs/Weapons/Revolver.prefab";

    private static readonly (string label, Vector3 euler)[] Candidates =
    {
        ("current fix", new Vector3(-90f, 0f, 0f)),
    };

    [MenuItem("Tools/Shooting Gallery/Capture Revolver Preview")]
    public static void Capture()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogError("[RevolverPreviewCapture] Revolver prefab not found at " + PrefabPath);
            return;
        }

        float spacing = 0.6f;
        var instances = new GameObject[Candidates.Length];
        for (int i = 0; i < Candidates.Length; i++)
        {
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.position = new Vector3(i * spacing, 0f, 0f);
            instance.transform.rotation = Quaternion.Euler(Candidates[i].euler);
            instances[i] = instance;
        }

        GameObject lightGO = new GameObject("PreviewLight");
        Light light = lightGO.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.3f;
        lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        GameObject fillLightGO = new GameObject("PreviewFillLight");
        Light fillLight = fillLightGO.AddComponent<Light>();
        fillLight.type = LightType.Directional;
        fillLight.intensity = 0.5f;
        fillLightGO.transform.rotation = Quaternion.Euler(-30f, 150f, 0f);

        float totalWidth = spacing * Candidates.Length;
        Vector3 center = new Vector3(totalWidth / 2f - spacing / 2f, 0f, 0f);

        GameObject camGO = new GameObject("PreviewCamera");
        Camera cam = camGO.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.2f, 0.2f, 0.25f);
        cam.fieldOfView = 40f;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 20f;
        camGO.transform.position = center + new Vector3(0f, 0f, -Mathf.Max(totalWidth * 1.1f, 1.2f));
        camGO.transform.LookAt(center, Vector3.up);

        const int width = 1800;
        const int height = 500;
        var rt = new RenderTexture(width, height, 24);
        cam.targetTexture = rt;
        RenderTexture.active = rt;

        cam.Render();

        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();

        byte[] pngData = tex.EncodeToPNG();
        string outPath = System.IO.Path.Combine(Application.dataPath, "..", "revolver_preview.png");
        System.IO.File.WriteAllBytes(outPath, pngData);

        RenderTexture.active = null;
        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
        Object.DestroyImmediate(camGO);
        Object.DestroyImmediate(lightGO);
        Object.DestroyImmediate(fillLightGO);
        foreach (GameObject instance in instances)
        {
            Object.DestroyImmediate(instance);
        }

        var sb = new System.Text.StringBuilder("[RevolverPreviewCapture] Candidates left-to-right: ");
        foreach (var c in Candidates)
        {
            sb.Append(c.label + ", ");
        }
        Debug.Log(sb.ToString());
        Debug.Log("[RevolverPreviewCapture] Saved comparison grid to " + outPath);
    }
}
