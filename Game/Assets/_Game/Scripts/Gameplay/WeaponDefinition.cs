using UnityEngine;

namespace Jam.Gameplay
{
    public enum WeaponKind : byte
    {
        Hitscan,
        Projectile,
    }

    /// <summary>Weapon tuning shared by client (visuals, local cooldown) and server (authority).</summary>
    [CreateAssetMenu(menuName = "nov26jam/Weapon")]
    public class WeaponDefinition : ScriptableObject
    {
        public string DisplayName = "Weapon";
        public WeaponKind Kind;
        [Tooltip("Hitscan: per hit. Projectile: at the centre of the explosion.")]
        public int Damage = 10;
        [Tooltip("Seconds between shots.")]
        public float Cooldown = 0.2f;

        [Header("Hitscan")]
        public float Range = 24f;

        [Header("Projectile")]
        public float LaunchSpeed = 22f;
        public float ProjectileGravity = 25f;
        public float ExplosionRadius = 2.5f;
        public float MaxLifetime = 5f;
        [Tooltip("Collision radius of the projectile itself against players.")]
        public float ProjectileRadius = 0.2f;
    }
}
