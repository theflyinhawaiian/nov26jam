using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing.Timing;
using FishNet.Object;
using Jam.Net;
using Jam.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Jam.Gameplay
{
    /// <summary>
    /// Weapon handling. The owner fires immediately for feel; the server validates and decides:
    /// - Hitscan: rewound against PositionHistory (see LagCompensation).
    /// - Projectile: simulated on the server from (origin, velocity), fast-forwarded by the
    ///   shooter's latency; other clients simulate the same arc for visuals only.
    /// </summary>
    [RequireComponent(typeof(PlayerAvatar))]
    public class PlayerWeapons : NetworkBehaviour
    {
        [SerializeField] private WeaponDefinition[] _weapons;

        /// <summary>Cap on how far the server fast-forwards a projectile to cover latency.</summary>
        private const float MaxCatchUpSeconds = 0.25f;
        /// <summary>
        /// Observers play a late grenade's arc from the start on a fast clock: catch-up time is
        /// lateness × factor, clamped. Factor 3 means 1.33x speed; the clamp caps it near 1.5x.
        /// </summary>
        private const float VisualCatchUpFactor = 3f;
        private const float MinVisualCatchUp = 0.3f;
        private const float MaxVisualCatchUp = 1f;
        /// <summary>
        /// Server fire-rate check: each weapon banks up to (cooldown + this) seconds of credit
        /// and each shot costs one cooldown, so shots bunched by network jitter still pass but
        /// the long-run rate can't beat the cooldown.
        /// </summary>
        private const float FireBurstSeconds = 0.5f;
        /// <summary>
        /// A shot may start this far from where the server has the shooter (plus a speed term).
        /// Generous because a pad launch outruns the speed estimate; the wall check is what stops
        /// shooting from the far side of terrain.
        /// </summary>
        private const float MaxOriginOffset = 4f;
        /// <summary>Extra origin slack per unit of shooter speed (seconds of travel).</summary>
        private const float OriginLagSeconds = 0.25f;

        private PlayerAvatar _avatar;
        private float[] _nextFireLocal;
        private float[] _fireCredit;
        private float[] _fireCreditAt;
        /// <summary>Server only: shots refused (cooldown) or moved (origin), for logs and smoke tests.</summary>
        public int RejectedShots { get; private set; }
        public int CorrectedOrigins { get; private set; }
        private ushort _nextProjectileId;

        private struct ServerProjectile
        {
            public ushort Id;
            public WeaponDefinition Weapon;
            public Vector2 Origin;
            public Vector2 Velocity;
            public Vector2 LastPosition;
            public float Age;
        }

        private readonly List<ServerProjectile> _serverProjectiles = new();
        private readonly Dictionary<ushort, GrenadeVisual> _visuals = new();

        public int CurrentIndex { get; private set; }
        public int Count => _weapons.Length;
        public WeaponDefinition Current => _weapons[CurrentIndex];
        public WeaponDefinition Get(int index) => _weapons[index];
        public float CooldownRemaining(int index) => Mathf.Max(0f, _nextFireLocal[index] - Time.time);

        private void Awake()
        {
            _avatar = GetComponent<PlayerAvatar>();
            _nextFireLocal = new float[_weapons.Length];
            _fireCredit = new float[_weapons.Length];
            _fireCreditAt = new float[_weapons.Length];
        }

        public override void OnStartServer()
        {
            for (int i = 0; i < _weapons.Length; i++)
            {
                _fireCredit[i] = _weapons[i].Cooldown + FireBurstSeconds;
                _fireCreditAt[i] = Time.time;
            }
            TimeManager.OnTick += SimulateProjectiles;
        }

        public override void OnStopServer() => TimeManager.OnTick -= SimulateProjectiles;

        public override void OnStopClient()
        {
            foreach (GrenadeVisual v in _visuals.Values)
                if (v != null)
                    Destroy(v.gameObject);
            _visuals.Clear();
        }

        private void Update()
        {
            if (!IsOwner || _avatar.IsDead.Value || _avatar.ScriptedInput.HasValue)
                return;

            Mouse mouse = Mouse.current;
            Camera cam = Camera.main;
            if (mouse != null && cam != null && mouse.leftButton.isPressed && !DebugHud.PointerOverHud && !GameMenu.CapturesPointer)
                FireAt(cam.ScreenToWorldPoint(mouse.position.ReadValue()));
        }

        public void Select(int index)
        {
            if (index >= 0 && index < _weapons.Length)
                CurrentIndex = index;
        }

        /// <summary>Owner only: fire the selected weapon. Public so the smoke-test driver can shoot without input devices.</summary>
        public void FireAt(Vector2 worldPoint) => FireAt(worldPoint, CurrentIndex);

        /// <summary>
        /// Owner only: fire a weapon slot. Slot 0 is the gun (left mouse); slot 1 is the grenade,
        /// fired only by the Grenade Q ability (PlayerAbilities). Returns whether it fired.
        /// </summary>
        public bool FireAt(Vector2 worldPoint, int index)
        {
            if (!IsOwner || _avatar.IsDead.Value || !_avatar.MatchLive || _avatar.Stunned || index < 0 || index >= _weapons.Length)
                return false;
            WeaponDefinition w = _weapons[index];
            if (Time.time < _nextFireLocal[index])
                return false;
            if (index == PlayerAbilities.GrenadeWeaponIndex && !GrenadeAllowed())
                return false;

            Vector2 origin = transform.position;
            Vector2 aim = worldPoint - origin;
            if (aim.sqrMagnitude < 1e-4f)
                return false;
            aim.Normalize();
            _nextFireLocal[index] = Time.time + w.Cooldown;

            PreciseTick pt = TimeManager.GetPreciseTick(TickType.LastPacketTick);
            if (w.Kind == WeaponKind.Hitscan)
                FireHitscanLocal((byte)index, w, origin, aim, pt);
            else
                FireProjectileLocal((byte)index, w, origin, aim, pt);
            return true;
        }

        /// <summary>The grenade is the Grenade ability: only while we hold it and aren't silenced.</summary>
        private bool GrenadeAllowed() =>
            _avatar.Abilities != null && _avatar.Abilities.Current.Value == AbilityId.Grenade && !_avatar.Silenced;

        /// <summary>Shared server-side checks. May correct the origin to the server's view of the shooter.</summary>
        private bool ServerValidateFire(byte index, WeaponKind kind, ref Vector2 origin, out WeaponDefinition weapon, out Match match)
        {
            weapon = null;
            match = _avatar.Match;
            if (match == null || match.Ended || match.State == null || !match.State.IsLive || _avatar.IsDead.Value || index >= _weapons.Length
                || _avatar.Stunned || (index == PlayerAbilities.GrenadeWeaponIndex && !GrenadeAllowed()))
                return false;
            weapon = _weapons[index];
            if (weapon.Kind != kind)
                return false;

            float credit = Mathf.Min(weapon.Cooldown + FireBurstSeconds, _fireCredit[index] + (Time.time - _fireCreditAt[index]));
            _fireCreditAt[index] = Time.time;
            // 10% slack for clock jitter; the debt carries over, so it doesn't raise the rate.
            if (credit < weapon.Cooldown * 0.9f)
            {
                _fireCredit[index] = credit;
                RejectedShots++;
                Debug.Log($"[Weapons] {_avatar.Nickname.Value}: {weapon.DisplayName} shot refused, cooldown " +
                          $"({credit:0.000}s of {weapon.Cooldown:0.000}s banked, {RejectedShots} refused so far)");
                return false;
            }
            _fireCredit[index] = credit - weapon.Cooldown;

            // The shot has to start roughly where the server believes the shooter is, and not
            // from the far side of a wall.
            // The server's copy of a client-authoritative position trails the client's by
            // latency plus interpolation, so allow more slack the faster the shooter is moving.
            // Right after a respawn or correction the server still has the old position, so
            // measure from where the teleport is sending us instead.
            Vector2 server = _avatar.PendingTeleport ?? (Vector2)transform.position;
            const uint speedTicks = 3;
            Vector2 earlier = _avatar.History.GetPosition(TimeManager.Tick - speedTicks);
            float speed = Vector2.Distance(transform.position, earlier) / (float)(speedTicks * TimeManager.TickDelta);
            float allowed = MaxOriginOffset + speed * OriginLagSeconds;
            bool tooFar = Vector2.Distance(origin, server) > allowed;
            if (tooFar || match.Physics.Linecast(server, origin, Layers.GroundMask).collider != null)
            {
                CorrectedOrigins++;
                Debug.Log($"[Weapons] {_avatar.Nickname.Value}: shot origin {origin} moved to {server} " +
                          $"({(tooFar ? "too far" : "behind a wall")}, {CorrectedOrigins} so far)");
                origin = server;
            }
            return true;
        }

        #region Hitscan
        private void FireHitscanLocal(byte index, WeaponDefinition w, Vector2 origin, Vector2 aim, PreciseTick pt)
        {
            RaycastHit2D hit = _avatar.ArenaPhysics.Raycast(origin, aim, w.Range, Layers.GroundMask);
            Fx.Tracer(origin, hit.collider != null ? hit.point : origin + aim * w.Range, Color.yellow);

            // Rewind diagnostics: report where we currently see the opponent. Not used for hit decisions.
            PlayerAvatar opponent = FindOpponent();
            Vector2 seen = opponent != null ? (Vector2)opponent.transform.position : Vector2.zero;
            if (opponent != null && FireDiagnostics.Enabled)
                Fx.Ghost(seen, _avatar.HitboxSize, new Color(0.2f, 1f, 0.2f, 0.5f));

            ServerFireHitscan(index, origin, aim, pt, seen, opponent != null);
        }

        [ServerRpc]
        private void ServerFireHitscan(byte index, Vector2 origin, Vector2 direction, PreciseTick pt, Vector2 seenOpponent, bool hasSeen)
        {
            if (!ServerValidateFire(index, WeaponKind.Hitscan, ref origin, out WeaponDefinition w, out Match match))
                return;

            MatchManager mm = NetworkManager.GetComponent<MatchManager>();
            double shooterTick = pt.Tick + pt.PercentAsDouble;
            double rewindTick = shooterTick - mm.RewindOffsetTicks;
            HitscanResult result = LagCompensation.Raycast(match, _avatar, origin, direction, w.Range, rewindTick);

            result.Target?.ApplyDamage(Mathf.RoundToInt(w.Damage * _avatar.DamageMultiplier), DamageSource.FromPlayer(_avatar));

            ObserversTracer(origin, result.EndPoint);

            if (hasSeen)
                SendDiagnostics(match, seenOpponent, shooterTick, rewindTick, result, pt);
        }

        private void SendDiagnostics(Match match, Vector2 seenOpponent, double shooterTick, double rewindTick, HitscanResult result, PreciseTick pt)
        {
            PlayerAvatar opponent = null;
            foreach (PlayerAvatar a in match.Avatars)
                if (a != null && a != _avatar)
                    opponent = a;
            if (opponent == null)
                return;

            Vector2 rewound = opponent.History.GetPosition(rewindTick);
            float error = Vector2.Distance(rewound, seenOpponent);

            // Which rewind offset would have best reproduced what the client saw?
            float bestOffset = 0f;
            float bestError = float.MaxValue;
            for (float offset = -6f; offset <= 6f; offset += 0.5f)
            {
                float e = Vector2.Distance(opponent.History.GetPosition(shooterTick - offset), seenOpponent);
                if (e < bestError)
                {
                    bestError = e;
                    bestOffset = offset;
                }
            }

            TargetFireDiagnostics(Owner, result.Target is PlayerAvatar, rewound, error, bestOffset, bestError, (int)(TimeManager.Tick - pt.Tick));
        }

        [ObserversRpc(ExcludeOwner = true)]
        private void ObserversTracer(Vector2 from, Vector2 to)
        {
            Fx.Tracer(from, to, Color.yellow);
        }

        [TargetRpc]
        private void TargetFireDiagnostics(NetworkConnection conn, bool hit, Vector2 rewound, float error, float bestOffset, float bestError, int ticksBehind)
        {
            if (FireDiagnostics.Enabled)
                Fx.Ghost(rewound, _avatar.HitboxSize, hit ? new Color(1f, 0.2f, 0.2f, 0.6f) : new Color(1f, 1f, 1f, 0.4f));
            FireDiagnostics.Record(hit, error, bestOffset, bestError, ticksBehind);
        }

        /// <summary>Client side only.</summary>
        public PlayerAvatar FindOpponent()
        {
            // Observers ensure a client only knows about avatars in its own match.
            foreach (PlayerAvatar a in FindObjectsByType<PlayerAvatar>(FindObjectsSortMode.None))
                if (a != _avatar)
                    return a;
            return null;
        }
        #endregion

        #region Projectile
        private void FireProjectileLocal(byte index, WeaponDefinition w, Vector2 origin, Vector2 aim, PreciseTick pt)
        {
            ushort id = _nextProjectileId++;
            _visuals[id] = GrenadeVisual.Spawn(origin, aim * w.LaunchSpeed, w, _avatar.ArenaPhysics);
            ServerFireProjectile(index, id, origin, aim, pt);
        }

        [ServerRpc]
        private void ServerFireProjectile(byte index, ushort id, Vector2 origin, Vector2 direction, PreciseTick pt)
        {
            if (!ServerValidateFire(index, WeaponKind.Projectile, ref origin, out WeaponDefinition w, out Match _))
                return;

            Vector2 velocity = direction.normalized * w.LaunchSpeed;
            // The shooter has been watching their grenade fly for about this long already.
            float catchUp = Mathf.Clamp((float)TimeManager.TimePassed(pt.Tick), 0f, MaxCatchUpSeconds);
            _serverProjectiles.Add(new ServerProjectile
            {
                Id = id,
                Weapon = w,
                Origin = origin,
                Velocity = velocity,
                LastPosition = origin,
                Age = catchUp,
            });
            ObserversSpawnProjectile(index, id, origin, velocity, TimeManager.Tick, catchUp);
        }

        [ObserversRpc(ExcludeOwner = true)]
        private void ObserversSpawnProjectile(byte index, ushort id, Vector2 origin, Vector2 velocity, uint serverTick, float serverAge)
        {
            float age = Mathf.Clamp(serverAge + (float)TimeManager.TimePassed(serverTick), 0f, MaxCatchUpSeconds * 2f);
            // The real grenade is already `age` seconds along its arc. Play it from the start (at the
            // thrower as drawn here) on a slightly fast clock until it catches up. Visual only.
            Vector2 shown = transform.position;
            float catchUp = Mathf.Clamp(age * VisualCatchUpFactor, MinVisualCatchUp, MaxVisualCatchUp);
            if (FireDiagnostics.Enabled || SmokeTestDriver.Requested)
                Debug.Log($"[Grenade] observer spawn: {age:0.000}s behind, thrower shown {Vector2.Distance(shown, origin):0.00}u from true origin; " +
                          $"catching up over {catchUp:0.00}s at {1f + age / catchUp:0.00}x");
            _visuals[id] = GrenadeVisual.Spawn(origin, velocity, _weapons[index], _avatar.ArenaPhysics, age, catchUp, shown);
        }

        private void SimulateProjectiles()
        {
            Match match = _avatar.Match;
            if (match == null || _serverProjectiles.Count == 0)
                return;

            float dt = (float)TimeManager.TickDelta;
            for (int i = _serverProjectiles.Count - 1; i >= 0; i--)
            {
                ServerProjectile p = _serverProjectiles[i];
                p.Age += dt;
                WeaponDefinition w = p.Weapon;
                Vector2 next = Grenade.PositionAt(p.Origin, p.Velocity, w.ProjectileGravity, p.Age);
                // Arming is by distance rather than time: the server fast-forwards fresh grenades by
                // the shooter's latency, and our (client-authoritative) position on the server lags
                // behind where we really are, so a time-based grace can expire while the grenade is
                // still inside our stale hitbox.
                bool armed = Vector2.Distance(p.Origin, p.LastPosition) >= w.ArmDistance;
                PlayerAvatar ignore = armed ? null : _avatar;

                bool expired = p.Age >= w.MaxLifetime;
                if (LagCompensation.Sweep(match, p.LastPosition, next, w.ProjectileRadius, ignore, _avatar.Team.Value, out Vector2 impact) || expired)
                {
                    bool dud = !armed && !expired;
                    ObserversExplode(p.Id, impact, dud ? 0f : w.ExplosionRadius);
                    if (!dud && !match.Ended)
                        Combat.Explode(match, impact, w, DamageSource.FromPlayer(_avatar));
                    _serverProjectiles.RemoveAt(i);
                    continue;
                }

                p.LastPosition = next;
                _serverProjectiles[i] = p;
            }
        }

        [ObserversRpc]
        private void ObserversExplode(ushort id, Vector2 point, float radius)
        {
            if (_visuals.TryGetValue(id, out GrenadeVisual v))
            {
                if (v != null)
                    Destroy(v.gameObject);
                _visuals.Remove(id);
            }
            // Radius 0 = an unarmed dud: just a puff.
            Fx.Explosion(point, radius > 0f ? radius : 0.3f);
        }
        #endregion
    }
}
