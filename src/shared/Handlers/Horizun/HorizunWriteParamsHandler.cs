// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// horizun_write_params_verified — the WRITE verb, shared by all five processes.
//
// Every skill in the estate has hand-written this same loop:
//
//     try: p.Set(v); n += 1
//     except: pass
//     ...
//     __output__ = "Datos de proyecto actualizados"
//
// and every one of them lies in a different place. The four failures we have
// actually measured, each of which this handler is built to be unable to report
// as success:
//
//   1. `Parameter.Set()` RETURNS A BOOL. It declines writes without throwing —
//      a formula-driven Description, an incompatible ViewTemplateId, an element
//      borrowed by another user. Counting a Set() that returned false is the
//      same class of lie as counting a rolled-back Commit as success. We check
//      the bool, and we still do not trust it (see 3).
//   2. The parameter is null or IsReadOnly, `if p and not p.IsReadOnly` skips it
//      in silence, and the row is counted anyway. Here an unresolvable target or
//      parameter is a row with an error, never a row that vanishes: the counts
//      would otherwise reconcile perfectly while the caller's elements were
//      never touched.
//   3. The setter is accepted and Revit ignores it. So the only value we are
//      willing to call written is one we RE-READ from the document AFTER the
//      commit. value_written != value_read_back is a FAILURE, not a warning.
//   4. The transaction rolls back after the loop and the pre-computed counter is
//      returned anyway — the 758-purged incident, verbatim. We commit only
//      through HorizunGuard.Commit, the terminal transaction state is a
//      first-class field, and a rolled-back transaction forces changed:false on
//      every row no matter what the loop saw.
//
// Two more things this owes to the processes it serves:
//
//   * ONE transaction, one undo step (/codificar rule 5 — the requirement that
//     forces that skill into raw python today, because no bridge tool exposes a
//     transaction at all).
//   * A write to a TYPE re-codes every instance of that type. That is the normal
//     case for Keynote, not the edge case. The blast radius is reported from a
//     full census — deliberately NOT FamilyInstanceFilter, which returns EMPTY
//     without throwing for wall/floor/roof types, i.e. reports a radius of zero
//     for exactly the categories where a type-wide write does the most damage.
//
// And when a batch goes wrong the caller decides, not us: 'atomic' rolls the
// whole thing back (default — a half-coded model is worse than an uncoded one),
// 'best_effort' commits what worked. The mode that actually ran is reported.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class HorizunWriteParamsHandler : IRevitCommand
    {
        public string Name => "horizun_write_params_verified";

        public string Description =>
            "Apply an explicit batch of parameter writes to elements, types or Project Information in ONE " +
            "named transaction (one undo step), then RE-READ every parameter from the model after the commit " +
            "and report value_written vs value_read_back — a difference is an explicit failure, not a warning. " +
            "Targets resolve by BuiltInParameter name, shared-parameter GUID, or parameter name (ambiguity is " +
            "an error). Parameter.Set() returning false is reported as a refused write, never counted. Writing " +
            "to a type re-codes every instance of it: the blast radius is measured and reported. on_failure=" +
            "'atomic' (default) rolls the whole batch back; 'best_effort' commits what worked. The terminal " +
            "transaction state is a first-class field. A unit STRING on Double/Integer storage is applied with " +
            "SetValueString, which parses the units inside Revit and never returns the parsed number, so those rows " +
            "can only be verified against a re-read of themselves: they are counted separately under " +
            "writes_confirmed_by_parse_read_back_only and never claimed as verified against your value. Use " +
            "dry_run=true to resolve every target and see what would be written without opening a transaction.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""writes""],
  ""properties"": {
    ""writes"": {
      ""type"": ""array"", ""minItems"": 1,
      ""description"": ""The batch. Each entry names ONE parameter on ONE target."",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""parameter"", ""value""],
        ""properties"": {
          ""target_id"": { ""type"": ""integer"", ""description"": ""Element id OR type id. Omit (or set target='project_info') to write a Project Information field. Writing a type id affects every instance of that type."" },
          ""target"": { ""type"": ""string"", ""enum"": [""project_info""], ""description"": ""Write to the document's Project Information element instead of an id."" },
          ""parameter"": { ""type"": ""string"", ""description"": ""A BuiltInParameter name (e.g. KEYNOTE_PARAM), a shared/project parameter GUID, or a parameter name as it reads in the UI. A name matching more than one parameter is an error, not a guess."" },
          ""value"": { ""description"": ""String | number | boolean | null. Coerced to the parameter's storage type; a value that cannot be coerced is an error naming that storage type, never a silent skip. For Double/Integer storage, a STRING value is applied with SetValueString (unit-aware, e.g. '3000 mm'); a NUMBER is applied raw, in Revit internal units (feet)."" }
        }
      }
    },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: write params"",
                            ""description"": ""The label of the single undo step this batch becomes."" },
    ""on_failure"": { ""type"": ""string"", ""enum"": [""atomic"", ""best_effort""], ""default"": ""atomic"",
                      ""description"": ""atomic: if ANY write fails, roll the whole batch back — nothing partial. best_effort: commit what worked and report the rest."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": false,
                   ""description"": ""Resolve every target and parameter and report what WOULD be written. Opens no transaction."" },
    ""allow_vary_between_groups"": { ""type"": ""boolean"", ""default"": true,
                                     ""description"": ""Call SetAllowVaryBetweenGroups(true) on project/shared parameters that do not vary yet. Without it Revit throws a modal at write time in any model with groups, which hangs the bridge. Reported as measured off the InternalDefinition, not assumed."" },
    ""target_document_title"": { ""type"": ""string"",
                                 ""description"": ""If given, the write aborts unless the active document's title matches. Writing to whichever model happened to be in front is how a batch lands in the wrong file."" },
    ""max_rows"": { ""type"": ""integer"", ""default"": 500, ""minimum"": 1,
                    ""description"": ""How many rows to include in the response. Totals are always exact regardless of this; truncation is reported."" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            _census = null;   // the model may have changed since the last request
            _censusUnreadable = 0;
            _censusUnreadableError = null;

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            // Writing into whichever model happened to be active is not a write we
            // can honestly report; the caller named a document, so prove it.
            var wantTitle = request.Value<string>("target_document_title");
            if (!string.IsNullOrEmpty(wantTitle))
            {
                var haveTitle = SafeTitle(doc);
                if (!string.Equals(haveTitle, wantTitle, StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail(
                        "Active document is '" + (haveTitle ?? "(title unreadable)") + "', not '" + wantTitle +
                        "'. Nothing was written. Activate the intended document, or drop target_document_title " +
                        "if you really mean whatever is in front.");
            }

            var writesToken = request["writes"] as JArray;
            if (writesToken == null || writesToken.Count == 0)
                return CommandResult.Fail("writes is required and must be a non-empty array.");

            var mode = (request.Value<string>("on_failure") ?? "atomic").ToLowerInvariant();
            if (mode != "atomic" && mode != "best_effort")
                return CommandResult.Fail("on_failure must be 'atomic' or 'best_effort'.");

            bool dryRun = request.Value<bool>("dry_run");
            bool allowVary = request["allow_vary_between_groups"] == null || request.Value<bool>("allow_vary_between_groups");
            var txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: write params";
            int maxRows = 500;
            if (request["max_rows"] != null) maxRows = Math.Max(1, request.Value<int>("max_rows"));

            // ---- Resolve everything BEFORE any transaction opens. ----
            var rows = new List<Row>();
            for (int i = 0; i < writesToken.Count; i++)
            {
                var row = new Row { Index = i };
                rows.Add(row);

                var w = writesToken[i] as JObject;
                if (w == null) { row.Error = "Entry is not an object."; continue; }

                row.ParamSpec = w.Value<string>("parameter");
                row.Requested = w["value"];
                if (string.IsNullOrWhiteSpace(row.ParamSpec)) { row.Error = "parameter is required."; continue; }
                if (row.Requested == null) { row.Error = "value is required (use null explicitly only where the storage type allows it)."; continue; }

                Element target = ResolveTarget(doc, w, out row.TargetKind, out row.TargetId, out string whyT);
                if (target == null) { row.Error = whyT; continue; }
                row.Target = target;
                row.TargetName = SafeName(target);

                row.Parameter = ResolveParameter(target, row.ParamSpec, out row.MatchedBy, out string whyP);
                if (row.Parameter == null) { row.Error = whyP; continue; }

                row.Storage = row.Parameter.StorageType.ToString();
                row.ReadOnly = SafeReadOnly(row.Parameter);
                row.Before = ReadValue(row.Parameter);
                row.VariesBefore = ReadVaries(row.Parameter);

                if (row.ReadOnly == true)
                {
                    row.Error = "Parameter '" + SafeDefName(row.Parameter) + "' is read-only on this target" +
                                (IsFormulaHint(row.Parameter) ? " (it may be driven by a formula — Revit computes it and refuses writes)" : "") +
                                ". A read-only parameter that is skipped in silence is how a batch reports success and changes nothing.";
                    continue;
                }
                if (row.Parameter.StorageType == StorageType.None)
                {
                    row.Error = "Parameter '" + SafeDefName(row.Parameter) + "' has storage type None; there is no value to write.";
                    continue;
                }
            }

            // Blast radius: a type-level write reaches every instance, including the ones
            // the caller never named. Affected and Collateral are computed from ONE census
            // lookup in ONE loop on purpose: when Affected was computed in the resolve loop
            // (which `continue`s past error rows) and Collateral in this one (which does
            // not), an error row on a type shipped elements_affected:0 next to
            // collateral_elements:37 — two counts of the same set that cannot both be true.
            var namedIds = new HashSet<string>(rows.Where(r => r.Target != null).Select(r => r.Target.Id.ToString()));
            foreach (var r in rows.Where(r => r.TargetKind == "type" && r.Target != null))
            {
                var instances = InstancesOfType(doc, r.Target.Id);
                r.Affected = instances.Count;
                r.Collateral = instances.Count(id => !namedIds.Contains(id.ToString()));
                // Whatever the census could not read, this row's counts inherit.
                r.CensusUnreadable = _censusUnreadable;
                r.CensusUnreadableError = _censusUnreadableError;
            }

            var planned = rows.Where(r => r.Error == null).ToList();
            int unresolved = rows.Count - planned.Count;

            if (dryRun)
            {
                var wouldReach = ReachedBy(doc, planned);
                return CommandResult.Ok(new JObject
                {
                    ["mode"] = "dry_run",
                    ["on_failure_if_run"] = mode,
                    ["transaction_status"] = "not_started",
                    ["transaction_name"] = txName,
                    ["document"] = SafeTitle(doc),
                    ["requested"] = rows.Count,
                    ["writes_planned"] = planned.Count,
                    ["unresolved"] = unresolved,
                    ["elements_that_would_change"] = wouldReach.Count,
                    ["collateral_elements"] = wouldReach.Count(id => !namedIds.Contains(id.ToString())),
                    ["census_unreadable_elements"] = _censusUnreadable,
                    ["blast_radius_is_lower_bound"] = _censusUnreadable > 0,
                    ["blast_radius_note"] = BlastRadiusNote(),
                    ["rows_total"] = rows.Count,
                    ["rows_shown"] = Math.Min(rows.Count, maxRows),
                    ["rows_truncated"] = rows.Count > maxRows,
                    ["rows"] = new JArray(rows.Take(maxRows).Select(r => (JToken)r.ToJson(false))),
                    ["note"] = "Nothing was written; no transaction was opened. " +
                               (unresolved > 0
                                   ? unresolved + " row(s) could not even be resolved — see their 'error'. Those are not writes that would 'probably work'. "
                                   : "") +
                               "Re-run with dry_run=false."
                });
            }

            // ---- Write. One transaction, one undo step. ----
            string txStatus;
            string txNote = null;
            bool committed = false;
            int accepted = 0;

            if (planned.Count == 0)
            {
                // Nothing resolved. Opening a transaction here would let us report a
                // clean Committed over a batch that touched nothing.
                return CommandResult.Ok(new JObject
                {
                    ["mode"] = mode,
                    ["transaction_status"] = "not_started",
                    ["transaction_name"] = txName,
                    ["document"] = SafeTitle(doc),
                    ["requested"] = rows.Count,
                    ["writes_attempted"] = 0,
                    ["writes_confirmed"] = 0,
                    ["writes_confirmed_against_your_value"] = 0,
                    ["writes_confirmed_by_parse_read_back_only"] = 0,
                    ["unresolved"] = unresolved,
                    ["rows_total"] = rows.Count,
                    ["rows_shown"] = Math.Min(rows.Count, maxRows),
                    ["rows_truncated"] = rows.Count > maxRows,
                    ["rows"] = new JArray(rows.Take(maxRows).Select(r => (JToken)r.ToJson(true))),
                    ["note"] = "No transaction was opened: not one of the " + rows.Count +
                               " row(s) resolved to a writable parameter. The model is untouched."
                });
            }

            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    // Swallowing the modal is the point: a Revit dialog here does not
                    // wait for a human, it hangs the bridge until the request times
                    // out and the caller retries a batch that may already be half in.
                    var opts = tx.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new SilenceModals());
                    opts.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(opts);

                    foreach (var r in planned)
                    {
                        // Without this, Revit throws the "desagrupar" modal mid-batch in
                        // any model with groups — far from here, and with the batch open.
                        if (allowVary && r.VariesBefore == false)
                        {
                            var def = SafeInternalDef(r.Parameter);
                            if (def != null)
                            {
                                try { def.SetAllowVaryBetweenGroups(doc, true); }
                                catch (Exception ex) { r.VaryError = "SetAllowVaryBetweenGroups failed: " + ex.Message; }
                            }
                        }

                        bool ok;
                        try { ok = TryApply(r.Parameter, r.Requested, out r.Accepted, out r.How, out r.Expected, out r.ExpectationFromModel, out string whyC); r.Error = whyC; }
                        catch (Exception ex) { ok = false; r.Accepted = false; r.SetThrew = true; r.Error = "Set threw: " + ex.Message; }

                        if (!ok) continue;
                        if (!r.Accepted)
                        {
                            // Set() returned false. It did not throw, and nothing changed.
                            r.Error = "Revit REFUSED the write (Parameter.Set returned false, without throwing). " +
                                      "Nothing changed on this target. Typical causes: the value is driven by a " +
                                      "formula, the element is borrowed by another user, or the value is not " +
                                      "acceptable for this parameter.";
                            continue;
                        }
                        accepted++;
                    }

                    // Formula-driven and derived values do not settle until a regen, so
                    // reading back before this would read our own intent, not the model.
                    doc.Regenerate();
                    foreach (var r in planned) r.Written = ReadValue(r.Parameter);

                    // Rows that Revit accepted but did not actually apply are failures
                    // NOW, while we can still honour 'atomic'. This compares the model
                    // against the CALLER'S value: comparing it against another read of
                    // itself is a tautology that passes even when Revit stored nothing.
                    foreach (var r in planned.Where(r => r.Error == null && r.Accepted))
                    {
                        if (!SameValue(r.Written, r.Expected))
                        {
                            r.InTxDrift = true;
                            r.Error = DriftError(r);
                        }
                    }

                    // Drift rows carry their own error now, so counting them again here
                    // would roll back an atomic batch on an inflated failure count.
                    int failedNow = rows.Count(r => r.Error != null);

                    if (mode == "atomic" && failedNow > 0)
                    {
                        tx.RollBack();
                        txStatus = "RolledBack";
                        txNote = "on_failure='atomic' and " + failedNow + " of " + rows.Count + " row(s) failed, so the " +
                                 "WHOLE batch was rolled back. Nothing was written — including the rows that worked. " +
                                 "This is the default because a half-coded model is worse than an uncoded one. " +
                                 "Fix the failing rows, or re-run with on_failure='best_effort' to keep the ones that land.";
                    }
                    else
                    {
                        // Turns a silent rollback into an error instead of a false success.
                        HorizunGuard.Commit(tx, txName);
                        txStatus = "Committed";
                        committed = true;
                    }
                }
                catch (HorizunSilentRollbackException ex)
                {
                    // Revit rolled back and returned a status instead of throwing. Every
                    // counter above is now fiction; say so and report nothing written.
                    foreach (var r in rows) { r.Written = null; r.Accepted = false; }
                    return CommandResult.Ok(new JObject
                    {
                        ["mode"] = mode,
                        ["transaction_status"] = "RolledBack",
                        ["transaction_name"] = txName,
                        ["document"] = SafeTitle(doc),
                        ["requested"] = rows.Count,
                        ["writes_attempted"] = planned.Count,
                        ["writes_confirmed"] = 0,
                        ["writes_confirmed_against_your_value"] = 0,
                        ["writes_confirmed_by_parse_read_back_only"] = 0,
                        ["unresolved"] = unresolved,
                        ["rows_total"] = rows.Count,
                        ["rows_shown"] = Math.Min(rows.Count, maxRows),
                        ["rows_truncated"] = rows.Count > maxRows,
                        ["rows"] = new JArray(rows.Take(maxRows).Select(r => (JToken)r.ToJson(true))),
                        ["note"] = ex.Message + " Every row reports changed:false regardless of what the write loop saw."
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Write batch failed and was rolled back; nothing was written: " + ex.Message);
                }
            }

            // ---- The only evidence that counts: a fresh read, after the commit. ----
            // Every row learns the transaction's fate before it renders: a row's prose
            // makes claims about the model ("WILL carry this value"), and those are only
            // true if this commit actually happened.
            foreach (var r in rows)
            {
                r.Committed = committed;
                if (r.Target == null || r.Parameter == null) continue;
                r.ReadBack = ReReadFromModel(doc, r);
            }

            int confirmed = 0;
            int confirmedAgainstCallerValue = 0;
            int confirmedByParseOnly = 0;
            foreach (var r in rows)
            {
                // Two distinct claims, and the row is only confirmed if BOTH hold: the
                // model now carries the value the CALLER asked for (Expected vs ReadBack),
                // and nothing moved between the in-transaction read and the commit
                // (Written vs ReadBack). The second one alone is the model agreeing with
                // itself — it is a drift check, it is not evidence of intent.
                r.Confirmed = committed
                              && r.Error == null
                              && r.Accepted
                              && Readable(r.Expected)
                              && Readable(r.Written)
                              && Readable(r.ReadBack)
                              && SameValue(r.ReadBack, r.Expected)
                              && SameValue(r.Written, r.ReadBack);
                if (r.Confirmed)
                {
                    confirmed++;
                    // Not every 'confirmed' is the same claim, and one bare bool cannot
                    // say which. On the SetValueString path Expected is itself a read of
                    // the parameter, so Expected/Written/ReadBack are three reads of one
                    // stored value: if Revit accepts the setter and stores nothing
                    // (header failure #3), all three agree and the row confirms anyway.
                    // The caller's "3000 mm" was never compared to anything. Split the
                    // total so a consumer cannot add the two kinds of evidence together.
                    if (r.ExpectationFromModel) confirmedByParseOnly++;
                    else confirmedAgainstCallerValue++;
                }

                if (committed && r.Error == null && r.Accepted && !r.Confirmed)
                    r.Error = PostCommitError(r);

                // RolledBack forces changed:false on every row, whatever the loop counted.
                if (!committed) r.Confirmed = false;

                // Classify AFTER the row knows the commit's fate and carries its final
                // error: the three-way split is the only thing that keeps an unknown out
                // of a total a consumer reads as known.
                r.Outcome = Classify(r);
            }

            // `failed` used to be rows.Count - confirmed, which swept UNKNOWN rows in with
            // the known-absent ones and let the closing note say "N row(s) are NOT written"
            // about a row this handler had just declared it could not see (PostCommitError:
            // "Whether the value is in the model is UNKNOWN"). Unknown now has its own
            // counter, so it has somewhere to go other than a claim of absence.
            int failed = rows.Count(r => r.Outcome == OUT_NOT_WRITTEN);
            int unknown = rows.Count(r => r.Outcome == OUT_UNKNOWN);

            // The blast radius is the DISTINCT set of elements the confirmed rows reach.
            // Summed per row it double-counts every element two rows share — the normal
            // case, e.g. Keynote + a PRD_ param on one type — and reports twice the
            // elements the model actually carries the write on.
            var reached = committed ? ReachedBy(doc, rows.Where(r => r.Confirmed)) : new HashSet<ElementId>();

            return CommandResult.Ok(new JObject
            {
                ["mode"] = mode,
                ["transaction_status"] = txStatus,
                ["transaction_name"] = txName,
                ["document"] = SafeTitle(doc),
                ["requested"] = rows.Count,
                ["writes_attempted"] = planned.Count,
                ["writes_accepted_by_set"] = accepted,
                ["writes_confirmed"] = confirmed,
                ["writes_confirmed_against_your_value"] = confirmedAgainstCallerValue,
                ["writes_confirmed_by_parse_read_back_only"] = confirmedByParseOnly,
                ["writes_confirmed_note"] = ParseOnlyNote(confirmedByParseOnly),
                // failed means ONE thing: the model was read back and does not carry the
                // value. A row we could not read back is `unknown`, never failed.
                ["failed"] = failed,
                ["unknown"] = unknown,
                ["unknown_note"] = UnknownNote(unknown),
                ["unresolved"] = unresolved,
                // Distinct elements that now carry a confirmed write, the written type
                // elements INCLUDED — the same meaning as a row's elements_affected.
                ["elements_affected"] = reached.Count,
                ["collateral_elements"] = reached.Count(id => !namedIds.Contains(id.ToString())),
                ["census_unreadable_elements"] = _censusUnreadable,
                ["blast_radius_is_lower_bound"] = _censusUnreadable > 0,
                ["blast_radius_note"] = BlastRadiusNote(),
                // NOT HorizunGuard.Verify. Verify takes two integers and renders a bool plus
                // one line of fixed prose, and neither can carry what this handler knows.
                // Fed (rows.Count, confirmed) it told two lies at once: `verified:true`
                // whenever the totals matched — including for rows confirmed only against a
                // re-read of themselves, which is the exact merge that
                // writes_confirmed_against_your_value / _by_parse_read_back_only exists to
                // prevent — and, on any mismatch, "The difference was NOT applied", a
                // positive claim of absence over a difference that includes rows whose own
                // error says their fate is UNKNOWN. Report the split instead.
                ["verification"] = Verification(rows.Count, confirmedAgainstCallerValue,
                                                confirmedByParseOnly, failed, unknown),
                ["rows_total"] = rows.Count,
                ["rows_shown"] = Math.Min(rows.Count, maxRows),
                ["rows_truncated"] = rows.Count > maxRows,
                ["rows"] = new JArray(rows.Take(maxRows).Select(r => (JToken)r.ToJson(true))),
                ["note"] = txNote ?? BatchNote(mode, rows.Count, confirmed, failed, unknown)
            });
        }

        // ---- The three-way split. -------------------------------------------------
        // A row is written, not written, or unknown. There is no fourth bucket, and the
        // third one is not a polite name for the second: PostCommitError already tells the
        // caller "UNKNOWN — which is not the same as it being absent, and not the same as
        // it being there", and the aggregate is not allowed to contradict the row.
        private const string OUT_CONFIRMED = "confirmed";
        private const string OUT_NOT_WRITTEN = "not_written";
        private const string OUT_UNKNOWN = "unknown";

        private static string Classify(Row r)
        {
            if (r.Confirmed) return OUT_CONFIRMED;

            // A rollback is not a guess: Revit undid the transaction, so nothing from this
            // batch reached the model. Known-absent, and the only claim of absence here
            // that does not rest on a read.
            if (!r.Committed) return OUT_NOT_WRITTEN;

            // Never reached the setter — unresolved target or parameter, read-only, storage
            // None, a value that could not be coerced. `How` is assigned only on the paths
            // that call Set/SetValueString, so a null How means the model was never touched.
            if (r.How == null) return OUT_NOT_WRITTEN;

            // Set() returned false without throwing: Revit declined and changed nothing.
            // A setter that THREW is a different animal — we never got an answer — so it
            // falls through to the evidence below rather than being called absent.
            if (!r.Accepted && !r.SetThrew) return OUT_NOT_WRITTEN;

            // From here the setter ran and the batch committed, so only the post-commit
            // read can separate "the model does not carry it" from "we could not look".
            if (!Readable(r.ReadBack)) return OUT_UNKNOWN;
            if (!Readable(r.Expected)) return OUT_UNKNOWN;
            if (!SameValue(r.ReadBack, r.Expected)) return OUT_NOT_WRITTEN;

            // The model holds the requested value but it moved between the in-transaction
            // read and the commit, so this write is not provably its author. Reporting that
            // as NOT written would be false in the plainest way: the value is right there.
            return OUT_UNKNOWN;
        }

        /// <summary>
        /// What the response says about the batch as a whole. `verified` is true only when
        /// EVERY row was proven against the caller's own value — a parse-only row proves
        /// the model agrees with itself and nothing more, and a bool that counts it cannot
        /// be read as "the model holds what I asked for".
        /// </summary>
        private static JObject Verification(int rowsTotal, int againstYourValue, int parseOnly,
                                            int notWritten, int unknown)
        {
            return new JObject
            {
                ["what"] = "parameter writes",
                ["rows"] = rowsTotal,
                ["confirmed_against_your_value"] = againstYourValue,
                ["confirmed_by_parse_read_back_only"] = parseOnly,
                ["not_written"] = notWritten,
                ["unknown"] = unknown,
                ["verified"] = againstYourValue == rowsTotal,
                ["verified_means"] = "true only if every row was re-read from the model AFTER the commit and matched " +
                                     "the value YOU passed. false does not mean the writes failed — read not_written " +
                                     "and unknown, they are different outcomes.",
                ["note"] = VerificationNote(rowsTotal, againstYourValue, parseOnly, notWritten, unknown)
            };
        }

        private static string VerificationNote(int rowsTotal, int againstYourValue, int parseOnly,
                                               int notWritten, int unknown)
        {
            if (againstYourValue == rowsTotal) return null;

            var parts = new List<string>();
            parts.Add("Only " + againstYourValue + " of " + rowsTotal + " row(s) were verified against the value you " +
                      "passed. The rest are NOT one outcome, and must not be added together:");
            if (parseOnly > 0)
                parts.Add(parseOnly + " row(s) are confirmed only against a re-read of themselves (SetValueString " +
                          "parsed your units inside Revit and never returned the number) — see writes_confirmed_note. " +
                          "That is proof of no drift, NOT proof the model holds what your string meant.");
            if (notWritten > 0)
                parts.Add(notWritten + " row(s) are NOT written: the model was re-read after the commit and does not " +
                          "carry the value.");
            if (unknown > 0)
                parts.Add(unknown + " row(s) are UNKNOWN: the setter ran, the transaction committed, and the model " +
                          "could not be read back to settle it. Unknown is not absent and not present — do not treat " +
                          "it as either, and do not re-run blindly. See each row's 'error'.");
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>
        /// Null when nothing is unknown: a caveat about a bucket with nothing in it is
        /// noise the real warning would hide behind.
        /// </summary>
        private static string UnknownNote(int unknown)
        {
            if (unknown == 0) return null;
            return unknown + " row(s) whose written state could not be established are counted HERE and in neither " +
                   "writes_confirmed nor failed. The setter ran and the commit is done, but the parameter could not " +
                   "be re-read from the model afterwards, so 'the value is in the model' and 'the value is not in the " +
                   "model' are both unproven. Counting these as failed would publish 'I could not look' as 'it is " +
                   "absent'; counting them as written would be the lie this handler exists to prevent.";
        }

        private static string BatchNote(string mode, int rowsTotal, int confirmed, int notWritten, int unknown)
        {
            if (notWritten == 0 && unknown == 0) return null;

            var parts = new List<string>();
            if (notWritten > 0)
                parts.Add(notWritten + " of " + rowsTotal + " row(s) are NOT written: the model was re-read after the " +
                          "commit and does not carry the value.");
            if (unknown > 0)
                parts.Add(unknown + " of " + rowsTotal + " row(s) are UNKNOWN — attempted, committed, and impossible " +
                          "to verify either way. They are counted as neither written nor failed.");
            parts.Add(confirmed + " row(s) confirmed. The commit is DONE" +
                      (mode == "best_effort" ? " (on_failure='best_effort' kept the rows that landed)" : "") +
                      ", so the model may now be partially coded — see each row's 'error'.");
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>
        /// writes_confirmed mixes two kinds of evidence and a bare integer cannot show it.
        /// A row applied with SetValueString is verified against a re-read of the parameter,
        /// because Revit parses the units and never hands the parsed number back — so the
        /// caller's literal never enters the comparison and the check cannot fail the way
        /// an intent check can. Null when no such row confirmed, because a caveat on a total
        /// that does not contain one is noise the real warning would hide behind.
        /// </summary>
        private static string ParseOnlyNote(int parseOnly)
        {
            if (parseOnly == 0) return null;
            return parseOnly + " of the confirmed row(s) were applied with SetValueString (a STRING value on " +
                   "Double/Integer storage). Their expectation is a re-read of the parameter taken immediately " +
                   "after the setter, NOT your literal value — Revit parsed the units internally and never returned " +
                   "the number it parsed. So for those rows 'confirmed' proves the value did not drift between the " +
                   "setter and the commit; it CANNOT prove Revit stored what your string meant, because if Revit " +
                   "accepted the setter and stored nothing, the expectation would be that nothing too and the row " +
                   "would still confirm. writes_confirmed_against_your_value is the count that carries your intent. " +
                   "To get an intent-verified row, pass a NUMBER in Revit internal units (feet) instead of a unit " +
                   "string, or read the row's value_read_back and judge it yourself.";
        }

        /// <summary>
        /// The one thing the caller must not conclude from a clean-looking radius: that the
        /// scan was complete. Null when every element was read, because a note on a sound
        /// count is noise the real warning would hide behind.
        /// </summary>
        private string BlastRadiusNote()
        {
            if (_censusUnreadable == 0) return null;
            return _censusUnreadable + " element(s) could not be read while scanning the model for instances of the " +
                   "targeted type(s)" + (_censusUnreadableError == null ? "" : " (first failure: " + _censusUnreadableError + ")") +
                   ". elements_affected and collateral_elements are therefore a LOWER BOUND — the real blast radius " +
                   "may be larger. A count of 0 collateral here means 'none found', NOT 'none exist': do not conclude " +
                   "from it that no unnamed element carries this value.";
        }

        /// <summary>
        /// Set() came back true and the model still does not hold the caller's value —
        /// failure #3 from the header, caught while 'atomic' can still undo it.
        /// </summary>
        private static string DriftError(Row r)
        {
            if (!Readable(r.Expected))
                return "Revit ACCEPTED this write but there is no value to verify it against: " +
                       Reason(r.Expected) + " An unverifiable write is not a written one.";

            if (!Readable(r.Written))
                return "Revit ACCEPTED this write, but the parameter could not be re-read inside the transaction: " +
                       Reason(r.Written) + " Whether the value landed is UNKNOWN, so it is not confirmed.";

            return "Revit ACCEPTED this write (Set returned true) and then did NOT apply it: the parameter reads " +
                   Show(r.Written) + " but " + Show(r.Expected) + " was requested. Revit silently coercing, " +
                   "truncating or discarding a value it accepted is the failure this handler exists to catch. " +
                   "Nothing is wrong with the transaction; the value simply is not what you asked for.";
        }

        /// <summary>Committed, and the row still cannot be called written. Names which claim failed.</summary>
        private static string PostCommitError(Row r)
        {
            string tail = " The commit is DONE; this cannot be undone from here. Do not treat this row as coded.";

            if (!Readable(r.Expected))
                return "The transaction COMMITTED, but this write can never be verified: " + Reason(r.Expected) + tail;

            if (!Readable(r.ReadBack))
                return "The transaction COMMITTED, but the parameter could not be re-read from the model afterwards: " +
                       Reason(r.ReadBack) + " Whether the value is in the model is UNKNOWN — which is not the same as " +
                       "it being absent, and not the same as it being there." + tail;

            if (!SameValue(r.ReadBack, r.Expected))
                return "The transaction COMMITTED, but the model does not hold what you asked for: it reads " +
                       Show(r.ReadBack) + " and " + Show(r.Expected) + " was requested." + tail;

            // Expected matches, so the value is right, but it moved after we read it —
            // something else in the commit wrote this parameter. Report it, do not smooth it.
            return "The transaction COMMITTED and the model holds the requested value, but it CHANGED between the " +
                   "in-transaction read (" + Show(r.Written) + ") and the post-commit read (" + Show(r.ReadBack) +
                   "). Something else in this commit touched this parameter, so the write is not the only author of " +
                   "the value." + tail;
        }

        private static string Reason(JObject v)
        {
            if (v == null) return "no value was ever captured for this row.";
            var e = v["error"];
            return e != null && e.Type == JTokenType.String ? v.Value<string>("error") : "reason unrecorded.";
        }

        private static string Show(JObject v)
        {
            if (!Readable(v)) return "(unreadable)";
            var t = v["value"];
            return t == null || t.Type == JTokenType.Null ? "(null)" : "'" + t.ToString() + "'";
        }

        // ---------------------------------------------------------------------
        private class Row
        {
            public int Index;
            public string ParamSpec;
            public JToken Requested;
            public Element Target;
            public string TargetKind;      // instance | type | project_info
            public long? TargetId;
            public string TargetName;
            public Parameter Parameter;
            public string MatchedBy;       // builtin | guid | name
            public string Storage;
            public bool? ReadOnly;
            public bool? VariesBefore;
            public string VaryError;
            public JObject Before;
            // What the caller asked for, in the shape the model stores it — computed at
            // apply time from Requested. Comparing Written against another read of the
            // same parameter compares the model to itself and can never fail; this is
            // the only value that carries the caller's intent into the verification.
            public JObject Expected;
            // True when Expected came from ReadValue(p) instead of from the caller (the
            // SetValueString path). Then Expected/Written/ReadBack are all reads of one
            // stored value and 'Confirmed' cannot fail on intent — it must not be counted
            // or rendered as though it had been checked against what was asked for.
            public bool ExpectationFromModel;
            public JObject Written;        // read inside the transaction, after Regenerate
            public JObject ReadBack;       // read fresh, after the commit
            public bool Accepted;          // what Parameter.Set() returned
            // Set() threw instead of returning a bool. Accepted stays false, but false here
            // means "we never got an answer", not "Revit declined" — and only the second one
            // is evidence that nothing changed. Without this flag the two collapse and a
            // thrown setter gets reported as a known-absent write.
            public bool SetThrew;
            public bool InTxDrift;
            public bool Confirmed;
            // confirmed | not_written | unknown. The three-way split the response is built
            // on: a row whose fate we could not establish is NOT a failed row, and summing
            // it into one is how "I could not look" is published as "it is absent".
            public string Outcome;
            // The transaction's terminal fate. A row's prose asserts things about the model
            // ("N elements WILL carry this value"); those are false on a rollback, so the
            // row is not allowed to render before it knows this.
            public bool Committed;
            public string How;
            public string Error;
            public int Affected;
            public int Collateral;
            // A census that could not read every element makes Affected/Collateral a
            // LOWER bound, and this row must not present them as a complete scan.
            public int CensusUnreadable;
            public string CensusUnreadableError;

            public JObject ToJson(bool wrote)
            {
                var o = new JObject
                {
                    ["index"] = Index,
                    ["target_kind"] = TargetKind,
                    ["target_id"] = TargetId.HasValue ? (JToken)TargetId.Value.ToString() : null,
                    ["target_name"] = TargetName,
                    ["parameter"] = ParamSpec,
                    ["matched_by"] = MatchedBy,
                    ["storage_type"] = Storage,
                    ["read_only"] = ReadOnly.HasValue ? (JToken)ReadOnly.Value : null,
                    ["varies_between_groups_before"] = VariesBefore.HasValue ? (JToken)VariesBefore.Value : null,
                    ["vary_between_groups_error"] = VaryError,
                    ["requested"] = Requested,
                    ["applied_via"] = How,
                    ["before"] = Before,
                    ["set_accepted"] = wrote ? (JToken)Accepted : null,
                    ["value_expected"] = Expected,
                    ["value_written"] = Written,
                    ["value_read_back"] = ReadBack,
                    ["confirmed"] = wrote ? (JToken)Confirmed : null,
                    // confirmed:false answers one question ("is it proven?") and callers
                    // read it as another ("did it fail?"). This says which of the two
                    // non-confirmed outcomes this row is, so the row cannot be summed into
                    // a failure count it does not belong in.
                    ["outcome"] = Outcome,
                    ["confirmed_against"] = wrote && Confirmed
                        ? (JToken)(ExpectationFromModel ? "a_re_read_not_your_value" : "your_value")
                        : null,
                    ["confirmation_caveat"] = wrote && Confirmed && ExpectationFromModel
                        ? (JToken)("This row is confirmed only in the weaker sense: Revit parsed your string's units " +
                                   "internally and never returned the number, so value_expected is a re-read of the " +
                                   "parameter, not your value. Nothing here compared '" + (Requested == null ? "" : Requested.ToString()) +
                                   "' against the model. If Revit accepted the setter and stored nothing, this row " +
                                   "would still say confirmed. Judge value_read_back yourself.")
                        : null,
                    ["error"] = Error
                };
                if (TargetKind == "type")
                {
                    // elements_affected must mean ONE thing across this response: the
                    // distinct elements carrying the value, the written type element
                    // INCLUDED — which is what the document-level total counts. Emitting
                    // instances-only under the same name is why one type row with 50
                    // instances read 50 here and 51 at the top of the same response.
                    o["elements_affected"] = Affected + 1;
                    o["instances_of_this_type"] = Affected;
                    o["collateral_elements"] = Collateral;
                    o["blast_radius_is_lower_bound"] = CensusUnreadable > 0;
                    o["collateral_note"] = CollateralNote(wrote);
                }
                return o;
            }

            private string CollateralNote(bool wrote)
            {
                string incomplete = CensusUnreadable == 0
                    ? null
                    : CensusUnreadable + " element(s) in this model could not be read while counting instances of " +
                      "this type" + (CensusUnreadableError == null ? "" : " (first failure: " + CensusUnreadableError + ")") +
                      ", so these counts are a LOWER BOUND: any of them may be an instance of this type and would " +
                      "carry the value too. This is not a scan that came back clean.";

                // Tense is a claim, and this one is about the model. "WILL carry this value"
                // is only true if this row's write reached the document: on an atomic
                // rollback the response says transaction_status:"RolledBack" while this note
                // asserted a blast radius for a write that was undone, and an errored row in
                // a best_effort run asserted one for a write that never landed. Gate it on
                // the outcome, never on the intent.
                string fate;
                if (!wrote)
                    fate = "WOULD carry this value if this row is run — nothing was written, this is a dry run";
                else if (Committed && Confirmed)
                    fate = "CARRY this value now: the write committed and the model was re-read to prove it";
                else if (Committed)
                    fate = "do NOT carry this value from this row — it is not confirmed written (see this row's " +
                           "'error'); this is the blast radius the write WOULD have had";
                else
                    fate = "do NOT carry this value — the transaction did not commit, so nothing was written; this " +
                           "is the blast radius the write WOULD have had";

                if (Collateral > 0)
                {
                    var s = Collateral + " element(s) you did not name share this type and " + fate + ". " +
                            "The parameter lives on the type; there is no way to write one instance without them. " +
                            "Duplicate the type first if that is not what you meant.";
                    return incomplete == null ? s : s + " " + incomplete;
                }

                // Collateral == 0 with an incomplete census is 'we found none', not
                // 'there are none'. Saying nothing here is what makes the two read alike.
                if (incomplete != null)
                    return "No unnamed element was FOUND sharing this type, but " + incomplete;
                return null;
            }
        }

        // ---- Target resolution. An id we cannot read is an error, never a skip. ----
        private static Element ResolveTarget(Document doc, JObject w, out string kind, out long? id, out string why)
        {
            kind = null; id = null; why = null;

            var explicitTarget = w.Value<string>("target");
            var idTok = w["target_id"];

            if (string.Equals(explicitTarget, "project_info", StringComparison.OrdinalIgnoreCase) || idTok == null)
            {
                var pi = doc.ProjectInformation;
                if (pi == null) { why = "This document has no Project Information element."; return null; }
                kind = "project_info";
                id = RevitCompat.GetIdOrNull(pi.Id);
                return pi;
            }

            if (idTok.Type != JTokenType.Integer)
            {
                why = "target_id '" + idTok.ToString() + "' is not an integer element id. Ignored ids are how a " +
                      "caller believes it wrote elements it never touched.";
                return null;
            }

            long raw = idTok.Value<long>();
            if (!RevitCompat.CanRepresentElementId(raw)) { why = RevitCompat.ElementIdRangeError(raw); return null; }

            var elem = doc.GetElement(RevitCompat.ToElementId(raw));
            if (elem == null)
            {
                why = "Element " + raw + " does not resolve in this document. It may have been deleted, or belong " +
                      "to another model.";
                return null;
            }
            id = raw;
            kind = (elem is ElementType) ? "type" : "instance";
            return elem;
        }

        /// <summary>
        /// BuiltInParameter name | shared-parameter GUID | parameter name. A name that
        /// matches more than one parameter is an error: LookupParameter would silently
        /// pick one, and the wrong one is indistinguishable from the right one in a report.
        /// </summary>
        private static Parameter ResolveParameter(Element e, string spec, out string matchedBy, out string why)
        {
            matchedBy = null; why = null;
            spec = spec.Trim();

            Guid guid;
            if (Guid.TryParse(spec, out guid))
            {
                matchedBy = "guid";
                Parameter p = null;
                try { p = e.get_Parameter(guid); } catch (Exception ex) { why = "Lookup by GUID failed: " + ex.Message; return null; }
                if (p == null)
                    why = "No shared parameter with GUID " + spec + " on this target. It exists in the SPF or not " +
                          "at all — either way it is not bound to this element's category, so nothing here can carry it.";
                return p;
            }

            // A BuiltInParameter lookup that THREW did not come back empty. Keep the reason
            // so the final error cannot claim we looked and found nothing.
            string bipError = null;
            if (spec.Length > 0 && char.IsLetter(spec[0]))
            {
                BuiltInParameter bip;
                if (Enum.TryParse(spec, false, out bip))
                {
                    Parameter p = null;
                    try { p = e.get_Parameter(bip); }
                    catch (Exception ex) { p = null; bipError = ex.Message; }
                    if (p != null) { matchedBy = "builtin"; return p; }
                    // Fall through: the name is also a legal UI name, so try that before failing.
                }
            }

            var hits = new List<Parameter>();
            // A parameter whose Name throws is one we cannot rule out as a second match for
            // spec. Skipping it in silence lets a genuine ambiguity resolve to hits.Count==1
            // and ship as matched_by:"name" — the guess-reported-as-fact this method's whole
            // contract is against.
            int unreadable = 0;
            string unreadableError = null;
            try
            {
                foreach (Parameter p in e.Parameters)
                {
                    string n;
                    try { n = p.Definition?.Name; }
                    catch (Exception ex)
                    {
                        unreadable++;
                        if (unreadableError == null) unreadableError = ex.Message;
                        continue;
                    }
                    if (string.Equals(n, spec, StringComparison.Ordinal)) hits.Add(p);
                }
            }
            catch (Exception ex) { why = "Could not enumerate this target's parameters: " + ex.Message; return null; }

            if (hits.Count > 1)
            {
                why = "'" + spec + "' matches " + hits.Count + " parameters on this target. Picking one would be a " +
                      "guess reported as a fact — name it by BuiltInParameter or by shared-parameter GUID instead.";
                return null;
            }

            string blind = unreadable == 0
                ? null
                : unreadable + " parameter(s) on this target could not be read by name" +
                  (unreadableError == null ? "" : " (first failure: " + unreadableError + ")") + ". ";

            if (hits.Count == 1)
            {
                // One visible match plus parameters we could not see is not a unique match;
                // it is a match we cannot prove. Refuse rather than resolve.
                if (unreadable > 0)
                {
                    why = "'" + spec + "' matched 1 parameter on this target, but " + blind +
                          "A unique match cannot be proven: one of them may carry the same name, and writing the " +
                          "wrong parameter is indistinguishable from writing the right one in this report. " +
                          "Name it by BuiltInParameter or by shared-parameter GUID instead.";
                    return null;
                }
                matchedBy = "name";
                return hits[0];
            }

            why = "No parameter named '" + spec + "' was found on this target (tried BuiltInParameter, GUID and UI " +
                  "name). " + blind +
                  (bipError == null
                      ? ""
                      : "The BuiltInParameter lookup did not come back empty — it FAILED: " + bipError + ". ") +
                  (e is ElementType
                      ? "This is a TYPE — instance-only parameters do not exist here."
                      : "If this parameter lives on the type, pass the type's id as target_id.");
            return null;
        }

        // ---- Coercion. A value that cannot be coerced names the storage type. ----
        //
        // `expected` is the load-bearing output. It is the caller's value in the shape
        // the model stores it, captured here — the only place that still knows what was
        // asked for. Verify against a second read of the parameter instead and the check
        // passes by construction: Revit coercing, truncating or ignoring the value moves
        // both reads together.
        //
        // `expectationIsParse` is how the caller tells the two apart. On the SetValueString
        // path `expected` is a READ, not the caller's value, so the verification degenerates
        // to the model agreeing with itself. That is still worth doing (it catches drift),
        // but it must never be counted as intent-verified, so the fact travels with the row.
        private static bool TryApply(Parameter p, JToken v, out bool accepted, out string how,
                                     out JObject expected, out bool expectationIsParse, out string why)
        {
            accepted = false; how = null; why = null; expected = null; expectationIsParse = false;
            var st = p.StorageType;
            bool isNull = v == null || v.Type == JTokenType.Null;

            switch (st)
            {
                case StorageType.String:
                    if (isNull) { why = "Parameter storage is String and value is null. Use \"\" to clear it — null and empty are not the same request."; return false; }
                    how = "Set(string)";
                    string sv = TokenText(v);
                    accepted = p.Set(sv);
                    expected = LiteralExpectation(p, sv);
                    return true;

                case StorageType.Integer:
                    if (isNull) { why = "Parameter storage is Integer; null is not a value it can hold."; return false; }
                    if (v.Type == JTokenType.Boolean)
                    {
                        how = "Set(int) [yes/no]";
                        int bv = v.Value<bool>() ? 1 : 0;
                        accepted = p.Set(bv);
                        expected = LiteralExpectation(p, bv);
                        return true;
                    }
                    if (v.Type == JTokenType.Integer)
                    {
                        long l = v.Value<long>();
                        if (l < int.MinValue || l > int.MaxValue) { why = "Value " + l + " does not fit an Integer parameter."; return false; }
                        how = "Set(int)";
                        accepted = p.Set((int)l);
                        expected = LiteralExpectation(p, (int)l);
                        return true;
                    }
                    if (v.Type == JTokenType.Float) { why = "Parameter storage is Integer but value " + v + " is fractional. Round it deliberately; silently truncating a number someone bills is not our call."; return false; }
                    if (v.Type == JTokenType.String)
                    {
                        how = "SetValueString(string) [unit-aware]";
                        string txt = TokenText(v);
                        accepted = p.SetValueString(txt);
                        expected = ParseExpectation(p, txt, accepted);
                        expectationIsParse = true;
                        return true;
                    }
                    why = "Parameter storage is Integer; cannot coerce a " + v.Type + ".";
                    return false;

                case StorageType.Double:
                    if (isNull) { why = "Parameter storage is Double; null is not a value it can hold."; return false; }
                    if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float)
                    {
                        how = "Set(double) [raw, Revit internal units]";
                        double dv = v.Value<double>();
                        accepted = p.Set(dv);
                        expected = LiteralExpectation(p, dv);
                        return true;
                    }
                    if (v.Type == JTokenType.String)
                    {
                        how = "SetValueString(string) [unit-aware]";
                        string txt = TokenText(v);
                        accepted = p.SetValueString(txt);
                        expected = ParseExpectation(p, txt, accepted);
                        expectationIsParse = true;
                        return true;
                    }
                    why = "Parameter storage is Double; cannot coerce a " + v.Type + ".";
                    return false;

                case StorageType.ElementId:
                    long idv;
                    if (isNull) idv = -1;
                    else if (v.Type == JTokenType.Integer) idv = v.Value<long>();
                    else if (v.Type == JTokenType.String && long.TryParse(TokenText(v), out idv)) { }
                    else { why = "Parameter storage is ElementId; it takes an element id (or null / -1 to clear), not a " + v.Type + "."; return false; }

                    if (!RevitCompat.CanRepresentElementId(idv)) { why = RevitCompat.ElementIdRangeError(idv); return false; }
                    how = "Set(ElementId)";
                    var eid = RevitCompat.ToElementId(idv);
                    accepted = p.Set(eid);
                    // ReadValue renders ElementId storage as ElementId.ToString(); expect
                    // the same rendering or every ElementId row would read as drift.
                    expected = LiteralExpectation(p, eid.ToString());
                    return true;

                default:
                    why = "Parameter storage type is " + st + "; there is nothing to write.";
                    return false;
            }
        }

        /// <summary>
        /// The exact value handed to Parameter.Set(), in ReadValue's shape so the two can
        /// be compared. This is intent, not a measurement — it is never presented as one.
        /// </summary>
        private static JObject LiteralExpectation(Parameter p, JToken value)
        {
            return new JObject
            {
                ["readable"] = true,
                ["storage"] = SafeStorage(p),
                ["value"] = value,
                ["expectation_source"] = "the exact value passed to Parameter.Set() — what the caller asked for."
            };
        }

        /// <summary>
        /// SetValueString hands Revit a string and lets it parse the project's units, so
        /// the caller's "3000 mm" is not a value we can compare against a Double read. The
        /// number Revit parsed is the only expectation it can be held to, and it is only
        /// canonical if taken at once — before Regenerate can move it. Said out loud on the
        /// row so a unit-rounded value reads as a parse, not as a mismatch.
        /// </summary>
        private static JObject ParseExpectation(Parameter p, string text, bool accepted)
        {
            if (!accepted)
                return new JObject
                {
                    ["readable"] = false,
                    ["error"] = "Revit refused SetValueString(\"" + text + "\"), so it never parsed a value; " +
                                "there is no expectation to hold this write to."
                };

            var e = ReadValue(p);
            if (Readable(e))
                e["expectation_source"] =
                    "re-read from the parameter immediately after SetValueString(\"" + text + "\"): Revit parsed the " +
                    "units and this is the number it stored. It is a parse of the request, NOT a value we computed — " +
                    "a unit-rounded result here is expected, not drift.";
            else
                e["expectation_source"] =
                    "SetValueString(\"" + text + "\") was accepted but the parsed value could not be re-read, so " +
                    "there is nothing to verify this write against. Unknown — not confirmed.";
            return e;
        }

        // ---- Reading. "I could not look" is a DISTINCT value from "it is empty". ----
        private static JObject ReadValue(Parameter p)
        {
            if (p == null)
                return new JObject { ["readable"] = false, ["error"] = "No parameter to read." };
            try
            {
                var o = new JObject { ["readable"] = true, ["storage"] = p.StorageType.ToString() };
                switch (p.StorageType)
                {
                    case StorageType.String: o["value"] = p.AsString(); break;
                    case StorageType.Integer: o["value"] = p.AsInteger(); break;
                    case StorageType.Double: o["value"] = p.AsDouble(); break;
                    case StorageType.ElementId: o["value"] = p.AsElementId()?.ToString(); break;
                    default: o["value"] = null; break;
                }
                o["has_value"] = p.HasValue;
                try { o["text"] = p.AsValueString(); } catch { o["text"] = null; }
                return o;
            }
            catch (Exception ex)
            {
                // Not "empty". Not "unchanged". Unknown — and it must read as unknown.
                return new JObject
                {
                    ["readable"] = false,
                    ["error"] = "Could not read this parameter: " + ex.Message +
                                ". This is NOT the same as the parameter being empty; its value is unknown."
                };
            }
        }

        private static bool Readable(JObject v)
        {
            return v != null && v["readable"] != null && v["readable"].Type == JTokenType.Boolean && v.Value<bool>("readable");
        }

        /// <summary>
        /// Compare two values. Unreadable never equals anything, including itself: an
        /// unknown that compares equal is how "I could not look" becomes "it matches".
        ///
        /// Double storage compares on a relative delta — the same shape as
        /// HorizunGuard.Reconcile, but far tighter, because these are not two independent
        /// measurements of a quantity: one of them is a value we just wrote. Bit-equality
        /// would report drift on the last ulp of a unit parse and send an honest write to
        /// an atomic rollback.
        /// </summary>
        private const double DoubleRelTolerance = 1e-9;

        private static bool SameValue(JObject a, JObject b)
        {
            if (!Readable(a) || !Readable(b)) return false;
            var av = a["value"];
            var bv = b["value"];

            if (IsDoubleStorage(a) && IsDoubleStorage(b) && IsNumber(av) && IsNumber(bv))
            {
                double x = av.Value<double>(), y = bv.Value<double>();
                double biggest = Math.Max(Math.Abs(x), Math.Abs(y));
                double delta = Math.Abs(x - y);
                return biggest > 1e-9 ? (delta / biggest) <= DoubleRelTolerance : delta <= 1e-9;
            }
            return JToken.DeepEquals(av, bv);
        }

        private static bool IsDoubleStorage(JObject v)
        {
            var s = v["storage"];
            return s != null && s.Type == JTokenType.String &&
                   string.Equals(v.Value<string>("storage"), "Double", StringComparison.Ordinal);
        }

        private static bool IsNumber(JToken t)
        {
            return t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer);
        }

        private static string SafeStorage(Parameter p) { try { return p.StorageType.ToString(); } catch { return null; } }

        /// <summary>
        /// Post-commit evidence: re-fetch the element from the document and re-resolve
        /// the parameter from scratch. Re-using the cached Parameter object would be
        /// reading our own handle, not the model.
        /// </summary>
        private static JObject ReReadFromModel(Document doc, Row r)
        {
            try
            {
                Element fresh;
                if (r.TargetKind == "project_info") fresh = doc.ProjectInformation;
                else if (r.TargetId.HasValue) fresh = doc.GetElement(RevitCompat.ToElementId(r.TargetId.Value));
                else fresh = null;

                if (fresh == null)
                    return new JObject { ["readable"] = false, ["error"] = "Target no longer resolves in the document after the commit." };

                var p = ResolveParameter(fresh, r.ParamSpec, out string _, out string why);
                if (p == null)
                    return new JObject { ["readable"] = false, ["error"] = "Parameter no longer resolves after the commit: " + why };

                return ReadValue(p);
            }
            catch (Exception ex)
            {
                return new JObject { ["readable"] = false, ["error"] = "Post-commit re-read failed: " + ex.Message };
            }
        }

        /// <summary>
        /// Suppresses Revit's modal dialogs during the batch. A modal here does not wait
        /// for a human — nobody is looking at Revit — it blocks the bridge until the
        /// caller times out and retries a batch that may already be half applied.
        /// Errors are still resolved/rolled back by Revit; we only refuse to be asked.
        /// </summary>
        private class SilenceModals : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (var f in a.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                }
                return FailureProcessingResult.Continue;
            }
        }

        /// <summary>
        /// Every instance of a type — i.e. everyone a type-level write reaches.
        ///
        /// Deliberately NOT FamilyInstanceFilter: that only matches FamilyInstances, so
        /// for a wall/floor/roof type it returns EMPTY without throwing, and we would
        /// report a blast radius of zero for exactly the categories where a type-wide
        /// write does the most damage. One scan, cached per request.
        /// </summary>
        // Per-request only. The dispatcher keeps one handler instance alive for the whole
        // session, so a cache that outlived a request would answer the next one from a
        // model that has since changed. Cleared on entry to Execute.
        private Dictionary<ElementId, List<ElementId>> _census;

        // An element whose GetTypeId() throws used to be dropped here in silence, and this
        // census is the ONLY source of the blast radius — so "I could not read this
        // element's type" became "this element is not an instance of your type", and the
        // radius came back understated with nothing to say so. Counted, and carried out to
        // the caller as a lower-bound flag.
        private int _censusUnreadable;
        private string _censusUnreadableError;

        private List<ElementId> InstancesOfType(Document doc, ElementId typeId)
        {
            if (_census == null)
            {
                _census = new Dictionary<ElementId, List<ElementId>>();
                foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    ElementId tid;
                    try { tid = e.GetTypeId(); }
                    catch (Exception ex)
                    {
                        _censusUnreadable++;
                        if (_censusUnreadableError == null) _censusUnreadableError = ex.Message;
                        continue;
                    }
                    if (tid == null || tid == ElementId.InvalidElementId) continue;
                    List<ElementId> list;
                    if (!_census.TryGetValue(tid, out list))
                        _census[tid] = list = new List<ElementId>();
                    list.Add(e.Id);
                }
            }
            List<ElementId> hits;
            return _census.TryGetValue(typeId, out hits) ? hits : new List<ElementId>();
        }

        /// <summary>
        /// The set of elements a batch of confirmed rows actually reaches. Summing a
        /// per-row Affected across rows counts the same element once per row — two writes
        /// to one type with 50 instances reported 100 — so the total is taken over the
        /// distinct set the model says was touched, never over the input list.
        /// </summary>
        private HashSet<ElementId> ReachedBy(Document doc, IEnumerable<Row> rows)
        {
            var set = new HashSet<ElementId>();
            foreach (var r in rows)
            {
                if (r.Target == null) continue;
                set.Add(r.Target.Id);
                if (r.TargetKind == "type")
                    foreach (var id in InstancesOfType(doc, r.Target.Id)) set.Add(id);
            }
            return set;
        }

        // ---- Small, boring, and each one honest about failing. ----
        private static string TokenText(JToken v)
        {
            return v.Type == JTokenType.String ? v.Value<string>() : v.ToString();
        }

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafeName(Element e) { try { return e?.Name; } catch { return null; } }
        private static string SafeDefName(Parameter p) { try { return p.Definition?.Name; } catch { return "(name unreadable)"; } }
        private static bool? SafeReadOnly(Parameter p) { try { return p.IsReadOnly; } catch { return null; } }

        private static bool IsFormulaHint(Parameter p)
        {
            // Revit does not expose "is driven by a formula" on a project Parameter, so
            // this is a HINT in an error string only — never a field the caller could
            // mistake for a measurement.
            try { return p.IsReadOnly && p.Definition != null && !(p.Definition is InternalDefinition); }
            catch { return false; }
        }

        private static InternalDefinition SafeInternalDef(Parameter p)
        {
            try { return p.Definition as InternalDefinition; } catch { return null; }
        }

        private static bool? ReadVaries(Parameter p)
        {
            try
            {
                var def = p.Definition as InternalDefinition;
                if (def == null) return null;   // not a bound project/shared param: the question does not apply
                return def.VariesAcrossGroups;
            }
            catch { return null; }
        }
    }
}
