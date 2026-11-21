using Jam.Gameplay;
using UnityEngine;

namespace Jam.Net
{
    /// <summary>Server-side damage rules.</summary>
    public static class Combat
    {
        /// <summary>
        /// Linear falloff from full damage at the centre to zero at the radius, measured to the
        /// nearest point of each target. Damages everyone in range, including the thrower.
        /// </summary>
        public static void Explode(Match match, Vector2 center, WeaponDefinition weapon)
        {
            float radius = weapon.ExplosionRadius;

            foreach (PlayerAvatar avatar in match.Avatars)
            {
                if (avatar == null || !avatar.IsSpawned || avatar.IsDead.Value)
                    continue;
                Rect box = avatar.HitboxAt(avatar.transform.position);
                float d = Vector2.Distance(center, ClosestPoint(box, center));
                int dmg = Falloff(weapon.Damage, d, radius);
                if (dmg > 0)
                    avatar.TakeDamage(dmg);
            }

            if (match.State == null)
                return;
            foreach (Nexus nexus in match.Nexuses)
            {
                if (nexus == null)
                    continue;
                Bounds b = nexus.Collider.bounds;
                float d = Vector2.Distance(center, b.ClosestPoint(center));
                int dmg = Falloff(weapon.Damage, d, radius);
                if (dmg > 0)
                    match.State.DamageNexus(nexus.Team, dmg);
            }
        }

        private static int Falloff(int damage, float distance, float radius)
        {
            if (distance > radius)
                return 0;
            return Mathf.Max(1, Mathf.RoundToInt(damage * (1f - distance / radius)));
        }

        private static Vector2 ClosestPoint(Rect r, Vector2 p) =>
            new(Mathf.Clamp(p.x, r.xMin, r.xMax), Mathf.Clamp(p.y, r.yMin, r.yMax));
    }
}
