using FishNet.Broadcast;

namespace Jam.Net
{
    public enum MatchStatus : byte
    {
        Idle,
        Queued,
        InMatch,
        Ended,
    }

    /// <summary>Client → server: put me in the matchmaking queue.</summary>
    public struct FindMatchBroadcast : IBroadcast
    {
        public string Nickname;
    }

    /// <summary>Client → server: leave the queue or the current match.</summary>
    public struct LeaveMatchBroadcast : IBroadcast { }

    /// <summary>Server → client: lobby/match state changes.</summary>
    public struct MatchStatusBroadcast : IBroadcast
    {
        public MatchStatus Status;
        public int MatchId;
        public string Message;
    }
}
