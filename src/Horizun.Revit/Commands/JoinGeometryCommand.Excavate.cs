// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_join_geometry - excavate / unexcavate: cut a TOPOSOLID with another element (Toposolid.ExcavateBy /
// RemoveExcavationBy, Revit 2025+), the way a basement, a pool or a footing is sunk into the terrain without
// editing the terrain's own points. Pairs are [toposolid_id, excavating_element_id].
//
// Its own path, not ModelEditRunner's: the dry run is a REAL rehearsal - the cut is made inside a transaction that
// is rolled back (rollback confirmed) and what Revit reports for it is read back - and the apply re-reads the
// committed model and rolls the whole group back on any disagreement.
//
// The relation is read back through Toposolid.GetIntersectingElementData (IntersectionType.Excavate, the
// excavating element's id and the volume removed). Before 2025 there is no such call: the refusal is typed
// (unsupported capability), nothing is written, and the message says why.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class JoinGeometryCommand
    {
        private const double CubicFeetToM3 = 0.028316846592;


        private CommandResult ExecuteExcavation(UIApplication app, JObject request)
        {
            string op = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            bool excavate = op == ToposolidExcavationRules.Excavate;
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
#if REVIT2025 || REVIT2026 || REVIT2027
            var pairs = new List<ExcavationPair>();
            var members = new List<Element>();
            try
            {
                foreach (JArray t in (JArray)request["pairs"])
                {
                    long topoId = t[0].Value<long>(), byId = t[1].Value<long>();
                    if (!Rid.CanRepresent(topoId) || !Rid.CanRepresent(byId)) throw new ArgumentException("pair [" + topoId + ", " + byId + "] holds an id outside the supported range.");
                    var topo = doc.GetElement(Rid.Make(topoId)) as Toposolid;
                    if (topo == null) throw new ArgumentException("element " + topoId + " is not a Toposolid of this document.");
                    Element by = doc.GetElement(Rid.Make(byId));
                    if (by == null || by is ElementType) throw new ArgumentException("excavating element " + byId + " is not a placed element of this document.");
                    var pair = new ExcavationPair { ToposolidId = topoId, ExcavatorId = byId };
                    pair.ExcavatedBefore = ExcavatedBy(topo, by.Id, out _);
                    if (excavate && !pair.ExcavatedBefore && !topo.CanBeExcavatedBy(by.Id))
                        throw new ArgumentException("element " + byId + " (" + (by.Category?.Name ?? by.GetType().Name) + ") cannot excavate toposolid " + topoId +
                            ": Revit's CanBeExcavatedBy refuses it (solid elements such as floors, walls, footings or masses can).");
                    if (!excavate && !pair.ExcavatedBefore)
                        throw new ArgumentException("toposolid " + topoId + " is not excavated by " + byId + ", so there is nothing to remove.");
                    pairs.Add(pair);
                    members.Add(topo); members.Add(by);
                }
            }
            catch (Exception ex)
            {
                var verdict = FallbackDecision.Decide(new[] { new ActionOutcome { Index = 0, Error = ex.Message, UnsupportedReason = UnsupportedCapability.ReasonOf(ex) } }, writeStarted: false);
                return CommandResult.RefuseBeforeWrite(ex.Message, fallback: verdict.Signal, capabilityGaps: verdict.CapabilityGaps);
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, ModelEditRunner.PlanScopeFields);
            var resolved = new ResolvedPlan { Command = Name, DocumentKey = gate.Fingerprint, RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest() };
            foreach (Element m in members.GroupBy(e => Rid.Value(e.Id)).Select(g => g.First()).OrderBy(e => Rid.Value(e.Id)))
                resolved.Elements.Add(ModelEditRunner.Planned(m, PlannedAction.Modify, request));

            if (dryRun)
            {
                JObject rehearsal = RehearseExcavation(doc, pairs, excavate);
                bool ok = rehearsal.Value<bool>("rollback_confirmed") && rehearsal["error"] == null &&
                          pairs.All(p => p.ExcavatedAfterRehearsal == excavate);
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "rehearsed_and_rolled_back", ["operation"] = op,
                    ["plan"] = new JObject
                    {
                        ["pairs"] = new JArray(pairs.Select(p => new JObject { ["toposolid_id"] = p.ToposolidId, ["excavating_element_id"] = p.ExcavatorId,
                            ["excavated_before"] = p.ExcavatedBefore, ["excavated_in_rehearsal"] = p.ExcavatedAfterRehearsal.HasValue ? (JToken)p.ExcavatedAfterRehearsal.Value : null,
                            ["volume_removed_m3_in_rehearsal"] = p.VolumeM3.HasValue ? (JToken)Math.Round(p.VolumeM3.Value, 6) : null })),
                        ["rehearsal"] = rehearsal
                    },
                    ["note"] = "The cut was made inside a transaction and rolled back (rollback_confirmed); nothing remains in the model. The apply re-reads the committed model and rolls back on any disagreement."
                };
                if (!ok)
                    return CommandResult.FailWithDetail("The " + op + " rehearsal did not come out as asked" +
                        (rehearsal["error"] != null ? ": " + rehearsal["error"] : ", or its rollback was not confirmed") + ". Nothing was written.",
                        new JObject { ["dry_run"] = true, ["write_started"] = true, ["plan"] = result["plan"] });
                DocumentGate.RecordResolvedPlan(resolved);
                ApplicationOutcome.StampRehearsal(result, pairs.Count, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, true, "the token binds the operation, the pairs and the state of every element they touch");
                return CommandResult.Ok(result);
            }

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolved, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: " + op.Replace('_', ' ') + " toposolid";
            bool started = false;
            using (var group = new TransactionGroup(doc, txName))
            {
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction group did not start");
                    using (var tx = new Transaction(doc, txName))
                    {
                        if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction did not start");
                        started = true;
                        ApplyExcavation(doc, pairs, excavate);
                        doc.Regenerate();
                        Guard.Commit(tx, txName);
                    }
                    string disagreement = ReadBackExcavation(doc, pairs, excavate);
                    if (disagreement != null)
                    {
                        var rolled = Guard.RollBack(group);
                        return CommandResult.FailWithDetail("The committed model disagreed with the request, so the whole edit was rolled back: " + disagreement,
                            new JObject { ["code"] = "postcondition_failed", ["write_started"] = true, ["changes_applied"] = rolled.Confirmed ? (JToken)false : null, ["rollback_status"] = rolled.StatusName });
                    }
                    Guard.Assimilate(group, txName);
                }
                catch (Exception ex)
                {
                    string rb = "not_attempted";
                    try { if (group.GetStatus() == TransactionStatus.Started) rb = Guard.RollBack(group).StatusName; } catch (Exception e2) { rb = "failed: " + e2.Message; }
                    return CommandResult.FailWithDetail(Name + " failed: " + ex.Message, new JObject
                    {
                        ["code"] = ex is ArgumentException ? ErrorCodes.PrecondicionNoCumplida : (rb == "RolledBack" ? ErrorCodes.TransaccionRevertida : "revit_edit_failed"),
                        ["write_started"] = started, ["rollback_status"] = rb,
                        ["changes_applied"] = !started ? (JToken)false : (rb == "RolledBack" ? (JToken)false : null)
                    });
                }
            }
            // A second read after the group assimilated: a disagreement now cannot be undone by this call.
            string late = ReadBackExcavation(doc, pairs, excavate);
            if (late != null)
                return CommandResult.FailWithDetail("Committed, but the re-read after the group assimilated disagrees; inspect the model: " + late,
                    new JObject { ["code"] = "postcommit_verification_failed", ["write_started"] = true, ["changes_applied"] = true });
            var rows = new JArray(pairs.Select(p => new JObject { ["toposolid_id"] = p.ToposolidId, ["excavating_element_id"] = p.ExcavatorId,
                ["excavated"] = excavate, ["verified"] = true,
                ["volume_removed_m3"] = excavate && p.VolumeM3.HasValue ? (JToken)Math.Round(p.VolumeM3.Value, 6) : null }));
            var done = new JObject { ["dry_run"] = false, ["transaction_status"] = "Committed", ["transaction_name"] = txName, ["operation"] = op, ["rows"] = rows,
                ["undo"] = new JObject { ["recorded"] = false, ["reason"] = "excavation is not covered by horizun_undo; run the opposite operation (unexcavate/excavate) or Revit's own undo." } };
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, pairs.Count, pairs.Count, pairs.Count, 0, 0, 0);
            return CommandResult.Ok(done);
