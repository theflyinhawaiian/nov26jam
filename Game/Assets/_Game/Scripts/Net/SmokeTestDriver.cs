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
    /// - selfnade: run back and forth near spawn throwing grenades forward, at the floor ahead,
    ///   and straight down (regression check for self-damage while moving).
    /// - pads: walk the mid lane back and forth without hopping, so every jump pad launches us
    ///   (checks the server's movement validation doesn't flag legal pad launches).
    /// - watch: walk to x = 20 on the enemy side and stand (with -smokeshot, frames the enemy
    ///   mid tower where our minions fight).
    /// - cheat: alternately teleport 15 units toward the enemy and fly straight up, to check
    ///   that the server's movement validation corrects it (corrections= in the result).
    /// -smokerequeue: after each match ends, queue again (exercises arena unload and repeat matches).
    /// -smokeshot dir: (windowed, not -batchmode) save the game's own frame to dir every 4s
    ///   once the match is live, to check the HUD without screenshotting the desktop.
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
        private int _minHealth = int.MaxValue;
        private int _grenadesThrown;
        private bool _requeue;
        private int _matches;
        private int _ends;
        private float _requeueAt = -1f;
        private float _matchFoundAt;
        private bool _sawLive;
        private string _countdowns = "";
        private bool _movedBeforeLive;
        private Vector2? _preLivePos;
        private int _cheats;
        private string _shotDir;
        private float _nextShot;
        private int _shots;
        private float _nextCheat;

        public static bool Requested => Array.IndexOf(Environment.GetCommandLineArgs(), "-smoketest") >= 0;

        private void Start()
        {
            _nm = GetComponent<NetworkManager>();
            _duration = float.TryParse(GetArg("-smokeduration"), out float d) ? d : _duration;
            _mode = GetArg("-smokemode") ?? _mode;
            _requeue = Array.IndexOf(Environment.GetCommandLineArgs(), "-smokerequeue") >= 0;
            _shotDir = GetArg("-smokeshot");

            _nm.ClientManager.OnClientConnectionState += OnClientConnectionState;
            _nm.ClientManager.RegisterBroadcast<MatchStatusBroadcast>(OnMatchStatus);
            _startTime = Time.time;
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            Debug.Log($"[Smoke] Client {args.ConnectionState}");
            if (args.ConnectionState == LocalConnectionState.Started)
                Queue();
        }

        private void Queue() =>
            _nm.ClientManager.Broadcast(new FindMatchBroadcast { Nickname = $"{_mode}{System.Diagnostics.Process.GetCurrentProcess().Id}" });

        private void OnMatchStatus(MatchStatusBroadcast msg, Channel channel)
        {
            Debug.Log($"[Smoke] Status {msg.Status} match={msg.MatchId}: {msg.Message}");
            if (msg.Status == MatchStatus.InMatch)
            {
                _matched = true;
                _matches++;
                _matchFoundAt = Time.time;
                _sawLive = false;
                _preLivePos = null;
            }
            if (msg.Status == MatchStatus.Ended)
            {
                _ends++;
                _endMessage = msg.Message;
                if (_requeue)
                    _requeueAt = Time.time + 1f;
            }
        }

        private void Update()
        {
            if (_requeueAt >= 0f && Time.time >= _requeueAt)
            {
                _requeueAt = -1f;
                Queue();
            }

            MatchState local = MatchState.Local;
            if (local != null && !_sawLive && local.IsLive)
            {
                _sawLive = true;
                _countdowns += $"{Time.time - _matchFoundAt:0.0}s ";
                Debug.Log($"[Smoke] Match live {Time.time - _matchFoundAt:0.0}s after match found");
            }

            if (_shotDir != null && local != null && local.IsLive && Time.time >= _nextShot)
            {
                _nextShot = Time.time + 4f;
                string path = System.IO.Path.Combine(_shotDir, $"hud-{++_shots:00}.png");
                ScreenCapture.CaptureScreenshot(path);
                Debug.Log($"[Smoke] Screenshot {path}");
            }

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
                MatchState state = MatchState.Local;
                string nexus = state != null ? $"nexus={state.BlueNexusHealth.Value}/{state.RedNexusHealth.Value}" : "nexus=?";
                Debug.Log($"[Smoke] avatars={avatars.Length} ping={_nm.TimeManager.RoundTripTime}ms pos={_lastOwnPos} deaths={_deaths} {nexus} " +
                          $"rifleShots={FireDiagnostics.Shots} rifleHits={FireDiagnostics.Hits} medianErr={FireDiagnostics.MedianError:0.000}");
            }

            if (Time.time - _startTime >= _duration)
            {
                Debug.Log($"SMOKE RESULT mode={_mode} matched={_matched} maxAvatarsSeen={_maxAvatarsSeen} minOwnY={_minOwnY:0.00} maxOwnY={_maxOwnY:0.00} " +
                          $"deaths={_deaths} respawns={_respawns} rifleShots={FireDiagnostics.Shots} rifleHits={FireDiagnostics.Hits} " +
                          $"medianErr={FireDiagnostics.MedianError:0.000} minHealth={_minHealth} grenades={_grenadesThrown} " +
                          $"matches={_matches} ends={_ends} liveAfter=[{_countdowns.Trim()}] movedBeforeLive={_movedBeforeLive} " +
                          $"corrections={PlayerAvatar.CorrectionsReceived} cheats={_cheats} end=\"{_endMessage}\"");
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

            if (a.IsSpawned)
                _minHealth = Mathf.Min(_minHealth, a.Health.Value);
            Vector2 pos = a.transform.position;
            if (!a.MatchLive && (a.State == null || a.State.Winner.Value < 0))
            {
                _preLivePos ??= pos;
                if (Vector2.Distance(_preLivePos.Value, pos) > 0.05f)
                    _movedBeforeLive = true;
            }
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

                case "watch":
                {
                    float hold = enemySide * 20f;
                    a.ScriptedInput = new MotorInput { X = Mathf.Abs(hold - pos.x) < 0.5f ? 0f : Mathf.Sign(hold - pos.x) };
                    break;
                }

                case "pads":
                {
                    // 7s toward the enemy, 7s back; walking onto a pad launches us.
                    float dir = Mathf.Repeat(t, 14f) < 7f ? enemySide : -enemySide;
                    a.ScriptedInput = new MotorInput { X = dir };
                    break;
                }

                case "cheat":
                {
                    a.ScriptedInput = default(MotorInput);
                    if (!a.MatchLive)
                        break;
                    // Every 2s once live: odd-numbered cheats teleport, even-numbered ones fly up
                    // at 15 u/s for a second (three times a jump's height).
                    if (Time.time >= _nextCheat)
                    {
                        _nextCheat = Time.time + 2f;
                        _cheats++;
                        if (_cheats % 2 == 1)
                        {
                            Vector2 to = pos + new Vector2(enemySide * 15f, 0f);
                            Debug.Log($"[Smoke] Cheat #{_cheats}: teleport {pos} -> {to}");
                            a.Motor.Teleport(to);
                        }
                        else
                            Debug.Log($"[Smoke] Cheat #{_cheats}: fly up from {pos}");
                    }
                    if (_cheats % 2 == 0 && _cheats > 0 && Time.time < _nextCheat - 1f)
                        a.Motor.Velocity.y = 15f;
                    break;
                }

                case "selfnade":
                {
                    // Sprint between x = ±(44..56) on our own side, flipping direction every 1.2s.
                    float side = a.Team.Value == 0 ? -1f : 1f;
                    float runDir = Mathf.Repeat(t, 2.4f) < 1.2f ? 1f : -1f;
                    if (Mathf.Abs(pos.x) < 44f) runDir = side;       // stay clear of the mid pads
                    if (Mathf.Abs(pos.x) > 56f) runDir = -side;      // and off our own nexus
                    bool hop = Mathf.Repeat(t, 2f) < Time.deltaTime * 1.5f;
                    a.ScriptedInput = new MotorInput { X = runDir, JumpPressed = hop, JumpHeld = hop };

                    a.Weapons.Select(1);
                    if (a.Weapons.CooldownRemaining(1) <= 0f)
                    {
                        // Cycle: forward-up, at the floor 2 units ahead, straight down at our feet.
                        Vector2 target = (_grenadesThrown % 3) switch
                        {
                            0 => pos + new Vector2(runDir * 6f, 3f),
                            1 => pos + new Vector2(runDir * 2f, -1.5f),
                            _ => pos + Vector2.down * 2f,
                        };
                        a.Weapons.FireAt(target);
                        _grenadesThrown++;
                    }
                    break;
                }

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
