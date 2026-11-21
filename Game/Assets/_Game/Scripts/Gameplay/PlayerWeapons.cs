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
        /// <summary>A fresh grenade can't hit its thrower (it spawns inside their hitbox).</summary>
        private const float SelfHitGraceSeconds = 0.15f;

        private PlayerAvatar _avatar;
        private float[] _nextFireLocal;
        private float[] _nextFireServer;
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
            _nextFireServer = new float[_weapons.Length];
        }

        public override void OnStartServer() => TimeManager.OnTick += SimulateProjectiles;
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

            Keyboard k = Keyboard.current;
            if (k != null)
            {
                if (k.digit1Key.wasPressedThisFrame) Select(0);
                if (k.digit2Key.wasPressedThisFrame) Select(1);
                if (k.qKey.wasPressedThisFrame) Select((CurrentIndex + 1) % _weapons.Length);
            }

            Mouse mouse = Mouse.current;
            Camera cam = Camera.main;
            if (mouse != null && cam != null && mouse.leftButton.isPressed && !DebugHud.PointerOverHud)
                FireAt(cam.ScreenToWorldPoint(mouse.position.ReadValue()));
        }

        public void Select(int index)
        {
            if (index >= 0 && index < _weapons.Length)
                CurrentIndex = index;
        }

        /// <summary>Owner only. Public so the smoke-test driver can shoot without input devices.</summary>
        public void FireAt(Vector2 worldPoint)
        {
            if (!IsOwner || _avatar.IsDead.Value)
                return;
            int index = CurrentIndex;
            WeaponDefinition w = _weapons[index];
            if (Time.time < _nextFireLocal[index])
                return;

            Vector2 origin = transform.position;
            Vector2 aim = worldPoint - origin;
            if (aim.sqrMagnitude < 1e-4f)
                return;
            aim.Normalize();
            _nextFireLocal[index] = Time.time + w.Cooldown;

            PreciseTick pt = TimeManager.GetPreciseTick(TickType.LastPacketTick);
            if (w.Kind == WeaponKind.Hitscan)
                FireHitscanLocal((byte)index, w, origin, aim, pt);
            else
                FireProjectileLocal((byte)index, w, origin, aim, pt);
        }

        /// <summary>Shared server-side checks. May correct the origin to the server's view of the shooter.</summary>
        private bool ServerValidateFire(byte index, WeaponKind kind, ref Vector2 origin, out WeaponDefinition weapon, out Match match)
        {
            weapon = null;
            match = _avatar.Match;
            if (match == null || match.Ended || _avatar.IsDead.Value || index >= _weapons.Length)
                return false;
            weapon = _weapons[index];
            if (weapon.Kind != kind)
                return false;
            // Allow some jitter between client and server clocks.
            if (Time.time < _nextFireServer[index])
                return false;
            _nextFireServer[index] = Time.time + weapon.Cooldown * 0.8f;
            // The shot has to start roughly where the server believes the shooter is.
            if (Vector2.Distance(origin, transform.position) > 2f)
                origin = transform.position;
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

            if (result.Target != null)
                result.Target.TakeDamage(w.Damage);
            else if (result.Terrain != null && match.State != null && match.TryGetNexus(result.Terrain, out Nexus nexus))
                match.State.DamageNexus(nexus.Team, w.Damage);

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

            TargetFireDiagnostics(Owner, result.Target != null, rewound, error, bestOffset, bestError, (int)(TimeManager.Tick - pt.Tick));
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
            _visuals[id] = GrenadeVisual.Spawn(origin, aim * w.LaunchSpeed, w, 0f, _avatar.ArenaPhysics);
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
            _visuals[id] = GrenadeVisual.Spawn(origin, velocity, _weapons[index], age, _avatar.ArenaPhysics);
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
                PlayerAvatar ignore = p.Age < SelfHitGraceSeconds ? _avatar : null;

                bool expired = p.Age >= w.MaxLifetime;
                if (LagCompensation.Sweep(match, p.LastPosition, next, w.ProjectileRadius, ignore, out Vector2 impact) || expired)
                {
                    ObserversExplode(p.Id, impact, w.ExplosionRadius);
                    if (!match.Ended)
                        Combat.Explode(match, impact, w);
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
            Fx.Explosion(point, radius);
        }
        #endregion
    }
}
