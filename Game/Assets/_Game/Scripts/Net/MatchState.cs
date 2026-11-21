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
        public const float CountdownSeconds = 3f;
        /// <summary>Server leniency for fire requests that arrive just before StartTick (client tick drift).</summary>
        private const uint StartToleranceTicks = 3;

        public readonly SyncVar<int> BlueNexusHealth = new(NexusMaxHealth);
        public readonly SyncVar<int> RedNexusHealth = new(NexusMaxHealth);
        /// <summary>-1 while the match is running.</summary>
        public readonly SyncVar<int> Winner = new(-1);
        /// <summary>0 until both players have loaded; then the tick at which play starts (end of the countdown).</summary>
        public readonly SyncVar<uint> StartTick = new();

        /// <summary>Client only: the state of the match this client is in (a client only ever observes one).</summary>
        public static MatchState Local { get; private set; }

        /// <summary>Server only. Argument is the winning team.</summary>
        public event Action<int> ServerNexusDestroyed;

        public int NexusHealth(int team) => team == 0 ? BlueNexusHealth.Value : RedNexusHealth.Value;

        public bool CountdownStarted => StartTick.Value != 0;

        /// <summary>True once the countdown has finished and nobody has won yet.</summary>
        public bool IsLive => CountdownStarted && TimeManager.Tick + (IsServerStarted ? StartToleranceTicks : 0) >= StartTick.Value && Winner.Value < 0;

        /// <summary>Seconds left on the countdown (negative once play has started).</summary>
        public float CountdownRemaining => CountdownStarted
            ? (float)(((long)StartTick.Value - (long)TimeManager.Tick) * TimeManager.TickDelta)
            : CountdownSeconds;

        public override void OnStartClient() => Local = this;

        public override void OnStopClient()
        {
            if (Local == this)
                Local = null;
        }

        [Server]
        public void BeginCountdown()
        {
            if (!CountdownStarted)
                StartTick.Value = TimeManager.Tick + TimeManager.TimeToTicks(CountdownSeconds);
        }

        [Server]
        public void DamageNexus(int team, int amount)
        {
            if (!IsLive || amount <= 0)
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
