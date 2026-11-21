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

        private void Awake()
        {
            _nm = GetComponent<NetworkManager>();
            _bootstrap = GetComponent<Bootstrap>();
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
            _statusMessage = msg.Message;
            if (msg.Status == MatchStatus.InMatch)
                FireDiagnostics.Reset();
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                _status = MatchStatus.Idle;
                _statusMessage = "Disconnected.";
            }
        }

        private void Update()
        {
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
            GUILayout.Label($"Server: {(server ? "running" : "off")}   Client: {(client ? "connected" : "off")}");

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

        private void DrawMatch()
        {
            foreach (PlayerAvatar a in FindObjectsByType<PlayerAvatar>(FindObjectsSortMode.None))
                GUILayout.Label($"{(a.IsOwner ? "You" : "Opp")}: {a.Nickname.Value}  team {a.Team.Value}  hit {a.TimesHit.Value}x");

            GUILayout.Label("WASD move, LMB fire.\nGreen = where you saw them, red/white = server rewind (red = hit).");
            GUILayout.Label($"Shots {FireDiagnostics.Shots}  hits {FireDiagnostics.Hits}  last {(FireDiagnostics.LastHit ? "HIT" : "miss")}");
            GUILayout.Label($"Rewind error: last {FireDiagnostics.LastError:0.00}u  median {FireDiagnostics.MedianError:0.00}u");
            GUILayout.Label($"Best offset (median): {FireDiagnostics.MedianBestOffset:0.0} ticks  |  shot age {FireDiagnostics.LastTicksBehind} ticks");
        }
    }
}
