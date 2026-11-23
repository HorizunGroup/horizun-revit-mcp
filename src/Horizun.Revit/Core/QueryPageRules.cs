namespace Horizun.Revit.Core
{
    /// <summary>Revit-free arithmetic for a bounded query response page.</summary>
    public static class QueryPageRules
    {
        /// <summary>Rows still missing after this response page, clamped for stale positions.</summary>
        public static int Remaining(int matchedTotal, int offset, int returned)
        {
            if (matchedTotal <= 0) return 0;
            long consumed = (long)(offset < 0 ? 0 : offset) + (returned < 0 ? 0 : returned);
            return (int)System.Math.Max(0L, (long)matchedTotal - consumed);
        }
    }
}
