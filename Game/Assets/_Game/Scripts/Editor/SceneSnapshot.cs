using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Jam.EditorTools
{
    /// <summary>
    /// Renders an arena scene (Arena.unity, or -snapshotScene TwoLane) as it is in edit mode (no Play mode, so no runtime code runs) to a
    /// PNG, to check the scene shows up without the game running.
    /// Batch (needs a graphics device, so no -nographics):
    ///   Unity -batchmode -quit -projectPath Game -executeMethod Jam.EditorTools.SceneSnapshot.Arena -snapshotPath out.png
    /// </summary>
    public static class SceneSnapshot
    {
        [MenuItem("nov26jam/Snapshot Arena (edit mode)")]
        public static void Arena()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-snapshotScene");
            ArenaDef def = (i >= 0 && i + 1 < args.Length ? Arenas.ByName(args[i + 1]) : null) ?? Arenas.Classic;
            Snapshot(def, args);
        }

        [MenuItem("nov26jam/Snapshot TwoLane (edit mode)")]
        public static void TwoLane() => Snapshot(Arenas.TwoLane, System.Environment.GetCommandLineArgs());

        private static void Snapshot(ArenaDef def, string[] args)
        {
            string path = $"Builds/{def.Scene.ToLowerInvariant()}-snapshot.png";
            int i = System.Array.IndexOf(args, "-snapshotPath");
            if (i >= 0 && i + 1 < args.Length)
                path = args[i + 1];

            EditorSceneManager.OpenScene($"Assets/_Game/Scenes/{def.Scene}.unity", OpenSceneMode.Single);

            var go = new GameObject("Snapshot Camera");
            Camera cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = def.Height * 0.5f + 1f;
            cam.transform.position = new Vector3(0f, def.Height * 0.5f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.magenta; // anything not drawn shows up as magenta

            const int width = 1280;
            int height = Mathf.RoundToInt(width * (def.Height + 2f) / (def.HalfWidth * 2f + 4f));
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
