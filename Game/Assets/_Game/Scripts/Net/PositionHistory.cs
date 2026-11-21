using FishNet.Object;
using UnityEngine;

namespace Jam.Net
{
    /// <summary>
    /// Server-only ring buffer of where this object was on each tick. FishNet's
    /// ColliderRollback is a Pro feature (stubbed in the free package), so hitscan
    /// lag compensation rewinds against this history instead of moving colliders.
    /// </summary>
    public class PositionHistory : NetworkBehaviour
    {
        // 64 ticks ≈ 2.1s at 30Hz, comfortably more than any latency we'll compensate for.
        private const int Capacity = 64;

        private struct Sample
        {
            public uint Tick;
            public Vector2 Position;
        }

        private readonly Sample[] _samples = new Sample[Capacity];
        private int _head;
        private int _count;

        public override void OnStartServer()
        {
            _count = 0;
            _head = 0;
            TimeManager.OnPostTick += Record;
        }

        public override void OnStopServer()
        {
            TimeManager.OnPostTick -= Record;
        }

        private void Record()
        {
            _samples[_head] = new Sample { Tick = TimeManager.Tick, Position = transform.position };
            _head = (_head + 1) % Capacity;
            if (_count < Capacity)
                _count++;
        }

        /// <summary>
        /// Position at a fractional server tick, interpolated between recorded samples.
        /// Clamps to the oldest/newest sample when out of range.
        /// </summary>
        public Vector2 GetPosition(double tick)
        {
            if (_count == 0)
                return transform.position;

            // Walk newest → oldest.
            Sample newer = SampleAt(0);
            if (tick >= newer.Tick)
                return newer.Position;

            for (int i = 1; i < _count; i++)
            {
                Sample older = SampleAt(i);
                if (tick >= older.Tick)
                {
                    float t = (float)((tick - older.Tick) / (newer.Tick - older.Tick));
                    return Vector2.Lerp(older.Position, newer.Position, t);
                }
                newer = older;
            }
            return newer.Position;
        }

        /// <param name="age">0 = newest sample.</param>
        private Sample SampleAt(int age) => _samples[(_head - 1 - age + Capacity * 2) % Capacity];
    }
}
