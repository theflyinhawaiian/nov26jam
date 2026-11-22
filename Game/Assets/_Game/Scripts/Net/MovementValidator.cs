using Jam.Gameplay;
using UnityEngine;

namespace Jam.Net
{
    /// <summary>
    /// Server-side plausibility check for one client-authoritative avatar, run once per tick
    /// on the position NetworkTransform has applied. Movement is allowed if it fits:
    /// - Horizontal and falling speed: budgets that refill at the motor's top speed (plus a
    ///   tolerance) and can bank up to BurstSeconds of travel, so a late burst of packets
    ///   after a lag spike isn't flagged.
    /// - Rising: a height allowance refilled near the ground (one jump) or at a jump pad
    ///   (that pad's apex height), spent as the avatar rises and lost once it has fallen a
    ///   little, so it can't jump in mid-air or fly.
    /// - Walls: the path between two samples can't cross solid ground.
    /// On a failure the caller snaps the owner back to the last accepted position. Forced
    /// teleports (respawn, corrections) pause the checks until the owner arrives.
    /// </summary>
    public class MovementValidator
    {
        private const float SpeedTolerance = 1.25f;
        private const float BurstSeconds = 0.75f;
        private const float RiseSlack = 1.5f;
        /// <summary>Falling further than this since the last support forfeits the jump allowance.</summary>
        private const float FallForfeit = 1f;
        private const float GroundProbe = 0.35f;
        private const float ArriveDistance = 1.5f;
        private const float ResendSeconds = 1.5f;

        private readonly PlayerAvatar _avatar;
        private readonly RaycastHit2D[] _hits = new RaycastHit2D[8];

        private bool _hasLast;
        private Vector2 _last;
        private float _lastDy;
        private float _xBudget;
        private float _downBudget;
        private float _riseCredit;
        private float _fallSinceSupport;
        private bool _pending;
        private Vector2 _pendingTarget;
        private float _pendingSince;

        public int Corrections { get; private set; }
        /// <summary>Where a forced teleport is sending the owner, until their position gets there.</summary>
        public Vector2? PendingTeleport => _pending ? _pendingTarget : null;
        /// <summary>Why the last check failed (for logs).</summary>
        public string LastFailure { get; private set; }

        public MovementValidator(PlayerAvatar avatar) => _avatar = avatar;

        private PlatformerMotor Motor => _avatar.Motor;
        private float MaxX => Motor.RunSpeed * SpeedTolerance;
        private float MaxDown => Motor.MaxFallSpeed * SpeedTolerance;
        private float JumpRise => Motor.JumpHeight + RiseSlack;

        /// <summary>A server-forced teleport is on its way to the owner: stop checking until they arrive.</summary>
        public void ExpectTeleport(Vector2 target, float now)
        {
            _pending = true;
            _pendingTarget = target;
            _pendingSince = now;
        }

        public enum Result { Ok, Violation, Resend }

        /// <param name="correction">Where to send the owner on Violation or Resend.</param>
        public Result Check(Vector2 pos, float dt, float now, PhysicsScene2D physics, out Vector2 correction)
        {
            correction = default;
            if (!_hasLast)
            {
                Accept(pos);
                return Result.Ok;
            }

            if (_pending)
            {
                if (Vector2.Distance(pos, _pendingTarget) <= ArriveDistance)
                {
                    _pending = false;
                    Accept(pos);
                    return Result.Ok;
                }
                if (now - _pendingSince < ResendSeconds)
                    return Result.Ok;
                _pendingSince = now;
                correction = _pendingTarget;
                return Result.Resend;
            }

            _xBudget = Mathf.Min(_xBudget + MaxX * dt, MaxX * BurstSeconds);
            _downBudget = Mathf.Min(_downBudget + MaxDown * dt, MaxDown * BurstSeconds);

            Vector2 delta = pos - _last;
            LastFailure = null;

            float dx = Mathf.Abs(delta.x);
            if (dx > _xBudget + 0.05f)
                LastFailure = $"horizontal {dx:0.00}u > budget {_xBudget:0.00}u";
            else
                _xBudget -= dx;

            if (LastFailure == null && delta.y < 0f)
            {
                if (-delta.y > _downBudget + 0.05f)
                    LastFailure = $"fall {-delta.y:0.00}u > budget {_downBudget:0.00}u";
                else
                    _downBudget += delta.y;
            }

            if (LastFailure == null)
            {
                // Support at the previous sample refills the jump allowance. Probe further when
                // moving fast: at 30 Hz a landing-and-rejump can happen between two samples.
                float probe = GroundProbe + Mathf.Max(Mathf.Abs(delta.y), Mathf.Abs(_lastDy));
                if (NearGround(physics, _last, probe))
                {
                    _riseCredit = Mathf.Max(_riseCredit, JumpRise);
                    _fallSinceSupport = 0f;
                }
                float pad = PadHeight(physics, _last, pos, probe);
                if (pad > 0f)
                {
                    _riseCredit = Mathf.Max(_riseCredit, pad + RiseSlack);
                    _fallSinceSupport = 0f;
                }

                if (delta.y > 0f)
                {
                    _riseCredit -= delta.y;
                    _fallSinceSupport = 0f;
                    if (_riseCredit < 0f)
                        LastFailure = $"rise {delta.y:0.00}u beyond allowance (over by {-_riseCredit:0.00}u)";
                }
                else
                {
                    _fallSinceSupport -= delta.y;
                    if (_fallSinceSupport > FallForfeit)
                        _riseCredit = 0f;
                }
            }

            if (LastFailure == null && ThroughWall(physics, _last, pos))
                LastFailure = $"through solid ground from {_last} to {pos}";

            if (LastFailure != null)
            {
                LastFailure += $" [from {_last} to {pos}, last dy {_lastDy:0.00}]";
                Corrections++;
                correction = _last;
                ExpectTeleport(_last, now);
                return Result.Violation;
            }

            _lastDy = delta.y;
            _last = pos;
            return Result.Ok;
        }

