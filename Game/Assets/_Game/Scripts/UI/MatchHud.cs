using Jam.Gameplay;
using Jam.Net;
using UnityEngine;

namespace Jam.UI
{
    /// <summary>
    /// Player-facing in-match HUD (IMGUI, greybox), drawn by GameMenu while in a match:
    /// - top: both nexus health bars (Blue left, Red right, like the arena), with player names,
    ///   a pip per standing tower, "SHIELDED" while towers stand, and the next-wave countdown
    /// - world: small health bars over damaged minions and towers
    /// - bottom-left: your health and money; a shop hint while in your spawn zone
    /// - bottom-centre: the gun and Q ability slots, cooldowns sweeping, STUNNED/SILENCED when blocked
    /// - world: STUNNED / SILENCED / SLOWED over affected players
    /// - centre: respawn countdown while dead
    /// - screen edge: an arrow pointing at the opponent when they're off-screen
    /// Coordinates are in a 720-high reference space, like the rest of the UI.
    /// </summary>
    public class MatchHud
    {
        private const float NexusBarWidth = 240f;
        private const float BarHeight = 18f;
        private const float ArrowMargin = 46f;
        /// <summary>Keep the arrow clear of the nexus bars (top) and health/weapons (bottom).</summary>
        private const float ArrowMarginTop = 80f;
        private const float ArrowMarginBottom = 100f;
        private const float ArrowSize = 40f;

        private static readonly Color Track = new(0f, 0f, 0f, 0.55f);
        private static readonly Color Frame = new(1f, 1f, 1f, 0.25f);

        private GUIStyle _barText, _name, _slot, _respawn, _respawnSmall, _arrowLabel, _waveLabel;
        private Texture2D _arrow;

        /// <param name="screen">Screen size in reference units.</param>
        /// <param name="scale">Reference units to pixels.</param>
        public void Draw(Vector2 screen, float scale, bool shopOpen)
        {
            MatchState state = MatchState.Local;
            PlayerAvatar me = PlayerAvatar.LocalPlayer;
            if (state == null || me == null || !me.IsSpawned)
                return;
            EnsureStyles();
            PlayerAvatar opp = PlayerAvatar.RemotePlayer;

            DrawUnitBars(scale);
            DrawNexusBars(screen, state, me, opp);
            DrawWaveTimer(screen, state);
            DrawHealth(screen, me);
            DrawWeapons(screen, me);
            DrawStatusLabels(scale);
            if (me.IsDead.Value)
                DrawRespawn(screen, me);
            else if (me.InSpawnZone && !shopOpen)
                Shadowed(new Rect(0f, screen.y - 150f, screen.x, 22f), "Safe zone  ·  B to open the shop", _waveLabel);
            if (opp != null && opp.IsSpawned && !opp.IsDead.Value)
                DrawOpponentArrow(opp, scale);
        }

        private void DrawNexusBars(Vector2 screen, MatchState state, PlayerAvatar me, PlayerAvatar opp)
        {
            float cx = screen.x * 0.5f;
            float y = 30f;
            for (int team = 0; team < 2; team++)
            {
                float x = team == 0 ? cx - 16f - NexusBarWidth : cx + 16f;
                Rect bar = new(x, y, NexusBarWidth, BarHeight);
                int hp = state.NexusHealth(team);
                float fill = (float)hp / MatchState.NexusMaxHealth;
                // Bars drain toward the middle of the screen.
                bool shielded = state.NexusShielded(team);
                Bar(bar, fill, shielded ? Color.Lerp(PlayerAvatar.TeamColor(team), Color.white, 0.45f) : PlayerAvatar.TeamColor(team), fromRight: team == 0);
                GUI.Label(bar, shielded ? $"{hp}  SHIELDED" : hp.ToString(), _barText);

                // A pip per lane tower in this arena (bottom, mid, top), dim once destroyed.
                const float pip = 12f, pipGap = 5f;
                for (int lane = 0, slot = 0; lane < LaneLayout.LaneCount; lane++)
                {
                    int i = LaneLayout.TowerIndex(team, (Lane)lane);
                    if (i >= state.TowerHealth.Count || !MatchState.TowerInPlay(state.TowerHealth[i]))
                        continue;
                    bool up = state.TowerHealth[i] > 0;
                    float px = team == 0 ? bar.x + slot * (pip + pipGap) : bar.xMax - (slot + 1) * (pip + pipGap) + pipGap;
                    slot++;
                    Fill(new Rect(px, bar.yMax + 5f, pip, pip), up ? PlayerAvatar.TeamColor(team) : new Color(1f, 1f, 1f, 0.15f));
                }

                PlayerAvatar owner = me.Team.Value == team ? me : opp;
                string who = owner == null ? "" : owner == me ? $"{owner.Nickname.Value} (you)" : owner.Nickname.Value;
                _name.alignment = team == 0 ? TextAnchor.LowerRight : TextAnchor.LowerLeft;
                Shadowed(new Rect(x, y - 24f, NexusBarWidth, 22f), $"{(team == 0 ? "BLUE" : "RED")} NEXUS  {who}".Trim(), _name);
            }
        }

