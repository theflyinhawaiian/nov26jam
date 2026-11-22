using System;
using System.Collections.Generic;
using FishNet.Connection;
using Jam.Gameplay;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam.Net
{
    /// <summary>
    /// Server-side state for one running match. Everything match-scoped (physics queries,
    /// object lookups) goes through here rather than global Physics2D / FindObjectOfType,
    /// because several arena copies live in the same server process.
    /// </summary>
    public class Match
    {
        public readonly int Id;
        public readonly NetworkConnection[] Players;
        public readonly List<PlayerAvatar> Avatars = new();
        public readonly List<Minion> Minions = new();
        /// <summary>Route milestones already logged (minion debug output, once each).</summary>
        public readonly HashSet<string> LoggedMilestones = new();
        /// <summary>Waves and tower fire; created once the arena has loaded.</summary>
        public LaneDirector Lanes;
        public Scene Scene;
        public MatchState State;
        public bool Ended;
        /// <summary>Time.unscaledTime when CreateMatch ran; used for the load timeout.</summary>
        public readonly float CreatedAt = Time.unscaledTime;

        private readonly Vector2[] _spawns = new Vector2[2];
        private readonly Nexus[] _nexuses = new Nexus[2];
        private readonly Tower[] _towers = new Tower[LaneLayout.TowerCount];

        public Match(int id, NetworkConnection[] players)
        {
            Id = id;
            Players = players;
        }

        public bool SceneLoaded => Scene.IsValid() && Scene.isLoaded;
        public PhysicsScene2D Physics => Scene.GetPhysicsScene2D();
        public int TeamOf(NetworkConnection conn) => Array.IndexOf(Players, conn);
        public NetworkConnection Opponent(NetworkConnection conn) => Players[1 - TeamOf(conn)];
        public bool AllPlayersSpawned => Avatars.Count == Players.Length;
        public Vector2 SpawnPosition(int team) => _spawns[Mathf.Clamp(team, 0, 1)];
        public IReadOnlyList<Nexus> Nexuses => _nexuses;
        public IReadOnlyList<Tower> Towers => _towers;

        /// <summary>Call once the arena scene has loaded.</summary>
        public void CacheSceneObjects()
        {
            foreach (GameObject root in Scene.GetRootGameObjects())
            {
                foreach (SpawnPoint sp in root.GetComponentsInChildren<SpawnPoint>())
                    _spawns[Mathf.Clamp(sp.Team, 0, 1)] = sp.transform.position;
                foreach (Nexus n in root.GetComponentsInChildren<Nexus>())
                {
                    _nexuses[Mathf.Clamp(n.Team, 0, 1)] = n;
                    n.Match = this;
                }
                foreach (Tower t in root.GetComponentsInChildren<Tower>())
                {
                    _towers[t.Index] = t;
                    t.Match = this;
                }
            }
        }

        /// <summary>
        /// Fills <paramref name="into"/> with everything that can be damaged in this match:
        /// players, minions, towers, nexuses (alive or not; check Attackable). A snapshot, so
        /// damage that kills or despawns while iterating it is safe.
        /// </summary>
        public void CollectTargets(List<IDamageable> into)
        {
            into.Clear();
            foreach (PlayerAvatar a in Avatars)
                if (a != null)
                    into.Add(a);
            foreach (Minion m in Minions)
                if (m != null)
                    into.Add(m);
            foreach (Tower t in _towers)
                if (t != null)
                    into.Add(t);
            foreach (Nexus n in _nexuses)
                if (n != null)
                    into.Add(n);
        }

        public bool TryGetNexus(Collider2D collider, out Nexus nexus)
        {
            foreach (Nexus n in _nexuses)
            {
                if (n != null && n.Collider == collider)
                {
                    nexus = n;
                    return true;
                }
            }
            nexus = null;
            return false;
        }

        public bool HasAvatarFor(NetworkConnection conn)
        {
            foreach (PlayerAvatar a in Avatars)
                if (a != null && a.Owner == conn)
                    return true;
            return false;
        }
    }
}
