// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT REVIT RAISED, GROUPED SO A PERSON CAN READ IT.
//
// Field 2026-10-27: an edit_type_members rehearsal passed every postcondition, then
// the commit rolled back with 1,202 copies of one sentence ("Los elementos deben
// desconectarse.") and not one element id. Nobody could say which connection Revit
// objected to. A failure is reported here per FailureMessage - definition GUID,
// severity, text, FailingElementIds, AdditionalElementIds, what was done with it -
// grouped by (definition, severity, text, action) and counted, with the first
// samples kept whole and every id counted once. The caller attaches what only it
// knows (which group instance and member an id is, which connector pair two failing
// elements share) to the samples.
//
// Revit-free: unit-tested in Horizun.Core.Tests.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class FailureDigest
    {
        public const int SamplesPerGroup = 12;
        public const int MaxGroups = 40;

        private sealed class Bucket
        {
            public string DefinitionId, Severity, Text, Action;
            public int Count;
            public readonly HashSet<long> Failing = new HashSet<long>();
            public readonly HashSet<long> Additional = new HashSet<long>();
            public readonly JArray Samples = new JArray();
        }

        private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>();
        private readonly List<string> _order = new List<string>();
        private int _overflow;

        public int Total { get; private set; }

        private static string Key(string def, string severity, string text, string action)
            => (def ?? "") + "|" + (severity ?? "") + "|" + (text ?? "") + "|" + (action ?? "");

        /// <summary>True when a sample for this group would still be kept (so the caller only describes what is kept).</summary>
        public bool WantsSample(string def, string severity, string text, string action)
        {
            Bucket b;
            if (!_buckets.TryGetValue(Key(def, severity, text, action), out b)) return _buckets.Count < MaxGroups;
            return b.Samples.Count < SamplesPerGroup;
        }

        /// <summary>One FailureMessage. `action`: deleted_warning | resolved | rolled_back | left.</summary>
        public void Add(string def, string severity, string text, string action,
                        IEnumerable<long> failing, IEnumerable<long> additional, JObject sample)
        {
            Total++;
            string key = Key(def, severity, text, action);
            Bucket b;
            if (!_buckets.TryGetValue(key, out b))
            {
                if (_buckets.Count >= MaxGroups) { _overflow++; return; }
                b = new Bucket { DefinitionId = def, Severity = severity, Text = text, Action = action };
                _buckets[key] = b; _order.Add(key);
            }
            b.Count++;
            var f = (failing ?? Enumerable.Empty<long>()).ToList();
            var a = (additional ?? Enumerable.Empty<long>()).ToList();
            foreach (long id in f) b.Failing.Add(id);
            foreach (long id in a) b.Additional.Add(id);
            if (b.Samples.Count < SamplesPerGroup)
            {
                var row = sample != null ? (JObject)sample.DeepClone() : new JObject();
                row["failing_element_ids"] = new JArray(f);
                if (a.Count > 0) row["additional_element_ids"] = new JArray(a);
                b.Samples.Add(row);
            }
        }

        public int CountOf(string def, string action = null)
            => _buckets.Values.Where(b => b.DefinitionId == def && (action == null || b.Action == action)).Sum(b => b.Count);

        public JObject ToJson()
        {
            var groups = new JArray();
            foreach (string k in _order)
            {
                Bucket b = _buckets[k];
                groups.Add(new JObject
                {
                    ["failure_definition_id"] = b.DefinitionId,
                    ["severity"] = b.Severity,
                    ["text"] = b.Text,
                    ["action"] = b.Action,
                    ["count"] = b.Count,
                    ["distinct_failing_elements"] = b.Failing.Count,
                    ["distinct_additional_elements"] = b.Additional.Count,
                    ["samples"] = b.Samples
                });
            }
            var o = new JObject { ["total"] = Total, ["groups"] = groups };
            if (_overflow > 0) o["not_grouped"] = _overflow;
            return o;
        }
    }
}