        private void Accept(Vector2 pos)
        {
            _hasLast = true;
            _last = pos;
            _lastDy = 0f;
            _xBudget = MaxX * BurstSeconds;
            _downBudget = MaxDown * BurstSeconds;
            // Spawns and corrections put the avatar on (or near) the ground.
            _riseCredit = JumpRise;
            _fallSinceSupport = 0f;
        }

        private bool NearGround(PhysicsScene2D physics, Vector2 pos, float probe)
        {
            int n = physics.BoxCast(pos, Motor.Size, 0f, Vector2.down, probe, _hits, Layers.GroundMask | Layers.OneWayMask);
            for (int i = 0; i < n; i++)
                if (_hits[i].distance > 0f)
                    return true;
            return false;
        }

        /// <summary>
        /// Tallest jump pad touched between the two samples, or 0. Samples are 1/30 s apart, so
        /// a fall onto a pad and the launch back up can both happen between them without the
        /// straight path ever reaching the (0.5-unit-tall) pad: probe downward from both too.
        /// </summary>
        private float PadHeight(PhysicsScene2D physics, Vector2 from, Vector2 to, float probe)
        {
            // Generous box: the motor launches on any overlap.
            Vector2 box = Motor.Size + Vector2.one * 0.5f;
            Vector2 d = to - from;
            float dist = d.magnitude;
            float best = BestPad(physics.BoxCast(from, box, 0f, dist > 1e-4f ? d / dist : Vector2.up, dist, _hits, Layers.JumpPadMask), 0f);
            best = BestPad(physics.BoxCast(from, box, 0f, Vector2.down, probe, _hits, Layers.JumpPadMask), best);
            best = BestPad(physics.BoxCast(to, box, 0f, Vector2.down, probe, _hits, Layers.JumpPadMask), best);
            return best;
        }

        private float BestPad(int hits, float best)
        {
            for (int i = 0; i < hits; i++)
                if (_hits[i].collider.TryGetComponent(out JumpPad pad))
                    best = Mathf.Max(best, pad.LaunchHeight);
            return best;
        }

        /// <summary>
        /// The motor resolves X then Y in small steps, so a legal move between two samples can
        /// round a corner. Only flag it if neither L-shaped path around the corner is clear.
        /// </summary>
        private static bool ThroughWall(PhysicsScene2D physics, Vector2 from, Vector2 to)
        {
            if (!Blocked(physics, from, to))
                return false;
            Vector2 cornerA = new(to.x, from.y);
            Vector2 cornerB = new(from.x, to.y);
            bool pathA = !Blocked(physics, from, cornerA) && !Blocked(physics, cornerA, to);
            bool pathB = !Blocked(physics, from, cornerB) && !Blocked(physics, cornerB, to);
            return !pathA && !pathB;
        }

        private static bool Blocked(PhysicsScene2D physics, Vector2 a, Vector2 b)
        {
            if ((b - a).sqrMagnitude < 1e-6f)
                return false;
            return physics.Linecast(a, b, Layers.GroundMask).collider != null;
        }
    }
}
