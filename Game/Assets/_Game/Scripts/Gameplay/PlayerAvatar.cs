using FishNet.Connection;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Jam.Net;
using Jam.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Jam.Gameplay
{
    /// <summary>
    /// M1 spike avatar: client-authoritative free movement (NetworkTransform), and a
    /// hitscan rifle resolved on the server against rewound positions.
    /// </summary>
    [RequireComponent(typeof(PositionHistory))]
    public class PlayerAvatar : NetworkBehaviour
    {
        public readonly SyncVar<int> Team = new();
        public readonly SyncVar<string> Nickname = new();
        public readonly SyncVar<int> TimesHit = new();

        [SerializeField] private SpriteRenderer _body;
        [SerializeField] private Vector2 _hitboxSize = new(1f, 1.5f);
        [SerializeField] private float _moveSpeed = 8f;
        [SerializeField] private float _rifleRange = 24f;
        [SerializeField] private float _rifleCooldown = 0.2f;

        // M1 arena is a 40x22 box centred on the origin; real collision arrives in M2.
        private static readonly Vector2 ArenaHalfExtents = new(19.5f, 10.5f);
        private static readonly Color[] TeamColors = { new(0.3f, 0.55f, 1f), new(1f, 0.35f, 0.3f) };

        public PositionHistory History { get; private set; }
        public Vector2 HitboxSize => _hitboxSize;

        private float _nextFireTime;
        private float _serverNextFireTime;

        public Rect HitboxAt(Vector2 center) => new(center - _hitboxSize * 0.5f, _hitboxSize);

        private void Awake()
        {
            History = GetComponent<PositionHistory>();
            if (_body != null && _body.sprite == null)
                _body.sprite = Fx.WhiteSprite;
        }

        public override void OnStartClient()
        {
            Team.OnChange += OnTeamChanged;
            ApplyTeamColor();
            if (IsOwner && Camera.main != null && Camera.main.TryGetComponent(out CameraFollow follow))
                follow.Target = transform;
        }

        public override void OnStopClient()
        {
            Team.OnChange -= OnTeamChanged;
        }

        private void OnTeamChanged(int prev, int next, bool asServer) => ApplyTeamColor();

        private void ApplyTeamColor()
        {
            if (_body != null)
                _body.color = TeamColors[Mathf.Clamp(Team.Value, 0, TeamColors.Length - 1)];
        }

        private void Update()
        {
            if (!IsOwner)
                return;
            Move();
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.isPressed && !DebugHud.PointerOverHud)
                Fire(mouse);
        }

        /// <summary>Overrides keyboard input when set (smoke-test driver).</summary>
        public Vector2? ScriptedInput;

        private void Move()
        {
            Vector2 input = ScriptedInput ?? ReadKeyboard();
            Vector2 p = (Vector2)transform.position + Vector2.ClampMagnitude(input, 1f) * (_moveSpeed * Time.deltaTime);
            transform.position = Vector2.Max(-ArenaHalfExtents, Vector2.Min(ArenaHalfExtents, p));
        }

        private static Vector2 ReadKeyboard()
        {
            Keyboard k = Keyboard.current;
            if (k == null)
                return Vector2.zero;
            return new Vector2(
                (k.dKey.isPressed ? 1 : 0) - (k.aKey.isPressed ? 1 : 0),
                (k.wKey.isPressed ? 1 : 0) - (k.sKey.isPressed ? 1 : 0)).normalized;
        }

        #region Rifle
        private void Fire(Mouse mouse)
        {
            Camera cam = Camera.main;
            if (cam != null)
                FireAt(cam.ScreenToWorldPoint(mouse.position.ReadValue()));
        }

        /// <summary>Owner only. Public so the smoke-test driver can shoot without input devices.</summary>
        public void FireAt(Vector2 worldPoint)
        {
            if (!IsOwner || Time.time < _nextFireTime)
                return;
            _nextFireTime = Time.time + _rifleCooldown;

            Vector2 origin = transform.position;
            Vector2 aim = worldPoint - origin;
            if (aim.sqrMagnitude < 1e-4f)
                return;
            aim.Normalize();

            // Immediate local tracer (terrain only); the server decides what was actually hit.
            PhysicsScene2D physics = UnityEngine.SceneManagement.SceneManager.GetSceneByName(NetConfig.ArenaScene).GetPhysicsScene2D();
            RaycastHit2D hit = physics.Raycast(origin, aim, _rifleRange, Physics2D.DefaultRaycastLayers);
            Fx.Tracer(origin, hit.collider != null ? hit.point : origin + aim * _rifleRange, Color.yellow);

            // M1 diagnostics: report where we currently see the opponent so the server can
            // measure how well its rewind matches. Not used for hit decisions.
            PlayerAvatar opponent = FindOpponent();
            Vector2 seen = opponent != null ? (Vector2)opponent.transform.position : Vector2.zero;
            if (opponent != null)
                Fx.Ghost(seen, _hitboxSize, new Color(0.2f, 1f, 0.2f, 0.5f));

            ServerFire(origin, aim, TimeManager.GetPreciseTick(TickType.LastPacketTick), seen, opponent != null);
        }

        /// <summary>Client side only.</summary>
        public PlayerAvatar FindOpponent()
        {
            // Observers ensure a client only knows about avatars in its own match.
            foreach (PlayerAvatar a in FindObjectsByType<PlayerAvatar>(FindObjectsSortMode.None))
                if (a != this)
                    return a;
            return null;
        }

        [ServerRpc]
        private void ServerFire(Vector2 origin, Vector2 direction, PreciseTick pt, Vector2 seenOpponent, bool hasSeen)
        {
            // Allow a little jitter between client and server clocks.
            if (Time.time < _serverNextFireTime)
                return;
            _serverNextFireTime = Time.time + _rifleCooldown * 0.8f;

            MatchManager mm = NetworkManager.GetComponent<MatchManager>();
            if (mm == null || !mm.TryGetMatch(gameObject.scene, out Match match))
                return;

            // The shot has to start roughly where the server believes the shooter is.
            if (Vector2.Distance(origin, transform.position) > 2f)
                origin = transform.position;

            double shooterTick = pt.Tick + pt.PercentAsDouble;
            double rewindTick = shooterTick - mm.RewindOffsetTicks;
            HitscanResult result = LagCompensation.Raycast(match, this, origin, direction, _rifleRange, rewindTick);
            if (result.Target != null)
                result.Target.TimesHit.Value++;

            ObserversTracer(origin, result.EndPoint);

            if (hasSeen)
                SendDiagnostics(match, seenOpponent, shooterTick, rewindTick, result, pt);
        }

        private void SendDiagnostics(Match match, Vector2 seenOpponent, double shooterTick, double rewindTick, HitscanResult result, PreciseTick pt)
        {
            PlayerAvatar opponent = null;
            foreach (PlayerAvatar a in match.Avatars)
                if (a != null && a != this)
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
            Fx.Ghost(rewound, _hitboxSize, hit ? new Color(1f, 0.2f, 0.2f, 0.6f) : new Color(1f, 1f, 1f, 0.4f));
            FireDiagnostics.Record(hit, error, bestOffset, bestError, ticksBehind);
        }
        #endregion
    }
}
