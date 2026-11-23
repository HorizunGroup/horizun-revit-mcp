// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_query_relations (read-only).
//
// What a Python script was asked every time: "what is this wall joined to, what cuts it,
// what does it host, what stands within 300 mm of it?". The command reads the raw facts
// from Revit; this file decides how they are classified and reported:
//
//  - joined   : geometry joins (and wall end joins) with NO cutting direction;
//  - cut_by   : a joined element that cuts this one in the join, or a void instance cutting it;
//  - cutting  : the mirror - what this element cuts (join order, or the void's targets);
//  - host / hosted / neighbors (box distance within radius_mm) / type_info.
//
// A joined element that cuts appears only under cut_by / cutting, never twice. Each related
// item names the id, the category and the relation type. A relation that could not be read
// is listed apart, never reported as empty.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class RelationKinds
    {
        public const string Joined = "joined";
        public const string CutBy = "cut_by";
        public const string Cutting = "cutting";
        public const string Host = "host";
        public const string Hosted = "hosted";
        public const string Neighbors = "neighbors";
        public const string TypeInfo = "type_info";
        public static readonly string[] All = { Joined, CutBy, Cutting, Host, Hosted, Neighbors, TypeInfo };
    }

    public sealed class RelatedItem
    {
        public long Id;
        public string Category;
        public string RelationType;
        public double? DistanceFeet;
    }

    public sealed class JoinFact
    {
        public long OtherId;
        public bool ThisCutsOther;
        public bool OtherCutsThis;
        public string Kind = "geometry_join";   // geometry_join | end_join
    }

    public sealed class RelationRequest
    {
        public List<long> Ids = new List<long>();
        public List<string> Relations = new List<string>();
        public double RadiusMm = 100;
        public string Unit = "mm";
    }

    public static class RelationRules
    {
        public const int MaxIds = 200;
        public const double MaxRadiusMm = 5000;
        public const int MaxNeighborsPerElement = 50;

        public static bool TryParse(JObject request, out RelationRequest parsed, out string error)
        {
            parsed = null; error = null;
            var r = new RelationRequest();
            var arr = request["ids"] as JArray;
            if (arr == null || arr.Count == 0) { error = "ids is required: 1.." + MaxIds + " integer element ids."; return false; }
            foreach (JToken t in arr)
            {
                if (t.Type != JTokenType.Integer) { error = "ids contains a value that is not an integer element id: " + t; return false; }
                long id = (long)t;
                if (!r.Ids.Contains(id)) r.Ids.Add(id);
            }
            if (r.Ids.Count > MaxIds) { error = "ids carries " + r.Ids.Count + " ids; the limit is " + MaxIds + "."; return false; }
            var rel = request["relations"] as JArray;
            if (rel == null || rel.Count == 0) { error = "relations is required: one or more of " + string.Join(", ", RelationKinds.All) + "."; return false; }
            foreach (JToken t in rel)
            {
                string k = ((string)t ?? "").Trim().ToLowerInvariant();
                if (Array.IndexOf(RelationKinds.All, k) < 0) { error = "relation '" + t + "' is not one of " + string.Join(", ", RelationKinds.All) + "."; return false; }
                if (!r.Relations.Contains(k)) r.Relations.Add(k);
            }
            if (request["radius_mm"] != null && request["radius_mm"].Type != JTokenType.Null)
            {
                double radius = (double)request["radius_mm"];
                if (!(radius >= 0) || radius > MaxRadiusMm) { error = "radius_mm must be between 0 and " + MaxRadiusMm + "."; return false; }
                r.RadiusMm = radius;
            }
            r.Unit = ((string)request["unit"] ?? "mm").Trim().ToLowerInvariant();
            if (!GeometryMeasureRules.TryUnit(r.Unit, out double _)) { error = "unit must be mm, m or feet."; return false; }
            parsed = r;
            return true;
        }

        /// <summary>Split the raw join and void facts into joined / cut_by / cutting.</summary>
        public static Dictionary<string, List<RelatedItem>> Classify(
            IEnumerable<JoinFact> joins, IEnumerable<long> voidCutters, IEnumerable<long> voidTargets, Func<long, string> categoryOf)
        {
            var result = new Dictionary<string, List<RelatedItem>>
            {
                [RelationKinds.Joined] = new List<RelatedItem>(),
                [RelationKinds.CutBy] = new List<RelatedItem>(),
                [RelationKinds.Cutting] = new List<RelatedItem>()
            };
            var seen = new HashSet<string>();
            void Add(string kind, long id, string type)
            {
                if (!seen.Add(kind + ":" + id)) return;
                result[kind].Add(new RelatedItem { Id = id, Category = categoryOf?.Invoke(id), RelationType = type });
            }
            foreach (JoinFact j in joins ?? Enumerable.Empty<JoinFact>())
            {
                if (j.OtherCutsThis) Add(RelationKinds.CutBy, j.OtherId, "join_cut");
                else if (j.ThisCutsOther) Add(RelationKinds.Cutting, j.OtherId, "join_cut");
                else Add(RelationKinds.Joined, j.OtherId, j.Kind);
            }
            foreach (long id in voidCutters ?? Enumerable.Empty<long>()) Add(RelationKinds.CutBy, id, "void_cut");
            foreach (long id in voidTargets ?? Enumerable.Empty<long>()) Add(RelationKinds.Cutting, id, "void_cut");
            foreach (var list in result.Values) list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return result;
        }

        /// <summary>Gap between two axis-aligned boxes, 0 when they touch or overlap. Same unit in and out.</summary>
        public static double BoxDistance(double[] minA, double[] maxA, double[] minB, double[] maxB)
        {
            double sum = 0;
            for (int i = 0; i < 3; i++)
            {
                double gap = Math.Max(0, Math.Max(minB[i] - maxA[i], minA[i] - maxB[i]));
                sum += gap * gap;
            }
            return Math.Sqrt(sum);
        }
    }
}
