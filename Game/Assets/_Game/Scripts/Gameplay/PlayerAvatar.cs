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
    public class PlayerAvatar : NetworkBehaviour
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

        public PositionHistory History { get; private set; }
        public PlatformerMotor Motor { get; private set; }
        public PlayerWeapons Weapons { get; private set; }
        /// <summary>Server only: the match this avatar was spawned into.</summary>
        public Match Match { get; set; }
        public Vector2 HitboxSize => _hitboxSize;

        /// <summary>Overrides keyboard input when set (smoke-test driver).</summary>
        public MotorInput? ScriptedInput;

        private Coroutine _flash;

        public Rect HitboxAt(Vector2 center) => new(center - _hitboxSize * 0.5f, _hitboxSize);

        public float RespawnSecondsRemaining =>
            IsDead.Value ? Mathf.Max(0f, (float)((long)RespawnTick.Value - TimeManager.Tick) * (float)TimeManager.TickDelta) : 0f;

        /// <summary>
        /// Physics scene of the arena this avatar lives in. On the server that's the avatar's own
        /// (stacked) scene; a client only ever has one arena loaded.
        /// </summary>
        public PhysicsScene2D ArenaPhysics =>
            gameObject.scene.name == NetConfig.ArenaScene
                ? gameObject.scene.GetPhysicsScene2D()
                : UnityEngine.SceneManagement.SceneManager.GetSceneByName(NetConfig.ArenaScene).GetPhysicsScene2D();

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
            TimeManager.OnTick += ServerTick;
        }

        public override void OnStopServer()
        {
            TimeManager.OnTick -= ServerTick;
        }

        public override void OnStartClient()
        {
            Team.OnChange += OnTeamChanged;
            IsDead.OnChange += OnDeadChanged;
            ApplyVisuals();
            if (IsOwner && Camera.main != null && Camera.main.TryGetComponent(out CameraFollow follow))
                follow.Target = transform;
        }

        public override void OnStopClient()
        {
            Team.OnChange -= OnTeamChanged;
            IsDead.OnChange -= OnDeadChanged;
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
            if (!IsOwner || IsDead.Value)
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
        [Server]
        public void TakeDamage(int amount)
        {
            if (IsDead.Value || amount <= 0)
                return;
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
            // Movement is client-authoritative, so the owner performs the teleport.
            TargetTeleport(Owner, spawn);
        }

        [TargetRpc]
        private void TargetTeleport(NetworkConnection conn, Vector2 position)
        {
            Motor.Teleport(position);
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
