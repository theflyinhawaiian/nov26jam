using UnityEngine;

namespace Jam
{
    public enum Lane { Bottom = 0, Mid = 1, Top = 2 }

    /// <summary>
    /// Where lanes, towers and minion routes are. Shared by ArenaBuilder (tower placement) and
    /// the server's minion and tower logic. Team 0 (Blue) is on the left (x &lt; 0), team 1 (Red)
    /// on the right; Side(team) is -1 or +1. All x values here are distances from the centre.
    /// </summary>
    public static class LaneLayout
    {
        public const int LaneCount = 3;
        public const int TowerCount = LaneCount * 2;
        public const float TowerWidth = 2f;
        public const float TowerHeight = 4f;

        /// <summary>|x| of each team's tower per lane (bottom is further out to clear the cover block at 30).</summary>
        private static readonly float[] TowerX = { 34f, 32f, 32f };
        /// <summary>|x| where minions spawn per lane: in front of the nexus for mid, near the wall for the flanks.</summary>
        private static readonly float[] SpawnX = { 60f, 54f, 60f };

        /// <summary>Top lane minions drop through the top-floor hatch here; bottom lane minions ride the high pad here.</summary>
        public const float TopDropX = 38f;
        public const float BottomPadX = 44f;
        public const float HighPadApex = 39f;
        /// <summary>Minions hold here in front of the enemy nexus (nexus face is at 58.5).</summary>
        public const float NexusHoldX = 57f;

        public static float Side(int team) => team == 0 ? -1f : 1f;

        public static float FloorY(Lane lane) => lane switch
        {
            Lane.Bottom => ArenaLayout.BottomFloorY,
            Lane.Mid => ArenaLayout.MidFloorY,
            _ => ArenaLayout.TopFloorY,
        };

        public static int TowerIndex(int team, Lane lane) => team * LaneCount + (int)lane;
        public static int TowerTeam(int index) => index / LaneCount;
        public static Lane TowerLane(int index) => (Lane)(index % LaneCount);

        /// <summary>Centre of a tower (it stands on the lane floor).</summary>
        public static Vector2 TowerCenter(int team, Lane lane) =>
            new(Side(team) * TowerX[(int)lane], FloorY(lane) + TowerHeight * 0.5f);

        public static Rect TowerRect(int team, Lane lane)
        {
            Vector2 c = TowerCenter(team, lane);
            return new Rect(c.x - TowerWidth * 0.5f, c.y - TowerHeight * 0.5f, TowerWidth, TowerHeight);
        }

        public static Vector2 MinionSpawn(int team, Lane lane) =>
            new(Side(team) * SpawnX[(int)lane], FloorY(lane) + 0.6f);
    }
}
