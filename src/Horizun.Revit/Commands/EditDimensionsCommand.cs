// -----------------------------------------------------------------------------
// Horizun Revit MCP - edit existing dimensions atomically, verify every field
// against a re-read, and roll the whole batch back when any check fails.
//
// The full mutation discipline, in the shape TransformElementsCommand set:
// DocumentGate.ForMutation on entry, dry_run defaulting to true, a single-use
// confirmation token bound to the request AND to the materialised plan, a
// StillTheSame recheck immediately before the write, one Transaction for the
// whole batch, verification in the still-reversible state, Guard.Commit or
// Guard.RollBack with the TransactionStatus Revit actually returned, and a
// post-commit re-read of every requested field reported as requested/read/match.
//
// The plan a dry run materialises is deliberately dense for a dimension: its
// type (uid and name), its curve endpoints rounded to 0.1 mm, its segment
// count, its current overrides, EQ and lock, and the stable representation of
// every reference. Editing a dimension somebody else changed between the dry
// run and the apply is refused as a stale plan, not applied over their work.
//
// The per-field split between single- and multi-segment dimensions, the
// segment-index arithmetic, the "empty string removes the override" rule, the
// exact-move comparison and the terminal-state matrix all live Revit-free in
// Core/DimensionEditRules.cs, where they are proved by unit test.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class EditDimensionsCommand : ICommand
    {
        public string Name => "horizun_edit_dimensions";
        public string Description =>
            "Edit existing dimensions in one atomic transaction - type (same shape only), position, prefix/" +
            "suffix/above/below/value override, EQ, lock, per-segment overrides, text-position reset - with a " +
            "materialised dry-run plan, a single-use confirmation token, and every requested field re-read from " +
            "the committed model and reported as requested/read/match. Any failed check rolls the whole batch back.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            bool readOnly = false;
            try { readOnly = doc.IsReadOnly; } catch { /* treated as writable; the transaction would refuse */ }
            if (readOnly)
                return CommandResult.Fail("The active document is read-only, so '" + Name +
                                          "' cannot write to it. Nothing was changed.");

            JArray input = request["actions"] as JArray;
            if (input == null || input.Count == 0) return CommandResult.Fail("actions is required and must be non-empty.");
            if (input.Count > 200) return CommandResult.Fail("actions exceeds 200 entries.");

            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            double scale;
            if (!Scale(units, out scale)) return CommandResult.Fail("units must be mm, m or feet.");

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "actions");

            // ---- Plan every action before anything else. One outcome per entry, so
            // the Python-fallback verdict is decided over the WHOLE batch. ----
            var plans = new List<Plan>();
            var errors = new JArray();
            var claimed = new HashSet<long>();
            var outcomes = new List<ActionOutcome>();
            for (int i = 0; i < input.Count; i++)
            {
                string error = null, reason = null;
                Plan plan = PlanAction(doc, i, input[i] as JObject, scale, claimed, out error, out reason);
                if (plan == null)
                {
                    string message = error ?? "entry is not an object";
                    errors.Add(new JObject { ["index"] = i, ["error"] = message });
                    outcomes.Add(new ActionOutcome { Index = i, Error = message, UnsupportedReason = reason });
                }
                else plans.Add(plan);
            }

            // ---- The MATERIALISED plan: these dimensions, as they stand right now.
            // Built identically on the dry run and on the apply; the dry run's
            // fingerprint rides in the token, the apply recomputes it, and a dimension
            // somebody edited in between is refused as stale rather than overwritten. ----
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (Plan p in plans)
            {
                Dimension d = doc.GetElement(p.Id) as Dimension;
                if (d == null) continue;
                resolvedPlan.Elements.Add(SnapshotElement(doc, d, p));
            }

            if (dryRun)
            {
                var rehearsal = new JObject
                {
                    ["dry_run"] = true,
                    ["transaction_status"] = ApplicationOutcome.NotStarted,
                    ["actions"] = input.Count,
                    ["valid_actions"] = plans.Count,
                    ["invalid_actions"] = errors.Count,
                    ["errors"] = errors,
                    ["plan"] = new JArray(plans.Select(p => p.Summary)),
                    ["units"] = units,
                    ["move_tolerance_feet"] = DimensionEditRules.DefaultMoveToleranceFeet,
                    ["note"] = "Nothing was edited; no transaction was opened."
                };
                if (errors.Count == 0) DocumentGate.RecordResolvedPlan(resolvedPlan);
                ApplicationOutcome.StampRehearsal(rehearsal, input.Count, errors.Count, 0, 0);
                DocumentGate.StampConfirmation(rehearsal, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0
                        ? "the token binds the ordered actions - types, vectors, override text, segment edits - " +
                          "and the before-state of every dimension they resolved to"
                        : "no usable confirmation is issued while any action is invalid");
                return FallbackDecision.Attach(
                    CommandResult.Ok(rehearsal),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }

            if (errors.Count > 0)
                return FallbackDecision.Refuse(
                    "Invalid actions; nothing ran: " + errors.ToString(Formatting.None),
                    FallbackDecision.Decide(outcomes, writeStarted: false));

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                     resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: edit dimensions";
            // MEASURED on live 2025 (2026-08-24, the EQ probe): computed dimension facts
            // - AreSegmentsEqual among them - do not read back inside the transaction
            // that set them, exactly like dimension values at creation. So the edit uses
            // the same shape the creation path earned: commit inside a TransactionGroup,
            // regenerate in a second committed transaction to make Revit compute, THEN
            // verify - with the group still open, so a failed check rolls the whole
            // batch back and "nothing was edited" stays a statement about the model.
            using (var group = new TransactionGroup(doc, txName))
            {
                group.Start();
                using (var tx = new Transaction(doc, txName))
                {
                    tx.Start();
                    try
                    {
                        foreach (Plan p in plans) Apply(doc, p);
                        doc.Regenerate();
                        Guard.Commit(tx, txName);
                    }
                    catch (Exception ex)
                    {
                        bool attempted = false;
                        string terminal;
                        if (ex is SilentRollbackException silent) terminal = silent.Status.ToString();
                        else if (tx.GetStatus() == TransactionStatus.Started)
                        {
                            attempted = true;
                            terminal = Guard.RollBack(tx).StatusName;
                        }
                        else terminal = ApplicationOutcome.NotStarted;
                        Guard.RollbackResult rbGroup = Guard.RollBack(group);

                        bool confirmed = PlanFailure.IsConfirmedRollback(rbGroup.StatusName);
                        int unknown = confirmed ? 0 : plans.Count;
                        var detail = new JObject
                        {
                            ["state"] = DimensionEditRules.DecideFinalState(rbGroup.StatusName, false),
                            ["transaction_status"] = terminal,
                            ["transaction_group_status"] = rbGroup.StatusName,
                            ["rollback_attempted"] = attempted,
                            ["rollback_confirmed"] = rbGroup.Confirmed
                        };
                        ApplicationOutcome.StampApplied(detail, rbGroup.StatusName, plans.Count, 0, 0, 0,
                                                        plans.Count - unknown, unknown);
                        return CommandResult.FailWithDetail(
                            "Atomic dimension edit failed: " + ex.Message + " " +
                            PlanFailure.SingleTransactionOutcome(attempted, attempted ? terminal : PlanFailure.NotAttempted,
                                                                 "nothing was edited") +
                            " The TransactionGroup reported '" + rbGroup.StatusName + "'.",
                            detail);
                    }
                }

                // Materialise: a committed regeneration is what asks Revit to compute
                // the facts the verification below reads. It writes nothing of its own.
                using (var regen = new Transaction(doc, txName + " (materialise for verification)"))
                {
                    regen.Start();
                    try { doc.Regenerate(); Guard.Commit(regen, txName); }
                    catch
                    {
                        if (regen.GetStatus() == TransactionStatus.Started) Guard.RollBack(regen);
                        // The reads below decide; unmaterialised facts fail closed there.
                    }
                }

                // ---- Verify with the GROUP still open: the reversible state. ----
                var reversibleRows = new JArray();
                int reversibleFailures = 0;
                foreach (Plan p in plans)
                {
                    bool okOne;
                    reversibleRows.Add(VerifyPlan(doc, p, out okOne));
                    if (!okOne) reversibleFailures++;
                }
                if (reversibleFailures > 0)
                {
                    Guard.RollbackResult rb = Guard.RollBack(group);
                    string state = DimensionEditRules.DecideFinalState(rb.StatusName, false);
                    var detail = new JObject
                    {
                        ["state"] = state,
                        ["transaction_status"] = ApplicationOutcome.Committed,
                        ["transaction_group_status"] = rb.StatusName,
                        ["rollback_confirmed"] = rb.Confirmed,
                        ["verified_in_reversible_state"] = false,
                        ["rows"] = reversibleRows
                    };
                    ApplicationOutcome.StampApplied(detail, rb.StatusName, plans.Count, 0, 0, 0,
                        rb.Confirmed ? reversibleFailures : 0,
                        rb.Confirmed ? 0 : plans.Count);
                    return CommandResult.FailWithDetail(
                        "Post-write verification in the reversible state found " + reversibleFailures +
                        " action(s) whose re-read did not match the request, so the whole batch was abandoned. " +
                        PlanFailure.SingleTransactionOutcome(true, rb.StatusName, "nothing was edited") +
                        " The TransactionGroup reported '" + rb.StatusName + "'.",
                        detail);
                }

                try { Guard.Assimilate(group, txName); }
                catch (SilentRollbackException ex)
                {
                    string rbStatus;
                    try { rbStatus = Guard.RollBack(group).StatusName; }
                    catch (Exception rex) { rbStatus = "RollBack threw: " + rex.Message; }
                    var detail = new JObject
                    {
                        ["state"] = DimensionEditRules.DecideFinalState(rbStatus, false),
                        ["transaction_status"] = ApplicationOutcome.Committed,
                        ["transaction_group_status"] = rbStatus,
                        ["rows"] = reversibleRows
                    };
                    ApplicationOutcome.StampApplied(detail, rbStatus, plans.Count, 0, 0, 0,
                        PlanFailure.IsConfirmedRollback(rbStatus) ? plans.Count : 0,
                        PlanFailure.IsConfirmedRollback(rbStatus) ? 0 : plans.Count);
                    return CommandResult.FailWithDetail(
                        "Every edit verified, but the TransactionGroup would not assimilate: " + ex.Message +
                        " A rollback was attempted and Revit reported '" + rbStatus + "'.",
                        detail);
                }
            }

            // ---- Post-assimilate: the same checks over the settled model. This is the
            // read the response stands on. ----
            var rows = new JArray();
            int verified = 0;
            foreach (Plan p in plans)
            {
                bool okOne;
                rows.Add(VerifyPlan(doc, p, out okOne));
                if (okOne) verified++;
            }

            if (verified != plans.Count)
            {
                // The reversible-state check said yes and the settled model says no.
                // Two measurements of one fact in contradiction are counted as UNKNOWN,
                // not as failures: what reached the model is exactly what has not been
                // established, and no retry may be built on it.
                var detail = new JObject
                {
                    ["state"] = DimensionEditRules.DecideFinalState(ApplicationOutcome.Committed, false),
                    ["transaction_status"] = ApplicationOutcome.Committed,
                    ["verified_in_reversible_state"] = true,
                    ["rows"] = rows
                };
                ApplicationOutcome.StampApplied(detail, ApplicationOutcome.Committed, plans.Count,
                                                verified, verified, 0, 0, plans.Count - verified);
                return CommandResult.FailWithDetail(
                    "The batch committed and assimilated, but " + (plans.Count - verified) + " action(s) failed " +
                    "the post-assimilate re-read after passing in the reversible state. Inspect the model before " +
                    "any retry.",
                    detail);
            }

            var result = new JObject
            {
                ["dry_run"] = false,
                ["state"] = DimensionEditRules.DecideFinalState(ApplicationOutcome.Committed, true),
                ["transaction_status"] = ApplicationOutcome.Committed,
                ["transaction_group_status"] = "Assimilated",
                ["transaction_name"] = txName,
                ["actions"] = plans.Count,
                ["actions_verified"] = verified,
                ["units"] = units,
                ["move_tolerance_feet"] = DimensionEditRules.DefaultMoveToleranceFeet,
                ["rows"] = rows
            };
            DocumentGate.StampConfirmation(result, gate, Name, planHash, false);
            ApplicationOutcome.StampApplied(result, ApplicationOutcome.Committed, plans.Count,
                                            plans.Count, verified, 0, 0, 0);
            return CommandResult.Ok(result);
        }

        // ---------------------------------------------------------------------
        // Planning.
        // ---------------------------------------------------------------------

        private static Plan PlanAction(Document doc, int index, JObject a, double scale, HashSet<long> claimed,
                                       out string error, out string unsupportedReason)
        {
            error = null; unsupportedReason = null;
            if (a == null) { error = "entry is not an object"; return null; }
            try
            {
                if (a["element_id"] == null || a["element_id"].Type != JTokenType.Integer)
                    throw new ArgumentException("element_id is required and must be an integer");
                long raw = a.Value<long>("element_id");
                if (!Rid.CanRepresent(raw)) throw new ArgumentException(Rid.RangeError(raw));
                Element e = doc.GetElement(Rid.Make(raw));
                if (e == null) throw new ArgumentException("ElementId " + raw + " does not exist in the active document");
                Dimension d = e as Dimension;
                if (d == null)
                    throw new ArgumentException("ElementId " + raw + " is a " + e.GetType().Name + ", not a Dimension");
                // Constraints wear the Dimension class: a locked alignment or a sketch EQ
                // is a Dimension that is NOT view-specific, and `lock: false` on one of
                // those would UNPIN a model constraint while reading back as a clean,
                // verified edit. An annotation dimension always belongs to a view; a
                // dimension that does not is a constraint, and editing constraints is a
                // different decision than editing annotation - refused, not guessed.
                bool viewSpecific;
                try { viewSpecific = d.ViewSpecific; }
                catch (Exception ex)
                { throw new ArgumentException("ElementId " + raw + "'s view-specificity could not be read (" +
                                              ex.Message + "), so an annotation dimension cannot be told from a " +
                                              "model constraint. Nothing was edited."); }
                if (!viewSpecific)
                    throw new ArgumentException("ElementId " + raw + " is a model CONSTRAINT (" +
                        SafeCategoryName(d) + "), not an annotation dimension: unlocking or restyling it would " +
                        "change what holds the geometry in place, not what a sheet says. horizun_edit_dimensions " +
                        "edits annotation dimensions only. Nothing was edited.");
                if (!claimed.Add(raw))
                    throw new ArgumentException("ElementId " + raw + " appears in more than one action; two edits " +
                        "of one dimension in one batch are order-dependent in a way nobody stated - combine them " +
                        "into one action");

                // Classify every field FIRST. A request carrying a field this command
                // cannot honour is refused before per-field validation reads meaning
                // into the rest of it.
                foreach (JProperty prop in a.Properties())
                {
                    switch (DimensionEditRules.ClassifyActionField(prop.Name))
                    {
                        case DimensionEditRules.ActionFieldClass.Identity:
                        case DimensionEditRules.ActionFieldClass.Edit:
                            continue;
                        case DimensionEditRules.ActionFieldClass.ReferenceReplacement:
                            // NOT a capability gap and NOT a Python matter: the Revit API
                            // itself has no setter for Dimension.References, on any year
                            // this bridge supports, so no path can do it. The fallback is
                            // deliberately withheld - a script would fail the same way.
                            throw new ArgumentException(
                                "'" + prop.Name + "' asks for reference replacement, which is not supported by the Revit API itself: " +
                                "Dimension.References has no setter in any Revit 2023-2027, so neither this command " +
                                "nor a Python script can swap a dimension's references. Do not fall back to Python " +
                                "for this. Delete the dimension and create the one you want against the intended " +
                                "references (horizun_annotate). Nothing was written.");
                        default:
                            throw new UnsupportedCapability(
                                "unsupported action field '" + prop.Name + "' - horizun_edit_dimensions edits " +
                                DimensionEditRules.EditFieldsSentence() + " only. Nothing was written.",
                                FallbackSignal.ReasonUnsupportedCapability);
                    }
                }

                int segCount;
                try { segCount = d.NumberOfSegments; }
                catch (Exception ex)
                {
                    throw new ArgumentException("the segment count of dimension " + raw + " could not be read (" +
                                                ex.Message + "), so no edit of it can be validated");
                }

                var p = new Plan { Index = index, RawId = raw, Id = d.Id, SegmentCount = segCount };
                var editNames = new List<string>();

                if (a["set_type_id"] != null)
                {
                    if (a["set_type_id"].Type != JTokenType.Integer)
                        throw new ArgumentException("set_type_id must be an integer ElementId");
                    long typeRaw = a.Value<long>("set_type_id");
                    if (!Rid.CanRepresent(typeRaw)) throw new ArgumentException(Rid.RangeError(typeRaw));
                    DimensionType t = doc.GetElement(Rid.Make(typeRaw)) as DimensionType;
                    if (t == null)
                        throw new ArgumentException("set_type_id " + typeRaw + " does not identify a DimensionType");
                    DimensionType current;
                    try { current = d.DimensionType; }
                    catch (Exception ex)
                    {
                        throw new ArgumentException("the current type of dimension " + raw + " could not be read (" +
                                                    ex.Message + "), so a same-shape type change cannot be validated");
                    }
                    if (current == null)
                        throw new ArgumentException("dimension " + raw + " reports no current type, so a same-shape " +
                                                    "type change cannot be validated");
                    if (current.StyleType != t.StyleType)
                        throw new ArgumentException("set_type_id " + typeRaw + " is a " + t.StyleType +
                            " type, but dimension " + raw + " uses a " + current.StyleType + " type. A type swap " +
                            "that changes the dimension's shape is refused as an argument error: create the " +
                            "dimension you want instead of re-dressing this one.");
                    p.SetTypeId = t.Id;
                    editNames.Add("set_type_id");
                }

                if (a["move_by"] != null)
                {
                    p.MoveBy = Point(a["move_by"], scale, "move_by");
                    string kind;
                    List<XYZ> samples = SamplePoints(d, out kind);
                    if (samples == null)
                        throw new ArgumentException("move_by cannot be verified for dimension " + raw + ": its " +
                            "curve exposes no bound endpoints and no readable origin, so a committed move could " +
                            "not be proved against a re-read - and a write this command cannot verify is one it " +
                            "does not make");
                    p.MoveSamples = samples;
                    p.SampleKind = kind;
                    editNames.Add("move_by");
                }

                p.Prefix = TextField(a, "prefix", segCount, editNames);
                p.Suffix = TextField(a, "suffix", segCount, editNames);
                p.Above = TextField(a, "above", segCount, editNames);
                p.Below = TextField(a, "below", segCount, editNames);
                p.ValueOverride = TextField(a, "value_override", segCount, editNames);

                if (a["eq"] != null)
                {
                    if (a["eq"].Type != JTokenType.Boolean) throw new ArgumentException("eq must be a boolean");
                    RequireEligible("eq", segCount);
                    p.Eq = a.Value<bool>("eq");
                    editNames.Add("eq");
                }
                if (a["lock"] != null)
                {
                    if (a["lock"].Type != JTokenType.Boolean) throw new ArgumentException("lock must be a boolean");
                    RequireEligible("lock", segCount);
                    p.Lock = a.Value<bool>("lock");
                    editNames.Add("lock");
                }

                if (a["segments"] != null)
                {
                    RequireEligible("segments", segCount);
                    p.Segments = PlanSegments(a["segments"], segCount);
                    editNames.Add("segments");
                }

                if (a["reset_text_position"] != null)
                {
                    if (a["reset_text_position"].Type != JTokenType.Boolean)
                        throw new ArgumentException("reset_text_position must be a boolean");
                    if (!a.Value<bool>("reset_text_position"))
                        throw new ArgumentException("reset_text_position must be true when present; omit it otherwise " +
                                                    "- an explicit false that means 'do nothing' would make silence " +
                                                    "and refusal read the same");
                    p.ResetTextPosition = true;
                    try { p.TextPositionBefore = d.TextPosition; } catch { p.TextPositionBefore = null; }
                    editNames.Add("reset_text_position");
                }

                if (editNames.Count == 0)
                    throw new ArgumentException("the action names no edit - include at least one of: " +
                                                DimensionEditRules.EditFieldsSentence());

                p.Summary = new JObject
                {
                    ["index"] = index,
                    ["element_id"] = raw,
                    ["segments"] = segCount,
                    ["edits"] = new JArray(editNames.Select(n => (JToken)n)),
                    ["verifiable"] = true
                };
                return p;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return null;
            }
        }

        private static void RequireEligible(string field, int segCount)
        {
            string why = DimensionEditRules.EligibilityError(field, segCount);
            if (why != null) throw new ArgumentException(why);
        }

        private static string TextField(JObject a, string name, int segCount, List<string> editNames)
        {
            JToken t = a[name];
            if (t == null) return null;
            if (t.Type != JTokenType.String)
                throw new ArgumentException("'" + name + "' must be a string; '' clears it, JSON null does not");
            RequireEligible(name, segCount);
            editNames.Add(name);
            return t.Value<string>();
        }

        private static List<SegEdit> PlanSegments(JToken token, int segCount)
        {
            JArray segs = token as JArray;
            if (segs == null || segs.Count == 0)
                throw new ArgumentException("segments must be a non-empty array of segment edits");
            var claimed = new HashSet<int>();
            var result = new List<SegEdit>();
            foreach (JToken entry in segs)
            {
                JObject so = entry as JObject;
                if (so == null) throw new ArgumentException("segments entries must be objects");
                foreach (JProperty sp in so.Properties())
                    if (sp.Name != "index" && sp.Name != "prefix" && sp.Name != "suffix" && sp.Name != "above" &&
                        sp.Name != "below" && sp.Name != "value_override" && sp.Name != "lock")
                        throw new ArgumentException("segments[] entries accept index, prefix, suffix, above, below, " +
                                                    "value_override and lock; '" + sp.Name + "' is none of them");
                if (so["index"] == null || so["index"].Type != JTokenType.Integer)
                    throw new ArgumentException("segments[].index is required and must be an integer");
                long si = so.Value<long>("index");
                string why = DimensionEditRules.SegmentIndexError(si, segCount);
                if (why != null) throw new ArgumentException(why);
                if (!claimed.Add((int)si))
                    throw new ArgumentException("segment index " + si + " appears more than once in one action");

                var se = new SegEdit { SegIndex = (int)si };
                int segEdits = 0;
                se.Prefix = SegText(so, "prefix", ref segEdits);
                se.Suffix = SegText(so, "suffix", ref segEdits);
                se.Above = SegText(so, "above", ref segEdits);
                se.Below = SegText(so, "below", ref segEdits);
                se.ValueOverride = SegText(so, "value_override", ref segEdits);
                if (so["lock"] != null)
                {
                    if (so["lock"].Type != JTokenType.Boolean)
                        throw new ArgumentException("segments[" + si + "].lock must be a boolean");
                    se.Lock = so.Value<bool>("lock");
                    segEdits++;
                }
                if (segEdits == 0)
                    throw new ArgumentException("segments[" + si + "] names no edit");
                result.Add(se);
            }
            return result;
        }

        private static string SegText(JObject so, string name, ref int segEdits)
        {
            JToken t = so[name];
            if (t == null) return null;
            if (t.Type != JTokenType.String)
                throw new ArgumentException("segments[]." + name + " must be a string; '' clears it");
            segEdits++;
            return t.Value<string>();
        }

        // ---------------------------------------------------------------------
        // The apply.
        // ---------------------------------------------------------------------

        private static void Apply(Document doc, Plan p)
        {
            Dimension d = doc.GetElement(p.Id) as Dimension;
            if (d == null)
                throw new InvalidOperationException("Dimension " + p.RawId + " vanished between the plan and the write");
            if (p.SetTypeId != null) d.ChangeTypeId(p.SetTypeId);
            if (p.MoveBy != null) ElementTransformUtils.MoveElement(doc, p.Id, p.MoveBy);
            if (p.Prefix != null) d.Prefix = p.Prefix;
            if (p.Suffix != null) d.Suffix = p.Suffix;
            if (p.Above != null) d.Above = p.Above;
            if (p.Below != null) d.Below = p.Below;
            if (p.ValueOverride != null) d.ValueOverride = p.ValueOverride;
            if (p.Eq.HasValue) d.AreSegmentsEqual = p.Eq.Value;
            if (p.Lock.HasValue) d.IsLocked = p.Lock.Value;
            if (p.Segments != null)
                foreach (SegEdit se in p.Segments)
                {
                    DimensionSegment s = SegmentAt(d, se.SegIndex);
                    if (s == null)
                        throw new InvalidOperationException("segment " + se.SegIndex + " of dimension " + p.RawId +
                                                            " vanished between the plan and the write");
                    if (se.Prefix != null) s.Prefix = se.Prefix;
                    if (se.Suffix != null) s.Suffix = se.Suffix;
                    if (se.Above != null) s.Above = se.Above;
                    if (se.Below != null) s.Below = se.Below;
                    if (se.ValueOverride != null) s.ValueOverride = se.ValueOverride;
                    if (se.Lock.HasValue) s.IsLocked = se.Lock.Value;
                }
            if (p.ResetTextPosition) d.ResetTextPosition();
        }

        // ---------------------------------------------------------------------
        // Verification. Called twice with the same arithmetic: once in the
        // reversible state, once over the committed model.
        // ---------------------------------------------------------------------

        private static JObject VerifyPlan(Document doc, Plan p, out bool ok)
        {
            bool allOk = true;
            var fields = new JArray();
            var row = new JObject { ["index"] = p.Index, ["element_id"] = p.RawId };

            Dimension d = doc.GetElement(p.Id) as Dimension;
            if (d == null)
            {
                row["error"] = "the dimension no longer resolves after the write";
                row["verified"] = false;
                row["fields"] = fields;
                ok = false;
                return row;
            }

            if (p.SetTypeId != null)
            {
                long want = Rid.Value(p.SetTypeId);
                long have = -1;
                string readError = null;
                try { have = Rid.Value(d.GetTypeId()); } catch (Exception ex) { readError = ex.Message; }
                bool m = readError == null && want == have;
                allOk = allOk && m;
                var f = new JObject
                {
                    ["field"] = "set_type_id",
                    ["requested"] = want,
                    ["read"] = readError == null ? (JToken)new JValue(have) : JValue.CreateNull(),
                    ["match"] = m
                };
                if (readError != null) f["read_error"] = readError;
                fields.Add(f);
            }

            if (p.MoveBy != null)
            {
                string kind;
                List<XYZ> after = SamplePoints(d, out kind);
                bool m = after != null && string.Equals(kind, p.SampleKind, StringComparison.Ordinal) &&
                         DimensionEditRules.MovedExactly(
                             Arrays(p.MoveSamples), Arrays(after),
                             new[] { p.MoveBy.X, p.MoveBy.Y, p.MoveBy.Z },
                             DimensionEditRules.DefaultMoveToleranceFeet);
                allOk = allOk && m;
                var f = new JObject
                {
                    ["field"] = "move_by",
                    ["requested_vector_feet"] = new JArray(p.MoveBy.X, p.MoveBy.Y, p.MoveBy.Z),
                    ["sample_kind"] = p.SampleKind,
                    ["before_feet"] = PointsJson(p.MoveSamples),
                    ["read_feet"] = after == null ? (JToken)JValue.CreateNull() : PointsJson(after),
                    ["tolerance_feet"] = DimensionEditRules.DefaultMoveToleranceFeet,
                    ["match"] = m
                };
                if (!m)
                    f["note"] = "Revit re-derives a dimension line from its references, so a displacement " +
                                "component along the dimension's own line does not survive regeneration. A move " +
                                "the committed model does not carry exactly is not reported as one.";
                fields.Add(f);
            }

            TextCheck(fields, "prefix", p.Prefix, delegate { return d.Prefix; }, ref allOk);
            TextCheck(fields, "suffix", p.Suffix, delegate { return d.Suffix; }, ref allOk);
            TextCheck(fields, "above", p.Above, delegate { return d.Above; }, ref allOk);
            TextCheck(fields, "below", p.Below, delegate { return d.Below; }, ref allOk);
            TextCheck(fields, "value_override", p.ValueOverride, delegate { return d.ValueOverride; }, ref allOk);

            if (p.Eq.HasValue) EqCheck(fields, p.Eq.Value, d, ref allOk);
            if (p.Lock.HasValue) BoolCheck(fields, "lock", p.Lock.Value, delegate { return d.IsLocked; }, ref allOk);

            if (p.Segments != null)
                foreach (SegEdit se in p.Segments)
                {
                    DimensionSegment s = SegmentAt(d, se.SegIndex);
                    string at = "segments[" + se.SegIndex.ToString(CultureInfo.InvariantCulture) + "]";
                    if (s == null)
                    {
                        fields.Add(new JObject
                        {
                            ["field"] = at,
                            ["match"] = false,
                            ["read_error"] = "the segment no longer exists at this index"
                        });
                        allOk = false;
                        continue;
                    }
                    DimensionSegment seg = s;
                    TextCheck(fields, at + ".prefix", se.Prefix, delegate { return seg.Prefix; }, ref allOk);
                    TextCheck(fields, at + ".suffix", se.Suffix, delegate { return seg.Suffix; }, ref allOk);
                    TextCheck(fields, at + ".above", se.Above, delegate { return seg.Above; }, ref allOk);
                    TextCheck(fields, at + ".below", se.Below, delegate { return seg.Below; }, ref allOk);
                    TextCheck(fields, at + ".value_override", se.ValueOverride, delegate { return seg.ValueOverride; }, ref allOk);
                    if (se.Lock.HasValue)
                        BoolCheck(fields, at + ".lock", se.Lock.Value, delegate { return seg.IsLocked; }, ref allOk);
                }

            if (p.ResetTextPosition)
            {
                XYZ after = null;
                string readError = null;
                try { after = d.TextPosition; } catch (Exception ex) { readError = ex.Message; }
                var f = new JObject
                {
                    ["field"] = "reset_text_position",
                    ["requested"] = true,
                    ["text_position_before_feet"] = p.TextPositionBefore == null
                        ? (JToken)JValue.CreateNull()
                        : new JArray(p.TextPositionBefore.X, p.TextPositionBefore.Y, p.TextPositionBefore.Z),
                    ["text_position_after_feet"] = after == null
                        ? (JToken)JValue.CreateNull()
                        : new JArray(after.X, after.Y, after.Z),
                    ["match"] = true,
                    ["verification"] = "invocation_completed: Revit publishes no predicate for the default text " +
                                       "position, so this row attests that ResetTextPosition ran inside the " +
                                       "transaction and reports the position before and after where readable - " +
                                       "it does NOT compare against an expected point, because none exists to compare to."
                };
                if (readError != null) f["read_error"] = readError;
                fields.Add(f);
            }

            row["verified"] = allOk;
            row["fields"] = fields;
            ok = allOk;
            return row;
        }

        private static void TextCheck(JArray fields, string name, string requested, Func<string> read, ref bool ok)
        {
            if (requested == null) return;
            string have = null;
            string readError = null;
            try { have = read(); } catch (Exception ex) { readError = ex.Message; }
            bool m = readError == null && DimensionEditRules.TextMatches(requested, have);
            ok = ok && m;
            var f = new JObject
            {
                ["field"] = name,
                ["requested"] = requested,
                ["read"] = readError == null ? (JToken)new JValue(DimensionEditRules.NormalizeText(have)) : JValue.CreateNull(),
                ["match"] = m
            };
            if (readError != null) f["read_error"] = readError;
            // '' is not "no change": it is the deletion of the override, verified by the
            // model answering empty.
            if (name.EndsWith("value_override", StringComparison.Ordinal) && DimensionEditRules.ClearsOverride(requested))
                f["clears_override"] = true;
            fields.Add(f);
        }

        /// <summary>
        /// EQ, decided by SUBSTANCE where the flag lags. Measured on live 2025
        /// (2026-08-24): AreSegmentsEqual can still read false after the EQ edit was
        /// committed and a materialising regeneration ran inside the open group -
        /// the same lazy family as AreReferencesAvailable. What EQ actually DOES is
        /// equalise the segments, and the segment values DO materialise; when the
        /// flag lags but every segment measures the same within tolerance, the edit
        /// demonstrably applied and the row says it stood on substance. A lagging
        /// flag with UNEQUAL segments still fails. eq=false is checked by the flag
        /// alone: un-equalising leaves values wherever the references sit, so there
        /// is no substance to read it from.
        /// </summary>
        private static void EqCheck(JArray fields, bool requested, Dimension d, ref bool ok)
        {
            bool? flag = null;
            string readError = null;
            try { flag = d.AreSegmentsEqual; } catch (Exception ex) { readError = ex.Message; }
            if (!requested || flag == true)
            {
                bool m = flag.HasValue && flag.Value == requested;
                ok = ok && m;
                var direct = new JObject
                {
                    ["field"] = "eq",
                    ["requested"] = requested,
                    ["read"] = flag.HasValue ? (JToken)new JValue(flag.Value) : JValue.CreateNull(),
                    ["match"] = m,
                    ["verified_by"] = "flag"
                };
                if (readError != null) direct["read_error"] = readError;
                fields.Add(direct);
                return;
            }
            var values = new List<double>();
            bool segmentsReadable = true;
            try
            {
                foreach (DimensionSegment s in d.Segments)
                {
                    double? v = s.Value;
                    if (v.HasValue) values.Add(v.Value); else segmentsReadable = false;
                }
            }
            catch { segmentsReadable = false; }
            bool equalised = segmentsReadable && values.Count >= 2;
            if (equalised)
            {
                double min = values[0], max = values[0];
                foreach (double v in values) { if (v < min) min = v; if (v > max) max = v; }
                equalised = (max - min) <= DimensionEditRules.DefaultMoveToleranceFeet;
            }
            ok = ok && equalised;
            var f = new JObject
            {
                ["field"] = "eq",
                ["requested"] = true,
                ["read"] = flag.HasValue ? (JToken)new JValue(flag.Value) : JValue.CreateNull(),
                ["match"] = equalised ? (JToken)JValue.CreateNull() : (JToken)false,
                ["verified_by"] = equalised ? "substance" : "flag",
                ["segment_values_feet"] = new JArray(values)
            };
            if (equalised)
                f["note"] = "AreSegmentsEqual lags after a committed EQ edit (measured live); every segment " +
                            "measures the same within tolerance, which is what EQ does, so the edit is verified " +
                            "by that substance and the flag is reported as observed.";
            if (readError != null) f["read_error"] = readError;
            fields.Add(f);
        }

        private static void BoolCheck(JArray fields, string name, bool requested, Func<bool> read, ref bool ok)
        {
            bool? have = null;
            string readError = null;
            try { have = read(); } catch (Exception ex) { readError = ex.Message; }
            bool m = have.HasValue && have.Value == requested;
            ok = ok && m;
            var f = new JObject
            {
                ["field"] = name,
                ["requested"] = requested,
                ["read"] = have.HasValue ? (JToken)new JValue(have.Value) : JValue.CreateNull(),
                ["match"] = m
            };
            if (readError != null) f["read_error"] = readError;
            fields.Add(f);
        }

        // ---------------------------------------------------------------------
        // Sampling and snapshots.
        // ---------------------------------------------------------------------

        /// <summary>
        /// The geometry a move can be verified against. A bound curve gives two
        /// endpoints; a single-segment dimension without one still exposes its origin.
        /// Null means nothing readable to sample - and therefore nothing to verify,
        /// which the planner treats as a refusal rather than a free pass.
        /// </summary>
        private static List<XYZ> SamplePoints(Dimension d, out string kind)
        {
            kind = null;
            try
            {
                Curve c = d.Curve;
                if (c != null && c.IsBound)
                {
                    kind = "curve_endpoints";
                    return new List<XYZ> { c.GetEndPoint(0), c.GetEndPoint(1) };
                }
            }
            catch { /* fall through to the origin */ }
            try
            {
                if (DimensionEditRules.IsSingleSegment(d.NumberOfSegments))
                {
                    XYZ o = d.Origin;
                    if (o != null) { kind = "origin"; return new List<XYZ> { o }; }
                }
            }
            catch { /* nothing readable */ }
            return null;
        }

        private static DimensionSegment SegmentAt(Dimension d, int index)
        {
            int i = 0;
            foreach (DimensionSegment s in d.Segments)
            {
                if (i == index) return s;
                i++;
            }
            return null;
        }

        /// <summary>
        /// One dimension as the plan resolved it: identity, type, geometry rounded to
        /// 0.1 mm, segment census, current overrides, EQ, lock, and the stable
        /// representation of every reference. Any of these moving between the dry run
        /// and the apply makes the apply a different edit than the one approved, and
        /// the fingerprint refuses it as stale. Every read is guarded: a value that
        /// cannot be read snapshots as a fixed marker, identically on both passes.
        /// </summary>
        private static PlannedElement SnapshotElement(Document doc, Dimension d, Plan p)
        {
            var before = new Dictionary<string, string>
            {
                { "type_id", SafeSnap(delegate { return Rid.Value(d.GetTypeId()).ToString(CultureInfo.InvariantCulture); }) },
                { "type_unique_id", SafeSnap(delegate { Element t = doc.GetElement(d.GetTypeId()); return t == null ? "" : t.UniqueId; }) },
                { "segments", p.SegmentCount.ToString(CultureInfo.InvariantCulture) },
                { "lock", SafeSnap(delegate { return d.IsLocked.ToString(); }) },
                { "references", SafeSnap(delegate { return ReferenceFingerprint(doc, d); }) }
            };
            if (DimensionEditRules.IsSingleSegment(p.SegmentCount))
            {
                before["prefix"] = SafeSnap(delegate { return d.Prefix ?? ""; });
                before["suffix"] = SafeSnap(delegate { return d.Suffix ?? ""; });
                before["above"] = SafeSnap(delegate { return d.Above ?? ""; });
                before["below"] = SafeSnap(delegate { return d.Below ?? ""; });
                before["value_override"] = SafeSnap(delegate { return d.ValueOverride ?? ""; });
                before["value"] = SafeSnap(delegate
                {
                    double? v = d.Value;
                    return v.HasValue ? DimensionEditRules.CanonicalTenthMillimetre(v.Value) : "";
                });
                before["eq"] = "";
            }
            else
            {
                before["prefix"] = ""; before["suffix"] = ""; before["above"] = ""; before["below"] = "";
                before["value_override"] = ""; before["value"] = "";
                before["eq"] = SafeSnap(delegate { return d.AreSegmentsEqual.ToString(); });
                before["segment_values"] = SafeSnap(delegate { return SegmentFingerprint(d); });
            }

            return new PlannedElement
            {
                UniqueId = SafeNull(delegate { return d.UniqueId; }),
                Category = SafeNull(delegate { return d.Category == null ? null : d.Category.Name; }),
                TypeName = SafeNull(delegate { Element t = doc.GetElement(d.GetTypeId()); return t == null ? null : t.Name; }),
                Action = PlannedAction.Modify,
                GeometryFingerprint = GeometryFingerprint(d),
                BeforeValues = before
            };
        }

        private static string GeometryFingerprint(Dimension d)
        {
            string kind;
            List<XYZ> samples = SamplePoints(d, out kind);
            if (samples == null) return null;
            var parts = new List<string>(samples.Count + 1) { kind };
            foreach (XYZ s in samples)
                parts.Add(DimensionEditRules.CanonicalPoint(s.X, s.Y, s.Z));
            return string.Join("|", parts.ToArray());
        }

        private static string ReferenceFingerprint(Document doc, Dimension d)
        {
            ReferenceArray refs = d.References;
            if (refs == null) return "";
            var parts = new List<string>();
            foreach (Reference r in refs)
                parts.Add(SafeSnap(delegate { return r.ConvertToStableRepresentation(doc); }));
            return string.Join("\u001f", parts.ToArray());
        }

        private static string SegmentFingerprint(Dimension d)
        {
            var sb = new StringBuilder();
            int i = 0;
            foreach (DimensionSegment s in d.Segments)
            {
                DimensionSegment seg = s;
                if (i > 0) sb.Append('\u001e');
                sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(':');
                sb.Append(SafeSnap(delegate { return seg.Prefix ?? ""; })).Append('\u001f');
                sb.Append(SafeSnap(delegate { return seg.Suffix ?? ""; })).Append('\u001f');
                sb.Append(SafeSnap(delegate { return seg.Above ?? ""; })).Append('\u001f');
                sb.Append(SafeSnap(delegate { return seg.Below ?? ""; })).Append('\u001f');
                sb.Append(SafeSnap(delegate { return seg.ValueOverride ?? ""; })).Append('\u001f');
                sb.Append(SafeSnap(delegate { return seg.IsLocked.ToString(); })).Append('\u001f');
                sb.Append(SafeSnap(delegate
                {
                    double? v = seg.Value;
                    return v.HasValue ? DimensionEditRules.CanonicalTenthMillimetre(v.Value) : "";
                }));
                i++;
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------------
        // Small helpers.
        // ---------------------------------------------------------------------

        private static List<double[]> Arrays(List<XYZ> points)
        {
            var result = new List<double[]>(points.Count);
            foreach (XYZ p in points) result.Add(new[] { p.X, p.Y, p.Z });
            return result;
        }

        private static JArray PointsJson(List<XYZ> points)
        {
            var result = new JArray();
            foreach (XYZ p in points) result.Add(new JArray(p.X, p.Y, p.Z));
            return result;
        }

        private static XYZ Point(JToken token, double scale, string name)
        {
            JArray a = token as JArray;
            if (a == null || a.Count != 3) throw new ArgumentException(name + " must contain three coordinates");
            return new XYZ(a[0].Value<double>() * scale, a[1].Value<double>() * scale, a[2].Value<double>() * scale);
        }

        private static bool Scale(string units, out double scale)
        {
            if (units == "feet") { scale = 1; return true; }
            if (units == "m") { scale = 1 / 0.3048; return true; }
            if (units == "mm") { scale = 1 / 304.8; return true; }
            scale = 0; return false;
        }

        /// <summary>Guarded snapshot read: an unreadable value is a fixed marker, so it
        /// snapshots identically on the dry run and the apply instead of poisoning the
        /// fingerprint with a throw.</summary>
        private static string SafeSnap(Func<string> f)
        {
            try { return f() ?? ""; } catch { return "(unreadable)"; }
        }

        private static string SafeNull(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }

        private static string SafeCategoryName(Dimension d)
        {
            try { return d.Category == null ? "no category" : d.Category.Name; }
            catch { return "category unreadable"; }
        }

        private sealed class Plan
        {
            public int Index;
            public long RawId;
            public ElementId Id;
            public int SegmentCount;
            public ElementId SetTypeId;
            public XYZ MoveBy;
            public List<XYZ> MoveSamples;
            public string SampleKind;
            public string Prefix, Suffix, Above, Below, ValueOverride;
            public bool? Eq, Lock;
            public List<SegEdit> Segments;
            public bool ResetTextPosition;
            public XYZ TextPositionBefore;
            public JObject Summary;
        }

        private sealed class SegEdit
        {
            public int SegIndex;
            public string Prefix, Suffix, Above, Below, ValueOverride;
            public bool? Lock;
        }
    }
}
