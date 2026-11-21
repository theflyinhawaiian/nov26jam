using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Grenades aren't NetworkObjects: every peer simulates the same analytic arc from
    /// (origin, velocity, age), and only the server decides where it explodes.
    /// </summary>
    public static class Grenade
    {
        public static Vector2 PositionAt(Vector2 origin, Vector2 velocity, float gravity, float age) =>
            origin + velocity * age + 0.5f * gravity * age * age * Vector2.down;
    }
}
