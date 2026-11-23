// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The result of a command. Two shapes only: success carrying a data payload, or
// failure carrying a message. The transport serializes this to
// { "id", "success", "data", "error" } — one envelope, no ambiguity about
// whether a call worked.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    public sealed class CommandResult
    {
        public bool Success { get; private set; }

        /// <summary>Payload on success (any JSON-serializable object). Null on failure.</summary>
        public object Data { get; private set; }

        /// <summary>Message on failure. Null on success.</summary>
        public string Error { get; private set; }

        /// <summary>
        /// MACHINE-READABLE VERDICT ON FALLING BACK TO PYTHON. Null when the command
        /// did not set a verdict. The MCP server explicitly denies unclassified refusals.
        ///
        /// The fallback policy used to live only in the server's instructions - prose
        /// that helps an obedient model and gives a client nothing to branch on. A
        /// client had to tell four different failures apart by reading English: a
        /// capability this bridge does not have, a mistake in the arguments, a Revit
        /// error, and a write that failed halfway. The first is the only one where
        /// generating Python is right, and the last is the one where it is dangerous.
        ///
        /// So the distinction is carried as data. See FallbackSignal for the rules;
        /// the one that matters is that write_started=true can never accompany
        /// allowed=true.
        /// </summary>
        public FallbackSignal Fallback { get; private set; }

        /// <summary>
        /// WHICH actions in a batch were capability gaps, by index. Present whenever any
        /// were, INCLUDING when the request-level grant was refused because other actions
        /// failed for fixable reasons - that is the case where a caller most needs to know
        /// the difference, and the old code told them nothing but "allowed".
        /// </summary>
        public Newtonsoft.Json.Linq.JArray CapabilityGaps { get; private set; }

        /// <summary>
        /// STRUCTURED DIAGNOSTIC that rides beside a failure's message. Null on almost every
        /// result. It exists because some failures - a failed atomic plan above all - carry
        /// facts a caller must branch on (did the TransactionGroup start? did the rollback
        /// actually land, or is the model's state uncertain?) that a prose sentence cannot be
        /// trusted to convey. Serialized into structuredContent on the error path, the same
        /// way the fallback signal is, so a client reads data instead of parsing English.
        /// </summary>
        public Newtonsoft.Json.Linq.JObject Detail { get; private set; }

        /// <summary>
        /// What Revit raised while this ran — warnings, errors, modal dialogs — filled in
        /// by the dispatcher, never by the command. It rides beside the result rather than
        /// inside it so no command has to remember to report it, and so a command that
        /// FAILED still carries what Revit objected to, which is usually the reason.
        /// </summary>
        public object RevitSaid { get; set; }

        private CommandResult() { }

        public static CommandResult Ok(object data)
        {
            Newtonsoft.Json.Linq.JObject payload = data as Newtonsoft.Json.Linq.JObject;
            if (payload == null && data != null)
            {
                try { payload = Newtonsoft.Json.Linq.JObject.FromObject(data); }
                catch { /* Non-object read results have no write declaration. */ }
            }
            if (ReadResultCoverage.Stamp(payload)) data = payload;
            string failure = WriteResultFailure(payload, out string code);
            return failure == null ? new CommandResult { Success = true, Data = data }
                : FailCoded(code, failure, extra: payload);
        }

        /// <summary>
        /// Last common boundary for every typed command, including child calls and jobs.
        /// Never turn a declared rollback or zero-of-N application into transport success.
        /// Only explicit rehearsals are exempt; nested historical evidence is not inspected.
        /// Also used by the server for replies from older add-ins.
        /// </summary>
        public static string WriteResultFailure(Newtonsoft.Json.Linq.JObject payload)
            => WriteResultFailure(payload, out _);

        public static string WriteResultFailure(Newtonsoft.Json.Linq.JObject payload, out string code)
        {
            code = ErrorCodes.ApiRevitExcepcion;
            if (payload == null) return null;
            var application = payload["application"] as Newtonsoft.Json.Linq.JObject;
            if (IsTrue(payload["dry_run"]))
            {
                string declaredCode = payload["error_code"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)payload["error_code"] : null;
                if (Count(payload["invalid"]) > 0 || Count(payload["invalid_operations"]) > 0)
                {
                    // 5.0.4: the arguments matched the schema; what failed is the CONTENT of some rows
                    // (a missing level, a type that does not exist). Field 2026-11: ~45 of these were
                    // reported as esquema_invalido with no row named, and agents re-read the schema
                    // instead of fixing the row.
                    code = ErrorCodes.IsStable(declaredCode) ? declaredCode : ErrorCodes.EntradaInvalida;
                    return "Rehearsal refused " + InvalidRowsSummary(payload) + ". Nothing was applied; correct those rows (or drop them) and send the rehearsal again." +
                           InvalidRowsCause(payload);
                }
                string state = application?["state"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)application["state"] : null;
                if (state == "partial" || state == "rejected" || state == "failed" || state == "uncertain" || state == "rolled_back")
                {
                    code = ErrorCodes.IsStable(declaredCode) ? declaredCode : ErrorCodes.PrecondicionNoCumplida;
                    return "Rehearsal failed: the application declaration is " + state + "." + RehearsalCause(payload) +
                           " No executable confirmation is established; inspect the preserved diagnostic.";
                }
                return null;
            }
            bool declaredRehearsal = DeclaredRehearsal(payload, application);
            foreach (var block in new[] { payload, application })
            {
                if (block == null) continue;
                string status = block["transaction_status"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? (string)block["transaction_status"] : null;
                if (string.Equals(status, "RolledBack", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "rolled_back", System.StringComparison.OrdinalIgnoreCase))
                {
                    code = ErrorCodes.TransaccionRevertida;
                    return "Write failed: Revit returned " + status + ". Nothing from that transaction was applied. See the preserved write diagnostic.";
                }
                if (declaredRehearsal) continue;
                string state = block["state"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? (string)block["state"] : null;
                if (state == "failed" || state == "rolled_back")
                {
                    if (state == "rolled_back") code = ErrorCodes.TransaccionRevertida;
                    else if (CommittedWithFailedPostconditions(payload, application)) code = ErrorCodes.VerificacionFallida;
                    // A junction composite (cad_connect) records its causes per junction, which DeclaredCause does not read.
                    return "Write failed: the application declaration is " + state + "." +
                           (payload["junctions"] is Newtonsoft.Json.Linq.JArray ? RehearsalCause(payload) : DeclaredCause(payload)) +
                           " Inspect the preserved diagnostic; changes may have been attempted but are not verified.";
                }
                long? requested = Count(block["writes_requested"]) ?? Count(block["requested"]);
                if (requested.GetValueOrDefault() <= 0 && state != "partial") continue;
                foreach (string name in WriteConfirmationCounters)
                    if (Count(block[name]) == 0)
                    {
                        // COMMITTED, THEN A NAMED POSTCONDITION FAILED. Field 2026-11-18: a CAD link was
                        // created and only its unit could not be confirmed (failed_postconditions
                        // ["units_unconfirmable"]), and the reply carried api_revit_excepcion although
                        // Revit raised nothing. The write reached the model; what failed is its check.
                        if (CommittedWithFailedPostconditions(payload, application))
                            code = ErrorCodes.VerificacionFallida;
                        return "Write failed: 0 of " + (requested?.ToString() ?? "the") + " requested changes were confirmed (" + name + ")." +
                               DeclaredCause(payload) + " See the preserved write diagnostic.";
                    }
            }
            return null;
        }

        /// <summary>
        /// The transaction committed (said by the payload or its application block) and the payload
        /// names at least one failed postcondition: the write is in the model and its verification
        /// failed, which is verificacion_fallida, not an exception of the Revit API.
        /// </summary>
        internal static bool CommittedWithFailedPostconditions(Newtonsoft.Json.Linq.JObject payload,
                                                                Newtonsoft.Json.Linq.JObject application)
        {
            if (!(payload?["failed_postconditions"] is Newtonsoft.Json.Linq.JArray failed) || failed.Count == 0) return false;
            foreach (var block in new[] { payload, application })
            {
                string status = block?["transaction_status"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? (string)block["transaction_status"] : null;
                if (string.Equals(status, "Committed", System.StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Shared by the add-in and Server (which links this source). Source-contract
        // tests inventory the actual producers against this list, including legacy
        // counters; a new spelling must be deliberately supported here.
        public static readonly string[] WriteConfirmationCounters = {
            "verified", "writes_confirmed", "writes_verified", "applied", "created_verified",
            "written", "deleted", "transformed", "writes_verified_after_commit", "renamed_verified"
            , "annotations_verified", "copies_geometry_verified", "actions_verified", "overrides_verified", "operations_verified", "params_set_confirmed",
            "paths_verified", "files_verified", "rows_verified"
        };

        // Names under which a command records WHY an item was refused or failed. The reply's
        // message used to say only "the application declaration is failed", while the reason
        // (a room's rejection, Revit's own words) sat in a nested block the caller never saw.
        private static readonly string[] CauseArrays = { "rejected_rooms", "rejected", "failed_items", "errors", "failures" };
        private static readonly string[] CauseTexts = { "reason", "error", "message", "why" };
        private const int CauseLimit = 3, CauseMaxChars = 400;

        /// <summary>
        /// " Cause: ..." built from the first reasons a write recorded (searched two levels
        /// deep, at most three, each id-prefixed), or "" when the payload names none.
        /// </summary>
        /// <summary>"2 of 5 rows" / "1 operation" - how many entries the rehearsal refused, out of how many.</summary>
        internal static string InvalidRowsSummary(Newtonsoft.Json.Linq.JObject payload)
        {
            bool operations = Count(payload["invalid"]) == null || Count(payload["invalid"]) == 0;
            long invalid = (operations ? Count(payload["invalid_operations"]) : Count(payload["invalid"])) ?? 0;
            long? valid = operations ? Count(payload["valid_operations"]) : Count(payload["valid"]);
            string noun = operations ? "operation" : "row";
            string of = valid.HasValue ? " of " + (valid.Value + invalid) : "";
            long total = valid.HasValue ? valid.Value + invalid : invalid;
            return invalid + of + " " + noun + (total == 1 ? "" : "s");
        }

        /// <summary>
        /// " Row 0: error; row 3 (path): error." from the rehearsal's errors array, at most three,
        /// or "" when it names none. The row index is the caller's own position in its list.
        /// </summary>
        internal static string InvalidRowsCause(Newtonsoft.Json.Linq.JObject payload)
        {
            if (!(payload?["errors"] is Newtonsoft.Json.Linq.JArray errors) || errors.Count == 0) return "";
            var found = new System.Collections.Generic.List<string>();
            foreach (var e in errors)
            {
                if (found.Count >= CauseLimit) break;
                string reason = null, where = null;
                if (e.Type == Newtonsoft.Json.Linq.JTokenType.String) reason = (string)e;
                else if (e is Newtonsoft.Json.Linq.JObject o)
                {
                    foreach (string t in CauseTexts)
                        if (o[t]?.Type == Newtonsoft.Json.Linq.JTokenType.String) { reason = (string)o[t]; break; }
                    var index = o["elements_index"] ?? o["index"];
                    if (index != null && index.Type == Newtonsoft.Json.Linq.JTokenType.Integer) where = "row " + index;
                    if (o["path"]?.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        where = (where == null ? "" : where + " ") + "(" + (string)o["path"] + ")";
                }
                if (string.IsNullOrWhiteSpace(reason)) continue;
                found.Add((where == null ? "" : where + ": ") + reason.Trim());
            }
            if (found.Count == 0) return "";
            string text = string.Join("; ", found);
            if (text.Length > CauseMaxChars) text = text.Substring(0, CauseMaxChars) + "...";
            int more = errors.Count - found.Count;
            text = char.ToUpperInvariant(text[0]) + text.Substring(1) + (more > 0 ? "; and " + more + " more in errors" : "");
            // A cause that already ends its sentence is not given a second full stop ("written..").
            return " " + text + (text.EndsWith(".", System.StringComparison.Ordinal) ? "" : ".");
        }

        internal static string DeclaredCause(Newtonsoft.Json.Linq.JObject payload)
        {
            var found = new System.Collections.Generic.List<string>();
            CollectCauses(payload, 0, found);
            if (found.Count == 0) return "";
            string text = string.Join("; ", found);
            if (text.Length > CauseMaxChars) text = text.Substring(0, CauseMaxChars) + "...";
            return " Cause: " + text + ".";
        }

        private static readonly string[] RehearsalRowArrays = { "actions", "rows" };

        /// <summary>
        /// " N row(s) could not be constructed. Cause: action i (op): reason; ..." from the
        /// rehearsal block, falling back to <see cref="DeclaredCause"/>. A failed rehearsal
        /// used to say only "partial" while the per-row reason sat in rehearsal.actions[].
        /// </summary>
        internal static string RehearsalCause(Newtonsoft.Json.Linq.JObject payload)
        {
            var found = new System.Collections.Generic.List<string>();
            var rehearsal = payload?["rehearsal"] as Newtonsoft.Json.Linq.JObject;
            long? notConstructible = rehearsal == null ? null : Count(rehearsal["not_constructible"]);
            foreach (var block in new[] { rehearsal, payload })
            {
                if (block == null) continue;
                foreach (string name in RehearsalRowArrays)
                {
                    if (!(block[name] is Newtonsoft.Json.Linq.JArray rows)) continue;
                    foreach (var row in rows)
                    {
                        if (found.Count >= CauseLimit || !(row is Newtonsoft.Json.Linq.JObject o)) continue;
                        bool failed = o["constructible"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && !(bool)o["constructible"];
                        string reason = o["reason"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)o["reason"] : null;
                        if (!failed || string.IsNullOrWhiteSpace(reason)) continue;
                        string label = "action " + (o["index"]?.Type == Newtonsoft.Json.Linq.JTokenType.Integer ? o["index"].ToString() : "?") +
                                       (o["operation"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? " (" + (string)o["operation"] + ")" : "");
                        string item = label + ": " + reason.Trim();
                        if (!found.Contains(item)) found.Add(item);
                    }
                }
            }
            // A planner that refuses REQUIRED rows (apply_reinforcement) lists them in refused[] as
            // {index, kind, rule_id, code, why}. Field 2026-11-19: a beam rule sent to a column answered
            // only "the application declaration is rejected"; the code and the reason sat in that array.
            int refusedTotal = 0, refusedShown = 0;
            if (payload?["refused"] is Newtonsoft.Json.Linq.JArray refusedRows)
                for (int position = 0; position < refusedRows.Count; position++)
                {
                    var row = refusedRows[position];
                    if (row is Newtonsoft.Json.Linq.JObject) refusedTotal++;
                    if (found.Count >= CauseLimit || !(row is Newtonsoft.Json.Linq.JObject o)) continue;
                    string code = o["code"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)o["code"] : null;
                    string why = o["why"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)o["why"] : null;
                    if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(why)) continue;
                    string label = "refused " + (o["kind"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)o["kind"] : "row") +
                                   (o["rule_id"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? " rule " + (string)o["rule_id"] : "") +
                                   // Its position in refused[], where the row can be read whole: the planner's own index runs across
                                   // covers, bars and hooks, and is not the per-kind numbering of will_not_build.
                                   " (refused[" + position + "])";
                    string item = label + ": " + (string.IsNullOrWhiteSpace(code) ? "" : code + (string.IsNullOrWhiteSpace(why) ? "" : " - ")) +
                                  (why?.Trim().TrimEnd('.') ?? "");
                    if (!found.Contains(item)) { found.Add(item); refusedShown++; }
                }
            // A composite (cad_connect) records each junction as {id, state, refused, says} in
            // junctions[]. Field 2026-11: a sequential rehearsal of 22 junctions answered only
            // "the application declaration is rejected" five times; the refusals (connectors
            // 12.7 mm apart, a member 40 mm too short) sat in that array.
            int refusedJunctions = 0, rehearsedJunctions = 0, madeJunctions = 0, unresolvedJunctions = 0, alreadyJunctions = 0;
            bool isApply = payload?["dry_run"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && !(bool)payload["dry_run"];
            string firstUnresolved = null;
            // SKIPPED, NOT REFUSED, AND STILL NOT MADE. Field 2026-11-17: 12 hand-written direct junctions
            // with no `automatic` were skipped for review, the rehearsal was rejected, and the error named
            // no cause at all - the reason sat in each row. They are named here by group.
            var skippedGroups = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Newtonsoft.Json.Linq.JObject>>();
            if (payload?["junctions"] is Newtonsoft.Json.Linq.JArray junctions)
            {
                foreach (var row in junctions)
                {
                    if (!(row is Newtonsoft.Json.Linq.JObject o)) continue;
                    string state = o["state"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)o["state"] : null;
                    if (state == "would_create" || state == "would_join") { rehearsedJunctions++; continue; }
                    if (state == "already_connected") { alreadyJunctions++; continue; }
                    if (state == "created" || state == "joined") { madeJunctions++; continue; }
                    if (state == "skipped_unresolved")
                    {
                        unresolvedJunctions++;
                        if (firstUnresolved == null && o["says"]?.Type == Newtonsoft.Json.Linq.JTokenType.String) firstUnresolved = ((string)o["says"]).Trim();
                        continue;
                    }
                    if (state == "skipped_needs_review" || state == "skipped_no_fitting")
                    {
                        if (!skippedGroups.TryGetValue(state, out var group)) skippedGroups[state] = group = new System.Collections.Generic.List<Newtonsoft.Json.Linq.JObject>();
                        group.Add(o);
                        continue;
                    }
                    if (state != "refused" && state != "uncertain" && state != "failed") continue;
                    refusedJunctions++;
                    if (found.Count >= CauseLimit) continue;
                    string code = o["refused"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)o["refused"] : state;
                    string says = o["says"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)o["says"] : null;
                    var idToken = o["id"];
                    string id = idToken != null && (idToken.Type == Newtonsoft.Json.Linq.JTokenType.String || idToken.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                        ? idToken.ToString() : "?";
                    string item = "junction " + id + ": " + code + (string.IsNullOrWhiteSpace(says) ? "" : " - " + says.Trim());
                    if (!found.Contains(item)) found.Add(item);
                }
            }
            int skippedJunctions = 0;
            foreach (var g in skippedGroups.Values) skippedJunctions += g.Count;
            string count;
            if (notConstructible.GetValueOrDefault() > 0)
                count = " " + notConstructible.Value + " row(s) could not be constructed.";
            else if (refusedJunctions > 0 || skippedJunctions > 0)
            {
                var parts = new System.Collections.Generic.List<string>();
                if (refusedJunctions > 0) parts.Add(refusedJunctions + " junction(s) were refused");
                if (skippedJunctions > 0) parts.Add(skippedJunctions + (isApply ? " junction(s) were skipped and not made" : " junction(s) were skipped and not rehearsed"));
                count = " " + string.Join(" and ", parts) + "; " +
                        (rehearsedJunctions > 0
                            ? rehearsedJunctions + " rehearsed and can be sent again without them"
                            : madeJunctions > 0
                                ? madeJunctions + " were made and verified"
                                : isApply ? "none of the others was made" : "none of the others rehearsed") +
                        (alreadyJunctions > 0 ? " (" + alreadyJunctions + " already connected)" : "") +
                        (unresolvedJunctions > 0 ? " (" + unresolvedJunctions + " more skipped as unresolved, which does not refuse the call)" : "") +
                        ".";
            }
            else if (unresolvedJunctions > 0 && rehearsedJunctions == 0 && madeJunctions == 0 && alreadyJunctions == 0)
            {
                // NOTHING RESOLVED (CadConnectVerdict.Composite): not a no-op, a call that found none of its runs here.
                string first = firstUnresolved == null ? "" : firstUnresolved.Length > 200 ? firstUnresolved.Substring(0, 200) + "..." : firstUnresolved;
                count = " None of the " + unresolvedJunctions + " junction(s) resolved to elements of this document " +
                        "(skipped_unresolved), so nothing " + (isApply ? "was made" : "could be rehearsed") +
                        ". Check target_document and that the conversion was applied here." +
                        (first.Length > 0 ? " First: " + first : "");
            }
            else count = "";
            string skipped = SkippedJunctionsCause(skippedGroups);
            if (found.Count == 0) return count + (skipped.Length > 0 ? skipped : DeclaredCause(payload));
            string text = string.Join("; ", found);
            if (text.Length > CauseMaxChars) text = text.Substring(0, CauseMaxChars) + "...";
            if (refusedTotal > refusedShown) text += "; and " + (refusedTotal - refusedShown) + " more in refused";
            string refusedCount = refusedShown > 0 ? " " + refusedTotal + " required row(s) refused." : "";
            // A cause that already ends its sentence is not given a second full stop ("cover..").
            return count + refusedCount + " Cause: " + text + (text.EndsWith(".", System.StringComparison.Ordinal) ? "" : ".") + skipped;
        }

        private const int SkippedIdsShown = 3;

        /// <summary>
        /// " Skipped for review (12: a, b, c and 9 more): why. To carry them out: remedy." per group of
        /// skipped junctions, or "" when there are none. The row's own sentence is long and repeats the
        /// remedy per row, so the reason is said once per group, from the row's review_reason field.
        /// </summary>
        private static string SkippedJunctionsCause(
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Newtonsoft.Json.Linq.JObject>> groups)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string state in new[] { "skipped_needs_review", "skipped_no_fitting" })
            {
                if (!groups.TryGetValue(state, out var rows) || rows.Count == 0) continue;
                var ids = new System.Collections.Generic.List<string>();
                foreach (var o in rows)
                {
                    if (ids.Count >= SkippedIdsShown) break;
                    var idToken = o["id"];
                    ids.Add(idToken != null && (idToken.Type == Newtonsoft.Json.Linq.JTokenType.String || idToken.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                        ? idToken.ToString() : "?");
                }
                string list = rows.Count + ": " + string.Join(", ", ids) + (rows.Count > ids.Count ? " and " + (rows.Count - ids.Count) + " more" : "");
                if (state == "skipped_needs_review")
                {
                    int notMarked = 0, byReading = 0;
                    string readingSays = null;
                    foreach (var o in rows)
                    {
                        string why = o["review_reason"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)o["review_reason"] : null;
                        if (why == "marked_by_the_reading")
                        {
                            byReading++;
                            if (readingSays == null && o["reading_says"]?.Type == Newtonsoft.Json.Linq.JTokenType.String) readingSays = ((string)o["reading_says"]).Trim();
                        }
                        else notMarked++;
                    }
                    if (readingSays != null && readingSays.Length > 160) readingSays = readingSays.Substring(0, 160) + "...";
                    var why2 = new System.Collections.Generic.List<string>();
                    if (notMarked > 0) why2.Add((notMarked == rows.Count ? "they are" : notMarked + " are") + " not marked automatic and carry no reason, so nothing vouched for them");
                    if (byReading > 0) why2.Add((byReading == rows.Count ? "the network reading marked them" : byReading + " were marked by the network reading") +
                                                " for a person" + (readingSays == null ? "" : " (first: " + readingSays + ")"));
                    sb.Append(" Skipped for review (skipped_needs_review, ").Append(list).Append("): ").Append(string.Join("; ", why2))
                      .Append(". To carry them out, send automatic=true on each junction you vouch for, or include_needs_review=true once somebody has looked; or send the call again without them.");
                }
                else
                {
                    sb.Append(" Skipped with no fitting (skipped_no_fitting, ").Append(list)
                      .Append("): the junction proposes fitting \"none\". Give it the fitting it needs (direct, elbow, tee, cross or transition), or send the call again without it.");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// The reason a rehearsal row gives when its checks did not match: the names of the
        /// checks that failed, so the caller sees WHICH one without opening the payload.
        /// </summary>
        public static string MismatchReason(Newtonsoft.Json.Linq.JObject verification)
        {
            var fields = new System.Collections.Generic.List<string>();
            if (verification?["checks"] is Newtonsoft.Json.Linq.JArray checks)
                foreach (var c in checks)
                    if (c is Newtonsoft.Json.Linq.JObject o && o["match"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean &&
                        !(bool)o["match"] && o["field"]?.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        fields.Add((string)o["field"]);
            return fields.Count == 0
                ? "one or more rehearsal checks did not match - see verification"
                : "rehearsal checks did not match: " + string.Join(", ", fields) + " - see verification";
        }

        private static void CollectCauses(Newtonsoft.Json.Linq.JObject block, int depth, System.Collections.Generic.List<string> found)
        {
            if (block == null || depth > 2 || found.Count >= CauseLimit) return;
            foreach (string name in CauseArrays)
            {
                if (!(block[name] is Newtonsoft.Json.Linq.JArray rows)) continue;
                foreach (var row in rows)
                {
                    if (found.Count >= CauseLimit) return;
                    string reason = null, id = null;
                    if (row.Type == Newtonsoft.Json.Linq.JTokenType.String) reason = (string)row;
                    else if (row is Newtonsoft.Json.Linq.JObject o)
                    {
                        foreach (string t in CauseTexts)
                            if (o[t]?.Type == Newtonsoft.Json.Linq.JTokenType.String) { reason = (string)o[t]; break; }
                        id = RowId(o);
                    }
                    if (string.IsNullOrWhiteSpace(reason)) continue;
                    string item = (id == null ? "" : id + ": ") + reason.Trim();
                    if (!found.Contains(item)) found.Add(item);
                }
            }
            // Per-row results (write_params_verified and the like): a row's own "error" is the
            // cause. Only "error", never a row's other prose, so a successful row adds nothing.
            if (block["rows"] is Newtonsoft.Json.Linq.JArray resultRows)
                foreach (var row in resultRows)
                {
                    if (found.Count >= CauseLimit) return;
                    if (!(row is Newtonsoft.Json.Linq.JObject o) || o["error"]?.Type != Newtonsoft.Json.Linq.JTokenType.String) continue;
                    string reason = (string)o["error"];
                    if (string.IsNullOrWhiteSpace(reason)) continue;
                    string id = RowId(o);
                    string param = o["parameter"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)o["parameter"] : null;
                    string item = (id == null ? "" : id + (param == null ? "" : " (" + param + ")") + ": ") + reason.Trim();
                    if (!found.Contains(item)) found.Add(item);
                }
            foreach (var p in block.Properties())
                if (p.Value is Newtonsoft.Json.Linq.JObject child) CollectCauses(child, depth + 1, found);
        }

        /// <summary>"name value" of the row's first id field: an integer, or a string of digits
        /// (write_params writes target_id as a string).</summary>
        private static string RowId(Newtonsoft.Json.Linq.JObject o)
        {
            foreach (var p in o.Properties())
            {
                if (p.Name != "id" && !p.Name.EndsWith("_id")) continue;
                if (p.Value.Type == Newtonsoft.Json.Linq.JTokenType.Integer) return p.Name + " " + p.Value;
                if (p.Value.Type == Newtonsoft.Json.Linq.JTokenType.String && long.TryParse((string)p.Value,
                        System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long n))
                    return p.Name + " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return null;
        }

        private static long? Count(Newtonsoft.Json.Linq.JToken token)
        {
            if (token?.Type != Newtonsoft.Json.Linq.JTokenType.Integer) return null;
            try { return (long)token; } catch { return null; }
        }

        private static bool IsTrue(Newtonsoft.Json.Linq.JToken token)
            => token?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && (bool)token;

        private static bool DeclaredRehearsal(Newtonsoft.Json.Linq.JObject payload, Newtonsoft.Json.Linq.JObject application)
        {
            // Some composing commands expose only the canonical application declaration.
            // Corroborate the rehearsal arithmetic instead of requiring a redundant dry_run.
            if (application == null || (string)application["state"] != "rehearsed" ||
                (string)application["transaction_status"] != "not_started" ||
                application["fully_applied"]?.Type != Newtonsoft.Json.Linq.JTokenType.Boolean ||
                (bool)application["fully_applied"] || !(Count(application["requested"]) >= 0)) return false;
            var topStatus = payload["transaction_status"];
            if (topStatus != null && (topStatus.Type != Newtonsoft.Json.Linq.JTokenType.String || (string)topStatus != "not_started")) return false;
            foreach (string name in new[] { "applied", "verified", "unresolved", "failed", "unknown" })
                if (Count(application[name]) != 0) return false;
            return true;
        }

        /// <summary>
        /// Lets the dispatcher normalize an anonymous JSON-serializable payload to a
        /// JObject before attaching transport metadata. Commands still create results
        /// through Ok/Fail and cannot change success into failure after the fact.
        /// </summary>
        internal void ReplaceData(object data) => Data = data;

        /// <summary>Same for a failure's structured detail (ownership_stamp rides there).</summary>
        internal void ReplaceDetail(Newtonsoft.Json.Linq.JObject detail) => Detail = detail;
        internal void ReplaceError(string error) { if (!Success) Error = error; }

        /// <summary>
        /// TRUE when the command refused the call BEFORE anything could have run: validation,
        /// permission, size, compatibility, syntax. Set by the command, never inferred from
        /// the wording of an error. The dispatcher then releases the idempotency key, so the
        /// same key may be reused once the caller fixes the call (4.2-N5, CT-P1-5b). A failure
        /// after the run began is never marked: its key stays consumed.
        /// </summary>
        public bool RejectedBeforeRun { get; private set; }

        /// <summary>Marks a refusal that happened before anything ran. Returns this result.</summary>
        public CommandResult AsRejectedBeforeRun()
        {
            if (!Success) RejectedBeforeRun = true;
            return this;
        }


        /// <summary>Adds one named block beside a failure's diagnostic. No effect on a success.</summary>
        internal void AttachDetail(string name, Newtonsoft.Json.Linq.JToken block)
        {
            if (Success || block == null) return;
            if (Detail == null) Detail = new Newtonsoft.Json.Linq.JObject();
            Detail[name] = block;
        }
        /// <summary>Tells the caller, in the failure's own detail, that its key was NOT consumed.</summary>
        internal void NoteKeyReleased(string key)
        {
            if (Success) return;
            if (Detail == null) Detail = new Newtonsoft.Json.Linq.JObject();
            Detail["idempotency"] = new Newtonsoft.Json.Linq.JObject
            {
                ["key"] = key,
                ["status"] = "released_not_consumed",
                ["command_executed_in_this_call"] = false,
                ["note"] = "The call was refused before anything ran, so this idempotency_key was NOT consumed: " +
                           "fix the call and resend it with the same key."
            };
        }

        /// <summary>
        /// Attach an already-decided verdict to a result that is otherwise unchanged.
        /// Internal, and reachable only through FallbackDecision.Attach, because the
        /// rule about WHEN a verdict may be granted lives there and nowhere else.
        /// </summary>
        internal void CarryFallback(FallbackSignal signal, Newtonsoft.Json.Linq.JArray capabilityGaps)
        {
            Fallback = signal;
            CapabilityGaps = capabilityGaps;
        }

        public static CommandResult Fail(string error)
            => new CommandResult { Success = false, Error = error };

        public CommandResult CarryDetails(Newtonsoft.Json.Linq.JObject fields)
        {
            if (fields == null || !fields.HasValues) return this;
            var target = Success ? Data as Newtonsoft.Json.Linq.JObject : Detail;
            if (target == null) target = new Newtonsoft.Json.Linq.JObject();
            foreach (var property in fields.Properties()) target[property.Name] = property.Value.DeepClone();
            if (Success) Data = target; else Detail = target;
            return this;
        }

        /// <summary>
        /// A failure that NAMES its cause with a stable error_code (see ErrorCodes) where the
        /// command knows better than the text does. Everything else is classified centrally
        /// when the reply is assembled, so most commands never call this.
        /// </summary>
        public static CommandResult FailCoded(string errorCode, string error, string exceptionType = null,
                                              long? limiteMs = null, Newtonsoft.Json.Linq.JObject extra = null)
            => new CommandResult
            {
                Success = false,
                Error = error,
                Detail = ErrorCodes.Detail(errorCode, exceptionType, limiteMs, extra)
            };

        /// <summary>A rejection known to occur before any transaction or write.</summary>
        public static CommandResult RefuseBeforeWrite(string error, string errorCode = null,
                                                       FallbackSignal fallback = null, Newtonsoft.Json.Linq.JArray capabilityGaps = null)
        {
            var detail = new Newtonsoft.Json.Linq.JObject { ["write_started"] = false, ["changes_applied"] = false };
            if (errorCode != null) detail["error_code"] = errorCode;
            return FailWithDetail(WithNothingWritten(error), detail, fallback, capabilityGaps);
        }

        /// <summary>
        /// The refusal text with "Nothing was written." exactly once at its end: a message that already ends with it (many plan
        /// refusals were written to stand alone) is not given a second copy (measured: "... Nothing was written. Nothing was written.").
        /// </summary>
        public static string WithNothingWritten(string error)
        {
            const string phrase = "Nothing was written.";
            string e = (error ?? "").TrimEnd();
            return e.EndsWith(phrase, System.StringComparison.Ordinal) ? e : (e.Length == 0 ? phrase : e + " " + phrase);
        }

        /// <summary>
        /// REBUILD A RECORDED ANSWER, field for field. The durable idempotency ledger is
        /// the only caller, and it needs this because the ordinary factories cannot
        /// express every shape a real result takes: a SUCCESS carrying a dry-run fallback
        /// grant has no factory, and reassembling one through Ok(...) plus CarryFallback
        /// worked only for the combinations somebody remembered to reassemble.
        ///
        /// That is exactly how three fields went missing from a replay. The ledger's job
        /// is to hand a retry the answer the first caller would have received; anything
        /// this constructor cannot carry is something the retry silently does not learn.
        ///
        /// It does not VALIDATE - the ledger decides whether a recorded combination is
        /// believable before it gets here, and an unbelievable one is in-doubt, not
        /// something to repair on the way past.
        /// </summary>
        internal static CommandResult Restore(bool success, object data, string error, object revitSaid,
                                              FallbackSignal fallback,
                                              Newtonsoft.Json.Linq.JArray capabilityGaps,
                                              Newtonsoft.Json.Linq.JObject detail)
            => new CommandResult
            {
                Success = success,
                Data = data,
                Error = error,
                RevitSaid = revitSaid,
                Fallback = fallback,
                CapabilityGaps = capabilityGaps,
                Detail = detail
            };

        /// <summary>
        /// A failure carrying a structured diagnostic beside its message. Built for the
        /// atomic-plan rollback path, where "what happened to the group" must be a value a
        /// client can read, not a sentence it has to trust. The message is expected to be
        /// derived from the same diagnostic (see PlanFailure.Message) so prose and data agree.
        /// </summary>
        public static CommandResult FailWithDetail(string error, Newtonsoft.Json.Linq.JObject detail)
            => new CommandResult { Success = false, Error = error, Detail = detail };

        /// <summary>
        /// A structured failure that also preserves the child's machine-readable fallback
        /// verdict. Atomic-plan rehearsal/recheck failures use this: flattening the child
        /// to prose would force the caller to infer permission from an English message.
        /// </summary>
        public static CommandResult FailWithDetail(string error, Newtonsoft.Json.Linq.JObject detail,
                                                   FallbackSignal fallback,
                                                   Newtonsoft.Json.Linq.JArray capabilityGaps)
            => new CommandResult
            {
                Success = false,
                Error = error,
                Detail = detail,
                Fallback = fallback,
                CapabilityGaps = capabilityGaps
            };

        /// <summary>
        /// A refusal carrying an already-decided signal - granted or refused - plus the
        /// per-action gaps behind it. Built by FallbackDecision, which is the only place
        /// the rule lives; a command that assembled this itself would be the fourth copy
        /// of a rule that has already been got wrong once.
        /// </summary>
        public static CommandResult FailWithFallback(string error, FallbackSignal signal,
                                                     Newtonsoft.Json.Linq.JArray capabilityGaps)
            => new CommandResult
            {
                Success = false,
                Error = error,
                Fallback = signal,
                CapabilityGaps = capabilityGaps
            };
    }
}
