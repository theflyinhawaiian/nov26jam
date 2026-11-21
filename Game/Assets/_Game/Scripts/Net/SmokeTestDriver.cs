using System;
using FishNet.Managing;
using FishNet.Transporting;
using Jam.Gameplay;
using Jam.UI;
using UnityEngine;

namespace Jam.Net
{
    /// <summary>
    /// Headless client bot for automated checks: -smoketest [-smokeduration N] [-smokemode duel|attack|passive]
    /// - duel: walk the mid lane to within rifle range, hop, shoot the opponent; a grenade every 3s.
    /// - attack: walk to the enemy nexus (hopping over jump pads) and shoot it.
    /// - passive: stand at spawn.
    /// Logs progress, then quits with "SMOKE RESULT" in the log.
    /// </summary>
    public class SmokeTestDriver : MonoBehaviour
    {
        // Mid-lane jump pads (see ArenaBuilder); the attack bot hops over them.
        private static readonly float[] MidLanePadXs = { -50f, -38f, -14f, -2f, 2f, 14f, 38f, 50f };

        private NetworkManager _nm;
        private float _duration = 20f;
        private string _mode = "duel";
        private float _startTime;
        private float _nextLog;
        private float _nextGrenade;
        private int _maxAvatarsSeen;
        private bool _matched;
        private string _endMessage = "";
        private float _minOwnY = float.PositiveInfinity;
        private float _maxOwnY = float.NegativeInfinity;
        private Vector2 _lastOwnPos;
        private int _deaths;
        private int _respawns;
        private bool _wasDead;

        public static bool Requested => Array.IndexOf(Environment.GetCommandLineArgs(), "-smoketest") >= 0;

        private void Start()
        {
            _nm = GetComponent<NetworkManager>();
            _duration = float.TryParse(GetArg("-smokeduration"), out float d) ? d : _duration;
            _mode = GetArg("-smokemode") ?? _mode;

            _nm.ClientManager.OnClientConnectionState += OnClientConnectionState;
            _nm.ClientManager.RegisterBroadcast<MatchStatusBroadcast>(OnMatchStatus);
            _startTime = Time.time;
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            Debug.Log($"[Smoke] Client {args.ConnectionState}");
            if (args.ConnectionState == LocalConnectionState.Started)
                _nm.ClientManager.Broadcast(new FindMatchBroadcast { Nickname = $"{_mode}{System.Diagnostics.Process.GetCurrentProcess().Id}" });
        }

        private void OnMatchStatus(MatchStatusBroadcast msg, Channel channel)
        {
            Debug.Log($"[Smoke] Status {msg.Status} match={msg.MatchId}: {msg.Message}");
            if (msg.Status == MatchStatus.InMatch)
                _matched = true;
            if (msg.Status == MatchStatus.Ended)
                _endMessage = msg.Message;
        }

        private void Update()
        {
            PlayerAvatar[] avatars = FindObjectsByType<PlayerAvatar>(FindObjectsSortMode.None);
            if (avatars.Length > _maxAvatarsSeen)
            {
                string list = string.Join(", ", Array.ConvertAll(avatars, x => $"{x.name}#{x.ObjectId} owner={x.OwnerId} spawned={x.IsSpawned} scene={x.gameObject.scene.name}"));
                Debug.Log($"[Smoke] Avatars now {avatars.Length}: {list}");
            }
            _maxAvatarsSeen = Mathf.Max(_maxAvatarsSeen, avatars.Length);

            foreach (PlayerAvatar a in avatars)
                if (a.IsOwner)
                    Drive(a);

            if (Time.time >= _nextLog)
            {
                _nextLog = Time.time + 3f;
                MatchState state = FindAnyObjectByType<MatchState>();
                string nexus = state != null ? $"nexus={state.BlueNexusHealth.Value}/{state.RedNexusHealth.Value}" : "nexus=?";
                Debug.Log($"[Smoke] avatars={avatars.Length} ping={_nm.TimeManager.RoundTripTime}ms pos={_lastOwnPos} deaths={_deaths} {nexus} " +
                          $"rifleShots={FireDiagnostics.Shots} rifleHits={FireDiagnostics.Hits} medianErr={FireDiagnostics.MedianError:0.000}");
            }

            if (Time.time - _startTime >= _duration)
            {
                Debug.Log($"SMOKE RESULT mode={_mode} matched={_matched} maxAvatarsSeen={_maxAvatarsSeen} minOwnY={_minOwnY:0.00} maxOwnY={_maxOwnY:0.00} " +
                          $"deaths={_deaths} respawns={_respawns} rifleShots={FireDiagnostics.Shots} rifleHits={FireDiagnostics.Hits} " +
                          $"medianErr={FireDiagnostics.MedianError:0.000} end=\"{_endMessage}\"");
                Application.Quit(_matched ? 0 : 1);
                enabled = false;
            }
        }

        private void Drive(PlayerAvatar a)
        {
            if (a.IsDead.Value != _wasDead)
            {
                if (a.IsDead.Value) _deaths++;
                else _respawns++;
                _wasDead = a.IsDead.Value;
            }
            if (a.IsDead.Value)
                return;

            Vector2 pos = a.transform.position;
            _minOwnY = Mathf.Min(_minOwnY, pos.y);
            _maxOwnY = Mathf.Max(_maxOwnY, pos.y);
            _lastOwnPos = pos;
            float t = Time.time - _startTime;
            float enemySide = a.Team.Value == 0 ? 1f : -1f;

            switch (_mode)
            {
                case "passive":
                    a.ScriptedInput = default(MotorInput);
                    break;

                case "attack":
                {
                    // Stop ~16 units short of the enemy nexus (x = ±60), inside rifle range.
                    float targetX = enemySide * 44f;
                    float dir = Mathf.Sign(targetX - pos.x);
                    bool arrived = Mathf.Abs(targetX - pos.x) < 1f;
                    bool padAhead = false;
                    foreach (float p in MidLanePadXs)
                    {
                        float ahead = (p - pos.x) * dir;
                        if (ahead > 0f && ahead < 3f)
                            padAhead = true;
                    }
                    bool jump = !arrived && padAhead && a.Motor.Grounded;
                    a.ScriptedInput = new MotorInput { X = arrived ? 0f : dir, JumpPressed = jump, JumpHeld = !arrived };
                    a.Weapons.Select(0);
                    if (Mathf.Abs(enemySide * 60f - pos.x) < 22f)
                        a.Weapons.FireAt(new Vector2(enemySide * 60f, ArenaLayout.MidFloorY + 3f));
                    break;
                }

                default: // duel
                {
                    // Walk the mid lane to within rifle range (x = ±6) and hop regularly, so the
                    // opponent's history has vertical motion to rewind against.
                    float holdX = 6f * Mathf.Sign(pos.x == 0f ? 1f : pos.x);
                    bool jump = Mathf.Repeat(t, 1.5f) < Time.deltaTime * 1.5f;
                    a.ScriptedInput = new MotorInput { X = Mathf.Clamp(holdX - pos.x, -1f, 1f), JumpPressed = jump, JumpHeld = jump };

                    PlayerAvatar opponent = a.Weapons.FindOpponent();
                    if (opponent == null || opponent.IsDead.Value)
                        break;
                    if (Time.time >= _nextGrenade)
                    {
                        _nextGrenade = Time.time + 3f;
                        a.Weapons.Select(1);
                        a.Weapons.FireAt(opponent.transform.position + Vector3.up * 2f);
                    }
                    a.Weapons.Select(0);
                    a.Weapons.FireAt(opponent.transform.position);
                    break;
                }
            }
        }

        private static string GetArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
