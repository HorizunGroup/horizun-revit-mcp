// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// MEP, AS FACTS AND NOTHING MORE.
//
// This section will not say a system is calculated, balanced, sized, or
// hydraulically continuous. It cannot: connectivity is not calculation. Two
// pipes that touch tell you the model joins them, not that anything flows, not
// that the diameter is right, and not that the pressure drop was ever computed.
// A tool that slides from "connected" to "correct" is the reason engineers stop
// trusting model audits, so the vocabulary here stops at what was observed.
//
// AN OPEN CONNECTOR IS NOT A DEFECT EITHER. A duct ending at a shaft, a pipe
// waiting for next week's equipment, and a genuine mistake all look identical
// from here. The count is reported; the judgement needs a rule somebody wrote.
//
// THREE STATES FOR SYSTEM MEMBERSHIP, not two: in a system, in NO system, and
// unreadable. And an element in MORE than one system is its own fact - it is
// normal for some families and a mistake in others, and merging it into "has a
// system" hides it from the people who would know which.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ConnectorState
    {
        public const string Connected = "connected";
        public const string Open = "open";
        public const string Unreadable = "unreadable";

        public static readonly string[] All = { Connected, Open, Unreadable };
    }

    public static class MepSystemState
    {
        public const string InSystem = "in_system";
        public const string NoSystem = "no_system";
        public const string MultipleSystems = "multiple_systems";
        public const string Unreadable = "unreadable";

        public static readonly string[] All = { InSystem, NoSystem, MultipleSystems, Unreadable };
    }

    public sealed class MepSystemFact
    {
        public long ElementId;
        public string Name;
        public bool NameReadable = true;
        /// <summary>Revit's classification. Null when absent - NOT "undefined".</summary>
        public string Classification;
        public bool ClassificationReadable = true;
        public long ElementCount;
        public string Kind;
    }

    public sealed class MepElementFact
    {
        public long ElementId;
        public string Category;
        /// <summary>How many systems claim this element. Null when the read threw.</summary>
        public int? SystemCount;
        public string SystemName;
        public long ConnectorsTotal;
        public long ConnectorsConnected;
        public long ConnectorsOpen;
        public long ConnectorsUnreadable;
        public bool Readable = true;

        public string State
        {
            get
            {
                if (!Readable || !SystemCount.HasValue) return MepSystemState.Unreadable;
                if (SystemCount.Value == 0) return MepSystemState.NoSystem;
                if (SystemCount.Value > 1) return MepSystemState.MultipleSystems;
                return MepSystemState.InSystem;
            }
        }
    }

    public static class MepCensusRules
    {
        public const string ConnectivityMeans =
            "connectivity is NOT calculation. Nothing here says a system is balanced, sized, calculated or " +
            "hydraulically continuous - two elements that connect tell you the model joins them and nothing " +
            "about whether anything flows, whether the diameter is right, or whether a pressure drop was ever " +
            "computed.";

        public const string OpenConnectorMeans =
            "an open connector is a FACT, not a defect. A duct ending at a shaft, a pipe waiting for equipment " +
            "that arrives next week, and a genuine mistake are indistinguishable from here. The count is " +
            "reported and the judgement needs a rule somebody wrote.";

        public const string ClassificationMeans =
            "a system whose classification is ABSENT is reported with a null classification. 'Undefined' is a " +
            "value Revit itself uses for a system somebody left unclassified, and collapsing the two loses the " +
            "difference between a system nobody classified and one this scan could not read.";

        /// <summary>
        /// Systems that carry no classification at all. Reported apart from those
        /// whose classification could not be READ.
        /// </summary>
        public static List<MepSystemFact> WithoutClassification(IEnumerable<MepSystemFact> systems)
        {
            return (systems ?? Enumerable.Empty<MepSystemFact>())
                .Where(s => s != null && s.ClassificationReadable && string.IsNullOrWhiteSpace(s.Classification))
                .ToList();
        }

        public static JObject Tally(IEnumerable<MepElementFact> elements, IEnumerable<MepSystemFact> systems)
        {
            List<MepElementFact> all =
                (elements ?? Enumerable.Empty<MepElementFact>()).Where(e => e != null).ToList();
            List<MepSystemFact> sys =
                (systems ?? Enumerable.Empty<MepSystemFact>()).Where(s => s != null).ToList();

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string s in MepSystemState.All) counts[s] = 0;
            foreach (MepElementFact e in all) counts[e.State]++;

            long connectors = all.Sum(e => e.ConnectorsTotal);
            long connected = all.Sum(e => e.ConnectorsConnected);
            long open = all.Sum(e => e.ConnectorsOpen);
            long unreadable = all.Sum(e => e.ConnectorsUnreadable);

            var o = new JObject
            {
                ["systems"] = sys.Count,
                ["systems_without_classification"] = WithoutClassification(sys).Count,
                ["systems_classification_unreadable"] = sys.Count(s => !s.ClassificationReadable),
                ["elements_examined"] = all.Count,
                ["connectors_total"] = connectors,
                ["connectors_connected"] = connected,
                ["connectors_open"] = open,
                ["connectors_unreadable"] = unreadable,
                // Published so nobody has to add three numbers and hope.
                ["connectors_balance"] = connected + open + unreadable == connectors,
                ["counts_are_exact"] = counts[MepSystemState.Unreadable] == 0 && unreadable == 0,
                ["connectivity_means"] = ConnectivityMeans,
                ["open_connector_means"] = OpenConnectorMeans,
                ["classification_means"] = ClassificationMeans
            };
            foreach (string s in MepSystemState.All) o[s] = counts[s];
            return o;
        }

        public static JObject ToJson(MepElementFact f)
        {
            if (f == null) return null;
            return new JObject
            {
                ["element_id"] = f.ElementId,
                ["category"] = f.Category,
                ["state"] = f.State,
                ["system_count"] = f.SystemCount,
                ["system_name"] = f.SystemName,
                ["connectors_total"] = f.ConnectorsTotal,
                ["connectors_connected"] = f.ConnectorsConnected,
                ["connectors_open"] = f.ConnectorsOpen,
                ["connectors_unreadable"] = f.ConnectorsUnreadable
            };
        }

        public static JObject ToJson(MepSystemFact f)
        {
            if (f == null) return null;
            return new JObject
            {
                ["system_id"] = f.ElementId,
                ["name"] = f.Name,
                ["kind"] = f.Kind,
                ["classification"] = f.Classification,
                ["classification_readable"] = f.ClassificationReadable,
                ["element_count"] = f.ElementCount
            };
        }
    }
}
