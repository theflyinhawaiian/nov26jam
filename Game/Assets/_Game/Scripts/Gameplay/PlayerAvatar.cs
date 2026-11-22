using System.Collections;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Jam.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Jam.Gameplay
{
    /// <summary>
    /// Player avatar: identity, health/death/respawn (server-owned), and client-authoritative
    /// platformer movement (PlatformerMotor + NetworkTransform). Weapons live in PlayerWeapons.
    /// </summary>
    [RequireComponent(typeof(PositionHistory), typeof(PlatformerMotor), typeof(PlayerWeapons))]
    public class PlayerAvatar : NetworkBehaviour, IDamageable
    {
        public const int MaxHealth = 100;
        public const float RespawnSeconds = 10f;

        public readonly SyncVar<int> Team = new();
        public readonly SyncVar<string> Nickname = new();
        public readonly SyncVar<int> Health = new(MaxHealth);
        public readonly SyncVar<bool> IsDead = new();
        public readonly SyncVar<uint> RespawnTick = new();
        public readonly SyncVar<int> Deaths = new();

        [SerializeField] private SpriteRenderer _body;
        [SerializeField] private Vector2 _hitboxSize = new(1f, 1.5f);

        private static readonly Color[] TeamColors = { new(0.3f, 0.55f, 1f), new(1f, 0.35f, 0.3f) };
        public static Color TeamColor(int team) => TeamColors[Mathf.Clamp(team, 0, TeamColors.Length - 1)];

        /// <summary>Client only: this client's own avatar, and the opponent it observes (a client sees one match).</summary>
        public static PlayerAvatar LocalPlayer { get; private set; }
        public static PlayerAvatar RemotePlayer { get; private set; }
        /// <summary>Client only: server movement corrections received (smoke tests report this).</summary>
        public static int CorrectionsReceived { get; private set; }

        private MovementValidator _movement;

        /// <summary>Server only: Time.time when this player last damaged an enemy player (towers aggro on it).</summary>
        public float LastHitEnemyPlayerAt { get; private set; } = float.NegativeInfinity;

        int IDamageable.TeamId => Team.Value;
        TargetKind IDamageable.Kind => TargetKind.Player;
        bool IDamageable.Attackable => IsSpawned && !IsDead.Value && !InSpawnZone;
        Rect IDamageable.Hitbox => HitboxAt(transform.position);

        public PositionHistory History { get; private set; }
        public PlatformerMotor Motor { get; private set; }
        public PlayerWeapons Weapons { get; private set; }
        /// <summary>Server only: the match this avatar was spawned into.</summary>
        public Match Match { get; set; }
        public Vector2 HitboxSize => _hitboxSize;
        /// <summary>Server only: where a respawn or correction is moving us, until our position arrives.</summary>
        public Vector2? PendingTeleport => _movement?.PendingTeleport;
        /// <summary>This avatar's match state (server: via Match; client: the one match it observes).</summary>
        public MatchState State => Match != null ? Match.State : MatchState.Local;
        /// <summary>False during the countdown and after the match is decided: no moving or firing.</summary>
        public bool MatchLive => State != null && State.IsLive;

        /// <summary>Overrides keyboard input when set (smoke-test driver).</summary>
        public MotorInput? ScriptedInput;

        private Coroutine _flash;

        public Rect HitboxAt(Vector2 center) => new(center - _hitboxSize * 0.5f, _hitboxSize);

        /// <summary>Inside our own team's starting area behind the nexus, where we can't be damaged.</summary>
        public bool InSpawnZone => Arenas.For(gameObject).SpawnZone(Team.Value).Contains(transform.position);

        public float RespawnSecondsRemaining =>
            IsDead.Value ? Mathf.Max(0f, (float)((long)RespawnTick.Value - TimeManager.Tick) * (float)TimeManager.TickDelta) : 0f;

        /// <summary>
        /// Physics scene of the arena this avatar lives in. On the server that's the avatar's own
        /// (stacked) scene; a client only ever has one arena loaded.
        /// </summary>
        public PhysicsScene2D ArenaPhysics =>
            Arenas.IsArena(gameObject.scene)
                ? gameObject.scene.GetPhysicsScene2D()
                : Arenas.LoadedScene().GetPhysicsScene2D();

        private void Awake()
        {
            History = GetComponent<PositionHistory>();
            Motor = GetComponent<PlatformerMotor>();
            Weapons = GetComponent<PlayerWeapons>();
            if (_body != null && _body.sprite == null)
                _body.sprite = Fx.WhiteSprite;
        }

        public override void OnStartServer()
        {
            _movement = new MovementValidator(this);
            TimeManager.OnTick += ServerTick;
            TimeManager.OnPostTick += ServerCheckMovement;
        }

        public override void OnStopServer()
        {
            TimeManager.OnTick -= ServerTick;
            TimeManager.OnPostTick -= ServerCheckMovement;
        }

        public override void OnStartClient()
        {
            Team.OnChange += OnTeamChanged;
            IsDead.OnChange += OnDeadChanged;
            ApplyVisuals();
            if (IsOwner)
                LocalPlayer = this;
            else
                RemotePlayer = this;
            if (IsOwner && Camera.main != null && Camera.main.TryGetComponent(out CameraFollow follow))
                follow.Target = transform;
        }

        public override void OnStopClient()
        {
            Team.OnChange -= OnTeamChanged;
            IsDead.OnChange -= OnDeadChanged;
            if (LocalPlayer == this)
                LocalPlayer = null;
            if (RemotePlayer == this)
                RemotePlayer = null;
        }

        private void OnTeamChanged(int prev, int next, bool asServer) => ApplyVisuals();
        private void OnDeadChanged(bool prev, bool next, bool asServer) => ApplyVisuals();

        private void ApplyVisuals()
        {
            if (_body == null)
                return;
            _body.color = TeamColors[Mathf.Clamp(Team.Value, 0, TeamColors.Length - 1)];
            _body.enabled = !IsDead.Value;
        }

        private void Update()
        {
            if (!IsOwner || IsDead.Value || !MatchLive)
                return;
            Motor.Step(ScriptedInput ?? ReadKeyboard(), Time.deltaTime, ArenaPhysics);
        }

        private static MotorInput ReadKeyboard()
        {
            Keyboard k = Keyboard.current;
            if (k == null)
                return default;
            bool left = k.aKey.isPressed || k.leftArrowKey.isPressed;
            bool right = k.dKey.isPressed || k.rightArrowKey.isPressed;
            return new MotorInput
            {
                X = (right ? 1f : 0f) - (left ? 1f : 0f),
                JumpHeld = k.spaceKey.isPressed || k.wKey.isPressed || k.upArrowKey.isPressed,
                JumpPressed = k.spaceKey.wasPressedThisFrame || k.wKey.wasPressedThisFrame || k.upArrowKey.wasPressedThisFrame,
                DownHeld = k.sKey.isPressed || k.downArrowKey.isPressed,
            };
        }

        #region Health (server)
        /// <summary>Server only. Players take damage from anyone, including their own grenades.</summary>
        public void ApplyDamage(int amount, DamageSource source)
        {
            if (!IsServerStarted || IsDead.Value || amount <= 0 || InSpawnZone)
                return;
            if (source.Player != null && source.Player != this && source.Team != Team.Value)
                source.Player.LastHitEnemyPlayerAt = Time.time;
            Health.Value = Mathf.Max(0, Health.Value - amount);
            ObserversDamageFlash();
            if (Health.Value == 0)
            {
                IsDead.Value = true;
                Deaths.Value++;
                RespawnTick.Value = TimeManager.Tick + TimeManager.TimeToTicks(RespawnSeconds);
            }
        }

        private void ServerTick()
        {
            if (IsDead.Value && TimeManager.Tick >= RespawnTick.Value)
                Respawn();
        }

        [Server]
        private void Respawn()
        {
            Vector2 spawn = Match != null ? Match.SpawnPosition(Team.Value) : (Vector2)transform.position;
            Health.Value = MaxHealth;
            IsDead.Value = false;
            ForceTeleport(spawn, correction: false);
        }
        #endregion

        #region Movement validation (server) and forced teleports
        /// <summary>
        /// Movement is client-authoritative, so the owner performs the teleport. The validator
        /// stops checking until the owner's position arrives there.
        /// </summary>
        [Server]
        public void ForceTeleport(Vector2 position, bool correction)
        {
            _movement?.ExpectTeleport(position, Time.unscaledTime);
            TargetTeleport(Owner, position, correction);
        }

        private void ServerCheckMovement()
        {
            if (_movement == null || Match == null || !Match.SceneLoaded || Owner == null || !Owner.IsActive)
                return;
            var result = _movement.Check(transform.position, (float)TimeManager.TickDelta, Time.unscaledTime, Match.Physics, out Vector2 correction);
            if (result == MovementValidator.Result.Ok)
                return;
            if (result == MovementValidator.Result.Violation)
                Debug.Log($"[Movement] Correcting {Nickname.Value} (match {Match.Id}, #{_movement.Corrections}): {_movement.LastFailure}");
            TargetTeleport(Owner, correction, true);
        }

        [TargetRpc]
        private void TargetTeleport(NetworkConnection conn, Vector2 position, bool correction)
        {
            Motor.Teleport(position);
            if (correction)
                CorrectionsReceived++;
        }

        /// <summary>
        /// Debug helper (DebugHud's T key): ask the server to move us. Only honoured by editor
        /// and development-build servers, never the release server.
        /// </summary>
        [ServerRpc]
        public void ServerDebugTeleport(Vector2 position)
        {
            if (!Application.isEditor && !Debug.isDebugBuild)
            {
                Debug.Log($"[Movement] Ignored debug teleport from {Nickname.Value}: release server.");
                return;
            }
            if (!IsDead.Value && Arenas.For(gameObject).Bounds.Contains(position))
                ForceTeleport(position, correction: false);
        }
        #endregion

        [ObserversRpc]
        private void ObserversDamageFlash()
        {
            if (_body == null || !isActiveAndEnabled)
                return;
            if (_flash != null)
                StopCoroutine(_flash);
            _flash = StartCoroutine(Flash());
        }

        private IEnumerator Flash()
        {
            _body.color = Color.white;
            yield return new WaitForSeconds(0.08f);
            ApplyVisuals();
            _flash = null;
        }
    }
}
