using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class GroupAnnotationSession
    {
        private readonly List<Entry> entries = new List<Entry>();
        private readonly object sync = new object();

        public void Record(string document, string operation, int tagsAtRisk, int tagsRecreated, int dimensionsLost)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            lock (sync)
                entries.Add(new Entry(document, operation, tagsAtRisk, tagsRecreated, dimensionsLost));
        }

        public Report Read(string document)
        {
            lock (sync)
            {
                var selected = entries.Where(x => string.Equals(x.Document, document, StringComparison.Ordinal)).ToList();
                return new Report(selected.Count, selected.Sum(x => x.TagsAtRisk),
                    selected.Sum(x => x.TagsRecreated), selected.Sum(x => x.DimensionsLost));
            }
        }

        private sealed class Entry
        {
            public string Document { get; }
            public string Operation { get; }
            public int TagsAtRisk { get; }
            public int TagsRecreated { get; }
            public int DimensionsLost { get; }
            public Entry(string document, string operation, int tagsAtRisk, int tagsRecreated, int dimensionsLost)
            { Document = document; Operation = operation; TagsAtRisk = tagsAtRisk;
              TagsRecreated = tagsRecreated; DimensionsLost = dimensionsLost; }
        }

        public sealed class Report
        {
            public int Operations { get; }
            public int TagsAtRisk { get; }
            public int TagsRecreated { get; }
            public int DimensionsLost { get; }
            public Report(int operations, int tagsAtRisk, int tagsRecreated, int dimensionsLost)
            { Operations = operations; TagsAtRisk = tagsAtRisk; TagsRecreated = tagsRecreated; DimensionsLost = dimensionsLost; }
        }
    }
}
