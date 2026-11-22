using System.Collections.Generic;
using Jam.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam.EditorTools
{
    /// <summary>
    /// Generates the 128x90 greybox arena. Everything is mirrored around x = 0
    /// (s = -1 is Blue / team 0, s = +1 is Red / team 1).
    ///
    ///   y=90  ── ceiling ──────────────────────────────────────────────
    ///         top lane (flank)                                   ↑ high pads
    ///   y=72  ══ top floor: solid + one-way hatches at ±14, ±38 ═══════
    ///         jungle: one-way ledges at y+20/24/28/32, cover blocks
    ///         mid lane: nexus at ±60, spawns at ±56
    ///   y=36  ══ mid floor: solid + one-way hatches at ±20, ±44 ═══════
    ///         jungle (same pattern)
    ///         bottom lane (flank)
    ///   y=0   ══ bottom floor ═════════════════════════════════════════
    ///
    /// Movement between levels: high pads under each hatch launch you lane-to-lane;
    /// low pads launch you into the jungle ledges; ledges step up 4 units at a time
    /// (jump height is 5) and the top ledge sits under a hatch. Down+Jump drops through
    /// any one-way surface.
    /// </summary>
    public static class ArenaBuilder
    {
        private const float FloorThickness = 1f;
        private const float OneWayThickness = 0.4f;
        private const float HatchWidth = 6f;
        private const float LedgeWidth = 8f;
        private const float PadWidth = 2f;
        private const float NexusWidth = 3f;
        private const float NexusHeight = 6f;

        // Apex heights: low pads land you on the first ledge (y+20), high pads clear the next floor (y+36).
        private const float LowPadApex = 21.5f;
        private const float HighPadApex = 39f;

        private static readonly Color LaneBg = new(0.11f, 0.12f, 0.15f);
        private static readonly Color JungleBg = new(0.09f, 0.13f, 0.1f);
        private static readonly Color GroundColor = new(0.42f, 0.42f, 0.47f);
        private static readonly Color OneWayColor = new(0.62f, 0.6f, 0.5f);
        private static readonly Color CoverColor = new(0.33f, 0.36f, 0.33f);
        private static readonly Color LowPadColor = new(0.3f, 0.85f, 0.4f);
        private static readonly Color HighPadColor = new(0.95f, 0.8f, 0.2f);
        private static readonly Color[] TeamColors = { new(0.3f, 0.55f, 1f), new(1f, 0.35f, 0.3f) };

        private struct Section
        {
            public float FloorY;          // lane floor this section sits on
            public float[] HatchXs;       // hatches in the floor above (positive side; mirrored)
            public float[] OddLedgeXs;    // ledges at y+20 and y+28
            public float[] LowPadXs;      // lane → jungle
            public (float x, float y, float w, float h)[] Cover; // solid cover, y relative to floor
        }

        private static readonly Section Bottom = new()
        {
            FloorY = ArenaLayout.BottomFloorY,
            HatchXs = new[] { 20f, 44f },
            OddLedgeXs = new[] { 8f, 32f, 56f },
            LowPadXs = new[] { 8f, 56f },
            Cover = new[] { (30f, 0f, 1f, 2.5f), (14f, 22f, 2f, 3f), (38f, 30f, 2f, 3f) },
        };

        private static readonly Section Mid = new()
        {
            FloorY = ArenaLayout.MidFloorY,
            HatchXs = new[] { 14f, 38f },
            OddLedgeXs = new[] { 2f, 26f, 50f },
            LowPadXs = new[] { 2f, 50f },
            Cover = new[] { (8f, 22f, 2f, 3f), (32f, 30f, 2f, 3f) },
        };

        /// <summary>The classic three-lane arena (Arenas.Classic, Arena.unity).</summary>
        public static void Build(string scenePath)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            ArenaDef def = Arenas.Classic;
            float halfW = ArenaLayout.HalfWidth;
            float band = ArenaLayout.BandHeight;

            Transform bg = Group("Background");
            for (int i = 0; i < 5; i++)
                Block(bg, i % 2 == 0 ? "Lane" : "Jungle", new Rect(-halfW, i * band, halfW * 2f, band), i % 2 == 0 ? LaneBg : JungleBg, -10, layer: -1);

            Shell(def);
            FloorWithHatches(Group("Mid Floor"), ArenaLayout.MidFloorY, Bottom.HatchXs);
            FloorWithHatches(Group("Top Floor"), ArenaLayout.TopFloorY, Mid.HatchXs);

            BuildSection(Group("Bottom Section"), Bottom);
            BuildSection(Group("Mid Section"), Mid);

            // Top lane cover.
            Transform top = Group("Top Section");
            foreach (float s in new[] { -1f, 1f })
                Block(top, "Lane Cover", CenterBottom(s * 26f, ArenaLayout.TopFloorY, 1f, 2.5f), CoverColor, 1, Layers.Ground);

            Bases(def);
            Towers(def);
            EditorSceneManager.SaveScene(scene, scenePath);
        }

        /// <summary>
        /// The two-lane arena (Arenas.TwoLane, TwoLane.unity), 128x54:
        ///
        ///   y=54  -- ceiling ----------------------------------------------
        ///         upper lane: towers at +-32, cover at +-26
        ///   y=36  == upper floor: solid + one-way hatches at +-14, +-38 ====
        ///         jungle: nexus at +-60, player spawns at +-56; high pads under
        ///         the upper hatches; one-way ledges stepping up to them
        ///   y=18  == jungle floor: solid + one-way hatches at +-20, +-44 ===
        ///         lower lane: towers at +-34, cover at +-30, high pads under
        ///         the jungle hatches
        ///   y=0   == bottom floor =========================================
        ///
        /// Lower lane minions ride the pad at +-44 up into the jungle; upper lane minions drop
        /// through the hatch at +-38. Both then walk to the enemy nexus.
        /// </summary>
        public static void BuildTwoLane(string scenePath)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            ArenaDef def = Arenas.TwoLane;
            float halfW = def.HalfWidth;
            float band = ArenaLayout.BandHeight;
            float lower = def.FloorY(Lane.Bottom), jungle = def.BaseFloorY, upper = def.FloorY(Lane.Top);
            float padApex = def.HighPadApex;

            Transform bg = Group("Background");
            Block(bg, "Lower Lane", new Rect(-halfW, lower, halfW * 2f, band), LaneBg, -10, layer: -1);
            Block(bg, "Jungle", new Rect(-halfW, jungle, halfW * 2f, band), JungleBg, -10, layer: -1);
            Block(bg, "Upper Lane", new Rect(-halfW, upper, halfW * 2f, band), LaneBg, -10, layer: -1);

            Shell(def);
            float[] jungleHatches = { 20f, 44f };
            float[] upperHatches = { 14f, 38f };
            FloorWithHatches(Group("Jungle Floor"), jungle, jungleHatches);
            FloorWithHatches(Group("Upper Floor"), upper, upperHatches);

            Transform lowerLane = Group("Lower Lane");
            Transform jungleSec = Group("Jungle");
            Transform upperLane = Group("Upper Lane");
            foreach (float s in new[] { -1f, 1f })
            {
                foreach (float x in jungleHatches)
                    Pad(lowerLane, s * x, lower, padApex, HighPadColor);
                Block(lowerLane, "Lane Cover", CenterBottom(s * 30f, lower, 1f, 2.5f), CoverColor, 1, Layers.Ground);

                foreach (float x in upperHatches)
                    Pad(jungleSec, s * x, jungle, padApex, HighPadColor);
                // Two staircases of one-way ledges (4.5 apart; jump height is 5), each ending
                // under an upper-floor hatch.
                foreach ((float x, float y) in new[] { (26f, 4.5f), (20f, 9f), (14f, 13.5f), (50f, 4.5f), (44f, 9f), (38f, 13.5f) })
                    OneWay(jungleSec, s * x, jungle + y, LedgeWidth);

                Block(upperLane, "Lane Cover", CenterBottom(s * 26f, upper, 1f, 2.5f), CoverColor, 1, Layers.Ground);
            }
            // A raised platform with cover in the middle of the jungle.
            OneWay(jungleSec, 0f, jungle + 5f, 10f);
            Block(jungleSec, "Cover", CenterBottom(0f, jungle + 5f, 2f, 2f), CoverColor, 1, Layers.Ground);

            Bases(def);
            Towers(def);
            EditorSceneManager.SaveScene(scene, scenePath);
        }

        /// <summary>Bottom floor, ceiling and side walls.</summary>
        private static void Shell(ArenaDef def)
        {
            Transform shell = Group("Shell");
            float halfW = def.HalfWidth;
            Block(shell, "Bottom Floor", new Rect(-halfW - 2f, -2f, halfW * 2f + 4f, 2f), GroundColor, 0, Layers.Ground);
            Block(shell, "Ceiling", new Rect(-halfW - 2f, def.Height, halfW * 2f + 4f, 2f), GroundColor, 0, Layers.Ground);
            Block(shell, "Wall L", new Rect(-halfW - 2f, -2f, 2f, def.Height + 4f), GroundColor, 0, Layers.Ground);
            Block(shell, "Wall R", new Rect(halfW, -2f, 2f, def.Height + 4f), GroundColor, 0, Layers.Ground);
        }

        /// <summary>Nexus + player spawn per team on the base floor. Team 0 = Blue (left), team 1 = Red (right).</summary>
        private static void Bases(ArenaDef def)
        {
            Transform bases = Group("Bases");
            for (int team = 0; team < 2; team++)
            {
                float s = LaneLayout.Side(team);
                GameObject nexus = Block(bases, team == 0 ? "Nexus Blue" : "Nexus Red", CenterBottom(s * def.NexusX, def.BaseFloorY, NexusWidth, NexusHeight), TeamColors[team], 2, Layers.Ground);
                nexus.AddComponent<Nexus>().Team = team;
                GameObject spawn = new(team == 0 ? "Spawn Blue" : "Spawn Red");
                spawn.transform.SetParent(bases, false);
                spawn.transform.position = new Vector3(s * def.PlayerSpawnX, def.BaseFloorY + 0.8f, 0f);
                spawn.AddComponent<SpawnPoint>().Team = team;
            }
        }

        /// <summary>
        /// One tower per lane per team, for the lanes the arena has. Visual only: no collider,
        /// so friendly players and minions walk through; enemy minions stop to attack it.
        /// </summary>
        private static void Towers(ArenaDef def)
        {
            Transform towers = Group("Towers");
            for (int team = 0; team < 2; team++)
            {
                for (int lane = 0; lane < LaneLayout.LaneCount; lane++)
                {
                    if (!def.HasLane[lane])
                        continue;
                    Rect r = def.TowerRect(team, (Lane)lane);
                    Color c = Color.Lerp(TeamColors[team], new Color(0.2f, 0.2f, 0.22f), 0.35f);
                    GameObject go = Block(towers, $"Tower {(team == 0 ? "Blue" : "Red")} {(Lane)lane}", r, c, 2, layer: -1);
                    Tower tower = go.AddComponent<Tower>();
                    tower.Team = team;
                    tower.Lane = (Lane)lane;
                }
            }
        }

        private static void BuildSection(Transform parent, Section sec)
        {
            float y0 = sec.FloorY;
            foreach (float s in new[] { -1f, 1f })
            {
                foreach (float x in sec.OddLedgeXs)
                {
                    OneWay(parent, s * x, y0 + 20f, LedgeWidth);
                    OneWay(parent, s * x, y0 + 28f, LedgeWidth);
                }
                foreach (float x in sec.HatchXs)
                {
                    OneWay(parent, s * x, y0 + 24f, LedgeWidth);
                    OneWay(parent, s * x, y0 + 32f, LedgeWidth);
                    Pad(parent, s * x, y0, HighPadApex, HighPadColor);
                }
                foreach (float x in sec.LowPadXs)
                    Pad(parent, s * x, y0, LowPadApex, LowPadColor);
                foreach ((float x, float y, float w, float h) in sec.Cover)
                    Block(parent, "Cover", CenterBottom(s * x, y0 + y, w, h), CoverColor, 1, Layers.Ground);
            }
        }

        /// <summary>A floor whose top surface is at <paramref name="topY"/>, solid except one-way hatches at ±hatchXs.</summary>
        private static void FloorWithHatches(Transform parent, float topY, float[] hatchXs, float halfWidth = ArenaLayout.HalfWidth)
        {
            List<(float min, float max)> holes = new();
            foreach (float x in hatchXs)
            {
                holes.Add((x - HatchWidth / 2f, x + HatchWidth / 2f));
                holes.Add((-x - HatchWidth / 2f, -x + HatchWidth / 2f));
            }
            holes.Sort((a, b) => a.min.CompareTo(b.min));

            float cursor = -halfWidth;
            foreach ((float min, float max) in holes)
            {
                if (min > cursor)
                    Block(parent, "Floor", new Rect(cursor, topY - FloorThickness, min - cursor, FloorThickness), GroundColor, 0, Layers.Ground);
                OneWay(parent, (min + max) / 2f, topY, max - min, "Hatch");
                cursor = max;
            }
            if (cursor < halfWidth)
                Block(parent, "Floor", new Rect(cursor, topY - FloorThickness, halfWidth - cursor, FloorThickness), GroundColor, 0, Layers.Ground);
        }

        private static void OneWay(Transform parent, float centerX, float topY, float width, string name = "Ledge")
        {
            Block(parent, name, new Rect(centerX - width / 2f, topY - OneWayThickness, width, OneWayThickness), OneWayColor, 1, Layers.OneWay);
        }

        private static void Pad(Transform parent, float centerX, float floorY, float apex, Color color)
        {
            GameObject go = Block(parent, apex > LowPadApex ? "High Pad" : "Low Pad", CenterBottom(centerX, floorY, PadWidth, 0.5f), color, 2, Layers.JumpPad);
            go.GetComponent<BoxCollider2D>().isTrigger = true;
            go.AddComponent<JumpPad>().LaunchHeight = apex;
        }

        private static Rect CenterBottom(float centerX, float bottomY, float w, float h) => new(centerX - w / 2f, bottomY, w, h);

        private static Transform Group(string name) => new GameObject(name).transform;

        /// <summary>layer -1 = visual only (no collider).</summary>
        private static GameObject Block(Transform parent, string name, Rect rect, Color color, int sortingOrder, int layer)
        {
            GameObject go = new(name);
            go.transform.SetParent(parent, false);
            go.transform.position = rect.center;
            go.transform.localScale = new Vector3(rect.width, rect.height, 1f);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = GreyboxSprite.Get();
            sr.color = color;
            sr.sortingOrder = sortingOrder;
            go.AddComponent<SolidBlock>();
            if (layer >= 0)
            {
                go.layer = layer;
                go.AddComponent<BoxCollider2D>().size = Vector2.one;
                go.isStatic = true;
            }
            return go;
        }
    }
}
