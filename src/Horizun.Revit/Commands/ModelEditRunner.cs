// -----------------------------------------------------------------------------
// Horizun Revit MCP - the shared discipline of one verified model edit.
//
// horizun_manage_curtain, horizun_slab_shape and horizun_create_railing each do
// ONE edit per call. The sequence around that edit is the same for all three and
// is the shape TransformElementsCommand set: gate the document, resolve the plan
// without a transaction, rehearse by default, spend a single-use confirmation,
// write inside a TransactionGroup, re-read the committed model through a
// PostconditionCheck and ROLL BACK when it disagrees, then re-read once more after
// the group assimilated. A read operation takes the read guard and never opens a
// transaction.
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
    /// <summary>What a planner resolved: either a read's answer, or an edit to apply and verify.</summary>
    internal sealed class ArchModelEdit
    {
        /// <summary>Set for a read: returned as is, nothing is written.</summary>
        public JObject ReadResult { get; set; }
        /// <summary>What the rehearsal shows.</summary>
        public JObject Summary = new JObject();
        /// <summary>The elements the plan touches, fingerprinted into the confirmation.</summary>
        public readonly List<PlannedElement> Planned = new List<PlannedElement>();
        /// <summary>Optional measurements made in a transaction that must roll back before confirmation.</summary>
        public Func<Document, CommandResult> MeasureReversiblePlan { get; set; }
        /// <summary>When set, rehearse the actual edit and verify it inside a reversible group.</summary>
        public string NativeRehearsalSubject { get; set; }
        /// <summary>Number of work units in a successful rehearsal.</summary>
        public int RehearsalItemCount { get; set; } = 1;
        /// <summary>The write, inside the open transaction.</summary>
        public Action<Document> Apply;
        /// <summary>Independent units owning their own transactions, still inside the reversible outer group.</summary>
        public Action<Document> ApplyWithTransactions { get; set; }
        /// <summary>The re-read of the committed model; its AllVerified is the verdict.</summary>
        public Func<Document, PostconditionCheck> Verify;
        public string VerificationFailureCode { get; set; }
        /// <summary>Facts the reply carries beside the checklist (created ids, measured values).</summary>
        public JObject Evidence = new JObject();
        /// <summary>Optional independent work units: skipped/unverified units must not be stamped as fully applied.</summary>
        public Func<int> PartialFailures { get; set; }
        public Func<int> VerifiedItems { get; set; }
    }

    internal static class ModelEditRunner
    {
        /// <summary>Which postconditions failed, with expected and measured values, and a short evidence extract -
        /// in the TEXT, because that is what a probe or a person reads first.</summary>
        internal static string FailedText(PostconditionCheck check, JObject evidence)
        {
            try
            {
                var props = check?.ToJson()?["properties"] as JArray;
                var failed = props == null ? new System.Collections.Generic.List<string>() : props.OfType<JObject>()
                    .Where(x => x.Value<bool?>("matches") != true)
                    .Select(x => x.Value<string>("property") + " (requested " + x["requested"]?.ToString(Newtonsoft.Json.Formatting.None) +
                                 ", found " + x["found_in_committed_model"]?.ToString(Newtonsoft.Json.Formatting.None) + ")").ToList();
                string ev = evidence == null ? "" : evidence.ToString(Newtonsoft.Json.Formatting.None);
                if (ev.Length > 500) ev = ev.Substring(0, 500) + "...";
                return (failed.Count > 0 ? "Failed: " + string.Join("; ", failed) + "." : "No postcondition named the failure.") +
                       (ev.Length > 0 ? " Evidence: " + ev : "");
            }
            catch { return ""; }
        }


        private static CommandResult RehearseApply(Document doc, ArchModelEdit edit, string name, string subject)
        {
            using (var group = new TransactionGroup(doc, name))
            {
                bool started = false;
                bool restored = false;
                string rollback = "not_started";
                string problem = null;
                string failures = null;
                string trialCall = "TransactionGroup.Start";
                PostconditionCheck check = null;
                try
                {
                    if (group.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("the rehearsal transaction group did not start");
                    started = true;
                    using (var tx = new Transaction(doc, name))
                    {
                        trialCall = "RevitErrorRecorder.On";
                        var recorder = RevitErrorRecorder.On(tx);
                        trialCall = "Transaction.Start";
                        if (tx.Start() != TransactionStatus.Started)
                            throw new InvalidOperationException("the rehearsal transaction did not start");
                        try
                        {
                            trialCall = "edit.Apply";
                            edit.Apply(doc);
                            trialCall = "Document.Regenerate";
                            doc.Regenerate();
                            trialCall = "Guard.Commit (Transaction.Commit)";
                            Guard.Commit(tx, name);
                        }
                        catch
                        {
                            failures = recorder.Said();
                            if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx);
                            throw;
                        }
                    }
                    trialCall = "edit.Verify (post-commit model read)";
                    check = edit.Verify(doc);
                    if (!check.AllVerified) problem = ModelEditRunner.FailedText(check, edit.Evidence);
                }
                catch (Exception ex)
                {
                    problem = string.IsNullOrWhiteSpace(ex.Message)
                        ? trialCall + " failed during the native trial (" + ex.GetType().Name + ")."
                        : ex.Message;
                }
                if (started)
                {
                    try
                    {
                        var result = Guard.RollBack(group);
                        rollback = result.StatusName;
                        restored = result.Confirmed;
                    }
                    catch (Exception ex) { rollback = ex.Message; }
                }
                if (restored && problem == null)
                {
                    edit.Summary["postconditions"] = check.ToJson();
                    return null;
                }
                return CommandResult.FailCoded(started && !restored ? ErrorCodes.VerificacionFallida : ErrorCodes.PrecondicionNoCumplida,
                    $"{subject}: the rehearsal could not verify the requested edit. {problem}" +
                    (started && !restored ? " Rollback could not be confirmed." : ""),
                    extra: new JObject
                    {
                        ["dry_run"] = true, ["write_started"] = started,
                        ["changes_applied"] = started && !restored ? JValue.CreateNull() : new JValue(false),
                        ["rollback_status"] = rollback, ["plan"] = edit.Summary, ["evidence"] = edit.Evidence,
                        ["postconditions"] = check?.ToJson(), ["failure_details"] = failures
                    });
            }
        }

        /// <summary>Every argument that changes WHAT this runner writes. The confirmation token binds exactly these.
        /// A schema property that is in neither this list nor <see cref="PresentationFields"/> is a hole: a token rehearsed
        /// without it would apply a different plan (review B1). ModelEditPlanHashGuardTests walks every command that goes
        /// through the runner and fails on such a property.</summary>
        public static readonly string[] PlanScopeFields = {
            "units", "operation", "element_id", "schedule_id", "grid_index", "direction",
                "offset", "point", "grid_line_id", "mode", "mullion_type_id", "segment_index", "panel_ids", "type_id",
                "points", "start", "end", "host_id", "placement", "path", "level_id", "base_offset",
                "source_type_id", "new_name", "profile_id", "material_id", "wall_ids", "height", "measured_from", "side", "wall_offset",
                "view_id", "min", "max", "keep_transform", "depth", "element_ids", "room_id", "margin", "offsets", "value", "allow_unmarked_view",
                "sheet_id", "viewport_id", "center", "corner", "anchor", "margins", "notes_width", "gap", "allow_unmarked",
                "categories", "overrides", "view_ids", "coarser_than", "include",
                "datum_kinds", "criteria", "keep_ids", "scale", "rehide_foreign_markers",
                "load_tag_family", "pairs", "cut_order", "priority", "level_id", "verification_mode",
                "name", "detail_level", "display_style", "element_groups", "source_view_id", "group_ids", "level_ids", "tolerance_mm", "relative_rotations_degrees",
                "link_instance_id", "finish_type_id", "thickness_mm", "side", "min_overlap", "exterior_type_id", "interior_type_id", "original_wall",
                "grid_ids", "ends", "bubble_diameter_mm", "gap_mm", "max_offset_mm", "datum_ids", "bubbles", "margin_mm", "viewport_ids", "source_legend_id", "spacing_mm", "label",
                "note_ids", "delta_mm", "frame", "keep_leader_ends", "elbow", "width_mm", "text", "fit_crop_margin_mm", "notes", "source", "text_note_type_id", "model_tag_id", "targets", "sheet_horizontal",
                "leader_index", "end_mm", "max_shift_mm", "old", "new", "pattern", "map",
            // B1 (review of int/4.2): arguments of view_visibility and sheet_layout that were missing.
            "family_names", "type_names", "columns", "gap_x", "gap_y", "title_gap", "label_offset", "ink_box", "keep_viewport_center", "items",
            "require_title_fit", "line_length", "repair_outside",
            // Found by the guardian test on its first run: every other argument of a runner command that changes the plan.
            "link_colors", "system_filters", "tolerance", "accept_panel_merge", "vector", "max_search_mm", "orient", "max_step_mm",
            "fixtures", "slope_percent", "stub_mm", "room_ids", "type_by_use", "default_type_id", "offset_mm",
            "boundary_gap_tolerance_mm", "copy_parameters", "height_mm",
            // create_railing: the new type and its balusters change what is written.
            "infill",
            // wall_sweep place_edge: which sweep, and the edges (or whole faces) it is hosted on.
            "kind", "edge_references", "face_references",
            // view_visibility set_overrides (P5): which elements are written, and the category rows cleared with them.
            "unprinted", "clear_category_overrides",
            // view_visibility link_datum_cleanup (q90-vinculos): where the filter goes (a copy, a template) and the second separator.
            "template_mode", "name_separator"
        };

        /// <summary>Arguments that never change the result: they pick the document, carry the confirmation itself, name the
        /// transaction, or are identity assertions the dispatcher's IdentityPrepass checks and consumes BEFORE the command parses anything.
        /// They are not part of the plan HERE because they are bound another way: Confirmation.IdentityDigest (taken before the
        /// prepass erases the fields) enters the token's PlanHash, so a rehearsal and an apply must carry the same identity.</summary>
        public static readonly string[] PresentationFields = {
            "target_document", "dry_run", "confirmation_token", "idempotency_key", "transaction_name",
            "unique_id", "unique_ids", "expected_category", "expected_type"
        };

        public static CommandResult Run(UIApplication app, string paramsJson, string name, string defaultTransaction,
            Func<JObject, string> validate, Func<Document, JObject, double, ArchModelEdit> plan, string readOperation = "read")
        {
            var assumptions = new List<JObject>();
            CommandResult result = RunCore(app, paramsJson, name, defaultTransaction, validate, (doc, request, scale) =>
            {
                ArchModelEdit edit = plan(doc, request, scale);
                if (edit.Summary["assumptions"] is JArray rows)
                    assumptions.AddRange(rows.OfType<JObject>().Select(row => (JObject)row.DeepClone()));
                edit.Summary.Remove("assumptions");
                edit.Summary.Remove("has_assumptions");
                return edit;
            }, readOperation, assumptions);
            return AssumptionNotes.MarkFailure(AssumptionNotes.StampResult(result, assumptions),
                "SUPUESTO: the operation relied on assumed values. ", assumptions);
        }

        private static CommandResult RunCore(UIApplication app, string paramsJson, string name, string defaultTransaction,
            Func<JObject, string> validate, Func<Document, JObject, double, ArchModelEdit> plan, string readOperation,
            List<JObject> assumptions)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.RefuseBeforeWrite("Parameters must be a JSON object: " + ex.Message, ErrorCodes.EsquemaInvalido); }
            bool graphics = name == "horizun_view_visibility";
            if (graphics) assumptions.AddRange(ReliabilityDefaults.Graphics(request));
            string graphicsOperation = graphics ? request.Value<string>("operation")?.Trim().ToLowerInvariant() : null;
            bool graphicsCopy = graphicsOperation == "copy_view_graphics";
            bool visibilityEdit = graphicsOperation == "hide" || graphicsOperation == "unhide" ||
                graphicsOperation == "set_overrides" || graphicsOperation == "clear_overrides" || graphicsOperation == "hide_foreign_markers";
            bool bindChildPlan = graphicsCopy || visibilityEdit;
            string expectedChildPlan = bindChildPlan ? request.Value<string>(PlanLedger.ExpectedChildPlanField) : null;
            if (bindChildPlan) request.Remove(PlanLedger.ExpectedChildPlanField);
            string invalid = validate(request);
            if (invalid != null) return CommandResult.RefuseBeforeWrite(invalid, ErrorCodes.EsquemaInvalido);
            double scale;
            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (units == "feet") scale = 1; else if (units == "m") scale = 1 / 0.3048; else if (units == "mm") scale = 1 / 304.8;
            else return CommandResult.RefuseBeforeWrite("units must be mm, m or feet.", ErrorCodes.EsquemaInvalido);

            bool read = string.Equals(request.Value<string>("operation"), readOperation, StringComparison.OrdinalIgnoreCase) ||
                (name == "horizun_view_visibility" && new[] { "graphics_report", "override_report", "explain_overrides" }.Contains(request.Value<string>("operation"))) ||
                (name == "horizun_view_crop" && string.Equals(request.Value<string>("operation"), "read_section_box", StringComparison.OrdinalIgnoreCase));
            if (read)
            {
                Document active = null;
                try { active = app?.ActiveUIDocument?.Document; } catch { active = null; }
                if (active == null) return CommandResult.Fail("No document is active in Revit; there is nothing to read.");
                CommandResult refused = DocumentGate.ReadGuard(active, request, name);
                if (refused != null) return refused;
                try
                {
                    ArchModelEdit r = plan(active, request, scale);
                    return CommandResult.Ok(r.ReadResult ?? new JObject());
                }
                catch (Exception ex)
                {
                    return PlanFailure(request, ex);
                }
            }

            GateResult gate = DocumentGate.ForMutation(app, request, name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string hash = DocumentGate.PlanHash(request, PlanScopeFields);
            ArchModelEdit edit;
            try { edit = plan(doc, request, scale); }
            catch (Exception ex)
            {
                return PlanFailure(request, ex);
            }

            if (edit.MeasureReversiblePlan != null)
            {
                if (!dryRun)
                {
                    var requestRefusal = DocumentGate.RequireConfirmation(app, gate, request, name, hash, null, null, checkRequestOnly: true);
                    if (requestRefusal != null) return requestRefusal;
                }
                var measurementFailure = edit.MeasureReversiblePlan(doc);
                if (measurementFailure != null) return measurementFailure;
            }

            var resolved = new ResolvedPlan
            {
                Command = name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            resolved.Elements.AddRange(edit.Planned);
            if (!dryRun && (graphicsCopy || visibilityEdit || edit.MeasureReversiblePlan != null))
            {
                string childAction = graphicsCopy ? "view graphics copy" : visibilityEdit ? "view visibility edit" : "fixture replacement";
                CommandResult drift = resolved.RefuseChangedChild(expectedChildPlan, childAction);
                if (drift != null) return drift;
            }
            // A write operation whose plan finds NOTHING TO DO (hide_foreign_markers in a clean
            // view, measured live 2026-10-18) answers its ReadResult as a no-op: no token, no
            // transaction, and not an error.
            if (edit != null && edit.ReadResult != null && edit.Apply == null)
                return CommandResult.Ok(edit.ReadResult);
            if (dryRun)
            {
                bool nativeRehearsal = edit.NativeRehearsalSubject != null;
                if (nativeRehearsal)
                {
                    CommandResult refusalInTrial = RehearseApply(doc, edit, defaultTransaction + " rehearsal", edit.NativeRehearsalSubject);
                    if (refusalInTrial != null) return refusalInTrial;
                }
                bool measured = nativeRehearsal || edit.MeasureReversiblePlan != null;
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = measured ? "preview_rolled_back" : "not_started", ["plan"] = edit.Summary,
                    ["note"] = measured
                        ? "The measurement transactions were rolled back. Applying this plan re-reads the committed model."
                        : "Nothing was written; no transaction was opened. The apply re-reads the committed model and rolls back on any disagreement."
                };
                CarryAssumptions(result, edit.Summary);
                DocumentGate.RecordResolvedPlan(resolved);
                ApplicationOutcome.StampRehearsal(result, edit.RehearsalItemCount, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, name, hash, true,
                    "the token binds the operation, its arguments and the current state of every element it touches");
                return CommandResult.Ok(result);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, name, hash, resolved, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, name);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = defaultTransaction;
            PostconditionCheck check = null;
            {
                var group = new TransactionGroup(doc, txName);
                JObject failureDetail = null;
                bool started = false;
                string said = "";
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction group did not start");
                    if (edit.ApplyWithTransactions != null)
                    {
                        started = true;
                        edit.ApplyWithTransactions(doc);
                    }
                    else using (var tx = new Transaction(doc, txName))
                    {
                        RevitErrorRecorder recorder = RevitErrorRecorder.On(tx);
                        if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction did not start");
                        started = true;
                        try { edit.Apply(doc); doc.Regenerate(); Guard.Commit(tx, txName); }
                        catch { said = recorder.Said(); throw; }
                    }
                    check = edit.Verify(doc);
                    if (!check.AllVerified)
                    {
                        var rolled = Guard.RollBack(group);
                        failureDetail = new JObject
                        {
                            ["code"] = edit.VerificationFailureCode ?? "postcondition_failed", ["write_started"] = true,
                            ["changes_applied"] = rolled.Confirmed ? (JToken)false : JValue.CreateNull(),
                            ["rollback_status"] = rolled.StatusName, ["postconditions"] = check.ToJson(),
                            ["evidence"] = edit.Evidence
                        };
                        if (edit.VerificationFailureCode != null)
                            failureDetail["error_code"] = edit.VerificationFailureCode;
                        return CommandResult.FailWithDetail((rolled.Confirmed
                            ? "The committed model disagreed with the request and the group rolled back. "
                            : "The committed model disagreed with the request and group rollback is unconfirmed. ") + FailedText(check, edit.Evidence), failureDetail);
                    }
                    Guard.Assimilate(group, txName);
                }
                catch (Exception ex)
                {
                    string rb = "not_attempted";
                    try
                    {
                        TransactionStatus status = group.GetStatus();
                        if (status == TransactionStatus.Started) rb = Guard.RollBack(group).StatusName;
                        else if (status == TransactionStatus.RolledBack) rb = status.ToString();
                    }
                    catch (Exception e2) { rb = "failed: " + e2.Message; }
                    string diagnosis = FailureDiagnosis(request, ex);
                    Exception native = NativeException(ex);
                    bool precondition = native == null && (ex is ArgumentException || ex is UnsupportedCapability);
                    string guidance = precondition
                        ? " Preconditions are checked before commit; use the named requirement and change the identified element or request to satisfy it, then rehearse again."
                        : string.IsNullOrWhiteSpace(said) ? "" : " Review the captured Revit failures before rehearsing the operation again.";
                    failureDetail = new JObject
                    {
                        ["code"] = precondition ? ErrorCodes.PrecondicionNoCumplida : (rb == "RolledBack" ? ErrorCodes.TransaccionRevertida : "revit_edit_failed"),
                        ["precondition"] = precondition ? diagnosis : null,
                        ["how_to_satisfy"] = guidance.Trim(),
                        ["failure_details"] = said,
                        ["write_started"] = started,
                        ["changes_applied"] = !started ? (JToken)false : (rb == "RolledBack" ? (JToken)false : JValue.CreateNull()),
                        ["rollback_status"] = rb,
                        ["evidence"] = edit.Evidence
                    };
                    if (native != null)
                        return CommandResult.FailCoded(rb == "RolledBack" ? ErrorCodes.TransaccionRevertida : ErrorCodes.ApiRevitExcepcion,
                            name + " failed: " + diagnosis + guidance + said, native.GetType().FullName, extra: failureDetail);
                    return CommandResult.FailWithDetail(name + " failed: " + diagnosis + guidance + said, failureDetail);
                }
                finally
                {
                    try { group.Dispose(); }
                    catch (Exception ex) when (failureDetail != null) { failureDetail["cleanup_error"] = ex.Message; }
                }
            }
            // Fresh re-read after the group assimilated: a disagreement now cannot be
            // undone by this call, so it is reported as committed and unverified.
            try { check = edit.Verify(doc); }
            catch (Exception ex)
            {
                // Diagnostic sinks can fail too (for example a full disk while flushing
                // a join checkpoint). Assimilation already happened; never imply rollback.
                Exception native = NativeException(ex);
                string diagnosis = FailureDiagnosis(request, ex);
                return CommandResult.FailCoded(native != null ? ErrorCodes.ApiRevitExcepcion : ErrorCodes.VerificacionFallida,
                    "Committed, but the post-commit verification could not finish: " + diagnosis, native?.GetType().FullName,
                    extra: new JObject { ["code"] = "postcommit_verification_failed", ["write_started"] = true,
                        ["changes_applied"] = true, ["verification_error"] = diagnosis, ["evidence"] = edit.Evidence });
            }
            if (!check.AllVerified)
                return CommandResult.FailWithDetail("Committed, but the re-read after the group assimilated disagrees; inspect the model.",
                    new JObject { ["code"] = "postcommit_verification_failed", ["error_code"] = edit.VerificationFailureCode, ["write_started"] = true, ["changes_applied"] = true,
                                  ["postconditions"] = check.ToJson(), ["evidence"] = edit.Evidence });
            var done = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["postconditions"] = check.ToJson(), ["evidence"] = edit.Evidence
            };
            CarryAssumptions(done, edit.Evidence);
            int failedItems = edit.PartialFailures?.Invoke() ?? 0;
            int verifiedItems = edit.VerifiedItems?.Invoke() ?? 1;
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, verifiedItems + failedItems, verifiedItems, verifiedItems, failedItems, 0, 0);
            if (failedItems > 0 && verifiedItems == 0) return CommandResult.FailWithDetail("All requested items rolled back; no replacement succeeded.", done);
            return CommandResult.Ok(done);
        }

        private static CommandResult PlanFailure(JObject request, Exception ex)
        {
            Exception native = NativeException(ex);
            string diagnosis = FailureDiagnosis(request, ex);
            var outcome = new ActionOutcome { Index = 0, Error = diagnosis, UnsupportedReason = native == null ? UnsupportedCapability.ReasonOf(ex) : null };
            var verdict = FallbackDecision.Decide(new[] { outcome }, writeStarted: false);
            var refusal = CommandResult.RefuseBeforeWrite(diagnosis,
                native == null ? ex.Data["error_code"] as string : ErrorCodes.ApiRevitExcepcion,
                verdict.Signal, verdict.CapabilityGaps);
            if (native != null) refusal.Detail[ErrorCodes.ExceptionTypeField] = native.GetType().Name;
            return refusal;
        }

        private static Exception NativeException(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
                if (ErrorCodes.IsRevitApiType(current.GetType().FullName)) return current;
            return null;
        }

        internal static string FailureDiagnosis(JObject request, Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(ex.Message)) return ex.Message;
            string operation = request.Value<string>("operation") ?? "model edit";
            string target = request["element_id"] != null ? " for element " + request["element_id"] : "";
            return operation + target + " raised " + ex.GetType().Name + " without a message.";
        }

        /// <summary>
        /// A plan or its evidence that carries SUPUESTO rows (AssumptionNotes) puts them at the top of the reply too, in the one format:
        /// has_assumptions + assumptions[{flag, what, why, override}]. Nothing when there are none.
        /// </summary>
        internal static void CarryAssumptions(JObject reply, JObject from)
        {
            if (reply == null || !(from?["assumptions"] is JArray rows) || rows.Count == 0) return;
            AssumptionNotes.Stamp(reply, rows.OfType<JObject>().Select(o => (JObject)o.DeepClone()).ToList());
        }

        // ---- small shared readers ----------------------------------------------------

        public static XYZ Point(JToken token, double scale, string name, int dims = 3)
        {
            JArray a = token as JArray;
            if (a == null || a.Count != dims) throw new ArgumentException(name + " must contain " + dims + " coordinates");
            return new XYZ(a[0].Value<double>() * scale, a[1].Value<double>() * scale, dims == 3 ? a[2].Value<double>() * scale : 0);
        }

        public static T Need<T>(Document doc, JObject request, string field) where T : Element
        {
            long raw = request.Value<long?>(field) ?? -1;
            Element e = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) : null;
            if (e is T t) return t;
            throw new ArgumentException(field + " " + raw + " is " + (e == null ? "not an element of this document" : "a " + e.GetType().Name + ", not a " + typeof(T).Name) + ".");
        }

        public static PlannedElement Planned(Element e, PlannedAction action, JObject request)
        {
            string uid = null, geometry = null;
            try { uid = e.UniqueId; } catch { }
            try
            {
                BoundingBoxXYZ b = e.get_BoundingBox(null);
                if (b != null)
                    geometry = string.Join(",", new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z }
                        .Select(v => Math.Round(v * 304.8).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            catch { }
            return new PlannedElement
            {
                UniqueId = uid, ElementId = Rid.Value(e.Id), Category = e.Category?.Name, Action = action,
                GeometryFingerprint = geometry,
                BeforeValues = new Dictionary<string, string> { { "type_id", Rid.Value(e.GetTypeId()).ToString(System.Globalization.CultureInfo.InvariantCulture) } },
                // NOT request.ToString(): the apply differs from its rehearsal by dry_run and
                // confirmation_token by construction, so hashing them made every apply of a
                // plan with a listed element refuse as stale (measured on Revit 2026,
                // 2026-09-24; the railing path route passed only because it lists none).
                ProposedValues = new Dictionary<string, string> { { "request", ArchitecturalEditRules.ProposedRequest(request) } }
            };
        }

        public static JArray Arr(XYZ p, double scale) => new JArray(Math.Round(p.X / scale, 4), Math.Round(p.Y / scale, 4), Math.Round(p.Z / scale, 4));
    }
}