        private void DrawWaveTimer(Vector2 screen, MatchState state)
        {
            if (state.NextWaveTick.Value == 0)
                return;
            Rect r = new(screen.x * 0.5f - 100f, 52f, 200f, 22f);
            Shadowed(r, $"Next wave {Mathf.CeilToInt(state.NextWaveIn)}s", _waveLabel);
        }

        /// <summary>Small world-space bars over damaged minions and towers, in pixel space.</summary>
        private void DrawUnitBars(float scale)
        {
            Camera cam = Camera.main;
            MatchState state = MatchState.Local;
            if (cam == null || state == null)
                return;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;

            foreach (Minion m in Minion.Active)
            {
                if (m == null || m.Health.Value >= m.MaxHp || m.Health.Value <= 0)
                    continue;
                WorldBar(cam, (Vector2)m.transform.position + Vector2.up * 0.8f, 30f * scale, 4f * scale,
                    (float)m.Health.Value / m.MaxHp, PlayerAvatar.TeamColor(m.Team.Value));
            }
            for (int i = 0; i < state.TowerHealth.Count; i++)
            {
                int hp = state.TowerHealth[i];
                if (hp <= 0 || hp >= MatchState.TowerMaxHealth)
                    continue;
                int team = LaneLayout.TowerTeam(i);
                Vector2 top = state.Arena.TowerCenter(team, LaneLayout.TowerLane(i)) + Vector2.up * (LaneLayout.TowerHeight * 0.5f + 0.6f);
                WorldBar(cam, top, 60f * scale, 6f * scale, (float)hp / MatchState.TowerMaxHealth, PlayerAvatar.TeamColor(team));
            }
            GUI.matrix = old;
        }

