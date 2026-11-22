using FishNet.Managing;
using FishNet.Transporting;
using Jam.Gameplay;
using Jam.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Jam.UI
{
    /// <summary>
    /// Player-facing match flow (IMGUI, greybox): connecting → menu (nickname, Find match) →
    /// waiting for an opponent → countdown → play (Esc for the leave menu) → results → menu.
    /// The server drives the state with MatchStatusBroadcast; the countdown comes from MatchState.
    /// </summary>
    [RequireComponent(typeof(NetworkManager), typeof(Bootstrap))]
    public class GameMenu : MonoBehaviour
    {
        public const string Title = "NEXUS DUEL";
        private const float ReferenceHeight = 720f;
        private const float PanelWidth = 440f;
        private const string NicknamePref = "nickname";
        private const float GoBannerSeconds = 0.8f;

        /// <summary>True while a menu is in front of the arena, so clicks don't fire weapons.</summary>
        public static bool CapturesPointer { get; private set; }

        private NetworkManager _nm;
        private Bootstrap _bootstrap;
        private string _nickname;
        private MatchStatus _status = MatchStatus.Idle;
        private string _message = "";
        private MatchOutcome _outcome;
        private string _opponent = "";
        private float _queuedAt;
        private bool _leaveMenuOpen;
        private bool _shopOpen;
        private bool _wasConnected;

        private readonly MatchHud _hud = new();
        private GUIStyle _title, _big, _heading, _label, _small, _button, _field, _panel;

        private void Awake()
        {
            _nm = GetComponent<NetworkManager>();
            _bootstrap = GetComponent<Bootstrap>();
            _nickname = PlayerPrefs.GetString(NicknamePref, "");
            if (string.IsNullOrWhiteSpace(_nickname))
                _nickname = $"Player{Random.Range(100, 999)}";
        }

        private void Start()
        {
            _nm.ClientManager.RegisterBroadcast<MatchStatusBroadcast>(OnMatchStatus);
            _nm.ClientManager.OnClientConnectionState += OnClientConnectionState;
        }

        private void OnDestroy()
        {
            if (_nm == null)
                return;
            _nm.ClientManager.UnregisterBroadcast<MatchStatusBroadcast>(OnMatchStatus);
            _nm.ClientManager.OnClientConnectionState -= OnClientConnectionState;
        }

        private void OnMatchStatus(MatchStatusBroadcast msg, Channel channel)
        {
            _status = msg.Status;
            _message = msg.Message;
            if (msg.Status == MatchStatus.Queued)
                _queuedAt = Time.unscaledTime;
            if (msg.Status == MatchStatus.InMatch)
            {
                _opponent = msg.OpponentName;
                _leaveMenuOpen = false;
                FireDiagnostics.Reset();
            }
            if (msg.Status == MatchStatus.Ended)
                _outcome = msg.Outcome;
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
                _wasConnected = true;
            if (args.ConnectionState != LocalConnectionState.Stopped)
                return;
            if (_status == MatchStatus.InMatch || _status == MatchStatus.Queued)
            {
                // No reconnect into a match: show it as a result so the player knows what happened.
                _status = MatchStatus.Ended;
                _outcome = MatchOutcome.Cancelled;
                _message = "Lost connection to the server.";
            }
            _leaveMenuOpen = false;
        }

        private void Update()
        {
            bool inMatch = _nm.IsClientStarted && _status == MatchStatus.InMatch;
            Keyboard k = Keyboard.current;
            PlayerAvatar me = PlayerAvatar.LocalPlayer;
            bool canShop = inMatch && me != null && me.IsSpawned && !me.IsDead.Value && me.InSpawnZone;
            if (!canShop)
                _shopOpen = false;
            if (inMatch && k?.escapeKey.wasPressedThisFrame == true)
            {
                if (_shopOpen)
                    _shopOpen = false;
                else
                    _leaveMenuOpen = !_leaveMenuOpen;
            }
            if (canShop && !_leaveMenuOpen && k?.bKey.wasPressedThisFrame == true)
                _shopOpen = !_shopOpen;
            if (!inMatch)
                _leaveMenuOpen = false;
            CapturesPointer = !inMatch || _leaveMenuOpen || _shopOpen;
        }

        private void FindMatch()
        {
            _nickname = (_nickname ?? "").Trim();
            PlayerPrefs.SetString(NicknamePref, _nickname);
            PlayerPrefs.Save();
            _nm.ClientManager.Broadcast(new FindMatchBroadcast { Nickname = _nickname });
        }

        private void OnGUI()
        {
#if UNITY_SERVER
            return;
#else
            if (Application.isBatchMode)
                return;
            // Editor: until something is started, DebugHud's Host/Server/Client buttons are the UI.
            if (!_bootstrap.WantsClient && !_nm.IsClientStarted)
                return;

            EnsureStyles();
            float scale = Screen.height / ReferenceHeight;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            Vector2 screen = new(Screen.width / scale, ReferenceHeight);

            if (!_nm.IsClientStarted)
            {
                if (_status == MatchStatus.Ended)
                    Panel(screen, DrawResults);
                else
                    Panel(screen, DrawConnecting);
                return;
            }

            switch (_status)
            {
                case MatchStatus.Idle:
                    Panel(screen, DrawMainMenu);
                    break;
                case MatchStatus.Queued:
                    Panel(screen, DrawQueued);
                    break;
                case MatchStatus.InMatch:
                    _hud.Draw(screen, scale, _shopOpen);
                    DrawCountdown(screen);
                    if (_leaveMenuOpen)
                        Panel(screen, DrawLeaveMenu);
                    else if (_shopOpen)
                        Panel(screen, DrawShop, ShopWidth);
                    break;
                case MatchStatus.Ended:
                    Panel(screen, DrawResults);
                    break;
            }
#endif
        }

        #region Screens
        private void DrawConnecting()
        {
            GUILayout.Label(Title, _title);
            if (_bootstrap.FailedAttempts == 0)
            {
                GUILayout.Label("Connecting to the server...", _label);
                return;
            }
            GUILayout.Label(_wasConnected ? "Lost connection to the server." : "Can't reach the server.", _label);
            float retryIn = _bootstrap.RetryIn;
            GUILayout.Label(retryIn >= 0f
                ? $"Retrying in {Mathf.CeilToInt(retryIn)}s (attempt {_bootstrap.FailedAttempts + 1})"
                : "Connecting...", _small);
            if (retryIn >= 0f && GUILayout.Button("Retry now", _button))
                _bootstrap.RetryNow();
        }

        private void DrawMainMenu()
        {
            GUILayout.Label(Title, _title);
            GUILayout.Label("1v1. Destroy the enemy nexus.", _small);
            GUILayout.Space(16);

            GUILayout.Label("Nickname", _label);
            bool submit = Event.current.type == EventType.KeyDown
                          && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            _nickname = GUILayout.TextField(_nickname, NetConfig.MaxNicknameLength, _field);
            GUILayout.Space(10);
            if (GUILayout.Button("Find match", _button) || submit)
            {
                if (submit)
                    Event.current.Use();
                FindMatch();
            }

            if (!string.IsNullOrEmpty(_message))
            {
                GUILayout.Space(8);
                GUILayout.Label(_message, _small);
            }
            GUILayout.Space(16);
            GUILayout.Label("A/D move · Space jump · S+Space drop through\nMouse aim · LMB fire · Q ability (starts as Grenade) · B shop (in spawn) · Esc leave", _small);
        }

        private void DrawQueued()
        {
            GUILayout.Label("Looking for an opponent", _heading);
            int secs = Mathf.FloorToInt(Time.unscaledTime - _queuedAt);
            GUILayout.Label($"{secs / 60}:{secs % 60:00}" + new string('.', 1 + secs % 3), _label);
            GUILayout.Space(12);
            if (GUILayout.Button("Cancel", _button))
                _nm.ClientManager.Broadcast(new LeaveMatchBroadcast());
        }

        private void DrawCountdown(Vector2 screen)
        {
            MatchState state = MatchState.Local;
            string big = null, sub = null;
            if (state == null)
                sub = "Loading arena...";
            else if (!state.CountdownStarted)
                sub = $"Waiting for {_opponent} to load...";
            else
            {
                float remaining = state.CountdownRemaining;
                if (remaining > 0f)
                {
                    big = Mathf.CeilToInt(remaining).ToString();
                    sub = $"vs {_opponent}";
                }
                else if (remaining > -GoBannerSeconds)
                    big = "GO!";
            }

            Rect area = new(0, screen.y * 0.3f, screen.x, 200);
            GUILayout.BeginArea(area);
            if (big != null)
                ShadowLabel(big, _big);
            if (sub != null)
                ShadowLabel(sub, _heading);
            GUILayout.EndArea();
        }

        /// <summary>Label with a dark drop shadow so it reads over the arena.</summary>
        private static void ShadowLabel(string text, GUIStyle style)
        {
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text), style);
            Color old = GUI.contentColor;
            GUI.contentColor = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(r.x + 3f, r.y + 3f, r.width, r.height), text, style);
            GUI.contentColor = old;
            GUI.Label(r, text, style);
        }

        private const float ShopWidth = 760f;

        private void DrawShop()
        {
            PlayerAvatar me = PlayerAvatar.LocalPlayer;
            if (me == null || me.Abilities == null)
                return;
            GUILayout.Label("SHOP", _heading);
            GUILayout.Label($"${me.Money.Value}  ·  minion kill ${Shop.MinionBounty}  ·  enemy tower ${Shop.TowerBounty}", _small);
            GUILayout.Space(12);
            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width((ShopWidth - 80f) * 0.5f));
            GUILayout.Label("UPGRADES", _label);
            GUILayout.Space(4);
            foreach (ShopItem item in Shop.Items)
            {
                int level = me.Level(item.Id);
                bool maxed = level >= item.MaxLevel;
                GUILayout.Label($"{item.Name}  ({level}/{item.MaxLevel})", _label);
                GUILayout.Label(item.Description, _small);
                GUI.enabled = !maxed && me.Money.Value >= item.Cost;
                if (GUILayout.Button(maxed ? "Maxed" : $"Buy  ${item.Cost}", _button))
                    me.ServerBuy(item.Id);
                GUI.enabled = true;
                GUILayout.Space(8);
            }
            GUILayout.EndVertical();

            GUILayout.Space(24);

            PlayerAbilities ab = me.Abilities;
            GUILayout.BeginVertical(GUILayout.Width((ShopWidth - 80f) * 0.5f));
            GUILayout.Label($"Q ABILITY  ·  you have {ab.CurrentDef.Name}", _label);
            GUILayout.Label("Buying replaces it.", _small);
            GUILayout.Space(4);
            for (int slot = 0; slot < 2; slot++)
            {
                AbilityDef def = Abilities.Get(ab.Offer(slot));
                GUILayout.Label($"{def.Name}  ·  {def.Cooldown:0}s cooldown", _label);
                GUILayout.Label(def.Description, _small);
                GUI.enabled = me.Money.Value >= def.Cost;
                if (GUILayout.Button($"Buy  ${def.Cost}", _button))
                    ab.ServerBuy((byte)slot);
                GUI.enabled = true;
                GUILayout.Space(8);
            }
            GUI.enabled = me.Money.Value >= Abilities.RerollCost;
            if (GUILayout.Button($"Reroll offers  ${Abilities.RerollCost}", _button))
                ab.ServerReroll();
            GUI.enabled = true;
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            if (GUILayout.Button("Close (B)", _button))
                _shopOpen = false;
        }

        private void DrawLeaveMenu()
        {
            GUILayout.Label("Leave the match?", _heading);
            GUILayout.Label("Leaving counts as a loss.", _small);
            GUILayout.Space(12);
            if (GUILayout.Button("Leave match", _button))
            {
                _leaveMenuOpen = false;
                _nm.ClientManager.Broadcast(new LeaveMatchBroadcast());
            }
            if (GUILayout.Button("Keep playing (Esc)", _button))
                _leaveMenuOpen = false;
        }

        private void DrawResults()
        {
            (string headline, Color color) = _outcome switch
            {
                MatchOutcome.Won => ("VICTORY", new Color(0.45f, 0.9f, 0.5f)),
                MatchOutcome.Lost => ("DEFEAT", new Color(1f, 0.4f, 0.35f)),
                MatchOutcome.Left => ("YOU LEFT", Color.white),
                _ => ("MATCH OVER", Color.white),
            };
            Color old = GUI.contentColor;
            GUI.contentColor = color;
            GUILayout.Label(headline, _title);
            GUI.contentColor = old;
            if (!string.IsNullOrEmpty(_message))
                GUILayout.Label(_message, _label);
            GUILayout.Space(16);

            if (!_nm.IsClientStarted)
            {
                if (GUILayout.Button("Back to menu", _button))
                {
                    _status = MatchStatus.Idle;
                    _message = "";
                }
                return;
            }
            if (GUILayout.Button("Find another match", _button))
                FindMatch();
            if (GUILayout.Button("Back to menu", _button))
            {
                _status = MatchStatus.Idle;
                _message = "";
            }
        }
        #endregion

        #region Layout
        private void Panel(Vector2 screen, System.Action draw, float width = PanelWidth)
        {
            // Height is measured on the Layout pass; centre vertically using a generous estimate.
            // The wide shop panel is also the tallest: start it higher.
            float top = width > PanelWidth ? 0.07f : 0.18f;
            Rect area = new((screen.x - width) * 0.5f, screen.y * top, width, screen.y * (0.95f - top));
            GUILayout.BeginArea(area);
            GUILayout.BeginVertical(_panel);
            draw();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void EnsureStyles()
        {
            if (_title != null)
                return;
            _panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(28, 28, 24, 24) };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _big = new GUIStyle(_title) { fontSize = 110 };
            _heading = new GUIStyle(GUI.skin.label) { fontSize = 26, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _small = new GUIStyle(_label) { fontSize = 15 };
            _small.normal.textColor = new Color(0.75f, 0.75f, 0.8f);
            _button = new GUIStyle(GUI.skin.button) { fontSize = 22, fixedHeight = 46, margin = new RectOffset(4, 4, 6, 6) };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 22, fixedHeight = 40, alignment = TextAnchor.MiddleCenter };
        }
        #endregion
    }
}
