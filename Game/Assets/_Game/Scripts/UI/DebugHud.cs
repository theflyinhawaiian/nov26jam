using FishNet.Managing;
using FishNet.Transporting;
using Jam.Gameplay;
using Jam.Net;
using UnityEngine;

namespace Jam.UI
{
    /// <summary>
    /// M1 throwaway IMGUI: start server/client in the editor, nickname + Find match,
    /// and the lag-compensation readout. Replaced by real UI in M4.
    /// </summary>
    [RequireComponent(typeof(NetworkManager), typeof(Bootstrap))]
    public class DebugHud : MonoBehaviour
    {
        private const float ReferenceHeight = 720f;
        private static Rect _panelRect;

        /// <summary>True while the mouse is over the HUD panel, so clicks there don't fire the rifle.</summary>
        public static bool PointerOverHud { get; private set; }

        private NetworkManager _nm;
        private Bootstrap _bootstrap;
        private string _nickname = "";
        private MatchStatus _status = MatchStatus.Idle;
        private string _statusMessage = "";
        private string _serverVersion = "?";

        private void Awake()
        {
            _nm = GetComponent<NetworkManager>();
            _bootstrap = GetComponent<Bootstrap>();
            _nickname = $"Player{Random.Range(100, 999)}";
        }

        private void Start()
        {
            _nm.ClientManager.RegisterBroadcast<MatchStatusBroadcast>(OnMatchStatus);
            _nm.ClientManager.RegisterBroadcast<ServerInfoBroadcast>(OnServerInfo);
            _nm.ClientManager.OnClientConnectionState += OnClientConnectionState;
        }

        private void OnDestroy()
        {
            if (_nm == null)
                return;
            _nm.ClientManager.UnregisterBroadcast<MatchStatusBroadcast>(OnMatchStatus);
            _nm.ClientManager.UnregisterBroadcast<ServerInfoBroadcast>(OnServerInfo);
            _nm.ClientManager.OnClientConnectionState -= OnClientConnectionState;
        }

        private void OnMatchStatus(MatchStatusBroadcast msg, Channel channel)
        {
            _status = msg.Status;
            _statusMessage = msg.Message;
            if (msg.Status == MatchStatus.InMatch)
                FireDiagnostics.Reset();
        }

        private void OnServerInfo(ServerInfoBroadcast msg, Channel channel)
        {
            _serverVersion = msg.Version;
            Debug.Log($"[DebugHud] Client build {BuildInfo.Version}, server build {msg.Version}");
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
                _nm.ClientManager.Broadcast(new HelloBroadcast());
            if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                _status = MatchStatus.Idle;
                _statusMessage = "Disconnected.";
            }
        }

        private void Update()
        {
            if (UnityEngine.InputSystem.Keyboard.current?.backquoteKey.wasPressedThisFrame == true)
                FireDiagnostics.Enabled = !FireDiagnostics.Enabled;
            if (FireDiagnostics.Enabled && UnityEngine.InputSystem.Keyboard.current?.tKey.wasPressedThisFrame == true)
                TeleportToEnemySpawn();

            float scale = Screen.height / ReferenceHeight;
            Vector2 mouse = UnityEngine.InputSystem.Mouse.current?.position.ReadValue() ?? Vector2.negativeInfinity;
            Vector2 guiMouse = new Vector2(mouse.x, Screen.height - mouse.y) / scale;
            PointerOverHud = _panelRect.Contains(guiMouse);
        }

        private void OnGUI()
        {
#if UNITY_SERVER
            return;
#else
            float scale = Screen.height / ReferenceHeight;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            _panelRect = new Rect(10, 10, 330, 0);
            GUILayout.BeginArea(new Rect(10, 10, 330, ReferenceHeight - 20));
            GUILayout.BeginVertical(GUI.skin.box);

            bool server = _nm.IsServerStarted;
            bool client = _nm.IsClientStarted;
            string status = server && client ? "Hosting (server + client)"
                : server ? "Server only"
                : client ? $"Connected to {_nm.TransportManager.Transport.GetClientAddress()}"
                : "Not connected";
            GUILayout.Label(status);
            GUILayout.Label($"Build {BuildInfo.Version}  |  server {(client ? _serverVersion : "-")}");

#if !UNITY_WEBGL || UNITY_EDITOR
            if (!server && !client)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Host")) _bootstrap.StartHost();
                if (GUILayout.Button("Server")) _bootstrap.StartServer();
                if (GUILayout.Button("Client")) _bootstrap.StartClient();
                GUILayout.EndHorizontal();
            }
#endif

