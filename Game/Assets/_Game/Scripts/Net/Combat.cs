using System.Collections.Generic;
using Jam.Gameplay;
using UnityEngine;

namespace Jam.Net
{
    /// <summary>Server-side damage rules.</summary>
    public static class Combat
    {
        private static readonly List<IDamageable> Targets = new();

        /// <summary>
        /// Linear falloff from full damage at the centre to zero at the radius, measured to the
        /// nearest point of each target. Damages every player in range, including the thrower,
        /// and enemy minions, towers and (unshielded) nexus.
        /// </summary>
        public static void Explode(Match match, Vector2 center, WeaponDefinition weapon, DamageSource source)
        {
            float radius = weapon.ExplosionRadius;
            int damage = Mathf.RoundToInt(weapon.Damage * (source.Player != null ? source.Player.DamageMultiplier : 1f));
            match.CollectTargets(Targets);
            foreach (IDamageable t in Targets)
            {
                if (!t.Attackable || (t.Kind != TargetKind.Player && t.TeamId == source.Team))
                    continue;
                int dmg = Falloff(damage, DamageMath.DistanceTo(t.Hitbox, center), radius);
                if (dmg > 0)
                    t.ApplyDamage(dmg, source);
            }
        }

        private static int Falloff(int damage, float distance, float radius)
        {
            if (distance > radius)
                return 0;
            return Mathf.Max(1, Mathf.RoundToInt(damage * (1f - distance / radius)));
        }
    }
}
