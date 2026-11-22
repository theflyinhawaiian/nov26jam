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
    /// Generates the scenes and prefabs from code so they're reproducible and reviewable.
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
        private const string MatchStatePrefabPath = PrefabsDir + "/MatchState.prefab";
        private const string WeaponsDir = DataDir + "/Weapons";
        private const string GamePrefabsPath = DataDir + "/GamePrefabs.asset";
        private const string SceneConditionPath = "Assets/FishNet/Runtime/Observing/Conditions/ScriptableObjects/SceneCondition.asset";

        [MenuItem("nov26jam/Rebuild Scenes and Prefabs")]
        public static void RebuildAll()
        {
            SetupLayers();
            EnsureFolder(ScenesDir);
            EnsureFolder(PrefabsDir);
            EnsureFolder(DataDir);

            EnsureFolder(WeaponsDir);
            WeaponDefinition[] weapons = { EnsureRifle(), EnsureGrenadeLauncher() };
            NetworkObject player = BuildPlayerPrefab(weapons);
            NetworkObject matchState = BuildMatchStatePrefab();
            SinglePrefabObjects prefabs = BuildPrefabCollection(player, matchState);
            ArenaBuilder.Build(ArenaScenePath);
            BuildBootScene(player, matchState, prefabs);

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(BootScenePath, true),
                new EditorBuildSettingsScene(ArenaScenePath, true),
            };
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, ScriptingImplementation.Mono2x);

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Scenes/SampleScene.unity") != null)
                AssetDatabase.DeleteAsset("Assets/Scenes");

            AssetDatabase.SaveAssets();
            Debug.Log("[ProjectBuilder] Rebuilt scenes and prefabs.");
        }

        [MenuItem("nov26jam/Rebuild Arena Only")]
        public static void RebuildArena()
        {
            SetupLayers();
            ArenaBuilder.Build(ArenaScenePath);
        }

        private static void SetupLayers()
        {
            SerializedObject tagManager = new(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");
            layers.GetArrayElementAtIndex(Layers.Ground).stringValue = "Ground";
            layers.GetArrayElementAtIndex(Layers.OneWay).stringValue = "OneWay";
            layers.GetArrayElementAtIndex(Layers.JumpPad).stringValue = "JumpPad";
            tagManager.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Weapon assets are created once; after that they're hand-tuned in the editor.</summary>
        private static WeaponDefinition EnsureWeapon(string name, System.Action<WeaponDefinition> init)
        {
            string path = $"{WeaponsDir}/{name}.asset";
            WeaponDefinition w = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);
            if (w != null)
                return w;
            w = ScriptableObject.CreateInstance<WeaponDefinition>();
            init(w);
            AssetDatabase.CreateAsset(w, path);
            return w;
        }

        private static WeaponDefinition EnsureRifle() => EnsureWeapon("Rifle", w =>
        {
            w.DisplayName = "Rifle";
            w.Kind = WeaponKind.Hitscan;
            w.Damage = 10;
            w.Cooldown = 0.2f;
            w.Range = 24f;
        });

        private static WeaponDefinition EnsureGrenadeLauncher() => EnsureWeapon("GrenadeLauncher", w =>
        {
            w.DisplayName = "Grenades";
            w.Kind = WeaponKind.Projectile;
            w.Damage = 40;
            w.Cooldown = 1f;
            // Max range on flat ground = speed^2 / gravity ≈ 20 units.
            w.LaunchSpeed = 22.4f;
            w.ProjectileGravity = 25f;
            w.ExplosionRadius = 2.5f;
            w.MaxLifetime = 5f;
            w.ProjectileRadius = 0.2f;
        });

        private static NetworkObject BuildMatchStatePrefab()
        {
            GameObject root = new("MatchState");
            root.AddComponent<NetworkObject>();
            root.AddComponent<MatchState>();
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, MatchStatePrefabPath);
            Object.DestroyImmediate(root);
            return saved.GetComponent<NetworkObject>();
        }

        private static NetworkObject BuildPlayerPrefab(WeaponDefinition[] weapons)
        {
            GameObject root = new("Player");
            NetworkObject nob = root.AddComponent<NetworkObject>();

            NetworkTransform nt = root.AddComponent<NetworkTransform>();
            SetFields(nt, so =>
            {
                so.FindProperty("_clientAuthoritative").boolValue = true;
                so.FindProperty("_synchronizeRotation").boolValue = false;
                so.FindProperty("_synchronizeScale").boolValue = false;
                // Snap instead of sliding on respawn teleports and on bursts of late updates
                // (e.g. from a backgrounded browser tab). Normal movement is < 2 units per tick.
                so.FindProperty("_enableTeleport").boolValue = true;
                so.FindProperty("_teleportThreshold").floatValue = 4f;
            });

            // PlayerAvatar's [RequireComponent] adds PositionHistory, PlatformerMotor and PlayerWeapons.
            // Adding any of them separately would create duplicates.
            PlayerAvatar avatar = root.AddComponent<PlayerAvatar>();
            PlayerWeapons playerWeapons = root.GetComponent<PlayerWeapons>();
            SetFields(playerWeapons, so =>
            {
                SerializedProperty list = so.FindProperty("_weapons");
                list.arraySize = weapons.Length;
                for (int i = 0; i < weapons.Length; i++)
                    list.GetArrayElementAtIndex(i).objectReferenceValue = weapons[i];
            });

            GameObject body = new("Body");
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(1f, 1.5f, 1f);
            SpriteRenderer sr = body.AddComponent<SpriteRenderer>();
            sr.sprite = GreyboxSprite.Get();
            sr.sortingOrder = 5;
            SetFields(avatar, so => so.FindProperty("_body").objectReferenceValue = sr);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
            Object.DestroyImmediate(root);
            return saved.GetComponent<NetworkObject>();
        }

        private static SinglePrefabObjects BuildPrefabCollection(params NetworkObject[] networkPrefabs)
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
                list.arraySize = networkPrefabs.Length;
                for (int i = 0; i < networkPrefabs.Length; i++)
                    list.GetArrayElementAtIndex(i).objectReferenceValue = networkPrefabs[i];
            });
            EditorUtility.SetDirty(collection);
            return collection;
        }

        private static void BuildBootScene(NetworkObject player, NetworkObject matchState, PrefabObjects prefabs)
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
            SetFields(mm, so =>
            {
                so.FindProperty("_playerPrefab").objectReferenceValue = player;
                so.FindProperty("_matchStatePrefab").objectReferenceValue = matchState;
            });
            nmGo.AddComponent<DebugHud>();
            nmGo.AddComponent<GameMenu>();

            // Assign last: NetworkManager's editor Reset/OnValidate auto-assigns DefaultPrefabObjects
            // while other components are being added.
            nm.SpawnablePrefabs = prefabs;
            EditorUtility.SetDirty(nm);

            EditorSceneManager.SaveScene(scene, BootScenePath);
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
