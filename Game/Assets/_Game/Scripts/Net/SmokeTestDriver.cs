using System;
using FishNet.Managing;
using FishNet.Transporting;
using Jam.Gameplay;
using Jam.UI;
using UnityEngine;

namespace Jam.Net
{
    /// <summary>
    /// Headless client bot for automated checks (-smoketest [-smokeduration N]).
    /// Queues for a match, strafes vertically, shoots at the opponent, logs the
    /// lag-compensation readout, then quits with "SMOKE RESULT" in the log.
    /// </summary>
    public class SmokeTestDriver : MonoBehaviour
    {
        private NetworkManager _nm;
        private float _duration = 20f;
        private float _startTime;
        private float _nextLog;
        private int _maxAvatarsSeen;
        private bool _matched;
        private float _minOwnY = float.PositiveInfinity;
        private float _maxOwnY = float.NegativeInfinity;
        private Vector2 _lastOwnPos;

        public static bool Requested => Array.IndexOf(Environment.GetCommandLineArgs(), "-smoketest") >= 0;

        private void Start()
        {
            _nm = GetComponent<NetworkManager>();
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-smokeduration");
            if (i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], out float d))
                _duration = d;

            _nm.ClientManager.OnClientConnectionState += OnClientConnectionState;
            _nm.ClientManager.RegisterBroadcast<MatchStatusBroadcast>(OnMatchStatus);
            _startTime = Time.time;
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            Debug.Log($"[Smoke] Client {args.ConnectionState}");
            if (args.ConnectionState == LocalConnectionState.Started)
                _nm.ClientManager.Broadcast(new FindMatchBroadcast { Nickname = $"Bot{System.Diagnostics.Process.GetCurrentProcess().Id}" });
        }

        private void OnMatchStatus(MatchStatusBroadcast msg, Channel channel)
        {
            Debug.Log($"[Smoke] Status {msg.Status} match={msg.MatchId}: {msg.Message}");
            if (msg.Status == MatchStatus.InMatch)
                _matched = true;
        }

        private void Update()
        {
            PlayerAvatar[] avatars = FindObjectsByType<PlayerAvatar>(FindObjectsSortMode.None);
            _maxAvatarsSeen = Mathf.Max(_maxAvatarsSeen, avatars.Length);

            foreach (PlayerAvatar a in avatars)
            {
                if (!a.IsOwner)
                    continue;
                float t = Time.time - _startTime;
                // Walk the mid lane to within rifle range (x = ±6) and hop regularly, so the
                // opponent's history has vertical motion to rewind against.
                float x = a.transform.position.x;
                float holdX = 6f * Mathf.Sign(x == 0f ? 1f : x);
                bool jump = Mathf.Repeat(t, 1.5f) < Time.deltaTime * 1.5f;
                a.ScriptedInput = new MotorInput { X = Mathf.Clamp(holdX - x, -1f, 1f), JumpPressed = jump, JumpHeld = jump };
                _minOwnY = Mathf.Min(_minOwnY, a.transform.position.y);
                _maxOwnY = Mathf.Max(_maxOwnY, a.transform.position.y);
                _lastOwnPos = a.transform.position;
                PlayerAvatar opponent = a.FindOpponent();
                if (opponent != null)
                    a.FireAt(opponent.transform.position);
            }

            if (Time.time >= _nextLog)
            {
                _nextLog = Time.time + 3f;
                Debug.Log($"[Smoke] avatars={avatars.Length} ping={_nm.TimeManager.RoundTripTime}ms shots={FireDiagnostics.Shots} hits={FireDiagnostics.Hits} " +
                          $"medianErr={FireDiagnostics.MedianError:0.000} bestOffset={FireDiagnostics.MedianBestOffset:0.0} shotAge={FireDiagnostics.LastTicksBehind}");
            }

            if (Time.time - _startTime >= _duration)
            {
                Debug.Log($"SMOKE RESULT matched={_matched} maxAvatarsSeen={_maxAvatarsSeen} minOwnY={_minOwnY:0.00} maxOwnY={_maxOwnY:0.00} lastPos={_lastOwnPos} shots={FireDiagnostics.Shots} hits={FireDiagnostics.Hits} " +
                          $"medianErr={FireDiagnostics.MedianError:0.000} bestOffset={FireDiagnostics.MedianBestOffset:0.0}");
                Application.Quit(_matched && _maxAvatarsSeen == 2 && _minOwnY > ArenaLayout.MidFloorY ? 0 : 1);
                enabled = false;
            }
        }
    }
}
