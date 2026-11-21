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

        [Tooltip("Ticks subtracted from the shooter's LastPacketTick when rewinding for hitscan. Tuned from the M1 debug readout.")]
        [SerializeField] private float _rewindOffsetTicks = 0f;

        public float RewindOffsetTicks => _rewindOffsetTicks;

        private NetworkManager _nm;
        private readonly List<NetworkConnection> _queue = new();
        private readonly Dictionary<int, Match> _matches = new();
        private readonly Dictionary<NetworkConnection, Match> _matchByConn = new();
        private readonly Dictionary<NetworkConnection, string> _nicknames = new();
        private int _nextMatchId;

        private void Awake()
        {
            _nm = GetComponent<NetworkManager>();
        }

        private void Start()
        {
            _nm.ServerManager.RegisterBroadcast<FindMatchBroadcast>(OnFindMatch);
            _nm.ServerManager.RegisterBroadcast<LeaveMatchBroadcast>(OnLeaveMatch);
            _nm.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
            _nm.SceneManager.OnLoadEnd += OnLoadEnd;
            _nm.SceneManager.OnClientPresenceChangeEnd += OnClientPresenceChangeEnd;
        }

        private void OnDestroy()
        {
            if (_nm == null)
                return;
            _nm.ServerManager.UnregisterBroadcast<FindMatchBroadcast>(OnFindMatch);
            _nm.ServerManager.UnregisterBroadcast<LeaveMatchBroadcast>(OnLeaveMatch);
            _nm.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
            _nm.SceneManager.OnLoadEnd -= OnLoadEnd;
            _nm.SceneManager.OnClientPresenceChangeEnd -= OnClientPresenceChangeEnd;
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
                EndMatch(m, conn, "Opponent left the match.");
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
                Send(p, MatchStatus.InMatch, m.Id, "Match found!");
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
            Vector3 pos = FindSpawn(m.Scene, team);
            NetworkObject nob = Instantiate(_playerPrefab, pos, Quaternion.identity);
            PlayerAvatar avatar = nob.GetComponent<PlayerAvatar>();
            avatar.Team.Value = team;
            avatar.Nickname.Value = GetNickname(conn);
            _nm.ServerManager.Spawn(nob, conn, m.Scene);
            m.Avatars.Add(avatar);
        }

        private static Vector3 FindSpawn(Scene scene, int team)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (SpawnPoint sp in root.GetComponentsInChildren<SpawnPoint>())
                    if (sp.Team == team)
                        return sp.transform.position;
            return Vector3.zero;
        }

        private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped)
                return;
            _queue.Remove(conn);
            _nicknames.Remove(conn);
            if (_matchByConn.TryGetValue(conn, out Match m))
                EndMatch(m, conn, "Opponent disconnected.");
        }

        /// <summary>Ends a match; everyone except <paramref name="leaver"/> is told why and sent back to the lobby.</summary>
        private void EndMatch(Match m, NetworkConnection leaver, string reason)
        {
            if (m.Ended)
                return;
            m.Ended = true;

            foreach (PlayerAvatar a in m.Avatars)
                if (a != null && a.IsSpawned)
                    _nm.ServerManager.Despawn(a.NetworkObject);
            m.Avatars.Clear();

            foreach (NetworkConnection p in m.Players)
            {
                _matchByConn.Remove(p);
                if (!p.IsActive)
                    continue;
                if (m.SceneLoaded)
                    _nm.SceneManager.UnloadConnectionScenes(p, new SceneUnloadData(m.Scene));
                Send(p, MatchStatus.Ended, m.Id, p == leaver ? "You left the match." : reason);
            }
            _matches.Remove(m.Id);
            Debug.Log($"[MatchManager] Ended match {m.Id}: {reason}");
        }
        #endregion

        private void Send(NetworkConnection conn, MatchStatus status, int matchId, string message)
        {
            _nm.ServerManager.Broadcast(conn, new MatchStatusBroadcast { Status = status, MatchId = matchId, Message = message });
        }
    }
}