#else
            string message = ToposolidExcavationRules.UnsupportedMessage(ParseYear(app));
            var gap = FallbackDecision.Decide(new[] { new ActionOutcome { Index = 0, Error = message,
                UnsupportedReason = UnsupportedCapability.ReasonOf(new UnsupportedCapability(message, FallbackSignal.ReasonUnsupportedCapability)) } }, writeStarted: false);
            return CommandResult.RefuseBeforeWrite(message, fallback: gap.Signal, capabilityGaps: gap.CapabilityGaps);
#endif
        }

        private static int ParseYear(UIApplication app)
        {
            int year;
            return int.TryParse(app?.Application?.VersionNumber, out year) ? year : 0;
        }

#if REVIT2025 || REVIT2026 || REVIT2027
        private sealed class ExcavationPair
        {
            public long ToposolidId, ExcavatorId;
            public bool ExcavatedBefore;
            public double? VolumeM3;
            public bool? ExcavatedAfterRehearsal;
        }

        /// <summary>Is this toposolid excavated by that element NOW, and by how much (m3, null when not).</summary>
        private static bool ExcavatedBy(Toposolid topo, ElementId by, out double? volumeM3)
        {
            volumeM3 = null;
            foreach (IntersectingElementData d in topo.GetIntersectingElementData())
            {
                if (d.IntersectionType != IntersectionType.Excavate || d.IntersectingElementId != by) continue;
                volumeM3 = d.IntersectionVolume * CubicFeetToM3;
                return true;
            }
            return false;
        }

        private static void ApplyExcavation(Document doc, List<ExcavationPair> pairs, bool excavate)
        {
            foreach (ExcavationPair p in pairs)
            {
                var topo = (Toposolid)doc.GetElement(Rid.Make(p.ToposolidId));
                ElementId by = Rid.Make(p.ExcavatorId);
                if (excavate) { if (!p.ExcavatedBefore) topo.ExcavateBy(by); }
                else topo.RemoveExcavationBy(by);
            }
        }

        /// <summary>Null when every pair reads as asked; otherwise the first disagreement. Fills each pair's volume.</summary>
        private static string ReadBackExcavation(Document doc, List<ExcavationPair> pairs, bool excavate)
        {
            foreach (ExcavationPair p in pairs)
            {
                var topo = doc.GetElement(Rid.Make(p.ToposolidId)) as Toposolid;
                if (topo == null) return "toposolid " + p.ToposolidId + " is gone.";
                bool now = ExcavatedBy(topo, Rid.Make(p.ExcavatorId), out double? volume);
                p.VolumeM3 = volume;
                if (now != excavate)
                    return "toposolid " + p.ToposolidId + (excavate ? " is not reported as excavated by " : " is still excavated by ") + p.ExcavatorId + ".";
                if (excavate && (!volume.HasValue || volume.Value <= 1e-9))
                    return "toposolid " + p.ToposolidId + " is related to " + p.ExcavatorId + " but no volume is removed: the element does not reach the terrain.";
            }
            return null;
        }

        private static JObject RehearseExcavation(Document doc, List<ExcavationPair> pairs, bool excavate)
        {
            Guard.RollbackResult? rb = null; string rbError = null, error = null;
            using (var tx = new Transaction(doc, "Horizun: rehearse " + (excavate ? "excavate" : "unexcavate")))
            {
                try
                {
                    tx.Start();
                    ApplyExcavation(doc, pairs, excavate);
                    doc.Regenerate();
                    string why = ReadBackExcavation(doc, pairs, excavate);
                    foreach (ExcavationPair p in pairs)
                        p.ExcavatedAfterRehearsal = ExcavatedBy((Toposolid)doc.GetElement(Rid.Make(p.ToposolidId)), Rid.Make(p.ExcavatorId), out _);
                    if (why != null) error = why;
                }
                catch (Exception ex) { error = ex.Message; }
                try { rb = Guard.RollBack(tx); } catch (Exception ex) { rbError = ex.Message; }
            }
            return new JObject
            {
                ["error"] = error, ["rollback_status"] = rb.HasValue ? rb.Value.StatusName : "exception: " + rbError,
                ["rollback_confirmed"] = rb.HasValue && rb.Value.Confirmed
            };
        }
#endif
    }
}
