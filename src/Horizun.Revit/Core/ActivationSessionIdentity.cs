using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public sealed class ActivationSessionIdentity<T> where T : class
    {
        private readonly List<Tuple<T, string, string>> entries = new List<Tuple<T, string, string>>();
        public string Key(T document, string title)
        {
            foreach (var entry in entries)
            {
                try
                {
                    if (entry.Item1.Equals(document) && String.Equals(entry.Item2, title, StringComparison.Ordinal))
                        return entry.Item3;
                }
                catch { /* A closed document cannot establish identity. */ }
            }
            string key = Guid.NewGuid().ToString();
            entries.Add(Tuple.Create(document, title, key));
            return key;
        }
    }
}
