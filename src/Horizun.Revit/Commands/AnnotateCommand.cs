// -----------------------------------------------------------------------------
// Horizun Revit MCP - text, tags and dimensions with explicit references.
//
// THE DIMENSION OPERATIONS ARE PRODUCTION WRITES, and this file holds them to
// the same bar as transform and write_params:
//
//   * a dry run does not merely validate arguments - it CREATES every annotation
//     provisionally in one transaction, regenerates, measures the result, and
//     rolls back, reporting the rollback status Revit actually returned. What a
//     caller approves is a batch Revit has already demonstrated it can build,
//     with the measured values in hand;
//   * the materialised plan binds the view, the EFFECTIVE dimension type
//     (explicit or the document default resolved now - a default swapped between
//     rehearsal and apply is a stale plan), every reference reserialized with
//     its owner, its subgeometry kind and a 0.1 mm geometry fingerprint, the
//     requested line, and the value the rehearsal measured;
//   * the apply runs inside a TransactionGroup: create, regenerate, verify every
//     postcondition while the transaction is still reversible, commit only when
//     all of them hold, re-read everything again after the commit, and only then
//     assimilate. Any failure rolls the WHOLE batch back and the response
//     carries the real TransactionStatus of every rollback - uncertain when
//     Revit did not confirm one, never a claimed-clean model;
//   * verification is per field: requested / read / match, with the comparison
//     tolerance named. There is no bare verified=true for a dimension.
//
// Operations whose API does not exist in this Revit (radial/diameter/arc length
// before 2025, spot slope everywhere) refuse with an ORDINARY planning error on
// purpose, never UnsupportedCapability: that type grants the Python fallback,
// and Python cannot call a class that is absent from RevitAPI.dll either.
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
    public sealed class AnnotateCommand : ICommand
    {
        public string Name => "horizun_annotate";
        public string Description => "Create text, tags and dimensions atomically and verify the committed annotations.";

#if REVIT2023
        private const string HostRevitYear = "2023";
#elif REVIT2024
        private const string HostRevitYear = "2024";
#elif REVIT2025
        private const string HostRevitYear = "2025";
#elif REVIT2026
        private const string HostRevitYear = "2026";
#elif REVIT2027
        private const string HostRevitYear = "2027";
#else
        private const string HostRevitYear = "unknown";
#endif

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name); if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document; JArray actions = request["actions"] as JArray;
            // The rehearsal itself opens a transaction, so a read-only document must be
            // refused in prose HERE - otherwise the very first dry run answers with a raw
            // Revit exception out of tx.Start() instead of a sentence about the document.
            bool docReadOnly; try { docReadOnly = doc.IsReadOnly; } catch { docReadOnly = false; }
            if (docReadOnly)
                return CommandResult.Fail("The active document is READ-ONLY, so nothing can be annotated in it - " +
                    "not even the dry run's provisional rehearsal, which creates and rolls back inside a " +
                    "transaction. Open the document writable and call again. Nothing was changed.");
            if (actions == null || actions.Count == 0 || actions.Count > 1000) return CommandResult.Fail("actions must contain 1..1000 entries.");
            double scale; if (!DimensionPlanRules.UnitScale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale)) return CommandResult.Fail("units must be mm, m or feet.");

            // MEASURED (2025, 2026-08-24, three runs): Revit materialises a dimension's
            // references and values only for a view that is DISPLAYED - in a view that
            // has never been shown, a correct committed dimension reads
            // AreReferencesAvailable=false and Value 0 whatever is regenerated, and this
            // command's verification would honestly roll it back. So a dimension's view
            // must be the ACTIVE graphical view, checked at plan time with the fix in
            // the message, instead of a rollback nobody can act on at the end.
            long? activeGraphicalViewId = null;
            try
            {
                View agv = app?.ActiveUIDocument?.ActiveGraphicalView;
                if (agv != null) activeGraphicalViewId = Rid.Value(agv.Id);
            }
            catch { activeGraphicalViewId = null; }

            var plans = new List<Plan>(); var errors = new JArray();
            // Every action's outcome, so the fallback is decided once over the whole
            // batch: one uncovered operation must not grant permission for a request that
            // also contains input the caller should fix. See FallbackDecision.
            var outcomes = new List<ActionOutcome>();
            for (int i = 0; i < actions.Count; i++)
            {
                string error = null, reason = null;
                Plan p = PlanAction(doc, i, actions[i] as JObject, scale, activeGraphicalViewId, out error, out reason);
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

            // ---- THE REHEARSAL: provisional creation, measurement, mandatory rollback. --
            // Run on BOTH paths whenever every action planned cleanly. On the dry run it
            // is the evidence a person approves; on the apply it recomputes the measured
            // values the materialised plan is fingerprinted over, so a model that moved
            // since the approved rehearsal refuses as stale instead of being written to.
            // Never run over a batch with invalid entries: rehearsing half a request
            // would publish constructibility about a batch that cannot be applied as-is,
            // and it would open a transaction on a call whose refusal claims none was.
            Rehearsal rehearsal = null;
            if (errors.Count == 0)
            {
                rehearsal = Rehearse(doc, plans);
                if (!rehearsal.RollbackConfirmed)
                {
                    // The one outcome that may not be smoothed: provisional annotations
                    // were created and Revit did not confirm their removal. No token, no
                    // claim of a clean model, and the state says so.
                    return CommandResult.FailWithDetail(
                        "The rehearsal transaction could not be rolled back: Revit reported '" +
                        rehearsal.RollbackStatus + "', not RolledBack. The model may still carry the provisional " +
                        "annotations, so the state of this call is UNCERTAIN - no confirmation token is issued and " +
                        "nothing is claimed clean. Re-read the model before anything else.",
                        new JObject
                        {
                            ["state"] = DimensionPlanRules.StateUncertain,
                            ["rehearsal_rollback_status"] = rehearsal.RollbackStatus,
                            ["write_started"] = true,
                            ["rehearsal"] = rehearsal.ToJson()
                        });
                }
            }

            // ---- The MATERIALISED plan: the VIEW and TARGET each annotation lands on. ---
            // hash binds the actions as written. An annotation is ABOUT something: a tag
            // points at a target element, a dimension hangs off references measured from
            // one, and everything lands on a view. A tag approved against "Bomba 5" that
            // gets applied after somebody swaps that element is a label telling a reader
            // the wrong thing in print - the quietest wrong answer a model can produce.
            // So each row records the view and the target as resolved now, by identity
            // and by name; a dimension row additionally binds the EFFECTIVE type, every
            // reference (reserialized, with its owner, kind and geometry fingerprint at
            // 0.1 mm), the requested geometry, and the value the rehearsal measured.
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
                var result = new JObject { ["dry_run"] = true, ["valid"] = plans.Count, ["invalid"] = errors.Count,
                    ["errors"] = errors, ["plan"] = new JArray(plans.Select(p => PlanRow(p))) };
                result["rehearsal"] = rehearsal == null
                    ? (JToken)JValue.CreateNull()
                    : rehearsal.ToJson();
                if (rehearsal == null)
                    result["rehearsal_note"] = "The batch was NOT rehearsed: " + errors.Count + " action(s) are " +
                        "invalid, so no transaction was opened and nothing was provisionally created.";
                if (errors.Count == 0 && constructible) DocumentGate.RecordResolvedPlan(resolvedPlan);
                // Invalid or non-constructible entries make this a partial rehearsal, not
                // a clean one: the token below is already withheld for them, and the plan
                // must read the same fact.
                ApplicationOutcome.StampRehearsal(result, plans.Count + errors.Count, errors.Count,
                                                  rehearsal == null ? 0 : rehearsal.NotConstructibleCount, 0);
                DocumentGate.StampConfirmation(result, gate, Name, hash, errors.Count == 0 && constructible,
                    errors.Count == 0 && constructible
                        ? "the token binds views, geometry, text, the identity of every view and target, and - for " +
                          "dimensions - the EFFECTIVE dimension type, every reference's reserialized identity, owner " +
                          "and 0.1 mm geometry fingerprint, and the measured value of this rehearsal. A model that " +
                          "moves before you spend it refuses as a stale plan rather than dimensioning something else."
                        : errors.Count > 0
                            ? "no usable token while invalid"
                            : "no usable token: the rehearsal could not construct every annotation - see rehearsal " +
                              "rows for Revit's own reason per action");
                // StampConfirmation only writes a note when it ISSUES a token, so the two
                // withheld cases must say so themselves - an absent token with no sentence
                // reads like a bug in the caller's parsing, not like a decision.
                if (!(errors.Count == 0 && constructible))
                    result["confirmation_note"] = errors.Count > 0
                        ? "NO token was issued: " + errors.Count + " action(s) are invalid. Fix them and re-run " +
                          "the dry run; a partial batch is never approvable."
                        : "NO token was issued: the rehearsal could not construct every annotation against the " +
                          "current model - see the rehearsal rows for Revit's own reason per action.";
                // THE REHEARSAL CARRIES THE VERDICT TOO. dry_run defaults to true, so this
                // is the first call a caller makes; without the block here they got
                // success=true with invalid rows and no way to tell a capability gap
                // from a typo except by sending an apply they had no reason to send.
                // writeStarted stays false: the provisional-creation transaction only ever
                // runs when EVERY action planned cleanly, so on any path where a fallback
                // could be granted (some action failed) no transaction was opened.
                return FallbackDecision.Attach(
                    CommandResult.Ok(result),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (errors.Count > 0)
            {
                // NOTHING RAN - no transaction was opened - so the decision is only about
                // what failed, and it is made centrally.
                return FallbackDecision.Refuse(
                    "Invalid annotations; nothing ran: " + errors.ToString(Formatting.None),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (!rehearsal.AllConstructible)
            {
                // The apply's own rehearsal could not build the batch against the model as
                // it stands NOW. Refusing here is what keeps the real transaction from
                // discovering the same failure half-way through a write.
                return CommandResult.FailWithDetail(
                    "Refused: " + rehearsal.NotConstructibleCount + " of " + plans.Count + " annotation(s) are not " +
                    "constructible against the current model - see the rehearsal rows for Revit's reason per action. " +
                    "Nothing was committed: the rehearsal transaction rolled back (Revit reported '" +
                    rehearsal.RollbackStatus + "').",
                    new JObject
                    {
                        ["state"] = DimensionPlanRules.StateRefused,
                        ["rehearsal"] = rehearsal.ToJson()
                    });
            }
            // Recomputed by THIS call's own PlanAction resolution AND its own rehearsal:
            // the fingerprint carries the measured values, so the stale check compares
            // what the model would build now against what was approved.
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash,
                                                                     resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name); if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name"); if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: annotate";
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
                        foreach (Plan p in plans)
                        {
                            Element e = Create(doc, p); p.Created = e?.Id;
                            Dimension d = e as Dimension; if (d != null) Decorate(d, p);
                        }
                        doc.Regenerate();
                        // Verify while the transaction is still reversible: a postcondition
                        // that fails here rolls back a write nobody has been told about yet.
                        foreach (Plan p in plans)
                        {
                            bool ok; JObject row = VerifyRow(doc, p, VerifyStage.InTransaction, out ok);
                            inTxRows.Add(row); if (ok) inTxVerified++;
                        }
                        if (inTxVerified != plans.Count)
                        {
                            Guard.RollbackResult rbTx = Guard.RollBack(tx);
                            Guard.RollbackResult rbGroup = Guard.RollBack(group);
                            string state = DimensionPlanRules.FinalState(false,
                                new[] { rbTx.StatusName, rbGroup.StatusName });
                            return CommandResult.FailWithDetail(
                                (plans.Count - inTxVerified) + " of " + plans.Count + " annotation(s) failed " +
                                "verification BEFORE the commit, so the whole batch was rolled back. " +
                                PlanFailure.SingleTransactionOutcome(true, rbTx.StatusName, "nothing was annotated") +
                                " The TransactionGroup reported '" + rbGroup.StatusName + "'. Each row lists every " +
                                "requested/read comparison.",
                                ApplyDetail(state, rbTx.StatusName, rbGroup.StatusName, inTxRows));
                        }
                        Guard.Commit(tx, txName);
                    }
                    catch (Exception ex)
                    {
                        // Report what the rollback ACTUALLY did, not the hoped-for prose. A
                        // status other than RolledBack keeps its uncertainty rather than
                        // claiming a clean model. When the transaction is NOT open any more
                        // - Guard.Commit threw after Revit silently rolled it back, say -
                        // its REAL status still goes into the state decision: a RolledBack
                        // read from Revit is a confirmation, and anything else must keep
                        // the whole answer uncertain rather than lean on the group alone.
                        bool attempted = false; string rbTxStatus = PlanFailure.NotAttempted;
                        TransactionStatus txNow; try { txNow = tx.GetStatus(); } catch { txNow = TransactionStatus.Uninitialized; }
                        if (txNow == TransactionStatus.Started) { attempted = true; rbTxStatus = Guard.RollBack(tx).StatusName; }
                        else if (txNow != TransactionStatus.Uninitialized) rbTxStatus = txNow.ToString();
                        Guard.RollbackResult rbGrp = Guard.RollBack(group);
                        var statuses = new List<string>();
                        if (attempted || (txNow != TransactionStatus.Uninitialized && txNow != TransactionStatus.Started))
                            statuses.Add(rbTxStatus);
                        statuses.Add(rbGrp.StatusName);
                        string state = DimensionPlanRules.FinalState(false, statuses);
                        return CommandResult.FailWithDetail(
                            "Atomic annotation failed: " + ex.Message + ". " +
                            PlanFailure.SingleTransactionOutcome(attempted, rbTxStatus, "nothing was annotated") +
                            " The TransactionGroup reported '" + rbGrp.StatusName + "'.",
                            ApplyDetail(state, rbTxStatus, rbGrp.StatusName, inTxRows));
                    }
                }
                // Committed inside the group. MEASURED (2025, 2026-08-24, twice): Revit
                // does not materialise AreReferencesAvailable or dimension values on the
                // inner commit alone - a correct dimension still read false/0 here. A
                // regeneration AFTER the commit, in its own committed transaction still
                // inside the group, is what asks Revit to compute them; the group remains
                // open, so a verification that fails after this still rolls EVERYTHING
                // back. The regeneration writes nothing of its own.
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
                // The only evidence that counts now is a FRESH read of every annotation;
                // a re-read that fails rolls the group back and takes the committed
                // transaction with it.
                var rows = new JArray(); int verified = 0;
                foreach (Plan p in plans)
                {
                    bool ok; JObject row = VerifyRow(doc, p, VerifyStage.PostCommit, out ok);
                    rows.Add(row); if (ok) verified++;
                }
                if (verified != plans.Count)
                {
                    Guard.RollbackResult rbGroup = Guard.RollBack(group);
                    string state = DimensionPlanRules.FinalState(false, new[] { rbGroup.StatusName });
                    return CommandResult.FailWithDetail(
                        "The transaction committed, but " + (plans.Count - verified) + " of " + plans.Count +
                        " annotation(s) failed the post-commit re-read, so the TransactionGroup was rolled back " +
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
                        "Every annotation verified, but the TransactionGroup would not assimilate: " + ex.Message +
                        " A rollback was attempted and Revit reported '" + rbStatus + "'. " +
                        (PlanFailure.IsConfirmedRollback(rbStatus)
                            ? "Nothing from this call remains in the model."
                            : "The state of the model is UNCERTAIN - re-read it before any retry."),
                        ApplyDetail(state, "Committed", rbStatus, rows));
                }
                var anResult = new JObject
                {
                    ["transaction_status"] = "Committed",
                    ["transaction_group_status"] = "Committed",
                    ["state"] = DimensionPlanRules.StateCommittedVerified,
                    ["annotations_verified"] = verified,
                    ["comparison_tolerance_feet"] = DimensionPlanRules.CurveToleranceFeet,
                    ["units_note"] = "Measured values are Revit internal units (decimal feet); 'presented' is the " +
                                     "document's own display formatting.",
                    ["rows"] = rows
                };
                // Reached only when verified == plans.Count; a shortfall rolled back above.
                ApplicationOutcome.StampApplied(anResult, ApplicationOutcome.Committed,
                                                plans.Count, verified, verified, 0, 0, 0);
                return CommandResult.Ok(anResult);
            }
        }

        // ---------------------------------------------------------------------
        // Planning.
        // ---------------------------------------------------------------------
        private static Plan PlanAction(Document doc, int index, JObject a, double scale, long? activeGraphicalViewId, out string error,
                                       out string unsupportedReason)
        {
            error = null; unsupportedReason = null;
            if (a == null) { error = "entry is not an object"; return null; }
            try
            {
                string op = (a.Value<string>("operation") ?? "").ToLowerInvariant();
                // The closed enum first: an operation OUTSIDE it is the structural gap
                // that may grant the Python fallback. An operation INSIDE it that this
                // Revit's API cannot build must NOT - see the year guards below.
                if (!DimensionPlanRules.IsKnownOperation(op))
                    throw new UnsupportedCapability(
                        "unsupported operation '" + op + "' - horizun_annotate creates text, tags and dimensions " +
                        "(linear, angular, radial, diameter, arc length, spot elevation, spot coordinate) only. " +
                        "Nothing was written.", FallbackSignal.ReasonUnsupportedOperation);

                // No Revit API can create these here, and Python cannot call an absent
                // class either - so these are ORDINARY planning errors on purpose, never
                // UnsupportedCapability: that type is the grant of the Python fallback,
                // and granting it would send a client to script against nothing.
                if (op == DimensionPlanRules.OpSpotSlope)
                    throw new ArgumentException(DimensionPlanRules.NoApiAnyYear(op));   // not supported: the API is absent everywhere
#if REVIT2023 || REVIT2024
                if (op == DimensionPlanRules.OpRadial || op == DimensionPlanRules.OpDiameter)
                    throw new ArgumentException(DimensionPlanRules.NoApiThisYear(op, "RadialDimension.Create", 2025, HostRevitYear));   // not supported before 2025
                if (op == DimensionPlanRules.OpArcLength)
                    throw new ArgumentException(DimensionPlanRules.NoApiThisYear(op, "ArcLengthDimension.Create", 2025, HostRevitYear));   // not supported before 2025
#endif

                View view = Need<View>(doc, a, "view_id");
                if (view.IsTemplate) throw new ArgumentException("view_id is a template");
                var p = new Plan { Index = index, Operation = op, View = view, Input = a, Scale = scale };
                if (op == "text" || op == "tag")
                {
                    // The schema carries the dimension fields on EVERY action (one
                    // properties table, conditional requireds), so a text action offering
                    // expected_value would otherwise succeed with the option silently
                    // dropped - a request the caller believes was honoured. Same rule the
                    // dimension path applies through UnavailableOptions, extended to the
                    // dimension-shape fields these operations cannot mean.
                    var foreign = new List<string>();
                    foreach (string f in DimensionPlanRules.OptionFields)
                        if (a[f] != null) foreign.Add(f);
                    foreach (string f in new[] { "line_start", "line_end", "references",
                                                 "arc_center", "arc_radius", "arc_reference", "reference" })
                        if (a[f] != null) foreign.Add(f);
                    if (foreign.Count > 0)
                        throw new ArgumentException(op + " does not carry " + string.Join(", ", foreign) +
                            " - those are dimension fields, and accepting them here would drop them silently. " +
                            "Remove them, or use a dimension operation.");
                }
                if (op == "text")
                {
                    if (a["tag_type_id"] != null || a["element_id"] != null || a["tag_mode"] != null ||
                        a["orientation"] != null || a["add_leader"] != null)
                        throw new ArgumentException("text does not accept tag_type_id, element_id, tag_mode, " +
                            "orientation or add_leader; accepting them would silently drop tag intent.");
                    p.Point = Point(a["point"], scale, false); p.Text = a.Value<string>("text");
                    if (string.IsNullOrEmpty(p.Text)) throw new ArgumentException("text is required");
                    p.Type = Need<TextNoteType>(doc, a, "text_type_id");
                }
                else if (op == "tag")
                {
                    if (a["text"] != null || a["text_type_id"] != null)
                        throw new ArgumentException("tag does not accept text or text_type_id; accepting them would silently drop text-note intent.");
                    p.Point = Point(a["point"], scale, false); p.Target = Need<Element>(doc, a, "element_id");
                    if (a["tag_type_id"] != null)
                        p.Type = Need<ElementType>(doc, a, "tag_type_id");
                    p.ExistingTagCount = ExistingTagCount(doc, p.View.Id, p.Target.Id);
                }
                else PlanDimension(doc, p, a, scale, activeGraphicalViewId);
                return p;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return null;
            }
        }

        /// <summary>
        /// One dimension action, fully resolved: view compatibility, the conditional
        /// requirements table, option availability and eligibility, references parsed
        /// AND reserialized AND fingerprinted, the effective type materialised. Throws
        /// with a message the caller can act on; writes nothing.
        /// </summary>
        private static void PlanDimension(Document doc, Plan p, JObject a, double scale, long? activeGraphicalViewId)
        {
            string op = p.Operation;

            List<string> missing = DimensionPlanRules.MissingFields(op, f => a[f] != null);
            if (missing != null && missing.Count > 0)
                throw new ArgumentException(op + " is missing required field(s): " + string.Join(", ", missing));

            var offered = DimensionPlanRules.OptionFields.Where(f => a[f] != null).ToList();
            List<string> unavailable = DimensionPlanRules.UnavailableOptions(op, offered);
            if (unavailable.Count > 0) throw new ArgumentException(string.Join(" ", unavailable));

            // Where a dimension may live. Schedules and sheets have no model space to
            // measure in; a 3D view is only stable once it is locked - Revit itself
            // refuses annotation in an unlocked one.
            if (p.View is ViewSchedule) throw new ArgumentException("view_id is a schedule; a dimension needs a model view.");
            if (p.View is ViewSheet) throw new ArgumentException("view_id is a sheet; a dimension needs a model view.");
            var v3 = p.View as View3D;
            if (v3 != null && !v3.IsLocked)
                throw new ArgumentException("view_id is an UNLOCKED 3D view. Revit only accepts dimensions in a " +
                                            "locked 3D view (View3D.IsLocked); lock it or pick a 2D view.");

            // The measured precondition, refused where the caller can act on it - and
            // AFTER the structural view checks above, so a schedule aimed at stays
            // refused as a schedule, not as merely inactive. Revit materialises a
            // dimension's references and values only for a DISPLAYED view (measured
            // live, three runs: in a never-shown view a correct committed dimension
            // reads AreReferencesAvailable=false and Value 0 whatever is regenerated,
            // and the post-commit verification would honestly roll the whole batch
            // back). Text and tags are exempt: their verification reads text and tagged
            // ids, which materialise regardless.
            long requestedViewId = Rid.Value(p.View.Id);
            if (activeGraphicalViewId == null)
                throw new ArgumentException("the ACTIVE graphical view could not be read, so the measured " +
                    "precondition for dimensions - Revit materialises their references and values only in a " +
                    "displayed view - cannot be checked. Activate the target view (horizun_navigate " +
                    "operation=open_view) and call again. Nothing was written.");
            if (activeGraphicalViewId.Value != requestedViewId)
                throw new ArgumentException("view_id " + requestedViewId + " is not the ACTIVE view (the active " +
                    "graphical view is " + activeGraphicalViewId.Value + "). Revit materialises a dimension's " +
                    "references and values only for a DISPLAYED view - in a never-shown view a correct committed " +
                    "dimension reads AreReferencesAvailable=false and Value 0, and the post-commit verification " +
                    "would roll the whole batch back. Open the target view first (horizun_navigate " +
                    "operation=open_view, view_id=" + requestedViewId + ") and call again. Nothing was written.");

            // ---- references ----
            if (op == DimensionPlanRules.OpDimension || op == DimensionPlanRules.OpAngular ||
                op == DimensionPlanRules.OpArcLength)
            {
                JArray refsToken = a["references"] as JArray;
                List<string> stable = refsToken == null
                    ? null
                    : refsToken.Select(t => t != null && t.Type == JTokenType.String ? t.Value<string>() : null).ToList();
                string listError = DimensionPlanRules.ReferenceListError(op, stable);
                if (listError != null) throw new ArgumentException(listError);
                for (int i = 0; i < stable.Count; i++) AddReference(doc, p, stable[i], "references[" + i + "]");
            }
            else
            {
                string single = a.Value<string>("reference");
                if (string.IsNullOrWhiteSpace(single))
                    throw new ArgumentException(op + " needs 'reference': one stable reference string.");
                AddReference(doc, p, single, "reference");
            }
            if (op == DimensionPlanRules.OpArcLength)
            {
                string arcRef = a.Value<string>("arc_reference");
                if (string.IsNullOrWhiteSpace(arcRef))
                    throw new ArgumentException(op + " needs 'arc_reference': the curved edge being measured.");
                p.ArcReference = AddReference(doc, p, arcRef, "arc_reference");
            }
            if (op == DimensionPlanRules.OpRadial || op == DimensionPlanRules.OpDiameter)
                p.ArcReference = p.RefList[0];

            // ---- per-operation geometry ----
            if (op == DimensionPlanRules.OpDimension)
            {
                XYZ x = Point(a["line_start"], scale, true), y = Point(a["line_end"], scale, true);
                if (x.DistanceTo(y) < 1e-9) throw new ArgumentException("dimension line endpoints must differ");
                p.Line = Line.CreateBound(x, y);
                p.References = new ReferenceArray();
                foreach (Reference r in p.RefList) p.References.Append(r);

                List<string> ineligible = DimensionPlanRules.IneligibleOptions(p.RefList.Count, offered);
                if (ineligible.Count > 0) throw new ArgumentException(string.Join(" ", ineligible));
                if (a["prefix"] != null) { p.HasPrefix = true; p.Prefix = a.Value<string>("prefix") ?? ""; }
                if (a["suffix"] != null) { p.HasSuffix = true; p.Suffix = a.Value<string>("suffix") ?? ""; }
                if (a["above"] != null) { p.HasAbove = true; p.Above = a.Value<string>("above") ?? ""; }
                if (a["below"] != null) { p.HasBelow = true; p.Below = a.Value<string>("below") ?? ""; }
                if (a["value_override"] != null) { p.HasValueOverride = true; p.ValueOverride = a.Value<string>("value_override") ?? ""; }
                if (a["eq"] != null) p.Eq = a.Value<bool>("eq");
                if (a["lock"] != null) p.Lock = a.Value<bool>("lock");
            }
            else if (op == DimensionPlanRules.OpAngular || op == DimensionPlanRules.OpArcLength)
            {
                XYZ center = Point(a["arc_center"], scale, true);
                double radius = (a.Value<double?>("arc_radius") ?? 0) * scale;
                if (radius <= 0) throw new ArgumentException("arc_radius must be greater than zero.");
                p.ArcCenter = center; p.ArcRadius = radius;
                XYZ xAxis = p.View.RightDirection, yAxis = p.View.UpDirection;
                if (op == DimensionPlanRules.OpAngular)
                {
                    // The dimension arc sweeps from the view's X axis through the angle
                    // BETWEEN the two references, measured from their own directions
                    // projected into the view plane. Two parallel references have no
                    // angle to dimension and refuse here rather than in the transaction.
                    XYZ d1 = DirectionOf(doc, p.RefList[0], p.View, 0);
                    XYZ d2 = DirectionOf(doc, p.RefList[1], p.View, 1);
                    double angle = d1.AngleTo(d2);
                    if (angle < 1e-6 || Math.Abs(Math.PI - angle) < 1e-6)
                        throw new ArgumentException("the two references are parallel in this view; an angular " +
                                                    "dimension needs two non-parallel directions.");
                    p.Arc = Arc.Create(center, radius, 0, angle, xAxis, yAxis);
                }
                else
                {
                    // The endpoints of an arc carry no direction to measure an angle
                    // from, so the dimension arc is drawn as a half sweep in the view
                    // plane at the caller's centre and radius; the rehearsal is what
                    // proves Revit accepts it against the referenced arc.
                    p.Arc = Arc.Create(center, radius, 0, Math.PI, xAxis, yAxis);
                }
            }
            else if (op == DimensionPlanRules.OpSpotElevation || op == DimensionPlanRules.OpSpotCoordinate)
            {
                p.SpotOrigin = Point(a["point"], scale, true);
                XYZ right = p.View.RightDirection, up = p.View.UpDirection;
                p.SpotBend = a["bend"] != null
                    ? Point(a["bend"], scale, true)
                    : p.SpotOrigin.Add(right.Multiply(2.0)).Add(up.Multiply(2.0));
                p.SpotEnd = a["end"] != null
                    ? Point(a["end"], scale, true)
                    : p.SpotBend.Add(right.Multiply(2.0));
                p.SpotLeader = a.Value<bool?>("leader") ?? false;
            }

            // ---- expected_value: the caller's postcondition on the measured value ----
            if (a["expected_tolerance"] != null && a["expected_value"] == null)
                throw new ArgumentException("expected_tolerance without expected_value tolerates nothing; " +
                                            "pass both or neither.");
            if (a["expected_value"] != null)
            {
                double raw = a.Value<double>("expected_value");
                if (op == DimensionPlanRules.OpAngular)
                {
                    // Millimetres mean nothing to an angle: angular expectations arrive
                    // in DEGREES and compare in radians.
                    p.ExpectedValueFeet = DimensionPlanRules.DegreesToRadians(raw);
                    double? tol = a.Value<double?>("expected_tolerance");
                    p.ExpectedToleranceFeet = tol.HasValue
                        ? DimensionPlanRules.DegreesToRadians(tol.Value)
                        : DimensionPlanRules.DefaultAngularToleranceRadians;
                }
                else
                {
                    p.ExpectedValueFeet = raw * scale;
                    p.ExpectedToleranceFeet = DimensionPlanRules.ExpectedToleranceFeet(
                        a.Value<double?>("expected_tolerance"), scale);
                }
            }

            // ---- the EFFECTIVE dimension type, explicit or the default materialised ----
            p.EffectiveType = EffectiveDimensionType(doc, a, op, out p.TypeFromDefault);
            p.Type = p.EffectiveType;
        }

        /// <summary>
        /// Parse one stable reference, refuse links, reserialize it, and fingerprint
        /// the geometry behind it. Returns the parsed reference and records every fact
        /// on the plan, in order.
        /// </summary>
        private static Reference AddReference(Document doc, Plan p, string stable, string field)
        {
            Reference r;
            try { r = Reference.ParseFromStableRepresentation(doc, stable); }
            catch (Exception ex)
            {
                throw new ArgumentException(field + " does not parse as a stable reference in this document: " +
                                            ex.Message);
            }
            if (r == null) throw new ArgumentException(field + " did not resolve to a reference.");
            ElementId linked = null;
            try { linked = r.LinkedElementId; } catch { linked = null; }
            if (linked != null && linked != ElementId.InvalidElementId)
                throw new ArgumentException(field + " resolves into a LINKED model; linked references are not supported by horizun_annotate - " +
                                            "this command is host-only by contract (the same rule as " +
                                            "horizun_get_dimension_references), so dimension host geometry instead. " +
                                            "Nothing was written.");

            Element owner = null;
            try { owner = doc.GetElement(r); } catch { owner = null; }
            if (owner == null)
                throw new ArgumentException(field + " parses but its element no longer exists in this document.");

            string reserialized;
            try { reserialized = r.ConvertToStableRepresentation(doc); }
            catch (Exception ex)
            {
                throw new ArgumentException(field + " could not be reserialized (" + ex.Message + "), so the plan " +
                                            "cannot bind it. Nothing was written.");
            }
            string refType;
            try { refType = r.ElementReferenceType.ToString(); } catch { refType = "unknown"; }

            int order = p.RefList.Count;
            string fingerprint = ReferenceGeometryFingerprint(doc, p.View, r, owner, order, refType, field);

            p.RefList.Add(r);
            p.RefRequested.Add(stable);
            p.RefReserialized.Add(reserialized);
            p.RefOwnerUids.Add(SafeUid(owner));
            p.RefTypes.Add(refType);
            p.RefFingerprints.Add(fingerprint);
            return r;
        }

        /// <summary>
        /// The geometric FACTS behind a reference, fingerprinted at 0.1 mm: a planar
        /// face by its origin and normal, a curve by its endpoints (a full circle by
        /// centre and radius), an endpoint by its coordinate, a datum by its curve in
        /// this view. A reference whose geometry cannot be read refuses: a plan that
        /// cannot bind the geometry cannot promise stale detection, and promising it
        /// anyway is the lie this whole file exists to prevent.
        /// </summary>
        private static string ReferenceGeometryFingerprint(Document doc, View view, Reference r, Element owner,
                                                           int order, string refType, string field)
        {
            var facts = new List<double>();
            string kind = null;

            GeometryObject g = null;
            try { g = owner.GetGeometryObjectFromReference(r); } catch { g = null; }

            if (g is PlanarFace pf)
            {
                kind = "face";
                Push(facts, pf.Origin); Push(facts, pf.FaceNormal);
            }
            else if (g is Face face)
            {
                // A non-planar face still has a measurable anchor: its midpoint and the
                // normal there.
                try
                {
                    BoundingBoxUV bb = face.GetBoundingBox();
                    var mid = new UV((bb.Min.U + bb.Max.U) / 2, (bb.Min.V + bb.Max.V) / 2);
                    kind = "face";
                    Push(facts, face.Evaluate(mid)); Push(facts, face.ComputeNormal(mid));
                }
                catch { kind = null; }
            }
            else if (g is Edge edge)
            {
                Curve c = null; try { c = edge.AsCurve(); } catch { c = null; }
                kind = CurveFacts(c, facts) ? "curve" : null;
            }
            else if (g is Curve curve)
            {
                kind = CurveFacts(curve, facts) ? "curve" : null;
            }
            else if (g is Autodesk.Revit.DB.Point point)
            {
                kind = "point"; Push(facts, point.Coord);
            }
            else if (g == null && owner is DatumPlane datum)
            {
                try
                {
                    IList<Curve> curves = datum.GetCurvesInView(DatumExtentType.Model, view);
                    if (curves != null && curves.Count > 0 && CurveFacts(curves[0], facts)) kind = "datum";
                }
                catch { kind = null; }
                if (kind == null && owner is ReferencePlane rp)
                {
                    try
                    {
                        kind = "reference_plane";
                        Push(facts, rp.BubbleEnd); Push(facts, rp.FreeEnd); Push(facts, rp.Normal);
                    }
                    catch { kind = null; facts.Clear(); }
                }
            }
            else if (g == null)
            {
                LocationCurve lc = null; LocationPoint lp = null;
                try { lc = owner.Location as LocationCurve; lp = owner.Location as LocationPoint; } catch { }
                if (lc != null && CurveFacts(lc.Curve, facts)) kind = "curve";
                else if (lp != null) { kind = "point"; try { Push(facts, lp.Point); } catch { kind = null; } }
            }

            if (kind == null || facts.Count == 0)
                throw new ArgumentException(field + ": the geometry behind this reference could not be read " +
                                            "(element " + Rid.Value(owner.Id) + ", " + owner.GetType().Name + ", " +
                                            refType + "), so the plan cannot fingerprint it and stale detection " +
                                            "could not be honest. Nothing was written.");
            return DimensionPlanRules.GeometryFingerprint(order, refType, kind, facts);
        }

        /// <summary>Endpoints for a bound curve; centre+radius for a full arc/circle.</summary>
        private static bool CurveFacts(Curve c, List<double> facts)
        {
            if (c == null) return false;
            try
            {
                if (c.IsBound) { Push(facts, c.GetEndPoint(0)); Push(facts, c.GetEndPoint(1)); return true; }
                var arc = c as Arc;
                if (arc != null) { Push(facts, arc.Center); facts.Add(arc.Radius); return true; }
            }
            catch { return false; }
            return false;
        }

        private static void Push(List<double> facts, XYZ v) { facts.Add(v.X); facts.Add(v.Y); facts.Add(v.Z); }

        /// <summary>
        /// The direction a reference contributes to an angular dimension, projected
        /// into the view plane. Grids, reference planes and line-based elements carry
        /// one; anything else refuses with the reason.
        /// </summary>
        private static XYZ DirectionOf(Document doc, Reference r, View view, int index)
        {
            Element el = null; try { el = doc.GetElement(r); } catch { el = null; }
            XYZ d = null;
            var grid = el as Grid;
            if (grid != null) { var gl = grid.Curve as Line; if (gl != null) d = gl.Direction; }
            if (d == null) { var rp = el as ReferencePlane; if (rp != null) d = rp.Direction; }
            if (d == null && el != null)
            {
                GeometryObject g = null;
                try { g = el.GetGeometryObjectFromReference(r); } catch { g = null; }
                var edge = g as Edge;
                if (edge != null) { var l = edge.AsCurve() as Line; if (l != null) d = l.Direction; }
                if (d == null) { var l2 = g as Line; if (l2 != null) d = l2.Direction; }
                if (d == null)
                {
                    var lc = el.Location as LocationCurve;
                    if (lc != null) { var l3 = lc.Curve as Line; if (l3 != null) d = l3.Direction; }
                }
            }
            if (d == null)
                throw new ArgumentException("references[" + index + "] carries no readable straight direction " +
                                            "(element " + (el == null ? "?" : Rid.Value(el.Id).ToString(CultureInfo.InvariantCulture)) +
                                            ", " + (el == null ? "?" : el.GetType().Name) + "). An angular dimension " +
                                            "needs direction-bearing references: walls, grids, reference planes or " +
                                            "line-based elements.");
            XYZ n = view.ViewDirection;
            XYZ projected = d.Subtract(n.Multiply(d.DotProduct(n)));
            if (projected.GetLength() < 1e-9)
                throw new ArgumentException("references[" + index + "] is perpendicular to this view's plane; its " +
                                            "direction has no angular meaning here.");
            return projected.Normalize();
        }

        /// <summary>
        /// The type the created dimension will actually carry: the explicit
        /// dimension_type_id where the operation takes one, otherwise the document's
        /// default for the operation's ElementTypeGroup, resolved and validated NOW so
        /// the plan can bind it - a default swapped between rehearsal and apply must
        /// read as a different plan. An irresoluble default is a refusal, not a shrug.
        /// </summary>
        private static DimensionType EffectiveDimensionType(Document doc, JObject a, string op, out bool fromDefault)
        {
            fromDefault = false;
            DimensionStyleType expected = ExpectedStyle(op);
            if (a["dimension_type_id"] != null && DimensionPlanRules.AllowsOption(op, "dimension_type_id"))
            {
                DimensionType explicitType = Need<DimensionType>(doc, a, "dimension_type_id");
                CheckStyle(explicitType, op, expected, "dimension_type_id");
                return explicitType;
            }
            fromDefault = true;
            ElementTypeGroup group = GroupOf(op);
            ElementId id = null;
            try { id = doc.GetDefaultElementTypeId(group); } catch { id = null; }
            DimensionType byDefault = (id == null || id == ElementId.InvalidElementId)
                ? null : doc.GetElement(id) as DimensionType;
            // The document's default TABLE can be wrong, and a real fixture proved it:
            // GetDefaultElementTypeId(ArcLengthDimensionType) answered a LINEAR type
            // ('Linear - 3/32" Arial') on a stock-derived model. A wrong default is not
            // the absence of capability - when any type of the RIGHT style exists in the
            // document, the lowest-id one is used deterministically, and the plan binds
            // THAT identity so a swap before the apply still reads as stale. Only a
            // document with no type of the style at all is a refusal.
            bool defaultUsable = byDefault != null && StyleMatches(byDefault, expected);
            if (!defaultUsable)
            {
                DimensionType fallback = null;
                try
                {
                    fallback = new FilteredElementCollector(doc)
                        .OfClass(typeof(DimensionType)).Cast<DimensionType>()
                        .Where(t => StyleMatches(t, expected))
                        .OrderBy(t => Rid.Value(t.Id))
                        .FirstOrDefault();
                }
                catch { fallback = null; }
                if (fallback != null) return fallback;
                throw new ArgumentException("this document has no DimensionType of style " + expected + " at all (" +
                                            "the default " + group + " resolved to " +
                                            (byDefault == null ? "nothing usable" : "'" + SafeName(byDefault) + "' of the wrong style") +
                                            ", and no type of the right style exists to fall back on), so '" + op +
                                            "' has nothing to create with. " +
                                            (DimensionPlanRules.AllowsOption(op, "dimension_type_id")
                                                ? "Pass dimension_type_id explicitly, or create a type of that style in Revit."
                                                : "Create a type of that style in Revit first."));
            }
            return byDefault;
        }

        private static bool StyleMatches(DimensionType t, DimensionStyleType expected)
        {
            try { return t.StyleType == expected; } catch { return false; }
        }

        private static void CheckStyle(DimensionType t, string op, DimensionStyleType expected, string source)
        {
            DimensionStyleType actual;
            try { actual = t.StyleType; }
            catch (Exception ex)
            {
                throw new ArgumentException(source + " resolved to '" + SafeName(t) + "' but its StyleType could " +
                                            "not be read: " + ex.Message);
            }
            if (actual != expected)
                throw new ArgumentException(source + " resolved to '" + SafeName(t) + "' whose StyleType is " +
                                            actual + ", but '" + op + "' needs " + expected + ". Pick a type of " +
                                            "the matching style.");
        }

        private static ElementTypeGroup GroupOf(string op)
        {
            switch (op)
            {
                case DimensionPlanRules.OpAngular: return ElementTypeGroup.AngularDimensionType;
                case DimensionPlanRules.OpRadial: return ElementTypeGroup.RadialDimensionType;
                case DimensionPlanRules.OpDiameter: return ElementTypeGroup.DiameterDimensionType;
                case DimensionPlanRules.OpArcLength: return ElementTypeGroup.ArcLengthDimensionType;
                case DimensionPlanRules.OpSpotElevation: return ElementTypeGroup.SpotElevationType;
                case DimensionPlanRules.OpSpotCoordinate: return ElementTypeGroup.SpotCoordinateType;
                default: return ElementTypeGroup.LinearDimensionType;
            }
        }

        private static DimensionStyleType ExpectedStyle(string op)
        {
            switch (op)
            {
                case DimensionPlanRules.OpAngular: return DimensionStyleType.Angular;
                case DimensionPlanRules.OpRadial: return DimensionStyleType.Radial;
                case DimensionPlanRules.OpDiameter: return DimensionStyleType.Diameter;
                case DimensionPlanRules.OpArcLength: return DimensionStyleType.ArcLength;
                case DimensionPlanRules.OpSpotElevation: return DimensionStyleType.SpotElevation;
                case DimensionPlanRules.OpSpotCoordinate: return DimensionStyleType.SpotCoordinate;
                default: return DimensionStyleType.Linear;
            }
        }

        // ---------------------------------------------------------------------
        // Creation.
        // ---------------------------------------------------------------------
        private static Element Create(Document doc, Plan p)
        {
            if (p.Operation == "text") return TextNote.Create(doc, p.View.Id, p.Point, p.Text, p.Type.Id);
            if (p.Operation == "tag")
            {
                TagMode mode = TagMode.TM_ADDBY_CATEGORY; string m = (p.Input.Value<string>("tag_mode") ?? "by_category").ToLowerInvariant();
                if (m == "multi_category") mode = TagMode.TM_ADDBY_MULTICATEGORY; else if (m == "material") mode = TagMode.TM_ADDBY_MATERIAL;
                TagOrientation orientation = (p.Input.Value<string>("orientation") ?? "horizontal").ToLowerInvariant() == "vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal;
                IndependentTag tag = IndependentTag.Create(doc, p.View.Id, new Reference(p.Target), p.Input.Value<bool?>("add_leader") == true, mode, orientation, p.Point);
                if (p.Type != null)
                {
                    ICollection<ElementId> valid = tag.GetValidTypes();
                    if (valid == null || !valid.Contains(p.Type.Id))
                        throw new InvalidOperationException("tag_type_id " + Rid.Value(p.Type.Id) +
                            " is not valid for the tag Revit created for element " + Rid.Value(p.Target.Id) + ".");
                    tag.ChangeTypeId(p.Type.Id);
                }
                return tag;
            }
            switch (p.Operation)
            {
                case DimensionPlanRules.OpDimension:
                    return doc.Create.NewDimension(p.View, p.Line, p.References, p.EffectiveType);
                case DimensionPlanRules.OpAngular:
                    return AngularDimension.Create(doc, p.View, p.Arc, p.RefList, p.EffectiveType);
#if !REVIT2023 && !REVIT2024
                case DimensionPlanRules.OpRadial:
                    return RadialDimension.Create(doc, p.View, p.ArcReference, false);
                case DimensionPlanRules.OpDiameter:
                    return RadialDimension.Create(doc, p.View, p.ArcReference, true);
                case DimensionPlanRules.OpArcLength:
                    // RefList carries the two endpoint references first and the measured
                    // arc's own reference last (AddReference order); the API wants them
                    // apart.
                    return ArcLengthDimension.Create(doc, p.View, p.Arc, p.ArcReference, p.RefList.GetRange(0, 2));
#endif
                case DimensionPlanRules.OpSpotElevation:
                    return doc.Create.NewSpotElevation(p.View, p.RefList[0], p.SpotOrigin, p.SpotBend, p.SpotEnd,
                                                       p.SpotOrigin, p.SpotLeader);
                case DimensionPlanRules.OpSpotCoordinate:
                    return doc.Create.NewSpotCoordinate(p.View, p.RefList[0], p.SpotOrigin, p.SpotBend, p.SpotEnd,
                                                        p.SpotOrigin, p.SpotLeader);
                default:
                    // Unreachable: PlanAction refused everything else before any
                    // transaction. Kept as a throw so that if it ever stops being
                    // unreachable the failure is loud instead of a null write.
                    throw new InvalidOperationException("operation '" + p.Operation + "' reached Create() without " +
                                                        "a creation path; planning should have refused it.");
            }
        }

        /// <summary>Apply the requested overrides/EQ/lock to a freshly created dimension.</summary>
        private static void Decorate(Dimension d, Plan p)
        {
            if (p.HasPrefix) d.Prefix = p.Prefix;
            if (p.HasSuffix) d.Suffix = p.Suffix;
            if (p.HasAbove) d.Above = p.Above;
            if (p.HasBelow) d.Below = p.Below;
            if (p.HasValueOverride) d.ValueOverride = p.ValueOverride;
            if (p.Eq.HasValue) d.AreSegmentsEqual = p.Eq.Value;
            if (p.Lock.HasValue) d.IsLocked = p.Lock.Value;
        }

        // ---------------------------------------------------------------------
        // The rehearsal: provisional creation, measurement, MANDATORY rollback.
        // ---------------------------------------------------------------------
        private Rehearsal Rehearse(Document doc, List<Plan> plans)
        {
            var re = new Rehearsal();
            int failedIndex = -1; string failedWhy = null, regenWhy = null;
            var attempted = new HashSet<int>();
            using (var tx = new Transaction(doc, "Horizun: rehearse annotate"))
            {
                tx.Start();
                var opts = tx.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new SilenceWarnings());
                opts.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(opts);

                foreach (Plan p in plans)
                {
                    attempted.Add(p.Index);
                    try
                    {
                        Element e = Create(doc, p);
                        if (e == null) { failedIndex = p.Index; failedWhy = "Revit returned no element"; break; }
                        p.Created = e.Id;
                        if (p.Operation == "tag") p.EffectiveTagType = doc.GetElement(e.GetTypeId()) as ElementType;
                        Dimension d = e as Dimension; if (d != null) Decorate(d, p);
                    }
                    catch (Exception ex) { failedIndex = p.Index; failedWhy = ex.Message; break; }
                }
                if (failedIndex < 0)
                {
                    try { doc.Regenerate(); } catch (Exception ex) { regenWhy = ex.Message; }
                }
                // Measure INSIDE the transaction - the mandatory rollback below erases
                // the evidence a moment later.
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
                    else if (DimensionPlanRules.IsDimensionOperation(p.Operation))
                    {
                        Element e = p.Created == null ? null : doc.GetElement(p.Created);
                        Measured m = Measure(doc, p, e);
                        p.Rehearsed = m;
                        bool ok; JObject verification = BuildChecks(p, m, m, VerifyStage.Rehearsal, out ok);
                        constructible = ok;
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
                        // The expected_value postcondition is deliberately NOT part of
                        // constructibility: it belongs to the APPLY, where its failure
                        // rolls the real batch back. Here it is previewed as evidence.
                        if (p.ExpectedValueFeet.HasValue)
                        {
                            double? total = m.Total;
                            bool materialised = m.ReferencesAvailable == true && total.HasValue;
                            row["expected_value_preview"] = new JObject
                            {
                                // The *_internal_feet names hold RADIANS for an angular
                                // dimension - Revit's internal angle unit - so the unit is
                                // said out loud instead of letting the field name lie.
                                ["unit"] = p.Operation == DimensionPlanRules.OpAngular ? "radians" : "feet",
                                ["expected_internal_feet"] = p.ExpectedValueFeet.Value,
                                ["tolerance_internal_feet"] = p.ExpectedToleranceFeet,
                                ["measured_internal_feet"] = materialised ? (JToken)total.Value : JValue.CreateNull(),
                                // Null, not false, when Revit has not materialised the
                                // value yet: pre-commit a correct dimension measures 0,
                                // and previewing that as "would fail" reads like a
                                // verdict. The postcondition itself runs post-commit.
                                ["would_pass"] = materialised
                                    ? (JToken)(Math.Abs(total.Value - p.ExpectedValueFeet.Value) <= p.ExpectedToleranceFeet)
                                    : JValue.CreateNull(),
                                ["note"] = materialised
                                    ? null
                                    : "Revit materialises dimension values at commit; the postcondition is " +
                                      "checked post-commit inside the still-open TransactionGroup."
                            };
                        }
                    }
                    else
                    {
                        Element e = p.Created == null ? null : doc.GetElement(p.Created);
                        constructible = Verify(p, e);
                        row = new JObject
                        {
                            ["index"] = p.Index, ["operation"] = p.Operation,
                            ["constructible"] = constructible,
                            ["reason"] = constructible ? null : "created, but the annotation would not verify " +
                                                                "(see this command's text/tag verification)"
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
            foreach (Plan p in plans) p.Created = null;
            re.AllConstructible = re.NotConstructibleCount == 0;
            return re;
        }

        // ---------------------------------------------------------------------
        // Measurement and verification.
        // ---------------------------------------------------------------------

        /// <summary>Every fact the verification compares, read guarded off one dimension.</summary>
        private static Measured Measure(Document doc, Plan p, Element e)
        {
            var m = new Measured();
            if (e == null) { m.Unreadable = "the created element could not be re-read from the document"; return m; }
            m.ClassName = e.GetType().Name;
            // The class check is by HIERARCHY, not by string: Revit 2025 answers a
            // two-grid NewDimension with a LinearDimension, which IS a Dimension -
            // measured live 2026-08-24, where the exact-name comparison refused a
            // perfectly correct dimension for wearing its subclass's name.
            m.ClassMatches = ClassMatches(p.Operation, e);
            m.ElementId = Rid.Value(e.Id);
            try { m.UniqueId = e.UniqueId; } catch (Exception ex) { Note(m, "UniqueId: " + ex.Message); }
            var dim = e as Dimension;
            if (dim == null) { Note(m, "the created element is a " + m.ClassName + ", not a Dimension"); return m; }

            try { m.OwnerViewId = Rid.Value(dim.OwnerViewId); } catch (Exception ex) { Note(m, "OwnerViewId: " + ex.Message); }
            try { m.TypeId = Rid.Value(dim.GetTypeId()); } catch (Exception ex) { Note(m, "GetTypeId: " + ex.Message); }
            try { m.Shape = dim.DimensionShape.ToString(); } catch (Exception ex) { Note(m, "DimensionShape: " + ex.Message); }
            try { m.ReferencesAvailable = dim.AreReferencesAvailable; } catch (Exception ex) { Note(m, "AreReferencesAvailable: " + ex.Message); }
            try
            {
                m.RefReps = new List<string>();
                ReferenceArray refs = dim.References;
                if (refs == null) Note(m, "References returned null");
                else foreach (Reference r in refs)
                    m.RefReps.Add(r == null ? "<null>" : r.ConvertToStableRepresentation(doc));
            }
            catch (Exception ex) { Note(m, "References: " + ex.Message); }
            try { m.SegmentCount = dim.NumberOfSegments; } catch (Exception ex) { Note(m, "NumberOfSegments: " + ex.Message); }
            try { m.SingleValue = dim.Value; } catch (Exception ex) { Note(m, "Value: " + ex.Message); }
            try
            {
                m.SegmentValues = new List<double>();
                var presentedSegments = new List<string>();
                foreach (DimensionSegment s in dim.Segments)
                {
                    double? v = s.Value;
                    if (v.HasValue) m.SegmentValues.Add(v.Value);
                    // Not Unreadable: pre-commit Revit legitimately has no segment
                    // values yet. Recorded as a COUNT so the post-commit stage - where
                    // absence really is a defect - can enforce it.
                    else m.SegmentValuesMissing++;
                    try { presentedSegments.Add(s.ValueString ?? ""); } catch { presentedSegments.Add("<unreadable>"); }
                }
                if (presentedSegments.Count > 0) m.Presented = string.Join("; ", presentedSegments);
            }
            catch (Exception ex) { Note(m, "Segments: " + ex.Message); }
            if (m.Presented == null)
            {
                try { m.Presented = dim.ValueString; } catch { m.Presented = null; }
            }

            if (p.Operation == DimensionPlanRules.OpDimension)
            {
                try
                {
                    var line = dim.Curve as Line;
                    if (line == null) Note(m, "Curve is not a Line");
                    else
                    {
                        m.CurveRead = true; m.CurveBound = line.IsBound;
                        if (line.IsBound) { m.CurveStart = Arr(line.GetEndPoint(0)); m.CurveEnd = Arr(line.GetEndPoint(1)); }
                        else { m.CurveOrigin = Arr(line.Origin); m.CurveDirection = Arr(line.Direction); }
                    }
                }
                catch (Exception ex) { Note(m, "Curve: " + ex.Message); }
            }
            else if (p.Operation == DimensionPlanRules.OpAngular)
            {
                try
                {
                    var arc = dim.Curve as Arc;
                    if (arc == null) Note(m, "Curve is not an Arc");
                    else { m.CurveRead = true; m.ArcCenter = Arr(arc.Center); m.ArcRadius = arc.Radius; }
                }
                catch (Exception ex) { Note(m, "Curve: " + ex.Message); }
            }
            else if (p.Operation == DimensionPlanRules.OpSpotElevation ||
                     p.Operation == DimensionPlanRules.OpSpotCoordinate)
            {
                try { m.SpotOrigin = Arr(dim.Origin); } catch (Exception ex) { Note(m, "Origin: " + ex.Message); }
            }

            if (p.HasPrefix) try { m.Prefix = dim.Prefix; } catch (Exception ex) { Note(m, "Prefix: " + ex.Message); }
            if (p.HasSuffix) try { m.Suffix = dim.Suffix; } catch (Exception ex) { Note(m, "Suffix: " + ex.Message); }
            if (p.HasAbove) try { m.Above = dim.Above; } catch (Exception ex) { Note(m, "Above: " + ex.Message); }
            if (p.HasBelow) try { m.Below = dim.Below; } catch (Exception ex) { Note(m, "Below: " + ex.Message); }
            if (p.HasValueOverride) try { m.ValueOverride = dim.ValueOverride; } catch (Exception ex) { Note(m, "ValueOverride: " + ex.Message); }
            if (p.Eq.HasValue) try { m.Eq = dim.AreSegmentsEqual; } catch (Exception ex) { Note(m, "AreSegmentsEqual: " + ex.Message); }
            if (p.Lock.HasValue) try { m.Locked = dim.IsLocked; } catch (Exception ex) { Note(m, "IsLocked: " + ex.Message); }
            return m;
        }

        private static void Note(Measured m, string what) { if (m.Unreadable == null) m.Unreadable = what; }

        /// <summary>
        /// Every comparison the contract demands, as requested / read / match rows.
        /// `baseline` is what THIS call's rehearsal measured - the deterministic answer
        /// the same model gives twice - so facts Revit normalises on creation (arc
        /// rebasing, reference storage for the API-shaped operations, spot origins) are
        /// held against the rehearsed measurement, while everything the caller stated
        /// (references for linear/angular, the line, overrides, the type, the view) is
        /// held against the REQUEST. In the rehearsal itself baseline == measured, so
        /// the baseline rows pass trivially and the request rows do the work.
        /// </summary>
        /// <summary>
        /// WHEN a fact may be demanded. Measured live on Revit 2025 (2026-08-24): a
        /// dimension inside a still-open transaction reports AreReferencesAvailable
        /// false and Value 0 even after Regenerate - Revit materialises both AT COMMIT.
        /// Demanding them earlier refused every correct dimension; not demanding them
        /// at all would let a broken one through. So the checks are staged: identity
        /// and geometry always; availability, values and the caller's expected_value
        /// only post-commit - where the TransactionGroup is still open, so a failure
        /// still rolls the WHOLE batch back.
        /// </summary>
        private enum VerifyStage { Rehearsal, InTransaction, PostCommit }

        private static JObject BuildChecks(Plan p, Measured m, Measured baseline, VerifyStage stage, out bool allOk)
        {
            var checks = new JArray();
            bool ok = true;
            bool deferAvailability = false;
            Action<string, JToken, JToken, bool> add = null;
            add = (field, requested, read, match) =>
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
            double tol = DimensionPlanRules.CurveToleranceFeet;
            string op = p.Operation;

            string expectedClass = ExpectedClassName(op);
            add("class", expectedClass + " (or a subclass)", m.ClassName, m.ClassMatches);
            string expectedShape = ExpectedShapeName(op);
            add("shape", expectedShape, m.Shape, string.Equals(m.Shape, expectedShape, StringComparison.Ordinal));

            long viewId = Rid.Value(p.View.Id);
            add("owner_view_id", viewId, m.OwnerViewId.HasValue ? (JToken)m.OwnerViewId.Value : null,
                m.OwnerViewId.HasValue && m.OwnerViewId.Value == viewId);

            long typeId = Rid.Value(p.EffectiveType.Id);
            add("dimension_type_id", typeId, m.TypeId.HasValue ? (JToken)m.TypeId.Value : null,
                m.TypeId.HasValue && m.TypeId.Value == typeId);

            if (stage == VerifyStage.PostCommit)
            {
                // Deferred to the END of the checks: measured three times now (the
                // reopened RFA, and radial/diameter and spot dimensions whose
                // references live in FAMILY-INSTANCE geometry), Revit computes this
                // flag lazily - it can read false post-commit on a dimension whose
                // MEASURED VALUE is exactly right. So the flag alone may not fail a
                // row whose every substantive check - class, view, type, references
                // by owner and order, segments, curve, values, the caller's
                // expected_value - passed; the row then says the verification stood
                // on substance, not on the flag. A false flag WITH failing substance
                // still fails.
                deferAvailability = true;
            }
            else
                // Observed, not demanded: Revit materialises reference availability at
                // commit, so inside an open transaction false is the normal reading of
                // a correct dimension. The value travels so nobody has to trust this
                // sentence; the post-commit stage is where false becomes a failure.
                checks.Add(new JObject
                {
                    ["field"] = "references_available_observed",
                    ["requested"] = "enforced post-commit; Revit materialises this at commit",
                    ["read"] = m.ReferencesAvailable.HasValue ? (JToken)m.ReferencesAvailable.Value : JValue.CreateNull(),
                    ["match"] = true
                });

            // References: the caller-stated operations are held to the caller's list;
            // the API-shaped ones (radial/diameter/arc length/spots, whose stored
            // reference set is the API's business) are held to the rehearsed baseline.
            bool vsRequested = op == DimensionPlanRules.OpDimension || op == DimensionPlanRules.OpAngular;
            List<string> wanted = vsRequested ? p.RefReserialized : baseline?.RefReps;
            int? readCount = m.RefReps == null ? (int?)null : m.RefReps.Count;
            add("references_count",
                wanted == null ? null : (JToken)wanted.Count, readCount.HasValue ? (JToken)readCount.Value : null,
                wanted != null && readCount.HasValue && readCount.Value == wanted.Count);
            // By OWNER, not by exact string, when held against the request: Revit
            // canonicalises a bare element reference on storage - a grid handed in as
            // 'uid' reads back as 'uid:0:SURFACE' (measured live, 2025) - and the same
            // element under two spellings is the same reference. The read-vs-rehearsed
            // ORDER check below stays exact-string: both sides of it went through the
            // same canonicalisation.
            bool setMatch;
            if (vsRequested)
                setMatch = wanted != null && m.RefReps != null &&
                           DimensionPlanRules.ReferenceOwnerKeys(wanted)
                               .SequenceEqual(DimensionPlanRules.ReferenceOwnerKeys(m.RefReps), StringComparer.Ordinal);
            else
                setMatch = wanted != null && m.RefReps != null &&
                           wanted.OrderBy(s => s, StringComparer.Ordinal)
                                 .SequenceEqual(m.RefReps.OrderBy(s => s, StringComparer.Ordinal), StringComparer.Ordinal);
            add(vsRequested ? "references_owners_vs_requested" : "references_set_vs_rehearsed",
                wanted == null ? null : new JArray(wanted), m.RefReps == null ? null : new JArray(m.RefReps), setMatch);
            bool orderMatch = baseline?.RefReps != null && m.RefReps != null &&
                              baseline.RefReps.SequenceEqual(m.RefReps, StringComparer.Ordinal);
            add("references_order_vs_rehearsed",
                baseline?.RefReps == null ? null : new JArray(baseline.RefReps),
                m.RefReps == null ? null : new JArray(m.RefReps), orderMatch);

            // Segments: the caller-shaped operations have an arithmetic expectation;
            // the rest are held to the rehearsed count.
            if (op == DimensionPlanRules.OpDimension)
            {
                int expectedSegments = DimensionPlanRules.ExpectedSegmentCount(p.RefList.Count);
                add("segments", expectedSegments, m.SegmentCount.HasValue ? (JToken)m.SegmentCount.Value : null,
                    m.SegmentCount.HasValue && m.SegmentCount.Value == expectedSegments);
            }
            else if (op != DimensionPlanRules.OpSpotElevation && op != DimensionPlanRules.OpSpotCoordinate)
            {
                add("segments_vs_rehearsed",
                    baseline?.SegmentCount == null ? null : (JToken)baseline.SegmentCount.Value,
                    m.SegmentCount.HasValue ? (JToken)m.SegmentCount.Value : null,
                    baseline?.SegmentCount != null && m.SegmentCount.HasValue &&
                    baseline.SegmentCount.Value == m.SegmentCount.Value);
            }

            // The measured value, against what THIS call rehearsed. The same model
            // must measure the same; a difference means it moved mid-call.
            bool isSpot = op == DimensionPlanRules.OpSpotElevation || op == DimensionPlanRules.OpSpotCoordinate;
            if (!isSpot)
            {
                // The rehearsal's value only binds when the rehearsal could MEASURE one:
                // pre-commit Revit reports availability false and value 0 for a correct
                // dimension, and holding the committed value to that 0 would refuse
                // every success. When the baseline never materialised, the enforced
                // facts are the post-commit ones: availability above, segment presence
                // below, and the caller's expected_value when one was stated.
                bool baselineMaterialised = baseline != null && baseline.ReferencesAvailable == true &&
                                            baseline.Total.HasValue;
                double? want = baseline?.Total; double? got = m.Total;
                string valueField = op == DimensionPlanRules.OpAngular ? "value_internal_radians_vs_rehearsed"
                                                                       : "value_internal_feet_vs_rehearsed";
                if (baselineMaterialised)
                    add(valueField,
                        want.HasValue ? (JToken)want.Value : null, got.HasValue ? (JToken)got.Value : null,
                        want.HasValue && got.HasValue && Math.Abs(want.Value - got.Value) <= tol);
                else
                    checks.Add(new JObject
                    {
                        ["field"] = "value_first_materialised",
                        ["requested"] = "no rehearsal value to hold this to: Revit materialises values at commit",
                        ["read"] = got.HasValue ? (JToken)got.Value : JValue.CreateNull(),
                        ["match"] = true
                    });
                if (baselineMaterialised && baseline.SegmentValues != null && baseline.SegmentValues.Count > 0)
                {
                    bool segMatch = m.SegmentValues != null &&
                                    m.SegmentValues.Count == baseline.SegmentValues.Count;
                    if (segMatch)
                        for (int i = 0; i < m.SegmentValues.Count; i++)
                            if (Math.Abs(m.SegmentValues[i] - baseline.SegmentValues[i]) > tol) { segMatch = false; break; }
                    add("segment_values_vs_rehearsed",
                        new JArray(baseline.SegmentValues),
                        m.SegmentValues == null ? null : new JArray(m.SegmentValues), segMatch);
                }
                // Post-commit, a chain must actually CARRY its values: a committed
                // dimension whose segments still report none is broken, not early.
                if (stage == VerifyStage.PostCommit && m.SegmentCount.HasValue && m.SegmentCount.Value > 0)
                    add("segment_values_present", m.SegmentCount.Value,
                        m.SegmentValues == null ? 0 : m.SegmentValues.Count,
                        m.SegmentValuesMissing == 0 && m.SegmentValues != null &&
                        m.SegmentValues.Count == m.SegmentCount.Value);
            }

            // Geometry.
            if (op == DimensionPlanRules.OpDimension)
            {
                double[] reqStart = Arr(p.Line.GetEndPoint(0)), reqEnd = Arr(p.Line.GetEndPoint(1));
                bool lineOk;
                string readDescription;
                if (!m.CurveRead) { lineOk = false; readDescription = "(curve unreadable)"; }
                else if (m.CurveBound)
                {
                    lineOk = DimensionPlanRules.SameEndpoints(reqStart, reqEnd, m.CurveStart, m.CurveEnd, tol)
                          || (DimensionPlanRules.PointOnLine(m.CurveStart, Delta(m.CurveEnd, m.CurveStart), reqStart, tol) &&
                              DimensionPlanRules.PointOnLine(m.CurveStart, Delta(m.CurveEnd, m.CurveStart), reqEnd, tol));
                    readDescription = "bound " + Show(m.CurveStart) + " -> " + Show(m.CurveEnd);
                }
                else
                {
                    lineOk = DimensionPlanRules.PointOnLine(m.CurveOrigin, m.CurveDirection, reqStart, tol) &&
                             DimensionPlanRules.PointOnLine(m.CurveOrigin, m.CurveDirection, reqEnd, tol);
                    readDescription = "unbound through " + Show(m.CurveOrigin) + " along " + Show(m.CurveDirection);
                }
                add("line_carries_requested_endpoints", Show(reqStart) + " -> " + Show(reqEnd), readDescription, lineOk);
            }
            else if (op == DimensionPlanRules.OpAngular)
            {
                add("arc_center_vs_rehearsed",
                    baseline?.ArcCenter == null ? null : (JToken)Show(baseline.ArcCenter),
                    m.ArcCenter == null ? null : (JToken)Show(m.ArcCenter),
                    DimensionPlanRules.SamePoint(baseline?.ArcCenter, m.ArcCenter, tol));
                add("arc_radius_vs_rehearsed",
                    baseline?.ArcRadius == null ? null : (JToken)baseline.ArcRadius.Value,
                    m.ArcRadius.HasValue ? (JToken)m.ArcRadius.Value : null,
                    baseline?.ArcRadius != null && m.ArcRadius.HasValue &&
                    Math.Abs(baseline.ArcRadius.Value - m.ArcRadius.Value) <= tol);
            }
            else if (isSpot)
            {
                add("spot_origin_vs_rehearsed",
                    baseline?.SpotOrigin == null ? null : (JToken)Show(baseline.SpotOrigin),
                    m.SpotOrigin == null ? null : (JToken)Show(m.SpotOrigin),
                    DimensionPlanRules.SamePoint(baseline?.SpotOrigin, m.SpotOrigin, tol));
            }

            // Overrides, EQ and lock: only re-read where they were requested.
            if (p.HasPrefix) add("prefix", p.Prefix, m.Prefix, string.Equals(p.Prefix, m.Prefix ?? "", StringComparison.Ordinal));
            if (p.HasSuffix) add("suffix", p.Suffix, m.Suffix, string.Equals(p.Suffix, m.Suffix ?? "", StringComparison.Ordinal));
            if (p.HasAbove) add("above", p.Above, m.Above, string.Equals(p.Above, m.Above ?? "", StringComparison.Ordinal));
            if (p.HasBelow) add("below", p.Below, m.Below, string.Equals(p.Below, m.Below ?? "", StringComparison.Ordinal));
            if (p.HasValueOverride) add("value_override", p.ValueOverride, m.ValueOverride,
                                        string.Equals(p.ValueOverride, m.ValueOverride ?? "", StringComparison.Ordinal));
            if (p.Eq.HasValue) add("eq", p.Eq.Value, m.Eq.HasValue ? (JToken)m.Eq.Value : null, m.Eq == p.Eq);
            if (p.Lock.HasValue) add("lock", p.Lock.Value, m.Locked.HasValue ? (JToken)m.Locked.Value : null, m.Locked == p.Lock);

            // The caller's own postcondition. Only POST-COMMIT: that is where Revit has
            // materialised the value, and the TransactionGroup is still open there, so
            // a miss rolls the whole batch back exactly as designed.
            if (stage == VerifyStage.PostCommit && p.ExpectedValueFeet.HasValue)
            {
                double? total = m.Total;
                add("expected_value",
                    // *_internal_feet holds RADIANS for angular - named, never implied.
                    new JObject { ["unit"] = op == DimensionPlanRules.OpAngular ? "radians" : "feet",
                                  ["value_internal_feet"] = p.ExpectedValueFeet.Value,
                                  ["tolerance_internal_feet"] = p.ExpectedToleranceFeet },
                    total.HasValue ? (JToken)total.Value : null,
                    total.HasValue && Math.Abs(total.Value - p.ExpectedValueFeet.Value) <= p.ExpectedToleranceFeet);
            }

            // A fact that could not be read poisons the whole row: "we could not look"
            // must never add up to "it matches".
            if (m.Unreadable != null)
                add("readable", "every field this operation verifies", m.Unreadable, false);

            if (deferAvailability)
            {
                bool flagTrue = m.ReferencesAvailable == true;
                bool substanceOk = ok; // every check so far, availability excluded
                var availability = new JObject
                {
                    ["field"] = "references_available",
                    ["requested"] = true,
                    ["read"] = m.ReferencesAvailable.HasValue ? (JToken)m.ReferencesAvailable.Value : JValue.CreateNull(),
                    ["match"] = flagTrue ? (JToken)true : (substanceOk ? JValue.CreateNull() : (JToken)false),
                    ["verified_by"] = flagTrue ? "flag" : (substanceOk ? "substance" : "flag")
                };
                if (!flagTrue && substanceOk)
                    availability["note"] =
                        "Revit computes this flag lazily and can read it false post-commit on a dimension whose " +
                        "measured value is exactly right (measured live on instance-geometry references and on " +
                        "reopened RFAs). Every substantive check in this row passed, and the verification stands " +
                        "on that substance; the flag is reported as observed, not used as the verdict.";
                checks.Add(availability);
                if (!flagTrue && !substanceOk) ok = false;
            }

            allOk = ok;
            return new JObject
            {
                ["comparison_tolerance_feet"] = tol,
                ["all_match"] = ok,
                ["checks"] = checks
            };
        }

        /// <summary>One response row for an apply phase - legacy fields kept, new ones added.</summary>
        private JObject VerifyRow(Document doc, Plan p, VerifyStage stage, out bool ok)
        {
            Element e = p.Created == null ? null : doc.GetElement(p.Created);
            if (!DimensionPlanRules.IsDimensionOperation(p.Operation))
            {
                ok = Verify(p, e);
                var legacy = new JObject
                {
                    ["index"] = p.Index, ["operation"] = p.Operation,
                    ["element_id"] = p.Created == null ? JValue.CreateNull() : new JValue(Rid.Value(p.Created)),
                    ["verified"] = ok
                };
                if (e != null) { try { legacy["unique_id"] = e.UniqueId; } catch { legacy["unique_id"] = null; } }
                return legacy;
            }
            Measured m = Measure(doc, p, e);
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
            var row = new JObject
            {
                ["index"] = p.Index, ["operation"] = p.Operation,
                ["element_id"] = p.Created == null ? JValue.CreateNull() : new JValue(Rid.Value(p.Created)),
                ["verified"] = ok,
                ["unique_id"] = m.UniqueId,
                ["class"] = m.ClassName,
                ["shape"] = m.Shape,
                ["values"] = Values(m),
                ["verification"] = verification
            };
            return row;
        }

        private static JObject Values(Measured m)
        {
            double? total = m.Total;
            return new JObject
            {
                ["internal_feet"] = total.HasValue ? (JToken)total.Value : JValue.CreateNull(),
                ["per_segment_feet"] = m.SegmentValues == null ? (JToken)JValue.CreateNull() : new JArray(m.SegmentValues),
                ["presented"] = m.Presented,
                ["internal_unit"] = "feet"
            };
        }

        private static JObject Evidence(Measured m)
        {
            var o = new JObject
            {
                ["class"] = m.ClassName,
                ["shape"] = m.Shape,
                ["values"] = Values(m),
                ["segments"] = m.SegmentCount.HasValue ? (JToken)m.SegmentCount.Value : JValue.CreateNull(),
                ["references_read"] = m.RefReps == null ? (JToken)JValue.CreateNull() : new JArray(m.RefReps),
                ["references_available"] = m.ReferencesAvailable.HasValue ? (JToken)m.ReferencesAvailable.Value : JValue.CreateNull()
            };
            if (m.SpotOrigin != null) o["spot_origin_feet"] = Show(m.SpotOrigin);
            if (m.ArcCenter != null) { o["arc_center_feet"] = Show(m.ArcCenter); o["arc_radius_feet"] = m.ArcRadius; }
            if (m.Unreadable != null) o["unreadable"] = m.Unreadable;
            return o;
        }

        private static string ExpectedClassName(string op)
        {
            switch (op)
            {
                case DimensionPlanRules.OpAngular: return "AngularDimension";
                case DimensionPlanRules.OpRadial:
                case DimensionPlanRules.OpDiameter: return "RadialDimension";
                case DimensionPlanRules.OpArcLength: return "ArcLengthDimension";
                case DimensionPlanRules.OpSpotElevation:
                case DimensionPlanRules.OpSpotCoordinate: return "SpotDimension";
                default: return "Dimension";
            }
        }

        /// <summary>
        /// By HIERARCHY: Revit is free to answer with a subclass (a two-grid
        /// NewDimension on 2025 returns LinearDimension), and a subclass of the right
        /// base is the right answer. The SHAPE check beside this one is what separates
        /// the Dimension-derived forms from each other.
        /// </summary>
        private static bool ClassMatches(string op, Element e)
        {
            switch (op)
            {
                case DimensionPlanRules.OpAngular: return e is AngularDimension;
#if !REVIT2023 && !REVIT2024
                case DimensionPlanRules.OpRadial:
                case DimensionPlanRules.OpDiameter: return e is RadialDimension;
                case DimensionPlanRules.OpArcLength: return e is ArcLengthDimension;
#endif
                case DimensionPlanRules.OpSpotElevation:
                case DimensionPlanRules.OpSpotCoordinate: return e is SpotDimension;
                default: return e is Dimension && !(e is SpotDimension);
            }
        }

        private static string ExpectedShapeName(string op)
        {
            switch (op)
            {
                case DimensionPlanRules.OpAngular: return "Angular";
                case DimensionPlanRules.OpRadial: return "Radial";
                case DimensionPlanRules.OpDiameter: return "Diameter";
                case DimensionPlanRules.OpArcLength: return "ArcLength";
                case DimensionPlanRules.OpSpotElevation:
                case DimensionPlanRules.OpSpotCoordinate: return "Spot";
                default: return "Linear";
            }
        }

        private static bool Verify(Plan p, Element e)
        {
            // Revit re-encodes a note's line endings and appends a terminating '\r'
            // (measured on 2023: 'D7_PROBE' reads back 'D7_PROBE\r'), so the comparison
            // is on the normalised text - the rule and its evidence live in
            // DimensionPlanRules.StoredTextMatches, where they are unit-tested.
            if (p.Operation == "text") return e is TextNote note && DimensionPlanRules.StoredTextMatches(p.Text, note.Text);
            if (p.Operation == "tag")
            {
                IndependentTag tag = e as IndependentTag;
                if (tag == null || !tag.GetTaggedLocalElementIds().Any(id => id == p.Target.Id)) return false;
                Element expected = p.Type ?? p.EffectiveTagType;
                return expected == null || tag.GetTypeId() == expected.Id;
            }
            return e is Dimension dimension && dimension.References != null && dimension.References.Size == p.References.Size;
        }

        // ---------------------------------------------------------------------
        // The materialised plan rows and the dry-run plan rows.
        // ---------------------------------------------------------------------
        private PlannedElement PlannedRow(Plan planned)
        {
            if (!DimensionPlanRules.IsDimensionOperation(planned.Operation))
            {
                return new PlannedElement
                {
                    UniqueId = "action:" + planned.Index,
                    Category = planned.Operation,
                    Action = PlannedAction.Create,
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "view", SafePlanIdName(planned.View) },
                        { "target", SafePlanIdName(planned.Target) },
                        { "type", SafePlanIdName(planned.Operation == "tag" ? (planned.Type ?? planned.EffectiveTagType) : planned.Type) },
                        { "existing_tags_for_target_in_view", planned.ExistingTagCount.ToString(CultureInfo.InvariantCulture) },
                        { "references", planned.References == null ? "" :
                              planned.References.Size.ToString(CultureInfo.InvariantCulture) }
                    }
                };
            }
            var before = new Dictionary<string, string>
            {
                { "view", SafePlanIdName(planned.View) },
                { "type", SafePlanIdName(planned.EffectiveType) },
                { "type_source", planned.TypeFromDefault ? "default" : "explicit" },
                { "references", planned.RefList.Count.ToString(CultureInfo.InvariantCulture) }
            };
            for (int i = 0; i < planned.RefList.Count; i++)
                before["ref." + i.ToString(CultureInfo.InvariantCulture)] =
                    planned.RefReserialized[i] + "|" + planned.RefOwnerUids[i] + "|" +
                    planned.RefTypes[i] + "|" + planned.RefFingerprints[i];
            if (planned.Line != null)
                before["line"] = Canon(planned.Line.GetEndPoint(0)) + ";" + Canon(planned.Line.GetEndPoint(1));
            if (planned.ArcCenter != null)
                before["arc"] = Canon(planned.ArcCenter) + ";" + DimensionPlanRules.CanonicalFeet(planned.ArcRadius);
            if (planned.SpotOrigin != null)
            {
                before["point"] = Canon(planned.SpotOrigin);
                before["bend"] = Canon(planned.SpotBend);
                before["end"] = Canon(planned.SpotEnd);
                before["leader"] = planned.SpotLeader ? "true" : "false";
            }
            before["measured"] = MeasuredCanonical(planned.Rehearsed);
            return new PlannedElement
            {
                UniqueId = "action:" + planned.Index,
                Category = planned.Operation,
                Action = PlannedAction.Create,
                GeometryFingerprint = DimensionPlanRules.CombineFingerprints(planned.RefFingerprints),
                BeforeValues = before
            };
        }

        /// <summary>
        /// What the rehearsal measured, canonically. It goes INTO the plan fingerprint,
        /// so the apply's own rehearsal must reproduce it or the token refuses stale -
        /// which is exactly the property "the model moved" needs.
        /// </summary>
        private static string MeasuredCanonical(Measured m)
        {
            if (m == null) return "";
            var parts = new List<string>();
            double? total = m.Total;
            parts.Add(total.HasValue ? DimensionPlanRules.CanonicalFeet(total.Value) : "");
            if (m.SegmentValues != null)
                foreach (double v in m.SegmentValues) parts.Add(DimensionPlanRules.CanonicalFeet(v));
            if (m.SpotOrigin != null)
                parts.Add(DimensionPlanRules.CanonicalPoint(m.SpotOrigin[0], m.SpotOrigin[1], m.SpotOrigin[2]));
            return string.Join("|", parts);
        }

        /// <summary>One dry-run plan row: the legacy keys plus everything the plan resolved.</summary>
        private JObject PlanRow(Plan p)
        {
            var row = new JObject
            {
                ["index"] = p.Index,
                ["operation"] = p.Operation,
                ["references"] = DimensionPlanRules.IsDimensionOperation(p.Operation)
                    ? (JToken)p.RefList.Count
                    : p.References == null ? (JToken)JValue.CreateNull() : p.References.Size
            };
            if (!DimensionPlanRules.IsDimensionOperation(p.Operation))
            {
                if (p.Operation == "tag")
                {
                    row["view"] = IdNameJson(p.View);
                    row["target"] = IdNameJson(p.Target);
                    Element effective = p.Type ?? p.EffectiveTagType;
                    row["tag_type"] = effective == null ? (JToken)JValue.CreateNull() : IdNameJson(effective);
                    if (effective != null) ((JObject)row["tag_type"])["source"] = p.Type == null ? "default" : "explicit";
                    row["existing_tags_for_target_in_view"] = p.ExistingTagCount;
                }
                return row;
            }

            row["view"] = IdNameJson(p.View);
            row["dimension_type"] = IdNameJson(p.EffectiveType);
            ((JObject)row["dimension_type"])["source"] = p.TypeFromDefault ? "default" : "explicit";
            var refRows = new JArray();
            for (int i = 0; i < p.RefList.Count; i++)
                refRows.Add(new JObject
                {
                    ["order"] = i,
                    ["requested"] = p.RefRequested[i],
                    ["reserialized"] = p.RefReserialized[i],
                    ["owner_unique_id"] = p.RefOwnerUids[i],
                    ["reference_type"] = p.RefTypes[i],
                    ["geometry_fingerprint"] = p.RefFingerprints[i]
                });
            row["reference_detail"] = refRows;
            if (p.Line != null)
                row["line_feet"] = new JObject { ["start"] = Show(Arr(p.Line.GetEndPoint(0))), ["end"] = Show(Arr(p.Line.GetEndPoint(1))) };
            if (p.ArcCenter != null)
                row["arc_feet"] = new JObject { ["center"] = Show(Arr(p.ArcCenter)), ["radius"] = p.ArcRadius };
            if (p.SpotOrigin != null)
                row["spot_feet"] = new JObject
                {
                    ["point"] = Show(Arr(p.SpotOrigin)), ["bend"] = Show(Arr(p.SpotBend)),
                    ["end"] = Show(Arr(p.SpotEnd)), ["leader"] = p.SpotLeader
                };
            if (p.ExpectedValueFeet.HasValue)
                row["expected_value_internal_feet"] = new JObject
                {
                    ["value"] = p.ExpectedValueFeet.Value,
                    ["tolerance"] = p.ExpectedToleranceFeet
                };
            if (p.Rehearsed != null)
                row["rehearsed_values"] = Values(p.Rehearsed);
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
                ["comparison_tolerance_feet"] = DimensionPlanRules.CurveToleranceFeet,
                ["rows"] = rows ?? new JArray()
            };
        }

        // ---------------------------------------------------------------------
        // Small helpers.
        // ---------------------------------------------------------------------
        private static T Need<T>(Document d, JObject a, string f) where T : Element { long id = a.Value<long?>(f) ?? -1; if (!Rid.CanRepresent(id) || !(d.GetElement(Rid.Make(id)) is T e)) throw new ArgumentException(f + " must identify " + typeof(T).Name); return e; }
        private static int ExistingTagCount(Document doc, ElementId viewId, ElementId targetId)
        {
            int count = 0;
            // Do not use FilteredElementCollector(doc, viewId) here. Revit 2023 was
            // measured omitting annotations from an unopened view until graphics had
            // regenerated even though OwnerViewId and get_BoundingBox(view) proved they
            // were there. Duplicate prevention is a database fact, so sweep the class
            // document-wide and filter by its authoritative owner instead.
            foreach (IndependentTag tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                try { if (tag.OwnerViewId == viewId && tag.GetTaggedLocalElementIds().Any(id => id == targetId)) count++; }
                catch { }
            }
            return count;
        }
        private static XYZ Point(JToken t, double s, bool z) { JArray a = t as JArray; if (a == null || a.Count < (z ? 3 : 2) || a.Count > 3) throw new ArgumentException("point/line coordinate has wrong length"); return new XYZ(a[0].Value<double>() * s, a[1].Value<double>() * s, (a.Count > 2 ? a[2].Value<double>() : 0) * s); }
        private static double[] Arr(XYZ v) => new[] { v.X, v.Y, v.Z };
        private static double[] Delta(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static string Canon(XYZ v) => DimensionPlanRules.CanonicalPoint(v.X, v.Y, v.Z);
        private static string Show(double[] v)
            => v == null ? "(null)" : "[" + string.Join(",", v.Select(d => d.ToString("0.######", CultureInfo.InvariantCulture))) + "]";
        private static string SafeName(Element e) { try { return e?.Name ?? ""; } catch { return "<unreadable>"; } }
        private static string SafeUid(Element e) { try { return e?.UniqueId ?? ""; } catch { return "<unreadable>"; } }

        /// <summary>
        /// Identity and name in one guarded read. The name is what the person read when
        /// they approved; the UniqueId is what makes a swap under the same name visible.
        /// A plan must never fail while MEASURING, so unreadable stays a value.
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
                ["note"] = "Every annotation was created PROVISIONALLY inside one transaction, measured, and the " +
                           "transaction was rolled back; transaction_status is what Revit's RollBack() returned. " +
                           "Nothing from the rehearsal remains in the model when rolled_back_confirmed is true.",
                ["actions"] = Rows
            };
        }

        /// <summary>Everything the verification reads off one created dimension, guarded.</summary>
        private sealed class Measured
        {
            public string ClassName, Shape;
            public bool ClassMatches;
            public int SegmentValuesMissing;
            public long? ElementId;
            public string UniqueId;
            public long? OwnerViewId, TypeId;
            public bool? ReferencesAvailable;
            public int? SegmentCount;
            public List<string> RefReps;
            public double? SingleValue;
            public List<double> SegmentValues;
            public string Presented;
            public bool CurveRead, CurveBound;
            public double[] CurveStart, CurveEnd, CurveOrigin, CurveDirection;
            public double[] ArcCenter;
            public double? ArcRadius;
            public double[] SpotOrigin;
            public string Prefix, Suffix, Above, Below, ValueOverride;
            public bool? Eq, Locked;
            public string Unreadable;
            public double? Total => DimensionPlanRules.TotalOf(SingleValue, SegmentValues);
        }

        private sealed class Plan
        {
            public int Index; public string Operation, Text; public View View; public JObject Input; public double Scale;
            public XYZ Point; public Element Target, Type; public Line Line; public ReferenceArray References;
            public ElementId Created;
            public int ExistingTagCount;
            public ElementType EffectiveTagType;

            // Dimension production.
            public readonly List<Reference> RefList = new List<Reference>();
            public readonly List<string> RefRequested = new List<string>();
            public readonly List<string> RefReserialized = new List<string>();
            public readonly List<string> RefOwnerUids = new List<string>();
            public readonly List<string> RefTypes = new List<string>();
            public readonly List<string> RefFingerprints = new List<string>();
            public DimensionType EffectiveType; public bool TypeFromDefault;
            public Reference ArcReference; public Arc Arc; public XYZ ArcCenter; public double ArcRadius;
            public XYZ SpotOrigin, SpotBend, SpotEnd; public bool SpotLeader;
            public bool HasPrefix, HasSuffix, HasAbove, HasBelow, HasValueOverride;
            public string Prefix, Suffix, Above, Below, ValueOverride;
            public bool? Eq, Lock;
            public double? ExpectedValueFeet; public double ExpectedToleranceFeet;
            public Measured Rehearsed;
        }
    }
}
