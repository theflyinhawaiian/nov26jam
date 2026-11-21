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
        public const float NexusX = 60f;
        public const float SpawnX = 56f;

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

        public static void Build(string scenePath)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            float halfW = ArenaLayout.HalfWidth;
            float band = ArenaLayout.BandHeight;

            Transform bg = Group("Background");
            for (int i = 0; i < 5; i++)
                Block(bg, i % 2 == 0 ? "Lane" : "Jungle", new Rect(-halfW, i * band, halfW * 2f, band), i % 2 == 0 ? LaneBg : JungleBg, -10, layer: -1);

            Transform shell = Group("Shell");
            Block(shell, "Bottom Floor", new Rect(-halfW - 2f, -2f, halfW * 2f + 4f, 2f), GroundColor, 0, Layers.Ground);
            Block(shell, "Ceiling", new Rect(-halfW - 2f, ArenaLayout.Height, halfW * 2f + 4f, 2f), GroundColor, 0, Layers.Ground);
            Block(shell, "Wall L", new Rect(-halfW - 2f, -2f, 2f, ArenaLayout.Height + 4f), GroundColor, 0, Layers.Ground);
            Block(shell, "Wall R", new Rect(halfW, -2f, 2f, ArenaLayout.Height + 4f), GroundColor, 0, Layers.Ground);

            FloorWithHatches(Group("Mid Floor"), ArenaLayout.MidFloorY, Bottom.HatchXs);
            FloorWithHatches(Group("Top Floor"), ArenaLayout.TopFloorY, Mid.HatchXs);

            BuildSection(Group("Bottom Section"), Bottom);
            BuildSection(Group("Mid Section"), Mid);

            // Top lane cover.
            Transform top = Group("Top Section");
            foreach (float s in new[] { -1f, 1f })
                Block(top, "Lane Cover", CenterBottom(s * 26f, ArenaLayout.TopFloorY, 1f, 2.5f), CoverColor, 1, Layers.Ground);

            // Nexus + spawns on the mid lane. Team 0 = Blue (left), team 1 = Red (right).
            Transform bases = Group("Bases");
            for (int team = 0; team < 2; team++)
            {
                float s = team == 0 ? -1f : 1f;
                GameObject nexus = Block(bases, team == 0 ? "Nexus Blue" : "Nexus Red", CenterBottom(s * NexusX, ArenaLayout.MidFloorY, NexusWidth, NexusHeight), TeamColors[team], 2, Layers.Ground);
                nexus.AddComponent<Nexus>().Team = team;
                GameObject spawn = new(team == 0 ? "Spawn Blue" : "Spawn Red");
                spawn.transform.SetParent(bases, false);
                spawn.transform.position = new Vector3(s * SpawnX, ArenaLayout.MidFloorY + 0.8f, 0f);
                spawn.AddComponent<SpawnPoint>().Team = team;
            }

            EditorSceneManager.SaveScene(scene, scenePath);
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
        private static void FloorWithHatches(Transform parent, float topY, float[] hatchXs)
        {
            List<(float min, float max)> holes = new();
            foreach (float x in hatchXs)
            {
                holes.Add((x - HatchWidth / 2f, x + HatchWidth / 2f));
                holes.Add((-x - HatchWidth / 2f, -x + HatchWidth / 2f));
            }
            holes.Sort((a, b) => a.min.CompareTo(b.min));

            float cursor = -ArenaLayout.HalfWidth;
            foreach ((float min, float max) in holes)
            {
                if (min > cursor)
                    Block(parent, "Floor", new Rect(cursor, topY - FloorThickness, min - cursor, FloorThickness), GroundColor, 0, Layers.Ground);
                OneWay(parent, (min + max) / 2f, topY, max - min, "Hatch");
                cursor = max;
            }
            if (cursor < ArenaLayout.HalfWidth)
                Block(parent, "Floor", new Rect(cursor, topY - FloorThickness, ArenaLayout.HalfWidth - cursor, FloorThickness), GroundColor, 0, Layers.Ground);
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
