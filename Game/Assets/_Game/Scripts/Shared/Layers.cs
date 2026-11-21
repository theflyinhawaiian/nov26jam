namespace Jam
{
    /// <summary>Physics layers (names are written to the TagManager by ProjectBuilder).</summary>
    public static class Layers
    {
        /// <summary>Solid terrain and structures: blocks movement and hitscan.</summary>
        public const int Ground = 6;
        /// <summary>One-way platforms: stand on from above, jump up/drop through. Don't block hitscan.</summary>
        public const int OneWay = 7;
        /// <summary>Jump pad triggers.</summary>
        public const int JumpPad = 8;

        public const int GroundMask = 1 << Ground;
        public const int OneWayMask = 1 << OneWay;
        public const int JumpPadMask = 1 << JumpPad;
    }
}
