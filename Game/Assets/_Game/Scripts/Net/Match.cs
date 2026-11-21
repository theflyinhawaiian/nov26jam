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
        public bool Ended;

        public Match(int id, NetworkConnection[] players)
        {
            Id = id;
            Players = players;
        }

        public bool SceneLoaded => Scene.IsValid() && Scene.isLoaded;
        public PhysicsScene2D Physics => Scene.GetPhysicsScene2D();
        public int TeamOf(NetworkConnection conn) => Array.IndexOf(Players, conn);

        public bool HasAvatarFor(NetworkConnection conn)
        {
            foreach (PlayerAvatar a in Avatars)
                if (a != null && a.Owner == conn)
                    return true;
            return false;
        }
    }
}