        private static void WorldBar(Camera cam, Vector2 world, float width, float height, float fill, Color color)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z < 0f || sp.x < -width || sp.x > Screen.width + width || sp.y < -height || sp.y > Screen.height + height)
                return;
            Rect r = new(sp.x - width * 0.5f, Screen.height - sp.y - height * 0.5f, width, height);
            Fill(r, new Color(0f, 0f, 0f, 0.6f));
            Fill(new Rect(r.x, r.y, r.width * Mathf.Clamp01(fill), r.height), color);
        }

        private void DrawHealth(Vector2 screen, PlayerAvatar me)
        {
            Rect bar = new(20f, screen.y - 44f, 260f, 24f);
            float fill = me.IsDead.Value ? 0f : (float)me.Health.Value / me.MaxHp;
            Color color = Color.Lerp(new Color(0.95f, 0.3f, 0.25f), new Color(0.35f, 0.85f, 0.4f), Mathf.InverseLerp(0.2f, 0.7f, fill));
            Bar(bar, fill, color, fromRight: false);
            GUI.Label(bar, me.IsDead.Value ? "DEAD" : $"{me.Health.Value} / {me.MaxHp}", _barText);
            _name.alignment = TextAnchor.LowerLeft;
            Shadowed(new Rect(bar.x, bar.y - 24f, bar.width, 22f), "HEALTH", _name);
            _name.alignment = TextAnchor.LowerRight;
            Shadowed(new Rect(bar.x, bar.y - 24f, bar.width, 22f), $"${me.Money.Value}", _name);
        }

        /// <summary>Two slots: the gun (left mouse) and the Q ability, each with a cooldown sweep.</summary>
        private void DrawWeapons(Vector2 screen, PlayerAvatar me)
        {
            const float w = 170f, h = 46f, gap = 10f;
            float x = (screen.x - (w * 2f + gap)) * 0.5f;
            float y = screen.y - h - 16f;
            PlayerWeapons weapons = me.Weapons;
            PlayerAbilities abilities = me.Abilities;

            WeaponDefinition gun = weapons.Get(0);
            Slot(new Rect(x, y, w, h), $"LMB  {gun.DisplayName}", weapons.CooldownRemaining(0), gun.Cooldown, me.Stunned ? "STUNNED" : null);
            if (abilities != null)
            {
                string blocked = me.Stunned ? "STUNNED" : me.Silenced ? "SILENCED" : null;
                Slot(new Rect(x + w + gap, y, w, h), $"Q  {abilities.CurrentDef.Name}", abilities.CooldownRemaining, abilities.CooldownTotal, blocked);
            }
        }

        private void Slot(Rect r, string label, float remaining, float total, string blocked)
        {
            Fill(r, Frame);
            Rect inner = Inset(r, 1f);
            Fill(inner, new Color(0.08f, 0.08f, 0.1f, 0.85f));
            // Cooldown: a dark sweep that empties left to right as it becomes ready.
            if (remaining > 0f && total > 0f)
            {
                float t = Mathf.Clamp01(remaining / total);
                Fill(new Rect(inner.xMax - inner.width * t, inner.y, inner.width * t, inner.height), new Color(0.45f, 0.45f, 0.5f, 0.6f));
            }
            Color old = GUI.contentColor;
            GUI.contentColor = blocked != null ? new Color(0.75f, 0.5f, 1f) : remaining > 0f ? new Color(0.7f, 0.7f, 0.75f) : Color.white;
            GUI.Label(inner, blocked ?? (remaining > 0.05f && total >= 2f ? $"{label}  {Mathf.CeilToInt(remaining)}" : label), _slot);
            GUI.contentColor = old;
        }

        /// <summary>STUNNED / SILENCED / SLOWED over any affected player, in pixel space.</summary>
        private void DrawStatusLabels(float scale)
        {
            Camera cam = Camera.main;
            if (cam == null)
                return;
            Matrix4x4 old = GUI.matrix;
            foreach (PlayerAvatar a in new[] { PlayerAvatar.LocalPlayer, PlayerAvatar.RemotePlayer })
            {
                if (a == null || !a.IsSpawned || a.IsDead.Value)
                    continue;
                string text = a.Stunned ? "STUNNED" : a.Silenced ? "SILENCED" : a.Slowed ? "SLOWED" : null;
                if (text == null)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(a.transform.position + Vector3.up * 1.5f);
                if (sp.z < 0f)
                    continue;
                GUI.matrix = Matrix4x4.TRS(new Vector3(sp.x, Screen.height - sp.y, 0f), Quaternion.identity, new Vector3(scale, scale, 1f));
                Shadowed(new Rect(-60f, -11f, 120f, 22f), text, _arrowLabel);
            }
            GUI.matrix = old;
        }

        private void DrawRespawn(Vector2 screen, PlayerAvatar me)
        {
            Rect r = new(0f, screen.y * 0.36f, screen.x, 60f);
            Shadowed(r, "YOU DIED", _respawn);
            r.y += 58f;
            Shadowed(r, $"Respawning in {Mathf.CeilToInt(me.RespawnSecondsRemaining)}", _respawnSmall);
        }

        /// <summary>Drawn in pixel space (GUI.matrix is replaced), so it takes the UI scale.</summary>
        private void DrawOpponentArrow(PlayerAvatar opp, float scale)
        {
            Camera cam = Camera.main;
            if (cam == null)
                return;
            Vector3 vp = cam.WorldToViewportPoint(opp.transform.position);
            const float onScreen = 0.02f;
            if (vp.x > onScreen && vp.x < 1f - onScreen && vp.y > onScreen && vp.y < 1f - onScreen)
                return;

            // Direction from the screen centre, in GUI pixels (y down), pushed out to the edge
            // of an inset rectangle.
            Vector2 half = new(Screen.width * 0.5f, Screen.height * 0.5f);
            Vector2 dir = new((vp.x - 0.5f) * Screen.width, -(vp.y - 0.5f) * Screen.height);
            if (dir.sqrMagnitude < 1e-4f)
                return;
            float reachX = half.x - ArrowMargin * scale;
            float reachY = half.y - (dir.y < 0f ? ArrowMarginTop : ArrowMarginBottom) * scale;
            float tx = Mathf.Abs(dir.x) > 1e-4f ? reachX / Mathf.Abs(dir.x) : float.MaxValue;
            float ty = Mathf.Abs(dir.y) > 1e-4f ? reachY / Mathf.Abs(dir.y) : float.MaxValue;
            Vector2 point = half + dir * Mathf.Min(tx, ty);
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;

            Matrix4x4 old = GUI.matrix;
            Color oldColor = GUI.color;
            float size = ArrowSize * scale;

            GUI.matrix = Matrix4x4.TRS(point, Quaternion.Euler(0f, 0f, angle), Vector3.one);
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(-size * 0.5f + 2f, -size * 0.5f + 2f, size, size), Arrow);
            GUI.color = PlayerAvatar.TeamColor(opp.Team.Value);
            GUI.DrawTexture(new Rect(-size * 0.5f, -size * 0.5f, size, size), Arrow);

            // Distance, upright, just inside the arrow.
            GUI.color = oldColor;
            float dist = Vector2.Distance(opp.transform.position, cam.transform.position);
            Vector2 labelAt = point - dir.normalized * size * 1.5f;
            GUI.matrix = Matrix4x4.TRS(labelAt, Quaternion.identity, new Vector3(scale, scale, 1f));
            Shadowed(new Rect(-40f, -11f, 80f, 22f), $"{Mathf.RoundToInt(dist)}m", _arrowLabel);
            GUI.matrix = old;
        }

        #region Drawing helpers
        private static void Bar(Rect r, float fill, Color color, bool fromRight)
        {
            Fill(r, Frame);
            Rect inner = Inset(r, 1f);
            Fill(inner, Track);
            float w = inner.width * Mathf.Clamp01(fill);
            Fill(fromRight ? new Rect(inner.xMax - w, inner.y, w, inner.height) : new Rect(inner.x, inner.y, w, inner.height), color);
        }

        private static void Fill(Rect r, Color color)
        {
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static Rect Inset(Rect r, float by) => new(r.x + by, r.y + by, r.width - by * 2f, r.height - by * 2f);

        private static void Shadowed(Rect r, string text, GUIStyle style)
        {
            Color old = GUI.contentColor;
            GUI.contentColor = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, style);
            GUI.contentColor = old;
            GUI.Label(r, text, style);
        }

        /// <summary>A right-pointing triangle, generated once.</summary>
        private Texture2D Arrow
        {
            get
            {
                if (_arrow != null)
                    return _arrow;
                const int n = 64;
                _arrow = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var pixels = new Color32[n * n];
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        // Tip at (60, 32), base from (8, 8) to (8, 56); one pixel of soft edge.
                        float halfHeight = (60f - x) * 24f / 52f;
                        float edge = Mathf.Min(x + 0.5f - 8f, halfHeight - Mathf.Abs(y + 0.5f - 32f));
                        byte a = (byte)(Mathf.Clamp01(edge + 0.5f) * 255f);
                        pixels[y * n + x] = new Color32(255, 255, 255, a);
                    }
                }
                _arrow.SetPixels32(pixels);
                _arrow.Apply();
                return _arrow;
            }
        }

        private void EnsureStyles()
        {
            if (_barText != null)
                return;
            _barText = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _barText.normal.textColor = Color.white;
            _name = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Overflow };
            _name.normal.textColor = Color.white;
            _slot = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _slot.normal.textColor = Color.white;
            _respawn = new GUIStyle(GUI.skin.label) { fontSize = 48, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _respawn.normal.textColor = new Color(1f, 0.4f, 0.35f);
            _respawnSmall = new GUIStyle(GUI.skin.label) { fontSize = 26, alignment = TextAnchor.MiddleCenter };
            _respawnSmall.normal.textColor = Color.white;
            _arrowLabel = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _arrowLabel.normal.textColor = Color.white;
            _waveLabel = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter };
            _waveLabel.normal.textColor = new Color(0.85f, 0.85f, 0.9f);
        }
        #endregion
    }
}
