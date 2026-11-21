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
        public Scene Scene;
        public MatchState State;
        public bool Ended;

        private readonly Vector2[] _spawns = new Vector2[2];
        private readonly Nexus[] _nexuses = new Nexus[2];

        public Match(int id, NetworkConnection[] players)
        {
            Id = id;
            Players = players;
        }

        public bool SceneLoaded => Scene.IsValid() && Scene.isLoaded;
        public PhysicsScene2D Physics => Scene.GetPhysicsScene2D();
        public int TeamOf(NetworkConnection conn) => Array.IndexOf(Players, conn);
        public Vector2 SpawnPosition(int team) => _spawns[Mathf.Clamp(team, 0, 1)];
        public IReadOnlyList<Nexus> Nexuses => _nexuses;

        /// <summary>Call once the arena scene has loaded.</summary>
        public void CacheSceneObjects()
        {
            foreach (GameObject root in Scene.GetRootGameObjects())
            {
                foreach (SpawnPoint sp in root.GetComponentsInChildren<SpawnPoint>())
                    _spawns[Mathf.Clamp(sp.Team, 0, 1)] = sp.transform.position;
                foreach (Nexus n in root.GetComponentsInChildren<Nexus>())
                    _nexuses[Mathf.Clamp(n.Team, 0, 1)] = n;
            }
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
