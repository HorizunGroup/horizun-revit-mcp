// -----------------------------------------------------------------------------
// Horizun Revit MCP - typed, verified 2D detail production.
//
// THE OPERATIONS ARE PRODUCTION WRITES, held to the same bar as annotate,
// transform and write_params:
//
//   * a dry run does not merely validate arguments - it CREATES every element
//     provisionally in one transaction, regenerates, reads the result back, and
//     rolls back, reporting the rollback status Revit actually returned. What a
//     caller approves is a batch Revit has already demonstrated it can build;
//   * the materialised plan binds the view, every resource by identity (line
//     style, filled-region type WITH its IsMasking read from the type, family
//     symbol), the normalized geometry and its deterministic signature
//     (Detail2DRules), the batch order, and what the rehearsal read - a resource
//     or a default swapped between rehearsal and apply is a stale plan;
//   * the apply runs inside a TransactionGroup: create, regenerate, verify every
//     postcondition while the transaction is still reversible, commit, run one
//     materialising regeneration, RE-READ everything from the committed model,
//     and only then assimilate. Any failure rolls the WHOLE batch back and the
//     response carries the real TransactionStatus of every rollback;
//   * verification is per field: requested / read / match, with the comparison
//     tolerance named. Geometry is verified from the AUTHORED facts
//     (GeometryCurve, GetBoundaries, LocationPoint) at every stage; the one fact
//     Revit materialises late - the view bounding box - is declared as such and
//     enforced post-commit, inside the still-open group, where a miss still
//     rolls everything back.
//
// COORDINATE CONVENTION (one convention for every 2D view): a request point
// [x, y] is view-plane coordinates - X along View.RightDirection, Y along
// View.UpDirection, origin at View.Origin - and the model point is
// view.Origin + x*RightDirection + y*UpDirection. A third component must be 0:
// out-of-plane detail is refused, never silently projected.
//
// An operation outside the closed enum refuses as UnsupportedCapability, which
// is what may grant the Python fallback - decided ONCE over the whole batch by
// FallbackDecision, so a mixed batch (one gap, one typo) grants nothing.
// Everything a caller could fix - a non-ViewBased family, a style outside
// GetLineStyleIds, a masking mismatch, an invalid loop - is an ordinary
// argument error on purpose.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class Detail2DCommand : ICommand
    {
        public string Name => "horizun_detail_2d";
        public string Description =>
            "Create detail lines, arcs, polylines, filled and masking regions, place view-based detail components " +
            "and generic annotation symbols, and set line styles - atomically, rehearsed provisionally on the dry " +
            "run and re-read from the committed model on the apply.";

        // ---- the closed operation enum --------------------------------------
        private const string OpLine = "create_detail_line";
        private const string OpArc = "create_detail_arc";
        private const string OpPolyline = "create_detail_polyline";
        private const string OpFilled = "create_filled_region";
        private const string OpMasking = "create_masking_region";
        private const string OpComponent = "place_detail_component";
        private const string OpSymbol = "place_symbol";
        private const string OpSetStyle = "set_line_style";

        private static readonly string[] KnownOperations =
        {
            OpLine, OpArc, OpPolyline, OpFilled, OpMasking, OpComponent, OpSymbol, OpSetStyle
        };

        // ---- limits, from the shared pure rules so there is one source ------
        private const int MaxActions = Detail2DRules.MaxActions;
        private const int MaxPolylinePoints = Detail2DRules.MaxPolylinePoints;
        private const int MaxLoops = Detail2DRules.MaxLoopsPerRegion;
        private const int MaxCurvesPerLoop = Detail2DRules.MaxCurvesPerLoop;

        private const string Convention =
            "view-plane coordinates: X along View.RightDirection, Y along View.UpDirection, origin at " +
            "View.Origin; model_point = Origin + x*RightDirection + y*UpDirection. A third component must be 0 - " +
            "out-of-plane detail is refused, never silently projected.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name); if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            // The rehearsal itself opens a transaction, so a read-only document must be
            // refused in prose HERE rather than as a raw exception out of tx.Start().
            bool docReadOnly; try { docReadOnly = doc.IsReadOnly; } catch { docReadOnly = false; }
            if (docReadOnly)
                return CommandResult.Fail("The active document is READ-ONLY, so no 2D detail can be drawn in it - " +
                    "not even the dry run's provisional rehearsal, which creates and rolls back inside a " +
                    "transaction. Open the document writable and call again. Nothing was changed.");
            JArray actions = request["actions"] as JArray;
            if (actions == null || actions.Count == 0 || actions.Count > MaxActions)
                return CommandResult.Fail("actions must contain 1.." + MaxActions + " entries.");
            double scale;
            if (!DimensionPlanRules.UnitScale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale))
                return CommandResult.Fail("units must be mm, m or feet.");

            var plans = new List<Plan>(); var errors = new JArray();
            var outcomes = new List<ActionOutcome>();
            // Keys declared by earlier actions, in batch order: a set_line_style may only
            // reference a key that already exists when its own action is planned.
            var keys = new Dictionary<string, Plan>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                string error = null, reason = null;
                Plan p = PlanAction(doc, i, actions[i] as JObject, scale, keys, out error, out reason);
                if (p == null)
                {
                    string message = error ?? "entry is not an object";
                    errors.Add(new JObject { ["index"] = i, ["error"] = message });
                    outcomes.Add(new ActionOutcome { Index = i, Error = message, UnsupportedReason = reason });
                }
                else plans.Add(p);
            }
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            string hash = DocumentGate.PlanHash(request, "units", "actions");

            // ---- THE REHEARSAL: provisional creation, measurement, mandatory rollback.
            // Run on BOTH paths whenever every action planned cleanly; never over a batch
            // with invalid entries (rehearsing half a request would open a transaction on
            // a call whose refusal claims none was).
            Rehearsal rehearsal = null;
            if (errors.Count == 0)
            {
                rehearsal = Rehearse(doc, plans);
                if (!rehearsal.RollbackConfirmed)
                {
                    return CommandResult.FailWithDetail(
                        "The rehearsal transaction could not be rolled back: Revit reported '" +
                        rehearsal.RollbackStatus + "', not RolledBack. The model may still carry the provisional " +
                        "detail elements, so the state of this call is UNCERTAIN - no confirmation token is " +
                        "issued and nothing is claimed clean. Re-read the model before anything else.",
                        new JObject
                        {
                            ["state"] = DimensionPlanRules.StateUncertain,
                            ["rehearsal_rollback_status"] = rehearsal.RollbackStatus,
                            ["write_started"] = true,
                            ["rehearsal"] = rehearsal.ToJson()
                        });
                }
            }

            // ---- The MATERIALISED plan. Each row binds the view and every resource by
            // identity, the canonical geometry, and what the rehearsal read - so a model
            // that moves between the approval and the apply refuses as stale instead of
            // being drawn on.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (Plan planned in plans) resolvedPlan.Elements.Add(PlannedRow(planned));

            if (dry)
            {
                bool constructible = rehearsal != null && rehearsal.AllConstructible;
                var result = new JObject
                {
                    ["dry_run"] = true, ["valid"] = plans.Count, ["invalid"] = errors.Count,
                    ["errors"] = errors,
                    ["coordinate_convention"] = Convention,
                    ["plan"] = new JArray(plans.Select(p => PlanRow(p)))
                };
                result["rehearsal"] = rehearsal == null ? (JToken)JValue.CreateNull() : rehearsal.ToJson();
                if (rehearsal == null)
                    result["rehearsal_note"] = "The batch was NOT rehearsed: " + errors.Count + " action(s) are " +
                        "invalid, so no transaction was opened and nothing was provisionally created.";
                if (errors.Count == 0 && constructible) DocumentGate.RecordResolvedPlan(resolvedPlan);
                ApplicationOutcome.StampRehearsal(result, plans.Count + errors.Count, errors.Count,
                                                  rehearsal == null ? 0 : rehearsal.NotConstructibleCount, 0);
                DocumentGate.StampConfirmation(result, gate, Name, hash, errors.Count == 0 && constructible,
                    errors.Count == 0 && constructible
                        ? "the token binds the view, every resource by id AND UniqueId (line styles, region types " +
                          "with their IsMasking as read from the type, family symbols), the normalized view-plane " +
                          "geometry and its deterministic signature, the batch order, and what this rehearsal " +
                          "read. A model that moves before you spend it refuses as a stale plan rather than " +
                          "drawing something else."
                        : errors.Count > 0
                            ? "no usable token while invalid"
                            : "no usable token: the rehearsal could not construct every element - see rehearsal " +
                              "rows for Revit's own reason per action");
                if (!(errors.Count == 0 && constructible))
                    result["confirmation_note"] = errors.Count > 0
                        ? "NO token was issued: " + errors.Count + " action(s) are invalid. Fix them and re-run " +
                          "the dry run; a partial batch is never approvable."
                        : "NO token was issued: the rehearsal could not construct every element against the " +
                          "current model - see the rehearsal rows for Revit's own reason per action.";
                // The rehearsal carries the fallback verdict too: dry_run defaults to true,
                // so this is the first call a caller makes. writeStarted stays false - the
                // provisional transaction only ever runs when EVERY action planned cleanly,
                // so on any path where a fallback could be granted no transaction opened.
                return FallbackDecision.Attach(
                    CommandResult.Ok(result),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (errors.Count > 0)
            {
                // Nothing ran - no transaction was opened - so the decision is only about
                // what failed, and it is made centrally.
                return FallbackDecision.Refuse(
                    "Invalid actions; nothing ran: " + errors.ToString(Formatting.None),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (!rehearsal.AllConstructible)
            {
                return CommandResult.FailWithDetail(
                    "Refused: " + rehearsal.NotConstructibleCount + " of " + plans.Count + " action(s) are not " +
                    "constructible against the current model - see the rehearsal rows for Revit's reason per " +
                    "action. Nothing was committed: the rehearsal transaction rolled back (Revit reported '" +
                    rehearsal.RollbackStatus + "').",
                    new JObject
                    {
                        ["state"] = DimensionPlanRules.StateRefused,
                        ["rehearsal"] = rehearsal.ToJson()
                    });
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash,
                                                                     resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name); if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: detail 2d";
            using (var group = new TransactionGroup(doc, txName))
            {
                group.Start();
                var inTxRows = new JArray(); int inTxVerified = 0;
                using (var tx = new Transaction(doc, txName))
                {
                    tx.Start();
                    var opts = tx.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new SilenceWarnings());
                    opts.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(opts);
                    try
                    {
                        foreach (Plan p in plans) Create(doc, p);
                        doc.Regenerate();
                        // Verify while the transaction is still reversible: a postcondition
                        // that fails here rolls back a write nobody has been told about yet.
                        foreach (Plan p in plans)
                        {
                            bool rowOk; JObject row = VerifyRow(doc, p, VerifyStage.InTransaction, out rowOk);
                            inTxRows.Add(row); if (rowOk) inTxVerified++;
                        }
                        if (inTxVerified != plans.Count)
                        {
                            Guard.RollbackResult rbTx = Guard.RollBack(tx);
                            Guard.RollbackResult rbGroup = Guard.RollBack(group);
                            string state = DimensionPlanRules.FinalState(false,
                                new[] { rbTx.StatusName, rbGroup.StatusName });
                            return CommandResult.FailWithDetail(
                                (plans.Count - inTxVerified) + " of " + plans.Count + " action(s) failed " +
                                "verification BEFORE the commit, so the whole batch was rolled back. " +
                                PlanFailure.SingleTransactionOutcome(true, rbTx.StatusName, "nothing was drawn") +
                                " The TransactionGroup reported '" + rbGroup.StatusName + "'. Each row lists " +
                                "every requested/read comparison.",
                                ApplyDetail(state, rbTx.StatusName, rbGroup.StatusName, inTxRows));
                        }
                        Guard.Commit(tx, txName);
                    }
                    catch (Exception ex)
                    {
                        // Report what the rollback ACTUALLY did. A status other than
                        // RolledBack keeps its uncertainty rather than claiming a clean
                        // model; a transaction Revit already closed contributes its REAL
                        // status to the state decision.
                        bool attempted = false; string rbTxStatus = PlanFailure.NotAttempted;
                        TransactionStatus txNow;
                        try { txNow = tx.GetStatus(); } catch { txNow = TransactionStatus.Uninitialized; }
                        if (txNow == TransactionStatus.Started) { attempted = true; rbTxStatus = Guard.RollBack(tx).StatusName; }
                        else if (txNow != TransactionStatus.Uninitialized) rbTxStatus = txNow.ToString();
                        Guard.RollbackResult rbGrp = Guard.RollBack(group);
                        var statuses = new List<string>();
                        if (attempted || (txNow != TransactionStatus.Uninitialized && txNow != TransactionStatus.Started))
                            statuses.Add(rbTxStatus);
                        statuses.Add(rbGrp.StatusName);
                        string state = DimensionPlanRules.FinalState(false, statuses);
                        return CommandResult.FailWithDetail(
                            "Atomic detail batch failed: " + ex.Message + ". " +
                            PlanFailure.SingleTransactionOutcome(attempted, rbTxStatus, "nothing was drawn") +
                            " The TransactionGroup reported '" + rbGrp.StatusName + "'.",
                            ApplyDetail(state, rbTxStatus, rbGrp.StatusName, inTxRows));
                    }
                }
                // Committed inside the group. The geometry facts were already read in-tx;
                // this regeneration is what asks Revit to compute the late ones (the view
                // bounding boxes) before the post-commit re-read. The group remains open,
                // so a verification that fails after this still rolls EVERYTHING back.
                using (var regen = new Transaction(doc, txName + " (materialise for verification)"))
                {
                    regen.Start();
                    try { doc.Regenerate(); Guard.Commit(regen, txName); }
                    catch
                    {
                        if (regen.GetStatus() == TransactionStatus.Started) Guard.RollBack(regen);
                        // A failed regeneration is not a failed batch: the reads below
                        // decide, and unmaterialised facts fail closed there.
                    }
                }
                var rows = new JArray(); int verified = 0;
                foreach (Plan p in plans)
                {
                    bool rowOk; JObject row = VerifyRow(doc, p, VerifyStage.PostCommit, out rowOk);
                    rows.Add(row); if (rowOk) verified++;
                }
                if (verified != plans.Count)
                {
                    Guard.RollbackResult rbGroup = Guard.RollBack(group);
                    string state = DimensionPlanRules.FinalState(false, new[] { rbGroup.StatusName });
                    return CommandResult.FailWithDetail(
                        "The transaction committed, but " + (plans.Count - verified) + " of " + plans.Count +
                        " action(s) failed the post-commit re-read, so the TransactionGroup was rolled back " +
                        "(Revit reported '" + rbGroup.StatusName + "'). " +
                        (PlanFailure.IsConfirmedRollback(rbGroup.StatusName)
                            ? "Nothing from this call remains in the model."
                            : "DO NOT assume the model is clean - re-read its real state before any retry.") +
                        " Each row lists every requested/read comparison.",
                        ApplyDetail(state, "Committed", rbGroup.StatusName, rows));
                }
                try { Guard.Assimilate(group, txName); }
                catch (SilentRollbackException ex)
                {
                    string rbStatus;
                    try { rbStatus = Guard.RollBack(group).StatusName; }
                    catch (Exception rex) { rbStatus = "RollBack threw: " + rex.Message; }
                    string state = DimensionPlanRules.FinalState(false, new[] { rbStatus });
                    return CommandResult.FailWithDetail(
                        "Every action verified, but the TransactionGroup would not assimilate: " + ex.Message +
                        " A rollback was attempted and Revit reported '" + rbStatus + "'. " +
                        (PlanFailure.IsConfirmedRollback(rbStatus)
                            ? "Nothing from this call remains in the model."
                            : "The state of the model is UNCERTAIN - re-read it before any retry."),
                        ApplyDetail(state, "Committed", rbStatus, rows));
                }
                var applied = new JObject
                {
                    ["transaction_status"] = "Committed",
                    ["transaction_group_status"] = "Committed",
                    ["state"] = DimensionPlanRules.StateCommittedVerified,
                    ["actions_verified"] = verified,
                    ["comparison_tolerance_feet"] = Detail2DRules.CurveToleranceFeet,
                    ["coordinate_convention"] = Convention,
                    ["units_note"] = "Geometry facts in the rows are Revit internal units (decimal feet), in " +
                                     "view-plane coordinates (" + Convention + ").",
                    ["rows"] = rows
                };
                ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed,
                                                plans.Count, verified, verified, 0, 0, 0);
                return CommandResult.Ok(applied);
            }
        }

        // ---------------------------------------------------------------------
        // Planning.
        // ---------------------------------------------------------------------
        private static Plan PlanAction(Document doc, int index, JObject a, double scale,
                                       Dictionary<string, Plan> keys, out string error, out string unsupportedReason)
        {
            error = null; unsupportedReason = null;
            if (a == null) { error = "entry is not an object"; return null; }
            try
            {
                string op = (a.Value<string>("operation") ?? "").ToLowerInvariant();
                // The closed enum first: an operation OUTSIDE it is the structural gap
                // that may grant the Python fallback. Everything a caller could fix by
                // sending different arguments stays an ordinary ArgumentException.
                if (!KnownOperations.Contains(op))
                    throw new UnsupportedCapability(
                        "unsupported operation '" + op + "' - horizun_detail_2d creates detail lines, arcs and " +
                        "polylines, filled and masking regions, places view-based detail components and generic " +
                        "annotation symbols, and sets line styles (" + string.Join(", ", KnownOperations) + ") " +
                        "only. Nothing was written.", FallbackSignal.ReasonUnsupportedOperation);

                // Every field the action offers must MEAN something to its operation:
                // accepting a foreign field would drop it silently, and a request the
                // caller believes was honoured is the quietest wrong answer.
                CheckForeignFields(op, a);

                var p = new Plan { Index = index, Operation = op, Scale = scale };

                p.Key = a.Value<string>("key");
                if (a["key"] != null && string.IsNullOrWhiteSpace(p.Key))
                    throw new ArgumentException("key, when present, must be a non-empty string.");
                if (op == OpSetStyle && a["key"] != null)
                    throw new ArgumentException("set_line_style takes element_key (the key of an earlier action " +
                                                "to restyle), not key - it creates nothing for a key to name.");

                if (op == OpSetStyle) PlanSetStyle(doc, p, a, keys);
                else
                {
                    p.View = NeedView(doc, a);
                    switch (op)
                    {
                        case OpLine: PlanLine(doc, p, a, scale); break;
                        case OpArc: PlanArc(doc, p, a, scale); break;
                        case OpPolyline: PlanPolyline(doc, p, a, scale); break;
                        case OpFilled:
                        case OpMasking: PlanRegion(doc, p, a, scale); break;
                        default: PlanPlacement(doc, p, a, scale); break;
                    }
                }

                if (p.Key != null)
                {
                    Plan already;
                    if (keys.TryGetValue(p.Key, out already))
                        throw new ArgumentException("key '" + p.Key + "' is already declared by action " +
                                                    already.Index + " - keys must be unique across the batch.");
                    keys[p.Key] = p;
                }
                return p;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return null;
            }
        }

        /// <summary>Per-operation field whitelist; a field outside it refuses by name.</summary>
        private static void CheckForeignFields(string op, JObject a)
        {
            string[] allowed;
            switch (op)
            {
                case OpLine: allowed = new[] { "operation", "view_id", "start", "end", "line_style_id", "key" }; break;
                case OpArc:
                    allowed = new[] { "operation", "view_id", "start", "end", "point_on_arc", "center", "radius",
                                      "start_angle_degrees", "end_angle_degrees", "line_style_id", "key" }; break;
                case OpPolyline: allowed = new[] { "operation", "view_id", "points", "closed", "line_style_id", "key" }; break;
                case OpFilled: allowed = new[] { "operation", "view_id", "filled_region_type_id", "loops",
                                                 "allow_masking_type_as_filled", "key" }; break;
                case OpMasking: allowed = new[] { "operation", "view_id", "masking_region_type_id", "loops", "key" }; break;
                case OpComponent:
                case OpSymbol: allowed = new[] { "operation", "view_id", "family_symbol_id", "point",
                                                 "rotation_degrees", "key" }; break;
                default: allowed = new[] { "operation", "view_id", "element_id", "element_key", "line_style_id", "key" }; break;
            }
            var foreign = new List<string>();
            foreach (JProperty prop in a.Properties())
                if (!allowed.Contains(prop.Name)) foreign.Add(prop.Name);
            if (foreign.Count > 0)
                throw new ArgumentException(op + " does not carry " + string.Join(", ", foreign) +
                    " - accepting them here would drop them silently. Remove them, or use the operation they " +
                    "belong to.");
        }

        /// <summary>
        /// The view a creating action draws in. Refused BY CLASS for the view kinds that
        /// have no drawing plane at all, then by ViewType against the closed set this
        /// bridge draws detail in. The view is deliberately NOT required to be active:
        /// detail geometry is verified from authored facts, which do not depend on the
        /// view being displayed.
        /// </summary>
        private static View NeedView(Document doc, JObject a)
        {
            View view = Need<View>(doc, a, "view_id");
            if (view.IsTemplate)
                throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a VIEW TEMPLATE; a template owns " +
                                            "no detail elements. Pass a real view.");
            if (view is ViewSchedule)
                throw new ArgumentException("view_id is a ViewSchedule; a schedule has no drawing plane for 2D detail.");
            if (view is ViewSheet)
                throw new ArgumentException("view_id is a ViewSheet; draw detail in a view placed ON the sheet, " +
                                            "not on the sheet itself.");
            if (view is View3D)
                throw new ArgumentException("view_id is a View3D; 2D detail needs a 2D view.");
            ViewType vt = view.ViewType;
            switch (vt)
            {
                case ViewType.DraftingView:
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.Section:
                case ViewType.Elevation:
                case ViewType.Detail:
                    return view;
                default:
                    throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a " + view.GetType().Name +
                        " with ViewType '" + vt + "', which is outside the set this bridge draws detail in " +
                        "(DraftingView, FloorPlan, CeilingPlan, EngineeringPlan, Section, Elevation, Detail). " +
                        "Nothing was written.");
            }
        }

        private static void PlanLine(Document doc, Plan p, JObject a, double scale)
        {
            ViewPoint s = ParsePoint(p.View, a["start"], scale, "start");
            ViewPoint e = ParsePoint(p.View, a["end"], scale, "end");
            string invalid = Detail2DRules.ValidateSegment(s.ViewFeet, e.ViewFeet);
            if (invalid != null) throw new ArgumentException("start/end: " + invalid);
            p.LineStyle = OptionalLineStyle(doc, a);
            p.ModelCurves.Add(Line.CreateBound(s.Model, e.Model));
            p.RequestedSegments.Add(new[] { s.ViewFeet, e.ViewFeet });
            p.RequestModelPoints.Add(s.Model); p.RequestModelPoints.Add(e.Model);
            p.RequestSignature = Detail2DRules.CanonicalLineSignature(s.ViewFeet, e.ViewFeet);
            if (p.RequestSignature == null)
                throw new ArgumentException("start/end could not be canonically signed; the plan cannot bind this " +
                                            "geometry. Nothing was written.");
        }

        private static void PlanArc(Document doc, Plan p, JObject a, double scale)
        {
            bool byThree = a["start"] != null || a["end"] != null || a["point_on_arc"] != null;
            bool byCenter = a["center"] != null || a["radius"] != null ||
                            a["start_angle_degrees"] != null || a["end_angle_degrees"] != null;
            if (byThree == byCenter)
                throw new ArgumentException("create_detail_arc takes EXACTLY one form: {start, end, point_on_arc} " +
                    "or {center, radius, start_angle_degrees, end_angle_degrees}. " +
                    (byThree ? "Fields of both forms were sent - remove one form entirely."
                             : "Neither form is complete - send one of them entirely."));
            p.LineStyle = OptionalLineStyle(doc, a);
            if (byThree)
            {
                foreach (string f in new[] { "start", "end", "point_on_arc" })
                    if (a[f] == null) throw new ArgumentException("the three-point arc form needs '" + f + "'.");
                ViewPoint s = ParsePoint(p.View, a["start"], scale, "start");
                ViewPoint e = ParsePoint(p.View, a["end"], scale, "end");
                ViewPoint on = ParsePoint(p.View, a["point_on_arc"], scale, "point_on_arc");
                double[] center; double radius;
                string invalid = Detail2DRules.ValidateArcByThreePoints(s.ViewFeet, e.ViewFeet, on.ViewFeet,
                                                                        out center, out radius);
                if (invalid != null) throw new ArgumentException("start/end/point_on_arc: " + invalid);
                p.ArcViewCenter = center; p.ArcRadiusFeet = radius;
                p.ArcViewStart = s.ViewFeet; p.ArcViewEnd = e.ViewFeet;
                p.ModelCurves.Add(Arc.Create(s.Model, e.Model, on.Model));
                p.RequestModelPoints.Add(s.Model); p.RequestModelPoints.Add(e.Model); p.RequestModelPoints.Add(on.Model);
            }
            else
            {
                foreach (string f in new[] { "center", "radius", "start_angle_degrees", "end_angle_degrees" })
                    if (a[f] == null) throw new ArgumentException("the center arc form needs '" + f + "'.");
                ViewPoint c = ParsePoint(p.View, a["center"], scale, "center");
                double radius = (a.Value<double?>("radius") ?? 0) * scale;
                if (radius <= Detail2DRules.CurveToleranceFeet)
                    throw new ArgumentException("radius must be greater than zero (a zero-radius arc is a " +
                                                "degenerate curve).");
                double a0 = DimensionPlanRules.DegreesToRadians(a.Value<double>("start_angle_degrees"));
                double a1 = DimensionPlanRules.DegreesToRadians(a.Value<double>("end_angle_degrees"));
                double sweep = a1 - a0;
                if (sweep <= 0 || sweep >= 2 * Math.PI)
                    throw new ArgumentException("start_angle_degrees/end_angle_degrees must describe a positive " +
                        "sweep smaller than a full circle (0 < end - start < 360). A full circle is not a bound " +
                        "detail arc; split it into two arcs.");
                if (sweep * radius <= Detail2DRules.CurveToleranceFeet)
                    throw new ArgumentException("the arc is degenerate: its length (radius x sweep) is below the " +
                                                "comparison tolerance.");
                double[] sV = { c.ViewFeet[0] + radius * Math.Cos(a0), c.ViewFeet[1] + radius * Math.Sin(a0), 0 };
                double[] eV = { c.ViewFeet[0] + radius * Math.Cos(a1), c.ViewFeet[1] + radius * Math.Sin(a1), 0 };
                p.ArcViewCenter = c.ViewFeet; p.ArcRadiusFeet = radius;
                p.ArcViewStart = sV; p.ArcViewEnd = eV;
                p.ModelCurves.Add(Arc.Create(c.Model, radius, a0, a1, p.View.RightDirection, p.View.UpDirection));
                p.RequestModelPoints.Add(ModelOf(p.View, sV)); p.RequestModelPoints.Add(ModelOf(p.View, eV));
            }
            p.RequestSignature = Detail2DRules.CanonicalArcSignature(p.ArcViewCenter, p.ArcRadiusFeet,
                                                                     p.ArcViewStart, p.ArcViewEnd);
            if (p.RequestSignature == null)
                throw new ArgumentException("the arc could not be canonically signed; the plan cannot bind this " +
                                            "geometry. Nothing was written.");
        }

        private static void PlanPolyline(Document doc, Plan p, JObject a, double scale)
        {
            JArray points = a["points"] as JArray;
            if (points == null || points.Count < 2 || points.Count > MaxPolylinePoints)
                throw new ArgumentException("points must contain 2.." + MaxPolylinePoints + " vertices.");
            p.ClosedPolyline = a.Value<bool?>("closed") ?? false;
            var view = new List<double[]>(); var model = new List<XYZ>();
            for (int i = 0; i < points.Count; i++)
            {
                ViewPoint v = ParsePoint(p.View, points[i], scale, "points[" + i + "]");
                view.Add(v.ViewFeet); model.Add(v.Model);
            }
            string invalid = Detail2DRules.ValidatePolyline(view, p.ClosedPolyline);
            if (invalid != null) throw new ArgumentException("points: " + invalid);
            p.LineStyle = OptionalLineStyle(doc, a);
            var segSigs = new List<string>();
            int segments = p.ClosedPolyline ? view.Count : view.Count - 1;
            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % view.Count;
                p.ModelCurves.Add(Line.CreateBound(model[i], model[j]));
                p.RequestedSegments.Add(new[] { view[i], view[j] });
                string sig = Detail2DRules.CanonicalLineSignature(view[i], view[j]);
                if (sig == null)
                    throw new ArgumentException("points: segment " + i + " could not be canonically signed; the " +
                                                "plan cannot bind this geometry. Nothing was written.");
                segSigs.Add(sig);
            }
            foreach (XYZ m in model) p.RequestModelPoints.Add(m);
            p.RequestSignature = p.ClosedPolyline
                ? Detail2DRules.LoopSignature(segSigs)
                : OpenPathSignature(segSigs);
            if (p.RequestSignature == null)
                throw new ArgumentException("points could not be canonically signed as a set; the plan cannot " +
                                            "bind this geometry. Nothing was written.");
        }

        private static void PlanRegion(Document doc, Plan p, JObject a, double scale)
        {
            bool masking = p.Operation == OpMasking;
            string typeField = masking ? "masking_region_type_id" : "filled_region_type_id";
            FilledRegionType type = Need<FilledRegionType>(doc, a, typeField);
            if (!FilledRegion.IsValidFilledRegionTypeId(doc, type.Id))
                throw new ArgumentException(typeField + " " + Rid.Value(type.Id) + " is not a valid filled region " +
                                            "type in this document (FilledRegion.IsValidFilledRegionTypeId).");
            bool typeIsMasking;
            try { typeIsMasking = type.IsMasking; }
            catch (Exception ex)
            {
                throw new ArgumentException(typeField + ": IsMasking could not be read from the type (" + ex.Message +
                    "), so the masking/filled distinction cannot be verified. Nothing was written.");
            }
            if (masking && !typeIsMasking)
                throw new ArgumentException("masking_region_type_id resolves to '" + SafeName(type) + "', whose " +
                    "IsMasking is FALSE - read from the type, not from its name. It would draw as an ordinary " +
                    "filled region, not mask anything. Use create_filled_region for it, or pick a type whose " +
                    "IsMasking is true (horizun_query_detail_2d mode=resources lists them). Nothing was written.");
            if (!masking && typeIsMasking && a.Value<bool?>("allow_masking_type_as_filled") != true)
                throw new ArgumentException("filled_region_type_id resolves to '" + SafeName(type) + "', whose " +
                    "IsMasking is TRUE - a masking type drawn as an ordinary filled region hides the model " +
                    "graphics behind it. Use create_masking_region, or pass allow_masking_type_as_filled=true " +
                    "to do it deliberately. Nothing was written.");
            p.RegionType = type; p.RegionTypeIsMasking = typeIsMasking;

            JArray loops = a["loops"] as JArray;
            if (loops == null || loops.Count < 1 || loops.Count > MaxLoops)
                throw new ArgumentException("loops must contain 1.." + MaxLoops + " loops, each an array of " +
                                            "[x, y] vertices (" + Convention + ").");
            var loopsView = new List<IReadOnlyList<double[]>>();
            var loopsModel = new List<List<XYZ>>();
            for (int li = 0; li < loops.Count; li++)
            {
                JArray loop = loops[li] as JArray;
                if (loop == null || loop.Count < 3 || loop.Count > MaxCurvesPerLoop)
                    throw new ArgumentException("loops[" + li + "] must be an array of 3.." + MaxCurvesPerLoop +
                                                " vertices (one straight boundary curve per vertex).");
                var vView = new List<double[]>(); var vModel = new List<XYZ>();
                for (int vi = 0; vi < loop.Count; vi++)
                {
                    ViewPoint v = ParsePoint(p.View, loop[vi], scale, "loops[" + li + "][" + vi + "]");
                    vView.Add(v.ViewFeet); vModel.Add(v.Model);
                }
                string loopInvalid = Detail2DRules.ValidateLoop(vView);
                if (loopInvalid != null) throw new ArgumentException("loops[" + li + "]: " + loopInvalid);
                loopsView.Add(vView); loopsModel.Add(vModel);
            }
            int outerIndex;
            string structural = Detail2DRules.ValidateRegionLoops(loopsView, out outerIndex);
            if (structural != null) throw new ArgumentException("loops: " + structural);
            p.OuterLoopIndex = outerIndex;
            p.LoopViewPoints = loopsView;

            var loopSigs = new List<string>();
            for (int li = 0; li < loopsView.Count; li++)
            {
                IReadOnlyList<double[]> v = loopsView[li];
                var segSigs = new List<string>();
                var curveLoop = new CurveLoop();
                for (int vi = 0; vi < v.Count; vi++)
                {
                    int vj = (vi + 1) % v.Count;
                    string sig = Detail2DRules.CanonicalLineSignature(v[vi], v[vj]);
                    if (sig == null)
                        throw new ArgumentException("loops[" + li + "]: segment " + vi + " could not be " +
                                                    "canonically signed. Nothing was written.");
                    segSigs.Add(sig);
                    curveLoop.Append(Line.CreateBound(loopsModel[li][vi], loopsModel[li][vj]));
                    p.RequestModelPoints.Add(loopsModel[li][vi]);
                }
                string loopSig = Detail2DRules.LoopSignature(segSigs);
                if (loopSig == null)
                    throw new ArgumentException("loops[" + li + "] could not be canonically signed as a loop. " +
                                                "Nothing was written.");
                loopSigs.Add(loopSig);
                p.ModelLoops.Add(curveLoop);
            }
            var holes = new List<string>();
            for (int i = 0; i < loopSigs.Count; i++) if (i != outerIndex) holes.Add(loopSigs[i]);
            p.RequestSignature = Detail2DRules.RegionSignature(loopSigs[outerIndex], holes);
            if (p.RequestSignature == null)
                throw new ArgumentException("the region could not be canonically signed; the plan cannot bind " +
                                            "this geometry. Nothing was written.");
        }

        private static void PlanPlacement(Document doc, Plan p, JObject a, double scale)
        {
            FamilySymbol symbol = Need<FamilySymbol>(doc, a, "family_symbol_id");
            Family family; try { family = symbol.Family; } catch { family = null; }
            if (family == null)
                throw new ArgumentException("family_symbol_id " + Rid.Value(symbol.Id) + ": the symbol's Family " +
                                            "could not be read, so its placement type cannot be verified. Nothing " +
                                            "was written.");
            FamilyPlacementType placement;
            try { placement = family.FamilyPlacementType; }
            catch (Exception ex)
            {
                throw new ArgumentException("family_symbol_id " + Rid.Value(symbol.Id) + ": FamilyPlacementType " +
                                            "could not be read (" + ex.Message + "). Nothing was written.");
            }
            if (placement != FamilyPlacementType.ViewBased)
                throw new ArgumentException("family_symbol_id resolves to '" + SafeName(family) + " : " +
                    SafeName(symbol) + "', whose FamilyPlacementType is '" + placement + "', not ViewBased - a " +
                    "model, level or face based family cannot be placed as 2D detail in a view. Pick a view-based " +
                    "detail item or generic annotation (horizun_query_detail_2d mode=resources lists them). " +
                    "Nothing was written.");
            long catId = symbol.Category == null ? 0 : Rid.Value(symbol.Category.Id);
            long wanted = p.Operation == OpComponent
                ? (int)BuiltInCategory.OST_DetailComponents
                : (int)BuiltInCategory.OST_GenericAnnotation;
            if (catId != wanted)
            {
                string catName; try { catName = symbol.Category?.Name ?? "(none)"; } catch { catName = "(unreadable)"; }
                long other = p.Operation == OpComponent
                    ? (int)BuiltInCategory.OST_GenericAnnotation
                    : (int)BuiltInCategory.OST_DetailComponents;
                throw new ArgumentException("family_symbol_id resolves to '" + SafeName(symbol) + "' in category '" +
                    catName + "', but " + p.Operation + " places " +
                    (p.Operation == OpComponent ? "Detail Items (OST_DetailComponents)" :
                                                  "Generic Annotations (OST_GenericAnnotation)") + " only." +
                    (catId == other
                        ? " Use " + (p.Operation == OpComponent ? OpSymbol : OpComponent) + " for this symbol."
                        : "") + " Nothing was written.");
            }
            p.Symbol = symbol; p.SymbolCategoryId = catId;
            ViewPoint point = ParsePoint(p.View, a["point"], scale, "point");
            p.PlacementViewPoint = point.ViewFeet;
            p.ModelPoint = point.Model;
            if (a["rotation_degrees"] != null)
            {
                double deg = a.Value<double>("rotation_degrees");
                double rad = DimensionPlanRules.DegreesToRadians(deg) % (2 * Math.PI);
                if (rad < 0) rad += 2 * Math.PI;
                p.RotationRadians = rad;
            }
        }

        private static void PlanSetStyle(Document doc, Plan p, JObject a, Dictionary<string, Plan> keys)
        {
            bool byId = a["element_id"] != null, byKey = a["element_key"] != null;
            if (byId == byKey)
                throw new ArgumentException("set_line_style takes EXACTLY one of element_id (an existing curve " +
                                            "element) or element_key (the key of an earlier create action in this " +
                                            "batch).");
            p.NewStyle = Need<GraphicsStyle>(doc, a, "line_style_id");
            if (byId)
            {
                Element target = Need<Element>(doc, a, "element_id");
                var ce = target as CurveElement;
                if (ce == null)
                    throw new ArgumentException("element_id " + Rid.Value(target.Id) + " is a " +
                        target.GetType().Name + ", not a CurveElement - set_line_style acts on detail and model " +
                        "curves only.");
                ICollection<ElementId> valid;
                try { valid = ce.GetLineStyleIds(); }
                catch (Exception ex)
                {
                    throw new ArgumentException("element_id " + Rid.Value(ce.Id) + ": GetLineStyleIds could not be " +
                                                "read (" + ex.Message + "), so line_style_id cannot be validated. " +
                                                "Nothing was written.");
                }
                if (valid == null || !valid.Contains(p.NewStyle.Id))
                    throw new ArgumentException("line_style_id " + Rid.Value(p.NewStyle.Id) + " ('" +
                        SafeName(p.NewStyle) + "') is not in element " + Rid.Value(ce.Id) + "'s valid set " +
                        "(CurveElement.GetLineStyleIds). Valid: " + DescribeStyles(doc, valid) + ".");
                p.TargetExisting = ce;
                try { p.TargetUid = ce.UniqueId; } catch { p.TargetUid = "<unreadable>"; }
                try
                {
                    ElementId owner = ce.OwnerViewId;
                    p.ExpectedOwnerViewId = owner == null || owner == ElementId.InvalidElementId
                        ? (long?)null : Rid.Value(owner);
                }
                catch { p.ExpectedOwnerViewId = null; }
                Element before;
                try { before = ce.LineStyle; }
                catch (Exception ex)
                {
                    throw new ArgumentException("element_id " + Rid.Value(ce.Id) + ": the current LineStyle could " +
                                                "not be read (" + ex.Message + "), so the plan cannot bind the " +
                                                "before-value. Nothing was written.");
                }
                p.BeforeStyleId = before == null ? (long?)null : Rid.Value(before.Id);
                p.BeforeStyleName = before == null ? null : SafeName(before);
                if (a["view_id"] != null)
                {
                    long wanted = a.Value<long>("view_id");
                    if (p.ExpectedOwnerViewId == null || p.ExpectedOwnerViewId.Value != wanted)
                        throw new ArgumentException("view_id " + wanted + " does not match element " +
                            Rid.Value(ce.Id) + "'s owner view (" +
                            (p.ExpectedOwnerViewId == null ? "none" :
                             p.ExpectedOwnerViewId.Value.ToString(CultureInfo.InvariantCulture)) +
                            "). view_id is optional on set_line_style; when present it must agree.");
                }
            }
            else
            {
                string key = a.Value<string>("element_key");
                if (string.IsNullOrWhiteSpace(key))
                    throw new ArgumentException("element_key must be a non-empty string.");
                Plan producer;
                if (!keys.TryGetValue(key, out producer))
                    throw new ArgumentException("element_key '" + key + "' does not name an EARLIER action of this " +
                        "batch - keys are declared by earlier create actions, and order matters. Declare the key " +
                        "on the creating action, place it before this one, and make sure that action is valid.");
                if (producer.Operation != OpLine && producer.Operation != OpArc && producer.Operation != OpPolyline)
                    throw new ArgumentException("element_key '" + key + "' names action " + producer.Index + " (" +
                        producer.Operation + "), which creates no CurveElement - set_line_style restyles detail " +
                        "lines, arcs and polylines only.");
                p.TargetKey = key; p.TargetPlan = producer;
                p.ExpectedOwnerViewId = Rid.Value(producer.View.Id);
                if (a["view_id"] != null)
                {
                    long wanted = a.Value<long>("view_id");
                    if (wanted != p.ExpectedOwnerViewId.Value)
                        throw new ArgumentException("view_id " + wanted + " does not match the view of action " +
                            producer.Index + " (" + p.ExpectedOwnerViewId.Value + "), which element_key '" + key +
                            "' names. view_id is optional on set_line_style; when present it must agree.");
                }
                // The per-element GetLineStyleIds membership cannot be checked before the
                // element exists; the rehearsal creates it provisionally and checks there.
            }
        }

        private static GraphicsStyle OptionalLineStyle(Document doc, JObject a)
        {
            if (a["line_style_id"] == null) return null;
            return Need<GraphicsStyle>(doc, a, "line_style_id");
        }

        // ---------------------------------------------------------------------
        // Creation. Runs inside the rehearsal transaction and again inside the
        // apply transaction; everything it makes lands in p.Created / p.Applied.
        // ---------------------------------------------------------------------
        private static void Create(Document doc, Plan p)
        {
            switch (p.Operation)
            {
                case OpLine:
                case OpArc:
                case OpPolyline:
                    foreach (Curve c in p.ModelCurves)
                    {
                        DetailCurve dc = doc.Create.NewDetailCurve(p.View, c);
                        if (dc == null) throw new InvalidOperationException("Revit returned no detail curve.");
                        if (p.LineStyle != null) dc.LineStyle = p.LineStyle;
                        p.Created.Add(dc.Id);
                    }
                    break;
                case OpFilled:
                case OpMasking:
                {
                    FilledRegion fr = FilledRegion.Create(doc, p.RegionType.Id, p.View.Id, p.ModelLoops);
                    if (fr == null) throw new InvalidOperationException("Revit returned no filled region.");
                    p.Created.Add(fr.Id);
                    break;
                }
                case OpComponent:
                case OpSymbol:
                {
                    // Activation is a write and belongs INSIDE the transaction.
                    if (!p.Symbol.IsActive) p.Symbol.Activate();
                    FamilyInstance fi = doc.Create.NewFamilyInstance(p.ModelPoint, p.Symbol, p.View);
                    if (fi == null) throw new InvalidOperationException("Revit returned no family instance.");
                    p.Created.Add(fi.Id);
                    if (p.RotationRadians.HasValue && Math.Abs(p.RotationRadians.Value) > 1e-12)
                        ElementTransformUtils.RotateElement(doc, fi.Id,
                            Line.CreateBound(p.ModelPoint, p.ModelPoint.Add(p.View.ViewDirection)),
                            p.RotationRadians.Value);
                    break;
                }
                default:   // set_line_style
                {
                    List<ElementId> targets = p.TargetExisting != null
                        ? new List<ElementId> { p.TargetExisting.Id }
                        : new List<ElementId>(p.TargetPlan.Created);
                    if (targets.Count == 0)
                        throw new InvalidOperationException("element_key '" + p.TargetKey + "': the elements of " +
                            "action " + (p.TargetPlan == null ? -1 : p.TargetPlan.Index) + " do not exist in this " +
                            "transaction.");
                    foreach (ElementId id in targets)
                    {
                        var ce = doc.GetElement(id) as CurveElement;
                        if (ce == null)
                            throw new InvalidOperationException("set_line_style target " + Rid.Value(id) +
                                                                " is not a CurveElement.");
                        ICollection<ElementId> valid = ce.GetLineStyleIds();
                        if (valid == null || !valid.Contains(p.NewStyle.Id))
                            throw new ArgumentException("line_style_id " + Rid.Value(p.NewStyle.Id) + " ('" +
                                SafeName(p.NewStyle) + "') is not in the valid set of element " + Rid.Value(id) +
                                " (CurveElement.GetLineStyleIds). Valid: " + DescribeStyles(doc, valid) + ".");
                        ce.LineStyle = p.NewStyle;
                    }
                    p.Applied = targets;
                    break;
                }
            }
        }

        // ---------------------------------------------------------------------
        // The rehearsal: provisional creation, measurement, MANDATORY rollback.
        // ---------------------------------------------------------------------
        private Rehearsal Rehearse(Document doc, List<Plan> plans)
        {
            var re = new Rehearsal();
            int failedIndex = -1; string failedWhy = null, regenWhy = null;
            var attempted = new HashSet<int>();
            using (var tx = new Transaction(doc, "Horizun: rehearse detail 2d"))
            {
                tx.Start();
                var opts = tx.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new SilenceWarnings());
                opts.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(opts);

                foreach (Plan p in plans)
                {
                    attempted.Add(p.Index);
                    try { Create(doc, p); }
                    catch (Exception ex) { failedIndex = p.Index; failedWhy = ex.Message; break; }
                }
                if (failedIndex < 0)
                {
                    try { doc.Regenerate(); } catch (Exception ex) { regenWhy = ex.Message; }
                }
                // Measure INSIDE the transaction - the mandatory rollback below erases
                // the evidence a moment later. Element ids from here are never reported:
                // they do not survive the rollback.
                foreach (Plan p in plans)
                {
                    JObject row;
                    bool constructible = false;
                    if (failedIndex >= 0 || regenWhy != null)
                    {
                        string reason = p.Index == failedIndex
                            ? failedWhy
                            : regenWhy != null && attempted.Contains(p.Index)
                                ? "the rehearsal could not regenerate after creating the batch: " + regenWhy
                                : attempted.Contains(p.Index)
                                    ? "created, then action " + failedIndex + " failed before the batch could be " +
                                      "measured; the shared rehearsal transaction was rolled back"
                                    : "not attempted: action " + failedIndex + " failed first and the shared " +
                                      "rehearsal transaction was rolled back";
                        row = new JObject
                        {
                            ["index"] = p.Index, ["operation"] = p.Operation,
                            ["constructible"] = p.Index == failedIndex || regenWhy != null
                                ? (JToken)false
                                : JValue.CreateNull(),
                            ["reason"] = reason
                        };
                    }
                    else
                    {
                        Measured m = Measure(doc, p);
                        p.Rehearsed = m;
                        bool rowOk; JObject verification = BuildChecks(p, m, m, VerifyStage.Rehearsal, out rowOk);
                        constructible = rowOk;
                        row = new JObject
                        {
                            ["index"] = p.Index, ["operation"] = p.Operation,
                            ["constructible"] = constructible,
                            ["reason"] = constructible
                                ? null
                                : (m.Unreadable ?? "one or more rehearsal checks did not match - see verification"),
                            ["evidence"] = Evidence(m),
                            ["verification"] = verification
                        };
                    }
                    if (!(row["constructible"] is JValue jc && jc.Type == JTokenType.Boolean && (bool)jc.Value))
                        re.NotConstructibleCount++;
                    re.Rows.Add(row);
                }

                Guard.RollbackResult rb = Guard.RollBack(tx);
                re.RollbackStatus = rb.StatusName;
                re.RollbackConfirmed = rb.Confirmed;
            }
            // Ids from a rolled-back transaction must never leak into a later phase.
            foreach (Plan p in plans) { p.Created.Clear(); p.Applied = null; }
            re.AllConstructible = re.NotConstructibleCount == 0;
            return re;
        }

        // ---------------------------------------------------------------------
        // Measurement: every fact the verification compares, read guarded.
        // ---------------------------------------------------------------------
        private static Measured Measure(Document doc, Plan p)
        {
            var m = new Measured();
            List<ElementId> ids = p.Operation == OpSetStyle
                ? (p.Applied ?? new List<ElementId>())
                : p.Created;
            foreach (ElementId id in ids)
            {
                var em = new ElementFacts();
                m.Elements.Add(em);
                Element e = null;
                try { e = doc.GetElement(id); } catch (Exception ex) { Note(m, "GetElement: " + ex.Message); }
                if (e == null) { Note(m, "element " + Rid.Value(id) + " could not be re-read"); continue; }
                em.Id = Rid.Value(e.Id);
                em.ClassName = e.GetType().Name;
                try { em.Uid = e.UniqueId; } catch (Exception ex) { Note(m, "UniqueId: " + ex.Message); }
                try
                {
                    ElementId owner = e.OwnerViewId;
                    em.OwnerViewId = owner == null || owner == ElementId.InvalidElementId
                        ? (long?)null : Rid.Value(owner);
                }
                catch (Exception ex) { Note(m, "OwnerViewId: " + ex.Message); }

                View frame = p.View ?? (p.TargetPlan != null ? p.TargetPlan.View : null);

                var ce = e as CurveElement;
                if (ce != null)
                {
                    em.IsCurveElement = true;
                    em.IsDetailCurve = e is DetailCurve;
                    try
                    {
                        Element style = ce.LineStyle;
                        em.StyleId = style == null ? (long?)null : Rid.Value(style.Id);
                        em.StyleName = style == null ? null : SafeName(style);
                    }
                    catch (Exception ex) { Note(m, "LineStyle: " + ex.Message); }
                    try
                    {
                        Curve c = ce.GeometryCurve;
                        if (c is Line line)
                        {
                            em.CurveKind = "line";
                            if (frame != null)
                            {
                                em.VStart = ViewFrame(frame, line.GetEndPoint(0));
                                em.VEnd = ViewFrame(frame, line.GetEndPoint(1));
                                em.Signature = Detail2DRules.CanonicalLineSignature(em.VStart, em.VEnd);
                            }
                        }
                        else if (c is Arc arc)
                        {
                            em.CurveKind = "arc";
                            if (frame != null)
                            {
                                em.VCenter = ViewFrame(frame, arc.Center);
                                em.RadiusFeet = arc.Radius;
                                em.VStart = ViewFrame(frame, arc.GetEndPoint(0));
                                em.VEnd = ViewFrame(frame, arc.GetEndPoint(1));
                                em.Signature = Detail2DRules.CanonicalArcSignature(em.VCenter, arc.Radius,
                                                                                   em.VStart, em.VEnd);
                            }
                        }
                        else em.CurveKind = c == null ? null : c.GetType().Name.ToLowerInvariant();
                    }
                    catch (Exception ex) { Note(m, "GeometryCurve: " + ex.Message); }
                }
                var fr = e as FilledRegion;
                if (fr != null)
                {
                    em.IsFilledRegion = true;
                    try { em.TypeId = Rid.Value(fr.GetTypeId()); } catch (Exception ex) { Note(m, "GetTypeId: " + ex.Message); }
                    if (em.TypeId.HasValue)
                    {
                        try
                        {
                            var t = doc.GetElement(Rid.Make(em.TypeId.Value)) as FilledRegionType;
                            if (t == null) Note(m, "the region's type could not be re-read");
                            else em.TypeIsMasking = t.IsMasking;
                        }
                        catch (Exception ex) { Note(m, "IsMasking: " + ex.Message); }
                    }
                    try
                    {
                        IList<CurveLoop> loops = fr.GetBoundaries();
                        if (loops == null) Note(m, "GetBoundaries returned null");
                        else
                        {
                            em.LoopCount = loops.Count;
                            em.CurvesPerLoop = new List<int>();
                            em.Vertices = new List<double[]>();
                            var loopVertexLists = new List<IReadOnlyList<double[]>>();
                            var loopSigs = new List<string>();
                            bool signable = frame != null;
                            foreach (CurveLoop loop in loops)
                            {
                                int curves = 0;
                                var vertices = new List<double[]>();
                                var segSigs = new List<string>();
                                foreach (Curve c in loop)
                                {
                                    curves++;
                                    if (frame == null) continue;
                                    double[] va = ViewFrame(frame, c.GetEndPoint(0));
                                    double[] vb = ViewFrame(frame, c.GetEndPoint(1));
                                    vertices.Add(va);
                                    em.Vertices.Add(va);
                                    string sig = c is Line
                                        ? Detail2DRules.CanonicalLineSignature(va, vb)
                                        : c is Arc carc
                                            ? Detail2DRules.CanonicalArcSignature(ViewFrame(frame, carc.Center),
                                                                                  carc.Radius, va, vb)
                                            : null;
                                    if (sig == null) signable = false; else segSigs.Add(sig);
                                }
                                em.CurvesPerLoop.Add(curves);
                                loopVertexLists.Add(vertices);
                                if (signable)
                                {
                                    string loopSig = Detail2DRules.LoopSignature(segSigs);
                                    if (loopSig == null) signable = false; else loopSigs.Add(loopSig);
                                }
                            }
                            if (signable && loopSigs.Count == loopVertexLists.Count)
                            {
                                int outer;
                                string structural = Detail2DRules.ValidateRegionLoops(loopVertexLists, out outer);
                                if (structural == null && outer >= 0 && outer < loopSigs.Count)
                                {
                                    var holes = new List<string>();
                                    for (int i = 0; i < loopSigs.Count; i++) if (i != outer) holes.Add(loopSigs[i]);
                                    em.RegionSignature = Detail2DRules.RegionSignature(loopSigs[outer], holes);
                                }
                                else Note(m, "the read boundaries did not classify into one outer loop plus holes" +
                                             (structural == null ? "" : ": " + structural));
                            }
                        }
                    }
                    catch (Exception ex) { Note(m, "GetBoundaries: " + ex.Message); }
                }
                var fi = e as FamilyInstance;
                if (fi != null)
                {
                    em.IsFamilyInstance = true;
                    try { em.SymbolId = Rid.Value(fi.GetTypeId()); } catch (Exception ex) { Note(m, "GetTypeId: " + ex.Message); }
                    try { em.CategoryId = fi.Category == null ? (long?)null : Rid.Value(fi.Category.Id); }
                    catch (Exception ex) { Note(m, "Category: " + ex.Message); }
                    try
                    {
                        var lp = fi.Location as LocationPoint;
                        if (lp == null) Note(m, "Location is not a LocationPoint");
                        else
                        {
                            XYZ pt = lp.Point;
                            em.LocModel = new[] { pt.X, pt.Y, pt.Z };
                            // MEASURED live (2025, 2026-08-24): LocationPoint.Rotation THROWS
                            // on a generic-annotation instance ("Unable to extract rotation
                            // from family instance"). When the caller asked for a rotation,
                            // an unreadable read-back is a real verification failure; when
                            // they did not, it is a fact about the class, not about this
                            // request, and it must not poison the row.
                            try { em.RotationRadians = lp.Rotation; }
                            catch (Exception ex)
                            {
                                if (p.RotationRadians.HasValue) Note(m, "Rotation: " + ex.Message);
                            }
                        }
                    }
                    catch (Exception ex) { Note(m, "Location: " + ex.Message); }
                    try
                    {
                        XYZ origin = fi.GetTransform().Origin;
                        em.TransformOriginModel = new[] { origin.X, origin.Y, origin.Z };
                    }
                    catch (Exception ex) { Note(m, "GetTransform: " + ex.Message); }
                }

                // The view bounding box, observed at every stage and ENFORCED post-commit.
                try
                {
                    View boxView = frame;
                    BoundingBoxXYZ bb = boxView == null ? null : e.get_BoundingBox(boxView);
                    if (bb != null)
                    {
                        em.BboxRead = true;
                        Transform t = bb.Transform;
                        double[] min = null, max = null;
                        for (int i = 0; i < 8; i++)
                        {
                            var corner = new XYZ((i & 1) == 0 ? bb.Min.X : bb.Max.X,
                                                 (i & 2) == 0 ? bb.Min.Y : bb.Max.Y,
                                                 (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                            XYZ world = t == null ? corner : t.OfPoint(corner);
                            if (min == null)
                            {
                                min = new[] { world.X, world.Y, world.Z };
                                max = new[] { world.X, world.Y, world.Z };
                            }
                            else
                            {
                                min[0] = Math.Min(min[0], world.X); min[1] = Math.Min(min[1], world.Y); min[2] = Math.Min(min[2], world.Z);
                                max[0] = Math.Max(max[0], world.X); max[1] = Math.Max(max[1], world.Y); max[2] = Math.Max(max[2], world.Z);
                            }
                        }
                        em.BboxMinModel = min; em.BboxMaxModel = max;
                    }
                }
                catch { em.BboxRead = false; /* enforced (or not) by stage in BuildChecks */ }
            }

            // The set signature of a multi-curve action, from the read elements in
            // creation order - deterministic for the same model, so read-vs-rehearsed
            // holds it without re-quantization risk.
            if (p.Operation == OpPolyline && m.Elements.All(x => x.Signature != null) && m.Elements.Count > 0)
            {
                var sigs = m.Elements.Select(x => x.Signature).ToList();
                m.SetSignature = p.ClosedPolyline
                    ? Detail2DRules.LoopSignature(sigs)
                    : OpenPathSignature(sigs);
            }
            return m;
        }

        private static void Note(Measured m, string what) { if (m.Unreadable == null) m.Unreadable = what; }

        // ---------------------------------------------------------------------
        // Verification: requested / read / match per field, staged.
        //
        // Geometry and identity are demanded at EVERY stage - detail geometry is
        // authored, not computed, and reads inside an open transaction. The view
        // bounding box is the fact Revit materialises late: observed at every
        // stage, ENFORCED post-commit, after the materialising regeneration and
        // inside the still-open TransactionGroup where a miss still rolls the
        // whole batch back.
        // ---------------------------------------------------------------------
        private enum VerifyStage { Rehearsal, InTransaction, PostCommit }

        private static JObject BuildChecks(Plan p, Measured m, Measured baseline, VerifyStage stage, out bool allOk)
        {
            var checks = new JArray();
            bool ok = true;
            Action<string, JToken, JToken, bool> add = (field, requested, read, match) =>
            {
                checks.Add(new JObject
                {
                    ["field"] = field,
                    ["requested"] = requested ?? JValue.CreateNull(),
                    ["read"] = read ?? JValue.CreateNull(),
                    ["match"] = match
                });
                if (!match) ok = false;
            };
            double tol = Detail2DRules.CurveToleranceFeet;
            string op = p.Operation;

            int expected = ExpectedElementCount(p);
            add("elements_count", expected, m.Elements.Count, m.Elements.Count == expected);

            // Class, by HIERARCHY - Revit is free to answer with a subclass
            // (NewDetailCurve over a Line returns a DetailLine, which IS a DetailCurve).
            string expectedClass; Func<ElementFacts, bool> classOk;
            if (op == OpFilled || op == OpMasking) { expectedClass = "FilledRegion"; classOk = e => e.IsFilledRegion; }
            else if (op == OpComponent || op == OpSymbol) { expectedClass = "FamilyInstance"; classOk = e => e.IsFamilyInstance; }
            else if (op == OpSetStyle) { expectedClass = "CurveElement"; classOk = e => e.IsCurveElement; }
            else { expectedClass = "DetailCurve"; classOk = e => e.IsDetailCurve; }
            add("class", expectedClass + " (or a subclass)",
                JoinDistinct(m.Elements.Select(e => e.ClassName)),
                m.Elements.Count > 0 && m.Elements.All(classOk));

            long? expectedOwner = op == OpSetStyle ? p.ExpectedOwnerViewId : Rid.Value(p.View.Id);
            add("owner_view_id",
                expectedOwner.HasValue ? (JToken)expectedOwner.Value : "none (a model curve has no owner view)",
                JoinDistinct(m.Elements.Select(e => e.OwnerViewId.HasValue
                    ? e.OwnerViewId.Value.ToString(CultureInfo.InvariantCulture) : "none")),
                m.Elements.Count > 0 && m.Elements.All(e =>
                    expectedOwner.HasValue ? e.OwnerViewId == expectedOwner : e.OwnerViewId == null));

            if (op == OpLine || op == OpArc || op == OpPolyline)
            {
                if (p.LineStyle != null)
                {
                    long styleId = Rid.Value(p.LineStyle.Id);
                    add("line_style_id", styleId,
                        JoinDistinct(m.Elements.Select(e => e.StyleId.HasValue
                            ? e.StyleId.Value.ToString(CultureInfo.InvariantCulture) : "none")),
                        m.Elements.Count > 0 && m.Elements.All(e => e.StyleId == styleId));
                }
                else
                {
                    // No style requested: the default Revit used is READ and reported,
                    // never guessed - and held to what the rehearsal read, so a default
                    // that moves between approval and apply fails as a mismatch.
                    add("line_style_default_read",
                        "document default (read from the created element, not guessed)",
                        JoinDistinct(m.Elements.Select(e => (e.StyleId.HasValue
                            ? e.StyleId.Value.ToString(CultureInfo.InvariantCulture) : "none") +
                            (e.StyleName == null ? "" : " '" + e.StyleName + "'"))),
                        m.Elements.Count > 0 && m.Elements.All(e => e.StyleId.HasValue));
                    bool styleStable = baseline != null && baseline.Elements.Count == m.Elements.Count &&
                        baseline.Elements.Zip(m.Elements, (b, r) => b.StyleId == r.StyleId).All(x => x);
                    add("line_style_vs_rehearsed",
                        baseline == null ? null : JoinDistinct(baseline.Elements.Select(e => e.StyleId.HasValue
                            ? e.StyleId.Value.ToString(CultureInfo.InvariantCulture) : "none")),
                        JoinDistinct(m.Elements.Select(e => e.StyleId.HasValue
                            ? e.StyleId.Value.ToString(CultureInfo.InvariantCulture) : "none")),
                        styleStable);
                }
            }
            if (op == OpSetStyle)
            {
                long styleId = Rid.Value(p.NewStyle.Id);
                add("line_style_id", styleId,
                    JoinDistinct(m.Elements.Select(e => e.StyleId.HasValue
                        ? e.StyleId.Value.ToString(CultureInfo.InvariantCulture) : "none")),
                    m.Elements.Count > 0 && m.Elements.All(e => e.StyleId == styleId));
            }

            // ---- geometry, against the REQUEST ----
            if (op == OpLine)
            {
                ElementFacts e0 = m.Elements.Count > 0 ? m.Elements[0] : null;
                double[][] seg = p.RequestedSegments[0];
                add("curve_kind", "line", e0 == null ? null : e0.CurveKind,
                    e0 != null && e0.CurveKind == "line");
                add("endpoints_carry_request", Show(seg[0]) + " -> " + Show(seg[1]),
                    e0 == null || e0.VStart == null ? "(unread)" : Show(e0.VStart) + " -> " + Show(e0.VEnd),
                    e0 != null && DimensionPlanRules.SameEndpoints(seg[0], seg[1], e0.VStart, e0.VEnd, tol));
            }
            else if (op == OpArc)
            {
                ElementFacts e0 = m.Elements.Count > 0 ? m.Elements[0] : null;
                add("curve_kind", "arc", e0 == null ? null : e0.CurveKind, e0 != null && e0.CurveKind == "arc");
                add("arc_center", Show(p.ArcViewCenter),
                    e0 == null || e0.VCenter == null ? "(unread)" : Show(e0.VCenter),
                    e0 != null && DimensionPlanRules.SamePoint(p.ArcViewCenter, e0.VCenter, tol));
                add("arc_radius_feet", p.ArcRadiusFeet,
                    e0 == null || !e0.RadiusFeet.HasValue ? (JToken)null : e0.RadiusFeet.Value,
                    e0 != null && e0.RadiusFeet.HasValue && Math.Abs(e0.RadiusFeet.Value - p.ArcRadiusFeet) <= tol);
                add("arc_endpoints_carry_request", Show(p.ArcViewStart) + " -> " + Show(p.ArcViewEnd),
                    e0 == null || e0.VStart == null ? "(unread)" : Show(e0.VStart) + " -> " + Show(e0.VEnd),
                    e0 != null && DimensionPlanRules.SameEndpoints(p.ArcViewStart, p.ArcViewEnd,
                                                                   e0.VStart, e0.VEnd, tol));
            }
            else if (op == OpPolyline)
            {
                int matched = 0;
                for (int i = 0; i < p.RequestedSegments.Count && i < m.Elements.Count; i++)
                {
                    ElementFacts em = m.Elements[i];
                    if (em.CurveKind == "line" &&
                        DimensionPlanRules.SameEndpoints(p.RequestedSegments[i][0], p.RequestedSegments[i][1],
                                                         em.VStart, em.VEnd, tol))
                        matched++;
                }
                add("segments_carry_request",
                    p.RequestedSegments.Count + " line segment(s), in order",
                    matched + " of " + m.Elements.Count + " read segment(s) match their requested pair",
                    matched == p.RequestedSegments.Count && m.Elements.Count == p.RequestedSegments.Count);
            }
            else if (op == OpFilled || op == OpMasking)
            {
                ElementFacts e0 = m.Elements.Count > 0 ? m.Elements[0] : null;
                long typeId = Rid.Value(p.RegionType.Id);
                add("region_type_id", typeId,
                    e0 == null || !e0.TypeId.HasValue ? (JToken)null : e0.TypeId.Value,
                    e0 != null && e0.TypeId == typeId);
                add("is_masking", p.RegionTypeIsMasking,
                    e0 == null || !e0.TypeIsMasking.HasValue ? (JToken)null : e0.TypeIsMasking.Value,
                    e0 != null && e0.TypeIsMasking == p.RegionTypeIsMasking);
                add("loops", p.LoopViewPoints.Count,
                    e0 == null || !e0.LoopCount.HasValue ? (JToken)null : e0.LoopCount.Value,
                    e0 != null && e0.LoopCount == p.LoopViewPoints.Count);
                var wantCurves = p.LoopViewPoints.Select(l => l.Count).OrderBy(x => x).ToList();
                var readCurves = e0 == null || e0.CurvesPerLoop == null
                    ? null : e0.CurvesPerLoop.OrderBy(x => x).ToList();
                add("curves_per_loop_sorted", new JArray(wantCurves),
                    readCurves == null ? (JToken)null : new JArray(readCurves),
                    readCurves != null && wantCurves.SequenceEqual(readCurves));
                // Every requested vertex must exist among the read boundary vertices,
                // one-to-one within tolerance.
                var requestedVertices = new List<double[]>();
                foreach (IReadOnlyList<double[]> loop in p.LoopViewPoints) requestedVertices.AddRange(loop);
                int vertexMatches = e0 == null || e0.Vertices == null
                    ? 0 : CountMatchedVertices(requestedVertices, e0.Vertices, tol);
                add("boundary_vertices_carry_request",
                    requestedVertices.Count + " vertices",
                    (e0 == null || e0.Vertices == null ? 0 : e0.Vertices.Count) + " read, " + vertexMatches +
                    " matched within tolerance",
                    e0 != null && e0.Vertices != null && e0.Vertices.Count == requestedVertices.Count &&
                    vertexMatches == requestedVertices.Count);
                bool sigStable = baseline != null && baseline.Elements.Count > 0 && e0 != null &&
                                 baseline.Elements[0].RegionSignature != null &&
                                 string.Equals(baseline.Elements[0].RegionSignature, e0.RegionSignature,
                                               StringComparison.Ordinal);
                add("region_signature_vs_rehearsed",
                    baseline == null || baseline.Elements.Count == 0 ? null : baseline.Elements[0].RegionSignature,
                    e0 == null ? null : e0.RegionSignature, sigStable);
            }
            else if (op == OpComponent || op == OpSymbol)
            {
                ElementFacts e0 = m.Elements.Count > 0 ? m.Elements[0] : null;
                long symbolId = Rid.Value(p.Symbol.Id);
                add("family_symbol_id", symbolId,
                    e0 == null || !e0.SymbolId.HasValue ? (JToken)null : e0.SymbolId.Value,
                    e0 != null && e0.SymbolId == symbolId);
                add("category_id", p.SymbolCategoryId,
                    e0 == null || !e0.CategoryId.HasValue ? (JToken)null : e0.CategoryId.Value,
                    e0 != null && e0.CategoryId == p.SymbolCategoryId);
                double[] wantedPoint = { p.ModelPoint.X, p.ModelPoint.Y, p.ModelPoint.Z };
                add("location_point_model_feet", Show(wantedPoint),
                    e0 == null || e0.LocModel == null ? "(unread)" : Show(e0.LocModel),
                    e0 != null && DimensionPlanRules.SamePoint(wantedPoint, e0.LocModel, tol));
                if (p.RotationRadians.HasValue)
                    add("rotation_radians", p.RotationRadians.Value,
                        e0 == null || !e0.RotationRadians.HasValue ? (JToken)null : e0.RotationRadians.Value,
                        e0 != null && e0.RotationRadians.HasValue &&
                        AngleDiff(p.RotationRadians.Value, e0.RotationRadians.Value) <= tol);
                else
                    checks.Add(new JObject
                    {
                        ["field"] = "rotation_observed",
                        ["requested"] = "no rotation requested; the placed rotation is reported, not judged",
                        ["read"] = e0 == null || !e0.RotationRadians.HasValue
                            ? (JToken)JValue.CreateNull() : e0.RotationRadians.Value,
                        ["match"] = true
                    });
                // The final transform, as evidence beside the enforced point/rotation.
                checks.Add(new JObject
                {
                    ["field"] = "transform_origin_observed",
                    ["requested"] = "reported as evidence (Instance.GetTransform().Origin, model feet)",
                    ["read"] = e0 == null || e0.TransformOriginModel == null
                        ? (JToken)JValue.CreateNull() : Show(e0.TransformOriginModel),
                    ["match"] = true
                });
            }

            // Single-curve and polyline signatures, read-vs-rehearsed. Both sides are
            // computed from RE-READ geometry by the same arithmetic, so this holds "the
            // model built the same thing the approval measured" without re-quantization.
            if (op == OpLine || op == OpArc || op == OpPolyline)
            {
                string baseSig = baseline == null ? null : SetSignatureOf(p, baseline);
                string readSig = SetSignatureOf(p, m);
                add("signature_vs_rehearsed", baseSig, readSig,
                    baseSig != null && string.Equals(baseSig, readSig, StringComparison.Ordinal));
            }

            // ---- the bounding box: observed everywhere, ENFORCED post-commit. ----
            // set_line_style is exempt: it creates nothing, so a target's box is the
            // model's business, not this write's postcondition - and an existing model
            // curve target may not even have a view to read one in.
            if (op != OpSetStyle)
            {
                int boxes = m.Elements.Count(e => e.BboxRead);
                if (stage == VerifyStage.PostCommit)
                {
                    add("bbox_present", "a view bounding box on every element",
                        boxes + " of " + m.Elements.Count,
                        m.Elements.Count > 0 && boxes == m.Elements.Count);
                    if (p.RequestModelPoints.Count > 0)
                    {
                        double[] uMin = null, uMax = null;
                        foreach (ElementFacts e in m.Elements)
                        {
                            if (!e.BboxRead || e.BboxMinModel == null) continue;
                            if (uMin == null)
                            {
                                uMin = (double[])e.BboxMinModel.Clone();
                                uMax = (double[])e.BboxMaxModel.Clone();
                            }
                            else
                                for (int i = 0; i < 3; i++)
                                {
                                    uMin[i] = Math.Min(uMin[i], e.BboxMinModel[i]);
                                    uMax[i] = Math.Max(uMax[i], e.BboxMaxModel[i]);
                                }
                        }
                        bool contains = uMin != null;
                        if (contains)
                            foreach (XYZ pt in p.RequestModelPoints)
                            {
                                if (pt.X < uMin[0] - tol || pt.Y < uMin[1] - tol || pt.Z < uMin[2] - tol ||
                                    pt.X > uMax[0] + tol || pt.Y > uMax[1] + tol || pt.Z > uMax[2] + tol)
                                { contains = false; break; }
                            }
                        add("bbox_contains_authored_points",
                            p.RequestModelPoints.Count + " authored point(s) inside the union box (tolerance " +
                            tol.ToString("0.#e+0", CultureInfo.InvariantCulture) + " ft)",
                            uMin == null ? "(no box read)" : Show(uMin) + " -> " + Show(uMax), contains);
                    }
                }
                else
                    checks.Add(new JObject
                    {
                        ["field"] = "bbox_observed",
                        ["requested"] = "enforced post-commit; Revit materialises view boxes late",
                        ["read"] = boxes + " of " + m.Elements.Count + " element(s) answered a view bounding box",
                        ["match"] = true
                    });
            }

            // A fact that could not be read poisons the whole row: "we could not look"
            // must never add up to "it matches".
            if (m.Unreadable != null)
                add("readable", "every field this operation verifies", m.Unreadable, false);

            allOk = ok;
            return new JObject
            {
                ["comparison_tolerance_feet"] = tol,
                ["all_match"] = ok,
                ["checks"] = checks
            };
        }

        private static string SetSignatureOf(Plan p, Measured m)
        {
            if (p.Operation == OpPolyline) return m.SetSignature;
            return m.Elements.Count > 0 ? m.Elements[0].Signature : null;
        }

        private static int ExpectedElementCount(Plan p)
        {
            switch (p.Operation)
            {
                case OpPolyline: return p.ModelCurves.Count;
                case OpSetStyle: return p.TargetExisting != null ? 1 : p.TargetPlan.ModelCurves.Count;
                default: return p.Operation == OpLine || p.Operation == OpArc ? p.ModelCurves.Count : 1;
            }
        }

        /// <summary>Greedy one-to-one matching of requested vertices to read vertices.</summary>
        private static int CountMatchedVertices(List<double[]> requested, List<double[]> read, double tol)
        {
            var used = new bool[read.Count];
            int matched = 0;
            foreach (double[] want in requested)
            {
                for (int i = 0; i < read.Count; i++)
                {
                    if (used[i]) continue;
                    if (DimensionPlanRules.SamePoint(want, read[i], tol)) { used[i] = true; matched++; break; }
                }
            }
            return matched;
        }

        private static double AngleDiff(double a, double b)
        {
            double d = Math.Abs(a - b) % (2 * Math.PI);
            return Math.Min(d, 2 * Math.PI - d);
        }

        /// <summary>One response row for an apply phase.</summary>
        private JObject VerifyRow(Document doc, Plan p, VerifyStage stage, out bool ok)
        {
            Measured m = Measure(doc, p);
            Measured baseline = p.Rehearsed;
            bool allOk;
            JObject verification = BuildChecks(p, m, baseline, stage, out allOk);
            if (baseline == null)
            {
                // Cannot happen while the constructibility gate holds, but if it ever
                // does, an absent baseline must read as failure, not as agreement.
                allOk = false;
                verification["all_match"] = false;
                verification["baseline_missing"] = "this action carries no rehearsal measurement to verify against";
            }
            ok = allOk;
            return new JObject
            {
                ["index"] = p.Index,
                ["operation"] = p.Operation,
                ["key"] = p.Key,
                ["element_ids"] = new JArray(m.Elements.Select(e => (JToken)e.Id)),
                ["unique_ids"] = new JArray(m.Elements.Select(e => e.Uid == null
                    ? (JToken)JValue.CreateNull() : e.Uid)),
                ["verified"] = ok,
                ["verification"] = verification
            };
        }

        /// <summary>Rehearsal evidence: facts only - the provisional ids die with the rollback.</summary>
        private static JObject Evidence(Measured m)
        {
            var o = new JObject
            {
                ["elements"] = m.Elements.Count,
                ["classes"] = JoinDistinct(m.Elements.Select(e => e.ClassName)),
                ["line_styles_read"] = JoinDistinct(m.Elements.Select(e => e.StyleId.HasValue
                    ? e.StyleId.Value.ToString(CultureInfo.InvariantCulture) +
                      (e.StyleName == null ? "" : " '" + e.StyleName + "'")
                    : null)),
                ["signatures_read"] = new JArray(m.Elements
                    .Select(e => e.RegionSignature ?? e.Signature)
                    .Where(s => s != null).Select(s => (JToken)s)),
                ["bboxes_observed"] = m.Elements.Count(e => e.BboxRead),
                ["note"] = "Provisional element ids are NOT reported: they were rolled back and do not survive."
            };
            if (m.SetSignature != null) o["set_signature"] = m.SetSignature;
            if (m.Unreadable != null) o["unreadable"] = m.Unreadable;
            return o;
        }

        // ---------------------------------------------------------------------
        // The materialised plan rows and the dry-run plan rows.
        // ---------------------------------------------------------------------
        private PlannedElement PlannedRow(Plan p)
        {
            var before = new Dictionary<string, string>
            {
                { "view", p.View == null ? "" : SafePlanIdName(p.View) },
                { "units_scale", DimensionPlanRules.CanonicalFeet(p.Scale) },
                { "key", p.Key ?? "" }
            };
            if (p.LineStyle != null) before["line_style"] = SafePlanIdName(p.LineStyle);
            else if (p.Operation == OpLine || p.Operation == OpArc || p.Operation == OpPolyline)
                before["line_style"] = "document_default";
            if (p.RegionType != null)
                before["region_type"] = SafePlanIdName(p.RegionType) + "|masking=" +
                                        (p.RegionTypeIsMasking ? "true" : "false");
            if (p.Symbol != null)
                before["symbol"] = SafePlanIdName(p.Symbol) + "|category=" +
                                   p.SymbolCategoryId.ToString(CultureInfo.InvariantCulture);
            if (p.Operation == OpSetStyle)
            {
                before["new_style"] = SafePlanIdName(p.NewStyle);
                if (p.TargetExisting != null)
                    before["target"] = (p.TargetUid ?? "") + "|before_style=" +
                        (p.BeforeStyleId.HasValue
                            ? p.BeforeStyleId.Value.ToString(CultureInfo.InvariantCulture) : "none");
                else before["target"] = "key:" + p.TargetKey + "|action:" +
                                        p.TargetPlan.Index.ToString(CultureInfo.InvariantCulture);
            }
            if (p.PlacementViewPoint != null)
            {
                before["point"] = DimensionPlanRules.CanonicalPoint(p.PlacementViewPoint[0],
                                                                    p.PlacementViewPoint[1],
                                                                    p.PlacementViewPoint[2]);
                before["rotation"] = p.RotationRadians.HasValue
                    ? p.RotationRadians.Value.ToString("0.########", CultureInfo.InvariantCulture) : "none";
            }
            before["elements"] = ExpectedElementCount(p).ToString(CultureInfo.InvariantCulture);
            before["measured"] = MeasuredCanonical(p.Rehearsed);
            return new PlannedElement
            {
                UniqueId = p.Operation == OpSetStyle && p.TargetExisting != null
                    ? p.TargetUid
                    : "action:" + p.Index,
                Category = p.Operation,
                Action = p.Operation == OpSetStyle ? PlannedAction.Modify : PlannedAction.Create,
                GeometryFingerprint = p.RequestSignature,
                BeforeValues = before
            };
        }

        /// <summary>
        /// What the rehearsal read, canonically. It goes INTO the plan fingerprint, so
        /// the apply's own rehearsal must reproduce it or the token refuses stale -
        /// which is how a swapped default line style or a moved model surfaces.
        /// </summary>
        private static string MeasuredCanonical(Measured m)
        {
            if (m == null) return "";
            var parts = new List<string>
            {
                m.Elements.Count.ToString(CultureInfo.InvariantCulture)
            };
            foreach (ElementFacts e in m.Elements)
            {
                parts.Add(e.StyleId.HasValue ? e.StyleId.Value.ToString(CultureInfo.InvariantCulture) : "");
                parts.Add(e.RegionSignature ?? e.Signature ?? "");
            }
            if (m.SetSignature != null) parts.Add(m.SetSignature);
            return string.Join("|", parts);
        }

        /// <summary>One dry-run plan row: everything the plan resolved, by identity.</summary>
        private JObject PlanRow(Plan p)
        {
            var row = new JObject
            {
                ["index"] = p.Index,
                ["operation"] = p.Operation,
                ["key"] = p.Key,
                ["elements_to_create"] = p.Operation == OpSetStyle ? 0 : ExpectedElementCount(p),
                ["view"] = p.View == null ? (JToken)JValue.CreateNull() : IdNameJson(p.View)
            };
            if (p.Operation == OpLine || p.Operation == OpArc || p.Operation == OpPolyline)
                row["line_style"] = p.LineStyle != null
                    ? IdNameJson(p.LineStyle)
                    : new JObject
                    {
                        ["source"] = "document_default",
                        ["note"] = "no line_style_id was passed; the style Revit creates with is READ from the " +
                                   "rehearsed element and reported in the rehearsal row, never guessed"
                    };
            if (p.RegionType != null)
            {
                row["region_type"] = IdNameJson(p.RegionType);
                ((JObject)row["region_type"])["is_masking"] = p.RegionTypeIsMasking;
                row["loops"] = p.LoopViewPoints.Count;
                row["curves_per_loop"] = new JArray(p.LoopViewPoints.Select(l => (JToken)l.Count));
                row["outer_loop_index"] = p.OuterLoopIndex;
            }
            if (p.Symbol != null)
            {
                row["symbol"] = IdNameJson(p.Symbol);
                ((JObject)row["symbol"])["family_name"] = SafeName(SafeFamily(p.Symbol));
                row["point_view_feet"] = new JArray(p.PlacementViewPoint.Select(v => (JToken)v));
                row["rotation_radians"] = p.RotationRadians.HasValue
                    ? (JToken)p.RotationRadians.Value : JValue.CreateNull();
            }
            if (p.Operation == OpSetStyle)
            {
                row["new_line_style"] = IdNameJson(p.NewStyle);
                row["target"] = p.TargetExisting != null
                    ? new JObject
                    {
                        ["element_id"] = Rid.Value(p.TargetExisting.Id),
                        ["unique_id"] = p.TargetUid,
                        ["before_line_style_id"] = p.BeforeStyleId.HasValue
                            ? (JToken)p.BeforeStyleId.Value : JValue.CreateNull(),
                        ["before_line_style_name"] = p.BeforeStyleName
                    }
                    : new JObject { ["element_key"] = p.TargetKey, ["producing_action"] = p.TargetPlan.Index };
            }
            if (p.RequestedSegments.Count > 0)
            {
                var segs = new JArray();
                foreach (double[][] s in p.RequestedSegments)
                    segs.Add(new JObject { ["start"] = new JArray(s[0].Select(v => (JToken)v)),
                                           ["end"] = new JArray(s[1].Select(v => (JToken)v)) });
                row["segments_view_feet"] = segs;
                if (p.Operation == OpPolyline) row["closed"] = p.ClosedPolyline;
            }
            if (p.ArcViewCenter != null)
                row["arc_view_feet"] = new JObject
                {
                    ["center"] = new JArray(p.ArcViewCenter.Select(v => (JToken)v)),
                    ["radius"] = p.ArcRadiusFeet,
                    ["start"] = new JArray(p.ArcViewStart.Select(v => (JToken)v)),
                    ["end"] = new JArray(p.ArcViewEnd.Select(v => (JToken)v))
                };
            row["request_signature"] = p.RequestSignature;
            row["coordinate_convention"] = Convention;
            return row;
        }

        private static JObject IdNameJson(Element e)
        {
            var o = new JObject();
            if (e == null) return o;
            o["id"] = Rid.Value(e.Id);
            try { o["unique_id"] = e.UniqueId; } catch { o["unique_id"] = "<unreadable>"; }
            try { o["name"] = e.Name; } catch { o["name"] = "<unreadable>"; }
            return o;
        }

        private static JObject ApplyDetail(string state, string txStatus, string groupStatus, JArray rows)
        {
            return new JObject
            {
                ["state"] = state,
                ["transaction_status"] = txStatus,
                ["transaction_group_status"] = groupStatus,
                ["rollback_confirmed"] = state == DimensionPlanRules.StateRolledBack,
                ["comparison_tolerance_feet"] = Detail2DRules.CurveToleranceFeet,
                ["rows"] = rows ?? new JArray()
            };
        }

        // ---------------------------------------------------------------------
        // Small helpers.
        // ---------------------------------------------------------------------
        private sealed class ViewPoint
        {
            public double[] ViewFeet;   // [x, y, 0] on the view plane
            public XYZ Model;
        }

        /// <summary>
        /// One request point into the view-plane frame and the model. A third
        /// component other than 0 refuses: out-of-plane detail is non-coplanar with
        /// the view, and projecting it silently would draw something the caller did
        /// not ask for.
        /// </summary>
        private static ViewPoint ParsePoint(View view, JToken token, double scale, string field)
        {
            JArray a = token as JArray;
            if (a == null || a.Count < 2 || a.Count > 3)
                throw new ArgumentException(field + " must be [x, y] (optionally [x, y, 0]) in " + Convention);
            double x = a[0].Value<double>() * scale;
            double y = a[1].Value<double>() * scale;
            if (a.Count == 3)
            {
                double z = a[2].Value<double>() * scale;
                if (Math.Abs(z) > Detail2DRules.CurveToleranceFeet)
                    throw new ArgumentException(field + " carries a non-zero third component - that is " +
                        "non-coplanar with the view plane, and this command will not project it silently. " +
                        Convention);
            }
            var vp = new ViewPoint { ViewFeet = new[] { x, y, 0d } };
            vp.Model = view.Origin.Add(view.RightDirection.Multiply(x)).Add(view.UpDirection.Multiply(y));
            return vp;
        }

        private static XYZ ModelOf(View view, double[] viewFeet)
            => view.Origin.Add(view.RightDirection.Multiply(viewFeet[0]))
                          .Add(view.UpDirection.Multiply(viewFeet[1]));

        /// <summary>Model point into the view-plane frame: [x, y, out-of-plane], feet.</summary>
        private static double[] ViewFrame(View view, XYZ p)
        {
            XYZ d = p.Subtract(view.Origin);
            return new[]
            {
                d.DotProduct(view.RightDirection),
                d.DotProduct(view.UpDirection),
                d.DotProduct(view.ViewDirection)
            };
        }

        /// <summary>
        /// A direction-canonical signature for an OPEN path: the same polyline sent
        /// reversed is the same polyline. Closed paths use Detail2DRules.LoopSignature,
        /// which additionally canonicalises rotation.
        /// </summary>
        private static string OpenPathSignature(IReadOnlyList<string> segmentSignatures)
        {
            if (segmentSignatures == null || segmentSignatures.Count == 0) return null;
            string forward = string.Join("", segmentSignatures);
            string backward = string.Join("", segmentSignatures.Reverse());
            return Detail2DRules.Sha256Hex("open:" +
                (string.CompareOrdinal(forward, backward) <= 0 ? forward : backward));
        }

        private static string DescribeStyles(Document doc, ICollection<ElementId> valid)
        {
            if (valid == null || valid.Count == 0) return "(none)";
            var parts = new List<string>();
            foreach (ElementId id in valid.OrderBy(Rid.Value))
            {
                Element e = null; try { e = doc.GetElement(id); } catch { e = null; }
                parts.Add(Rid.Value(id) + " '" + (e == null ? "?" : SafeName(e)) + "'");
                if (parts.Count >= 30) { parts.Add("... (" + valid.Count + " total)"); break; }
            }
            return string.Join(", ", parts);
        }

        private static string JoinDistinct(IEnumerable<string> values)
        {
            var distinct = values.Where(v => v != null).Distinct(StringComparer.Ordinal).ToList();
            return distinct.Count == 0 ? "(none)" : string.Join(", ", distinct);
        }

        private static T Need<T>(Document d, JObject a, string f) where T : Element
        {
            long id = a.Value<long?>(f) ?? -1;
            if (!Rid.CanRepresent(id) || !(d.GetElement(Rid.Make(id)) is T e))
                throw new ArgumentException(f + " must identify a " + typeof(T).Name + " by ElementId in the " +
                                            "active document (this command never resolves resources by name; " +
                                            "list them with horizun_query_detail_2d mode=resources).");
            return e;
        }

        private static string Show(double[] v)
            => v == null ? "(null)"
                         : "[" + string.Join(",", v.Select(d => d.ToString("0.######", CultureInfo.InvariantCulture))) + "]";
        private static string SafeName(Element e) { try { return e?.Name ?? ""; } catch { return "<unreadable>"; } }
        private static Family SafeFamily(FamilySymbol s) { try { return s?.Family; } catch { return null; } }

        /// <summary>
        /// Identity and name in one guarded read - the UniqueId is what makes a swap
        /// under the same name visible; unreadable stays a value, never a throw.
        /// </summary>
        private static string SafePlanIdName(Element e)
        {
            if (e == null) return "";
            string uid; try { uid = e.UniqueId ?? ""; } catch { uid = "<unreadable>"; }
            string name; try { name = e.Name ?? ""; } catch { name = "<unreadable>"; }
            return uid + "|" + name;
        }

        /// <summary>
        /// Deletes WARNINGS during a batch so Revit does not raise a modal nobody is at
        /// the keyboard to answer; errors are still Revit's to resolve or roll back.
        /// </summary>
        private class SilenceWarnings : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (var f in a.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        private sealed class Rehearsal
        {
            public bool RollbackConfirmed;
            public string RollbackStatus = PlanFailure.NotAttempted;
            public bool AllConstructible;
            public int NotConstructibleCount;
            public JArray Rows = new JArray();

            public JObject ToJson() => new JObject
            {
                ["transaction_status"] = RollbackStatus,
                ["rolled_back_confirmed"] = RollbackConfirmed,
                ["all_constructible"] = AllConstructible,
                ["not_constructible"] = NotConstructibleCount,
                ["note"] = "Every element was created PROVISIONALLY inside one transaction, read back, and the " +
                           "transaction was rolled back; transaction_status is what Revit's RollBack() returned. " +
                           "Nothing from the rehearsal remains in the model when rolled_back_confirmed is true.",
                ["actions"] = Rows
            };
        }

        /// <summary>Everything the verification reads off one action's elements, guarded.</summary>
        private sealed class Measured
        {
            public readonly List<ElementFacts> Elements = new List<ElementFacts>();
            public string SetSignature;
            public string Unreadable;
        }

        private sealed class ElementFacts
        {
            public long Id;
            public string Uid, ClassName;
            public bool IsDetailCurve, IsCurveElement, IsFilledRegion, IsFamilyInstance;
            public long? OwnerViewId;
            public long? StyleId; public string StyleName;
            public string CurveKind;
            public double[] VStart, VEnd, VCenter;
            public double? RadiusFeet;
            public string Signature;
            public long? TypeId; public bool? TypeIsMasking;
            public int? LoopCount; public List<int> CurvesPerLoop;
            public List<double[]> Vertices; public string RegionSignature;
            public long? SymbolId, CategoryId;
            public double[] LocModel; public double? RotationRadians;
            public double[] TransformOriginModel;
            public bool BboxRead; public double[] BboxMinModel, BboxMaxModel;
        }

        private sealed class Plan
        {
            public int Index; public string Operation; public View View; public double Scale;
            public string Key;

            // Resources, resolved by identity at plan time.
            public GraphicsStyle LineStyle;                 // explicit, or null = document default
            public FilledRegionType RegionType; public bool RegionTypeIsMasking;
            public FamilySymbol Symbol; public long SymbolCategoryId;

            // Geometry.
            public readonly List<Curve> ModelCurves = new List<Curve>();
            public readonly List<double[][]> RequestedSegments = new List<double[][]>();
            public readonly List<XYZ> RequestModelPoints = new List<XYZ>();
            public string RequestSignature;
            public bool ClosedPolyline;
            public double[] ArcViewCenter, ArcViewStart, ArcViewEnd; public double ArcRadiusFeet;
            public IReadOnlyList<IReadOnlyList<double[]>> LoopViewPoints = new List<IReadOnlyList<double[]>>();
            public readonly List<CurveLoop> ModelLoops = new List<CurveLoop>();
            public int OuterLoopIndex;
            public double[] PlacementViewPoint; public XYZ ModelPoint; public double? RotationRadians;

            // set_line_style.
            public CurveElement TargetExisting; public string TargetUid;
            public string TargetKey; public Plan TargetPlan;
            public GraphicsStyle NewStyle;
            public long? BeforeStyleId; public string BeforeStyleName;
            public long? ExpectedOwnerViewId;

            // Execution.
            public readonly List<ElementId> Created = new List<ElementId>();
            public List<ElementId> Applied;
            public Measured Rehearsed;
        }
    }
}
