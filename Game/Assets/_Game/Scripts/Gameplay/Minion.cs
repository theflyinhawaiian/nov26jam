using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Jam.Net;
using UnityEngine;

namespace Jam.Gameplay
{
    public enum MinionType : byte { Melee, Ranged, Flyer }

    /// <summary>
    /// A lane minion. Server-owned and server-driven (NetworkTransform syncs it to clients).
    /// Three types (see Stats): melee and ranged walk (PlatformerMotor, pads off); flyers fly.
    ///
    /// Targeting: the nearest enemy minion or player on its level within sight that it can
    /// actually reach (melee can't reach flyers), otherwise the nearest enemy tower or
    /// (unshielded) nexus. It moves into range and attacks; with no target it follows its
    /// lane's route:
    /// - mid: walk to the enemy nexus
    /// - top: walk to the hatch near the enemy base, drop down to the mid lane, walk to the nexus
    /// - bottom: walk to the high pad near the enemy base, ride it up to the mid lane, walk to the nexus
    /// Enemy towers sit on the route, so they're always fought before the nexus (which is shielded
    /// until all its towers fall anyway).
    ///
    /// Spacing: minions queue Spacing behind teammates ahead of them instead of stacking, and
    /// step back if they end up overlapping. Melee don't queue behind ranged (they walk through
    /// to the front). Flyers fly in one of three height slots over their lane and only queue
    /// behind a flyer in the same slot; a flyer that's held up short of its target switches to a
    /// free slot above or below and flies past, and one whose target is out of reach vertically
    /// moves to a slot that reaches it.
    ///
    /// Flyers have no terrain collision: they only move horizontally along a lane and vertically
    /// through the same hatch columns the ground route uses, so they never cross solid ground.
    /// </summary>
    [RequireComponent(typeof(PlatformerMotor), typeof(PositionHistory))]
    public class Minion : NetworkBehaviour, IDamageable
    {
        private struct Stats
        {
            public int Health;
            public int Damage;
            public float Range;
            public float Cooldown;
            public Vector2 Size;
            public float Paleness;
        }

        private static readonly Stats[] TypeStats =
        {
            new() { Health = 80, Damage = 12, Range = 1.2f, Cooldown = 1f, Size = new Vector2(0.9f, 0.85f), Paleness = 0.1f },  // Melee
            new() { Health = 45, Damage = 7, Range = 4.5f, Cooldown = 1f, Size = new Vector2(0.65f, 1.1f), Paleness = 0.35f },  // Ranged
            new() { Health = 40, Damage = 6, Range = 4.5f, Cooldown = 1f, Size = new Vector2(0.9f, 0.6f), Paleness = 0.55f },   // Flyer
        };

        private const float SightRange = 9f;
        /// <summary>Targets must be on roughly the same level (no shooting through floors).</summary>
        private const float LevelTolerance = 4f;
        private const float RetargetSeconds = 0.25f;
        /// <summary>Gap kept to a teammate ahead (centre to centre); closer than BackOff*Spacing, step back.</summary>
        private const float Spacing = 1.1f;
        private const float BackOff = 0.6f;

        /// <summary>Flyer height slots above the lane floor: above the cover (2.5), low enough to hit ground targets.</summary>
        private static readonly float[] SlotHeights = { 3f, 4.1f, 5.2f };
        private const int DefaultSlot = 1;
        private const float BobAmplitude = 0.25f;
        private const float BobSpeed = 1.7f;
        private const float FlySpeed = 4.5f;
        private const float ClimbSpeed = 7f;
        private const float BobFollowSpeed = 3f;
        /// <summary>A flyer changing slot holds its horizontal position until within this of the new height.</summary>
        private const float SlotSettle = 0.4f;

        public readonly SyncVar<int> Team = new();
        public readonly SyncVar<int> Health = new(TypeStats[0].Health);
        /// <summary>Set before spawning.</summary>
        public readonly SyncVar<MinionType> Type = new();

