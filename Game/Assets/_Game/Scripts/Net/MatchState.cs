using System;
using FishNet.Object;
using Jam.Gameplay;
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
        public const int TowerMaxHealth = 800;
        public const float CountdownSeconds = 3f;
        /// <summary>Server leniency for fire requests that arrive just before StartTick (client tick drift).</summary>
        private const uint StartToleranceTicks = 3;

        public readonly SyncVar<int> BlueNexusHealth = new(NexusMaxHealth);
        public readonly SyncVar<int> RedNexusHealth = new(NexusMaxHealth);
        /// <summary>-1 while the match is running.</summary>
        public readonly SyncVar<int> Winner = new(-1);
        /// <summary>0 until both players have loaded; then the tick at which play starts (end of the countdown).</summary>
        public readonly SyncVar<uint> StartTick = new();
        /// <summary>Health of each tower, indexed by LaneLayout.TowerIndex(team, lane).</summary>
        public readonly SyncList<int> TowerHealth = new();
        /// <summary>Tick at which the next minion wave spawns (0 before the first is scheduled).</summary>
        public readonly SyncVar<uint> NextWaveTick = new();

        /// <summary>Client only: the state of the match this client is in (a client only ever observes one).</summary>
        public static MatchState Local { get; private set; }

        /// <summary>Server only. Argument is the winning team.</summary>
        public event Action<int> ServerNexusDestroyed;

        public int NexusHealth(int team) => team == 0 ? BlueNexusHealth.Value : RedNexusHealth.Value;

        public int TowersStanding(int team)
        {
            int n = 0;
            for (int lane = 0; lane < LaneLayout.LaneCount; lane++)
            {
                int i = LaneLayout.TowerIndex(team, (Lane)lane);
                if (i < TowerHealth.Count && TowerHealth[i] > 0)
                    n++;
            }
            return n;
        }

        /// <summary>A nexus can't be damaged while any of its team's towers stand.</summary>
        public bool NexusShielded(int team) => TowersStanding(team) > 0;

        /// <summary>Seconds until the next wave (0 if none is scheduled).</summary>
        public float NextWaveIn => NextWaveTick.Value == 0 ? 0f
            : Mathf.Max(0f, (float)(((long)NextWaveTick.Value - (long)TimeManager.Tick) * TimeManager.TickDelta));

        public bool CountdownStarted => StartTick.Value != 0;

        /// <summary>True once the countdown has finished and nobody has won yet.</summary>
        public bool IsLive => CountdownStarted && TimeManager.Tick + (IsServerStarted ? StartToleranceTicks : 0) >= StartTick.Value && Winner.Value < 0;

        /// <summary>Seconds left on the countdown (negative once play has started).</summary>
        public float CountdownRemaining => CountdownStarted
            ? (float)(((long)StartTick.Value - (long)TimeManager.Tick) * TimeManager.TickDelta)
            : CountdownSeconds;

        /// <summary>TowerHealth value for a tower whose lane is out of play: not alive, not destroyed, not drawn.</summary>
        public const int TowerNotInPlay = -1;

        public static bool TowerInPlay(int health) => health != TowerNotInPlay;

        /// <summary>Server, before play: take a lane out of the match (both teams' towers on it).</summary>
        public void ServerRemoveLane(Lane lane)
        {
            for (int team = 0; team < 2; team++)
                TowerHealth[LaneLayout.TowerIndex(team, lane)] = TowerNotInPlay;
        }

        public override void OnStartServer()
        {
            TowerHealth.Clear();
            for (int i = 0; i < LaneLayout.TowerCount; i++)
                TowerHealth.Add(TowerMaxHealth);
        }

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
            if (!IsLive || amount <= 0 || NexusShielded(team))
                return;
            SyncVar<int> hp = team == 0 ? BlueNexusHealth : RedNexusHealth;
            hp.Value = Mathf.Max(0, hp.Value - amount);
            if (hp.Value == 0)
            {
                Winner.Value = 1 - team;
                ServerNexusDestroyed?.Invoke(Winner.Value);
            }
        }

        /// <summary>Server only. Argument is the destroyed tower's index.</summary>
        public event Action<int> ServerTowerDestroyed;

        [Server]
        public void DamageTower(int index, int amount)
        {
            if (!IsLive || amount <= 0 || index < 0 || index >= TowerHealth.Count || TowerHealth[index] <= 0)
                return;
            TowerHealth[index] = Mathf.Max(0, TowerHealth[index] - amount);
            if (TowerHealth[index] == 0)
            {
                ObserversTowerDestroyed(index);
                ServerTowerDestroyed?.Invoke(index);
            }
        }

        /// <summary>Visual only: a tower shot from its muzzle to a point.</summary>
        [ObserversRpc]
        public void ObserversTowerShot(int index, Vector2 to)
        {
            int team = LaneLayout.TowerTeam(index);
            Lane lane = LaneLayout.TowerLane(index);
            Vector2 muzzle = LaneLayout.TowerCenter(team, lane) + Vector2.up * (LaneLayout.TowerHeight * 0.4f);
            Fx.Tracer(muzzle, to, Color.Lerp(PlayerAvatar.TeamColor(team), Color.white, 0.4f), 0.12f, 0.16f);
        }

        [ObserversRpc]
        private void ObserversTowerDestroyed(int index)
        {
            Vector2 c = LaneLayout.TowerCenter(LaneLayout.TowerTeam(index), LaneLayout.TowerLane(index));
            Fx.Explosion(c, 3f);
        }
    }
}
