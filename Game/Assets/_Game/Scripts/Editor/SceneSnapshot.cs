using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Jam.EditorTools
{
    /// <summary>
    /// Renders Arena.unity as it is in edit mode (no Play mode, so no runtime code runs) to a
    /// PNG, to check the scene shows up without the game running.
    /// Batch (needs a graphics device, so no -nographics):
    ///   Unity -batchmode -quit -projectPath Game -executeMethod Jam.EditorTools.SceneSnapshot.Arena -snapshotPath out.png
    /// </summary>
    public static class SceneSnapshot
    {
        [MenuItem("nov26jam/Snapshot Arena (edit mode)")]
        public static void Arena()
        {
            string path = "Builds/arena-snapshot.png";
            string[] args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-snapshotPath");
            if (i >= 0 && i + 1 < args.Length)
                path = args[i + 1];

            EditorSceneManager.OpenScene(ProjectBuilder.ArenaScenePath, OpenSceneMode.Single);

            var go = new GameObject("Snapshot Camera");
            Camera cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = ArenaLayout.Height * 0.5f + 1f;
            cam.transform.position = new Vector3(0f, ArenaLayout.Height * 0.5f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.magenta; // anything not drawn shows up as magenta

            const int width = 1280, height = 920;
            var rt = new RenderTexture(width, height, 24);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(rt);
            Debug.Log($"[SceneSnapshot] Wrote {Path.GetFullPath(path)} ({SystemInfo.graphicsDeviceType}, pipeline {UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.name ?? "built-in"})");
        }
    }
}
