using System.Collections.Generic;
using System.Linq;

namespace Jam.UI
{
    /// <summary>
    /// M1 lag-compensation readout, client side. "Error" is the distance between where this
    /// client saw the opponent and where the server rewound them to; "best offset" is the
    /// rewind offset (ticks) that would have minimised that error.
    /// </summary>
    public static class FireDiagnostics
    {
        private const int Window = 30;
        private static readonly Queue<float> _errors = new();
        private static readonly Queue<float> _bestOffsets = new();

        /// <summary>Show rewind ghosts and the readout (toggle with the backquote key).</summary>
        public static bool Enabled;

        public static int Shots { get; private set; }
        public static int Hits { get; private set; }
        public static bool LastHit { get; private set; }
        public static float LastError { get; private set; }
        public static int LastTicksBehind { get; private set; }

        public static float MedianError => Median(_errors);
        public static float MedianBestOffset => Median(_bestOffsets);

        public static void Record(bool hit, float error, float bestOffset, float bestError, int ticksBehind)
        {
            Shots++;
            if (hit)
                Hits++;
            LastHit = hit;
            LastError = error;
            LastTicksBehind = ticksBehind;

            // Only learn from shots where some offset actually explained what the client saw
            // (a stationary opponent matches every offset equally and tells us nothing).
            Push(_errors, error);
            if (bestError < 0.25f)
                Push(_bestOffsets, bestOffset);
        }

        public static void Reset()
        {
            Shots = Hits = 0;
            _errors.Clear();
            _bestOffsets.Clear();
        }

        private static void Push(Queue<float> q, float v)
        {
            q.Enqueue(v);
            while (q.Count > Window)
                q.Dequeue();
        }

        private static float Median(Queue<float> q)
        {
            if (q.Count == 0)
                return float.NaN;
            float[] sorted = q.OrderBy(v => v).ToArray();
            return sorted[sorted.Length / 2];
        }
    }
}
