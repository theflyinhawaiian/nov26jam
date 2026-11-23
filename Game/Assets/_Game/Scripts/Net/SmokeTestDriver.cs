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
    /// - abilities [-smokeability Heal|Teleport|Slow|Silence|Stun|Grenade]: buy that Q ability
    ///   (rerolling until offered; give the server -startmoney), then duel and use it on the opponent.
    /// - farm: drop into the lower lane and shoot enemy minions (bounties); buys upgrades back at spawn.
    /// - attack: walk to the enemy nexus (hopping over jump pads) and shoot it.
    /// - passive: stand at spawn.
    /// - selfnade: run back and forth near spawn throwing grenades forward, at the floor ahead,
    ///   and straight down (regression check for self-damage while moving).
    /// - pads: walk the mid lane back and forth without hopping, so every jump pad launches us
    ///   (checks the server's movement validation doesn't flag legal pad launches).
    /// - watch: walk to x = 20 on the enemy side and stand (with -smokeshot, frames the enemy
    ///   mid tower where our minions fight).
    /// - platnade: walk to x = 26 on our side of the mid lane and lob grenades onto the one-way
    ///   hatch at x = 20 (they should burst on it, at y = 36).
    /// - cheat: alternately teleport 15 units toward the enemy and fly straight up, to check
    ///   that the server's movement validation corrects it (corrections= in the result).
    /// -smokegun rifle|blaster|none: the gun the bot picks at the start of each match (default rifle;
    ///   none leaves it to the server's random pick when the countdown ends).
    /// -smokename name: queue with this nickname (e.g. the dev nickname "peet").
    /// -smokeshop: open the shop whenever we're in our spawn zone (with -smokeshot, to see it).
    /// -smokehover: with -smokeshop, show the tooltip of the second ability offer (for screenshots).
    /// -smokerequeue: after each match ends, queue again (exercises arena unload and repeat matches).
    /// -smokeshot dir: (windowed, not -batchmode) save the game's own frame to dir every 4s
    ///   once the match is live, to check the HUD without screenshotting the desktop.
    /// Logs progress, then quits with "SMOKE RESULT" in the log.
    /// </summary>
    public class SmokeTestDriver : MonoBehaviour
    {
        // Mid-lane jump pads (see ArenaBuilder); the attack bot hops over them.

        private NetworkManager _nm;
        private float _duration = 20f;
        private string _mode = "duel";
        private float _startTime;
        private float _nextLog;
        private float _nextGrenade;
        private float _nextBuy;
        private float _nextGunPick;
        private float _gunPromptSeenAt = -1f;
        /// <summary>-smokegun rifle|blaster: which gun the bot picks (weapon slot).</summary>
        private int _gun;
        private AbilityId _wantAbility = AbilityId.Heal;
        private int _money;
        private string _upgrades = "";
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
        /// <summary>-smokeshotevery seconds: how often -smokeshot saves a frame (default 4).</summary>
        private float _shotEvery = 4f;
        private int _shots;
        private float _nextCheat;

        public static bool Requested => Array.IndexOf(Environment.GetCommandLineArgs(), "-smoketest") >= 0;

        private void Start()
        {
            _nm = GetComponent<NetworkManager>();
            _duration = float.TryParse(GetArg("-smokeduration"), out float d) ? d : _duration;
            _mode = GetArg("-smokemode") ?? _mode;
            string gunArg = GetArg("-smokegun") ?? "rifle";
            _gun = gunArg.Equals("blaster", StringComparison.OrdinalIgnoreCase) ? PlayerWeapons.GunChoices[1]
                : gunArg.Equals("none", StringComparison.OrdinalIgnoreCase) ? -1 : PlayerWeapons.GunChoices[0];
            GameMenu.SmokeOpenShop = Array.IndexOf(Environment.GetCommandLineArgs(), "-smokeshop") >= 0;
            GameMenu.SmokeHover = Array.IndexOf(Environment.GetCommandLineArgs(), "-smokehover") >= 0;
            if (Enum.TryParse(GetArg("-smokeability") ?? "", true, out AbilityId want))
                _wantAbility = want;
            _requeue = Array.IndexOf(Environment.GetCommandLineArgs(), "-smokerequeue") >= 0;
            _shotDir = GetArg("-smokeshot");
            if (float.TryParse(GetArg("-smokeshotevery") ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float every) && every > 0f)
                _shotEvery = every;

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
            _nm.ClientManager.Broadcast(new FindMatchBroadcast { Nickname = GetArg("-smokename") ?? $"{_mode}{System.Diagnostics.Process.GetCurrentProcess().Id}" });

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
                _nextShot = Time.time + _shotEvery;
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
            {
                if (!a.IsOwner)
                    continue;
                // Pick a second in (before the countdown ends, so -smokegun isn't replaced by a random pick).
                if (_gunPromptSeenAt < 0f)
                    _gunPromptSeenAt = Time.time;
                if (_gun >= 0 && !a.Weapons.HasGun && Time.time >= _nextGunPick && Time.time - _gunPromptSeenAt > 1f)
                {
                    _nextGunPick = Time.time + 1f;
                    a.Weapons.ServerChooseGun(_gun);
                }
                Drive(a);
                // Exercise the shop: whenever we're back in our spawn zone with money, buy the cheapest upgrade left.
                if (_mode != "abilities" && Time.time >= _nextBuy && a.InSpawnZone && !a.IsDead.Value)
                {
                    _nextBuy = Time.time + 1f;
                    foreach (ShopItem item in Shop.Items)
                    {
                        if (a.Money.Value >= item.Cost && a.Level(item.Id) < item.MaxLevel)
                        {
                            Debug.Log($"[Smoke] Buying {item.Name} (${a.Money.Value})");
                            a.ServerBuy(item.Id);
                            break;
                        }
                    }
                }
                _money = a.Money.Value;
                _upgrades = $"gun={(a.Weapons.HasGun ? a.Weapons.Get(a.Weapons.Gun.Value).DisplayName : "none")} vit{a.VitalityLevel.Value}/fire{a.FirepowerLevel.Value} maxHp={a.MaxHp} hp={a.Health.Value} armor={a.Armor.Value} q={a.Abilities.Current.Value} offers={a.Abilities.OfferA.Value}/{a.Abilities.OfferB.Value}" +
                            $"{(a.Slowed ? " SLOWED" : "")}{(a.Silenced ? " SILENCED" : "")}{(a.Stunned ? " STUNNED" : "")}";
            }

            if (Time.time >= _nextLog)
            {
                _nextLog = Time.time + 3f;
                MatchState state = MatchState.Local;
                string nexus = state != null ? $"nexus={state.BlueNexusHealth.Value}/{state.RedNexusHealth.Value}" : "nexus=?";
                Debug.Log($"[Smoke] avatars={avatars.Length} ping={_nm.TimeManager.RoundTripTime}ms pos={_lastOwnPos} deaths={_deaths} {nexus} money={_money} {_upgrades} " +
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
            ArenaDef arena = Arenas.For(a.gameObject);
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

                    if (a.Abilities.CooldownRemaining <= 0f)
                    {
                        // Cycle: forward-up, at the floor 2 units ahead, straight down at our feet.
                        Vector2 target = (_grenadesThrown % 3) switch
                        {
                            0 => pos + new Vector2(runDir * 6f, 3f),
                            1 => pos + new Vector2(runDir * 2f, -1.5f),
                            _ => pos + Vector2.down * 2f,
                        };
                        a.Abilities.TryUse(target);
                        _grenadesThrown++;
                    }
                    break;
                }

                case "platnade":
                {
                    // Walk to x = ±26 on our side of the mid lane, then lob grenades onto the
                    // one-way hatch at ±20 (they should burst on it, not fall to the bottom lane).
                    float side = a.Team.Value == 0 ? -1f : 1f;
                    float targetX = side * 26f;
                    float dir = Mathf.Sign(targetX - pos.x);
                    bool arrived = Mathf.Abs(targetX - pos.x) < 0.5f;
                    bool padAhead = HopAhead(arena, pos, dir);
                    a.ScriptedInput = new MotorInput { X = arrived ? 0f : dir, JumpPressed = !arrived && padAhead && a.Motor.Grounded, JumpHeld = !arrived };
                    if (arrived && a.Motor.Grounded && a.Abilities.CooldownRemaining <= 0f)
                    {
                        a.Abilities.TryUse(new Vector2(side * 20f, arena.BaseFloorY + 1f));
                        _grenadesThrown++;
                    }
                    break;
                }

                case "abilities":
                {
                    // Shop for -smokeability (buying it when offered, rerolling otherwise), then
                    // walk to rifle range of the opponent and use Q on them whenever it's ready.
                    PlayerAbilities ab = a.Abilities;
                    bool shopping = ab.Current.Value != _wantAbility && a.InSpawnZone && a.MatchLive && t < 30f;
                    if (shopping)
                    {
                        a.ScriptedInput = new MotorInput();
                        if (Time.time >= _nextBuy)
                        {
                            _nextBuy = Time.time + 0.5f;
                            int slot = ab.OfferA.Value == _wantAbility ? 0 : ab.OfferB.Value == _wantAbility ? 1 : -1;
                            if (slot >= 0)
                            {
                                Debug.Log($"[Smoke] Buying ability {_wantAbility} (${a.Money.Value})");
                                ab.ServerBuy((byte)slot);
                            }
                            else
                            {
                                Debug.Log($"[Smoke] Rerolling offers {ab.OfferA.Value}/{ab.OfferB.Value} (${a.Money.Value})");
                                ab.ServerReroll();
                            }
                        }
                        break;
                    }
                    float holdX = 6f * Mathf.Sign(pos.x == 0f ? 1f : pos.x);
                    float dir = Mathf.Abs(holdX - pos.x) < 0.5f ? 0f : Mathf.Sign(holdX - pos.x);
                    a.ScriptedInput = new MotorInput { X = dir, JumpPressed = dir != 0f && HopAhead(arena, pos, dir) && a.Motor.Grounded, JumpHeld = dir != 0f };
                    PlayerAvatar opp = a.Weapons.FindOpponent();
                    if (opp == null || opp.IsDead.Value || Mathf.Abs(pos.x) > 20f)
                        break;
                    // Only the Heal bot's opponent needs to take damage; otherwise keep both alive.
                    if (opp.Abilities != null && opp.Abilities.Current.Value == AbilityId.Heal)
                    {
                        a.Weapons.FireAt(opp.transform.position);
                    }
                    if (ab.CooldownRemaining <= 0f && ab.TryUse(opp.transform.position))
                        Debug.Log($"[Smoke] Used {ab.Current.Value} at {opp.transform.position} from {pos}");
                    break;
                }

                case "farm":
                {
                    // Drop through our jungle-floor hatch at ±20 into the lower lane, then walk
                    // toward the middle shooting the nearest enemy minion (minion bounties; when
                    // we die we respawn in the spawn zone and buy upgrades).
                    float side = a.Team.Value == 0 ? -1f : 1f;
                    bool inLower = pos.y < arena.BaseFloorY - 1f;
                    Minion best = null;
                    float bestD = 24f;
                    foreach (Minion m in Minion.Active)
                    {
                        if (m == null || m.Team.Value == a.Team.Value || m.Health.Value <= 0)
                            continue;
                        float d = Vector2.Distance(m.transform.position, pos);
                        if (d < bestD && Mathf.Abs(m.transform.position.y - pos.y) < 4f)
                        {
                            bestD = d;
                            best = m;
                        }
                    }
                    if (!inLower)
                    {
                        // Outer edge of the hatch (17-23), clear of the high pad below it (19-21).
                        float targetX = side * 22.3f;
                        float dir = Mathf.Sign(targetX - pos.x);
                        bool over = Mathf.Abs(targetX - pos.x) < 0.4f;
                        a.ScriptedInput = new MotorInput { X = over ? 0f : dir, DownHeld = over, JumpPressed = (over && a.Motor.Grounded) || (!over && HopAhead(arena, pos, dir) && a.Motor.Grounded), JumpHeld = !over };
                    }
                    else
                    {
                        // Hold at ±26, between the pad at ±20 and our tower at ±34; enemy waves come to us.
                        float holdX = side * 26f;
                        a.ScriptedInput = new MotorInput { X = Mathf.Abs(holdX - pos.x) < 1f ? 0f : Mathf.Sign(holdX - pos.x) };
                    }
                    if (best != null)
                        a.Weapons.FireAt(best.transform.position);
                    break;
                }

                case "attack":
                {
                    // Stop ~11 units short of the enemy nexus (x = ±55), inside rifle range.
                    float targetX = enemySide * 44f;
                    float dir = Mathf.Sign(targetX - pos.x);
                    bool arrived = Mathf.Abs(targetX - pos.x) < 1f;
                    bool padAhead = HopAhead(arena, pos, dir);
                    bool jump = !arrived && padAhead && a.Motor.Grounded;
                    a.ScriptedInput = new MotorInput { X = arrived ? 0f : dir, JumpPressed = jump, JumpHeld = !arrived };
                    if (Mathf.Abs(enemySide * arena.NexusX - pos.x) < 22f)
                        a.Weapons.FireAt(new Vector2(enemySide * arena.NexusX, arena.BaseFloorY + arena.NexusHeight * 0.5f));
                    break;
                }

                default: // duel
                {
                    // Walk the mid lane to within rifle range (x = ±6) and hop regularly, so the
                    // opponent's history has vertical motion to rewind against.
                    float holdX = 6f * Mathf.Sign(pos.x == 0f ? 1f : pos.x);
                    float moveX = Mathf.Clamp(holdX - pos.x, -1f, 1f);
                    // Hop regularly, and over our own nexus on the way out of spawn.
                    bool jump = Mathf.Repeat(t, 1.5f) < Time.deltaTime * 1.5f
                                || (Mathf.Abs(moveX) > 0.5f && HopAhead(arena, pos, Mathf.Sign(moveX)) && a.Motor.Grounded);
                    a.ScriptedInput = new MotorInput { X = moveX, JumpPressed = jump, JumpHeld = jump || Mathf.Abs(moveX) > 0.5f };

                    PlayerAvatar opponent = a.Weapons.FindOpponent();
                    if (opponent == null || opponent.IsDead.Value)
                        break;
                    if (Time.time >= _nextGrenade)
                    {
                        _nextGrenade = Time.time + 3f;
                            a.Abilities.TryUse(opponent.transform.position + Vector3.up * 2f);
                    }
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
        /// <summary>
        /// A jump pad or our own nexus (we spawn behind it) just ahead on the base floor: hop it.
        /// </summary>
        private static bool HopAhead(ArenaDef arena, Vector2 pos, float dir)
        {
            foreach (float p in arena.BasePadXs)
            {
                float ahead = (p - pos.x) * dir;
                if (ahead > 0f && ahead < 3f)
                    return true;
            }
            foreach (float side in new[] { -1f, 1f })
            {
                float face = side * arena.NexusX - dir * arena.NexusWidth * 0.5f;
                float ahead = (face - pos.x) * dir;
                if (ahead > 0f && ahead < 2f)
                    return true;
            }
            return false;
        }
    }
}
