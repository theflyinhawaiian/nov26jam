using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Scened;
using FishNet.Object;
using FishNet.Transporting;
using Jam.Gameplay;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam.Net
{
    /// <summary>
    /// Server-side matchmaking. "Find match" is a FIFO queue; every match goes through
    /// CreateMatch, so room codes can later be a second caller of it.
    /// Each match is a stacked copy of the arena scene with its own 2D physics scene,
    /// visible only to its two connections (SceneCondition on the ObserverManager).
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public class MatchManager : MonoBehaviour
    {
        [SerializeField] private NetworkObject _playerPrefab;
        [SerializeField] private NetworkObject _matchStatePrefab;
        [SerializeField] private NetworkObject _minionPrefab;

        [Tooltip("Ticks subtracted from the shooter's LastPacketTick when rewinding for hitscan. Tuned from the M1 debug readout.")]
        [SerializeField] private float _rewindOffsetTicks = 0f;

        public float RewindOffsetTicks => _rewindOffsetTicks;

        private const float LoadTimeoutSeconds = 20f;

        private NetworkManager _nm;
        private readonly List<NetworkConnection> _queue = new();
        private readonly Dictionary<int, Match> _matches = new();
        private readonly Dictionary<NetworkConnection, Match> _matchByConn = new();
        private readonly Dictionary<NetworkConnection, string> _nicknames = new();
        private int _nextMatchId;
        private readonly List<(Match match, int winner)> _pendingEnds = new();
        private readonly List<Match> _timedOut = new();
        /// <summary>Which teams send minion waves. Debug: -minionteams blue|red|both|none on the server.</summary>
        private readonly bool[] _teamSendsMinions = { true, true };
        /// <summary>Debug: -waveseconds N on the server.</summary>
        private float _waveSeconds = LaneDirector.DefaultWaveSeconds;
        /// <summary>
        /// Which lanes are in play (indexed by Lane). Experiment: two lanes (bottom and top) by
        /// default; -lanes all|bottom,mid,top (any comma list) on the server overrides it.
        /// Lanes out of play send no minions and their towers are removed.
        /// </summary>
        private readonly bool[] _laneActive = { true, false, true };

        private void Awake()
        {
            _nm = GetComponent<NetworkManager>();
            string[] args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-minionteams");
            if (i >= 0 && i + 1 < args.Length)
            {
                string v = args[i + 1].ToLowerInvariant();
                _teamSendsMinions[0] = v is "blue" or "both";
                _teamSendsMinions[1] = v is "red" or "both";
                Debug.Log($"[MatchManager] Minion waves from: blue={_teamSendsMinions[0]} red={_teamSendsMinions[1]}");
            }
            i = System.Array.IndexOf(args, "-lanes");
            if (i >= 0 && i + 1 < args.Length)
            {
                string v = args[i + 1].ToLowerInvariant();
                foreach (Lane lane in System.Enum.GetValues(typeof(Lane)))
                    _laneActive[(int)lane] = v == "all" || System.Array.IndexOf(v.Split(','), lane.ToString().ToLowerInvariant()) >= 0;
            }
            Debug.Log($"[MatchManager] Lanes in play: bottom={_laneActive[0]} mid={_laneActive[1]} top={_laneActive[2]}");
            i = System.Array.IndexOf(args, "-waveseconds");
            if (i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float secs) && secs >= 1f)
            {
                _waveSeconds = secs;
                Debug.Log($"[MatchManager] Minion waves every {secs}s");
            }
        }

        private void Start()
        {
            _nm.ServerManager.RegisterBroadcast<FindMatchBroadcast>(OnFindMatch);
            _nm.ServerManager.RegisterBroadcast<LeaveMatchBroadcast>(OnLeaveMatch);
            _nm.ServerManager.RegisterBroadcast<HelloBroadcast>(OnHello, requireAuthentication: false);
            _nm.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
            _nm.SceneManager.OnLoadEnd += OnLoadEnd;
            _nm.SceneManager.OnClientPresenceChangeEnd += OnClientPresenceChangeEnd;
            _nm.SceneManager.OnUnloadEnd += OnUnloadEnd;
            _nm.TimeManager.OnTick += OnTick;
        }

        private void OnDestroy()
        {
            if (_nm == null)
                return;
            _nm.ServerManager.UnregisterBroadcast<FindMatchBroadcast>(OnFindMatch);
            _nm.ServerManager.UnregisterBroadcast<LeaveMatchBroadcast>(OnLeaveMatch);
            _nm.ServerManager.UnregisterBroadcast<HelloBroadcast>(OnHello);
            _nm.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
            _nm.SceneManager.OnLoadEnd -= OnLoadEnd;
            _nm.SceneManager.OnClientPresenceChangeEnd -= OnClientPresenceChangeEnd;
            _nm.SceneManager.OnUnloadEnd -= OnUnloadEnd;
            _nm.TimeManager.OnTick -= OnTick;
        }

        private readonly List<Match> _tickMatches = new();

        private void OnTick()
        {
            if (!_nm.IsServerStarted)
                return;
            // Copy: a tick can end a match (nexus destroyed is deferred, but be safe).
            _tickMatches.Clear();
            _tickMatches.AddRange(_matches.Values);
            foreach (Match m in _tickMatches)
                m.Lanes?.Tick();
        }

        private void SpawnMinion(Match m, int team, Lane lane, MinionType type)
        {
            if (m.Ended || !m.SceneLoaded || _minionPrefab == null)
                return;
            NetworkObject nob = Instantiate(_minionPrefab, LaneLayout.MinionSpawn(team, lane), Quaternion.identity);
            Minion minion = nob.GetComponent<Minion>();
            minion.Team.Value = team;
            minion.Type.Value = type;
            minion.Lane = lane;
            minion.Match = m;
            _nm.ServerManager.Spawn(nob, null, m.Scene);
            m.Minions.Add(minion);
        }

        private void Update()
        {
            foreach ((Match m, int winner) in _pendingEnds)
                EndMatch(m, winner, "nexus destroyed");
            _pendingEnds.Clear();

            // A player whose client never finishes loading the arena would otherwise strand the other one.
            _timedOut.Clear();
            foreach (Match m in _matches.Values)
                if (!m.Ended && !m.AllPlayersSpawned && Time.unscaledTime - m.CreatedAt > LoadTimeoutSeconds)
                    _timedOut.Add(m);
            foreach (Match m in _timedOut)
                EndMatch(m, -1, "a player failed to load the arena");
        }

        public bool TryGetMatch(Scene scene, out Match match)
        {
            foreach (Match m in _matches.Values)
            {
                if (m.Scene == scene)
                {
                    match = m;
                    return true;
                }
            }
            match = null;
            return false;
        }

        public string GetNickname(NetworkConnection conn) =>
            _nicknames.TryGetValue(conn, out string n) ? n : $"Player{conn.ClientId}";

        private void OnHello(NetworkConnection conn, HelloBroadcast msg, Channel channel)
        {
            _nm.ServerManager.Broadcast(conn, new ServerInfoBroadcast { Version = BuildInfo.Version }, requireAuthenticated: false);
        }

        #region Queue
        private void OnFindMatch(NetworkConnection conn, FindMatchBroadcast msg, Channel channel)
        {
            if (_queue.Contains(conn) || _matchByConn.ContainsKey(conn))
                return;

            _nicknames[conn] = SanitizeNickname(msg.Nickname, conn);
            _queue.Add(conn);
            Send(conn, MatchStatus.Queued, 0, "Waiting for an opponent...");
            TryMakeMatches();
        }

        private void OnLeaveMatch(NetworkConnection conn, LeaveMatchBroadcast msg, Channel channel)
        {
            if (_queue.Remove(conn))
                Send(conn, MatchStatus.Idle, 0, "Left the queue.");
            else if (_matchByConn.TryGetValue(conn, out Match m))
                EndMatch(m, 1 - m.TeamOf(conn), "your opponent left", leaver: conn);
        }

        private void TryMakeMatches()
        {
            _queue.RemoveAll(c => c == null || !c.IsActive);
            while (_queue.Count >= 2)
            {
                NetworkConnection a = _queue[0];
                NetworkConnection b = _queue[1];
                _queue.RemoveRange(0, 2);
                CreateMatch(a, b);
            }
        }

        private static string SanitizeNickname(string nickname, NetworkConnection conn)
        {
            nickname = (nickname ?? string.Empty).Trim();
            if (nickname.Length > NetConfig.MaxNicknameLength)
                nickname = nickname.Substring(0, NetConfig.MaxNicknameLength);
            return nickname.Length == 0 ? $"Player{conn.ClientId}" : nickname;
        }
        #endregion

        #region Match lifecycle
        public Match CreateMatch(params NetworkConnection[] players)
        {
            Match m = new Match(++_nextMatchId, players);
            _matches[m.Id] = m;
            foreach (NetworkConnection p in players)
                _matchByConn[p] = m;

            SceneLoadData sld = new SceneLoadData(NetConfig.ArenaScene);
            sld.Options.AllowStacking = true;
            sld.Options.LocalPhysics = LocalPhysicsMode.Physics2D;
            sld.Options.AutomaticallyUnload = true;
            sld.Params.ServerParams = new object[] { m.Id };
            _nm.SceneManager.LoadConnectionScenes(players, sld);

            foreach (NetworkConnection p in players)
                Send(p, MatchStatus.InMatch, m.Id, "Match found!", opponentName: GetNickname(m.Opponent(p)));
            Debug.Log($"[MatchManager] Created match {m.Id} for {string.Join(", ", System.Array.ConvertAll(players, p => GetNickname(p)))}");
            return m;
        }

        private void OnLoadEnd(SceneLoadEndEventArgs args)
        {
            if (!args.QueueData.AsServer)
                return;
            object[] sp = args.QueueData.SceneLoadData.Params.ServerParams;
            if (sp == null || sp.Length == 0 || sp[0] is not int id || !_matches.TryGetValue(id, out Match m))
                return;
            if (args.LoadedScenes.Length == 0)
                return;

            m.Scene = args.LoadedScenes[0];
            m.CacheSceneObjects();

            NetworkObject stateNob = Instantiate(_matchStatePrefab);
            _nm.ServerManager.Spawn(stateNob, null, m.Scene);
            m.State = stateNob.GetComponent<MatchState>();
            for (int lane = 0; lane < LaneLayout.LaneCount; lane++)
                if (!_laneActive[lane])
                    m.State.ServerRemoveLane((Lane)lane);
            // Deferred: this fires from inside damage code that may be iterating the match's lists.
            m.State.ServerNexusDestroyed += winner => _pendingEnds.Add((m, winner));
            m.State.ServerTowerDestroyed += index =>
                Debug.Log($"[Lanes] Match {m.Id}: {(LaneLayout.TowerTeam(index) == 0 ? "Blue" : "Red")} {LaneLayout.TowerLane(index)} tower destroyed");
            m.Lanes = new LaneDirector(m, SpawnMinion, _teamSendsMinions, _laneActive, _waveSeconds);

            // Clients that already finished loading (e.g. clientHost) won't raise a presence event later.
            foreach (NetworkConnection p in m.Players)
                if (p.Scenes.Contains(m.Scene))
                    SpawnAvatar(m, p);
        }

        private void OnClientPresenceChangeEnd(ClientPresenceChangeEventArgs args)
        {
            if (!args.Added || !_matchByConn.TryGetValue(args.Connection, out Match m))
                return;
            if (m.SceneLoaded && args.Scene == m.Scene)
                SpawnAvatar(m, args.Connection);
        }

        private void SpawnAvatar(Match m, NetworkConnection conn)
        {
            if (m.Ended || m.HasAvatarFor(conn) || !conn.IsActive)
                return;

            int team = m.TeamOf(conn);
            NetworkObject nob = Instantiate(_playerPrefab, m.SpawnPosition(team), Quaternion.identity);
            PlayerAvatar avatar = nob.GetComponent<PlayerAvatar>();
            avatar.Team.Value = team;
            avatar.Nickname.Value = GetNickname(conn);
            avatar.Match = m;
            _nm.ServerManager.Spawn(nob, conn, m.Scene);
            m.Avatars.Add(avatar);
            Debug.Log($"[MatchManager] Spawned avatar {nob.ObjectId} for client {conn.ClientId} in match {m.Id}");
            if (m.AllPlayersSpawned && m.State != null)
            {
                m.State.BeginCountdown();
                Debug.Log($"[MatchManager] Match {m.Id}: both players in, countdown started");
            }
        }

        private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped)
                return;
            _queue.Remove(conn);
            _nicknames.Remove(conn);
            if (_matchByConn.TryGetValue(conn, out Match m))
                EndMatch(m, 1 - m.TeamOf(conn), "your opponent disconnected", leaver: conn);
        }

        /// <summary>
        /// Ends a match: each remaining player is told whether they won and why, then sent back
        /// to the lobby. <paramref name="winningTeam"/> -1 cancels the match with no winner.
        /// <paramref name="leaver"/> (if any) just gets "You left the match."
        /// </summary>
        private void EndMatch(Match m, int winningTeam, string reason, NetworkConnection leaver = null)
        {
            if (m.Ended)
                return;
            m.Ended = true;
            if (m.State != null && m.State.Winner.Value < 0 && winningTeam >= 0)
                m.State.Winner.Value = winningTeam;

            foreach (PlayerAvatar a in m.Avatars)
                if (a != null && a.IsSpawned)
                    _nm.ServerManager.Despawn(a.NetworkObject);
            m.Avatars.Clear();
            foreach (Minion minion in m.Minions.ToArray())
                if (minion != null && minion.IsSpawned)
                    _nm.ServerManager.Despawn(minion.NetworkObject);
            m.Minions.Clear();

            foreach (NetworkConnection p in m.Players)
            {
                _matchByConn.Remove(p);
                if (!p.IsActive)
                    continue;
                if (m.SceneLoaded)
                    _nm.SceneManager.UnloadConnectionScenes(p, new SceneUnloadData(m.Scene));

                MatchOutcome outcome;
                string message;
                if (p == leaver)
                    (outcome, message) = (MatchOutcome.Left, "You left the match.");
                else if (winningTeam < 0)
                    (outcome, message) = (MatchOutcome.Cancelled, $"Match cancelled: {reason}.");
                else if (m.TeamOf(p) == winningTeam)
                    (outcome, message) = (MatchOutcome.Won, $"You win: {reason}!");
                else
                    (outcome, message) = (MatchOutcome.Lost, $"You lose: {reason}.");
                Send(p, MatchStatus.Ended, m.Id, message, outcome);
            }
            if (m.State != null && m.State.IsSpawned)
                _nm.ServerManager.Despawn(m.State.NetworkObject);
            _matches.Remove(m.Id);
            Debug.Log($"[MatchManager] Ended match {m.Id}: {(winningTeam < 0 ? "cancelled" : $"team {winningTeam} won")} ({reason}). {_matches.Count} match(es) running.");
        }

        private void OnUnloadEnd(SceneUnloadEndEventArgs args)
        {
            if (!args.QueueData.AsServer)
                return;
            Debug.Log($"[MatchManager] Unloaded {args.UnloadedScenesV2.Count} scene(s); {UnityEngine.SceneManagement.SceneManager.sceneCount} scene(s) now loaded on the server.");
        }
        #endregion

        private void Send(NetworkConnection conn, MatchStatus status, int matchId, string message,
            MatchOutcome outcome = MatchOutcome.None, string opponentName = null)
        {
            _nm.ServerManager.Broadcast(conn, new MatchStatusBroadcast
            {
                Status = status,
                MatchId = matchId,
                Message = message,
                Outcome = outcome,
                OpponentName = opponentName ?? string.Empty,
            });
        }
    }
}