            if (client)
                DrawLobby();

            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
                _panelRect.height = GUILayoutUtility.GetLastRect().yMax;
            GUILayout.EndArea();
#endif
        }

        private void DrawLobby()
        {
            GUILayout.Label($"Ping: {_nm.TimeManager.RoundTripTime} ms");

            switch (_status)
            {
                case MatchStatus.Idle:
                case MatchStatus.Ended:
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Nickname", GUILayout.Width(70));
                    _nickname = GUILayout.TextField(_nickname, NetConfig.MaxNicknameLength);
                    GUILayout.EndHorizontal();
                    if (GUILayout.Button("Find match"))
                        _nm.ClientManager.Broadcast(new FindMatchBroadcast { Nickname = _nickname });
                    break;
                case MatchStatus.Queued:
                    if (GUILayout.Button("Cancel"))
                        _nm.ClientManager.Broadcast(new LeaveMatchBroadcast());
                    break;
                case MatchStatus.InMatch:
                    DrawMatch();
                    if (GUILayout.Button("Leave match"))
                        _nm.ClientManager.Broadcast(new LeaveMatchBroadcast());
                    break;
            }

            if (!string.IsNullOrEmpty(_statusMessage))
                GUILayout.Label(_statusMessage);
        }

        /// <summary>
        /// Debug: put the local player just in front of the opponent's spawn: the first spot at
        /// least 6 units toward the centre that isn't on a jump pad. Movement is client-authoritative, so this is a local teleport; NetworkTransform
        /// snaps it on the other screens. Needs to be whitelisted once the server validates movement (M5).
        /// </summary>
        private void TeleportToEnemySpawn()
        {
            PlayerAvatar me = null;
            foreach (PlayerAvatar a in FindObjectsByType<PlayerAvatar>(FindObjectsSortMode.None))
                if (a.IsOwner)
                    me = a;
            if (me == null || me.IsDead.Value)
                return;
            foreach (SpawnPoint sp in FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None))
            {
                if (sp.Team == me.Team.Value)
                    continue;
                Vector2 spawn = sp.transform.position;
                float towardCentre = -Mathf.Sign(spawn.x);
                PhysicsScene2D physics = me.ArenaPhysics;
                for (float d = 6f; d <= 30f; d += 0.5f)
                {
                    Vector2 candidate = new(spawn.x + towardCentre * d, spawn.y);
                    if (!physics.OverlapBox(candidate, me.HitboxSize, 0f, Layers.JumpPadMask))
                    {
                        me.Motor.Teleport(candidate);
                        return;
                    }
                }
                return;
            }
        }

        private void DrawMatch()
        {
            PlayerAvatar me = null, opp = null;
            foreach (PlayerAvatar a in FindObjectsByType<PlayerAvatar>(FindObjectsSortMode.None))
            {
                if (a.IsOwner) me = a;
                else opp = a;
            }

            MatchState state = FindAnyObjectByType<MatchState>();
            if (state != null)
            {
                string blue = $"Blue nexus {state.BlueNexusHealth.Value}/{MatchState.NexusMaxHealth}";
                string red = $"Red nexus {state.RedNexusHealth.Value}/{MatchState.NexusMaxHealth}";
                GUILayout.Label($"{blue}   {red}");
            }

            if (me != null)
            {
                string team = me.Team.Value == 0 ? "Blue" : "Red";
                GUILayout.Label(me.IsDead.Value
                    ? $"You ({team}): DEAD, respawn in {me.RespawnSecondsRemaining:0.0}s"
                    : $"You ({team}): {me.Health.Value}/{PlayerAvatar.MaxHealth} HP");

                PlayerWeapons w = me.Weapons;
                string weapons = "";
                for (int i = 0; i < w.Count; i++)
                {
                    string cd = w.CooldownRemaining(i) > 0f ? $" {w.CooldownRemaining(i):0.0}s" : "";
                    weapons += (i == w.CurrentIndex ? $"[{i + 1} {w.Get(i).DisplayName}{cd}]" : $" {i + 1} {w.Get(i).DisplayName}{cd} ") + "  ";
                }
                GUILayout.Label(weapons);
            }
            if (opp != null)
                GUILayout.Label(opp.IsDead.Value ? $"{opp.Nickname.Value}: DEAD" : $"{opp.Nickname.Value}: {opp.Health.Value}/{PlayerAvatar.MaxHealth} HP");

            GUILayout.Label("A/D move, Space jump, S+Space drop, LMB fire, 1/2/Q weapon, ` debug" + (FireDiagnostics.Enabled ? ", T: TP to enemy spawn" : ""));

            if (FireDiagnostics.Enabled)
            {
                GUILayout.Label("Green = where you saw them, red/white = server rewind (red = hit).");
                GUILayout.Label($"Rifle shots {FireDiagnostics.Shots}  hits {FireDiagnostics.Hits}  last {(FireDiagnostics.LastHit ? "HIT" : "miss")}");
                GUILayout.Label($"Rewind error: last {FireDiagnostics.LastError:0.00}u  median {FireDiagnostics.MedianError:0.00}u");
                GUILayout.Label($"Best offset (median): {FireDiagnostics.MedianBestOffset:0.0} ticks  |  shot age {FireDiagnostics.LastTicksBehind} ticks");
            }
        }
    }
}
