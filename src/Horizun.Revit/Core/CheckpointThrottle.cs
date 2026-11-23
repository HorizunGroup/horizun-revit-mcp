using System;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Bounds the lines a single job writes as checkpoints. The first ones are kept whole; afterwards a
    /// line is written when the label changes or enough time passed, up to a hard ceiling of lines and
    /// bytes. What is suppressed is counted, never silently lost: the finish line reports it (M3).
    /// </summary>
    public sealed class CheckpointThrottle
    {
        private readonly int _wholeUntil, _maxLines;
        private readonly long _maxBytes, _minIntervalMs;
        private string _lastLabel;
        private long _lastWriteMs = long.MinValue;
        private long _bytes;

        public int Written { get; private set; }
        public int Suppressed { get; private set; }

        public CheckpointThrottle(int wholeUntil = 50, int maxLines = 400, long maxBytes = 128 * 1024, long minIntervalMs = 2000)
        {
            _wholeUntil = wholeUntil; _maxLines = maxLines; _maxBytes = maxBytes; _minIntervalMs = minIntervalMs;
        }

        public bool ShouldWrite(string label, long nowMs, int approximateBytes)
        {
            bool allowed;
            if (Written >= _maxLines || _bytes + approximateBytes > _maxBytes) allowed = false;
            else if (Written < _wholeUntil) allowed = true;
            else if (!string.Equals(label, _lastLabel, StringComparison.Ordinal)) allowed = true;
            else allowed = _lastWriteMs == long.MinValue || nowMs - _lastWriteMs >= _minIntervalMs;
            if (!allowed) { Suppressed++; return false; }
            Written++; _bytes += approximateBytes; _lastLabel = label; _lastWriteMs = nowMs;
            return true;
        }
    }
}
