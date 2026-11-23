using System.Collections.Generic;
using Jam.Gameplay;
using Jam.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam.UI
{
    /// <summary>
    /// Bottom-right minimap (IMGUI, drawn by MatchHud). The terrain, nexuses and towers are always
    /// shown; units only when your team can see them:
    /// - your own screen (the camera's view),
    /// - your standing towers (their attack range),
    /// - your minions (their sight range).
    /// Your team's units are always shown. Vision is circles/rects only (no line of sight).
    /// </summary>
    public class Minimap
    {
        private const float Width = 300f;
        private const float Margin = 16f;
        /// <summary>Clear of the "Development Build" watermark in dev builds.</summary>
        private const float BottomMargin = 30f;

        private static readonly Color Background = new(0.02f, 0.02f, 0.04f, 0.85f);
        private static readonly Color Border = new(1f, 1f, 1f, 0.25f);
        private static readonly Color VisionTint = new(1f, 1f, 1f, 0.09f);

        private readonly struct Block
        {
            public readonly Rect Rect;
            public readonly Color Color;
            public Block(Rect rect, Color color) { Rect = rect; Color = color; }
        }

        private readonly List<Block> _terrain = new();
        private readonly List<(Vector2 center, float radius)> _vision = new();
        private Scene _terrainScene;
        private Texture2D _disc;

        // Per-draw mapping from world to minimap (group-local) coordinates.
        private Rect _world;
        private float _k;

        public void Draw(Vector2 screen, MatchState state, PlayerAvatar me)
        {
            Camera cam = Camera.main;
            ArenaDef arena = state.Arena;
            if (cam == null || arena == null || Event.current.type != EventType.Repaint)
                return;
            _world = arena.Bounds;
            _k = Width / _world.width;
            float height = _world.height * _k;
            Rect map = new(screen.x - Width - Margin, screen.y - height - BottomMargin, Width, height);
            // The arena scene this client has loaded (MatchState itself may live in another scene).
            CacheTerrain(Arenas.LoadedScene());

            Fill(new Rect(map.x - 1f, map.y - 1f, map.width + 2f, map.height + 2f), Border);
            Fill(map, Background);
            GUI.BeginGroup(map);

            // What our team can see.
            int team = me.Team.Value;
            Rect view = CameraRect(cam);
            _vision.Clear();
            for (int i = 0; i < state.TowerHealth.Count; i++)
                if (LaneLayout.TowerTeam(i) == team && state.TowerHealth[i] > 0)
                    _vision.Add((arena.TowerCenter(team, LaneLayout.TowerLane(i)), LaneDirector.TowerRange));
            foreach (Minion m in Minion.Active)
                if (m != null && m.Team.Value == team && m.Health.Value > 0)
                    _vision.Add((m.transform.position, Minion.SightRange));

            Fill(ToMap(view), VisionTint);
            foreach ((Vector2 c, float r) in _vision)
                Disc(c, r, VisionTint);

            foreach (Block b in _terrain)
                Fill(ToMap(b.Rect), b.Color);

            // Towers: always known; dim once destroyed.
            for (int i = 0; i < state.TowerHealth.Count; i++)
            {
                int hp = state.TowerHealth[i];
                if (!MatchState.TowerInPlay(hp))
                    continue;
                int t = LaneLayout.TowerTeam(i);
                Rect r = ToMap(arena.TowerRect(t, LaneLayout.TowerLane(i)));
                Fill(Grow(r, 1.5f), hp > 0 ? PlayerAvatar.TeamColor(t) : new Color(1f, 1f, 1f, 0.15f));
            }

            foreach (Minion m in Minion.Active)
            {
                if (m == null || m.Health.Value <= 0)
                    continue;
                Vector2 p = m.transform.position;
                if (m.Team.Value != team && !Visible(p, view))
                    continue;
                Dot(p, 3f, Color.Lerp(PlayerAvatar.TeamColor(m.Team.Value), Color.white, 0.25f));
            }

            foreach (PlayerAvatar a in new[] { PlayerAvatar.RemotePlayer, PlayerAvatar.LocalPlayer })
            {
                if (a == null || !a.IsSpawned || a.IsDead.Value)
                    continue;
                Vector2 p = a.transform.position;
                if (a.Team.Value != team && !Visible(p, view))
                    continue;
                if (a == me)
                    Dot(p, 9f, Color.white);
                Dot(p, 7f, PlayerAvatar.TeamColor(a.Team.Value));
            }

            Outline(ToMap(view), new Color(1f, 1f, 1f, 0.5f));
            GUI.EndGroup();
        }

        private bool Visible(Vector2 p, Rect view)
        {
            if (view.Contains(p))
                return true;
            foreach ((Vector2 c, float r) in _vision)
                if ((p - c).sqrMagnitude <= r * r)
                    return true;
            return false;
        }

        private static Rect CameraRect(Camera cam)
        {
            float h = cam.orthographicSize;
            float w = h * cam.aspect;
            Vector2 c = cam.transform.position;
            return new Rect(c.x - w, c.y - h, w * 2f, h * 2f);
        }

        /// <summary>Solid terrain, one-way platforms, jump pads and nexuses, from the arena scene's sprites.</summary>
        private void CacheTerrain(Scene scene)
        {
            if (!scene.IsValid() || scene == _terrainScene)
                return;
            _terrainScene = scene;
            _terrain.Clear();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (SpriteRenderer sr in root.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    int layer = sr.gameObject.layer;
                    if (layer != Layers.Ground && layer != Layers.OneWay && layer != Layers.JumpPad)
                        continue;
                    Bounds b = sr.bounds;
                    Rect r = Rect.MinMaxRect(
                        Mathf.Max(b.min.x, _world.xMin), Mathf.Max(b.min.y, _world.yMin),
                        Mathf.Min(b.max.x, _world.xMax), Mathf.Min(b.max.y, _world.yMax));
                    if (r.width <= 0f || r.height <= 0f)
                        continue; // the shell (outer walls, floor, ceiling) is outside the bounds
                    Color c = sr.color;
                    c.a = 1f;
                    _terrain.Add(new Block(r, c));
                }
            }
        }

        private Vector2 ToMap(Vector2 w) => new((w.x - _world.xMin) * _k, (_world.yMax - w.y) * _k);

        private Rect ToMap(Rect w)
        {
            Vector2 a = ToMap(new Vector2(w.xMin, w.yMax));
            // At least a pixel thick, so thin platforms still show.
            return new Rect(a.x, a.y, Mathf.Max(1f, w.width * _k), Mathf.Max(1f, w.height * _k));
        }

        private static Rect Grow(Rect r, float by) => new(r.x - by, r.y - by, r.width + by * 2f, r.height + by * 2f);

        private void Dot(Vector2 world, float size, Color color)
        {
            Vector2 p = ToMap(world);
            Fill(new Rect(p.x - size * 0.5f, p.y - size * 0.5f, size, size), color);
        }

        private void Disc(Vector2 world, float radius, Color color)
        {
            Vector2 p = ToMap(world);
            float r = radius * _k;
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(p.x - r, p.y - r, r * 2f, r * 2f), DiscTexture);
            GUI.color = old;
        }

        private static void Outline(Rect r, Color c)
        {
            Fill(new Rect(r.x, r.y, r.width, 1f), c);
            Fill(new Rect(r.x, r.yMax - 1f, r.width, 1f), c);
            Fill(new Rect(r.x, r.y, 1f, r.height), c);
            Fill(new Rect(r.xMax - 1f, r.y, 1f, r.height), c);
        }

        private static void Fill(Rect r, Color color)
        {
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>A white disc with a soft edge, generated once.</summary>
        private Texture2D DiscTexture
        {
            get
            {
                if (_disc != null)
                    return _disc;
                const int n = 64;
                _disc = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float d = new Vector2(x + 0.5f - n * 0.5f, y + 0.5f - n * 0.5f).magnitude;
                        px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(n * 0.5f - d) * 255f));
                    }
                _disc.SetPixels32(px);
                _disc.Apply();
                return _disc;
            }
        }
    }
}
