using UnityEngine;

namespace Jam
{
    /// <summary>
    /// Arena dimensions in world units. One screen = 32x18 (orthographic size 9 at 16:9);
    /// the arena is 4 screens wide and 5 tall, centred on x = 0 with the bottom floor at y = 0.
    /// Bands from the bottom: bottom lane, jungle, mid lane, jungle, top lane (18 units each).
    /// </summary>
    public static class ArenaLayout
    {
        public const float ScreenWidth = 32f;
        public const float ScreenHeight = 18f;
        public const float HalfWidth = ScreenWidth * 2f;   // 64
        public const float Height = ScreenHeight * 5f;     // 90
        public const float BandHeight = ScreenHeight;

        public const float BottomFloorY = 0f;
        public const float MidFloorY = BandHeight * 2f;   // 36
        public const float TopFloorY = BandHeight * 4f;   // 72

        public static readonly Rect Bounds = new(-HalfWidth, 0f, HalfWidth * 2f, Height);
    }
}
