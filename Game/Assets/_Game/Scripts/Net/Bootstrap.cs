using System;
using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Bayou;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace Jam.Net
{
    /// <summary>
    /// Decides what this process is:
    /// - Dedicated server build: start the server on NetConfig.ServerPort.
    /// - Web build: connect back to the host that served the page (wss://host/ via Caddy).
    ///   Append ?server=host:port to the page URL to target a plain ws:// server instead.
    /// - Editor/desktop: idle until DebugHud buttons (or -server / -client args) start something.
    /// Once a client has been started it keeps reconnecting (with backoff and jitter) whenever
    /// the connection fails or drops.
    /// </summary>
    [RequireComponent(typeof(NetworkManager), typeof(Bayou))]
    public class Bootstrap : MonoBehaviour
    {
        private const float MaxRetryDelay = 8f;

        private NetworkManager _nm;
        private Bayou _bayou;
        private bool _wantClient;
        private float _retryAt = -1f;
        private bool _quitting;

        /// <summary>Failed connection attempts since the last successful connect.</summary>
        public int FailedAttempts { get; private set; }
        /// <summary>Seconds until the next reconnect attempt, or -1 if none is scheduled.</summary>
        public float RetryIn => _retryAt < 0f ? -1f : Mathf.Max(0f, _retryAt - Time.unscaledTime);
        /// <summary>True once a client has been started (and will be kept connected).</summary>
        public bool WantsClient => _wantClient;

        private void Awake()
        {
            _nm = GetComponent<NetworkManager>();
            _bayou = GetComponent<Bayou>();
            _nm.ClientManager.OnClientConnectionState += OnClientConnectionState;
            Application.quitting += () => _quitting = true;
        }

        private void OnDestroy()
        {
            if (_nm != null)
                _nm.ClientManager.OnClientConnectionState -= OnClientConnectionState;
        }

        private void Update()
        {
            if (_retryAt >= 0f && Time.unscaledTime >= _retryAt)
            {
                _retryAt = -1f;
                if (_wantClient && !_nm.IsClientStarted)
                    _nm.ClientManager.StartConnection();
            }
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                FailedAttempts = 0;
                return;
            }
            if (args.ConnectionState != LocalConnectionState.Stopped)
                return;

            if (!_nm.IsServerStarted)
                UnloadArenaScenes();
            if (!_wantClient || _quitting)
                return;
            FailedAttempts++;
            // 1, 2, 4, 8, 8... seconds, plus jitter so a crowd of clients doesn't reconnect in lockstep.
            float delay = Mathf.Min(MaxRetryDelay, Mathf.Pow(2f, FailedAttempts - 1)) + UnityEngine.Random.Range(0f, 0.5f);
            _retryAt = Time.unscaledTime + delay;
            Debug.Log($"[Bootstrap] Client connection stopped; retrying in {delay:0.0}s (attempt {FailedAttempts + 1}).");
        }

        /// <summary>A pure client that loses the server keeps whatever arena it had loaded; drop it.</summary>
        private static void UnloadArenaScenes()
        {
            for (int i = UnitySceneManager.sceneCount - 1; i >= 0; i--)
            {
                var scene = UnitySceneManager.GetSceneAt(i);
                if (scene.name == NetConfig.ArenaScene && scene.isLoaded)
                    UnitySceneManager.UnloadSceneAsync(scene);
            }
        }

        private void Start()
        {
#if UNITY_SERVER
            StartServer();
#elif UNITY_WEBGL && !UNITY_EDITOR
            ConfigureClientFromPageUrl();
            StartClient();
#else
            // -connect host  →  wss://host:443 (e.g. smoke-testing production from a desktop build)
            string remote = GetArg("-connect");
            if (remote != null)
                ConfigureClient(remote, 443, useWss: true);
            else
                ConfigureClient("localhost", NetConfig.ServerPort, useWss: false);
            if (SmokeTestDriver.Requested)
                gameObject.AddComponent<SmokeTestDriver>();
            // -latency ms: FishNet's latency simulator (applied to this process's outgoing traffic).
            if (long.TryParse(GetArg("-latency"), out long latency))
            {
                _nm.TransportManager.LatencySimulator.SetEnabled(true);
                _nm.TransportManager.LatencySimulator.SetLatency(latency);
            }
            if (HasArg("-server"))
                StartServer();
            else if (HasArg("-client"))
                StartClient();
#endif
        }

        public void StartServer()
        {
#if UNITY_SERVER
            Application.targetFrameRate = NetConfig.TickRate;
#endif
            _bayou.SetPort(NetConfig.ServerPort);
            _nm.ServerManager.StartConnection();
        }

        public void StartClient()
        {
            _wantClient = true;
            _retryAt = -1f;
            _nm.ClientManager.StartConnection();
        }

        /// <summary>Reconnect now instead of waiting for the backoff.</summary>
        public void RetryNow()
        {
            if (_wantClient && !_nm.IsClientStarted)
                StartClient();
        }

        public void StartHost()
        {
            StartServer();
            StartClient();
        }

        private void ConfigureClient(string host, ushort port, bool useWss)
        {
            _bayou.SetClientAddress(host);
            _bayou.SetPort(port);
            _bayou.SetUseWSS(useWss);
        }

        private void ConfigureClientFromPageUrl()
        {
            Uri page = new(Application.absoluteURL);
            string overrideServer = GetQueryParam(page, "server");
            if (!string.IsNullOrEmpty(overrideServer))
            {
                string[] parts = overrideServer.Split(':');
                ushort port = parts.Length > 1 && ushort.TryParse(parts[1], out ushort p) ? p : NetConfig.ServerPort;
                ConfigureClient(parts[0], port, useWss: false);
                return;
            }

            bool secure = page.Scheme == Uri.UriSchemeHttps;
            ConfigureClient(page.Host, (ushort)page.Port, secure);
        }

        private static string GetQueryParam(Uri uri, string key)
        {
            foreach (string pair in uri.Query.TrimStart('?').Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == key)
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            return null;
        }

        private static bool HasArg(string arg) => Array.IndexOf(Environment.GetCommandLineArgs(), arg) >= 0;

        private static string GetArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
