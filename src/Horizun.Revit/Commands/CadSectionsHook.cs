using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>
    /// EACH DUCT RUN'S SECTION, FROM THE DRAWING'S LABELS - one reading, shared by the
    /// first conversion (plan_from_cad) and every later revision (plan_cad_update).
    ///
    /// Two readings would drift: a revision that reads sizes differently from the
    /// conversion reports every duct as resized, or none. The one difference between
    /// the two callers is what an UNSETTLED run becomes. A first conversion withdraws
    /// it - nothing is built at a size nobody chose. An update must NOT withdraw it:
    /// a candidate missing from the reading reads as "deleted from the drawing", and
    /// the update would propose deleting a duct whose line is still drawn. There the
    /// run stays, without a size, not eligible for automatic apply, with its reason.
    /// </summary>
    internal static class CadSectionsHook
    {
        /// <returns>The report (null when no rule declares a section); failure is set when labels could not be read.</returns>
        public static JObject Apply(Element element, CadInstanceFacts facts, CadRequirementSet set, CadHarvest harvest,
                                    JObject request, CadInterpretation interpretation, JArray withdrawn,
                                    bool keepUnresolved, out string failure)
        {
            failure = null;
            var sectionRules = set.Rules.Where(r => r.Section != null).ToList();
            if (sectionRules.Count == 0) return null;

            var sectionsReport = new JObject();
            var labels = new List<CadLabel>();
            var leaders = new List<CadLeaderLine>();
            var readReport = new JObject();
            bool read = CadBlockSource.ReadLabels(element, facts, set, harvest, request.Value<string>("dwg_path"),
                Math.Max(30, Math.Min(3600, request.Value<int?>("dwg_read_timeout_seconds") ?? 900)),
                sectionRules.SelectMany(r => r.Section.LabelLayers).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                labels, leaders, readReport);
            sectionsReport["read"] = readReport;
            if (!read)
            {
                failure = "sections_unread: " + ((string)readReport["refused"] ?? "unknown") + ". " +
                          CadBlockSource.Explain(readReport) +
                          " A duct rule of this set reads each run's section from labels, and no run is planned at " +
                          "a size nobody chose. Nothing was planned.";
                return sectionsReport;
            }
            var byRule = new JObject();
            int keptUnsized = 0;
            int piecesMade = 0;
            foreach (CadRule rule in sectionRules)
            {
                var mine = interpretation.Candidates.Where(c => c.RuleId == rule.Id && c.Geometry.Count >= 2).ToList();
                var runs = mine.Select(c => new CadRunSection
                {
                    RunId = c.Id, SemanticId = c.SemanticId, Start = c.Geometry[0], End = c.Geometry[c.Geometry.Count - 1],
                    SourceEntities = new List<string>(c.SourceSurrogates)
                }).ToList();
                bool cs = set.CaseSensitiveLayers;
                var myLabels = labels.Where(l =>
                {
                    string bare = l.Layer.Contains("|") ? l.Layer.Substring(l.Layer.LastIndexOf('|') + 1) : l.Layer;
                    return rule.Section.LabelLayers.Any(p => CadGlob.IsMatch(l.Layer, p, cs) || CadGlob.IsMatch(bare, p, cs));
                }).ToList();
                CadSectionReading sections = CadDuctSections.Assign(runs, myLabels, leaders, rule.Section, set.PointToleranceMm);
                var byId = mine.ToDictionary(c => c.Id, StringComparer.Ordinal);
                var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
                // A RUN CUT INTO PIECES is replaced by its pieces: each one a candidate of its own with the
                // parent's decisions, its own line and identities, and the parent named in its lineage.
                foreach (var parentId in sections.Runs.Where(x => x.ParentRunId != null).Select(x => x.ParentRunId).Distinct().ToList())
                {
                    CadCandidate parent = byId[parentId];
                    int at = interpretation.Candidates.IndexOf(parent);
                    interpretation.Candidates.RemoveAt(at);
                    foreach (CadRunSection piece in sections.Runs.Where(x => x.ParentRunId == parentId))
                    {
                        double z0 = parent.Geometry[0].Z;
                        CadCandidate pc = parent.Piece(new List<CadPoint> { new CadPoint(piece.Start.X, piece.Start.Y, z0),
                                                                            new CadPoint(piece.End.X, piece.End.Y, z0) },
                                                       piece.PieceKey, set.PointToleranceMm, interpretation.SourceHash);
                        interpretation.Candidates.Insert(at++, pc);
                        renamed[piece.RunId] = pc.Id;
                        byId[pc.Id] = pc;
                        piece.RunId = pc.Id;
                        piece.SemanticId = pc.SemanticId;
                    }
                    piecesMade++;
                }
                // what the reading called a piece before it had an identity, everywhere it is cited
                foreach (CadRunSection r in sections.Runs)
                {
                    string to;
                    if (r.PropagatedFrom != null && renamed.TryGetValue(r.PropagatedFrom, out to)) r.PropagatedFrom = to;
                    foreach (string end in new[] { "a", "b" })
                    {
                        var e = r.TransitionEnds?[end] as JObject;
                        if (e != null && renamed.TryGetValue(e.Value<string>("run") ?? "", out to)) e["run"] = to;
                    }
                }
                foreach (CadRunSection r in sections.Runs)
                {
                    CadCandidate c = byId[r.RunId];
                    if (r.WidthMm.HasValue && (r.State == "documented" || r.State == "propagated"))
                    {
                        c.SectionWidthMm = r.WidthMm; c.SectionHeightMm = r.HeightMm;
                        continue;
                    }
                    if (keepUnresolved)
                    {
                        c.EligibleForAutomaticApply = false;
                        c.IneligibleReasons.Add("section_" + r.State + ": " + r.Reason);
                        keptUnsized++;
                        continue;
                    }
                    interpretation.Candidates.Remove(c);
                    withdrawn.Add(new JObject
                    {
                        ["candidate_id"] = c.Id, ["semantic_id"] = c.SemanticId, ["kind"] = "duct",
                        ["at_mm"] = new JArray(Math.Round(r.Start.X, 1), Math.Round(r.Start.Y, 1),
                                               Math.Round(r.End.X, 1), Math.Round(r.End.Y, 1)),
                        ["reason"] = "section_" + r.State,
                        ["means"] = r.Reason
                    });
                }
                var ruleReport = sections.ToJson();
                ruleReport["section_rule"] = rule.Section.ToJson();
                byRule[rule.Id] = ruleReport;
            }
            sectionsReport["by_rule"] = byRule;
            sectionsReport["runs_cut_into_pieces"] = piecesMade;
            if (keepUnresolved)
            {
                sectionsReport["unsized_runs_kept"] = keptUnsized;
                sectionsReport["unsized_runs_mean"] =
                    "an update keeps a run whose size the labels do not settle IN the reading, without a size and " +
                    "never applied automatically: withdrawing it would read as the drawing deleting a line it " +
                    "still draws. Its held element is compared by position only.";
            }
            return sectionsReport;
        }
    }
}
