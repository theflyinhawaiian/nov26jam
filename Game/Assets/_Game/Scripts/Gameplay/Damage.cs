using UnityEngine;

namespace Jam.Gameplay
{
    public enum TargetKind { Player, Minion, Structure }

    /// <summary>Who dealt damage: always a team, plus the player when a player did it.</summary>
    public readonly struct DamageSource
    {
        public readonly int Team;
        public readonly PlayerAvatar Player;

        private DamageSource(int team, PlayerAvatar player)
        {
            Team = team;
            Player = player;
        }

        public static DamageSource FromPlayer(PlayerAvatar player) => new(player.Team.Value, player);
        public static DamageSource FromTeam(int team) => new(team, null);
    }

    /// <summary>
    /// Anything that can be shot (server side): players, minions, towers, nexuses. Structures and
    /// minions only take damage from the other team; players also take their own grenade damage.
    /// </summary>
    public interface IDamageable
    {
        int TeamId { get; }
        TargetKind Kind { get; }
        /// <summary>Alive and currently able to take damage (a shielded nexus isn't).</summary>
        bool Attackable { get; }
        /// <summary>Current server-side hitbox.</summary>
        Rect Hitbox { get; }
        void ApplyDamage(int amount, DamageSource source);
    }

    public static class DamageMath
    {
        public static Vector2 ClosestPoint(Rect r, Vector2 p) =>
            new(Mathf.Clamp(p.x, r.xMin, r.xMax), Mathf.Clamp(p.y, r.yMin, r.yMax));

        public static float DistanceTo(Rect r, Vector2 p) => Vector2.Distance(p, ClosestPoint(r, p));
    }
}
