using UnityEngine;

namespace Jam.Gameplay
{
    public struct MotorInput
    {
        /// <summary>-1..1</summary>
        public float X;
        public bool JumpHeld;
        public bool JumpPressed;
        public bool DownHeld;
    }

    /// <summary>
    /// Kinematic side-view character controller. Moves by box-casting against a specific
    /// PhysicsScene2D (each arena copy has its own), resolving X then Y.
    /// - Ground: solid on all sides.
    /// - OneWay: solid only when falling onto it from above; Down+Jump drops through.
    /// - JumpPad triggers launch the player to the pad's apex height.
    /// Runs on the owning client only (movement is client-authoritative).
    /// </summary>
    public class PlatformerMotor : MonoBehaviour
    {
        [SerializeField] private Vector2 _size = new(0.9f, 1.45f);
        [SerializeField] private float _runSpeed = 9f;
        [SerializeField] private float _groundAccel = 90f;
        [SerializeField] private float _airAccel = 50f;
        [SerializeField] private float _gravity = 40f;
        [SerializeField] private float _maxFallSpeed = 30f;
        [SerializeField] private float _jumpHeight = 5f;
        [SerializeField] private float _jumpCutMultiplier = 0.5f;
        [SerializeField] private float _coyoteTime = 0.1f;
        [SerializeField] private float _jumpBufferTime = 0.12f;
        [SerializeField] private float _dropThroughTime = 0.25f;

        private const float Skin = 0.02f;
        private const float MaxStep = 1f / 30f;

        public Vector2 Velocity;
        public bool Grounded { get; private set; }
        public float Gravity => _gravity;
        public float RunSpeed => _runSpeed;
        public float MaxFallSpeed => _maxFallSpeed;
        public float JumpSpeed => Mathf.Sqrt(2f * _gravity * _jumpHeight);

        private bool _groundIsOneWay;
        private bool _jumpCuttable;
        private float _lastGroundedTime = float.NegativeInfinity;
        private float _lastJumpPressedTime = float.NegativeInfinity;
        private float _ignoreOneWayUntil = float.NegativeInfinity;
        private float _padCooldownUntil = float.NegativeInfinity;

        private readonly RaycastHit2D[] _hits = new RaycastHit2D[8];
        private readonly Collider2D[] _overlaps = new Collider2D[4];

        public float PadLaunchSpeed(float apexHeight) => Mathf.Sqrt(2f * _gravity * apexHeight);

        public void Teleport(Vector2 position)
        {
            transform.position = position;
            Velocity = Vector2.zero;
        }

        public void Step(MotorInput input, float deltaTime, PhysicsScene2D physics)
        {
            // Sub-step long frames so fast falls and pad launches can't tunnel through 1-unit floors.
            while (deltaTime > 0f)
            {
                float dt = Mathf.Min(deltaTime, MaxStep);
                StepOnce(input, dt, physics);
                input.JumpPressed = false;
                deltaTime -= dt;
            }
        }

        private void StepOnce(MotorInput input, float dt, PhysicsScene2D physics)
        {
            float now = Time.time;
            if (input.JumpPressed)
                _lastJumpPressedTime = now;

            float accel = Grounded ? _groundAccel : _airAccel;
            Velocity.x = Mathf.MoveTowards(Velocity.x, Mathf.Clamp(input.X, -1f, 1f) * _runSpeed, accel * dt);

            bool jumpBuffered = now - _lastJumpPressedTime <= _jumpBufferTime;
            if (jumpBuffered && input.DownHeld && Grounded && _groundIsOneWay)
            {
                _ignoreOneWayUntil = now + _dropThroughTime;
                _lastJumpPressedTime = float.NegativeInfinity;
                Grounded = false;
            }
            else if (jumpBuffered && now - _lastGroundedTime <= _coyoteTime)
            {
                Velocity.y = JumpSpeed;
                _jumpCuttable = true;
                _lastJumpPressedTime = float.NegativeInfinity;
                _lastGroundedTime = float.NegativeInfinity;
                Grounded = false;
            }

            // Variable jump height: releasing jump while rising cuts upward speed once.
            if (_jumpCuttable && !input.JumpHeld && Velocity.y > 0f)
            {
                Velocity.y *= _jumpCutMultiplier;
                _jumpCuttable = false;
            }

            Velocity.y = Mathf.Max(Velocity.y - _gravity * dt, -_maxFallSpeed);

            Vector2 pos = transform.position;
            pos = MoveX(physics, pos, Velocity.x * dt);
            pos = MoveY(physics, pos, Velocity.y * dt, now);
            pos = ClampToArena(pos);
            transform.position = pos;

            CheckJumpPads(physics, pos, now);
        }

