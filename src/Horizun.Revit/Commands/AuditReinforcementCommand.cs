// -----------------------------------------------------------------------------
// Horizun Revit MCP - does the model carry the reinforcement that was asked for.
// Original Horizun code. Read-only: no transaction is opened.
//
// The verdict vocabulary is three words and one of them is the point. `agrees`
// means every property this bridge checks was READ and matched. `incomplete`
// means nothing disagreed and something could not be read - which is not a clean
// model, it is a partly audited one, and the difference is the whole reason this
// command is not allowed to say "passed".
//
// Bars are matched to rules by PROVENANCE, not by position. Two identical
// stirrup sets in one beam are indistinguishable by geometry, and matching them
// by "the nearest one" would report a set as correct because its twin was.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class AuditReinforcementCommand : ICommand
    {
        public string Name => "horizun_audit_reinforcement";
        public string Description =>
            "Compare the reinforcement in the model against a structural requirement set, with evidence on every " +
            "finding and unknown never counted as a pass. Read-only.";

        public const double FtToMm = 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            // The host boundaries are cached for the length of ONE command, and a
            // host somebody resized between the plan and the apply must be measured
            // again rather than remembered.
            ReinforcementResolver.ForgetMeshes();

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            JObject setJson = request["requirement_set"] as JObject;
            if (setJson == null)
                return CommandResult.Fail("requirement_set is required and must be an object.");
            StructuralRequirementSet set = StructuralRequirementSet.Load(setJson);
            if (!set.Ok)
                return CommandResult.FailWithDetail(
                    "The requirement set was refused, so nothing was audited: " + set.Error,
                    StructuralRequirementSet.RefusalDetail(set));
            string setSha = StructuralRequirementSet.Sha256Of(setJson);

            var narrow = new List<long>();
            foreach (JToken t in request["host_ids"] as JArray ?? new JArray())
            {
                long v = t.Value<long?>() ?? -1;
                if (!Rid.CanRepresent(v)) return CommandResult.Fail("host_ids carries a value that is not an ElementId.");
                narrow.Add(v);
            }

            List<ResolvedRebarRow> expected = ReinforcementResolver.ResolveRebar(doc, set, narrow);
            List<ResolvedCoverRow> coverRows = ReinforcementResolver.ResolveCover(doc, set, narrow);
            Dictionary<long, StructuralProvenance> provenance = StructuralProvenanceStore.Index(doc);

            var findings = new JArray();
            JArray result_orphans = new JArray();
            var rows = new JArray();
            var observedBars = new List<JObject>();
            var claimed = new HashSet<long>();

            // ------------------------------------------------------ rebar rules
            int i = 0;
            foreach (ResolvedRebarRow e in expected)
            {
                JObject row = new JObject
                {
                    ["index"] = i++,
                    ["rule_id"] = e.Rule.Id,
                    ["host_id"] = e.Host == null ? -1 : Rid.Value(e.Host.Id)
                };
                if (!e.Ok)
                {
                    // A rule that cannot even be RESOLVED against this model is a
                    // finding about the pair, not about a bar.
                    row["resolved"] = false;
                    row["code"] = e.Code;
                    JObject f = RebarAuditRules.Finding(
                        e.Code == ReinforcementResolver.CodeHostIneligible
                            ? RebarFinding.HostIneligible : RebarFinding.RuleBuiltNothing,
                        RebarSeverity.Error, e.Rule.Id, -1, e.Rule.Id, null, "exact",
                        e.Why, false, null);
                    findings.Add(f);
                    rows.Add(row);
                    continue;
                }
                row["resolved"] = true;

                // MATCHED BY PROVENANCE. Two identical stirrup sets in one beam are
                // indistinguishable by geometry, and "the nearest one" would report
                // a set as correct because its twin was.
                List<Rebar> candidates = BarsIn(doc, e.Host)
                    .Where(b =>
                    {
                        StructuralProvenance p;
                        if (!provenance.TryGetValue(Rid.Value(b.Id), out p)) return false;
                        return string.Equals(p.RuleId, e.Rule.Id, StringComparison.Ordinal)
                            && !claimed.Contains(Rid.Value(b.Id));
                    })
                    .OrderBy(b => Rid.Value(b.Id)).ToList();

                if (candidates.Count == 0)
                {
                    // Nothing this rule made. Say whether the host holds OTHER bars,
                    // because "no reinforcement here" and "reinforcement nobody can
                    // attribute" are different situations - and a third one, "Revit
                    // would not tell me", used to be reported as the first.
                    bool hostReadable;
                    int otherBars = BarsIn(doc, e.Host, out hostReadable).Count;
                    row["matched"] = 0;
                    row["other_bars_in_host"] = hostReadable ? (JToken)otherBars : JValue.CreateNull();
                    row["host_readable"] = hostReadable;
                    if (!hostReadable)
                        findings.Add(RebarAuditRules.Unknown(RebarFinding.RuleBuiltNothing, e.Rule.Id, -1,
                            e.Layout.Quantity + " bars",
                            "Revit would not list the reinforcement in host " + Rid.Value(e.Host.Id) +
                            ", so whether this rule built anything there is UNKNOWN."));
                    else
                        findings.Add(RebarAuditRules.Finding(
                            RebarFinding.RuleBuiltNothing, RebarSeverity.Error, e.Rule.Id,
                            -1, e.Layout.Quantity + " bars", "none carrying this rule", "exact",
                            otherBars == 0
                                ? "this host carries no reinforcement at all."
                                : "this host carries " + otherBars + " bar set(s), and none of them records this " +
                                  "rule in its provenance. They may be somebody's hand modelling, or built from " +
                                  "an earlier release - either way nothing here can attribute them.",
                            true, "horizun_apply_reinforcement"));
                    rows.Add(row);
                    continue;
                }

                Rebar bar = candidates[0];
                claimed.Add(Rid.Value(bar.Id));
                row["matched"] = candidates.Count;
                row["rebar_id"] = Rid.Value(bar.Id);
                if (candidates.Count > 1)
                {
                    row["ambiguous"] = true;
                    findings.Add(RebarAuditRules.Finding(
                        RebarFinding.RuleBuiltNothing, RebarSeverity.Info, e.Rule.Id, Rid.Value(bar.Id),
                        1, candidates.Count, "exact",
                        candidates.Count + " bar sets in this host record rule '" + e.Rule.Id + "'. The lowest " +
                        "id was audited and the others are listed; which is intended cannot be decided here.",
                        false, null));
                    row["also_recording_this_rule"] =
                        new JArray(candidates.Skip(1).Select(b => Rid.Value(b.Id)).Cast<object>().ToArray());
                }

                // WITH POSITIONS. The spacing comparison is made against the pitch
                // MEASURED between consecutive bar positions - Revit's MaxSpacing is
                // the number the layout was given, not the pitch it produced - and
                // that pitch only exists in the reply when the positions are read.
                int findingsBefore = findings.Count;
                JObject observed = RebarFacts.Describe(doc, bar, true);
                observedBars.Add(observed);
                JObject want = ReinforcementResolver.DescribeRebarRow(e, 0);

                JArray these = RebarAuditRules.CompareBar(want, observed, set.Tolerances);
                foreach (JToken t in these) findings.Add(t);

                StructuralProvenance prov;
                provenance.TryGetValue(Rid.Value(bar.Id), out prov);
                // THE SET IT NAMES, and WHY there is nothing when there is nothing.
                // CheckProvenance compares the requirement-set id and this block did
                // not carry one, so its cross-set check could never fire.
                string provWhy = null;
                if (prov == null) StructuralProvenanceStore.Read(bar, out provWhy);
                var provJson = new JObject
                {
                    ["written"] = prov != null,
                    ["why_not"] = provWhy,
                    ["requirement_set_id"] = prov == null ? null : prov.RequirementSetId,
                    ["requirement_set_sha256"] = prov == null ? null : prov.RequirementSetSha256
                };
                // A BAR WHOSE PROVENANCE THIS BUILD DECLINED TO READ is not a bar
                // that has none. The audit used to state the second as a fact.
                if (prov == null && provWhy != null && provWhy != StructuralProvenanceStore.ReadAbsent)
                    findings.Add(RebarAuditRules.Unknown(RebarFinding.ProvenanceMissing, e.Rule.Id,
                        Rid.Value(bar.Id), set.Id,
                        provWhy == StructuralProvenanceStore.ReadNewerSchema
                            ? "this bar carries provenance written by a LATER release, whose schema this build " +
                              "refuses to read. It is not unattributed - it is unreadable here."
                            : "this bar carries provenance that could not be read."));
                foreach (JToken t in RebarAuditRules.CheckProvenance(provJson, e.Rule.Id, Rid.Value(bar.Id),
                                                                     set.Id, setSha))
                    findings.Add(t);

                // THE GEOMETRY, re-measured. Every check above can agree while the
                // steel stands outside the concrete.
                JArray positionFindings = PositionsInsideHost(doc, bar, e, set);
                foreach (JToken t in positionFindings) findings.Add(t);

                // EVERY finding this row produced, provenance and geometry included.
                // The count used to omit them, so a row could report 0 findings while
                // the reply carried three about it.
                row["findings"] = findings.Count - findingsBefore;
                rows.Add(row);
            }

            // ------------------------------------------------------ cover rules
            var coverResults = new JArray();
            foreach (ResolvedCoverRow c in coverRows)
            {
                var row = new JObject
                {
                    ["rule_id"] = c.Rule.Id,
                    ["host_id"] = c.Host == null ? -1 : Rid.Value(c.Host.Id)
                };
                if (!c.Ok)
                {
                    row["resolved"] = false;
                    row["code"] = c.Code;
                    findings.Add(RebarAuditRules.Finding(RebarFinding.CoverUnreadable, RebarSeverity.Error,
                        c.Rule.Id, -1, c.Rule.CoverTypeName ?? (object)c.Rule.DistanceMm, null, "exact",
                        c.Why, false, null));
                    coverResults.Add(row);
                    continue;
                }
                row["resolved"] = true;
                row["expected_mm"] = c.WantedDistanceMm;
                row["observed_mm"] = c.CurrentDistanceMm;
                if (!c.CurrentDistanceMm.HasValue)
                    findings.Add(RebarAuditRules.Unknown(RebarFinding.CoverDiffers, c.Rule.Id,
                        Rid.Value(c.Host.Id), c.WantedDistanceMm,
                        "this host has no COMMON cover, which happens when its faces carry different cover " +
                        "types. That is a fact about the host, and it means the rule as written cannot be " +
                        "checked against a single number."));
                else if (!c.AlreadyRight)
                    findings.Add(RebarAuditRules.Finding(RebarFinding.CoverDiffers, RebarSeverity.Error,
                        c.Rule.Id, Rid.Value(c.Host.Id),
                        Math.Round(c.WantedDistanceMm ?? 0, 3), Math.Round(c.CurrentDistanceMm.Value, 3),
                        set.Tolerances.CoverMm + " mm",
                        "the host's common cover is not the cover type the rule names.", true,
                        "horizun_apply_reinforcement"));
                coverResults.Add(row);
            }

            // ----------------------------------------------------------- orphans
            //
            // REINFORCEMENT THIS SET BUILT AND NO LONGER ASKS FOR. A rule deleted
            // from the requirement set leaves its bars standing in the model, and
            // every check above is driven by the rules that are still there - so the
            // audit was structurally incapable of seeing them. It says so rather than
            // deleting anything.
            var ruleIds = new HashSet<string>(set.RebarRules.Select(r => r.Id), StringComparer.Ordinal);
            var orphans = new JArray();
            foreach (var pair in provenance)
            {
                StructuralProvenance p = pair.Value;
                if (p == null) continue;
                if (!string.Equals(p.RequirementSetId, set.Id, StringComparison.Ordinal)) continue;
                if (ruleIds.Contains(p.RuleId)) continue;
                orphans.Add(new JObject
                {
                    ["rebar_id"] = pair.Key,
                    ["rule_id"] = p.RuleId,
                    ["host_element_id"] = p.HostElementId,
                    ["written_utc"] = p.WrittenUtc
                });
                findings.Add(RebarAuditRules.Finding(
                    RebarFinding.RuleBuiltNothing, RebarSeverity.Info, p.RuleId, pair.Key,
                    "a rule in this set", "no rule with this id", "exact",
                    "this reinforcement records requirement set '" + set.Id + "' under rule '" + p.RuleId +
                    "', and the set as given here has no such rule. It was built by an earlier version of this " +
                    "set and is now unclaimed. Nothing is deleted: whether it should go is not this command's " +
                    "decision.", false, null));
            }
            result_orphans = orphans;

            // ------------------------------------------------- duplicate marks
            //
            // OVER EVERY BAR IN EVERY AUDITED HOST, not only the ones a rule matched.
            // A mark collision between a set this bridge built and one somebody
            // modelled by hand is exactly the collision worth finding, and it was
            // invisible.
            var allInScope = new List<JObject>(observedBars);
            var seenIds = new HashSet<long>(observedBars.Select(x => x.Value<long?>("id") ?? -1));
            foreach (ResolvedRebarRow e2 in expected)
            {
                if (e2.Host == null) continue;
                foreach (Rebar other in BarsIn(doc, e2.Host))
                {
                    long oid = Rid.Value(other.Id);
                    if (!seenIds.Add(oid)) continue;
                    allInScope.Add(RebarFacts.Describe(doc, other, false));
                }
            }
            foreach (JToken t in RebarAuditRules.DuplicateMarks(allInScope)) findings.Add(t);

            JObject summary = RebarAuditRules.Summarise(findings);
            summary["marks_checked_over"] = allInScope.Count;
            var result = new JObject
            {
                ["requirement_set"] = new JObject
                {
                    ["id"] = set.Id,
                    ["version"] = set.Version,
                    ["sha256"] = setSha
                },
                ["scope"] = new JObject
                {
                    ["rebar_rules_examined"] = expected.Count,
                    ["cover_rules_examined"] = coverRows.Count,
                    ["bars_matched"] = claimed.Count,
                    ["bars_in_document"] = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount(),
                    ["host_ids_narrowed_to"] = narrow.Count
                },
                ["reinforcement"] = rows,
                ["orphans"] = result_orphans,
                ["orphans_mean"] =
                    "reinforcement carrying THIS requirement set's id under a rule the set no longer contains. " +
                    "Reported, never deleted.",
                ["cover"] = coverResults,
                ["findings"] = findings,
                ["summary"] = summary,
                ["writes_nothing"] = true
            };
            return CommandResult.Ok(result);
        }

        private static List<Rebar> BarsIn(Document doc, Element host)
        {
            bool readable;
            return BarsIn(doc, host, out readable);
        }

        /// <summary>
        /// The bars in a host, and whether Revit actually answered. An empty list
        /// used to mean both "no reinforcement" and "the question threw", and the
        /// audit published the first of those as a measured fact.
        /// </summary>
        private static List<Rebar> BarsIn(Document doc, Element host, out bool readable)
        {
            readable = false;
            if (host == null) return new List<Rebar>();
            RebarHostData data = null;
            try { data = RebarHostData.GetRebarHostData(host); } catch { }
            if (data == null) return new List<Rebar>();
            using (data)
            {
                try { List<Rebar> found = data.GetRebarsInHost().ToList(); readable = true; return found; }
                catch { return new List<Rebar>(); }
            }
        }

        /// <summary>
        /// The bar positions Revit computed, measured against the host now - not the
        /// ones the plan predicted. A set that was correct when it was built and has
        /// since had its host shortened is exactly the case this catches.
        /// </summary>
        private static JArray PositionsInsideHost(Document doc, Rebar bar, ResolvedRebarRow e,
                                                  StructuralRequirementSet set)
        {
            var findings = new JArray();
            RebarShapeDrivenAccessor acc = null;
            try { acc = bar.GetShapeDrivenAccessor(); } catch { }
            int slots = 0;
            try { slots = bar.NumberOfBarPositions; } catch { }
            if (acc == null || slots == 0)
            {
                findings.Add(RebarAuditRules.Unknown(RebarFinding.BarOutsideHost, e.Rule.Id, Rid.Value(bar.Id),
                    "every position inside the host",
                    "the set would not return its bar positions, so whether the steel is inside the host " +
                    "could not be measured."));
                return findings;
            }

            var actual = new List<double[]>();
            for (int i = 0; i < slots; i++)
            {
                Transform t = null;
                try { t = acc.GetBarPositionTransform(i); } catch { }
                if (t == null)
                {
                    findings.Add(RebarAuditRules.Unknown(RebarFinding.BarOutsideHost, e.Rule.Id,
                        Rid.Value(bar.Id), "every position inside the host",
                        "position " + i + " would not return a transform."));
                    return findings;
                }
                actual.Add(new[] { t.Origin.X * FtToMm, t.Origin.Y * FtToMm, t.Origin.Z * FtToMm });
            }

            List<double[]> corners = ReinforcementResolver.HostCorners(e.Host);
            double baseAt = RebarPlanRules.Project(actual[0], e.Rule.NormalMm);
            var measured = actual.Select(a => RebarPlanRules.Project(a, e.Rule.NormalMm) - baseAt).ToList();
            RebarFitVerdict fit = RebarPlanRules.Fit(e.PointsMm, corners, e.Rule.NormalMm,
                                                     measured, set.Tolerances.LengthMm);
            if (!fit.Fits)
            {
                JObject f = RebarAuditRules.Finding(RebarFinding.BarOutsideHost, RebarSeverity.Error,
                    e.Rule.Id, Rid.Value(bar.Id),
                    "every position inside the host",
                    fit.OutsideIndices.Count + " of " + measured.Count + " outside",
                    set.Tolerances.LengthMm + " mm", fit.Why, true, "horizun_apply_reinforcement");
                f["outside_positions"] = new JArray(fit.OutsideIndices.Cast<object>().ToArray());
                f["measured_from"] = "RebarShapeDrivenAccessor.GetBarPositionTransform, read from the model now";
                f["this_is_a_projection"] =
                    "onto the distribution axis, against Revit's axis-aligned bounding box. The containment " +
                    "finding beside it is the one measured against the host's own boundary.";
                findings.Add(f);
            }

            // AGAINST THE HOST'S OWN BOUNDARY, on the centreline the model carries
            // now. This is the same function the plan ran before the write and the
            // apply ran after it: a plan that said a set fits and an audit that says
            // it does not are then disagreeing about the MODEL, not about arithmetic.
            List<double[]> drawn = RebarFacts.CentrelinePointsMm(bar, false) ?? e.PointsMm;
            string meshWhy;
            HostMesh mesh = ReinforcementResolver.MeshFor(e.Host, out meshWhy);
            double radiusMm = 0;
            try
            {
                var bt = bar.Document.GetElement(bar.GetTypeId()) as RebarBarType;
                if (bt != null) radiusMm = bt.BarModelDiameter * FtToMm / 2.0;
            }
            catch { }

            SetContainment c = RebarContainment.Check(
                mesh, drawn, e.Rule.Closed, measured, e.Rule.NormalMm, radiusMm,
                ReinforcementResolver.CoverForContainment(bar.Document, set, e.Host),
                set.Tolerances.LengthMm, RebarContainment.DefaultSampleStepMm);

            string code = RebarContainment.FindingCodeFor(c.Word);
            if (code != null)
            {
                string severity = RebarContainment.SeverityFor(c.Word);
                JObject f = severity == RebarSeverity.Unknown
                    ? RebarAuditRules.Unknown(code, e.Rule.Id, Rid.Value(bar.Id),
                        "every bar inside the host solid",
                        (meshWhy ?? c.Why) + " Containment was NOT established.")
                    : RebarAuditRules.Finding(code, severity, e.Rule.Id, Rid.Value(bar.Id),
                        "every bar inside the host solid", c.Word,
                        set.Tolerances.LengthMm + " mm",
                        RebarContainment.Explain(c) + " " + c.Why, true, "horizun_apply_reinforcement");
                f["containment"] = c.ToJson();
                f["measured_from"] =
                    "the centreline Revit draws, offset by GetBarPositionTransform, against the triangulated " +
                    "boundary of the host - not against its bounding box";
                findings.Add(f);
            }
            return findings;
        }

        /// <summary>The centreline the model carries now, in millimetres, or null.</summary>
        private static List<double[]> ActualCentrelineMm(Rebar bar)
        {
            IList<Curve> curves;
            try
            {
                curves = bar.GetCenterlineCurves(false, false, false,
                                                 MultiplanarOption.IncludeAllMultiplanarCurves, 0);
            }
            catch { return null; }
            if (curves == null || curves.Count == 0) return null;

            var pts = new List<double[]>();
            double[] previous = null;
            foreach (Curve cu in curves)
            {
                IList<XYZ> tess;
                try { tess = cu.Tessellate(); } catch { return null; }
                if (tess == null) return null;
                foreach (XYZ q in tess)
                {
                    var v = new[] { q.X * FtToMm, q.Y * FtToMm, q.Z * FtToMm };
                    if (previous != null && Math.Abs(v[0] - previous[0]) < 1e-6 &&
                        Math.Abs(v[1] - previous[1]) < 1e-6 && Math.Abs(v[2] - previous[2]) < 1e-6) continue;
                    pts.Add(v);
                    previous = v;
                }
            }
            return pts.Count >= 2 ? pts : null;
        }
    }
}
