using UnityEditor;
using UnityEngine;

/// <summary>
/// Renders the Revolver prefab - one or more rotation candidates, each in its own perfectly
/// centered frame (avoids the perspective/keystone distortion a side-by-side row introduces for
/// anything off-center) - to PNGs for direct visual inspection. This is how we found that the
/// source FBX's own orientation put its "flat" resting plane in world XZ, and confirmed
/// Euler(-90, 0, 0) as the fix, without needing an in-game screenshot round trip for every guess.
/// Kept around as a reusable tool for verifying any future model's proportions/orientation the
/// same way. Requires running WITHOUT -nographics (needs a real graphics device to render).
/// </summary>
public static class RevolverPreviewCapture
{
    private const string PrefabPath = "Assets/Prefabs/Weapons/Revolver.prefab";

    private static readonly Quaternion Base = Quaternion.Euler(-90f, 0f, 0f);

    private static readonly (string label, Quaternion rotation)[] Candidates =
    {
        ("current", Quaternion.Euler(0f, 0f, -90f) * Base),
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

        foreach (var candidate in Candidates)
        {
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = candidate.rotation;

            bool hasBounds = false;
            Bounds bounds = default;
            foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (!hasBounds)
                {
                    bounds = r.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }

            float radius = hasBounds ? Mathf.Max(bounds.extents.magnitude, 0.05f) : 1f;
            Vector3 center = hasBounds ? bounds.center : Vector3.zero;

            GameObject camGO = new GameObject("PreviewCamera");
            Camera cam = camGO.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.2f, 0.2f, 0.25f);
            cam.fieldOfView = 35f;
            cam.nearClipPlane = radius * 0.05f;
            cam.farClipPlane = radius * 20f;
            camGO.transform.position = center + new Vector3(0f, 0f, -radius * 3f);
            camGO.transform.LookAt(center, Vector3.up);

            const int width = 700;
            const int height = 700;
            var rt = new RenderTexture(width, height, 24);
            cam.targetTexture = rt;
            RenderTexture.active = rt;

            cam.Render();

            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();

            byte[] pngData = tex.EncodeToPNG();
            string outPath = System.IO.Path.Combine(Application.dataPath, "..", $"revolver_preview_{candidate.label}.png");
            System.IO.File.WriteAllBytes(outPath, pngData);
            Debug.Log($"[RevolverPreviewCapture] Saved '{candidate.label}' to {outPath}");

            RenderTexture.active = null;
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(camGO);
            Object.DestroyImmediate(instance);
        }

        Object.DestroyImmediate(lightGO);
        Object.DestroyImmediate(fillLightGO);
    }
}
