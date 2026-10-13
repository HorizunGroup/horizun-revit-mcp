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
using System.Globalization;
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
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
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

            // ONE PIECE (L10): the rows then echo each rule's source_selector. The set's digest
            // stays the WHOLE set's, which is what the bars' provenance records.
            ChunkOutcome chunk = ReinforcementChunking.Resolve(doc, request, setJson, set, partitionRequired: false);
            if (chunk != null && !chunk.Ok) return chunk.Refusal;
            if (chunk != null) set = chunk.PieceSet;

            // A DECLARED CROSSING ORDER (etapa 1c): the bars are expected where the plan displaced them, decided the same way.
            CrossingDecision crossing;
            List<ResolvedRebarRow> expected = ReinforcementResolver.ResolveRebarCrossed(doc, set, narrow, false, out crossing);
            // Every displaced set the model carries, measured against its DECLARED position (crossing_displacements).
            var crossingMeasured = new JArray();
            List<ResolvedCoverRow> coverRows = ReinforcementResolver.ResolveCover(doc, set, narrow);
            Dictionary<long, StructuralProvenance> provenance = StructuralProvenanceStore.Index(doc);
            // Area, path and fabric (acero 3.0, L4b), resolved WITHOUT the already-built guard:
            // what is built is what the audit came to check.
            List<ResolvedSystemRow> systemRows = SystemReinforcementResolver.Resolve(doc, set, narrow, refuseAlreadyBuilt: false);

            var findings = new JArray();
            JArray result_orphans = new JArray();
            var rows = new JArray();
            var observedBars = new List<JObject>();
            // The bar each rule matched, by rule id, for the laps measured after the loop (F2.1).
            var observedByRule = new Dictionary<string, JObject>(StringComparer.Ordinal);
            // Every matched set, as the overlap check reads it (overlapping_bar), measured after the loop.
            var overlapMembers = new List<OverlapMember>();
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
                // Provenance: a rule narrowed to element_ids by RequirementSetChunks names the
                // selector its ids came from.
                if (e.Rule.Host?.SourceSelector != null) row["source_selector"] = e.Rule.Host.SourceSelector.DeepClone();
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
                if (e.Rule != null && e.Rule.Id != null) observedByRule[e.Rule.Id] = observed;
                JObject want = ReinforcementResolver.DescribeRebarRow(e, 0);

                JArray these = RebarAuditRules.CompareBar(want, observed, set.Tolerances);
                foreach (JToken t in these) findings.Add(t);
                overlapMembers.Add(RebarOverlap.FromObserved(want, observed));
                if (e.Crossing != null)
                {
                    JObject cm = RebarCrossing.Measure(e.Crossing, e.DeclaredPointsMm,
                        RebarAuditRules.PointsOf(observed["geometry"]?["centreline_points_as_declared_mm"]), set.Tolerances.LengthMm);
                    cm["rebar_id"] = Rid.Value(bar.Id);
                    crossingMeasured.Add(cm);
                    row["crossing"] = cm;
                    if (cm.Value<bool?>("agrees") == false)
                        findings.Add(RebarAuditRules.Finding(RebarFinding.CrossingDisplacementDiffers, RebarSeverity.Error, e.Rule.Id, Rid.Value(bar.Id),
                            cm["expected_start_mm"] + " / " + cm["expected_end_mm"] + " mm",
                            cm["measured_start_mm"] + " / " + cm["measured_end_mm"] + " mm",
                            set.Tolerances.LengthMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " mm",
                            "the declared crossing_order displaced this set (" + e.Crossing.Describe() + "), and the bar the model carries " +
                            "does not stand where the displacement put it (start / end, measured from the declared position along the displacement).",
                            false, null));
                    else if (cm.Value<bool?>("agrees") == null)
                        findings.Add(RebarAuditRules.Unknown(RebarFinding.CrossingDisplacementDiffers, e.Rule.Id, Rid.Value(bar.Id),
                            e.Crossing.Describe(), (string)cm["why"]));
                }

                // A PLACED HOOP OR SPIRAL (L7) is compared by its placement: the centreline the model
                // draws against the radius, the plane or the height it was placed at, and a spiral's
                // pitch, height and finishing turns as the bar carries them now.
                if (e.Rule.Placement != null)
                    foreach (JToken t in PlacedShapes.AuditFindings(e.Rule.Id, Rid.Value(bar.Id), (JObject)want["placement"],
                                 RebarFacts.CentrelinePointsMm(bar, false),
                                 e.Rule.Placement.IsSpiral ? ReinforcementResolver.SpiralParametersRead(bar, e.Rule.Placement, set.Tolerances.LengthMm) : null,
                                 set.Tolerances))
                        findings.Add(t);

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

            // ------------------------------------------------------------- laps
            //
            // A LAP IS A FACT ABOUT TWO BARS, and every check above looks at one. Each piece of
            // a spliced section layer is held to its own curve, which places the overlap - but
            // only if both pieces are there and on one line, and that is measured here, off the
            // centrelines the model returns (F2.1).
            var laps = new JArray();
            foreach (ResolvedRebarRow e in expected)
            {
                if (e.Rule == null || !e.Rule.LapNextMm.HasValue || e.Rule.LapNextRuleId == null) continue;
                JObject a, b;
                if (!observedByRule.TryGetValue(e.Rule.Id, out a) || !observedByRule.TryGetValue(e.Rule.LapNextRuleId, out b))
                    continue;   // a piece that is not there is already rule_built_nothing
                long aid = a.Value<long?>("id") ?? -1, bid = b.Value<long?>("id") ?? -1;
                // As declared - without the hooks - when the model gives it, as the bar comparison does.
                List<double[]> pa = RebarAuditRules.PointsOf(a["geometry"]?["centreline_points_as_declared_mm"])
                                    ?? RebarAuditRules.PointsOf(a["geometry"]?["centreline_points_mm"]);
                List<double[]> pb = RebarAuditRules.PointsOf(b["geometry"]?["centreline_points_as_declared_mm"])
                                    ?? RebarAuditRules.PointsOf(b["geometry"]?["centreline_points_mm"]);
                JArray these = RebarAuditRules.CheckLap(e.Rule.Id, aid, bid, e.Rule.LapNextMm.Value,
                                                         e.Rule.LapNextAcrossMm ?? 0, pa, pb, set.Tolerances);
                foreach (JToken t in these) findings.Add(t);
                laps.Add(new JObject
                {
                    ["rule_id"] = e.Rule.Id,
                    ["next_rule_id"] = e.Rule.LapNextRuleId,
                    ["rebar_id"] = aid,
                    ["next_rebar_id"] = bid,
                    ["declared_mm"] = Math.Round(e.Rule.LapNextMm.Value, 3),
                    ["declared_across_mm"] = Math.Round(e.Rule.LapNextAcrossMm ?? 0, 3),
                    ["measured_across_mm"] = pa == null || pb == null || pa.Count < 2 || pb.Count < 2 ? null : (JToken)Math.Round(RebarAuditRules.LapAcrossMm(pa, pb), 3),
                    ["measured_mm"] = pa == null || pb == null ? null : (JToken)Math.Round(RebarAuditRules.LapOverlapMm(pa, pb), 3),
                    ["ok"] = these.Count == 0
                });
            }

            // ------------------------------------------------- overlapping bars
            //
            // BARS OF DIFFERENT RULES, against each other, on the bars the model carries now: every position
            // Revit says exists, the centreline it drew with the hooks cut off, the model diameter. The plan ran
            // the same check on the declaration; a clash the plan never saw is a bar somebody moved since, or
            // one this audit's set did not plan. Unmeasurable is unknown, never a pass.
            //
            // THE SAME BAR BUILT TWICE (duplicate_bar) is looked for against EVERY bar in the audited hosts, not
            // only the ones a rule matched: a hand-modelled set, another set's, or an area or path system's bar
            // laid on a rule's centreline is that steel counted twice. Those bars are compared for duplicates
            // only - whether they cross the set's bars is overlap_with_unattributed_bar, in not_checked.
            var memberIds = new HashSet<long>(overlapMembers.Select(m => m.RebarId));
            var categories = new Dictionary<long, string>();
            var auditedHosts = new HashSet<long>();
            foreach (ResolvedRebarRow e2 in expected)
            {
                if (!e2.Ok || e2.Host == null || !auditedHosts.Add(Rid.Value(e2.Host.Id))) continue;
                foreach (Rebar other in BarsIn(doc, e2.Host).OrderBy(b => Rid.Value(b.Id)))
                {
                    if (!memberIds.Add(Rid.Value(other.Id))) continue;
                    OverlapMember m = RebarSteelReview.Member(SteelReviewReader.Read(doc, other, categories));
                    m.DuplicateOnly = true;
                    overlapMembers.Add(m);
                }
                foreach (long sb in SteelReviewReader.SystemBarsIn(e2.Host).OrderBy(x => x))
                {
                    var bar = doc.GetElement(Rid.Make(sb)) as RebarInSystem;
                    if (bar == null || !memberIds.Add(sb)) continue;
                    OverlapMember m = RebarSteelReview.Member(SteelReviewReader.Read(doc, bar, categories));
                    m.DuplicateOnly = true;
                    overlapMembers.Add(m);
                }
            }
            OverlapReport overlap = RebarOverlap.Check(overlapMembers, set.Clearances, set.Tolerances.LengthMm);
            foreach (JToken t in RebarOverlap.AuditFindings(overlap)) findings.Add(t);

            // ---------------------------------------- area, path and fabric (L4b)
            //
            // MATCHED BY PROVENANCE, like a bar: the system element that records this rule on
            // this host. Every parameter the rule set is read back and compared; the bars Revit
            // laid from them are not (named in not_checked).
            var systemResults = new JArray();
            int systemsMatched = 0;
            for (int si = 0; si < systemRows.Count; si++)
            {
                ResolvedSystemRow sr = systemRows[si];
                var row = new JObject
                {
                    ["index"] = si,
                    ["kind"] = sr.Kind,
                    ["rule_id"] = sr.RuleId,
                    ["host_id"] = sr.Host == null ? -1 : Rid.Value(sr.Host.Id)
                };
                if (!sr.Ok)
                {
                    row["resolved"] = false;
                    row["code"] = sr.Code;
                    findings.Add(RebarAuditRules.Finding(
                        sr.Code == ReinforcementResolver.CodeHostIneligible ? RebarFinding.HostIneligible : RebarFinding.RuleBuiltNothing,
                        RebarSeverity.Error, sr.RuleId, -1, sr.RuleId, null, "exact", sr.Why, false, null));
                    systemResults.Add(row);
                    continue;
                }
                row["resolved"] = true;
                long hostId = Rid.Value(sr.Host.Id);
                List<long> mine = provenance
                    .Where(kv => kv.Value != null && string.Equals(kv.Value.RuleId, sr.RuleId, StringComparison.Ordinal) &&
                                 kv.Value.HostElementId == hostId && IsSystemOfKind(doc, kv.Key, sr.Kind))
                    .Select(kv => kv.Key).OrderBy(x => x).ToList();
                row["matched"] = mine.Count;
                if (mine.Count == 0)
                {
                    findings.Add(RebarAuditRules.Finding(RebarFinding.RuleBuiltNothing, RebarSeverity.Error, sr.RuleId, -1,
                        "one " + sr.Kind + " system", "none carrying this rule", "exact",
                        "no " + sr.Kind + " system on host " + hostId + " records rule '" + sr.RuleId + "' in its provenance.",
                        true, "horizun_apply_reinforcement"));
                    systemResults.Add(row);
                    continue;
                }
                long sid = mine[0];
                systemsMatched++;
                row["system_id"] = sid;
                if (mine.Count > 1)
                {
                    row["also_recording_this_rule"] = new JArray(mine.Skip(1).Cast<object>().ToArray());
                    findings.Add(RebarAuditRules.Finding(RebarFinding.RuleBuiltNothing, RebarSeverity.Info, sr.RuleId, sid,
                        1, mine.Count, "exact",
                        mine.Count + " " + sr.Kind + " systems on this host record rule '" + sr.RuleId + "'. The lowest id " +
                        "was audited and the others are listed; which is intended cannot be decided here.", false, null));
                }

                int before = findings.Count;
                JObject read = SystemReinforcementWriter.Verify(doc, Rid.Make(sid), sr, set.Tolerances);
                row["checks"] = read["checks"];
                row["verified"] = read["verified"];
                foreach (JProperty c in ((read["checks"] as JObject) ?? new JObject()).Properties())
                {
                    var check = c.Value as JObject;
                    if (check == null) continue;
                    JToken v = check["verified"];
                    if (v != null && v.Type == JTokenType.Boolean && (bool)v) continue;
                    bool notApplicable = check["applicable"] != null && check["applicable"].Type == JTokenType.Boolean &&
                                         !(bool)check["applicable"];
                    if (notApplicable) continue;
                    if (v != null && v.Type == JTokenType.Boolean && check["read"] != null && check["read"].Type != JTokenType.Null)
                    {
                        JObject f = RebarAuditRules.Finding(RebarFinding.SystemParameterDiffers, RebarSeverity.Error,
                            sr.RuleId, sid, check["requested"], check["read"], "per the set's tolerances",
                            c.Name + " on this " + sr.Kind + " system does not read back as declared.", true,
                            "horizun_apply_reinforcement");
                        f["parameter"] = c.Name;
                        findings.Add(f);
                    }
                    else
                    {
                        JObject f = RebarAuditRules.Unknown(RebarFinding.SystemParameterDiffers, sr.RuleId, sid,
                            check["requested"], c.Name + " on this " + sr.Kind + " system could not be read" +
                            (check["why"] != null ? ": " + (string)check["why"] : "."));
                        f["parameter"] = c.Name;
                        findings.Add(f);
                    }
                }

                StructuralProvenance prov;
                provenance.TryGetValue(sid, out prov);
                var provJson = new JObject
                {
                    ["written"] = prov != null,
                    ["requirement_set_id"] = prov == null ? null : prov.RequirementSetId,
                    ["requirement_set_sha256"] = prov == null ? null : prov.RequirementSetSha256
                };
                foreach (JToken t in RebarAuditRules.CheckProvenance(provJson, sr.RuleId, sid, set.Id, setSha))
                    findings.Add(t);
                row["findings"] = findings.Count - before;
                systemResults.Add(row);
            }

            // --------------------------------------------------------- couplers
            //
            // A COUPLER IS A FACT ABOUT TWO BARS too (L7): the coupler at the end of one piece must
            // be the one at the start of the next, and of the declared type. Read off both bars, at the
            // end the coupler is declared at (end 0 of a piece declared from the station back).
            var couplers = new JArray();
            foreach (ResolvedRebarRow e in expected)
            {
                if (e.Rule == null || e.Rule.CouplerNextRuleId == null) continue;
                JObject a, b;
                if (!observedByRule.TryGetValue(e.Rule.Id, out a) || !observedByRule.TryGetValue(e.Rule.CouplerNextRuleId, out b))
                    continue;   // a piece that is not there is already rule_built_nothing
                long couplerId = -1;
                JObject endA = (a["terminations"] as JArray)?.OfType<JObject>().FirstOrDefault(x => (x.Value<int?>("end") ?? -1) == e.Rule.CouplerNextEnd);
                if (endA != null) couplerId = endA.Value<long?>("coupler_id") ?? -1;
                string typeRead = null;
                if (couplerId > 0)
                    try
                    {
                        Element c = doc.GetElement(Rid.Make(couplerId));
                        Element t = c == null ? null : doc.GetElement(c.GetTypeId());
                        typeRead = t?.Name;
                    }
                    catch { typeRead = null; }
                JArray these = RebarAuditRules.CheckCoupler(e.Rule.Id, a, b, e.Rule.CouplerNextTypeName, typeRead, e.Rule.CouplerNextEnd);
                foreach (JToken t in these) findings.Add(t);
                couplers.Add(new JObject
                {
                    ["rule_id"] = e.Rule.Id,
                    ["next_rule_id"] = e.Rule.CouplerNextRuleId,
                    ["rebar_id"] = a.Value<long?>("id") ?? -1,
                    ["next_rebar_id"] = b.Value<long?>("id") ?? -1,
                    ["declared_type"] = e.Rule.CouplerNextTypeName,
                    ["coupler_id"] = couplerId,
                    ["type_read"] = typeRead,
                    ["ok"] = these.Count == 0
                });
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
                if (c.Rule.Face == StructuralCoverRule.FaceNormal)
                {
                    // PER FACE (L7): every face pointing that way must carry the declared cover type.
                    row["face"] = CoverFaceRules.Describe(c.Rule.FaceNormalMm);
                    row["faces"] = c.Faces.Count;
                    if (!c.CurrentReadable)
                        findings.Add(RebarAuditRules.Unknown(RebarFinding.CoverDiffers, c.Rule.Id, Rid.Value(c.Host.Id),
                            c.WantedDistanceMm, "the cover of a face pointing that way could not be read."));
                    else if (!c.AlreadyRight)
                        findings.Add(RebarAuditRules.Finding(RebarFinding.CoverDiffers, RebarSeverity.Error,
                            c.Rule.Id, Rid.Value(c.Host.Id),
                            Math.Round(c.WantedDistanceMm ?? 0, 3),
                            c.CurrentDistanceMm.HasValue ? (object)Math.Round(c.CurrentDistanceMm.Value, 3) : "faces carry different covers",
                            set.Tolerances.CoverMm + " mm",
                            "not every face of the host whose outward normal is the declared one carries the cover type the rule names.",
                            true, "horizun_apply_reinforcement"));
                }
                else if (!c.CurrentDistanceMm.HasValue)
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

            // ---------------------------------------------------- hook lengths
            //
            // A DECLARED HOOK LENGTH is a property of the BAR TYPE, once per hook type, so it is
            // compared once per pair rather than once per bar: every bar of that type with that hook
            // carries the model's number, whichever set built it.
            var hookLengthResults = new JArray();
            foreach (ResolvedHookLength h in RebarHookLengths.Resolve(doc, set))
            {
                var row = new JObject
                {
                    ["path"] = h.Declared.Path,
                    ["bar_type"] = h.Declared.BarTypeId,
                    ["hook_type"] = h.Declared.HookTypeId,
                    ["declared_mm"] = Math.Round(h.Declared.LengthMm, 3)
                };
                if (!h.Ok)
                {
                    row["resolved"] = false;
                    row["code"] = h.Code;
                    JObject f = RebarAuditRules.Unknown(RebarFinding.HookLengthDiffers, h.Declared.Path, -1,
                        Math.Round(h.Declared.LengthMm, 3), h.Why + " The hook length could not be compared.");
                    f["bar_type"] = h.Declared.BarTypeId;
                    f["hook_type"] = h.Declared.HookTypeId;
                    findings.Add(f);
                    row["ok"] = false;
                    hookLengthResults.Add(row);
                    continue;
                }
                row["resolved"] = true;
                row["bar_type_element_id"] = h.BarTypeElementId;
                row["hook_type_element_id"] = h.HookTypeElementId;
                row["read"] = h.Before;
                JArray these = HookLengthRules.AuditFindings(h.Declared, h.BarTypeElementId, h.HookTypeElementId,
                                                             h.Before, set.Tolerances.LengthMm);
                foreach (JToken t in these) findings.Add(t);
                row["ok"] = these.Count == 0;
                hookLengthResults.Add(row);
            }

            // ----------------------------------------------------------- orphans
            //
            // REINFORCEMENT THIS SET BUILT AND NO LONGER ASKS FOR. A rule deleted
            // from the requirement set leaves its bars standing in the model, and
            // every check above is driven by the rules that are still there - so the
            // audit was structurally incapable of seeing them. It says so rather than
            // deleting anything.
            // EVERY RULE THE SET MEANS, expanded: a stirrup zone, a mat component and a section
            // layer are rules of this set under their expanded ids, and only the top-level
            // reinforcement rules were known here - so every bar a zone, a mat or a section
            // built was reported as an orphan of its own set (measured live 2026-10-08).
            var ruleIds = RebarAuditRules.KnownRuleIds(set.RebarRules.Select(r => r.Id),
                                                       expected.Where(r => r.Rule != null).Select(r => r.Rule.Id)
                                                           .Concat(set.AreaRules.Select(r => r.Id))
                                                           .Concat(set.PathRules.Select(r => r.Id))
                                                           .Concat(set.FabricRules.Select(r => r.Id)));
            var orphans = new JArray();
            HashSet<long> narrowSet = narrow.Count > 0 ? new HashSet<long>(narrow) : null;
            foreach (var pair in provenance)
            {
                StructuralProvenance p = pair.Value;
                if (p == null) continue;
                if (!string.Equals(p.RequirementSetId, set.Id, StringComparison.Ordinal)) continue;
                // ONE PIECE: a bar in a host of another piece belongs to that piece's audit. Its rule
                // was left out of this piece, or expanded under another host's id - not unclaimed.
                if (chunk != null && !chunk.PieceHosts.Contains(p.HostElementId)) continue;
                // HOST_IDS NARROW THE AUDIT THE SAME WAY (backlog #14): a bar in a host outside the
                // list belongs to another audit. Its zone, mat or section rule expands under that
                // host's id, which this narrowed audit never expanded, so it read as an orphan and
                // the audit could never agree.
                if (narrowSet != null && !narrowSet.Contains(p.HostElementId)) continue;
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

            // ------------------------------------------------------ mark conflicts
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
            // ONE MARK, ONE BAR (L9): identical sets sharing a mark are correct and are listed in
            // marks_shared; a mark over different bars is bar_mark_conflict, judged against this
            // set's own length tolerance.
            //
            // ONLY MARKS A RULE DECLARED. MEASURED on Revit 2026 (acero 3.0, batch 1): every set whose
            // rule declared no mark read schedule mark '1' - a 16M straight bar, a 10M stirrup and a
            // 10M bent bar alike - so Revit's own numbering is not a schedule line anybody wrote, and
            // judging it reported a conflict on every audit. Undeclared marks are named in not_checked.
            var markedRules = new HashSet<string>(expected.Where(x => x.Rule != null && !string.IsNullOrWhiteSpace(x.Rule.Mark))
                                                          .Select(x => x.Rule.Id), StringComparer.Ordinal);
            var declaredMarks = allInScope.Where(b =>
            {
                StructuralProvenance pv;
                return provenance.TryGetValue(b.Value<long?>("id") ?? -1, out pv) && pv != null && markedRules.Contains(pv.RuleId ?? "");
            }).ToList();
            foreach (JToken t in RebarAuditRules.MarkConflicts(declaredMarks, set.Tolerances.LengthMm)) findings.Add(t);
            JArray marksShared = RebarAuditRules.SharedMarks(declaredMarks, set.Tolerances.LengthMm);

            JObject summary = RebarAuditRules.Summarise(findings);
            summary["marks_checked_over"] = declaredMarks.Count;
            summary["marks_shared"] = marksShared;
            summary["marks_shared_means"] =
                "marks carried by several sets that were proved to be the same bar (bar type, shape and one " +
                "bar's length within the set's length tolerance). That is how a schedule line is meant to work " +
                "and it is not a finding; a mark over different bars is bar_mark_conflict.";
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
                    ["system_rules_examined"] = systemRows.Count,
                    ["systems_matched"] = systemsMatched,
                    ["bars_matched"] = claimed.Count,
                    ["bars_in_document"] = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount(),
                    ["host_ids_narrowed_to"] = narrow.Count
                },
                ["chunk"] = chunk == null ? JValue.CreateNull() : (JToken)chunk.Chunking,
                ["reinforcement"] = rows,
                ["orphans"] = result_orphans,
                ["laps"] = laps,
                ["couplers"] = couplers,
                ["overlaps"] = overlap.ToJson(),
                ["crossing_order"] = ReinforcementResolver.DescribeCrossing(crossing, expected),
                ["crossing_displacements"] = crossingMeasured,
                ["crossing_displacements_means"] =
                    "every set the declared crossing_order displaced, as the model carries it: its hook-free ends against the DECLARED " +
                    "(undisplaced) ends, along the displacement. These are the bars an acceptance holds to the displacement rather than " +
                    "to the declared position. A bar that is not where the displacement put it is crossing_displacement_differs.",
                ["orphans_mean"] =
                    "reinforcement carrying THIS requirement set's id under a rule the set no longer contains. " +
                    "Reported, never deleted.",
                ["cover"] = coverResults,
                ["hook_lengths"] = hookLengthResults,
                ["hook_lengths_mean"] =
                    "every hook length the set declares (hook_types[].lengths_mm), read from its BAR TYPE: a TYPE " +
                    "property, compared once per (bar type, hook type) pair. Auto-calculated is a difference even at " +
                    "the same number - it follows the bend diameter, and the declaration is a fixed length.",
                ["systems"] = systemResults,
                ["systems_mean"] =
                    "area, path and fabric systems this set declares, matched by provenance. Every parameter the rule " +
                    "set is read back and compared; where Revit laid the bars or sheets is not (see not_checked).",
                ["findings"] = findings,
                ["summary"] = summary,
                ["writes_nothing"] = true
            };
            return CommandResult.Ok(result);
        }

        /// <summary>Is this element the kind of system a rule of this kind builds?</summary>
        private static bool IsSystemOfKind(Document doc, long id, string kind)
        {
            Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
            if (kind == SystemReinforcementRules.KindArea) return e is AreaReinforcement;
            if (kind == SystemReinforcementRules.KindPath) return e is PathReinforcement;
            if (kind == SystemReinforcementRules.KindFabric) return e is FabricArea;
            return false;
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

            // THE HOST, OR ITS UNION WITH THE ELEMENTS THE RULE RUNS INTO (L1) - the same
            // context the plan and the apply measured against.
            ReinforcementResolver.ContainmentContext ctx =
                ReinforcementResolver.ContextFor(bar.Document, set, e.Host, e.Rule);
            if (ctx.Code != null)
            {
                findings.Add(RebarAuditRules.Unknown(RebarFinding.ContainmentNotEvaluable, e.Rule.Id, Rid.Value(bar.Id),
                    "every bar inside the elements it was declared into", ctx.Code + ": " + ctx.Why));
                return findings;
            }
            List<double[]> corners = ctx.Corners;
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
            string meshWhy = ctx.MeshWhy;
            HostMesh mesh = ctx.Mesh;
            double radiusMm = 0;
            try
            {
                var bt = bar.Document.GetElement(bar.GetTypeId()) as RebarBarType;
                if (bt != null) radiusMm = bt.BarModelDiameter * FtToMm / 2.0;
            }
            catch { }

            SetContainment c = RebarContainment.Check(
                mesh, drawn, e.Rule.Closed, measured, e.Rule.NormalMm, radiusMm,
                ctx.CoverMm, set.Tolerances.LengthMm, RebarContainment.DefaultSampleStepMm,
                mesh == null ? meshWhy : null);

            string code = RebarContainment.FindingCodeFor(c.Word);
            if (code != null)
            {
                string severity = RebarContainment.SeverityFor(c.Word);
                JObject f = severity == RebarSeverity.Unknown
                    ? RebarAuditRules.Unknown(code, e.Rule.Id, Rid.Value(bar.Id),
                        "every bar inside the host solid",
                        c.Why + " Containment was NOT established.")
                    : RebarAuditRules.Finding(code, severity, e.Rule.Id, Rid.Value(bar.Id),
                        "every bar inside the host solid", c.Word,
                        set.Tolerances.LengthMm + " mm",
                        RebarContainment.Describe(c), true, "horizun_apply_reinforcement");
                f["containment"] = c.ToJson();
                f["measured_from"] =
                    "the centreline Revit draws, offset by GetBarPositionTransform, against the triangulated " +
                    "boundary of the host - not against its bounding box";
                if (ctx.IsUnion) f["measured_against"] = new JArray(ctx.PartIds.Cast<object>().ToArray());
                findings.Add(f);
            }

            // THE ANCHORED LENGTH THE RULE DECLARED (L1), on the bar as the model draws it -
            // hooks included, because that is the steel that is there.
            foreach (AnchorageResult a in AnchorageCheck.Evaluate(mesh, ctx.PartIds, e.Rule.Anchorage, drawn, measured,
                         e.Rule.NormalMm, set.Tolerances.LengthMm, true, ReinforcementResolver.AnyHookDeclared(set, e.Rule)))
            {
                if (a.Verdict == AnchorageCheck.Met) continue;
                JObject f = a.Verdict == AnchorageCheck.Short
                    ? RebarAuditRules.Finding(RebarFinding.AnchorageShort, RebarSeverity.Error, e.Rule.Id, Rid.Value(bar.Id),
                        a.RequiredMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm in element " + a.ElementId,
                        a.MeasuredMm.Value.ToString("0.###", CultureInfo.InvariantCulture) + " mm",
                        set.Tolerances.LengthMm + " mm", a.Why, true, "horizun_apply_reinforcement")
                    : RebarAuditRules.Unknown(RebarFinding.AnchorageShort, e.Rule.Id, Rid.Value(bar.Id),
                        a.RequiredMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm in element " + a.ElementId, a.Why);
                f["anchorage"] = a.ToJson();
                f["measured_from"] = "the centreline Revit draws, hooks included, offset by GetBarPositionTransform, " +
                                     "clipped against the solid of element " + a.ElementId;
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
