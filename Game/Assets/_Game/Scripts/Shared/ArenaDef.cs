using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam
{
    /// <summary>
    /// Where things are in one arena scene: size, the base floor (nexuses, player spawns), which
    /// lanes exist and their floors, towers, and minion routes. Team 0 (Blue) is on the left,
    /// team 1 (Red) on the right; x values are distances from the centre (mirrored).
    /// Runtime code finds the arena it's in with <see cref="Arenas.For(Scene)"/>.
    /// </summary>
    public sealed class ArenaDef
    {
        public string Scene;
        public float HalfWidth = ArenaLayout.HalfWidth;
        public float Height;
        /// <summary>Floor the nexuses, player spawns and minion hold points stand on.</summary>
        public float BaseFloorY;
        /// <summary>Indexed by Lane.</summary>
        public bool[] HasLane;
        public float[] LaneFloorY;
        /// <summary>|x| of each team's tower per lane.</summary>
        public float[] TowerX;
        /// <summary>|x| where minions spawn per lane.</summary>
        public float[] MinionSpawnX;
        /// <summary>Top lane minions drop through the hatch here to the base floor; bottom lane minions ride the high pad here up to it.</summary>
        public float TopDropX;
        public float BottomPadX;
        public float HighPadApex;
        /// <summary>Minions hold here in front of the enemy nexus (nexus face is at 58.5).</summary>
        public float NexusHoldX = 57f;
        /// <summary>Flyers on the base floor stay this side of the nexus face so they never pass through it.</summary>
        public float FlyerMaxX = 57.5f;
        public float NexusX = 60f;
        public float PlayerSpawnX = 56f;
        /// <summary>Jump pads on the base floor (both sides), for bots that walk it.</summary>
        public float[] BasePadXs;

        public Rect Bounds => new(-HalfWidth, 0f, HalfWidth * 2f, Height);

        public float FloorY(Lane lane) => LaneFloorY[(int)lane];

        /// <summary>Centre of a tower (it stands on the lane floor).</summary>
        public Vector2 TowerCenter(int team, Lane lane) =>
            new(LaneLayout.Side(team) * TowerX[(int)lane], FloorY(lane) + LaneLayout.TowerHeight * 0.5f);

        public Rect TowerRect(int team, Lane lane)
        {
            Vector2 c = TowerCenter(team, lane);
            return new Rect(c.x - LaneLayout.TowerWidth * 0.5f, c.y - LaneLayout.TowerHeight * 0.5f, LaneLayout.TowerWidth, LaneLayout.TowerHeight);
        }

        public Vector2 TowerMuzzle(int team, Lane lane) => TowerCenter(team, lane) + Vector2.up * (LaneLayout.TowerHeight * 0.4f);

        public Vector2 MinionSpawn(int team, Lane lane) =>
            new(LaneLayout.Side(team) * MinionSpawnX[(int)lane], FloorY(lane) + 0.6f);
    }

    public static class Arenas
    {
        /// <summary>
        /// The original 128x90 arena: three lanes (bottom, mid, top) with jungles between; the
        /// nexuses sit on the mid lane.
        /// </summary>
        public static readonly ArenaDef Classic = new()
        {
            Scene = "Arena",
            Height = ArenaLayout.Height,
            BaseFloorY = ArenaLayout.MidFloorY,
            HasLane = new[] { true, true, true },
            LaneFloorY = new[] { ArenaLayout.BottomFloorY, ArenaLayout.MidFloorY, ArenaLayout.TopFloorY },
            TowerX = new[] { 34f, 32f, 32f },
            MinionSpawnX = new[] { 60f, 54f, 60f },
            TopDropX = 38f,
            BottomPadX = 44f,
            HighPadApex = 39f,
            BasePadXs = new[] { -50f, -38f, -14f, -2f, 2f, 14f, 38f, 50f },
        };

        /// <summary>
        /// 128x54: a lower lane (y 0-18), the jungle (y 18-36, where the nexuses and player
        /// spawns are) and an upper lane (y 36-54). No mid lane.
        /// </summary>
        public static readonly ArenaDef TwoLane = new()
        {
            Scene = "TwoLane",
            Height = ArenaLayout.ScreenHeight * 3f,
            BaseFloorY = ArenaLayout.ScreenHeight,
            HasLane = new[] { true, false, true },
            LaneFloorY = new[] { 0f, ArenaLayout.ScreenHeight, ArenaLayout.ScreenHeight * 2f },
            TowerX = new[] { 34f, 32f, 32f },
            MinionSpawnX = new[] { 60f, 54f, 60f },
            TopDropX = 38f,
            BottomPadX = 44f,
            HighPadApex = 21f,
            BasePadXs = new[] { -38f, -14f, 14f, 38f },
        };

        public static readonly ArenaDef[] All = { Classic, TwoLane };

        private static readonly Dictionary<Scene, ArenaDef> ByScene = new();

        public static ArenaDef ByName(string name)
        {
            foreach (ArenaDef a in All)
                if (string.Equals(a.Scene, name, System.StringComparison.OrdinalIgnoreCase))
                    return a;
            return null;
        }

        public static bool IsArena(Scene scene) => scene.IsValid() && For(scene) != null;

        /// <summary>The arena a scene is, or null if it isn't one. Cached per scene.</summary>
        public static ArenaDef For(Scene scene)
        {
            if (!scene.IsValid())
                return null;
            if (!ByScene.TryGetValue(scene, out ArenaDef a))
                ByScene[scene] = a = ByName(scene.name);
            return a;
        }

        /// <summary>
        /// The arena an object is in: its own scene if that's an arena, else the arena loaded on
        /// this peer (a client only ever has one), else Classic.
        /// </summary>
        public static ArenaDef For(GameObject go) => For(go.scene) ?? For(LoadedScene()) ?? Classic;

        /// <summary>The first loaded arena scene on this peer (default Scene if none).</summary>
        public static Scene LoadedScene()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && For(s) != null)
                    return s;
            }
            return default;
        }
    }
}
