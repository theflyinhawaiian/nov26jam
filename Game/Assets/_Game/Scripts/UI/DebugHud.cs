using FishNet.Managing;
using FishNet.Transporting;
using Jam.Gameplay;
using Jam.Net;
using UnityEngine;

namespace Jam.UI
{
    /// <summary>
    /// Debug IMGUI in the top-left corner: start server/client in the editor, connection and
    /// build info, ping, movement corrections and the lag-compensation readout.
    /// The player-facing match flow is GameMenu.
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
        private string _serverVersion = "?";

        private void Awake()
        {
            _nm = GetComponent<NetworkManager>();
            _bootstrap = GetComponent<Bootstrap>();
        }

        private void Start()
        {
            _nm.ClientManager.RegisterBroadcast<ServerInfoBroadcast>(OnServerInfo);
            _nm.ClientManager.OnClientConnectionState += OnClientConnectionState;
        }

        private void OnDestroy()
        {
            if (_nm == null)
                return;
            _nm.ClientManager.UnregisterBroadcast<ServerInfoBroadcast>(OnServerInfo);
            _nm.ClientManager.OnClientConnectionState -= OnClientConnectionState;
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
            if (!server && !client && !_bootstrap.WantsClient)
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
            if (MatchState.Local != null)
                DrawMatch();
        }

        /// <summary>
        /// Debug: put the local player just in front of the opponent's spawn: the first spot at
        /// least 6 units toward the centre that isn't on a jump pad. The server validates
        /// movement, so this asks it for a forced teleport (editor and dev-build servers only).
        /// </summary>
        private void TeleportToEnemySpawn()
        {
            PlayerAvatar me = PlayerAvatar.LocalPlayer;
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
                        me.ServerDebugTeleport(candidate);
                        return;
                    }
                }
                return;
            }
        }

        /// <summary>Player stats are on the match HUD (MatchHud); this is debug only.</summary>
        private void DrawMatch()
        {
            if (PlayerAvatar.CorrectionsReceived > 0)
                GUILayout.Label($"Movement corrections: {PlayerAvatar.CorrectionsReceived}");
            GUILayout.Label("` debug" + (FireDiagnostics.Enabled ? ", T: TP to enemy spawn" : ""));

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
