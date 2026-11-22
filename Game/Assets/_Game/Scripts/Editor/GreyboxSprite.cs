using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Jam.EditorTools
{
    /// <summary>
    /// A white 1x1-unit sprite saved as an asset, so greybox SpriteRenderers show up in the
    /// editor's Scene view. (At runtime SolidBlock and PlayerAvatar fall back to Fx.WhiteSprite,
    /// which is created in code and so never existed in edit mode.)
    /// Batch: Unity -batchmode -quit -executeMethod Jam.EditorTools.GreyboxSprite.AssignAll
    /// </summary>
    public static class GreyboxSprite
    {
        public const string Path = "Assets/_Game/Art/White.png";
        private const int Size = 4;

        /// <summary>The sprite asset, created on first use.</summary>
        public static Sprite Get()
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(Path);
            if (sprite != null)
                return sprite;

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            var pixels = new Color32[Size * Size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(pixels);
            File.WriteAllBytes(Path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(Path);

            var importer = (TextureImporter)AssetImporter.GetAtPath(Path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = Size;   // 1 x 1 world units, like Fx.WhiteSprite
            importer.filterMode = FilterMode.Point;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(Path);
        }

        /// <summary>Give every sprite-less SpriteRenderer in the arena scene and player prefab the greybox sprite.</summary>
        [MenuItem("nov26jam/Assign Greybox Sprites")]
        public static void AssignAll()
        {
            Sprite sprite = Get();

            var scene = EditorSceneManager.OpenScene(ProjectBuilder.ArenaScenePath, OpenSceneMode.Single);
            int count = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
                count += Assign(root, sprite);
            EditorSceneManager.SaveScene(scene);

            int prefabCount = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Game/Prefabs" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = PrefabUtility.LoadPrefabContents(path);
                int n = Assign(prefab, sprite);
                if (n > 0)
                    PrefabUtility.SaveAsPrefabAsset(prefab, path);
                PrefabUtility.UnloadPrefabContents(prefab);
                prefabCount += n;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[GreyboxSprite] Assigned {Path} to {count} renderers in the arena and {prefabCount} in prefabs.");
        }

        private static int Assign(GameObject root, Sprite sprite)
        {
            int n = 0;
            foreach (SpriteRenderer sr in root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr.sprite != null)
                    continue;
                sr.sprite = sprite;
                EditorUtility.SetDirty(sr);
                n++;
            }
            return n;
        }
    }
}
