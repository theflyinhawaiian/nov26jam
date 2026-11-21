using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace Jam.Net
{
    /// <summary>
    /// Per-match networked state, spawned (unowned) into the match's arena scene so only
    /// that match's players observe it.
    /// </summary>
    public class MatchState : NetworkBehaviour
    {
        public const int NexusMaxHealth = 1500;

        public readonly SyncVar<int> BlueNexusHealth = new(NexusMaxHealth);
        public readonly SyncVar<int> RedNexusHealth = new(NexusMaxHealth);
        /// <summary>-1 while the match is running.</summary>
        public readonly SyncVar<int> Winner = new(-1);

        /// <summary>Server only. Argument is the winning team.</summary>
        public event Action<int> ServerNexusDestroyed;

        public int NexusHealth(int team) => team == 0 ? BlueNexusHealth.Value : RedNexusHealth.Value;

        [Server]
        public void DamageNexus(int team, int amount)
        {
            if (Winner.Value >= 0 || amount <= 0)
                return;
            SyncVar<int> hp = team == 0 ? BlueNexusHealth : RedNexusHealth;
            hp.Value = Mathf.Max(0, hp.Value - amount);
            if (hp.Value == 0)
            {
                Winner.Value = 1 - team;
                ServerNexusDestroyed?.Invoke(Winner.Value);
            }
        }
    }
}
