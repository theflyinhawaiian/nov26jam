using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Jam.Net;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// A lane minion. Server-owned and server-driven (NetworkTransform syncs it to clients).
    /// Each tick it picks a target: the nearest enemy minion or player on its level within
    /// sight, otherwise the nearest enemy tower or (unshielded) nexus. It walks into range and
    /// shoots; with no target it follows its lane's route:
    /// - mid: walk to the enemy nexus
    /// - top: walk to the hatch near the enemy base, drop down to the mid lane, walk to the nexus
    /// - bottom: walk to the high pad near the enemy base, ride it up to the mid lane, walk to the nexus
    /// Enemy towers sit on the route, so they're always fought before the nexus (which is shielded
    /// until all its towers fall anyway). Minions reuse PlatformerMotor with pads switched off.
    /// </summary>
    [RequireComponent(typeof(PlatformerMotor), typeof(PositionHistory))]
    public class Minion : NetworkBehaviour, IDamageable
    {
        public const int MaxHealth = 60;
        private const float SightRange = 9f;
        private const float AttackRange = 4.5f;
        private const int AttackDamage = 8;
        private const float AttackCooldown = 1f;
        /// <summary>Targets must be on roughly the same level (no shooting through floors).</summary>
        private const float LevelTolerance = 4f;
        private const float RetargetSeconds = 0.25f;

        public readonly SyncVar<int> Team = new();
        public readonly SyncVar<int> Health = new(MaxHealth);

        [SerializeField] private SpriteRenderer _body;
        [SerializeField] private Vector2 _hitboxSize = new(0.8f, 1f);

        /// <summary>Client only: minions this client can see (for HUD health bars).</summary>
        public static readonly List<Minion> Active = new();

        /// <summary>Server only.</summary>
        public Match Match { get; set; }
        public Lane Lane { get; set; }

        public PlatformerMotor Motor { get; private set; }
        public PositionHistory History { get; private set; }
        public Rect HitboxAt(Vector2 center) => new(center - _hitboxSize * 0.5f, _hitboxSize);

        int IDamageable.TeamId => Team.Value;
        TargetKind IDamageable.Kind => TargetKind.Minion;
        bool IDamageable.Attackable => IsSpawned && Health.Value > 0;
        Rect IDamageable.Hitbox => HitboxAt(transform.position);

        private enum StepKind { Walk, Drop, Pad, Hold }

        private struct Step
        {
            public StepKind Kind;
            public float Value;
        }

        private readonly List<Step> _route = new();
        private int _step;
        private IDamageable _target;
        private float _retargetIn;
        private float _nextAttack;
        private bool _wantJump;
        private bool _launched;
        private readonly List<IDamageable> _scratch = new();

        private void Awake()
        {
            Motor = GetComponent<PlatformerMotor>();
            History = GetComponent<PositionHistory>();
            Motor.AutoPads = false;
            if (_body != null && _body.sprite == null)
                _body.sprite = Fx.WhiteSprite;
        }

        public override void OnStartServer()
        {
            BuildRoute();
            TimeManager.OnTick += ServerTick;
        }

        public override void OnStopServer()
        {
            TimeManager.OnTick -= ServerTick;
            Match?.Minions.Remove(this);
        }

        public override void OnStartClient()
        {
            Active.Add(this);
            Team.OnChange += OnTeamChanged;
            ApplyColor();
        }

        public override void OnStopClient()
        {
            Active.Remove(this);
            Team.OnChange -= OnTeamChanged;
        }

        private void OnTeamChanged(int prev, int next, bool asServer) => ApplyColor();

        private void ApplyColor()
        {
            if (_body != null)
                _body.color = Color.Lerp(PlayerAvatar.TeamColor(Team.Value), Color.white, 0.3f);
        }

        #region Route
        private void BuildRoute()
        {
            float enemy = -LaneLayout.Side(Team.Value);
            _route.Clear();
            switch (Lane)
            {
                case Lane.Top:
                    _route.Add(new Step { Kind = StepKind.Walk, Value = enemy * LaneLayout.TopDropX });
                    _route.Add(new Step { Kind = StepKind.Drop, Value = ArenaLayout.MidFloorY + 3f });
                    break;
                case Lane.Bottom:
                    _route.Add(new Step { Kind = StepKind.Walk, Value = enemy * LaneLayout.BottomPadX });
                    _route.Add(new Step { Kind = StepKind.Pad, Value = LaneLayout.HighPadApex });
                    break;
            }
            _route.Add(new Step { Kind = StepKind.Walk, Value = enemy * LaneLayout.NexusHoldX });
            _route.Add(new Step { Kind = StepKind.Hold });
            _step = 0;
        }

        private MotorInput FollowRoute(Vector2 pos)
        {
            MotorInput input = default;
            if (_step >= _route.Count)
                return input;
            Step s = _route[_step];
            switch (s.Kind)
            {
                case StepKind.Walk:
                    if (Mathf.Abs(s.Value - pos.x) < 0.3f)
                    {
                        _step++;
                        break;
                    }
                    input.X = Mathf.Clamp((s.Value - pos.x) * 2f, -1f, 1f);
                    break;
                case StepKind.Drop:
                    if (pos.y < s.Value)
                    {
                        _step++;
                        LogMilestone($"dropped into the mid lane at {pos}");
                    }
                    else if (Motor.Grounded)
                    {
                        input.DownHeld = true;
                        input.JumpPressed = true;
                    }
                    break;
                case StepKind.Pad:
                    if (Motor.Grounded)
                    {
                        Motor.Launch(s.Value);
                        _launched = true;
                        _step++;
                        LogMilestone($"launched from {pos}");
                    }
                    break;
                case StepKind.Hold:
                    break;
            }
            return input;
        }

        /// <summary>Logs each (match, team, lane, milestone) once, to confirm routes work in bot tests.</summary>
        private void LogMilestone(string what)
        {
            if (Match != null && Match.LoggedMilestones.Add($"{Team.Value}/{Lane}/{_step}"))
                Debug.Log($"[Minion] Match {Match.Id}: {(Team.Value == 0 ? "Blue" : "Red")} {Lane} minion {what}");
        }
        #endregion

        #region AI (server)
        private void ServerTick()
        {
            if (Match == null || Match.State == null || Match.Ended || !Match.SceneLoaded || Health.Value <= 0)
                return;
            float dt = (float)TimeManager.TickDelta;
            Vector2 pos = transform.position;
            MotorInput input = default;

            if (Match.State.IsLive)
            {
                _retargetIn -= dt;
                if (_retargetIn <= 0f || !StillValid(_target, pos))
                {
                    _retargetIn = RetargetSeconds;
                    _target = FindTarget(pos);
                }

                if (_target != null)
                {
                    Rect hb = _target.Hitbox;
                    Vector2 aimPoint = DamageMath.ClosestPoint(hb, pos);
                    if (Vector2.Distance(pos, aimPoint) <= AttackRange)
                    {
                        if (Time.time >= _nextAttack)
                        {
                            _nextAttack = Time.time + AttackCooldown;
                            _target.ApplyDamage(AttackDamage, DamageSource.FromTeam(Team.Value));
                            if (_target is Nexus && Match.LoggedMilestones.Add($"{Team.Value}/{Lane}/nexus"))
                                Debug.Log($"[Minion] Match {Match.Id}: {(Team.Value == 0 ? "Blue" : "Red")} {Lane} minion first hit the enemy nexus from {pos}");
                            ObserversZap(pos, hb.center);
                        }
                    }
                    else
                        input.X = Mathf.Sign(aimPoint.x - pos.x);
                }
                else
                    input = FollowRoute(pos);
            }

            // Hop over cover and steps we walk into.
            if (_wantJump)
                input.JumpPressed = true;
            input.JumpHeld = true;
            // A pad launch goes straight up through a 6-unit hatch: no steering until we land,
            // or we drift into the solid floor beside it and fall back down.
            if (_launched)
            {
                if (Motor.Grounded && Motor.Velocity.y <= 0f)
                    _launched = false;
                else
                    input.X = 0f;
            }
            Motor.Step(input, dt, Match.Physics);
            _wantJump = input.X != 0f && Motor.Grounded && Mathf.Abs(Motor.Velocity.x) < 0.05f;
        }

        private bool StillValid(IDamageable t, Vector2 pos)
        {
            if (t == null || (t is Object o && o == null) || !t.Attackable)
                return false;
            Rect hb = t.Hitbox;
            return DamageMath.DistanceTo(hb, pos) <= SightRange * 1.2f && SameLevel(hb, pos);
        }

        private IDamageable FindTarget(Vector2 pos)
        {
            IDamageable bestUnit = null, bestStructure = null;
            float unitDist = float.MaxValue, structureDist = float.MaxValue;
            Match.CollectTargets(_scratch);
            foreach (IDamageable t in _scratch)
            {
                if (t.TeamId == Team.Value || !t.Attackable)
                    continue;
                Rect hb = t.Hitbox;
                float d = DamageMath.DistanceTo(hb, pos);
                if (d > SightRange || !SameLevel(hb, pos) || !ClearShot(pos, t, hb))
                    continue;
                if (t.Kind == TargetKind.Structure)
                {
                    if (d < structureDist)
                        (structureDist, bestStructure) = (d, t);
                }
                else if (d < unitDist)
                    (unitDist, bestUnit) = (d, t);
            }
            return bestUnit ?? bestStructure;
        }

        private static bool SameLevel(Rect hb, Vector2 pos) =>
            Mathf.Abs(DamageMath.ClosestPoint(hb, pos).y - pos.y) <= LevelTolerance;

        private bool ClearShot(Vector2 from, IDamageable t, Rect hb)
        {
            RaycastHit2D hit = Match.Physics.Linecast(from, DamageMath.ClosestPoint(hb, from), Layers.GroundMask);
            // The nexus is itself solid ground.
            return hit.collider == null || (t is Nexus n && hit.collider == n.Collider);
        }

        /// <summary>Server only. Minions only take damage from the other team.</summary>
        public void ApplyDamage(int amount, DamageSource source)
        {
            if (!IsServerStarted || source.Team == Team.Value || Health.Value <= 0 || amount <= 0)
                return;
            Health.Value = Mathf.Max(0, Health.Value - amount);
            if (Health.Value == 0)
            {
                ObserversDie(transform.position);
                Despawn();
            }
        }
        #endregion

        [ObserversRpc]
        private void ObserversZap(Vector2 from, Vector2 to)
        {
            Fx.Tracer(from, to, Color.Lerp(PlayerAvatar.TeamColor(Team.Value), Color.white, 0.6f), 0.06f, 0.05f);
        }

        [ObserversRpc]
        private void ObserversDie(Vector2 at)
        {
            Fx.Explosion(at, 0.6f);
        }
    }
}
