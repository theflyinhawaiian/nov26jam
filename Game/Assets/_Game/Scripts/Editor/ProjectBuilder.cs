using System.Collections.Generic;
using FishNet.Component.Observing;
using FishNet.Component.Transforming;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Managing.Observing;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Observing;
using FishNet.Transporting.Bayou;
using Jam.Gameplay;
using Jam.Net;
using Jam.UI;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam.EditorTools
{
    /// <summary>
    /// Generates the M1 scenes and prefabs from code so they're reproducible and reviewable.
    /// Re-running overwrites Boot.unity, Arena.unity, Player.prefab and GamePrefabs.asset.
    /// Batch: Unity -batchmode -quit -executeMethod Jam.EditorTools.ProjectBuilder.RebuildAll
    /// </summary>
    public static class ProjectBuilder
    {
        private const string Root = "Assets/_Game";
        private const string ScenesDir = Root + "/Scenes";
        private const string PrefabsDir = Root + "/Prefabs";
        private const string DataDir = Root + "/Data";
        public const string BootScenePath = ScenesDir + "/Boot.unity";
        public const string ArenaScenePath = ScenesDir + "/" + NetConfig.ArenaScene + ".unity";
        private const string PlayerPrefabPath = PrefabsDir + "/Player.prefab";
        private const string GamePrefabsPath = DataDir + "/GamePrefabs.asset";
        private const string SceneConditionPath = "Assets/FishNet/Runtime/Observing/Conditions/ScriptableObjects/SceneCondition.asset";

        [MenuItem("nov26jam/Rebuild M1 Scenes and Prefabs")]
        public static void RebuildAll()
        {
            EnsureFolder(ScenesDir);
            EnsureFolder(PrefabsDir);
            EnsureFolder(DataDir);

            NetworkObject player = BuildPlayerPrefab();
            SinglePrefabObjects prefabs = BuildPrefabCollection(player);
            BuildArenaScene();
            BuildBootScene(player, prefabs);

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(BootScenePath, true),
                new EditorBuildSettingsScene(ArenaScenePath, true),
            };
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, ScriptingImplementation.Mono2x);

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Scenes/SampleScene.unity") != null)
                AssetDatabase.DeleteAsset("Assets/Scenes");

            AssetDatabase.SaveAssets();
            Debug.Log("[ProjectBuilder] Rebuilt M1 scenes and prefabs.");
        }

        private static NetworkObject BuildPlayerPrefab()
        {
            GameObject root = new("Player");
            NetworkObject nob = root.AddComponent<NetworkObject>();

            NetworkTransform nt = root.AddComponent<NetworkTransform>();
            SetFields(nt, so =>
            {
                so.FindProperty("_clientAuthoritative").boolValue = true;
                so.FindProperty("_synchronizeRotation").boolValue = false;
                so.FindProperty("_synchronizeScale").boolValue = false;
            });

            root.AddComponent<PositionHistory>();
            PlayerAvatar avatar = root.AddComponent<PlayerAvatar>();

            GameObject body = new("Body");
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(1f, 1.5f, 1f);
            SpriteRenderer sr = body.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 5;
            SetFields(avatar, so => so.FindProperty("_body").objectReferenceValue = sr);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
            Object.DestroyImmediate(root);
            return saved.GetComponent<NetworkObject>();
        }

        private static SinglePrefabObjects BuildPrefabCollection(NetworkObject player)
        {
            SinglePrefabObjects collection = AssetDatabase.LoadAssetAtPath<SinglePrefabObjects>(GamePrefabsPath);
            if (collection == null)
            {
                collection = ScriptableObject.CreateInstance<SinglePrefabObjects>();
                AssetDatabase.CreateAsset(collection, GamePrefabsPath);
            }
            SetFields(collection, so =>
            {
                SerializedProperty list = so.FindProperty("_prefabs");
                list.ClearArray();
                list.InsertArrayElementAtIndex(0);
                list.GetArrayElementAtIndex(0).objectReferenceValue = player;
            });
            EditorUtility.SetDirty(collection);
            return collection;
        }

        private static void BuildArenaScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 40x22 play area (matches PlayerAvatar.ArenaHalfExtents), purely visual.
            Block("Background", Vector2.zero, new Vector2(40f, 22f), new Color(0.12f, 0.12f, 0.14f), -10, collider: false);

            // Cover to check that terrain blocks hitscan on both client and server.
            Color wall = new(0.45f, 0.45f, 0.5f);
            Block("Pillar Center", new Vector2(0f, 0f), new Vector2(1.5f, 8f), wall, 0);
            Block("Pillar Left", new Vector2(-8f, 5f), new Vector2(1f, 4f), wall, 0);
            Block("Pillar Right", new Vector2(8f, -5f), new Vector2(1f, 4f), wall, 0);

            SpawnPoint(0, new Vector2(-15f, 0f));
            SpawnPoint(1, new Vector2(15f, 0f));

            EditorSceneManager.SaveScene(scene, ArenaScenePath);
        }

        private static void BuildBootScene(NetworkObject player, PrefabObjects prefabs)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject camGo = new("Main Camera") { tag = "MainCamera" };
            camGo.transform.position = new Vector3(0f, 0f, -10f);
            Camera cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 9f; // one screen = 32x18 units at 16:9
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.07f);
            camGo.AddComponent<CameraFollow>();

            GameObject nmGo = new("NetworkManager");
            NetworkManager nm = nmGo.AddComponent<NetworkManager>();

            Bayou bayou = nmGo.AddComponent<Bayou>();
            SetFields(bayou, so =>
            {
                so.FindProperty("_port").intValue = NetConfig.ServerPort;
                so.FindProperty("_clientAddress").stringValue = "localhost";
            });

            nmGo.AddComponent<TimeManager>();

            ObserverManager om = nmGo.AddComponent<ObserverManager>();
            ObserverCondition sceneCondition = AssetDatabase.LoadAssetAtPath<SceneCondition>(SceneConditionPath);
            if (sceneCondition == null)
                throw new System.Exception($"SceneCondition asset missing at {SceneConditionPath}");
            SetFields(om, so =>
            {
                SerializedProperty list = so.FindProperty("_defaultConditions");
                list.ClearArray();
                list.InsertArrayElementAtIndex(0);
                list.GetArrayElementAtIndex(0).objectReferenceValue = sceneCondition;
            });

            nmGo.AddComponent<Bootstrap>();
            MatchManager mm = nmGo.AddComponent<MatchManager>();
            SetFields(mm, so => so.FindProperty("_playerPrefab").objectReferenceValue = player);
            nmGo.AddComponent<DebugHud>();

            // Assign last: NetworkManager's editor Reset/OnValidate auto-assigns DefaultPrefabObjects
            // while other components are being added.
            nm.SpawnablePrefabs = prefabs;
            EditorUtility.SetDirty(nm);

            EditorSceneManager.SaveScene(scene, BootScenePath);
        }

        private static void Block(string name, Vector2 center, Vector2 size, Color color, int sortingOrder, bool collider = true)
        {
            GameObject go = new(name);
            go.transform.position = center;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.color = color;
            sr.sortingOrder = sortingOrder;
            go.AddComponent<SolidBlock>();
            if (collider)
                go.AddComponent<BoxCollider2D>().size = Vector2.one;
        }

        private static void SpawnPoint(int team, Vector2 position)
        {
            GameObject go = new(team == 0 ? "Spawn Blue" : "Spawn Red");
            go.transform.position = position;
            go.AddComponent<SpawnPoint>().Team = team;
        }

        private static void SetFields(Object target, System.Action<SerializedObject> edit)
        {
            SerializedObject so = new(target);
            edit(so);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }
    }
}
