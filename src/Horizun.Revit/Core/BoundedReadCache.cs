using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // DTOs only. No Revit objects, no write results, and no persistence. The epoch
    // prevents a computation begun before invalidation from repopulating the cache.
    public sealed class BoundedReadCache
    {
        sealed class Entry { public string Json; public int Bytes; public DateTime Expires; public long Used; }
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        readonly object gate = new object();
        readonly int maxEntries, maxBytes;
        readonly TimeSpan lifetime;
        readonly Func<DateTime> now;
        long epoch, clock;
        int bytes;
        public BoundedReadCache(int maxEntries = 32, int maxBytes = 8 * 1024 * 1024,
                                TimeSpan? lifetime = null, Func<DateTime> now = null)
        {
            if (maxEntries < 1 || maxBytes < 1) throw new ArgumentOutOfRangeException();
            this.maxEntries = maxEntries; this.maxBytes = maxBytes;
            this.lifetime = lifetime ?? TimeSpan.FromSeconds(5);
            if (this.lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
            this.now = now ?? (() => DateTime.UtcNow);
        }
        public long Epoch { get { lock (gate) return epoch; } }
        public int Count { get { lock (gate) return entries.Count; } }
        public void Invalidate() { lock (gate) { epoch++; entries.Clear(); bytes = 0; } }
        public bool TryGet(string key, long expectedEpoch, out JObject result)
        {
            lock (gate)
            {
                result = null;
                if (expectedEpoch != epoch || !entries.TryGetValue(key, out Entry entry)) return false;
                if (entry.Expires <= now()) { Remove(key); return false; }
                entry.Used = ++clock;
                result = JObject.Parse(entry.Json); // callers cannot mutate the stored answer
                return true;
            }
        }
        public bool Store(string key, long expectedEpoch, JObject result)
        {
            string json = result.ToString(Formatting.None);
            int size = Encoding.UTF8.GetByteCount(json);
            if (size > maxBytes) return false;
            lock (gate)
            {
                if (epoch != expectedEpoch) return false;
                Remove(key);
                foreach (string expired in entries.Where(p => p.Value.Expires <= now()).Select(p => p.Key).ToArray()) Remove(expired);
                while (entries.Count >= maxEntries || bytes + size > maxBytes)
                    Remove(entries.OrderBy(p => p.Value.Used).First().Key);
                entries[key] = new Entry { Json = json, Bytes = size, Expires = now() + lifetime, Used = ++clock };
                bytes += size;
                return true;
            }
        }
        void Remove(string key) { if (entries.TryGetValue(key, out Entry entry)) { bytes -= entry.Bytes; entries.Remove(key); } }
    }
}
