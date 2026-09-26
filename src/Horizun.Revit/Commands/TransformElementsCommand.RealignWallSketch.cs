// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_transform_elements - realign_wall_sketch: the typed fix for a
// "stranded profile" (horizun_audit_model wall_sketch_drift). A wall's edited
// elevation profile lives in a Sketch that a move does not follow; this
// translates the sketch's curves back onto the wall's current location line,
// using the SAME drift Core/WallSketchGeometry.cs and WallSketchDriftRules.cs
// already compute for the audit finding - a correction that disagreed with
// the finding that proposed it would be its own defect.
//
// A SEPARATE PATH FROM THE REST OF THIS COMMAND, on purpose. Every other
// operation here shares one Transaction and a dry_run that opens no
// transaction at all - true for a move, a rotate, a type change. A sketch's
// curves can only be edited inside a SketchEditScope, which is its own
// unit of work with its own Cancel/Commit, incompatible with opening a
// second, nested Transaction inside the one the rest of this command already
// has open. So realign_wall_sketch may not be mixed with any other operation
// in one call - refused explicitly, not silently reinterpreted - and the
// REHEARSAL IS REAL: dry_run opens the same SketchEditScope, moves the same
// curves, and Cancels the scope instead of committing it. Revit's own
// geometry engine validates the move either way; only whether it survives is
// different. See Autodesk's own pattern for this exact API in
// Core/IfcProfileUpdate.cs, verified present in RevitAPI.dll for 2023-2027.
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
    public sealed partial class TransformElementsCommand
    {
        private const string RealignOp = "realign_wall_sketch";

        private sealed class RealignRow
        {
            public long Id;
            public Wall Wall;
            public ElementId SketchId;
            public WallSketchDriftResult Drift;
        }

        private CommandResult ExecuteRealignWallSketch(UIApplication app, GateResult gate, JObject request, JArray input)
        {
            Document doc = gate.Document;
            double toleranceMm = request.Value<double?>("tolerance_mm") ?? WallSketchDriftRules.DefaultToleranceMm;
            if (toleranceMm < 0) return CommandResult.Fail("tolerance_mm must be >= 0.");

            var claimed = new HashSet<long>();
            var errors = new JArray();
            var targets = new List<long>();

            for (int i = 0; i < input.Count; i++)
            {
                var o = input[i] as JObject;
                if (o == null) { errors.Add(new JObject { ["index"] = i, ["error"] = "entry is not an object" }); continue; }
                foreach (JProperty field in o.Properties())
                    if (field.Name != "operation" && field.Name != "element_ids")
                    { errors.Add(new JObject { ["index"] = i, ["error"] = field.Name + " is not applicable to " + RealignOp }); goto nextEntry; }
                JArray ids = o["element_ids"] as JArray;
                if (ids == null || ids.Count == 0 || ids.Count > 200)
                { errors.Add(new JObject { ["index"] = i, ["error"] = "element_ids must contain 1..200 ids" }); continue; }
                foreach (JToken t in ids)
                {
                    long raw = t.Value<long>();
                    if (!Rid.CanRepresent(raw)) { errors.Add(new JObject { ["index"] = i, ["error"] = "ElementId " + raw + " is outside the supported range" }); continue; }
                    if (!claimed.Add(raw)) { errors.Add(new JObject { ["index"] = i, ["error"] = "ElementId " + raw + " appears in more than one entry" }); continue; }
                    targets.Add(raw);
                }
                nextEntry: ;
            }

            var rows = new List<RealignRow>();
            foreach (long id in targets)
            {
                Element e = doc.GetElement(Rid.Make(id));
                var wall = e as Wall;
                if (wall == null) { errors.Add(new JObject { ["id"] = id, ["error"] = "not a Wall" }); continue; }
                ElementId sketchId;
                try { sketchId = wall.SketchId; } catch { sketchId = ElementId.InvalidElementId; }
                if (sketchId == null || Rid.Value(sketchId) < 0)
                { errors.Add(new JObject { ["id"] = id, ["error"] = "this wall carries no edited profile (Wall.SketchId); there is nothing to realign" }); continue; }
                WallSketchDriftResult drift = WallSketchGeometry.Evaluate(doc, wall, sketchId, toleranceMm);
                if (drift == null)
                { errors.Add(new JObject { ["id"] = id, ["error"] = "the sketch's plane or profile could not be read" }); continue; }
                if (!drift.Stranded)
                { errors.Add(new JObject { ["id"] = id, ["error"] = "already aligned within tolerance; nothing to correct" }); continue; }
                if (!drift.Correctable)
                { errors.Add(new JObject { ["id"] = id, ["error"] = drift.Reason }); continue; }
                rows.Add(new RealignRow { Id = id, Wall = wall, SketchId = sketchId, Drift = drift });
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "tolerance_mm", "operations");

            var resolvedPlan = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint, RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (RealignRow r in rows)
                resolvedPlan.Elements.Add(new PlannedElement
                {
                    UniqueId = SafePlanUniqueId(r.Wall),
                    ElementId = r.Id,
                    Category = r.Wall.Category?.Name,
                    TypeName = SafePlanTypeName(doc, r.Wall),
                    Action = PlannedAction.Modify,
                    GeometryFingerprint = SafePlanGeometry(r.Wall),
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "operation", RealignOp },
                        { "in_plane_offset_mm", r.Drift.InPlaneOffsetMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) }
                    },
                    ProposedValues = new Dictionary<string, string> { { "operation", RealignOp } }
                });

            if (dryRun)
            {
                var plan = new JArray();
                foreach (RealignRow r in rows)
                {
                    JObject detail; string err;
                    bool ok = TryRealign(doc, r.Wall, r.SketchId, ToXyzFeet(r.Drift.CorrectionVectorMm), commit: false, out detail, out err);
                    plan.Add(new JObject
                    {
                        ["element_id"] = r.Id,
                        ["correction_vector_mm"] = new JArray(r.Drift.CorrectionVectorMm.X, r.Drift.CorrectionVectorMm.Y, r.Drift.CorrectionVectorMm.Z),
                        ["in_plane_offset_mm"] = Math.Round(r.Drift.InPlaneOffsetMm, 1),
                        // REHEARSED, not assumed: the SketchEditScope actually opened, the curves actually
                        // moved, and Revit actually validated the sketch - then the whole scope was
                        // Cancelled. rehearsal_ok=false means Revit itself refused this move.
                        ["rehearsal_ok"] = ok,
                        ["rehearsal_error"] = err,
                        ["rehearsal_detail"] = detail
                    });
                }
                bool rehearsalClean = errors.Count == 0 && plan.All(p => ((JObject)p).Value<bool>("rehearsal_ok"));
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "rehearsed_and_cancelled",
                    ["targets"] = targets.Count, ["valid_targets"] = rows.Count, ["invalid_targets"] = errors.Count,
                    ["errors"] = errors, ["plan"] = plan,
                    ["note"] = "Every row was rehearsed inside a SketchEditScope that was then Cancelled: the " +
                                "sketch's curves were actually moved and Revit actually validated the result, " +
                                "but nothing was committed."
                };
                if (rehearsalClean) DocumentGate.RecordResolvedPlan(resolvedPlan);
                ApplicationOutcome.StampRehearsal(result, targets.Count, errors.Count, plan.Count(p => !((JObject)p).Value<bool>("rehearsal_ok")), 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, rehearsalClean,
                    rehearsalClean ? "the token binds the ordered element_ids and tolerance_mm" :
                    "no usable confirmation is issued while any target is invalid or its rehearsal failed");
                return CommandResult.Ok(result);
            }

            if (errors.Count > 0)
                return CommandResult.Fail("Invalid targets; nothing ran: " + errors.ToString(Formatting.None));

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            using (var group = new TransactionGroup(doc, "Horizun: realign wall sketch"))
            {
                if (group.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("the transaction group would not start. Nothing was changed.");
                try
                {
                    foreach (RealignRow r in rows)
                    {
                        JObject detail; string err;
                        bool ok = TryRealign(doc, r.Wall, r.SketchId, ToXyzFeet(r.Drift.CorrectionVectorMm), commit: true, out detail, out err);
                        if (!ok)
                        {
                            // REPORT what RollBack() actually returned - never assume RolledBack. See
                            // Guard.RollBack and PlanFailure.SingleTransactionOutcome: anything other
                            // than a confirmed RolledBack keeps its uncertainty in the message rather
                            // than claiming a clean model nobody re-read.
                            Guard.RollbackResult rb = Guard.RollBack(group);
                            return CommandResult.Fail("Realigning wall " + r.Id + " failed: " + err + " " +
                                PlanFailure.SingleTransactionOutcome(true, rb.StatusName, "the whole batch was rolled back; nothing was changed"));
                        }
                    }
                    // Guard.Assimilate throws SilentRollbackException when the group did not actually
                    // commit - the "758 lie" this whole file exists to prevent (see Guard.cs).
                    Guard.Assimilate(group, RealignOp);
                }
                catch (SilentRollbackException)
                {
                    if (group.HasStarted()) Guard.RollBack(group);
                    throw;
                }
                catch (Exception ex)
                {
                    bool attempted = false; string rbStatus = PlanFailure.NotAttempted;
                    if (group.HasStarted()) { attempted = true; rbStatus = Guard.RollBack(group).StatusName; }
                    return CommandResult.Fail("Realigning wall sketch failed: " + ex.Message + ". " +
                        PlanFailure.SingleTransactionOutcome(attempted, rbStatus, "nothing was changed"));
                }
            }

            var verification = new JArray();
            int verified = 0;
            foreach (RealignRow r in rows)
            {
                WallSketchDriftResult after = WallSketchGeometry.Evaluate(doc, r.Wall, r.SketchId, toleranceMm);
                bool ok = after != null && !after.Stranded;
                verification.Add(new JObject
                {
                    ["element_id"] = r.Id, ["verified"] = ok,
                    ["perpendicular_offset_mm_after"] = after == null ? (JToken)JValue.CreateNull() : Math.Round(after.PerpendicularOffsetMm, 1),
                    ["in_plane_offset_mm_after"] = after == null ? (JToken)JValue.CreateNull() : Math.Round(after.InPlaneOffsetMm, 1)
                });
                if (ok) verified++;
            }
            if (rows.Count == 0 || verified != rows.Count)
                return CommandResult.Fail("The transaction group committed, but " + (rows.Count - verified) +
                    " wall(s) failed post-commit verification (the sketch is still stranded by re-measurement). " +
                    verification.ToString(Formatting.None));

            var trResult = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed",
                ["operations_verified"] = verified, ["targets"] = rows.Count, ["rows"] = verification
            };
            ApplicationOutcome.StampApplied(trResult, ApplicationOutcome.Committed, rows.Count, verified, verified, 0, 0, 0);
            trResult["undo"] = new JObject
            {
                ["recorded"] = false,
                ["reason"] = "sketch geometry edits are not covered by horizun_undo; use Revit's own undo within this session."
            };
            return CommandResult.Ok(trResult);
        }

        private static XYZ ToXyzFeet(Vec3 mm) => new XYZ(mm.X / 304.8, mm.Y / 304.8, mm.Z / 304.8);

        /// <summary>
        /// Moves the sketch's own curve elements by vectorFeet, inside a SketchEditScope. commit=false
        /// moves them, lets Revit validate the resulting sketch, then Cancels the WHOLE scope - which
        /// discards the inner transaction's commit too, so nothing survives; commit=true keeps it.
        /// Mirrors Core/IfcProfileUpdate.cs's Start/inner-transaction/Commit(preprocessor)/finally-Cancel
        /// shape exactly, the one place in this codebase already measured against RevitAPI.dll for
        /// 2023-2027 for this exact API surface.
        /// </summary>
        private static bool TryRealign(Document doc, Wall wall, ElementId sketchId, XYZ vectorFeet, bool commit,
                                       out JObject detail, out string error)
        {
            detail = new JObject(); error = null;
            var scope = new SketchEditScope(doc, "Horizun: realign wall sketch");
            try
            {
                if (!scope.IsSketchEditingSupported(sketchId))
                {
                    error = "Revit reports that this sketch cannot be edited (a group, a part, or an element " +
                            "borrowed by somebody else answers this way).";
                    return false;
                }
                scope.Start(sketchId);

                var sketch = doc.GetElement(sketchId) as Sketch;
                if (sketch == null) { error = "the sketch could not be read after the edit scope opened."; return false; }

                var curveIds = new List<ElementId>();
                foreach (ElementId eid in sketch.GetAllElements())
                    if (doc.GetElement(eid) is ModelCurve) curveIds.Add(eid);
                if (curveIds.Count == 0) { error = "the sketch has no curve elements to move."; return false; }

                using (var tx = new Transaction(doc, "Horizun: move sketch curves"))
                {
                    RevitErrorRecorder recorder = RevitErrorRecorder.On(tx);
                    if (tx.Start() != TransactionStatus.Started) { error = "the sketch curve transaction would not start."; return false; }
                    ElementTransformUtils.MoveElements(doc, curveIds, vectorFeet);
                    if (tx.Commit() != TransactionStatus.Committed)
                    { error = "the sketch curve transaction did not commit." + recorder.Said(); return false; }
                    detail["curves_moved"] = curveIds.Count;
                    detail["revit_said"] = recorder.Errors.Count == 0 ? null : string.Join("; ", recorder.Errors);
                }

                // COMMITTING THE SCOPE IS WHERE REVIT VALIDATES THE SKETCH AS A WHOLE - a curve that
                // would leave the profile self-intersecting or strand a hosted element is refused HERE.
                if (commit) scope.Commit(new RevitErrorRecorder());
                else scope.Cancel();
                return true;
            }
            catch (Exception ex)
            {
                error = "Revit refused the realignment: " + ex.Message +
                        " A move that would leave the profile self-intersecting, non-planar, or strand a " +
                        "hosted element is refused at this point, and the wall keeps the profile it had.";
                return false;
            }
            finally
            {
                // IsActive, not IsStarted: see Core/IfcProfileUpdate.cs's own note on this - the
                // documentation for 2023-2027 lists Cancel, Commit and IsActive and no IsStarted at
                // all. A scope that already Committed or was explicitly Cancelled is no longer active,
                // so this only catches the one that threw on the way.
                try { if (scope.IsActive) scope.Cancel(); } catch { }
            }
        }
    }
}
