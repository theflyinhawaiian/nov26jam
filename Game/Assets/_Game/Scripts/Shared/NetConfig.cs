namespace Jam
{
    public static class NetConfig
    {
        /// <summary>Port the dedicated server listens on (Caddy proxies wss://domain/ to it).</summary>
        public const ushort ServerPort = 7770;
        public const int TickRate = 30;
        public const string ArenaScene = "Arena";
        public const int MaxNicknameLength = 16;
    }
}