        [SerializeField] private SpriteRenderer _body;

        /// <summary>Client only: minions this client can see (for HUD health bars).</summary>
        public static readonly List<Minion> Active = new();

        /// <summary>Server only.</summary>
        public Match Match { get; set; }
        /// <summary>Server only: the match's arena (routes and floors).</summary>
        private ArenaDef Arena => Match != null ? Match.Arena : Arenas.For(gameObject);
        public Lane Lane { get; set; }

        public PlatformerMotor Motor { get; private set; }
        public PositionHistory History { get; private set; }

        private Stats S => TypeStats[(int)Type.Value];
        public bool Flying => Type.Value == MinionType.Flyer;
        public int MaxHp => S.Health;
        public Rect HitboxAt(Vector2 center) => new(center - S.Size * 0.5f, S.Size);

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
        /// <summary>Last tick's horizontal intent (-1, 0, 1), so teammates walking toward each other don't deadlock.</summary>
        private float _moveDir;
        /// <summary>Flyers: floor height of the lane they're over, height slot, bob phase, hatch climb in progress.</summary>
        private float _levelY;
        private int _slot = DefaultSlot;
        private float _bobPhase;
        private bool _climbing;
        private readonly List<IDamageable> _scratch = new();
        /// <summary>Server: Time.time until which an ability's slow / stun holds us.</summary>
        private float _slowUntil, _stunUntil;

        /// <summary>Server only: slowed (half speed) or stunned (no moving or attacking) for a while.</summary>
        public void ApplyStatus(StatusEffect e, float seconds)
        {
            float until = Time.time + seconds;
            if (e == StatusEffect.Slow)
                _slowUntil = Mathf.Max(_slowUntil, until);
            else if (e == StatusEffect.Stun)
                _stunUntil = Mathf.Max(_stunUntil, until);
            ObserversStatus(e, seconds);
        }

        /// <summary>Visual only: tint while slowed or stunned.</summary>
        [ObserversRpc]
        private void ObserversStatus(StatusEffect e, float seconds)
        {
            if (_body == null || Application.isBatchMode)
                return;
            _statusTintUntil = Time.time + seconds;
            _statusTint = e == StatusEffect.Stun ? new Color(1f, 0.95f, 0.3f) : new Color(0.45f, 0.6f, 1f);
        }

        private float _statusTintUntil;
        private Color _statusTint;

        private void Update()
        {
            if (_body == null || _statusTintUntil <= 0f)
                return;
            Color baseColor = Color.Lerp(PlayerAvatar.TeamColor(Team.Value), Color.white, S.Paleness);
            if (Time.time < _statusTintUntil)
                _body.color = Color.Lerp(baseColor, _statusTint, 0.6f);
            else
            {
                _body.color = baseColor;
                _statusTintUntil = 0f;
            }
        }

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
            Health.Value = MaxHp;
            _levelY = Arena.FloorY(Lane);
            _bobPhase = Random.Range(0f, Mathf.PI * 2f);
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
            Type.OnChange += OnTypeChanged;
            ApplyLook();
        }

        public override void OnStopClient()
        {
            Active.Remove(this);
            Team.OnChange -= OnTeamChanged;
            Type.OnChange -= OnTypeChanged;
        }

        private void OnTeamChanged(int prev, int next, bool asServer) => ApplyLook();
        private void OnTypeChanged(MinionType prev, MinionType next, bool asServer) => ApplyLook();

        /// <summary>Melee: squat and dark. Ranged: tall and thin. Flyers: flat and pale.</summary>
        private void ApplyLook()
        {
            if (_body == null)
                return;
            _body.color = Color.Lerp(PlayerAvatar.TeamColor(Team.Value), Color.white, S.Paleness);
            _body.transform.localScale = new Vector3(S.Size.x, S.Size.y, 1f);
        }

