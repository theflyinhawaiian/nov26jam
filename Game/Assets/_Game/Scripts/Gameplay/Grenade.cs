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

        /// <summary>
        /// Terrain a grenade moving from → to hits: solid ground, or a one-way platform's top
        /// face while the grenade is moving down (rising grenades pass up through them, like players).
        /// Shared by the server sweep and the client visual so both stop in the same place.
        /// </summary>
        public static bool CastTerrain(PhysicsScene2D physics, Vector2 from, Vector2 to, out float distance)
        {
            distance = float.PositiveInfinity;
            Vector2 delta = to - from;
            float length = delta.magnitude;
            if (length < 1e-5f)
                return false;
            Vector2 dir = delta / length;

            RaycastHit2D ground = physics.Raycast(from, dir, length, Layers.GroundMask);
            if (ground.collider != null)
                distance = ground.distance;

            // Only the top face counts: the step must start at or above it. A grenade that rose
            // into a platform's thickness and starts falling inside it drops out through the bottom.
            if (delta.y < 0f)
            {
                RaycastHit2D platform = physics.Raycast(from, dir, length, Layers.OneWayMask);
                if (platform.collider != null && from.y >= platform.collider.bounds.max.y - 0.01f && platform.distance < distance)
                    distance = platform.distance;
            }
            return !float.IsPositiveInfinity(distance);
        }
    }
}
