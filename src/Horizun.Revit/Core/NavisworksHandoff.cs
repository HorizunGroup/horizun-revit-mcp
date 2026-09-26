// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// PARSING NAVISCOORD'S HANDOFF. naviscoord-mcp's navis_handoff writes two files
// this reads: coordination_handoff.json (schema naviscoord.coordination/1 - every
// issue, both sides, full detail) and revit_worklist.json (schema
// naviscoord.coordination.worklist/1 - issues regrouped by source model, ONLY the
// side expected to move). This file is Revit-free: parsing JSON and normalizing a
// filename are arithmetic, not modelling, and this is exactly the part a test can
// hold without a building.
//
// WHAT THE WORKLIST CANNOT GIVE US. revit_worklist.json keeps only the responsible
// side's items (naviscoord.interop.revit_worklist filters `target["discipline"] !=
// issue.responsible`), so it never carries the FIXED side's element at all. Without
// both sides there is no pair to re-detect - importing it would mean trusting the
// clash existed rather than measuring it, which the product's contract forbids. So
// a worklist import reports every issue as untraceable-by-design, naming the reason,
// rather than fabricating a one-sided finding.
//
// WHAT "IMMOVABLE SIDE" MEANS HERE. naviscoord's Issue carries `responsible` and
// `immovable_side` as DISCIPLINE NAMES (e.g. "Mechanical"), never as "a"/"b" - and
// `side_a_discipline`/`side_b_discipline` say which discipline sits on which
// lettered side FOR THIS ISSUE. Resolving "which side is immovable" therefore reads
// straight off the issue: side A when immovable_side equals side_a_discipline (and
// not also side_b_discipline - a clash between two elements of the SAME discipline
// cannot be told apart by name), side B the mirror, and unknown otherwise. This
// mapping is computed once per issue, at import, and the ledger only ever carries
// the resolved letter (CoordinationFinding.ImmovableSideIsA) - never the discipline
// string alone, which NormalizePair's pair-order swap could not follow.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One element a naviscoord issue names, on one side of the pair.</summary>
    public sealed class NavisTarget
    {
        public string Side;              // "a" | "b"
        public string PathId;
        public string RevitElementId;    // string; empty when the NWC carried none
        public string SourceFile;
        public string Discipline;
        public string Category;
        public string Name;
        public bool ActionableInRevit;

        /// <summary>The Element Id parsed as a long, or null when it is empty/not numeric.</summary>
        public long? ElementIdValue()
        {
            if (string.IsNullOrWhiteSpace(RevitElementId)) return null;
            return long.TryParse(RevitElementId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? (long?)v : null;
        }
    }

    /// <summary>One naviscoord coordination issue, with its (possibly folded) targets.</summary>
    public sealed class NavisIssue
    {
        public string IssueId;
        public string Priority;
        public string Severity;
        public string Level;
        public double[] CentroidMm;          // converted from the handoff's metres
        public double MaxPenetrationMm;
        public string Responsible;
        public string ImmovableSide;         // discipline name, as naviscoord wrote it
        public string SideADiscipline;
        public string SideBDiscipline;
        public string SuggestedAction;
        public string FoldedInto;
        public List<NavisTarget> Targets = new List<NavisTarget>();

        public IEnumerable<NavisTarget> Side(string letter) => Targets.Where(t => t.Side == letter);

        /// <summary>
        /// Which ledger side (A/B) the issue marks immovable, or null when it named none or
        /// the two sides share one discipline and the letter cannot be told apart.
        /// </summary>
        public bool? ImmovableSideIsA()
        {
            if (string.IsNullOrEmpty(ImmovableSide)) return null;
            if (string.Equals(SideADiscipline, SideBDiscipline, StringComparison.Ordinal)) return null;
            if (string.Equals(ImmovableSide, SideADiscipline, StringComparison.Ordinal)) return true;
            if (string.Equals(ImmovableSide, SideBDiscipline, StringComparison.Ordinal)) return false;
            return null;
        }
    }

    public sealed class NavisHandoffParseResult
    {
        public string Schema;
        public List<NavisIssue> Issues = new List<NavisIssue>();
        /// <summary>True for revit_worklist.json: only the responsible side survives, so no pair can be re-detected.</summary>
        public bool OneSidedOnly;
        public string Error;
        public bool Ok => Error == null;
    }

    public static class NavisworksHandoff
    {
        public const string HandoffSchema = "naviscoord.coordination/1";
        public const string WorklistSchema = "naviscoord.coordination.worklist/1";

        // Extensions naviscoord's own exporters and Revit itself produce. Stripped
        // case-insensitively before the NWC-suffix pass below.
        private static readonly string[] KnownExtensions = { ".rvt", ".nwc", ".nwd", ".nwf", ".ifc" };

        private static readonly Regex NwcSuffix = new Regex(
            @"[\s_\-]*\(?(nwc|nwd|export|copy)\)?\s*(\(\d+\))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A source_file name reduced to what identifies the AUTHORING model: no
        /// extension, no trailing "(NWC)"/" - export"/" (1)" decoration, compared
        /// case-insensitively. Applied to BOTH the handoff's source_file and the
        /// candidate Revit document/link title before comparing either.
        /// </summary>
        public static string NormalizeSourceFile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string s = name.Trim();
            int slash = Math.Max(s.LastIndexOfAny(new[] { '\\', '/' }), -1);
            if (slash >= 0) s = s.Substring(slash + 1);
            foreach (string ext in KnownExtensions)
                if (s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { s = s.Substring(0, s.Length - ext.Length); break; }
            // The suffix pass can legitimately fire twice ("model - NWC (2).nwc" strips the
            // extension, then the trailing "(2)", then still carries "- NWC").
            string prev;
            do { prev = s; s = NwcSuffix.Replace(s, "").TrimEnd(); } while (s != prev && s.Length > 0);
            return s.Trim();
        }

        public static bool SourceFileMatches(string handoffName, string candidateTitle) =>
            string.Equals(NormalizeSourceFile(handoffName), NormalizeSourceFile(candidateTitle), StringComparison.OrdinalIgnoreCase)
            && NormalizeSourceFile(handoffName).Length > 0;

        /// <summary>Parse either coordination_handoff.json or revit_worklist.json.</summary>
        public static NavisHandoffParseResult Parse(string json)
        {
            var result = new NavisHandoffParseResult();
            JObject root;
            try { root = JObject.Parse(json); }
            catch (Exception ex) { result.Error = "not valid JSON: " + ex.Message; return result; }

            string schema = root.Value<string>("schema");
            result.Schema = schema;
            if (string.Equals(schema, WorklistSchema, StringComparison.Ordinal))
                return ParseWorklist(root, result);
            if (root["issues"] is JArray) return ParseHandoff(root, result);

            result.Error = "schema '" + (schema ?? "(none)") + "' is neither '" + HandoffSchema + "' nor '" +
                            WorklistSchema + "', and the file has no top-level 'issues' array to fall back on.";
            return result;
        }

        private static NavisHandoffParseResult ParseHandoff(JObject root, NavisHandoffParseResult result)
        {
            foreach (JToken token in (JArray)root["issues"])
            {
                if (!(token is JObject o)) continue;
                // Issues with folded_into are omitted: the representative issue carries the
                // fix, and importing a folded one too would send someone to do the same
                // rework twice.
                if (!string.IsNullOrEmpty(o.Value<string>("folded_into"))) continue;

                var issue = new NavisIssue
                {
                    IssueId = o.Value<string>("issue_id"),
                    Priority = o.Value<string>("priority"),
                    Severity = o.Value<string>("severity"),
                    Level = o.Value<string>("level"),
                    MaxPenetrationMm = o.Value<double?>("max_penetration_mm") ?? 0.0,
                    Responsible = o.Value<string>("responsible"),
                    ImmovableSide = o.Value<string>("immovable_side"),
                    SideADiscipline = o.Value<string>("side_a_discipline"),
                    SideBDiscipline = o.Value<string>("side_b_discipline"),
                    SuggestedAction = o.Value<string>("suggested_action"),
                    FoldedInto = o.Value<string>("folded_into")
                };
                if (o["centroid"] is JArray c && c.Count == 3)
                    // The handoff's centroid is METRES (naviscoord.model: "every length in
                    // this module is in metres"); the ledger's PointMm is millimetres.
                    issue.CentroidMm = new[] { (double)c[0] * 1000.0, (double)c[1] * 1000.0, (double)c[2] * 1000.0 };

                if (o["targets"] is JArray targets)
                    foreach (JToken t in targets)
                        if (t is JObject to)
                            issue.Targets.Add(new NavisTarget
                            {
                                Side = to.Value<string>("side"),
                                PathId = to.Value<string>("path_id"),
                                RevitElementId = to.Value<string>("revit_element_id"),
                                SourceFile = to.Value<string>("source_file"),
                                Discipline = to.Value<string>("discipline"),
                                Category = to.Value<string>("category"),
                                Name = to.Value<string>("name"),
                                ActionableInRevit = to.Value<bool?>("actionable_in_revit") == true
                            });
                if (string.IsNullOrEmpty(issue.IssueId)) continue;
                result.Issues.Add(issue);
            }
            return result;
        }

        private static NavisHandoffParseResult ParseWorklist(JObject root, NavisHandoffParseResult result)
        {
            result.OneSidedOnly = true;
            if (!(root["models"] is JArray models)) return result;
            foreach (JToken modelToken in models)
            {
                if (!(modelToken is JObject model)) continue;
                string sourceFile = model.Value<string>("source_file");
                string discipline = model.Value<string>("discipline");
                if (!(model["items"] is JArray items)) continue;
                foreach (JToken itemToken in items)
                {
                    if (!(itemToken is JObject item)) continue;
                    var issue = new NavisIssue
                    {
                        IssueId = item.Value<string>("issue_id"),
                        Priority = item.Value<string>("priority"),
                        Level = item.Value<string>("level"),
                        Responsible = discipline,
                        SuggestedAction = item.Value<string>("action")
                    };
                    if (string.IsNullOrEmpty(issue.IssueId)) continue;
                    issue.Targets.Add(new NavisTarget
                    {
                        Side = "a", RevitElementId = item.Value<string>("revit_element_id"),
                        SourceFile = sourceFile, Discipline = discipline, Name = item.Value<string>("element"),
                        ActionableInRevit = !string.IsNullOrEmpty(item.Value<string>("revit_element_id"))
                    });
                    result.Issues.Add(issue);
                }
            }
            return result;
        }
    }
}
