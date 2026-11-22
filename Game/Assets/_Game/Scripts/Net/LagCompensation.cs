using Jam.Gameplay;
using UnityEngine;

namespace Jam.Net
{
    public struct HitscanResult
    {
        /// <summary>What the shot hit and can damage: an enemy player, minion, tower or nexus.</summary>
        public IDamageable Target;
        /// <summary>Terrain the shot stopped on, when it didn't hit a target.</summary>
        public Collider2D Terrain;
        public Vector2 EndPoint;
    }

    /// <summary>
    /// Server-side hit tests. Terrain is tested with a raycast in the match's own physics
    /// scene (static, so no rewind needed); players and minions are tested analytically as
    /// AABBs, at historical positions for hitscan and current positions for projectiles.
    /// Towers have no collider and are tested as static AABBs. Only enemy minions and
    /// structures are hit; shots pass through friendly ones.
    /// </summary>
    public static class LagCompensation
    {
        public static HitscanResult Raycast(Match match, PlayerAvatar shooter, Vector2 origin, Vector2 direction, float range, double rewindTick)
        {
            direction.Normalize();
            float best = range;
            HitscanResult result = default;
            int team = shooter.Team.Value;

            RaycastHit2D terrain = match.Physics.Raycast(origin, direction, range, Layers.GroundMask);
            if (terrain.collider != null)
            {
                best = terrain.distance;
                result.Terrain = terrain.collider;
                // The nexus is solid terrain; an enemy one is also a target.
                if (match.TryGetNexus(terrain.collider, out Nexus nexus) && nexus.Team != team)
                    result.Target = nexus;
            }

            foreach (PlayerAvatar avatar in match.Avatars)
            {
                if (avatar == null || avatar == shooter || !avatar.IsSpawned || avatar.IsDead.Value)
                    continue;
                Vector2 pos = avatar.History.GetPosition(rewindTick);
                if (RayIntersectsRect(origin, direction, avatar.HitboxAt(pos), out float distance) && distance < best)
                    (best, result.Target, result.Terrain) = (distance, avatar, null);
            }

            foreach (Minion minion in match.Minions)
            {
                if (minion == null || minion.Team.Value == team || !((IDamageable)minion).Attackable)
                    continue;
                Vector2 pos = minion.History.GetPosition(rewindTick);
                if (RayIntersectsRect(origin, direction, minion.HitboxAt(pos), out float distance) && distance < best)
                    (best, result.Target, result.Terrain) = (distance, minion, null);
            }

            foreach (Tower tower in match.Towers)
            {
                if (tower == null || tower.Team == team || !tower.Alive)
                    continue;
                if (RayIntersectsRect(origin, direction, tower.Rect, out float distance) && distance < best)
                    (best, result.Target, result.Terrain) = (distance, tower, null);
            }

            result.EndPoint = origin + direction * best;
            return result;
        }

        /// <summary>
        /// Sweeps a projectile from <paramref name="from"/> to <paramref name="to"/>. Returns the
        /// first impact point against terrain, a (current-position) player hitbox, or an enemy
        /// minion or standing enemy tower.
        /// </summary>
        public static bool Sweep(Match match, Vector2 from, Vector2 to, float radius, PlayerAvatar ignore, int team, out Vector2 impact)
        {
            Vector2 delta = to - from;
            float length = delta.magnitude;
            impact = to;
            if (length < 1e-5f)
                return false;
            Vector2 dir = delta / length;
            float best = float.PositiveInfinity;

            RaycastHit2D terrain = match.Physics.Raycast(from, dir, length, Layers.GroundMask);
            if (terrain.collider != null)
                best = terrain.distance;

            void Test(Rect box)
            {
                box.min -= Vector2.one * radius;
                box.max += Vector2.one * radius;
                if (RayIntersectsRect(from, dir, box, out float d) && d <= length && d < best)
                    best = d;
            }

            foreach (PlayerAvatar avatar in match.Avatars)
                if (avatar != null && avatar != ignore && avatar.IsSpawned && !avatar.IsDead.Value)
                    Test(avatar.HitboxAt(avatar.transform.position));
            foreach (Minion minion in match.Minions)
                if (minion != null && minion.Team.Value != team && ((IDamageable)minion).Attackable)
                    Test(minion.HitboxAt(minion.transform.position));
            foreach (Tower tower in match.Towers)
                if (tower != null && tower.Team != team && tower.Alive)
                    Test(tower.Rect);

            if (float.IsPositiveInfinity(best))
                return false;
            impact = from + dir * best;
            return true;
        }

        /// <summary>Slab test. A ray starting inside the rect hits at distance 0.</summary>
        public static bool RayIntersectsRect(Vector2 origin, Vector2 dir, Rect rect, out float distance)
        {
            float tMin = 0f;
            float tMax = float.PositiveInfinity;
            distance = 0f;

            for (int axis = 0; axis < 2; axis++)
            {
                float o = origin[axis];
                float d = dir[axis];
                float min = rect.min[axis];
                float max = rect.max[axis];

                if (Mathf.Abs(d) < 1e-6f)
                {
                    if (o < min || o > max)
                        return false;
                    continue;
                }

                float t1 = (min - o) / d;
                float t2 = (max - o) / d;
                if (t1 > t2)
                    (t1, t2) = (t2, t1);
                tMin = Mathf.Max(tMin, t1);
                tMax = Mathf.Min(tMax, t2);
                if (tMin > tMax)
                    return false;
            }

            distance = tMin;
            return true;
        }
    }
}
