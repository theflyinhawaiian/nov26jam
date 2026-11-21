using System;
using FishNet.Managing;
using FishNet.Transporting.Bayou;
using UnityEngine;

namespace Jam.Net
{
    /// <summary>
    /// Decides what this process is:
    /// - Dedicated server build: start the server on NetConfig.ServerPort.
    /// - Web build: connect back to the host that served the page (wss://host/ via Caddy).
    ///   Append ?server=host:port to the page URL to target a plain ws:// server instead.
    /// - Editor/desktop: idle until DebugHud buttons (or -server / -client args) start something.
    /// </summary>
    [RequireComponent(typeof(NetworkManager), typeof(Bayou))]
    public class Bootstrap : MonoBehaviour
    {
        private NetworkManager _nm;
        private Bayou _bayou;

        private void Awake()
        {
            _nm = GetComponent<NetworkManager>();
            _bayou = GetComponent<Bayou>();
        }

        private void Start()
        {
#if UNITY_SERVER
            StartServer();
#elif UNITY_WEBGL && !UNITY_EDITOR
            ConfigureClientFromPageUrl();
            _nm.ClientManager.StartConnection();
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

        public void StartClient() => _nm.ClientManager.StartConnection();

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
