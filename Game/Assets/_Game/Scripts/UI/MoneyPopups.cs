using System.Collections.Generic;
using UnityEngine;

namespace Jam.UI
{
    /// <summary>
    /// Client side: "+$N" popups for bounties the local player earned (PlayerAvatar.AwardBounty),
    /// drawn by MatchHud at the world point where the kill happened.
    /// </summary>
    public static class MoneyPopups
    {
        public const float Duration = 1.8f;
        /// <summary>World units a popup drifts up over its life.</summary>
        public const float Rise = 1.6f;

        public readonly struct Popup
        {
            public readonly int Amount;
            public readonly Vector2 At;
            public readonly float Start;

            public Popup(int amount, Vector2 at, float start)
            {
                Amount = amount;
                At = at;
                Start = start;
            }
        }

        private static readonly List<Popup> List = new();
        public static IReadOnlyList<Popup> Active => List;

        public static void Add(int amount, Vector2 at) => List.Add(new Popup(amount, at, Time.time));

        public static void Expire() => List.RemoveAll(p => Time.time - p.Start > Duration);
    }
}
