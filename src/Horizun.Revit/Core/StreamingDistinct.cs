using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    // Preserve first occurrence without buffering the entire input before yielding.
    public static class StreamingDistinct
    {
        public static IEnumerable<T> ByKey<T, TKey>(IEnumerable<T> source, Func<T, TKey> key)
        {
            var seen = new HashSet<TKey>();
            foreach (T item in source)
                if (seen.Add(key(item))) yield return item;
        }
    }
}
