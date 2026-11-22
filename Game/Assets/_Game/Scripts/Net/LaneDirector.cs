using System;
using System.Collections.Generic;
using Jam.Gameplay;
using UnityEngine;

namespace Jam.Net
{
    /// <summary>
    /// Server-side lane logic for one match, run every tick by MatchManager:
    /// - Waves: when play starts and every 30 s after (debug: -waveseconds N on the server),
    ///   each team sends WavePerLane (melee, flyer, ranged, flyer) down each lane in play, a little apart.
    /// - Towers: each standing tower fires once per TowerCooldown at a target in range on its
    ///   level with a clear line: an enemy player who just hurt a friendly player near the
    ///   tower first, then the nearest enemy minion, then an enemy player. It sticks with its
    ///   target while it stays valid.
    /// </summary>
    public class LaneDirector
    {
        public const float DefaultWaveSeconds = 30f;
        /// <summary>Per lane per team, in spawn order (melee leads).</summary>
        private static readonly MinionType[] WavePerLane = { MinionType.Melee, MinionType.Flyer, MinionType.Ranged, MinionType.Flyer };
        private const float SpawnSpacing = 0.8f;

        private const float TowerRange = 11f;
        private const float TowerCooldown = 1f;
        private const int TowerDamageToMinions = 20;
        private const int TowerDamageToPlayers = 12;
        /// <summary>How recently a player must have hurt a friendly player to draw tower fire.</summary>
        private const float AggroSeconds = 2f;
        private const float TowerLevelTolerance = 5f;

        private readonly Match _match;
        private readonly Action<Match, int, Lane, MinionType> _spawn;
        private readonly bool[] _teamSendsMinions;
        private readonly bool[] _laneActive;
        private readonly float _waveSeconds;
        private readonly List<(float at, int team, Lane lane, MinionType type)> _pendingSpawns = new();
        private readonly float[] _towerNextShot = new float[LaneLayout.TowerCount];
        private readonly IDamageable[] _towerTarget = new IDamageable[LaneLayout.TowerCount];
        private readonly List<IDamageable> _scratch = new();
        private int _waves;

        public LaneDirector(Match match, Action<Match, int, Lane, MinionType> spawn, bool[] teamSendsMinions, bool[] laneActive, float waveSeconds = DefaultWaveSeconds)
        {
            _waveSeconds = waveSeconds;
            _match = match;
            _spawn = spawn;
            _teamSendsMinions = teamSendsMinions;
            _laneActive = laneActive;
        }

        public void Tick()
        {
            MatchState state = _match.State;
            if (state == null || _match.Ended || !_match.SceneLoaded || !state.IsLive)
                return;
            float now = Time.time;
            var tm = state.TimeManager;

            if (state.NextWaveTick.Value == 0 || tm.Tick >= state.NextWaveTick.Value)
            {
                QueueWave(now);
                state.NextWaveTick.Value = tm.Tick + tm.TimeToTicks(_waveSeconds);
            }

            for (int i = _pendingSpawns.Count - 1; i >= 0; i--)
            {
                if (now < _pendingSpawns[i].at)
                    continue;
                (_, int team, Lane lane, MinionType type) = _pendingSpawns[i];
                _pendingSpawns.RemoveAt(i);
                _spawn(_match, team, lane, type);
            }

            for (int i = 0; i < LaneLayout.TowerCount; i++)
                TickTower(i, now);
        }

        private void QueueWave(float now)
        {
            _waves++;
            for (int team = 0; team < 2; team++)
            {
                if (!_teamSendsMinions[team])
                    continue;
                for (int lane = 0; lane < LaneLayout.LaneCount; lane++)
                    if (_laneActive[lane])
                        for (int k = 0; k < WavePerLane.Length; k++)
                        _pendingSpawns.Add((now + k * SpawnSpacing, team, (Lane)lane, WavePerLane[k]));
            }
            Debug.Log($"[Lanes] Match {_match.Id}: wave {_waves}, {_match.Minions.Count} minions alive before it");
        }

        private void TickTower(int index, float now)
        {
            Tower tower = _match.Towers[index];
            if (tower == null || !tower.Alive || now < _towerNextShot[index])
                return;

            IDamageable target = PickTowerTarget(tower, _towerTarget[index]);
            _towerTarget[index] = target;
            if (target == null)
                return;

            _towerNextShot[index] = now + TowerCooldown;
            int damage = target.Kind == TargetKind.Player ? TowerDamageToPlayers : TowerDamageToMinions;
            Vector2 hit = target.Hitbox.center;
            target.ApplyDamage(damage, DamageSource.FromTeam(tower.Team));
            _match.State.ObserversTowerShot(index, hit);
        }

        private IDamageable PickTowerTarget(Tower tower, IDamageable current)
        {
            Vector2 muzzle = tower.Muzzle;
            _match.CollectTargets(_scratch);

            // 1. An enemy player who just hurt one of our players, both near this tower.
            foreach (IDamageable t in _scratch)
            {
                if (t is not PlayerAvatar attacker || !InRange(tower, muzzle, t))
                    continue;
                if (Time.time - attacker.LastHitEnemyPlayerAt > AggroSeconds)
                    continue;
                foreach (PlayerAvatar victim in _match.Avatars)
                    if (victim != null && victim.Team.Value == tower.Team && ((IDamageable)victim).Attackable
                        && DamageMath.DistanceTo(((IDamageable)victim).Hitbox, muzzle) <= TowerRange)
                        return attacker;
            }

            // 2. Keep shooting the current target while it's still valid.
            if (current != null && !(current is UnityEngine.Object o && o == null) && InRange(tower, muzzle, current))
                return current;

            // 3. Nearest enemy minion, then 4. an enemy player.
            IDamageable best = null;
            float bestDist = float.MaxValue;
            bool bestIsMinion = false;
            foreach (IDamageable t in _scratch)
            {
                if (t.Kind == TargetKind.Structure || !InRange(tower, muzzle, t))
                    continue;
                bool isMinion = t.Kind == TargetKind.Minion;
                float d = DamageMath.DistanceTo(t.Hitbox, muzzle);
                if ((isMinion && !bestIsMinion) || (isMinion == bestIsMinion && d < bestDist))
                    (best, bestDist, bestIsMinion) = (t, d, isMinion);
            }
            return best;
        }

        private bool InRange(Tower tower, Vector2 muzzle, IDamageable t)
        {
            if (t.TeamId == tower.Team || !t.Attackable)
                return false;
            Rect hb = t.Hitbox;
            Vector2 p = DamageMath.ClosestPoint(hb, muzzle);
            if (Vector2.Distance(p, muzzle) > TowerRange || Mathf.Abs(p.y - muzzle.y) > TowerLevelTolerance)
                return false;
            return _match.Physics.Linecast(muzzle, hb.center, Layers.GroundMask).collider == null;
        }
    }
}
