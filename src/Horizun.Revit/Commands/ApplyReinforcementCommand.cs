// -----------------------------------------------------------------------------
// Horizun Revit MCP - build the reinforcement a requirement set declares.
// Original Horizun code. The single write path for rebar.
//
// WHAT THIS COMMAND HAS TO PROVE, and why the usual proof is not enough.
//
// A created rebar set re-reads beautifully. Host: right. Type: right. Shape:
// right. Element present after commit: yes. And half the bars can be standing
// outside the beam, because Revit does not check that and neither does any
// parameter. So the post-commit verification here does not stop at identity: it
// reads back the ACTUAL bar position transforms Revit computed and measures them
// against the host, and it compares Revit's own bar COUNT against the count the
// layout arithmetic predicted before the transaction opened.
//
// Those two numbers - predicted and measured - come from different places on
// purpose. If the plan were allowed to supply both, the verification would only
// ever confirm its own opinion.
//
// One transaction for the whole set. A half-applied requirement set is a model
// nobody can reason about: some beams reinforced, some not, and no record of
// which. If anything fails, everything rolls back and the reply says so.
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
    public sealed class ApplyReinforcementCommand : ICommand
    {
        public string Name => "horizun_apply_reinforcement";
        public string Description =>
            "Build the cover and reinforcement a structural requirement set declares, in one transaction, and " +
            "re-read every bar's real position from the model afterwards.";

        public const double FtToMm = 304.8;
        public const double MmToFt = 1.0 / 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            // The host boundaries are cached for the length of ONE command, and a
            // host somebody resized between the plan and the apply must be measured
            // again rather than remembered.
            ReinforcementResolver.ForgetMeshes();

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            JObject setJson = request["requirement_set"] as JObject;
            if (setJson == null)
                return CommandResult.Fail("requirement_set is required and must be an object. Schema: " +
                                          StructuralRequirementSet.SchemaName + ".");
            StructuralRequirementSet set = StructuralRequirementSet.Load(setJson);
            if (!set.Ok)
                return CommandResult.FailWithDetail(
                    "The requirement set was refused, so nothing was written: " + set.Error,
                    StructuralRequirementSet.RefusalDetail(set));

            var narrow = new List<long>();
            foreach (JToken t in request["host_ids"] as JArray ?? new JArray())
            {
                long v = t.Value<long?>() ?? -1;
                if (!Rid.CanRepresent(v)) return CommandResult.Fail("host_ids carries a value that is not an ElementId.");
                narrow.Add(v);
            }

            // ONE PIECE (L10). A write must quote the cut it was planned against
            // (chunk.partition_sha256): a cut that moved between two submissions of a loop
            // would build some hosts twice and others never. The provenance keeps the digest of
            // the WHOLE set the caller sent, so auditing the whole set later does not call every
            // piece's bars stale.
            ChunkOutcome chunk = ReinforcementChunking.Resolve(doc, request, setJson, set, partitionRequired: true);
            if (chunk != null && !chunk.Ok) return chunk.Refusal;
            if (chunk != null) set = chunk.PieceSet;

            List<ResolvedCoverRow> covers = ReinforcementResolver.ResolveCover(doc, set, narrow);
            // The plan's own crossing order (etapa 1c): the same sets displaced the same way, so what is built is what was read.
            CrossingDecision crossing;
            List<ResolvedRebarRow> bars = ReinforcementResolver.ResolveRebarCrossed(doc, set, narrow,
                                                                                    refuseAlreadyBuilt: true, decision: out crossing);
            // The plan's own overlap check (overlapping_bar), so the rehearsal a person read is the thing that runs.
            OverlapReport overlap = ReinforcementResolver.RefuseOverlaps(bars, set);
            // Area, path and fabric (acero 3.0, L4b): resolved by the plan's own resolver.
            List<ResolvedSystemRow> systems = SystemReinforcementResolver.Resolve(doc, set, narrow,
                                                                                  refuseAlreadyBuilt: true);
            // DECLARED HOOK LENGTHS: written to the BAR TYPE, so every bar of that type with that hook
            // takes them. Resolved by the plan's own resolver and listed in every reply.
            List<ResolvedHookLength> hookLengths = RebarHookLengths.Resolve(doc, set);

            // NO PYTHON FALLBACK IS EVER OFFERED HERE, and that is a decision
            // rather than an omission. The fallback signal exists for "no typed
            // capability covers what you asked for"; reinforcement is covered
            // ENTIRELY by this command, so every refusal below is bad input or an
            // unresolvable name - never a gap. And a Python retry would build the
            // bars without the post-commit position check that is the whole reason
            // this command exists, which is the opposite of a recovery.
            var refusals = new JArray();
            int idx = 0;
            foreach (ResolvedCoverRow c in covers)
            {
                if (!c.Ok && c.Rule.Required)
                    refusals.Add(new JObject { ["index"] = idx, ["kind"] = "cover", ["rule_id"] = c.Rule.Id, ["code"] = c.Code, ["why"] = c.Why });
                idx++;
            }
            foreach (ResolvedRebarRow b in bars)
            {
                if (!b.Ok && b.Rule.Required)
                    refusals.Add(new JObject { ["index"] = idx, ["kind"] = "rebar", ["rule_id"] = b.Rule.Id, ["code"] = b.Code, ["why"] = b.Why });
                idx++;
            }
            foreach (ResolvedSystemRow sr in systems)
            {
                if (!sr.Ok && sr.Required)
                    refusals.Add(new JObject { ["index"] = idx, ["kind"] = sr.Kind, ["rule_id"] = sr.RuleId, ["code"] = sr.Code, ["why"] = sr.Why });
                idx++;
            }

            foreach (ResolvedHookLength h in hookLengths)
            {
                // Never optional: a declared hook length names a type to change, and one that does
                // not resolve would leave bars built with whatever length the model happens to carry.
                if (!h.Ok)
                    refusals.Add(new JObject { ["index"] = idx, ["kind"] = "bar_type_change", ["rule_id"] = h.Declared.Path, ["code"] = h.Code, ["why"] = h.Why });
                idx++;
            }

            List<ResolvedRebarRow> toBuild = bars.Where(b => b.Ok).ToList();
            List<ResolvedHookLength> toChange = hookLengths.Where(h => RebarHookLengths.NeedsChange(h, set.Tolerances.LengthMm)).ToList();
            List<ResolvedCoverRow> toSet = covers.Where(c => c.Ok && !c.AlreadyRight).ToList();
            List<ResolvedSystemRow> systemsToBuild = systems.Where(x => x.Ok).ToList();

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "requirement_set", "host_ids", "chunk");
            string setSha = StructuralRequirementSet.Sha256Of(setJson);

            ResolvedPlan plan = BuildPlan(doc, gate, set, setSha, toSet, toBuild, systemsToBuild, hookLengths);

            var payload = new JObject
            {
                ["dry_run"] = dryRun,
                ["requirement_set"] = new JObject
                {
                    ["id"] = set.Id,
                    ["version"] = set.Version,
                    ["sha256"] = setSha
                },
                ["planned"] = new JObject
                {
                    ["cover_rows_to_set"] = toSet.Count,
                    ["cover_rows_already_right"] = covers.Count(c => c.Ok && c.AlreadyRight),
                    ["rebar_sets"] = toBuild.Count,
                    ["expected_bars"] = toBuild.Sum(b => b.Layout.Quantity),
                    ["expected_bar_positions"] = toBuild.Sum(b => b.Layout.NumberOfBarPositions),
                    ["area_systems"] = systemsToBuild.Count(x => x.Kind == SystemReinforcementRules.KindArea),
                    ["path_systems"] = systemsToBuild.Count(x => x.Kind == SystemReinforcementRules.KindPath),
                    ["fabric_areas"] = systemsToBuild.Count(x => x.Kind == SystemReinforcementRules.KindFabric),
                    ["bar_type_changes"] = toChange.Count
                },
                ["chunk"] = chunk == null ? JValue.CreateNull() : (JToken)chunk.Chunking,
                ["overlap_check"] = overlap.ToJson(),
                ["crossing_order"] = ReinforcementResolver.DescribeCrossing(crossing, bars),
                ["refused"] = refusals,
                ["refused_means"] =
                    "rows whose rule is REQUIRED and could not be resolved. A rule marked required: false may be " +
                    "refused without stopping the rest; it is reported in the plan and not here."
            };

            // ------------------------------------------------------- rehearsal
            if (dryRun)
            {
                var rows = new JArray();
                for (int i = 0; i < toSet.Count; i++) rows.Add(ReinforcementResolver.DescribeCoverRow(toSet[i], i));
                for (int i = 0; i < toBuild.Count; i++) rows.Add(ReinforcementResolver.DescribeRebarRow(toBuild[i], i));
                for (int i = 0; i < systemsToBuild.Count; i++) rows.Add(SystemReinforcementResolver.Describe(systemsToBuild[i], i));
                payload["will_build"] = rows;
                var typeChanges = new JArray();
                foreach (ResolvedHookLength h in hookLengths) typeChanges.Add(RebarHookLengths.Describe(h, set.Tolerances.LengthMm));
                payload["bar_type_changes"] = typeChanges;
                payload["bar_type_changes_mean"] =
                    "the hook lengths this set declares, per (bar type, hook type) pair. Those with change: true are " +
                    "written to the BAR TYPE in the same transaction, before any bar is built - which changes every " +
                    "bar of that type with that hook in this model, not only the bars this set builds.";
                // AND THE ROWS IT IS DROPPING. A required:false row that failed
                // appeared in neither `refused` nor `will_build`, in the rehearsal or
                // the apply, while `refused_means` asserted such rows were reported.
                // The rehearsal a person reads before approving omitted them.
                var dropped = new JArray();
                for (int i = 0; i < covers.Count; i++)
                    if (!covers[i].Ok) dropped.Add(ReinforcementResolver.DescribeCoverRow(covers[i], i));
                for (int i = 0; i < bars.Count; i++)
                    if (!bars[i].Ok) dropped.Add(ReinforcementResolver.DescribeRebarRow(bars[i], i));
                for (int i = 0; i < systems.Count; i++)
                    if (!systems[i].Ok) dropped.Add(SystemReinforcementResolver.Describe(systems[i], i));
                payload["will_not_build"] = dropped;
                payload["will_not_build_means"] =
                    "every row that did not resolve, required or not. A rule marked required: false is dropped " +
                    "here and does not stop the rest - but it is not silent.";
                payload["nothing_written"] = true;

                if (refusals.Count == 0) DocumentGate.RecordResolvedPlan(plan);
                ApplicationOutcome.StampRehearsal(payload, toSet.Count + toBuild.Count + systemsToBuild.Count + toChange.Count,
                                                  refusals.Count, 0, 0);
                CommandResult rehearsal = CommandResult.Ok(payload);
                DocumentGate.StampConfirmation(payload, gate, Name, planHash, refusals.Count == 0,
                    refusals.Count == 0 ? null
                        : "NO TOKEN WAS ISSUED: " + refusals.Count + " required row(s) could not be resolved, so " +
                          "there is nothing to confirm. Fix them and rehearse again.");
                return rehearsal;
            }

            // --------------------------------------------------------- refusals
            if (refusals.Count > 0)
                return CommandResult.FailWithDetail(
                    refusals.Count + " required row(s) could not be resolved, so nothing was written: " +
                    string.Join("; ", refusals.OfType<JObject>().Take(5).Select(r => (string)r["code"])) +
                    ". Every one of them is a name, a host or a geometry this set declared and this model does " +
                    "not have - fix the set or the model; there is no other tool that could do this instead.",
                    new JObject { ["refused"] = refusals, ["write_started"] = false });

            if (toBuild.Count == 0 && toSet.Count == 0 && systemsToBuild.Count == 0 && toChange.Count == 0)
            {
                int droppedRows = covers.Count(c => !c.Ok) + bars.Count(b => !b.Ok) + systems.Count(x => !x.Ok);
                payload["nothing_to_do"] = true;
                payload["rows_dropped"] = droppedRows;
                if (droppedRows > 0)
                {
                    // NOT A NO-OP. Every row failed, they were all required:false, so
                    // `refusals` was empty and this branch reported NoOp and
                    // fully_applied TRUE - a caller who asked for reinforcement, got
                    // none, and was told the operation completed legitimately. It is
                    // also six zeros for six things nobody measured.
                    var why = new JArray();
                    for (int i = 0; i < covers.Count; i++)
                        if (!covers[i].Ok) why.Add(ReinforcementResolver.DescribeCoverRow(covers[i], i));
                    for (int i = 0; i < bars.Count; i++)
                        if (!bars[i].Ok) why.Add(ReinforcementResolver.DescribeRebarRow(bars[i], i));
                    for (int i = 0; i < systems.Count; i++)
                        if (!systems[i].Ok) why.Add(SystemReinforcementResolver.Describe(systems[i], i));
                    payload["will_not_build"] = why;
                    ApplicationOutcome.StampApplied(payload, ApplicationOutcome.NotStarted,
                                                    droppedRows, 0, 0, droppedRows, 0, 0);
                    return CommandResult.FailWithDetail(
                        "Nothing was built: all " + droppedRows + " row(s) in this requirement set failed to " +
                        "resolve. They are marked required: false, so this is not an error in the set - but it " +
                        "is not a completed operation either, and reporting it as one would tell somebody their " +
                        "reinforcement is in the model.", payload);
                }
                ApplicationOutcome.StampApplied(payload, ApplicationOutcome.NotStarted, 0, 0, 0, 0, 0, 0);
                return CommandResult.Ok(payload);
            }

            CommandResult confirmation = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, plan, null);
            if (confirmation != null) return confirmation;
            CommandResult moved = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (moved != null) return moved;

            // ------------------------------------------------------------ write
            string txName = request.Value<string>("transaction_name") ?? "Horizun reinforcement";
            var made = new List<Built>();
            var madeSystems = new List<KeyValuePair<ResolvedSystemRow, ElementId>>();
            var couplersNotPlaced = new JArray();
            var coversSet = new JArray();
            var provenanceProblems = new JArray();
            var written = new HashSet<ResolvedHookLength>();

            using (var tx = new Transaction(doc, txName))
            {
                try
                {
                    tx.Start();

                    // HOOK LENGTHS FIRST, before any bar exists: a bar built with this hook takes the
                    // length its type carries, so the type is set before the bar is made.
                    foreach (ResolvedHookLength h in toChange)
                    {
                        try { RebarHookLengths.Write(h); }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                "Revit refused hook length " + h.Declared.LengthMm.ToString("0.###", CultureInfo.InvariantCulture) +
                                " mm for hook type '" + h.Declared.HookTypeName + "' on bar type '" + h.Declared.BarTypeName +
                                "' (" + h.Declared.Path + "): " + ex.Message + " Nothing in this batch is kept.", ex);
                        }
                        written.Add(h);
                    }

                    foreach (ResolvedCoverRow c in toSet)
                    {
                        RebarHostData data = RebarHostData.GetRebarHostData(c.Host);
                        if (data == null)
                            throw new InvalidOperationException(
                                "host " + Rid.Value(c.Host.Id) + " would not return its RebarHostData inside the " +
                                "transaction, although it did during the rehearsal.");
                        var entry = new JObject
                        {
                            ["host_id"] = Rid.Value(c.Host.Id),
                            ["cover_type_id"] = Rid.Value(c.CoverType.Id),
                            ["rule_id"] = c.Rule.Id
                        };
                        if (c.Rule.Face == StructuralCoverRule.FaceNormal)
                        {
                            // THE FACES ARE FOUND AGAIN, by the same normal, inside the transaction: the
                            // rehearsal's references are not carried across, and a host whose faces changed
                            // since is refused rather than half covered.
                            List<double[]> seen;
                            string why;
                            List<ReinforcementResolver.FaceCover> faces = ReinforcementResolver.CoverFaces(
                                c.Host, c.Rule.FaceNormalMm, set.Tolerances.AngleDegrees, out seen, out why);
                            if (faces == null || faces.Count != c.Faces.Count)
                                throw new InvalidOperationException(
                                    "cover rule '" + c.Rule.Id + "' found " + c.Faces.Count + " face(s) of host " + Rid.Value(c.Host.Id) +
                                    " pointing that way in the rehearsal and " + (faces == null ? "none readable (" + why + ")" : faces.Count.ToString(CultureInfo.InvariantCulture)) +
                                    " inside the transaction. Nothing in this batch is kept.");
                            using (data)
                                foreach (ReinforcementResolver.FaceCover f in faces) data.SetCoverType(f.Ref, c.CoverType);
                            entry["face"] = CoverFaceRules.Describe(c.Rule.FaceNormalMm);
                            entry["faces"] = faces.Count;
                        }
                        else
                            using (data) data.SetCommonCoverType(c.CoverType);
                        coversSet.Add(entry);
                    }

                    foreach (ResolvedRebarRow b in toBuild)
                    {
                        Rebar bar = Create(doc, b);
                        if (bar == null)
                            throw new InvalidOperationException(
                                "Revit returned no element for rule '" + b.Rule.Id + "' on host " +
                                Rid.Value(b.Host.Id) + ", without raising. Nothing in this batch is kept.");

                        ApplyLayout(bar, b);
                        WriteProvenance(bar, b, set, setSha, plan, provenanceProblems);
                        made.Add(new Built { Row = b, Id = bar.Id });
                    }

                    // AREA, PATH AND FABRIC, after the rebar sets and in the SAME transaction:
                    // one requirement set, one commit or none.
                    foreach (ResolvedSystemRow sr in systemsToBuild)
                    {
                        Element sys = SystemReinforcementWriter.Create(doc, sr);
                        WriteSystemProvenance(sys, sr, set, setSha, plan, provenanceProblems);
                        madeSystems.Add(new KeyValuePair<ResolvedSystemRow, ElementId>(sr, sys.Id));
                    }

                    // COUPLERS (L7): once every piece exists, the coupler of each coupled splice joins the
                    // END of one piece to the START of the next, which meet at the declared station. A
                    // partner piece that was not built (a required: false row refused) leaves its coupler
                    // unplaced and said so; the verification then reports the splice as not coupled.
                    if (made.Any(m => m.Row.Rule.CouplerNextRuleId != null))
                    {
                        doc.Regenerate();
                        foreach (Built m in made.Where(x => x.Row.Rule.CouplerNextRuleId != null))
                        {
                            Built next = Partner(made, m);
                            if (next == null)
                            {
                                couplersNotPlaced.Add(new JObject
                                {
                                    ["rule_id"] = m.Row.Rule.Id,
                                    ["next_rule_id"] = m.Row.Rule.CouplerNextRuleId,
                                    ["why"] = "the next piece was not built in this batch, so there is nothing to couple to."
                                });
                                continue;
                            }
                            RebarCouplerError err;
                            // THE NEXT PIECE FIRST (measured, Revit 2024 and 2026): given (this piece, end 1)
                            // first, Revit REVERSED this piece so the coupler sat at its end 0.
                            RebarCoupler coupler = RebarCoupler.Create(doc, m.Row.CouplerType.Id,
                                RebarReinforcementData.Create(next.Id, 0), RebarReinforcementData.Create(m.Id, m.Row.Rule.CouplerNextEnd), out err);
                            if (coupler == null || err != RebarCouplerError.ValidationSuccessfuly)
                                throw new InvalidOperationException(
                                    "Revit refused the coupler '" + m.Row.Rule.CouplerNextTypeName + "' between rule '" + m.Row.Rule.Id +
                                    "' and rule '" + m.Row.Rule.CouplerNextRuleId + "' on host " + Rid.Value(m.Row.Host.Id) + ": " + err +
                                    ". Nothing in this batch is kept.");
                            m.CouplerId = coupler.Id;
                        }
                    }

                    // HOLD THE ENDS (stage 1b, asked for): once every bar and coupler exists, the open bars
                    // Revit pulled onto a neighbour's cover are re-pinned where the plan put them. Coupled
                    // pieces are seated by their coupler and closed stirrups have no free end: not applicable.
                    if (made.Any(m => set.HoldsEnds(m.Row.Rule)))
                    {
                        doc.Regenerate();
                        foreach (Built m in made.Where(x => set.HoldsEnds(x.Row.Rule)))
                        {
                            bool coupled = m.Row.Rule.CoupledAtStart || m.Row.Rule.CouplerNextRuleId != null;
                            if (m.Row.Rule.Closed || coupled || m.Row.Rule.Placement != null)
                            {
                                m.Hold = new JObject
                                {
                                    ["requested"] = true, ["applicable"] = false, ["verified"] = null,
                                    ["why"] = m.Row.Rule.Closed ? "a closed bar has no free end to hold."
                                            : coupled ? "a coupled end is seated by its coupler, not held."
                                            : "a placed shape is laid by its placement, not by its ends."
                                };
                                continue;
                            }
                            m.Hold = RebarHoldEnds.Hold(doc, (Rebar)doc.GetElement(m.Id), m.Row.PointsMm);
                        }
                    }

                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    bool attempted = false;
                    string rb = PlanFailure.NotAttempted;
                    if (tx.GetStatus() == TransactionStatus.Started)
                    {
                        attempted = true;
                        rb = Guard.RollBack(tx).StatusName;
                    }
                    // AN APPLICATION BLOCK, so a reader gets the measured state
                    // rather than Uncertain - "the absence of evidence" - for a
                    // rollback that was confirmed. And when Guard.Commit raises
                    // SilentRollbackException the transaction is already closed, so
                    // `attempted` is false and the old sentence claimed a clean model
                    // on the strength of not having looked.
                    bool silent = ex is SilentRollbackException;
                    var detail = ApplicationOutcome.FailureAfterWrite(
                        "sets_created_before_the_failure", made.Count + madeSystems.Count,
                        made.Count + madeSystems.Count == 0 ? "first_element"
                            : "after_" + (made.Count + madeSystems.Count) + "_sets",
                        silent ? ApplicationOutcome.RolledBackStatus : rb,
                        silent ? ApplicationState.RolledBack : ApplicationState.Uncertain,
                        objectReread: false);
                    detail["rollback"] = rb;
                    detail["rollback_means"] = silent
                        ? "Revit rolled this transaction back itself before the commit returned, so the batch is " +
                          "not in the model. The status is Revit's, not an assumption."
                        : PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing in this batch was kept");
                    return CommandResult.FailWithDetail(
                        "Reinforcement failed: " + ex.Message + " " + (string)detail["rollback_means"], detail);
                }
            }

            // ------------------------------------------------- re-read and prove
            var verification = new JArray();
            int verified = 0;
            foreach (Built m in made)
            {
                JObject row = Verify(doc, m, set, made);
                if ((bool)row["verified"]) verified++;
                verification.Add(row);
            }

            // The cover is re-read too. SetCommonCoverType does not throw when it
            // does not take, and a cover reported as set and not set is precisely
            // the class of claim this bridge exists not to make.
            int coversVerified = 0;
            foreach (ResolvedCoverRow c in toSet)
            {
                bool ok = false;
                if (c.Rule.Face == StructuralCoverRule.FaceNormal)
                {
                    // EVERY FACE pointing that way, read back one by one.
                    List<double[]> seen;
                    string why;
                    List<ReinforcementResolver.FaceCover> now = ReinforcementResolver.CoverFaces(
                        c.Host, c.Rule.FaceNormalMm, set.Tolerances.AngleDegrees, out seen, out why);
                    ok = now != null && now.Count == c.Faces.Count && now.All(f => f.Current != null && f.Current == c.CoverType.Id);
                }
                else
                {
                    RebarHostData data = null;
                    try { data = RebarHostData.GetRebarHostData(c.Host); } catch { }
                    if (data != null)
                        using (data)
                        {
                            RebarCoverType now = null;
                            try { now = data.GetCommonCoverType(); } catch { }
                            ok = now != null && now.Id == c.CoverType.Id;
                        }
                }
                if (ok) coversVerified++;
                // BY RULE AND HOST: a common rule and a face rule may name the same host.
                foreach (JToken t in coversSet)
                    if ((long)t["host_id"] == Rid.Value(c.Host.Id) && (string)t["rule_id"] == c.Rule.Id) ((JObject)t)["verified"] = ok;
            }

            // HOOK LENGTHS, re-read from the bar type - every declared pair, written or not. A pair
            // that does not read back fixed at the declared length fails the verification.
            var typeVerification = new JArray();
            int typeVerified = 0, typeWrittenVerified = 0, typeWrittenPresent = 0;
            foreach (ResolvedHookLength h in hookLengths.Where(x => x.Ok))
            {
                JObject after = RebarHookLengths.Read(h.BarType, h.HookType.Id);
                JObject v = HookLengthRules.VerifyEntry(h.Declared, h.BarTypeElementId, h.HookTypeElementId,
                                                        h.Before, after, written.Contains(h), set.Tolerances.LengthMm);
                bool ok = (bool)v["verified"];
                if (ok) typeVerified++;
                if (written.Contains(h))
                {
                    if ((bool?)after["readable"] == true) typeWrittenPresent++;
                    if (ok) typeWrittenVerified++;
                }
                typeVerification.Add(v);
            }

            // AREA, PATH AND FABRIC, re-read: every parameter set, and that each produced at
            // least one bar or sheet. Never claimed from the creation call returning.
            var systemVerification = new JArray();
            int systemsVerified = 0, systemsPresent = 0;
            foreach (KeyValuePair<ResolvedSystemRow, ElementId> ms in madeSystems)
            {
                JObject srow = SystemReinforcementWriter.Verify(doc, ms.Value, ms.Key, set.Tolerances);
                if ((bool?)srow["present"] == true) systemsPresent++;
                if ((bool?)srow["verified"] == true) systemsVerified++;
                systemVerification.Add(srow);
            }

            payload["transaction_status"] = ApplicationOutcome.Committed;
            payload["cover_set"] = coversSet;
            payload["cover_verified"] = coversVerified;
            payload["created_verified"] = verified;
            payload["verification"] = verification;
            payload["verification_means"] =
                "each set was re-read from the model: its host, its bar type, its layout rule, Revit's OWN bar " +
                "count against the count the layout arithmetic predicted before the transaction opened, and the " +
                "ACTUAL position transforms Revit computed, measured against the host. The predicted and the " +
                "measured numbers come from different places on purpose - a verification supplied with both " +
                "would only confirm its own opinion.";
            if (madeSystems.Count > 0)
            {
                payload["system_verification"] = systemVerification;
                payload["system_verified"] = systemsVerified;
            }
            if (typeVerification.Count > 0)
            {
                payload["bar_type_changes"] = typeVerification;
                payload["bar_type_changes_verified"] = typeVerified;
                payload["bar_type_changes_mean"] =
                    "each declared hook length re-read from its BAR TYPE after the commit: requested, read and verified " +
                    "(fixed, not auto-calculated, within the length tolerance). A TYPE property - every bar of that type " +
                    "with that hook in this model carries it.";
            }
            if (provenanceProblems.Count > 0) payload["provenance_problems"] = provenanceProblems;
            if (couplersNotPlaced.Count > 0) payload["couplers_not_placed"] = couplersNotPlaced;

            // APPLIED IS WHAT THE MODEL WAS MEASURED TO CARRY, which is what the
            // declaration block means by the word. It used to be made.Count -
            // creation calls that returned - so a batch where nothing survived the
            // commit reported applied: 5, verified: 0 and classified as PARTIAL
            // rather than FAILED. Guard.cs exists to stop exactly that being counted.
            int presentAfterCommit = 0;
            foreach (JToken row in verification)
                if (row["checks"] != null && row["checks"]["host"] != null) presentAfterCommit++;
            int requested = made.Count + toSet.Count + madeSystems.Count + written.Count;
            int applied = presentAfterCommit + coversVerified + systemsPresent + typeWrittenPresent;
            int done = verified + coversVerified + systemsVerified + typeWrittenVerified;
            ApplicationOutcome.StampApplied(payload, ApplicationOutcome.Committed, requested, applied, done, 0,
                                            requested - done, 0);

            if (verified != made.Count || coversVerified != toSet.Count || systemsVerified != madeSystems.Count ||
                typeVerified != typeVerification.Count)
                return CommandResult.FailWithDetail(
                    "The transaction committed and only " + done + " of " + requested +
                    " rows were re-read as what was asked for" +
                    (typeVerified != typeVerification.Count
                        ? ", and " + (typeVerification.Count - typeVerified) + " declared hook length(s) do not read back from their bar type"
                        : "") + ". Success is NOT claimed; inspect the model. The " +
                    "elements exist - their ids are in the verification rows - so the fix is to correct them, " +
                    "never to build them again.",
                    payload);

            return CommandResult.Ok(payload);
        }

        private sealed class Built
        {
            public ResolvedRebarRow Row;
            public ElementId Id;
            /// <summary>The coupler joining this piece's end to the next piece (L7), when one was placed.</summary>
            public ElementId CouplerId;
            /// <summary>What hold_ends did to this bar (stage 1b), when it was asked for.</summary>
            public JObject Hold;
        }

        /// <summary>The piece a coupled splice joins this one to: the next rule, in the same host.</summary>
        private static Built Partner(List<Built> made, Built m) =>
            made.FirstOrDefault(n => n.Row.Rule.Id == m.Row.Rule.CouplerNextRuleId && n.Row.Host.Id == m.Row.Host.Id);

        // --------------------------------------------------------------- create

        private static Rebar Create(Document doc, ResolvedRebarRow b)
        {
            if (b.Rule.Placement != null) return CreatePlaced(doc, b);

            RebarStyle style = b.Rule.Style == StructuralStyle.StirrupTie
                ? RebarStyle.StirrupTie : RebarStyle.Standard;

            if (b.Shape != null)
                return RebarApi.CreateFromCurvesAndShape(doc, b.Shape, b.BarType, b.Host, b.Normal, b.Curves,
                                                         b.StartHookId, b.EndHookId,
                                                         b.Rule.Start.Orientation, b.Rule.End.Orientation);

            // allow_new_shape was declared - the resolver refuses this path otherwise.
            // useExistingShapeIfPossible stays true so an existing shape is reused
            // rather than duplicated; createNewShape is the permission itself.
            return RebarApi.CreateFromCurves(doc, style, b.BarType, b.Host, b.Normal, b.Curves,
                                             b.StartHookId, b.EndHookId,
                                             b.Rule.Start.Orientation, b.Rule.End.Orientation,
                                             useExistingShapeIfPossible: true, createNewShape: true);
        }

        /// <summary>
        /// A HOOP OR A SPIRAL PLACED FROM A SHAPE (L7). Rebar.CreateFromCurves cannot draw either
        /// (measured on Revit 2024: every chain with an arc fails inside Revit), so the shape the rule
        /// names - its definition already read and judged by the resolver - is placed with
        /// CreateFromRebarShape at the corner of the hoop's box, its x and y on the section's two
        /// directions, and fitted with the documented ScaleToBox (the box INCLUDES the bar, so it is
        /// the centreline diameter plus one bar) or, for a spiral, ScaleToBoxFor3D over the declared
        /// height. Revit assigns hooks to a placed shape arbitrarily, so both ends are cleared. A
        /// spiral's pitch, finishing turns and height are then set from the declaration. Every one of
        /// these is re-read after the commit; nothing here is reported as done on its own say-so.
        /// </summary>
        private static Rebar CreatePlaced(Document doc, ResolvedRebarRow b)
        {
            PlacedShapeGeometry g = b.Rule.Placement;
            double d = ReinforcementResolver.SafeDiameterMm(b.BarType);
            if (d <= 0)
                throw new InvalidOperationException(
                    "rule '" + b.Rule.Id + "': bar type '" + SafeName(b.BarType) + "' reports no model diameter, and the box a placed " +
                    "hoop is fitted to includes the bar. Nothing in this batch is kept.");
            double[] origin, xv, yv;
            PlacedShapes.Box(g, d, out origin, out xv, out yv);
            XYZ o = Ft(origin);
            Rebar bar = Rebar.CreateFromRebarShape(doc, b.Shape, b.BarType, b.Host, o,
                                                   new XYZ(g.XVec[0], g.XVec[1], g.XVec[2]), new XYZ(g.YVec[0], g.YVec[1], g.YVec[2]));
            if (bar == null) return null;
            // "If appropriate hooks can be found in the document, they will be assigned arbitrarily."
            bar.SetHookTypeId(0, ElementId.InvalidElementId);
            bar.SetHookTypeId(1, ElementId.InvalidElementId);
            RebarShapeDrivenAccessor acc = bar.GetShapeDrivenAccessor();
            if (acc == null)
                throw new InvalidOperationException("the bar placed from shape '" + SafeName(b.Shape) + "' for rule '" + b.Rule.Id +
                                                    "' is not shape driven, so it cannot be fitted. Nothing in this batch is kept.");
            if (g.IsSpiral)
            {
                acc.ScaleToBoxFor3D(o, Ft(xv), Ft(yv), g.HeightMm.Value * MmToFt);
                SetPlacedParameter(bar, b, BuiltInParameter.REBAR_SHAPE_SPIRAL_PITCH, g.PitchMm.Value * MmToFt, null);
                SetPlacedParameter(bar, b, BuiltInParameter.REBAR_SHAPE_SPIRAL_BASE_FINISHING_TURNS, null, g.BaseFinishingTurns.Value);
                SetPlacedParameter(bar, b, BuiltInParameter.REBAR_SHAPE_SPIRAL_TOP_FINISHING_TURNS, null, g.TopFinishingTurns.Value);
                SetPlacedParameter(bar, b, BuiltInParameter.REBAR_SHAPE_SPIRAL_HEIGHT, g.HeightMm.Value * MmToFt, null);
            }
            else acc.ScaleToBox(o, Ft(xv), Ft(yv));
            return bar;
        }

        private static void SetPlacedParameter(Rebar bar, ResolvedRebarRow b, BuiltInParameter which, double? lengthFt, int? count)
        {
            Parameter p = bar.get_Parameter(which);
            if (p == null || p.IsReadOnly)
                throw new InvalidOperationException(
                    "rule '" + b.Rule.Id + "': the spiral placed from shape '" + SafeName(b.Shape) + "' carries no writable " + which +
                    ", so the declared value cannot be set. Nothing in this batch is kept.");
            bool set = count.HasValue
                ? (p.StorageType == StorageType.Integer ? p.Set(count.Value) : p.Set((double)count.Value))
                : p.Set(lengthFt.Value);
            if (!set)
                throw new InvalidOperationException("rule '" + b.Rule.Id + "': Revit refused " + which + ". Nothing in this batch is kept.");
        }

        private static XYZ Ft(double[] mm) => new XYZ(mm[0] * MmToFt, mm[1] * MmToFt, mm[2] * MmToFt);

        private static void ApplyLayout(Rebar bar, ResolvedRebarRow b)
        {
            // Revit THROWS for a free-form bar rather than returning null, so the
            // crafted message below was unreachable and the raw exception rolled the
            // whole batch back saying nothing useful.
            RebarShapeDrivenAccessor acc = null;
            try { acc = bar.GetShapeDrivenAccessor(); } catch { }
            if (acc == null)
                throw new InvalidOperationException(
                    "the bar created for rule '" + b.Rule.Id + "' is not shape driven, so no layout can be set " +
                    "on it. A free-form bar takes a different path this command does not implement.");

            RebarLayoutPlan p = b.Layout;
            double arrayFt = p.ArrayLengthMm * MmToFt;
            double spacingFt = (p.ResultingSpacingMm ?? 0) * MmToFt;
            bool side = b.Rule.BarsOnNormalSide;

            switch (p.Layout)
            {
                case RebarLayout.Single:
                    acc.SetLayoutAsSingle();
                    break;
                case RebarLayout.FixedNumber:
                    acc.SetLayoutAsFixedNumber(p.NumberOfBarPositions, arrayFt, side,
                                               p.IncludeFirstBar, p.IncludeLastBar);
                    break;
                case RebarLayout.NumberWithSpacing:
                    acc.SetLayoutAsNumberWithSpacing(p.NumberOfBarPositions, spacingFt, side,
                                                     p.IncludeFirstBar, p.IncludeLastBar);
                    break;
                case RebarLayout.MaximumSpacing:
                    // THE DECLARED maximum, not the resulting spacing. Sending the
                    // resulting one would work and would mean something different:
                    // Revit would then treat that smaller number as the new maximum,
                    // and a host that changed size would re-space against it.
                    acc.SetLayoutAsMaximumSpacing(b.Rule.Layout.SpacingMm.Value * MmToFt, arrayFt, side,
                                                  p.IncludeFirstBar, p.IncludeLastBar);
                    break;
                case RebarLayout.MinimumClearSpacing:
                    acc.SetLayoutAsMinimumClearSpacing(b.Rule.Layout.SpacingMm.Value * MmToFt, arrayFt, side,
                                                       p.IncludeFirstBar, p.IncludeLastBar);
                    break;
                default:
                    throw new InvalidOperationException("unhandled layout '" + p.Layout + "'.");
            }

            if (!string.IsNullOrWhiteSpace(b.Rule.Mark))
            {
                try { bar.ScheduleMark = b.Rule.Mark; }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "rule '" + b.Rule.Id + "' declares mark '" + b.Rule.Mark + "' and Revit refused it: " +
                        ex.Message + " Marks are often read-only under a numbering schema.");
                }
            }
        }

        private static void WriteProvenance(Rebar bar, ResolvedRebarRow b, StructuralRequirementSet set,
                                            string setSha, ResolvedPlan plan, JArray problems)
        {
            var p = new StructuralProvenance
            {
                RuleId = b.Rule.Id,
                RequirementSetId = set.Id,
                RequirementSetVersion = set.Version,
                RequirementSetSha256 = setSha,
                PlanFingerprint = plan == null ? null : plan.Fingerprint(),
                HostElementId = Rid.Value(b.Host.Id),
                HostUniqueId = SafeUid(b.Host),
                LayoutRule = b.Layout.Layout,
                ExpectedQuantity = b.Layout.Quantity,
                HorizunVersion = Build.Version,
                HorizunCommit = Build.Commit,
                WrittenUtc = DateTime.UtcNow.ToString("o")
            };
            string why;
            if (!StructuralProvenanceStore.Write(bar, p, out why))
                problems.Add(new JObject
                {
                    ["element_id"] = Rid.Value(bar.Id),
                    ["rule_id"] = b.Rule.Id,
                    // WHAT REVIT SAID. Blaming the element for refusing an entity it
                    // was never offered cost the CAD store a whole release.
                    ["revit_said"] = why
                });
        }

        /// <summary>
        /// The same provenance a bar carries, on the system element itself - the store
        /// accepts any element. The layout field names the kind of system; the expected
        /// quantity is 0 because the bar count of a system is Revit's, not predicted here.
        /// </summary>
        private static void WriteSystemProvenance(Element sys, ResolvedSystemRow r, StructuralRequirementSet set,
                                                  string setSha, ResolvedPlan plan, JArray problems)
        {
            var p = new StructuralProvenance
            {
                RuleId = r.RuleId,
                RequirementSetId = set.Id,
                RequirementSetVersion = set.Version,
                RequirementSetSha256 = setSha,
                PlanFingerprint = plan == null ? null : plan.Fingerprint(),
                HostElementId = Rid.Value(r.Host.Id),
                HostUniqueId = SafeUid(r.Host),
                LayoutRule = r.Kind + "_reinforcement_system",
                ExpectedQuantity = 0,
                HorizunVersion = Build.Version,
                HorizunCommit = Build.Commit,
                WrittenUtc = DateTime.UtcNow.ToString("o")
            };
            string why;
            if (!StructuralProvenanceStore.Write(sys, p, out why))
                problems.Add(new JObject
                {
                    ["element_id"] = Rid.Value(sys.Id),
                    ["rule_id"] = r.RuleId,
                    ["revit_said"] = why
                });
        }

        // --------------------------------------------------------------- verify

        private static JObject Verify(Document doc, Built m, StructuralRequirementSet set, List<Built> made)
        {
            ResolvedRebarRow b = m.Row;
            var row = new JObject
            {
                ["rule_id"] = b.Rule.Id,
                ["element_id"] = Rid.Value(m.Id),
                ["host_id"] = Rid.Value(b.Host.Id)
            };
            var bar = doc.GetElement(m.Id) as Rebar;
            if (bar == null)
            {
                row["verified"] = false;
                row["why"] = "the id the creation returned is not a Rebar in the document after the commit.";
                return row;
            }

            var checks = new JObject();
            bool ok = true;

            ElementId host = null;
            try { host = bar.GetHostId(); } catch { }
            bool hostOk = host != null && host == b.Host.Id;
            checks["host"] = Check(hostOk, Rid.Value(b.Host.Id), host == null ? -1 : Rid.Value(host));
            ok &= hostOk;

            bool typeOk = bar.GetTypeId() == b.BarType.Id;
            checks["bar_type"] = Check(typeOk, Rid.Value(b.BarType.Id), Rid.Value(bar.GetTypeId()));
            ok &= typeOk;

            ElementId shapeId = null;
            try { shapeId = bar.GetShapeId(); } catch { }
            if (b.Shape != null)
            {
                bool shapeOk = shapeId != null && shapeId == b.Shape.Id;
                checks["shape"] = Check(shapeOk, Rid.Value(b.Shape.Id), shapeId == null ? -1 : Rid.Value(shapeId));
                ok &= shapeOk;
            }
            else
            {
                // allow_new_shape WAS DECLARED, so Revit matched an existing shape or
                // CREATED one - and the reply said neither. The resolver argues at
                // length that creating a shape family puts it in the project browser,
                // in schedules and in everybody else's model; the command that does
                // it has to name it.
                Element usedShape = (shapeId != null && shapeId != ElementId.InvalidElementId)
                    ? doc.GetElement(shapeId) : null;
                checks["shape"] = new JObject
                {
                    ["verified"] = shapeId != null && shapeId != ElementId.InvalidElementId,
                    ["declared"] = false,
                    ["revit_used_shape_id"] = shapeId == null ? -1 : Rid.Value(shapeId),
                    ["revit_used_shape_name"] = usedShape == null ? null : SafeName(usedShape),
                    ["why"] = "the rule named no shape and set allow_new_shape, so Revit either matched an " +
                              "existing rebar shape or created one. This is the shape it ended up with."
                };
                if (shapeId == null || shapeId == ElementId.InvalidElementId) ok = false;
            }

            string rule = null;
            try { rule = RebarFacts.LayoutWord(bar.LayoutRule.ToString()); } catch { }
            bool ruleOk = string.Equals(rule, b.Layout.Layout, StringComparison.Ordinal);
            checks["layout_rule"] = Check(ruleOk, b.Layout.Layout, rule);
            ok &= ruleOk;

            // REVIT'S OWN COUNT against the count predicted before the transaction.
            int? positions = null, quantity = null;
            try { positions = bar.NumberOfBarPositions; } catch { }
            try { quantity = bar.Quantity; } catch { }
            bool posOk = positions.HasValue && positions.Value == b.Layout.NumberOfBarPositions;
            bool qtyOk = quantity.HasValue && quantity.Value == b.Layout.Quantity;
            checks["number_of_bar_positions"] = Check(posOk, b.Layout.NumberOfBarPositions, positions);
            checks["quantity"] = Check(qtyOk, b.Layout.Quantity, quantity);
            ok &= posOk && qtyOk;

            // THE FLAGS THAT DECIDE WHICH BARS EXIST. include_first/include_last and
            // bars_on_normal_side were sent to Revit and never read back: a set with
            // the wrong side or a suppressed end bar has the right count whenever
            // the arithmetic happens to agree, and nothing here noticed.
            //
            // EXCEPT ON A SINGLE BAR, where they do not apply and Revit THROWS
            // rather than answering. Measured live, 2026-08-28: a correct
            // single-bar set - right host, right type, right length to the
            // millimetre, position inside the host - was reported as failed
            // because three inapplicable flags came back unreadable. NOT
            // APPLICABLE and UNREADABLE are different answers, and this file has a
            // vocabulary that says so; it just was not using it.
            bool singleBar = string.Equals(b.Layout.Layout, RebarLayout.Single, StringComparison.Ordinal);
            if (singleBar)
            {
                foreach (string flag in new[] { "include_first_bar", "include_last_bar", "bars_on_normal_side" })
                    checks[flag] = NotApplicable(
                        "layout single places one bar, so there is no first, no last and no side for the set to " +
                        "march to. Revit raises rather than answering, and that is not a finding.");
            }
            else
            {
                CompareFlag(checks, "include_first_bar", b.Layout.IncludeFirstBar, Flag(() => bar.IncludeFirstBar), ref ok);
                CompareFlag(checks, "include_last_bar", b.Layout.IncludeLastBar, Flag(() => bar.IncludeLastBar), ref ok);
            }

            // THE TERMINATIONS. Sent to Revit at creation and never read back - and
            // the one check that would have caught a hook that did not take, the
            // total length, is switched off precisely WHEN a hook is declared. So
            // "a hook was asked for and Revit did not apply it" was the one case
            // nothing looked at, while horizun_audit_reinforcement raises
            // hook_differs on the very same bar.
            List<double[]> drawnPts = null, declaredPts = null;
            for (int end = 0; end <= 1; end++)
            {
                ElementId wantHook = end == 0 ? b.StartHookId : b.EndHookId;
                string wantOrient = end == 0 ? b.Rule.Start.Orientation : b.Rule.End.Orientation;
                string endName = end == 0 ? "start" : "end";

                ElementId gotHook = null;
                bool hookReadable = true;
                try { gotHook = bar.GetHookTypeId(end); } catch { hookReadable = false; }
                bool hookOk = hookReadable && gotHook != null && gotHook == (wantHook ?? ElementId.InvalidElementId);
                JObject hookCheck = Check(hookOk, Rid.Value(wantHook ?? ElementId.InvalidElementId),
                                          gotHook == null ? -1 : Rid.Value(gotHook));
                if (!hookReadable) hookCheck["why"] = "Revit would not report the hook type at this end.";
                checks["hook_type_" + endName] = hookCheck;
                ok &= hookOk;

                // A STRAIGHT END HAS NOTHING THAT TURNS (measured live 2026-10-08: hookless 16M
                // ends read 'right' against the default 'left' and failed a correct set).
                long wantHookValue = Rid.Value(wantHook ?? ElementId.InvalidElementId);
                long gotHookValue = gotHook == null ? -1 : Rid.Value(gotHook);
                if (hookReadable && !RebarAuditRules.OrientationApplies(wantHookValue < 0 ? -1 : wantHookValue,
                                                                         gotHookValue < 0 ? -1 : gotHookValue))
                {
                    checks["termination_orientation_" + endName] = NotApplicable(
                        "no hook at this end, so nothing turns; Revit's orientation here is not a fact about the bar.");
                    continue;
                }
                string gotOrient = RebarApi.ReadOrientation(bar, end);
                // THE DRAWING DECIDES WHEN IT CAN BE READ (etapa 1b). MEASURED on Revit 2024: a straight bar
                // matched to M_01, declared 'left', drew its hooks on the left and read back 'right'. The side
                // is measured from the drawn centreline (HookSide); the word decides only when it could not be.
                if (drawnPts == null) drawnPts = RebarFacts.CentrelinePointsMm(bar, false);
                if (declaredPts == null) declaredPts = RebarFacts.CentrelinePointsMm(bar, true);
                string sideWhy;
                string drawnOrient = HookSide.Measure(drawnPts, declaredPts,
                    b.Normal == null ? null : new[] { b.Normal.X, b.Normal.Y, b.Normal.Z }, end, out sideWhy);
                bool orientOk = drawnOrient != null
                    ? string.Equals(drawnOrient, wantOrient, StringComparison.Ordinal)
                    : string.Equals(gotOrient, wantOrient, StringComparison.Ordinal);
                JObject orientCheck = Check(orientOk, wantOrient, gotOrient);
                orientCheck["drawn"] = drawnOrient;
                if (drawnOrient == null)
                    orientCheck["drawn_why"] = sideWhy;
                if (gotOrient == null)
                    orientCheck["why"] = "Revit would not report which way the termination turns at this end.";
                else if (drawnOrient != null && !string.Equals(gotOrient, drawnOrient, StringComparison.Ordinal))
                    orientCheck["why"] = "Revit reports '" + gotOrient + "' and the hook is DRAWN turning '" + drawnOrient +
                                         "' (measured from the drawn centreline); the drawing is what is judged.";
                checks["termination_orientation_" + endName] = orientCheck;
                ok &= orientOk;
            }

            // THE MARK. Written inside the transaction and never re-read; the catch
            // at the setter only covers a mark Revit REFUSES, not one it accepts and
            // does not keep.
            if (!string.IsNullOrWhiteSpace(b.Rule.Mark))
            {
                string gotMark = null;
                bool markReadable = true;
                try { gotMark = bar.ScheduleMark; } catch { markReadable = false; }
                bool markOk = markReadable && string.Equals(gotMark, b.Rule.Mark, StringComparison.Ordinal);
                JObject markCheck = Check(markOk, b.Rule.Mark, gotMark);
                if (!markReadable) markCheck["why"] = "Revit would not report the schedule mark.";

                // AND WHO ELSE NOW CARRIES IT.
                //
                // MEASURED on Revit 2026: a rebar created after another one INHERITS
                // that one's schedule mark rather than taking a default. Three fresh
                // bars all came back as mark "1"; setting the first to AAA left the
                // other two alone; and a fourth bar created afterwards came out AAA.
                // So a set that declares a mark hands it to everything built next
                // that does not declare its own - and a schedule groups by mark, so
                // those become one line and the rest of the steel never appears.
                //
                // The write itself is correct and is reported as such. This is the
                // reach of it, measured rather than assumed, because nothing else in
                // the reply would show it.
                var sharing = new JArray();
                foreach (Rebar other in BarsInHost(b.Host))
                {
                    if (other.Id == bar.Id) continue;
                    string otherMark = null;
                    try { otherMark = other.ScheduleMark; } catch { }
                    if (string.Equals(otherMark, b.Rule.Mark, StringComparison.Ordinal))
                        sharing.Add(Rid.Value(other.Id));
                }
                markCheck["also_carrying_this_mark"] = sharing;
                if (sharing.Count > 0)
                    markCheck["reach"] =
                        sharing.Count + " other bar set(s) in this host already carry mark '" + b.Rule.Mark +
                        "'. A schedule groups by mark, so they will appear as one line. Revit gives a new bar " +
                        "the mark of an existing one unless it is told otherwise. On the same bar that is how a " +
                        "schedule is meant to work; on a different bar horizun_audit_reinforcement reports it as " +
                        "bar_mark_conflict.";
                checks["schedule_mark"] = markCheck;
                ok &= markOk;
            }

            // THE POSITIONS REVIT ACTUALLY COMPUTED, measured against the host. This
            // is the check nothing else performs: everything above can agree while
            // half the steel stands outside the concrete.
            var actual = new List<double[]>();
            bool positionsReadable = true;
            RebarShapeDrivenAccessor acc = null;
            try { acc = bar.GetShapeDrivenAccessor(); } catch { }
            // THE MODEL DIAMETER, read once. Two checks below need it - the array
            // length Revit reports and the positions it distributes - and it is the
            // radius the containment test takes off the centreline.
            double arrayModelDiameterMm = 0;
            try
            {
                RebarBarType appliedType = doc.GetElement(bar.GetTypeId()) as RebarBarType;
                if (appliedType != null) arrayModelDiameterMm = appliedType.BarModelDiameter * FtToMm;
            }
            catch { }
            // THE SPAN REVIT USED, read from the model rather than derived from the
            // declaration. Which of the two the positions are compared across is
            // the whole difference between a check that survives contact with a
            // second Revit and one that does not - see RebarArrayGeometry.
            double? arrayReadMm = null;
            if (acc != null)
            {
                try { arrayReadMm = acc.ArrayLength * FtToMm; } catch { }
            }
            if (acc != null && !singleBar)
                CompareFlag(checks, "bars_on_normal_side", b.Rule.BarsOnNormalSide,
                            Flag(() => acc.BarsOnNormalSide), ref ok);
            // A COUNT THAT COULD NOT BE READ IS NOT A COUNT OF ZERO. When
            // NumberOfBarPositions threw, `positions` was null, the loop below never
            // ran, positionsReadable stayed true, and Fit was handed an EMPTY
            // position list - which it skipped, returning fits:true with
            // measured_positions 0 and outside_positions []. A pass, and a count, out
            // of a measurement that never happened.
            if (acc == null || !positions.HasValue) positionsReadable = false;
            else
                for (int i = 0; i < positions.Value; i++)
                {
                    Transform t = null;
                    try { t = acc.GetBarPositionTransform(i); } catch { }
                    if (t == null) { positionsReadable = false; break; }
                    XYZ o = t.Origin;
                    actual.Add(new[] { o.X * FtToMm, o.Y * FtToMm, o.Z * FtToMm });
                }

            if (!positionsReadable)
            {
                // BOTH CHECKS, UNDER THE NAMES THEY ALWAYS HAVE. This branch used to
                // publish a third name that appears nowhere else in the reply, so on
                // exactly the failure path a reader looking for either real check
                // found it MISSING - and an absent key and a failed one are not the
                // same thing. The reply now carries the same keys whatever happened.
                JObject unknown = new JObject
                {
                    ["verified"] = false,
                    ["why"] = "Revit would not return a position transform, so whether the set sits inside its " +
                              "host is UNKNOWN. Unknown is not a pass."
                };
                checks["positions_within_host_extent"] = unknown;
                checks["inside_host_solid"] = new JObject
                {
                    ["verified"] = false,
                    ["containment"] = SolidContainment.NotEvaluable,
                    ["why"] = "the same: with no position transforms there is nothing to measure against the " +
                              "host's boundary."
                };
                // THE SAME KEYS AS THE MEASURED PATH, for the same reason as above:
                // a reader looking for the cover or the openings check must find it
                // failed, not absent.
                if (b.Rule.CoverPrediction != null)
                    checks["cover_prediction"] = new JObject
                    {
                        ["verified"] = false,
                        ["status"] = StirrupCoverPrediction.Marker,
                        ["why"] = "with no position transforms the first and last bar could not be placed, so the " +
                                  "cover-derived prediction was not compared with anything. Unknown is not a pass."
                    };
                if (b.Rule.OpeningContext != null)
                    checks["clear_of_openings"] = new JObject
                    {
                        ["verified"] = false,
                        ["why"] = "with no position transforms no drawn bar could be measured against the openings."
                    };
                ok = false;
            }
            else
            {
                // THE HOST, OR ITS UNION WITH THE ELEMENTS THE RULE RUNS INTO (L1): the same
                // context the plan measured against before the transaction.
                ReinforcementResolver.ContainmentContext ctx = ReinforcementResolver.ContextFor(doc, set, b.Host, b.Rule);
                List<double[]> corners = ctx.Corners;

                // THE BAR REVIT ACTUALLY DREW, not the one that was asked for. The
                // position transforms are OFFSETS from bar 0 - measured, every set
                // starts at (0,0,0) - so adding them to the DECLARED centreline made
                // the whole check translation-invariant: a bar Revit had put
                // somewhere else entirely would still have been measured where the
                // plan wanted it.
                // A PLACED HOOP OR SPIRAL (L7) is measured on its TESSELLATED centreline: its curves are
                // arcs or a helix, and their end points alone would leave the bar between them unmeasured.
                // EVERY BAR IS MEASURED ON ITS TESSELLATED CENTRELINE, hooks and bends included - the very call the
                // audit makes. MEASURED (Revit 2024, anonymized regression case roof, etapa 1b): reading only each curve's two end points
                // turned a 180 degree hook into its chord, and a hook 2.04 mm short of its cover passed the apply
                // while the audit, measuring the drawn arc, reported cover_violated.
                List<double[]> barPoints = RebarFacts.CentrelinePointsMm(bar, false);
                bool fromModel = barPoints != null && barPoints.Count > 0;
                if (!fromModel) barPoints = b.PointsMm;

                double baseAt = actual.Count > 0
                    ? RebarPlanRules.Project(actual[0], b.Rule.NormalMm) : 0;
                var measured = new List<double>();
                foreach (double[] a in actual)
                    measured.Add(RebarPlanRules.Project(a, b.Rule.NormalMm) - baseAt);

                RebarFitVerdict fit = RebarPlanRules.Fit(barPoints, corners, b.Rule.NormalMm,
                                                          measured, set.Tolerances.LengthMm);
                checks["positions_within_host_extent"] = new JObject
                {
                    ["verified"] = fit.Fits && fromModel,
                    ["code"] = fit.Code,
                    ["measured_positions"] = actual.Count,
                    ["outside_positions"] = new JArray(fit.OutsideIndices.Cast<object>().ToArray()),
                    ["bar_read_from_model"] = fromModel,
                    ["how_measured"] = fit.Why,
                    ["source"] = "the centreline Revit drew, offset by GetBarPositionTransform, both read back " +
                                 "after the commit",
                    ["this_is_a_projection"] =
                        "onto the distribution axis, against Revit's AXIS-ALIGNED bounding box. It answers " +
                        "whether the set is too long for its host and nothing else. inside_host_solid is the " +
                        "check that answers whether the steel is in the concrete."
                };
                if (!fromModel)
                    checks["positions_within_host_extent"]["why"] =
                        "the bar would not return its centreline, so the test fell back to the DECLARED " +
                        "geometry - which cannot detect a bar Revit put somewhere else. Unknown is not a pass.";
                ok &= fit.Fits && fromModel;

                // THE SOLID. Same code the plan ran before the transaction and the
                // audit runs afterwards - on the centreline Revit DREW and the
                // offsets Revit COMPUTED, so a bar the model put somewhere else is
                // measured where it actually is.
                string applyMeshWhy = ctx.Code != null ? ctx.Code + ": " + ctx.Why : ctx.MeshWhy;
                HostMesh applyMesh = ctx.Code != null ? null : ctx.Mesh;
                double applyRadiusMm = arrayModelDiameterMm / 2.0;
                SetContainment inside = RebarContainment.Check(
                    applyMesh, barPoints, b.Rule.Closed, measured, b.Rule.NormalMm, applyRadiusMm,
                    ctx.CoverMm, set.Tolerances.LengthMm, RebarContainment.DefaultSampleStepMm,
                    applyMesh == null ? applyMeshWhy : null);

                JObject insideJson = inside.ToJson();
                insideJson["verified"] = inside.Word == SolidContainment.Inside && fromModel;
                insideJson["bar_read_from_model"] = fromModel;
                insideJson["source"] = "the centreline Revit drew, offset by GetBarPositionTransform, against " +
                                       "the triangulated boundary of the host itself - both read back after " +
                                       "the commit";
                if (applyMesh == null && applyMeshWhy != null && !inside.Why.Contains(applyMeshWhy)) insideJson["boundary_why"] = applyMeshWhy;
                if (!fromModel)
                    insideJson["why_not_verified"] =
                        "the bar would not return its centreline, so this was measured on the DECLARED " +
                        "geometry. Unknown is not a pass.";
                if (ctx.IsUnion) insideJson["measured_against"] = new JArray(ctx.PartIds.Cast<object>().ToArray());
                checks["inside_host_solid"] = insideJson;
                ok &= inside.Word == SolidContainment.Inside && fromModel;

                // THE ANCHORED LENGTH (L1), on the bar Revit drew - hooks included. The plan could
                // only measure the declared centreline; this is where a hook's share is known.
                if (b.Rule.Anchorage.Count > 0)
                {
                    List<AnchorageResult> anch = AnchorageCheck.Evaluate(applyMesh, ctx.PartIds, b.Rule.Anchorage,
                        barPoints, measured, b.Rule.NormalMm, set.Tolerances.LengthMm, fromModel,
                        ReinforcementResolver.AnyHookDeclared(set, b.Rule));
                    bool anchOk = fromModel && anch.All(a => a.Verdict == AnchorageCheck.Met);
                    checks["anchorage"] = new JObject
                    {
                        ["verified"] = anchOk,
                        ["anchorages"] = new JArray(anch.Select(a => (object)a.ToJson()).ToArray()),
                        ["source"] = "the centreline Revit drew, hooks included, offset by GetBarPositionTransform, " +
                                     "clipped against the solid of each element"
                    };
                    ok &= anchOk;
                }

                // THE POSITIONS AGAINST THE ONES THE PLAN PREDICTED. The containment
                // test only asks whether each measured position is between the host
                // bounds; a set with the right count at the wrong pitch passes it.
                //
                // ONTO THE SPAN REVIT ACTUALLY DISTRIBUTES OVER, which is the
                // declared array length minus one MODEL bar diameter. Comparing
                // against the declared span reported every correctly built array as
                // a failure - RebarArrayGeometry carries the four measurements.
                // ONE BAR HAS NO ARRAY, so it needs no span and cannot be rescaled.
                // Requiring one failed `layout single` outright - Revit does not
                // answer ArrayLength for a single-bar set, and gating the position
                // check on that answer turned a correct one-bar set into a failure.
                bool needsSpan = b.SignedPositionsMm.Count > 1;
                bool spanKnown = !needsSpan || arrayReadMm.HasValue;
                string spanWhy = spanKnown ? null : RebarArrayGeometry.WhyRevitWouldNotSay;
                double revitSpanMm = arrayReadMm ?? b.Layout.ArrayLengthMm;
                IList<double> predicted = needsSpan && arrayReadMm.HasValue
                    ? RebarArrayGeometry.Rescale(b.SignedPositionsMm, b.Layout.ArrayLengthMm, revitSpanMm)
                    : b.SignedPositionsMm;
                bool countMatches = predicted.Count == measured.Count;
                double worst = 0;
                if (countMatches)
                    for (int i = 0; i < measured.Count; i++)
                        worst = Math.Max(worst, Math.Abs(measured[i] - predicted[i]));
                bool positionsMatch = spanKnown && countMatches && worst <= set.Tolerances.LengthMm;
                JObject positionsCheck = new JObject
                {
                    ["verified"] = positionsMatch,
                    ["predicted"] = predicted.Count,
                    ["measured"] = measured.Count,
                    ["worst_difference_mm"] = countMatches ? (JToken)Math.Round(worst, 3) : JValue.CreateNull(),
                    ["tolerance_mm"] = set.Tolerances.LengthMm,
                    ["declared_array_length_mm"] = Math.Round(b.Layout.ArrayLengthMm, 3),
                    ["distributed_over_mm"] = arrayReadMm.HasValue
                        ? (JToken)Math.Round(arrayReadMm.Value, 3) : JValue.CreateNull(),
                    ["needed_a_span"] = needsSpan,
                    ["why"] = "each bar position Revit computed, against the offset the layout arithmetic " +
                              "predicted before the transaction opened, moved onto the span Revit distributes " +
                              "over. The containment test alone passes a set with the right count at the wrong " +
                              "pitch."
                };
                if (!needsSpan)
                    positionsCheck["distributed_over_means"] =
                        "one bar has no array, so there is no span to distribute it across and nothing to " +
                        "rescale. The single position is compared where the plan put it.";
                else if (spanKnown)
                    positionsCheck["distributed_over_means"] = RebarArrayGeometry.WhyMeasuredNotPredicted;
                else positionsCheck["why_not_verified"] = spanWhy;
                checks["positions_match_the_plan"] = positionsCheck;
                ok &= positionsMatch;

                // THE COVER-DERIVED PREDICTION, held to the model. A cover-aware
                // zone predicted its first and last station from one measured rule;
                // this is the comparison that turns predicted_from_host_cover into
                // evidence or into a finding. The first bar is anchored (measured:
                // Revit does not move it); the last may be up to one model diameter
                // short, the same bound the array check holds.
                if (b.Rule.CoverPrediction != null)
                    checks["cover_prediction"] = CoverPredictionCheck(b, barPoints, fromModel, measured,
                                                                      arrayReadMm, arrayModelDiameterMm,
                                                                      set.Tolerances.LengthMm, ref ok);

                // THE OPENINGS THE MAT WAS PLANNED AROUND, against the bars Revit
                // drew. Containment already refuses a bar over a void; this says
                // whether the drawn bars honour the declared policy - and, for trim,
                // the declared clearance - by the same arithmetic the plan used.
                if (b.Rule.OpeningContext != null)
                    checks["clear_of_openings"] = OpeningsCheck(b, barPoints, fromModel, measured,
                                                                set.Tolerances.LengthMm, ref ok);
            }

            // THE ARRAY LENGTH, against what Revit REPORTS rather than what was
            // declared. The two differ by exactly one model bar diameter, always.
            // Asserting the declared number here failed every correct array whose
            // bar was thicker than the length tolerance - which is every real bar.
            if (!singleBar)
            {
                double shortfallMm;
                string arrayWhy;
                bool arrayOk = RebarArrayGeometry.SpanIsWithinBound(
                    b.Layout.ArrayLengthMm, arrayReadMm ?? double.NaN, arrayModelDiameterMm,
                    set.Tolerances.LengthMm, out shortfallMm, out arrayWhy);
                JObject arrayCheck = Check(arrayOk, Math.Round(b.Layout.ArrayLengthMm, 3),
                    arrayReadMm.HasValue ? (object)Math.Round(arrayReadMm.Value, 3) : null);
                arrayCheck["declared_mm"] = Math.Round(b.Layout.ArrayLengthMm, 3);
                arrayCheck["model_diameter_mm"] = arrayModelDiameterMm > 0
                    ? (JToken)Math.Round(arrayModelDiameterMm, 3) : JValue.CreateNull();
                arrayCheck["shortfall_mm"] = double.IsNaN(shortfallMm)
                    ? JValue.CreateNull() : (JToken)Math.Round(shortfallMm, 3);
                arrayCheck["allowed_shortfall_mm"] = arrayModelDiameterMm > 0
                    ? (JToken)Math.Round(arrayModelDiameterMm, 3) : JValue.CreateNull();
                arrayCheck["why"] = arrayWhy;
                checks["array_length"] = arrayCheck;
                ok &= arrayOk;
            }
            else
            {
                checks["array_length"] = NotApplicable("a single bar has no array.");
            }

            // THE PLACEMENT (L7), re-read: the centreline Revit drew against the radius, the plane or
            // the height the hoop or spiral was placed at; the shape's size parameter; a spiral's pitch,
            // finishing turns and height.
            if (b.Rule.Placement != null)
                VerifyPlacement(doc, bar, b, set, checks, ref ok);

            // THE COUPLER (L7): one coupler at the END of this piece and the START of the next, of the
            // declared type - read from both bars.
            if (b.Rule.CouplerNextRuleId != null)
                checks["coupler_next"] = VerifyCoupler(doc, bar, m, made, ref ok);

            // Length is REPORTED, not asserted, whenever a hook is declared: Revit
            // adds hook length itself, and an expectation that guessed at it would
            // fail on every correctly built bar.
            double? totalFt = null;
            try { totalFt = bar.TotalLength; } catch { }
            bool hooked = b.StartHookId != ElementId.InvalidElementId || b.EndHookId != ElementId.InvalidElementId;
            // A BENT BAR IS SHORTER THAN ITS POLYLINE. Revit rounds every corner to
            // the bend radius, so a declared L or a stirrup measures LESS than the
            // sum of its segments - and asserting the polyline length against it
            // would fail on every correctly built bent bar. Only a straight,
            // hook-free bar is directly comparable; the rest is reported.
            bool straight = b.PointsMm.Count == 2 && !b.Rule.Closed;
            double expected = b.ExpectedBarLengthMm * b.Layout.Quantity;
            var length = new JObject
            {
                ["expected_from_declared_centreline_mm"] = Math.Round(expected, 3),
                ["revit_reports_mm"] = totalFt.HasValue ? (JToken)Math.Round(totalFt.Value * FtToMm, 3) : JValue.CreateNull(),
                ["hooks_declared"] = hooked,
                ["bar_is_straight"] = straight
            };
            // THE BAR AS DRAWN, NOT AS SCHEDULED (measured, Revit 2024, acero L6): TotalLength is rounded
            // per bar by the host's rebar rounding - a 4432.1 mm bar reads 4430 - so nine bars on a
            // stair's waist came out 19 mm "short". The centreline Revit drew is what is compared; the
            // rounded total is kept beside it as Revit's own figure.
            double? oneBarFt = RebarFacts.OneBarCentrelineFt(bar);
            if (oneBarFt.HasValue)
            {
                length["measured_centreline_mm"] = Math.Round(oneBarFt.Value * FtToMm * b.Layout.Quantity, 3);
                length["revit_reports_means"] = "Revit's TotalLength, rounded per bar by the host's rebar rounding; " +
                                                "measured_centreline_mm is what was compared.";
            }
            double? comparedFt = oneBarFt.HasValue ? oneBarFt.Value * b.Layout.Quantity : totalFt;
            // A COUPLED END IS SEATED, NOT PLACED (L7, measured 2024 and 2026): Revit leaves it short of the
            // station inside the coupler. Where it is on the bar's line and inside the coupler's body, the
            // trim is taken off what is expected - the same rule the audit applies (CoupledEnd).
            double trimMm = CoupledTrimMm(doc, bar, b, set.Tolerances.LengthMm);
            if (trimMm > 0)
            {
                expected -= trimMm * b.Layout.Quantity;
                length["coupled_end_trim_mm"] = Math.Round(trimMm, 3);
                length["expected_after_coupled_trim_mm"] = Math.Round(expected, 3);
            }
            if (!hooked && straight && comparedFt.HasValue)
            {
                double diff = Math.Abs(comparedFt.Value * FtToMm - expected);
                bool lenOk = diff <= set.Tolerances.LengthMm * Math.Max(1, b.Layout.Quantity);
                length["verified"] = lenOk;
                length["difference_mm"] = Math.Round(diff, 3);
                ok &= lenOk;
            }
            else if (!hooked && straight)
            {
                // UNREADABLE IS NOT A PASS. This branch used to leave `ok` untouched,
                // in a file whose neighbouring check says exactly that.
                length["verified"] = false;
                length["why"] = "Revit would not report the total length of a straight, hook-free bar, so the " +
                                "steel in this set was not measured. Unknown is not a pass.";
                ok = false;
            }
            else
            {
                length["verified"] = null;
                length["why"] = hooked
                    ? "a hook is declared, and Revit adds its length itself. Comparing against a centreline that " +
                      "excludes it would fail on every correctly built bar, so this is reported and not asserted."
                    : "this bar is bent, and Revit rounds every corner to the bend radius - so its centreline is " +
                      "SHORTER than the polyline that was declared. The two are not the same quantity, and " +
                      "asserting one against the other would fail on every correctly built bent bar.";
            }
            checks["total_length"] = length;

            // THE BAR AGAINST ITS PLAN (stage 1c), judged with THE AUDIT'S OWN RULE on the same reading of the drawn
            // bar. MEASURED (Revit 2024, anonymized regression case COLUMN-A built alone): a bar Revit moved 11.45 mm off its plan, its
            // handles pinned to a neighbour's cover, came back verified from here while the audit reported
            // geometry_differs - the length above is asserted only on a straight hook-free bar, and nothing
            // compared the drawn centreline of a bent or hooked one with the planned polyline.
            JObject geometry;
            try
            {
                geometry = RebarAuditRules.GeometryMatchesPlan(ReinforcementResolver.DescribeRebarRow(b, 0),
                    RebarFacts.Describe(doc, bar, false), set.Tolerances, set.HoldsEnds(b.Rule),
                    RebarConstraintRead.Summary(bar));
            }
            catch (Exception ex)
            {
                geometry = new JObject
                {
                    ["verified"] = false,
                    ["why"] = "the bar's geometry could not be compared with its plan: " + ex.Message + " Unknown is not a pass."
                };
            }
            checks["geometry_matches_the_plan"] = geometry;
            if (geometry["verified"]?.Type == JTokenType.Boolean) ok &= (bool)geometry["verified"];
            else if (geometry["applicable"]?.Type != JTokenType.Boolean || (bool)geometry["applicable"]) ok = false;

            string provWhy;
            StructuralProvenance prov = StructuralProvenanceStore.Read(bar, out provWhy);
            checks["provenance"] = new JObject
            {
                ["written"] = prov != null,
                ["why_not"] = prov == null ? provWhy : null,
                ["rule_id"] = prov == null ? null : prov.RuleId,
                ["requirement_set_id"] = prov == null ? null : prov.RequirementSetId,
                ["requirement_set_sha256"] = prov == null ? null : prov.RequirementSetSha256
            };
            ok &= prov != null;

            // HOLD_ENDS (stage 1b), re-read AFTER the commit: the held bar must be on the plan at every vertex.
            if (m.Hold != null)
            {
                var hold = (JObject)m.Hold.DeepClone();
                if (hold["applicable"]?.Type != JTokenType.Boolean || (bool)hold["applicable"])
                {
                    double w, t;
                    bool comparable = HoldEndsRules.Deviation(b.PointsMm, RebarFacts.CentrelinePointsMm(bar, true), out w, out t);
                    hold["after_commit_mm"] = comparable ? (JToken)Math.Round(w, 3) : JValue.CreateNull();
                    hold["verified"] = comparable && HoldEndsRules.Held(w);
                    ok &= (bool)hold["verified"];
                }
                checks["hold_ends"] = hold;
            }
            row["verified"] = ok;
            row["checks"] = checks;
            return row;
        }

        private static void VerifyPlacement(Document doc, Rebar bar, ResolvedRebarRow b, StructuralRequirementSet set,
                                            JObject checks, ref bool ok)
        {
            PlacedShapeGeometry g = b.Rule.Placement;
            PlacedShapeCheck pc = PlacedShapes.CheckCentreline(g, RebarFacts.CentrelinePointsMm(bar, false), set.Tolerances.LengthMm);
            JObject pj = pc.ToJson();
            pj["source"] = "Rebar.GetCenterlineCurves, tessellated, read back after the commit, against the placement's axis";
            pj["not_compared"] = "where round the axis the lap or the first turn starts, and the length of the lap";
            checks["placement_centreline"] = pj;
            ok &= pc.Verified;

            if (b.PlacedSizeParamId != null && b.PlacedSizeParamId != ElementId.InvalidElementId)
            {
                double? want = PlacedShapes.ExpectedSizeMm(b.PlacedSizeConstraint, b.PlacedSizeReference, g.CentrelineRadiusMm,
                                                           ReinforcementResolver.SafeDiameterMm(b.BarType));
                double? got = ReinforcementResolver.ReadShapeParameterMm(doc, bar, b.PlacedSizeParamId);
                bool sizeOk = want.HasValue && got.HasValue && Math.Abs(got.Value - want.Value) <= set.Tolerances.LengthMm;
                JObject sc = Check(sizeOk, want.HasValue ? (object)Math.Round(want.Value, 3) : null, got.HasValue ? (object)Math.Round(got.Value, 3) : null);
                sc["constraint"] = b.PlacedSizeConstraint;
                sc["measured_to"] = b.PlacedSizeReference;
                if (!want.HasValue) sc["why"] = "the shape measures its " + b.PlacedSizeConstraint + " to '" + b.PlacedSizeReference +
                                                "', which this build does not predict. Unknown is not a pass.";
                else if (!got.HasValue) sc["why"] = "the bar would not report the shape's " + b.PlacedSizeConstraint + " parameter.";
                checks["placement_size_parameter"] = sc;
                ok &= sizeOk;
            }
            else
                checks["placement_size_parameter"] = NotApplicable(
                    "the shape's definition carries no diameter or radius constraint, so there is no size parameter to read; the " +
                    "centreline radius in placement_centreline is the measurement.");

            if (g.IsSpiral)
            {
                JObject sp = ReinforcementResolver.SpiralParametersRead(bar, g, set.Tolerances.LengthMm);
                checks["spiral_parameters"] = sp;
                ok &= (bool)sp["verified"];
            }
        }

        /// <summary>The summed trim of this bar's coupled ends that sit inside their couplers, in mm (0 when none).</summary>
        private static double CoupledTrimMm(Document doc, Rebar bar, ResolvedRebarRow b, double tolMm)
        {
            if (!b.Rule.CoupledAtStart && b.Rule.CouplerNextRuleId == null) return 0;
            // AS DRAWN: the declared form does not carry Revit's seating of the coupled end.
            List<double[]> drawn = RebarFacts.CentrelinePointsMm(bar, false);
            List<double[]> decl = b.PointsMm;
            if (drawn == null || drawn.Count < 2 || decl == null || decl.Count < 2) return 0;
            double sum = 0;
            foreach (int end in new[] { 0, 1 })
            {
                if (end == 0 && !b.Rule.CoupledAtStart) continue;
                if (end == 1 && (b.Rule.CouplerNextRuleId == null || b.Rule.CouplerNextEnd != 1)) continue;
                BoundingBoxXYZ box = null;
                try
                {
                    ElementId cid = bar.GetCouplerId(end);
                    if (cid != null && cid != ElementId.InvalidElementId) box = doc.GetElement(cid)?.get_BoundingBox(null);
                }
                catch { }
                if (box == null) continue;
                double t = CoupledEnd.TrimMm(end == 0 ? decl[0] : decl[decl.Count - 1], end == 0 ? decl[1] : decl[decl.Count - 2],
                                             end == 0 ? drawn[0] : drawn[drawn.Count - 1],
                                             new[] { box.Min.X * FtToMm, box.Min.Y * FtToMm, box.Min.Z * FtToMm },
                                             new[] { box.Max.X * FtToMm, box.Max.Y * FtToMm, box.Max.Z * FtToMm }, tolMm);
                if (t > 0) sum += t;
            }
            return sum;
        }

        private static JObject VerifyCoupler(Document doc, Rebar bar, Built m, List<Built> made, ref bool ok)
        {
            ResolvedRebarRow b = m.Row;
            Built next = Partner(made, m);
            var o = new JObject
            {
                ["next_rule_id"] = b.Rule.CouplerNextRuleId,
                ["next_element_id"] = next == null ? -1 : Rid.Value(next.Id),
                ["declared_type_id"] = b.CouplerType == null ? -1 : Rid.Value(b.CouplerType.Id),
                ["declared_type_name"] = b.Rule.CouplerNextTypeName,
                ["source"] = "Rebar.GetCouplerId at the end of this piece and at the start of the next, and the coupler's own type, " +
                             "read back after the commit"
            };
            ElementId atEnd = null, atStart = null;
            try { atEnd = bar.GetCouplerId(b.Rule.CouplerNextEnd); } catch { }
            var nextBar = next == null ? null : doc.GetElement(next.Id) as Rebar;
            try { atStart = nextBar?.GetCouplerId(0); } catch { }
            var coupler = atEnd == null || atEnd == ElementId.InvalidElementId ? null : doc.GetElement(atEnd) as RebarCoupler;
            ElementId typeId = null;
            try { typeId = coupler?.GetTypeId(); } catch { }
            bool links = false;
            try { links = coupler != null && coupler.CouplerLinkTwoBars(); } catch { }
            o["coupler_at_end_of_this_piece"] = atEnd == null ? -1 : Rid.Value(atEnd);
            o["coupler_at_start_of_next_piece"] = atStart == null ? -1 : Rid.Value(atStart);
            o["read_type_id"] = typeId == null ? -1 : Rid.Value(typeId);
            o["links_two_bars"] = links;
            bool verified = coupler != null && atStart != null && atStart == atEnd && links &&
                            b.CouplerType != null && typeId == b.CouplerType.Id;
            o["verified"] = verified;
            if (!verified)
                o["why"] = next == null ? "the next piece was not built, so there is no splice to couple."
                         : coupler == null ? "this piece carries no coupler at its end."
                         : atStart != atEnd ? "the next piece does not carry the same coupler at its start."
                         : !links ? "the coupler does not sit on two bars."
                         : "the coupler is of another type than the layer declares.";
            ok &= verified;
            return o;
        }

        /// <summary>
        /// The first and last station Revit drew, against the ones a cover-aware
        /// zone predicted. The prediction rests on ADR-003 item 7 - the array is
        /// clamped to cover + bar radius at each end - and this is the only thing
        /// that proves it for THIS host: the first bar must sit where the plan put
        /// it, to the length tolerance, and the last may fall short of its station
        /// by no more than one model bar diameter, the bound the array check holds.
        /// </summary>
        private static JObject CoverPredictionCheck(ResolvedRebarRow b, List<double[]> barPoints, bool fromModel,
                                                    List<double> measured, double? arrayReadMm,
                                                    double modelDiameterMm, double toleranceMm, ref bool ok)
        {
            StirrupCoverPrediction cp = b.Rule.CoverPrediction;
            var o = new JObject
            {
                ["status"] = StirrupCoverPrediction.Marker,
                ["source"] = cp.Source,
                ["cover_mm"] = Math.Round(cp.CoverMm, 3),
                ["bar_radius_mm"] = Math.Round(cp.BarRadiusMm, 3),
                ["clamp_each_end_mm"] = Math.Round(cp.ClampEachEndMm, 3),
                ["host_span_mm"] = Math.Round(cp.HostSpanMm, 3),
                ["usable_span_mm"] = Math.Round(cp.UsableSpanMm, 3),
                ["zone"] = cp.ZoneName,
                ["bar_read_from_model"] = fromModel
            };
            double[] along = RebarContainment.Unit(cp.Along);
            if (along == null || barPoints == null || barPoints.Count == 0 || b.Rule.CurvesMm.Count == 0)
            {
                o["verified"] = false;
                o["why"] = "the zone direction or the drawn bar was not available, so the prediction was not " +
                           "compared. Unknown is not a pass.";
                ok = false;
                return o;
            }

            double predictedFirst = b.Rule.CurvesMm.Average(p => RebarPlanRules.Project(p, along));
            double measuredFirst = barPoints.Average(p => RebarPlanRules.Project(p, along));
            double firstDiff = Math.Abs(measuredFirst - predictedFirst);
            // The station from the host's start, on the assumption the profile was
            // declared AT that start - which is what "the outline at the START of
            // the span" means and what the prediction rests on. Published so a
            // profile declared elsewhere shows up as a station that is not the
            // clamp, rather than as an inexplicable failure.
            double hostStartAt = predictedFirst - cp.ZoneStartMm;
            o["first_station_predicted_mm"] = Math.Round(cp.ZoneStartMm, 3);
            o["first_station_measured_mm"] = Math.Round(measuredFirst - hostStartAt, 3);
            o["first_bar_difference_mm"] = Math.Round(firstDiff, 3);
            bool firstOk = fromModel && firstDiff <= toleranceMm;

            double predictedLastOffset = b.Layout.PositionsMm.Count > 0
                ? b.Layout.PositionsMm[b.Layout.PositionsMm.Count - 1] : 0;
            double measuredLastOffset = measured.Count > 0 ? measured[measured.Count - 1] : 0;
            double lastShortfall = predictedLastOffset - measuredLastOffset;
            double allowed = modelDiameterMm > 0 ? modelDiameterMm : 0;
            bool lastOk = fromModel && lastShortfall >= -toleranceMm && lastShortfall <= allowed + toleranceMm;
            o["last_station_predicted_mm"] = Math.Round(cp.ZoneEndMm, 3);
            o["last_station_measured_mm"] = Math.Round(measuredFirst - hostStartAt + measuredLastOffset, 3);
            o["last_bar_shortfall_mm"] = Math.Round(lastShortfall, 3);
            o["last_bar_allowed_shortfall_mm"] = Math.Round(allowed, 3);
            o["array_length_revit_reports_mm"] = arrayReadMm.HasValue
                ? (JToken)Math.Round(arrayReadMm.Value, 3) : JValue.CreateNull();
            o["tolerance_mm"] = toleranceMm;
            o["verified"] = firstOk && lastOk;
            o["why"] = !fromModel
                ? "the bar would not return its centreline, so the drawn stations are unknown. Unknown is not a pass."
                : !firstOk
                    ? "Revit drew the first bar " + Mm(firstDiff) + " from where the cover-derived plan put it. " +
                      "Either the host's cover is not what was planned with, or the profile is not at the " +
                      "host's start, or the measured clamping rule does not hold for this host - the first " +
                      "bar of a hosted array has not moved in any measured case."
                    : !lastOk
                        ? "the last bar falls " + Mm(lastShortfall) + " short of its predicted station, and the " +
                          "measured bound is one model bar diameter, " + Mm(allowed) + "."
                        : "the first bar is where the cover-derived plan put it and the last is within the " +
                          "measured bound; the prediction held for this host.";
            ok &= firstOk && lastOk;
            return o;
        }

        /// <summary>
        /// The drawn bars against the openings the mat was planned around. What
        /// counts as verified depends on the policy: under omit and trim no drawn
        /// bar may have its body over a considered opening, and under trim none
        /// may stop inside the declared clearance; under ignore the crossings are
        /// reported and not asserted, because building them was the declaration.
        /// </summary>
        private static JObject OpeningsCheck(ResolvedRebarRow b, List<double[]> barPoints, bool fromModel,
                                             List<double> measured, double toleranceMm, ref bool ok)
        {
            MatOpeningContext ctx = b.Rule.OpeningContext;
            MatOpeningCheck check = MatOpenings.CheckBars(ctx, barPoints, measured, toleranceMm);
            JObject o = check.ToJson();
            o["policy"] = ctx.Policy ?? "not_declared";
            o["openings_considered"] = ctx.Considered.Count();
            o["bar_read_from_model"] = fromModel;
            o["source"] = "the centreline Revit drew, offset by GetBarPositionTransform, in the component's own " +
                          "frame, against the opening rings read from the host's face - the same arithmetic the " +
                          "plan used to omit or trim.";
            bool asserted = ctx.Policy != StructuralMatOpenings.PolicyIgnore;
            if (!asserted)
            {
                o["verified"] = JValue.CreateNull();
                o["asserted"] = false;
                o["why"] = "policy ignore builds the bars as declared; the crossings are reported here and " +
                           "inside_host_solid is what refuses a bar really over the void. " + check.Why;
                return o;
            }
            bool clear = fromModel && check.Evaluated && check.Crossing.Count == 0 &&
                         (ctx.Policy != StructuralMatOpenings.PolicyTrim || check.ShortOfClearance.Count == 0);
            o["verified"] = clear;
            if (!fromModel) o["why"] = "the bar would not return its centreline, so this was measured on the " +
                                       "DECLARED geometry. Unknown is not a pass.";
            ok &= clear;
            return o;
        }

        private static string Mm(double v)
        {
            return Math.Round(v, 3).ToString(CultureInfo.InvariantCulture) + " mm";
        }

        /// <summary>
        /// A boolean that was SENT and must be read back. An unreadable flag is a
        /// failure, not a shrug: it decides which bars exist.
        /// </summary>
        private static void CompareFlag(JObject checks, string name, bool requested, bool? read, ref bool ok)
        {
            bool agree = read.HasValue && read.Value == requested;
            checks[name] = Check(agree, requested, read);
            if (!read.HasValue) checks[name]["why"] = "the model would not report this flag.";
            ok &= agree;
        }

        private static bool? Flag(Func<bool> f)
        {
            try { return f(); } catch { return null; }
        }

        /// <summary>
        /// A question that does not arise. `verified` is NULL rather than true or
        /// false: neither of those is available, and both would be a claim.
        /// </summary>
        private static JObject NotApplicable(string why)
        {
            return new JObject
            {
                ["verified"] = JValue.CreateNull(),
                ["applicable"] = false,
                ["why"] = why
            };
        }

        /// <summary>
        /// The centreline Revit DREW for bar 0 of this set, in millimetres, or null
        /// when it would not answer. Every endpoint of every curve, so the extent is
        /// the bar's real one - hooks and rounded corners included.
        /// </summary>
        /// <summary>Every bar set in a host, or an empty list when Revit would not say.</summary>
        private static List<Rebar> BarsInHost(Element host)
        {
            if (host == null) return new List<Rebar>();
            RebarHostData data = null;
            try { data = RebarHostData.GetRebarHostData(host); } catch { }
            if (data == null) return new List<Rebar>();
            using (data)
            {
                try { return data.GetRebarsInHost().ToList(); }
                catch { return new List<Rebar>(); }
            }
        }

        private static JObject Check(bool ok, object requested, object read)
        {
            return new JObject
            {
                ["verified"] = ok,
                ["requested"] = requested == null ? JValue.CreateNull() : JToken.FromObject(requested),
                ["read"] = read == null ? JValue.CreateNull() : JToken.FromObject(read)
            };
        }

        // ----------------------------------------------------------- the plan

        private static ResolvedPlan BuildPlan(Document doc, GateResult gate, StructuralRequirementSet set,
                                              string setSha, List<ResolvedCoverRow> covers,
                                              List<ResolvedRebarRow> bars, List<ResolvedSystemRow> systems,
                                              List<ResolvedHookLength> hookLengths)
        {
            var plan = new ResolvedPlan
            {
                Command = "horizun_apply_reinforcement",
                DocumentKey = gate.Fingerprint,
                RevitVersion = doc.Application.VersionNumber,
                DocumentFingerprint = gate.Identity == null ? null : gate.Identity.FingerprintDigest()
            };
            int i = 0;
            foreach (ResolvedRebarRow b in bars)
            {
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = "rebar:" + b.Rule.Id + ":" + Rid.Value(b.Host.Id),
                    Category = "OST_Rebar",
                    TypeName = SafeName(b.BarType),
                    Action = PlannedAction.Create,
                    HostUniqueId = SafeUid(b.Host),
                    // EVERYTHING THE RESULT DEPENDS ON goes in here, so that a host
                    // that moved 50 mm, a bar type whose diameter changed, or a
                    // shape swapped between rehearsal and apply all invalidate the
                    // token rather than silently building something else.
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "rule", b.Rule.Id },
                        { "set_sha", setSha },
                        { "bar_type_uid", SafeUid(b.BarType) },
                        { "shape_uid", b.Shape == null ? "(none)" : SafeUid(b.Shape) },
                        { "start_hook", Rid.Value(b.StartHookId).ToString(CultureInfo.InvariantCulture) },
                        { "end_hook", Rid.Value(b.EndHookId).ToString(CultureInfo.InvariantCulture) },
                        { "layout", b.Layout.Layout },
                        { "positions", b.Layout.NumberOfBarPositions.ToString(CultureInfo.InvariantCulture) },
                        { "quantity", b.Layout.Quantity.ToString(CultureInfo.InvariantCulture) },
                        { "array_mm", b.Layout.ArrayLengthMm.ToString("0.###", CultureInfo.InvariantCulture) },
                        { "host_box", HostBox(b.Host) },
                        // L7: where a placed hoop or spiral goes and which coupler joins a splice.
                        { "placement", b.Rule.Placement == null ? "(none)" : b.Rule.Placement.ToJson().ToString(Formatting.None) },
                        { "coupler_type_uid", b.CouplerType == null ? "(none)" : SafeUid(b.CouplerType) }
                    },
                    GeometryFingerprint = Geometry(b)
                });
                i++;
            }
            foreach (ResolvedSystemRow sr in systems)
            {
                // EVERYTHING THE SYSTEM DEPENDS ON, so a type swapped, a layer edited or a host
                // moved between rehearsal and apply invalidates the token.
                var before = new Dictionary<string, string>
                {
                    { "rule", sr.RuleId },
                    { "kind", sr.Kind },
                    { "set_sha", setSha },
                    { "system_type_uid", SafeUid(sr.SystemType) },
                    { "sheet_type_uid", sr.SheetType == null ? "(none)" : SafeUid(sr.SheetType) },
                    { "host_box", HostBox(sr.Host) }
                };
                foreach (KeyValuePair<string, RebarBarType> bt in sr.BarTypes.OrderBy(x => x.Key, StringComparer.Ordinal))
                    before["bar_type_uid:" + bt.Key] = SafeUid(bt.Value);
                var g = new System.Text.StringBuilder();
                foreach (double[] pt in sr.PointsMm)
                    g.Append(string.Join(",", pt.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)))).Append(';');
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = sr.Kind + ":" + sr.RuleId + ":" + Rid.Value(sr.Host.Id),
                    Category = sr.Kind == SystemReinforcementRules.KindArea ? "OST_AreaRein"
                             : sr.Kind == SystemReinforcementRules.KindPath ? "OST_PathRein" : "OST_FabricAreas",
                    TypeName = SafeName(sr.SystemType),
                    Action = PlannedAction.Create,
                    HostUniqueId = SafeUid(sr.Host),
                    BeforeValues = before,
                    GeometryFingerprint = g.ToString()
                });
            }
            // THE BAR TYPES A DECLARED HOOK LENGTH TOUCHES, with what they carry now: a type somebody
            // edited between rehearsal and apply invalidates the token rather than being overwritten.
            foreach (ResolvedHookLength h in hookLengths.Where(x => x.Ok))
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = "hook_length:" + h.Declared.Path,
                    Category = "OST_Rebar",
                    TypeName = SafeName(h.BarType),
                    Action = PlannedAction.Modify,
                    HostUniqueId = SafeUid(h.BarType),
                    BeforeValues = RebarHookLengths.BeforeValues(h, setSha)
                });
            foreach (ResolvedCoverRow c in covers)
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = "cover:" + c.Rule.Id + ":" + Rid.Value(c.Host.Id),
                    Category = "cover",
                    TypeName = SafeName(c.CoverType),
                    Action = PlannedAction.Modify,
                    HostUniqueId = SafeUid(c.Host),
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "rule", c.Rule.Id },
                        { "set_sha", setSha },
                        // THE COVER TYPE ITSELF, not only its name. A rule may name a
                        // type without a distance, and the name alone left the token
                        // valid while somebody edited that type's distance or
                        // replaced it - so the apply set a cover the rehearsal had
                        // shown as a different number.
                        { "cover_type_uid", SafeUid(c.CoverType) },
                        { "cover_mm", c.WantedDistanceMm.HasValue
                            ? c.WantedDistanceMm.Value.ToString("0.###", CultureInfo.InvariantCulture) : "(unread)" },
                        { "current_mm", c.CurrentDistanceMm.HasValue
                            ? c.CurrentDistanceMm.Value.ToString("0.###", CultureInfo.InvariantCulture) : "(none)" },
                        // L7: which faces, and how many were found pointing that way.
                        { "face", CoverFaceRules.Describe(c.Rule.FaceNormalMm) },
                        { "faces", c.Faces.Count.ToString(CultureInfo.InvariantCulture) }
                    }
                });
            return plan;
        }

        private static string Geometry(ResolvedRebarRow b)
        {
            var sb = new System.Text.StringBuilder();
            foreach (double[] p in b.PointsMm)
                sb.Append(p[0].ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p[1].ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p[2].ToString("0.##", CultureInfo.InvariantCulture)).Append(';');
            sb.Append('n').Append(b.Normal.X.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
              .Append(b.Normal.Y.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
              .Append(b.Normal.Z.ToString("0.####", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static string HostBox(Element host)
        {
            BoundingBoxXYZ box = null;
            try { box = host.get_BoundingBox(null); } catch { }
            if (box == null) return "(unmeasured)";
            return string.Join(",", new[]
            {
                box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z
            }.Select(v => (v * FtToMm).ToString("0.##", CultureInfo.InvariantCulture)));
        }

        private static string SafeName(Element e)
        {
            try { return e == null ? null : e.Name; } catch { return null; }
        }

        private static string SafeUid(Element e)
        {
            try { return e == null ? null : e.UniqueId; } catch { return null; }
        }
    }
}