        #region Route
        private void BuildRoute()
        {
            float enemy = -LaneLayout.Side(Team.Value);
            _route.Clear();
            switch (Lane)
            {
                case Lane.Top:
                    _route.Add(new Step { Kind = StepKind.Walk, Value = enemy * Arena.TopDropX });
                    _route.Add(new Step { Kind = StepKind.Drop, Value = Arena.BaseFloorY + 3f });
                    break;
                case Lane.Bottom:
                    _route.Add(new Step { Kind = StepKind.Walk, Value = enemy * Arena.BottomPadX });
                    _route.Add(new Step { Kind = StepKind.Pad, Value = Arena.HighPadApex });
                    break;
            }
            _route.Add(new Step { Kind = StepKind.Walk, Value = enemy * Arena.NexusHoldX });
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
                case StepKind.Drop when Flying:
                case StepKind.Pad when Flying:
                    // Straight down/up the hatch column to cruise height over the base floor.
                    _levelY = Arena.BaseFloorY;
                    _slot = DefaultSlot;
                    _climbing = true;
                    if (Mathf.Abs(pos.y - SlotY(_slot)) < 0.3f)
                    {
                        _climbing = false;
                        _step++;
                        LogMilestone($"flew to the base floor at {pos}");
                    }
                    break;
                case StepKind.Drop:
                    if (pos.y < s.Value)
                    {
                        _step++;
                        LogMilestone($"dropped to the base floor at {pos}");
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

        private string Describe() => $"{(Team.Value == 0 ? "Blue" : "Red")} {Lane} {Type.Value.ToString().ToLowerInvariant()}";

        /// <summary>Logs each (match, team, lane, type, milestone) once, to confirm routes work in bot tests.</summary>
        private void LogMilestone(string what)
        {
            if (Match != null && Match.LoggedMilestones.Add($"{Team.Value}/{Lane}/{Type.Value}/{_step}"))
                Debug.Log($"[Minion] Match {Match.Id}: {Describe()} {what}");
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
            bool wantsToClose = false;

            bool stunned = Time.time < _stunUntil;
            if (Match.State.IsLive && !stunned)
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
                    if (Vector2.Distance(pos, aimPoint) <= S.Range)
                    {
                        if (Time.time >= _nextAttack)
                        {
                            _nextAttack = Time.time + S.Cooldown;
                            _target.ApplyDamage(S.Damage, DamageSource.FromTeam(Team.Value));
                            if (_target is Nexus && Match.LoggedMilestones.Add($"{Team.Value}/{Lane}/{Type.Value}/nexus"))
                                Debug.Log($"[Minion] Match {Match.Id}: {Describe()} first hit the enemy nexus from {pos}");
                            ObserversAttack(pos, hb.center, Type.Value == MinionType.Melee);
                        }
                    }
                    else
                    {
                        wantsToClose = true;
                        if (Mathf.Abs(aimPoint.x - pos.x) > 0.05f)
                            input.X = Mathf.Sign(aimPoint.x - pos.x);
                    }
                }
                else
                    input = FollowRoute(pos);
            }

            input.X = KeepSpacing(pos, input.X, out bool blocked);
            // Only full-speed moves count (backing off to make space doesn't).
            _moveDir = Mathf.Abs(input.X) >= 0.99f ? Mathf.Sign(input.X) : 0f;
            if (Time.time < _slowUntil)
                input.X *= Abilities.SlowFactor;

            if (Flying)
            {
                if (!_climbing)
                    ChooseSlot(pos, wantsToClose, blocked);
                // Hold position while climbing a hatch column or settling into a new slot.
                bool settling = Mathf.Abs(pos.y - SlotY(_slot)) > SlotSettle + BobAmplitude;
                Fly(pos, _climbing || settling ? 0f : input.X, dt);
                return;
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

        /// <summary>
        /// Queue behind teammates ahead of us in our direction of travel (or toward the enemy when
        /// standing still): don't advance into one within Spacing, and step back if we're
        /// overlapping one. Exact ties are broken by object id so two overlapping minions don't both
        /// wait, and a teammate coming toward us is ignored so we don't block each other forever.
        /// </summary>
        private float KeepSpacing(Vector2 pos, float x, out bool blocked)
        {
            blocked = false;
            float dir = Mathf.Abs(x) > 0.01f ? Mathf.Sign(x) : -LaneLayout.Side(Team.Value);
            foreach (Minion o in Match.Minions)
            {
                if (o == null || o == this || o.Team.Value != Team.Value || o.Health.Value <= 0 || !InMyQueue(o))
                    continue;
                Vector2 op = o.transform.position;
                float ahead = (op.x - pos.x) * dir;
                if (Mathf.Abs(ahead) >= Spacing || o._moveDir == -dir)
                    continue;
                bool otherLeads = ahead > 0.05f || (Mathf.Abs(ahead) <= 0.05f && o.ObjectId < ObjectId);
                if (!otherLeads)
                    continue;
                if (x * dir > 0f)
                {
                    x = 0f;
                    blocked = true;
                }
                if (x == 0f && ahead < Spacing * BackOff)
                    x = -dir * 0.5f;
            }
            return x;
        }

        /// <summary>Teammates we queue behind: flyers in our height slot over our lane; walkers on our level (melee ignore ranged).</summary>
        private bool InMyQueue(Minion o)
        {
            if (Flying != o.Flying)
                return false;
            if (Flying)
                return !_climbing && !o._climbing && Mathf.Approximately(o._levelY, _levelY) && o._slot == _slot;
            if (Type.Value == MinionType.Melee && o.Type.Value == MinionType.Ranged)
                return false;
            return Mathf.Abs(o.transform.position.y - transform.position.y) <= 1f;
        }

        #region Flying
        private float SlotY(int slot) => _levelY + SlotHeights[slot];

        /// <summary>
        /// Pick a height slot. Held up short of our target by a flyer ahead, or unable to reach the
        /// target vertically from here: move to the nearest free slot that can reach it. With
        /// nothing to chase, drift back to the default slot when it's free.
        /// </summary>
        private void ChooseSlot(Vector2 pos, bool wantsToClose, bool blocked)
        {
            if (wantsToClose && _target != null)
            {
                Rect hb = _target.Hitbox;
                bool reachHere = CanReachFrom(SlotY(_slot), hb);
                if (!blocked && reachHere)
                    return;
                float dir = _moveDir != 0f ? _moveDir : -LaneLayout.Side(Team.Value);
                int best = -1;
                for (int c = 0; c < SlotHeights.Length; c++)
                {
                    if (c == _slot || !CanReachFrom(SlotY(c), hb) || !SlotFree(c, pos.x, dir) || !PathFree(_slot, c, pos.x))
                        continue;
                    if (best < 0 || Mathf.Abs(c - _slot) < Mathf.Abs(best - _slot))
                        best = c;
                }
                if (best >= 0)
                {
                    if (blocked && Match.LoggedMilestones.Add($"{Team.Value}/{Lane}/pass"))
                        Debug.Log($"[Minion] Match {Match.Id}: {Describe()} changed height (slot {_slot} -> {best}) to pass a teammate, chasing a {(_target is Minion m ? m.Type.Value.ToString() : _target.Kind.ToString())}");
                    _slot = best;
                }
                return;
            }

            if (_target == null && _slot != DefaultSlot && !blocked
                && SlotFree(DefaultSlot, pos.x, -LaneLayout.Side(Team.Value)) && PathFree(_slot, DefaultSlot, pos.x))
                _slot = DefaultSlot;
        }

        /// <summary>Can a flyer at this height get within range of the target by moving sideways?</summary>
        private bool CanReachFrom(float y, Rect hb) =>
            Mathf.Abs(Mathf.Clamp(y, hb.yMin, hb.yMax) - y) <= S.Range - 0.2f;

        /// <summary>No teammate flyer in that slot just behind us or within two spacings ahead.</summary>
        private bool SlotFree(int slot, float x, float dir)
        {
            foreach (Minion o in Match.Minions)
            {
                if (o == null || o == this || !o.Flying || o.Team.Value != Team.Value || o._climbing
                    || !Mathf.Approximately(o._levelY, _levelY) || o._slot != slot)
                    continue;
                float ahead = (o.transform.position.x - x) * dir;
                if (ahead > -S.Size.x && ahead < Spacing * 2f)
                    return false;
            }
            return true;
        }

        /// <summary>Slots strictly between from and to have no teammate right beside us (we'd pass through it).</summary>
        private bool PathFree(int from, int to, float x)
        {
            int step = to > from ? 1 : -1;
            for (int s = from + step; s != to; s += step)
            {
                foreach (Minion o in Match.Minions)
                    if (o != null && o != this && o.Flying && o.Team.Value == Team.Value && !o._climbing
                        && Mathf.Approximately(o._levelY, _levelY) && o._slot == s
                        && Mathf.Abs(o.transform.position.x - x) < S.Size.x)
                        return false;
            }
            return true;
        }

        private void Fly(Vector2 pos, float x, float dt)
        {
            float slotY = SlotY(_climbing ? DefaultSlot : _slot);
            float targetY = slotY;
            float speedY = ClimbSpeed;
            if (!_climbing)
            {
                targetY += BobAmplitude * Mathf.Sin(_bobPhase + Time.time * BobSpeed);
                if (Mathf.Abs(pos.y - slotY) <= BobAmplitude + 0.2f)
                    speedY = BobFollowSpeed;
            }
            pos.x += Mathf.Clamp(x, -1f, 1f) * FlySpeed * dt;
            pos.y = Mathf.MoveTowards(pos.y, targetY, speedY * dt);
            float maxX = Mathf.Approximately(_levelY, Arena.BaseFloorY) ? Arena.FlyerMaxX : Arena.HalfWidth - 1f;
            pos.x = Mathf.Clamp(pos.x, -maxX, maxX);
            transform.position = pos;
        }
        #endregion

        private bool StillValid(IDamageable t, Vector2 pos)
        {
            if (t == null || (t is Object o && o == null) || !t.Attackable)
                return false;
            Rect hb = t.Hitbox;
            return DamageMath.DistanceTo(hb, pos) <= SightRange * 1.2f && SameLevel(hb, pos) && Reachable(hb, pos);
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
                if (d > SightRange || !SameLevel(hb, pos) || !Reachable(hb, pos) || !ClearShot(pos, t, hb))
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

        /// <summary>
        /// Walkers can only close the horizontal gap, so the target must be within range vertically
        /// (melee can't hit flyers). Flyers can change slot, so check from any slot.
        /// </summary>
        private bool Reachable(Rect hb, Vector2 pos)
        {
            if (!Flying)
                return Mathf.Abs(DamageMath.ClosestPoint(hb, pos).y - pos.y) <= S.Range;
            for (int c = 0; c < SlotHeights.Length; c++)
                if (CanReachFrom(SlotY(c), hb))
                    return true;
            return false;
        }

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
                if (source.Player != null)
                    source.Player.AwardBounty(Shop.MinionBounty, transform.position);
                ObserversDie(transform.position);
                Despawn();
            }
        }
        #endregion

        /// <summary>Visual only: a zap for ranged attacks, a short thick swipe for melee.</summary>
        [ObserversRpc]
        private void ObserversAttack(Vector2 from, Vector2 to, bool melee)
        {
            Color c = Color.Lerp(PlayerAvatar.TeamColor(Team.Value), Color.white, 0.6f);
            if (melee)
                Fx.Tracer(from, Vector2.MoveTowards(from, to, 1.2f), c, 0.1f, 0.18f);
            else
                Fx.Tracer(from, to, c, 0.06f, 0.05f);
        }

        [ObserversRpc]
        private void ObserversDie(Vector2 at)
        {
            Fx.Explosion(at, 0.6f);
        }
    }
}
