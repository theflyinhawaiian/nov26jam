namespace Jam
{
    public enum Lane { Bottom = 0, Mid = 1, Top = 2 }

    /// <summary>
    /// Lane and tower indexing shared by every arena. Positions (tower x, lane floors, minion
    /// routes) are per arena, in <see cref="ArenaDef"/>. Team 0 (Blue) is on the left (x &lt; 0),
    /// team 1 (Red) on the right; Side(team) is -1 or +1.
    /// </summary>
    public static class LaneLayout
    {
        public const int LaneCount = 3;
        public const int TowerCount = LaneCount * 2;
        public const float TowerWidth = 2f;
        public const float TowerHeight = 4f;

        public static float Side(int team) => team == 0 ? -1f : 1f;

        public static int TowerIndex(int team, Lane lane) => team * LaneCount + (int)lane;
        public static int TowerTeam(int index) => index / LaneCount;
        public static Lane TowerLane(int index) => (Lane)(index % LaneCount);
    }
}
