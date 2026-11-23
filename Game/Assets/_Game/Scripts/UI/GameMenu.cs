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
        private GUIStyle _title, _big, _heading, _label, _small, _button, _field, _panel, _solidPanel,
            _rowLabel, _rowLabelHover, _rowButton, _tile, _currentTile, _tileName, _barLabel, _tooltipStyle;
        private readonly BackdropBlur _blur = new();

        /// <summary>Smoke tests (-smokeshop): open the shop whenever it's available, for screenshots.</summary>
        public static bool SmokeOpenShop;

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
            if (canShop && SmokeOpenShop)
                _shopOpen = true;
            if (!inMatch)
                _leaveMenuOpen = false;
            bool choosing = NeedsGunChoice(me) && inMatch;
            if (choosing && !_leaveMenuOpen && k != null)
            {
                if (k.digit1Key.wasPressedThisFrame)
                    me.Weapons.ServerChooseGun(PlayerWeapons.GunChoices[0]);
                else if (k.digit2Key.wasPressedThisFrame)
                    me.Weapons.ServerChooseGun(PlayerWeapons.GunChoices[1]);
            }
            CapturesPointer = !inMatch || _leaveMenuOpen || _shopOpen || choosing;
        }

        /// <summary>The local player is in a match but hasn't picked their gun yet.</summary>
        private static bool NeedsGunChoice(PlayerAvatar me) =>
            me != null && me.IsSpawned && me.Weapons != null && !me.Weapons.HasGun;

        private void LateUpdate()
        {
            if (_status == MatchStatus.InMatch && ModalOpen)
                _blur.Capture();
        }

        /// <summary>In a match, a menu (leave, gun choice, shop) is over the game: blur it behind the panel.</summary>
        private bool ModalOpen => _leaveMenuOpen || _shopOpen || NeedsGunChoice(PlayerAvatar.LocalPlayer);

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
                    // Menus sit on a blurred copy of the game; the countdown stays sharp above it.
                    if (ModalOpen)
                        _blur.Draw();
                    DrawCountdown(screen);
                    if (_leaveMenuOpen)
                        Panel(screen, DrawLeaveMenu, PanelWidth, _solidPanel);
                    else if (NeedsGunChoice(PlayerAvatar.LocalPlayer))
                        Panel(screen, DrawGunChoice, ShopWidth, _solidPanel, top: 0.52f);
                    else if (_shopOpen)
                    {
                        Panel(screen, DrawShop, ShopWidth, _solidPanel);
                        DrawTooltip(screen);
                    }
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
                    big = Mathf.CeilToInt(Mathf.Min(remaining, MatchState.CountdownSeconds)).ToString();
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

        private static readonly Vector2 TileSize = new(140f, 128f);

        /// <summary>Hover text for the shop, drawn after the panel so it isn't clipped (null: none).</summary>
        private string _tooltip;
        private Vector2 _tooltipAt;

        private void DrawShop()
        {
            PlayerAvatar me = PlayerAvatar.LocalPlayer;
            if (me == null || me.Abilities == null)
                return;
            _tooltip = null;
            GUILayout.Label("SHOP", _heading);
            GUILayout.Label($"${me.Money.Value}  ·  minion kill ${Shop.MinionBounty}  ·  enemy tower ${Shop.TowerBounty}", _small);
            GUILayout.Space(12);

            GUILayout.Label("UPGRADES", _label);
            GUILayout.Space(2);
            foreach (ShopItem item in Shop.Items)
                DrawUpgradeRow(me, item);
            GUILayout.Space(16);

            PlayerAbilities ab = me.Abilities;
            GUILayout.Label("Q ABILITY", _label);
            GUILayout.Label("Buying an ability replaces your current one. Hover for details.", _small);
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            AbilityDef current = ab.CurrentDef;
            Tile(Icons.Get(current.Icon), current.Name, "Current", AbilityTooltip(current), clickable: false, enabled: true);
            GUILayout.Label("→", _heading, GUILayout.Width(44), GUILayout.Height(TileSize.y));
            for (int slot = 0; slot < 2; slot++)
            {
                AbilityDef def = Abilities.Get(ab.Offer(slot));
                if (Tile(Icons.Get(def.Icon), def.Name, $"${def.Cost}", AbilityTooltip(def), clickable: true, enabled: me.Money.Value >= def.Cost))
                    ab.ServerBuy((byte)slot);
                GUILayout.Space(12);
            }
            GUILayout.Space(16);
            if (Tile(Icons.Get("reroll"), "Reroll", $"${Abilities.RerollCost}", "Reroll\nReplace both offers with new random abilities.",
                    clickable: true, enabled: me.Money.Value >= Abilities.RerollCost))
                ab.ServerReroll();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(14);
            if (GUILayout.Button("Close (B)", _button))
                _shopOpen = false;
        }

        private static string AbilityTooltip(AbilityDef def) => $"{def.Name}\n{def.Description}.\n{def.Cooldown:0}s cooldown";

        /// <summary>One stat upgrade: icon, name (hover for what it does), level bar, buy button.</summary>
        private void DrawUpgradeRow(PlayerAvatar me, ShopItem item)
        {
            const float rowHeight = 44f;
            int level = me.Level(item.Id);
            bool full = level >= item.MaxLevel;
            bool repaint = Event.current.type == EventType.Repaint;

            GUILayout.BeginHorizontal(GUILayout.Height(rowHeight));
            Rect iconRect = GUILayoutUtility.GetRect(34f, rowHeight, GUILayout.Width(34f), GUILayout.Height(rowHeight));
            Texture2D icon = Icons.Get(item.Icon);
            if (repaint && icon != null)
                GUI.DrawTexture(new Rect(iconRect.x, iconRect.center.y - 15f, 30f, 30f), icon, ScaleMode.ScaleToFit);
            GUILayout.Space(10);

            Rect nameRect = GUILayoutUtility.GetRect(130f, rowHeight, GUILayout.Width(130f), GUILayout.Height(rowHeight));
            bool hovered = Hover(nameRect, $"{item.Name}\n{item.Description}." +
                (item.Consumable ? $"\nBuying refills it to {item.MaxLevel}." : $"\nUp to {item.MaxLevel} times."));
            GUI.Label(nameRect, item.Name, hovered ? _rowLabelHover : _rowLabel);

            Rect barRect = GUILayoutUtility.GetRect(330f, rowHeight, GUILayout.Width(330f), GUILayout.Height(rowHeight));
            if (repaint)
                DrawLevelBar(new Rect(barRect.x, barRect.center.y - 8f, barRect.width, 16f), item, level);
            GUILayout.FlexibleSpace();

            GUI.enabled = !full && me.Money.Value >= item.Cost;
            if (GUILayout.Button(full ? (item.Consumable ? "Full" : "Maxed") : $"${item.Cost}", _rowButton, GUILayout.Width(120f)))
                me.ServerBuy(item.Id);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private static Color UpgradeColor(ShopItemId id) => id switch
        {
            ShopItemId.Vitality => new Color(0.9f, 0.3f, 0.35f),
            ShopItemId.Firepower => new Color(1f, 0.6f, 0.2f),
            _ => new Color(0.55f, 0.75f, 1f),
        };

        /// <summary>Upgrades: one segment per level. Armor: a bar of points left (it refills rather than levels).</summary>
        private void DrawLevelBar(Rect r, ShopItem item, int level)
        {
            Color empty = new(1f, 1f, 1f, 0.12f);
            Color fill = UpgradeColor(item.Id);
            if (item.Consumable)
            {
                Fill(r, empty);
                Fill(new Rect(r.x, r.y, r.width * Mathf.Clamp01((float)level / item.MaxLevel), r.height), fill);
                GUI.Label(new Rect(r.xMax + 10f, r.y - 4f, 70f, r.height + 8f), $"{level} / {item.MaxLevel}", _barLabel);
                return;
            }
            const float gap = 5f;
            float w = (r.width - gap * (item.MaxLevel - 1)) / item.MaxLevel;
            for (int i = 0; i < item.MaxLevel; i++)
                Fill(new Rect(r.x + i * (w + gap), r.y, w, r.height), i < level ? fill : empty);
        }

        /// <summary>Icon tile (ability offers, reroll, current ability). Returns whether it was clicked.</summary>
        private bool Tile(Texture2D icon, string name, string caption, string tooltip, bool clickable, bool enabled)
        {
            Rect r = GUILayoutUtility.GetRect(TileSize.x, TileSize.y, GUILayout.Width(TileSize.x), GUILayout.Height(TileSize.y));
            bool clicked = false;
            if (clickable)
            {
                GUI.enabled = enabled;
                clicked = GUI.Button(r, GUIContent.none, _tile);
                GUI.enabled = true;
            }
            else
                GUI.Box(r, GUIContent.none, _currentTile);
            Hover(r, tooltip);
            if (Event.current.type == EventType.Repaint)
            {
                Color old = GUI.color;
                if (!enabled)
                    GUI.color = new Color(1f, 1f, 1f, 0.4f);
                if (icon != null)
                    GUI.DrawTexture(new Rect(r.center.x - 28f, r.y + 12f, 56f, 56f), icon, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(r.x, r.y + 72f, r.width, 26f), name, _tileName);
                GUI.Label(new Rect(r.x, r.y + 98f, r.width, 22f), caption, _small);
                GUI.color = old;
            }
            return clicked;
        }

        /// <summary>If the mouse is over r, show text as the tooltip. Returns whether it's hovered.</summary>
        private bool Hover(Rect r, string text)
        {
            Vector2 mouse = SmokeHover ? SmokeHoverPoint(r) : Event.current.mousePosition;
            if (Event.current.type != EventType.Repaint || !r.Contains(mouse))
                return false;
            _tooltip = text;
            _tooltipAt = GUIUtility.GUIToScreenPoint(mouse);
            return true;
        }

        /// <summary>Smoke screenshots (-smokehover): pretend the mouse is over the second ability offer tile.</summary>
        public static bool SmokeHover;
        private int _smokeHoverCount;
        private Vector2 SmokeHoverPoint(Rect r)
        {
            // Tiles are hovered in order: current, offer A, offer B, reroll. Rows come first (3).
            bool target = r.width == TileSize.x && r.height == TileSize.y && _smokeHoverCount++ % 4 == 2;
            return target ? r.center : new Vector2(-1000f, -1000f);
        }

        /// <summary>Draws the shop's hover text beside the cursor, kept on screen.</summary>
        private void DrawTooltip(Vector2 screen)
        {
            if (_tooltip == null || Event.current.type != EventType.Repaint)
                return;
            Vector2 p = GUIUtility.ScreenToGUIPoint(_tooltipAt);
            // First line is the title.
            int nl = _tooltip.IndexOf('\n');
            GUIContent content = new(nl < 0 ? $"<b>{_tooltip}</b>" : $"<b>{_tooltip[..nl]}</b>{_tooltip[nl..]}");
            const float w = 300f;
            float h = _tooltipStyle.CalcHeight(content, w);
            Rect r = new(p.x + 18f, p.y + 18f, w, h);
            if (r.xMax > screen.x - 8f)
                r.x = p.x - w - 10f;
            if (r.yMax > screen.y - 8f)
                r.y = p.y - h - 10f;
            GUI.Box(r, content, _tooltipStyle);
        }

        private static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>Start-of-match pick between the two guns (below the countdown).</summary>
        private void DrawGunChoice()
        {
            PlayerAvatar me = PlayerAvatar.LocalPlayer;
            if (me == null)
                return;
            GUILayout.Label("Choose your weapon for this match", _heading);
            GUILayout.Label($"Picked at random if you haven't chosen when the countdown ends. Once both players have chosen, the match starts in {MatchState.ReadyCountdownSeconds:0}s.", _small);
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < PlayerWeapons.GunChoices.Length; i++)
            {
                int slot = PlayerWeapons.GunChoices[i];
                WeaponDefinition w = me.Weapons.Get(slot);
                GUILayout.BeginVertical(GUILayout.Width((ShopWidth - 90f) * 0.5f));
                GUILayout.Label(w.DisplayName, _label);
                GUILayout.Label($"{w.Damage} damage  ·  {w.Damage / w.Cooldown:0} per second  ·  range {w.Range:0}", _small);
                GUILayout.Label(w.Range < 11f ? "Short range: step into tower range to hit towers." : "Long range: hits towers from outside their range.", _small);
                if (GUILayout.Button($"{i + 1}  {w.DisplayName}", _button))
                    me.Weapons.ServerChooseGun(slot);
                GUILayout.EndVertical();
                if (i == 0)
                    GUILayout.Space(20);
            }
            GUILayout.EndHorizontal();
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
        private void Panel(Vector2 screen, System.Action draw, float width = PanelWidth, GUIStyle style = null, float top = -1f)
        {
            // Height is measured on the Layout pass; centre vertically using a generous estimate.
            // The wide shop panel is also the tallest: start it higher.
            if (top < 0f)
                top = width > PanelWidth ? 0.07f : 0.18f;
            Rect area = new((screen.x - width) * 0.5f, screen.y * top, width, screen.y * (0.95f - top));
            GUILayout.BeginArea(area);
            GUILayout.BeginVertical(style ?? _panel);
            draw();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        /// <summary>A 3x3 texture: one-pixel border around a fill, stretched by a 1-pixel GUIStyle border.</summary>
        private static Texture2D SolidTexture(Color fill, Color edge)
        {
            Texture2D t = new(3, 3) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            Color[] px = new Color[9];
            for (int i = 0; i < 9; i++)
                px[i] = i == 4 ? fill : edge;
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private void EnsureStyles()
        {
            if (_title != null)
                return;
            _panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(28, 28, 24, 24) };
            // Nearly opaque, for the shop over its blurred backdrop.
            _solidPanel = new GUIStyle(_panel);
            _solidPanel.normal.background = SolidTexture(new Color(0.09f, 0.09f, 0.12f, 0.97f), new Color(1f, 1f, 1f, 0.18f));
            _solidPanel.border = new RectOffset(1, 1, 1, 1);
            _title = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _big = new GUIStyle(_title) { fontSize = 110 };
            _heading = new GUIStyle(GUI.skin.label) { fontSize = 26, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _small = new GUIStyle(_label) { fontSize = 15 };
            _small.normal.textColor = new Color(0.75f, 0.75f, 0.8f);
            _button = new GUIStyle(GUI.skin.button) { fontSize = 22, fixedHeight = 46, margin = new RectOffset(4, 4, 6, 6) };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 22, fixedHeight = 40, alignment = TextAnchor.MiddleCenter };

            // Shop.
            _rowLabel = new GUIStyle(_label) { alignment = TextAnchor.MiddleLeft, wordWrap = false };
            _rowLabelHover = new GUIStyle(_rowLabel);
            _rowLabelHover.normal.textColor = new Color(1f, 0.85f, 0.4f);
            _rowButton = new GUIStyle(_button) { fontSize = 19, fixedHeight = 36, margin = new RectOffset(4, 4, 4, 4) };
            _tile = new GUIStyle(GUI.skin.button);
            _currentTile = new GUIStyle(GUI.skin.box);
            _currentTile.normal.background = SolidTexture(new Color(1f, 1f, 1f, 0.06f), new Color(1f, 0.85f, 0.4f, 0.7f));
            _currentTile.border = new RectOffset(1, 1, 1, 1);
            _tileName = new GUIStyle(_label) { fontSize = 18, wordWrap = false };
            _barLabel = new GUIStyle(_small) { alignment = TextAnchor.MiddleLeft, wordWrap = false };
            _tooltipStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 16, wordWrap = true, alignment = TextAnchor.UpperLeft, richText = true,
                padding = new RectOffset(12, 12, 10, 10), border = new RectOffset(1, 1, 1, 1),
            };
            _tooltipStyle.normal.background = SolidTexture(new Color(0.03f, 0.03f, 0.05f, 0.98f), new Color(1f, 0.85f, 0.4f, 0.6f));
            _tooltipStyle.normal.textColor = Color.white;
        }
        #endregion
    }
}
