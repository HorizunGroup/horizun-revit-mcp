// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// STRUCTURE, AS MODELLING FACTS ONLY.
//
// This section reports what is modelled. It does not and will not assess
// safety, capacity, adequacy or code compliance - not because those are hard,
// but because nothing in a Revit document supports them. A beam's presence is
// not a statement that it carries its load, and a report that drifts from
// "modelled" to "sound" is worse than no report: somebody will act on it.
//
// So the vocabulary stops at counts, hosts and parameters, and the reply says
// so in its own words rather than in documentation nobody opens.
//
// REBAR WITHOUT A HOST is the one fact here that is nearly always a real
// modelling problem - a bar hosted by nothing schedules, prices and clashes as
// though it were placed. It is still reported as a FACT with its ids, not as a
// verdict, because "nearly always" is not always.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class StructuralPopulations
    {
        public const string Columns = "structural_columns";
        public const string Framing = "structural_framing";
        public const string Foundations = "structural_foundations";
        public const string Walls = "structural_walls";
        public const string Floors = "structural_floors";
        public const string Connections = "structural_connections";
        public const string Rebar = "rebar";

        public static readonly string[] All =
        {
            Columns, Framing, Foundations, Walls, Floors, Connections, Rebar
        };
    }

    public sealed class StructuralPopulationFact
    {
        public string Population;
        public long Total;
        public long Unreadable;
        /// <summary>Structural material, where the element reports one.</summary>
        public Dictionary<string, long> ByMaterial = new Dictionary<string, long>(StringComparer.Ordinal);
        public long MaterialUnreadable;

        public bool CountsAreExact { get { return Unreadable == 0; } }
    }

    public sealed class RebarFact
    {
        public long ElementId;
        public string TypeName;
        /// <summary>Null when the host read threw. False is "no host", which is different.</summary>
        public bool? HasHost;
        public long? HostId;
        public string HostCategory;
        public int? BarsInSet;
        public double? CoverMm;
        public bool CoverReadable = true;
        public bool Readable = true;
    }

    public static class StructureCensusRules
    {
        public const string ScopeMeans =
            "these are MODELLING facts. Nothing here assesses safety, capacity, adequacy or code compliance, " +
            "because nothing in a Revit document supports those judgements: a beam's presence is not a " +
            "statement that it carries its load. Anything beyond a count needs an engineer, not a scan.";

        public const string RebarHostMeans =
            "rebar with no host schedules, prices and clashes as though it were placed, so it is nearly always " +
            "a real modelling problem - but it is reported as a FACT with its ids rather than a verdict, " +
            "because nearly always is not always. A host that could not be READ is a third state and is never " +
            "counted as hostless.";

        public static List<RebarFact> WithoutHost(IEnumerable<RebarFact> rebar)
        {
            return (rebar ?? Enumerable.Empty<RebarFact>())
                .Where(r => r != null && r.HasHost == false)
                .ToList();
        }

        public static long HostUnreadable(IEnumerable<RebarFact> rebar)
        {
            return (rebar ?? Enumerable.Empty<RebarFact>()).Count(r => r != null && r.HasHost == null);
        }

        public static JObject ToJson(StructuralPopulationFact f)
        {
            if (f == null) return null;
            var mats = new JArray();
            foreach (KeyValuePair<string, long> kv in GroupOptionRules.Ranked(f.ByMaterial))
                mats.Add(new JObject { ["material"] = kv.Key, ["elements"] = kv.Value });

            return new JObject
            {
                ["population"] = f.Population,
                ["total"] = f.Total,
                ["unreadable"] = f.Unreadable,
                ["counts_are_exact"] = f.CountsAreExact,
                ["material_unreadable"] = f.MaterialUnreadable,
                ["by_structural_material"] = mats
            };
        }

        public static JObject ToJson(RebarFact r)
        {
            if (r == null) return null;
            return new JObject
            {
                ["rebar_id"] = r.ElementId,
                ["type_name"] = r.TypeName,
                ["has_host"] = r.HasHost,
                ["host_id"] = r.HostId,
                ["host_category"] = r.HostCategory,
                ["bars_in_set"] = r.BarsInSet,
                // Null rather than 0: a cover that could not be read is not no cover.
                ["cover_mm"] = r.CoverReadable ? r.CoverMm : null,
                ["cover_readable"] = r.CoverReadable,
                ["readable"] = r.Readable
            };
        }

        public static JObject Summary(IEnumerable<StructuralPopulationFact> populations,
                                      IEnumerable<RebarFact> rebar)
        {
            List<StructuralPopulationFact> pops =
                (populations ?? Enumerable.Empty<StructuralPopulationFact>()).Where(p => p != null).ToList();
            List<RebarFact> bars = (rebar ?? Enumerable.Empty<RebarFact>()).Where(r => r != null).ToList();

            var byPopulation = new JObject();
            foreach (string name in StructuralPopulations.All)
            {
                StructuralPopulationFact f = pops.FirstOrDefault(p => p.Population == name);
                // Every population appears, so a missing key never has to be read as
                // either zero or "not collected".
                byPopulation[name] = f == null
                    ? new JObject { ["population"] = name, ["status"] = "not_collected" }
                    : ToJson(f);
            }

            return new JObject
            {
                ["populations"] = byPopulation,
                ["rebar_total"] = bars.Count,
                ["rebar_without_host"] = WithoutHost(bars).Count,
                ["rebar_host_unreadable"] = HostUnreadable(bars),
                ["rebar_cover_unreadable"] = bars.Count(r => !r.CoverReadable),
                ["counts_are_exact"] = pops.All(p => p.CountsAreExact) && bars.All(r => r.Readable),
                ["scope_means"] = ScopeMeans,
                ["rebar_host_means"] = RebarHostMeans
            };
        }
    }
}