        private Vector2 MoveX(PhysicsScene2D physics, Vector2 pos, float dx)
        {
            if (Mathf.Approximately(dx, 0f))
                return pos;

            Vector2 dir = new(Mathf.Sign(dx), 0f);
            float allowed = Mathf.Abs(dx);
            int n = physics.BoxCast(pos, _size, 0f, dir, allowed + Skin, _hits, Layers.GroundMask);
            for (int i = 0; i < n; i++)
            {
                // Ignore anything we already overlap so we can never get stuck inside geometry.
                if (_hits[i].distance <= 0f)
                    continue;
                float d = Mathf.Max(0f, _hits[i].distance - Skin);
                if (d < allowed)
                {
                    allowed = d;
                    Velocity.x = 0f;
                }
            }
            return pos + dir * allowed;
        }

        private Vector2 MoveY(PhysicsScene2D physics, Vector2 pos, float dy, float now)
        {
            bool falling = dy <= 0f;
            Vector2 dir = falling ? Vector2.down : Vector2.up;
            float allowed = Mathf.Abs(dy);
            int mask = Layers.GroundMask;
            if (falling && now >= _ignoreOneWayUntil)
                mask |= Layers.OneWayMask;

            float bottom = pos.y - _size.y * 0.5f;
            bool blocked = false;
            bool blockedByOneWay = false;

            int n = physics.BoxCast(pos, _size, 0f, dir, allowed + Skin, _hits, mask);
            for (int i = 0; i < n; i++)
            {
                RaycastHit2D hit = _hits[i];
                bool oneWay = hit.collider.gameObject.layer == Layers.OneWay;
                // A one-way platform only counts if our feet started at or above its top.
                if (oneWay && hit.collider.bounds.max.y > bottom + 0.05f)
                    continue;
                if (!oneWay && hit.distance <= 0f)
                    continue;

                float d = Mathf.Max(0f, hit.distance - Skin);
                if (d <= allowed)
                {
                    allowed = d;
                    blocked = true;
                    blockedByOneWay = oneWay;
                }
            }

            Grounded = falling && blocked;
            if (Grounded)
            {
                _groundIsOneWay = blockedByOneWay;
                _lastGroundedTime = now;
                _jumpCuttable = false;
            }
            if (blocked)
                Velocity.y = 0f;

            return pos + dir * allowed;
        }

        private void CheckJumpPads(PhysicsScene2D physics, Vector2 pos, float now)
        {
            if (now < _padCooldownUntil)
                return;
            int n = physics.OverlapBox(pos, _size, 0f, _overlaps, Layers.JumpPadMask);
            for (int i = 0; i < n; i++)
            {
                if (!_overlaps[i].TryGetComponent(out JumpPad pad))
                    continue;
                float launch = PadLaunchSpeed(pad.LaunchHeight);
                if (Velocity.y >= launch * 0.5f)
                    continue;
                Velocity.y = launch;
                _jumpCuttable = false;
                Grounded = false;
                _padCooldownUntil = now + 0.3f;
                return;
            }
        }

        private Vector2 ClampToArena(Vector2 pos)
        {
            Rect r = ArenaLayout.Bounds;
            Vector2 half = _size * 0.5f;
            return new Vector2(
                Mathf.Clamp(pos.x, r.xMin + half.x, r.xMax - half.x),
                Mathf.Clamp(pos.y, r.yMin + half.y, r.yMax - half.y));
        }
    }
}
