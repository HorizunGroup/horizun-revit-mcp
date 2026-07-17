// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// horizun_delete_verified — the DELETE verb, and the site of the flagship lie.
//
// The incident this file exists to make impossible, in full:
//
//   A batch purge reported "758 types purged". Nothing was purged. Revit had
//   rolled the whole transaction back and Transaction.Commit() returned
//   TransactionStatus.RolledBack WITHOUT throwing. The script had done
//   `doc.Delete(List[ElementId](ids)); total += len(ids)` and printed
//   "Purgados (aprox): 758" — it counted its own Delete() calls. The real cause
//   was that the batch mixed FamilySymbol with WallType/FloorType/RoofType, and
//   one bad element poisoned everything around it.
//
// So this handler refuses the three habits that produced it:
//
//   * IT NEVER COUNTS ITS OWN INPUT. A deletion is counted only when the id
//     fails to re-resolve against the document AFTER the commit. doc.Delete()'s
//     return value is used ONLY to attribute cascades — never as a verdict. It
//     is a statement of intent, and intent is what lied.
//   * IT COMMITS THROUGH HorizunGuard.Commit. That single line is what turns the
//     silent RolledBack into an error instead of a success report. Everything
//     else here is detail; that line is the fix.
//   * IT DELETES ONE ID AT A TIME. Batch Delete() returns the union of everything
//     that died with no attribution, so "cascaded_by" off a batch call would be a
//     guess dressed as a measurement. Per-id also means one FamilySymbol among
//     the WallTypes cannot poison the other 757 — the actual mechanism of the
//     incident. It costs API calls and buys the truth.
//
// Purge mode iterates to a REAL fixed point — a pass whose candidate set comes
// back empty — instead of the `for _ in range(3)` the Python does and the
// "run Manage > Purge Unused by hand 2-3 times" the skill falls back to. Each
// pass is its own transaction (so its count is a real post-commit measurement)
// inside one TransactionGroup (so the user gets one undo step).
//
// And: `converged:false` and `purge_supported:false` are DIFFERENT things, and
// both differ from "nothing to purge". On a Revit without GetUnusedElements we
// say we could not look. We do not say the model is clean. CAD present is
// 🔴 BLOQUEA ENTREGA — a false clean here is the worst report this product can
// emit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class HorizunDeleteHandler : IRevitCommand
    {
        public string Name => "horizun_delete_verified";

        public string Description =>
            "Delete an explicit list of ElementIds, or purge unused elements to a real fixed point, and report " +
            "only what the model confirms. Every id comes back with EXACTLY ONE verdict out of deleted | not_found | " +
            "failed | skipped_still_in_use | skipped_protected | unexamined_unreadable_id | attempted_fate_unknown; " +
            "the totals are disjoint and sum to requested_total. The first five are decided by re-resolving that id " +
            "against the document AFTER the commit — never from the return of Delete(); the last two are the cases " +
            "where we could not look, and they are never folded into a failure or a survival. Elements Revit cascaded away that you did not " +
            "name are reported explicitly, attributed to the id that took them. A rolled-back transaction is an " +
            "error, not a count. dry_run defaults to TRUE in purge mode; this is destructive and it is a client's model.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""mode"": { ""type"": ""string"", ""enum"": [""ids"", ""purge_unused""],
                ""description"": ""ids: delete exactly the ids given. purge_unused: ask Revit for unused elements and delete them, repeating until a pass finds none."" },
    ""ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
               ""description"": ""Required for mode='ids'. Ids that do not resolve are reported as not_found, never dropped."" },
    ""protect_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
                       ""description"": ""Never delete these, even if a purge pass calls them unused. Use for view templates you are about to assign rather than delete."" },
    ""dry_run"": { ""type"": ""boolean"",
                   ""description"": ""Default TRUE for purge_unused, FALSE for ids. Opens a transaction, asks Revit what would die (the real dependent closure, cascades included), then ROLLS BACK on purpose."" },
    ""max_passes"": { ""type"": ""integer"", ""default"": 8, ""minimum"": 1,
                      ""description"": ""Safety stop for purge. Hitting it is reported as converged:false — a stop, not a finish."" },
    ""transaction_name"": { ""type"": ""string"", ""description"": ""Name of the undo step."" },
    ""id_cap"": { ""type"": ""integer"", ""default"": 200, ""minimum"": 1,
                  ""description"": ""How many rows to list. Totals are exact regardless; every list states total vs shown vs truncated."" }
  }
}";

        // Verdicts. Deliberately explicit strings: "I could not look" and "there is
        // nothing there" must never collapse into the same value downstream.
        private const string V_DELETED = "deleted";
        private const string V_NOT_FOUND = "not_found";
        private const string V_FAILED = "failed";
        private const string V_IN_USE = "skipped_still_in_use";
        private const string V_PROTECTED = "skipped_protected";

        // The two "we could not look" verdicts. They exist because folding them into
        // V_FAILED made an element carry two totals at once — failed_total AND
        // unexamined_unreadable_id — which broke the one-verdict-per-id promise above and
        // made deleted+failed+in_use overshoot `attempted` by N with no way for a consumer
        // to explain the gap. Worse, V_FAILED feeds `residual`, whose note asserts the
        // element "survived": that turned "we never looked at it" into a positive claim
        // about the model, the mirror image of the unknown-to-deleted lie this file kills.
        private const string V_UNEXAMINED = "unexamined_unreadable_id";   // never attempted: its id could not be read
        private const string V_UNVERIFIABLE = "attempted_fate_unknown";   // attempted, but the post-commit re-resolve threw

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            if (doc.IsReadOnly) return CommandResult.Fail("The document is read-only; nothing can be deleted.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var mode = (request.Value<string>("mode") ?? "").ToLowerInvariant();
            if (mode.Length == 0) mode = request["ids"] != null ? "ids" : "purge_unused";
            if (mode != "ids" && mode != "purge_unused")
                return CommandResult.Fail("mode must be 'ids' or 'purge_unused'.");

            // Purge defaults to dry_run. An explicit id list is an explicit act; a
            // purge is a machine deciding what your model does not need.
            bool dryRun = request["dry_run"] != null
                ? request.Value<bool>("dry_run")
                : (mode == "purge_unused");

            int maxPasses = request["max_passes"] != null ? Math.Max(1, request.Value<int>("max_passes")) : 8;
            int idCap = request["id_cap"] != null ? Math.Max(1, request.Value<int>("id_cap")) : 200;
            var txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName))
                txName = mode == "ids" ? "Horizun: delete elements" : "Horizun: purge unused";

            // A protect list we cannot read protects NOTHING. `as JArray` on a bare int
            // or a string yields null, and null means "no protection" — the purge then
            // deletes the very ids the caller listed and reports skipped_protected: 0.
            // Refusing is the only honest answer: there is no way to run this request.
            var protectTok = request["protect_ids"];
            if (protectTok != null && protectTok.Type != JTokenType.Null && !(protectTok is JArray))
                return CommandResult.Fail(
                    "protect_ids must be a JSON array of integer element ids; got " + protectTok.Type +
                    ". Refusing to run: an unreadable protect list protects nothing, and the purge would " +
                    "delete exactly the elements you listed while reporting skipped_protected_total: 0.");

            var idsTok = request["ids"];
            if (idsTok != null && idsTok.Type != JTokenType.Null && !(idsTok is JArray))
                return CommandResult.Fail(
                    "ids must be a JSON array of integer element ids; got " + idsTok.Type + ".");

            // One reject array PER FIELD. Sharing one array let a protect_ids rejection
            // answer a question that was only ever about `ids`: {"ids": [], "protect_ids":
            // ["v1"]} made rejected.Count == 1, sailed past the "did the caller name
            // anything" guard below, and returned a clean Ok with requested_total: 0 and
            // verification.verified — a success report for a call that named nothing.
            var protRejected = new JArray();
            var protectedIds = ReadIds(protectTok as JArray, protRejected, "protect_ids",
                "It could not be read as an element id, so NOTHING is protecting it.");

            // A PARTIALLY unreadable protect list is the whole-token guard's defect in
            // miniature, and it used to slip straight past it: {"protect_ids": [12345,
            // "67890"]} left 67890 unprotected, so a purge pass that found it unused
            // deleted it and counted it in deleted_total — while rejected_input asserted
            // it "was NOT deleted". The entry the caller wrote to save an element is the
            // one thing that must never fail quietly. Refuse, exactly as we refuse an
            // entirely unreadable list: there is no way to run this request honestly.
            if (protRejected.Count > 0)
                return CommandResult.Fail(
                    "protect_ids contains " + protRejected.Count + " entry/entries that are not integer element ids: " +
                    string.Join(", ", protRejected.Select(r => "'" + r["value"] + "'").ToArray()) +
                    ". Refusing to run: an id that cannot be read protects nothing, so those elements would be " +
                    "deleted by the very purge you listed them to survive, and skipped_protected_total would not " +
                    "mention them. Fix them to integers and re-run.");

            try
            {
                if (mode == "ids")
                {
                    var idsRejected = new JArray();
                    var ids = ReadIds(idsTok as JArray, idsRejected, "ids",
                        "It was NOT deleted; ignoring it silently is how a caller mistakes an untouched element " +
                        "for a deleted one.");
                    // Only the `ids` field can answer this. An empty list with nothing
                    // rejected out of it means the caller asked us to delete nothing.
                    if (ids.Count == 0 && idsRejected.Count == 0)
                        return CommandResult.Fail("mode='ids' requires a non-empty ids array.");
                    return DeleteIds(doc, ids, protectedIds, dryRun, txName, idCap,
                        Concat(idsRejected, protRejected));
                }
                return PurgeUnused(doc, protectedIds, dryRun, maxPasses, txName, idCap,
                    Concat(protRejected));
            }
            catch (HorizunSilentRollbackException ex)
            {
                // The 758 lie, caught at the only place it is catchable.
                return CommandResult.Fail(ex.Message);
            }
        }

        // ---------------------------------------------------------------------
        // Mode 1: an explicit id list.
        // ---------------------------------------------------------------------
        private static CommandResult DeleteIds(Document doc, List<long> ids, List<long> protectedIds,
            bool dryRun, string txName, int idCap, JArray rejected)
        {
            var before = Census(doc);
            var targets = BuildTargets(doc, ids, protectedIds, rejected);
            var cascades = new List<Cascade>();
            var ledger = new WarningLedger();

            if (dryRun)
            {
                var preview = Preview(doc, targets, new HashSet<long>(ids), cascades, txName, ledger);
                if (preview != null) return preview;   // rollback of a dry run is normal; a throw is not
                return CommandResult.Ok(Report(doc, "ids", true, txName, before, before, targets, cascades,
                    idCap, rejected, null, ledger));
            }

            var owned = new HashSet<long>();
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                var handler = tx.GetFailureHandlingOptions();
                handler.SetFailuresPreprocessor(new SwallowNothingPreprocessor(owned, ledger));
                tx.SetFailureHandlingOptions(handler);
                try
                {
                    DeleteEach(doc, targets, new HashSet<long>(ids), cascades, owned);
                    // The line that makes the 758 impossible.
                    HorizunGuard.Commit(tx, txName);
                }
                catch (HorizunSilentRollbackException) { throw; }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Delete failed, nothing was deleted: " + ex.Message);
                }
            }

            // ONLY now do the verdicts exist. Everything above was intent.
            ResolveVerdicts(doc, targets);
            ResolveCascades(doc, cascades);
            var after = Census(doc);

            return CommandResult.Ok(Report(doc, "ids", false, txName, before, after, targets, cascades,
                idCap, rejected, null, ledger));
        }

        // ---------------------------------------------------------------------
        // Mode 2: purge unused, to a real fixed point.
        // ---------------------------------------------------------------------
        private static CommandResult PurgeUnused(Document doc, List<long> protectedIds, bool dryRun,
            int maxPasses, string txName, int idCap, JArray rejected)
        {
            string lookError;
            var first = GetUnused(doc, out lookError);
            if (first == null)
            {
                // NOT "nothing to purge". We could not look. These are different
                // facts and the caller must be able to tell them apart.
                return CommandResult.Ok(new JObject
                {
                    ["mode"] = "purge_unused",
                    ["purge_supported"] = false,
                    ["purge_unavailable_reason"] = lookError,
                    ["dry_run"] = dryRun,
                    ["deleted_total"] = 0,
                    ["converged"] = null,
                    ["remaining_purgeable"] = null,
                    ["failed"] = new JArray(),
                    ["residual"] = new JArray(),
                    ["note"] = "This Revit does not expose Document.GetUnusedElements, so NOTHING WAS EXAMINED. " +
                               "This is not a clean model; it is an unexamined one. Purge by hand or use mode='ids' " +
                               "with a candidate list you trust. Reporting 0 purged here would be the same lie as " +
                               "reporting 758."
                });
            }

            var before = Census(doc);
            var protectedSet = new HashSet<long>(protectedIds);
            // Keyed by id, not appended blindly: an element that comes back
            // skipped_still_in_use is STILL unused to Revit, so GetUnused re-offers it
            // every pass. Appending a fresh Target each time turned 50 stubborn
            // survivors across 4 passes into "skipped_still_in_use_total: 200" and sent
            // the user chasing 150 elements that do not exist.
            var byRawId = new Dictionary<long, Target>();
            var unreadableTargets = new List<Target>();
            var cascades = new List<Cascade>();
            var ledger = new WarningLedger();
            var passes = new JArray();
            bool converged = false;
            string convergenceBlockedBy = null;
            int passesRun = 0;

            if (dryRun)
            {
                // A dry run cannot honestly claim to know pass 2: pass 2's candidates
                // only exist once pass 1 is committed. So we report pass 1 exactly and
                // say the rest is unknown, rather than extrapolating.
                var t1 = BuildTargetsFrom(doc, first, protectedSet);
                var preview = Preview(doc, t1,
                    new HashSet<long>(t1.Where(t => t.RawId.HasValue).Select(t => t.RawId.Value)),
                    cascades, txName, ledger);
                if (preview != null) return preview;
                var report = Report(doc, "purge_unused", true, txName, before, before, t1, cascades, idCap, rejected, null, ledger);
                report["purge_supported"] = true;
                report["pass_1_candidates"] = first.Count;
                report["passes_run"] = 0;
                report["converged"] = null;
                report["remaining_purgeable"] = first.Count;
                report["note"] = "Nothing was deleted; the transaction was rolled back on purpose. Only pass 1 is " +
                                 "shown: purging cascades, so later passes' candidates do not exist until pass 1 is " +
                                 "committed. The real total is >= this. Re-run with dry_run=false.";
                return CommandResult.Ok(report);
            }

            // One group so the user gets one undo step; one transaction per pass so
            // each pass's count is a real post-commit measurement and not a tally.
            using (var group = new TransactionGroup(doc, txName))
            {
                group.Start();
                try
                {
                    var candidates = first;
                    while (passesRun < maxPasses)
                    {
                        var passTargets = BuildTargetsFrom(doc, candidates, protectedSet);
                        var deletable = passTargets.Where(t => t.Verdict == null).ToList();
                        int unreadableThisPass = passTargets.Count(t => t.IdUnreadable);
                        if (deletable.Count == 0)
                        {
                            // A pass with no candidates. THIS is what convergence means —
                            // not that a counter ran out. But candidates we could not
                            // even read are not "no candidates"; they are unexamined, and
                            // calling that convergence is the purge_supported:false lie
                            // wearing a different word.
                            Merge(byRawId, unreadableTargets, passTargets);
                            converged = unreadableThisPass == 0;
                            if (!converged)
                                convergenceBlockedBy = "The last pass offered no deletable candidates, but " +
                                    unreadableThisPass + " candidate(s) had unreadable ids and were never examined. " +
                                    "Convergence cannot be claimed over elements nobody looked at.";
                            break;
                        }

                        passesRun++;
                        var passCascades = new List<Cascade>();
                        var passOwned = new HashSet<long>();
                        using (var tx = new Transaction(doc, txName + " (pass " + passesRun + ")"))
                        {
                            tx.Start();
                            var opts = tx.GetFailureHandlingOptions();
                            opts.SetFailuresPreprocessor(new SwallowNothingPreprocessor(passOwned, ledger));
                            tx.SetFailureHandlingOptions(opts);
                            DeleteEach(doc, deletable,
                                new HashSet<long>(passTargets.Where(t => t.RawId.HasValue).Select(t => t.RawId.Value)),
                                passCascades, passOwned);
                            HorizunGuard.Commit(tx, txName + " (pass " + passesRun + ")");
                        }

                        ResolveVerdicts(doc, deletable);
                        ResolveCascades(doc, passCascades);
                        cascades.AddRange(passCascades);
                        Merge(byRawId, unreadableTargets, passTargets);

                        int deletedThisPass = deletable.Count(t => t.Verdict == V_DELETED);
                        passes.Add(new JObject
                        {
                            ["pass"] = passesRun,
                            ["candidates_offered"] = candidates.Count,
                            ["attempted"] = deletable.Count,
                            ["protected_skipped"] = passTargets.Count(t => t.Verdict == V_PROTECTED),
                            ["deleted_confirmed"] = deletedThisPass,
                            ["still_in_use"] = deletable.Count(t => t.Verdict == V_IN_USE),
                            ["failed"] = deletable.Count(t => t.Verdict == V_FAILED),
                            ["attempted_fate_unknown"] = deletable.Count(t => t.Verdict == V_UNVERIFIABLE),
                            ["unexamined_unreadable_id"] = unreadableThisPass,
                            ["cascaded_confirmed_gone"] = passCascades.Count(c => c.Gone == true),
                            ["cascaded_unknown"] = passCascades.Count(c => !c.Gone.HasValue)
                        });

                        // Nothing moved despite candidates on the table: another pass
                        // would offer the same list forever. Stop and say it did not
                        // converge, rather than spin and claim it did.
                        if (deletedThisPass == 0) break;

                        string err;
                        candidates = GetUnused(doc, out err);
                        if (candidates == null)
                        {
                            passes.Add(new JObject
                            {
                                ["pass"] = passesRun + 1,
                                ["error"] = err,
                                ["consequence"] = "Could not re-ask Revit for unused elements; convergence is UNKNOWN."
                            });
                            break;
                        }
                    }

                    HorizunGuard.Assimilate(group, txName);
                }
                catch (HorizunSilentRollbackException)
                {
                    if (group.HasStarted()) group.RollBack();
                    throw;
                }
                catch (Exception ex)
                {
                    if (group.HasStarted()) group.RollBack();
                    return CommandResult.Fail("Purge failed and was rolled back; nothing was purged: " + ex.Message);
                }
            }

            // One row per element, not one per (element, pass). Re-verify EVERY target
            // against the assimilated document. The per-pass numbers above were true
            // when measured; these are true now, and these are the ones totals come from.
            var allTargets = new List<Target>(byRawId.Values);
            allTargets.AddRange(unreadableTargets);
            ResolveVerdicts(doc, allTargets);
            ResolveCascades(doc, cascades);
            var after = Census(doc);

            string residualErr;
            var leftover = GetUnused(doc, out residualErr);

            var result = Report(doc, "purge_unused", false, txName, before, after, allTargets, cascades,
                idCap, rejected, null, ledger);
            result["purge_supported"] = true;
            result["passes_run"] = passesRun;
            result["converged"] = converged;
            result["passes"] = passes;
            result["unexamined_unreadable_id_total"] = unreadableTargets.Count;
            result["remaining_purgeable"] = leftover != null ? (JToken)leftover.Count : null;
            result["remaining_purgeable_unknown_reason"] = leftover != null ? null : residualErr;
            result["note"] = converged
                ? (leftover != null && leftover.Count > 0
                    ? "A pass returned no deletable candidates, but Revit still reports " + leftover.Count +
                      " unused element(s) — those are protected, in use, or refused. Purge is done, the model is not empty."
                    : null)
                : (convergenceBlockedBy ??
                   "DID NOT CONVERGE after " + passesRun + " pass(es): the loop stopped on max_passes or on a pass " +
                   "that deleted nothing. More may be purgeable. Do not report this model as fully purged.");
            return CommandResult.Ok(result);
        }

        // ---------------------------------------------------------------------
        // The delete itself. Note what this method does NOT do: return a count.
        // ---------------------------------------------------------------------
        private static void DeleteEach(Document doc, List<Target> targets, HashSet<long> requested,
            List<Cascade> cascades, HashSet<long> owned)
        {
            var byId = new Dictionary<long, Target>();
            foreach (var t in targets) if (t.RawId.HasValue) byId[t.RawId.Value] = t;

            foreach (var t in targets)
            {
                if (t.Verdict != null) continue;   // already not_found, protected or unreadable
                if (!t.RawId.HasValue) continue;
                if (owned != null) owned.Add(t.RawId.Value);

                // An earlier id's cascade may have taken this one already. Calling
                // Delete on it would throw and read as a failure, when in fact it is
                // gone — and gone for a reason worth naming.
                if (doc.GetElement(t.Id) == null) continue;

                ICollection<ElementId> touched = null;
                try
                {
                    touched = doc.Delete(t.Id);
                }
                catch (Exception ex)
                {
                    // Recorded, never swallowed — but it is still not the verdict.
                    // The model decides that after the commit; this only explains why.
                    t.Reason = ex.Message;
                    continue;
                }

                if (touched == null) continue;
                foreach (var got in touched)
                {
                    long raw;
                    try { raw = RevitCompat.GetId(got); }
                    catch (Exception ex)
                    {
                        // Revit just told us it took this element along. `continue` here
                        // meant it entered NO list: not cascaded_confirmed_gone, not
                        // cascaded_confirmed_surviving, and not even cascaded_unknown —
                        // because cascaded_unknown counts Cascade objects, and no Cascade
                        // was ever made. cascaded_unknown_note then stayed null and the
                        // response presented a COMPLETE collateral accounting for a delete
                        // whose blast radius it had failed to measure. That is the same
                        // lie ResolveCascades was hardened against, one function upstream:
                        // hardening the read is worthless if the list being read is the
                        // one that dropped the element. So record it as an unknown.
                        //
                        // We cannot check `raw == t.RawId` or `requested.Contains(raw)` on
                        // an id we could not read, so ElementId identity is the only
                        // self-check available; anything it does not catch is reported as
                        // collateral of unknown fate, which over-states the blast radius
                        // rather than hiding it.
                        if (SameElementId(got, t.Id)) continue;
                        cascades.Add(new Cascade
                        {
                            RawId = null,
                            Id = got,
                            IdUnreadable = true,
                            ParentRawId = t.RawId.Value,
                            Gone = null,
                            Note = "Revit reported this element as taken along by " + t.RawId.Value +
                                   ", but its id could not be read, so it could never be re-resolved and its fate " +
                                   "is UNKNOWN — it is neither confirmed gone nor confirmed surviving: " + ex.Message
                        });
                        continue;
                    }
                    if (raw == t.RawId) continue;

                    Target sibling;
                    if (byId.TryGetValue(raw, out sibling))
                    {
                        // We asked for it too, but this id got there first. Say so —
                        // otherwise its later Delete() throw looks like a failure.
                        if (sibling.CascadedBy == null) sibling.CascadedBy = t.RawId;
                        continue;
                    }
                    if (requested.Contains(raw)) continue;

                    // Collateral. Deleting one element can silently take others; the
                    // whole point of reporting this is that "silently" stops here.
                    if (owned != null) owned.Add(raw);
                    cascades.Add(new Cascade { RawId = raw, Id = got, ParentRawId = t.RawId.Value });
                }
            }
        }

        /// <summary>
        /// dry_run: do the real thing, then throw it away. Revit is the only source
        /// of the true dependent closure — guessing it from category rules is how you
        /// discover the cascade in production. A deliberate RollBack here is correct,
        /// so this does NOT go through HorizunGuard.
        /// Returns non-null only on hard failure.
        /// </summary>
        private static CommandResult Preview(Document doc, List<Target> targets, HashSet<long> requested,
            List<Cascade> cascades, string txName, WarningLedger ledger)
        {
            var owned = new HashSet<long>();
            using (var tx = new Transaction(doc, txName + " (dry run)"))
            {
                tx.Start();
                var opts = tx.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new SwallowNothingPreprocessor(owned, ledger));
                tx.SetFailureHandlingOptions(opts);
                try
                {
                    DeleteEach(doc, targets, requested, cascades, owned);
                    foreach (var t in targets)
                    {
                        if (t.Verdict != null) continue;
                        bool gone = doc.GetElement(t.Id) == null;
                        t.Verdict = gone ? V_DELETED : (t.Reason == null ? V_IN_USE : V_FAILED);
                    }
                    // Same rule as ResolveCascades: an unreadable cascade id stays unknown.
                    // A dry run that reported it as gone would be predicting the fate of an
                    // element it could not even name.
                    foreach (var c in cascades)
                        if (!c.IdUnreadable) c.Gone = doc.GetElement(c.Id) == null;
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Dry run failed before it could measure anything: " + ex.Message);
                }
                tx.RollBack();
            }
            return null;
        }

        // ---------------------------------------------------------------------
        // Verdicts: the only place a deletion is allowed to be called a deletion.
        // ---------------------------------------------------------------------
        private static void ResolveVerdicts(Document doc, List<Target> targets)
        {
            foreach (var t in targets)
            {
                if (t.Verdict == V_NOT_FOUND || t.Verdict == V_PROTECTED || t.Verdict == V_UNEXAMINED) continue;

                // Never attempted, and we cannot even name it. Re-resolving it here
                // could return null and promote an unexamined candidate to "deleted".
                if (t.IdUnreadable) continue;

                bool gone;
                try { gone = doc.GetElement(t.Id) == null; }
                catch (Exception ex)
                {
                    // We could not look. That is neither deleted nor surviving, and it must
                    // not be counted as either — including not as V_FAILED, which feeds
                    // `residual` and its "N element(s) survived" claim. "The re-resolve
                    // threw" is not evidence the element is still in the model.
                    t.Verdict = V_UNVERIFIABLE;
                    t.Reason = "Delete() was attempted for this id, but re-resolving it after the commit threw, so " +
                               "its fate is UNKNOWN — it is neither confirmed deleted nor confirmed surviving: " + ex.Message;
                    continue;
                }

                if (gone)
                {
                    // The model says it is gone. That outranks an exception from
                    // Delete(): Revit sometimes throws and deletes anyway.
                    t.Verdict = V_DELETED;
                }
                else if (t.Reason != null)
                {
                    t.Verdict = V_FAILED;
                }
                else
                {
                    // Delete() did not complain and the element is still here. This is
                    // the exact shape of the 758: no error, no deletion.
                    t.Verdict = V_IN_USE;
                    t.Reason = "Delete() raised nothing, yet the element still resolves after the commit. Revit " +
                               "kept it — it is still referenced, pinned, in a group, or owned elsewhere.";
                }
            }
        }

        private static void ResolveCascades(Document doc, List<Cascade> cascades)
        {
            foreach (var c in cascades)
            {
                // Its id was never readable, so its Gone/Note are already the honest
                // unknown DeleteEach recorded. Re-resolving it here could hand back null
                // and promote a collateral element nobody could name to confirmed_gone.
                if (c.IdUnreadable) continue;

                try { c.Gone = doc.GetElement(c.Id) == null; }
                catch (Exception ex)
                {
                    // null, not false. `Gone = false` was a claim that this collateral
                    // element survived — it made an unknown cascade drop out of the
                    // confirmed count, under-reporting the blast radius of a handler
                    // whose whole point is that silent cascades stop here.
                    c.Gone = null;
                    c.Note = "Could not re-resolve this cascaded id after the commit, so its fate is UNKNOWN — " +
                             "it is neither confirmed gone nor confirmed surviving: " + ex.Message;
                }
            }
        }

        // ---------------------------------------------------------------------
        // Building targets. Nothing is dropped in silence, at any point.
        // ---------------------------------------------------------------------
        private static List<Target> BuildTargets(Document doc, List<long> ids, List<long> protectedIds, JArray rejected)
        {
            var prot = new HashSet<long>(protectedIds);
            var seen = new HashSet<long>();
            var list = new List<Target>();
            foreach (var raw in ids)
            {
                if (!seen.Add(raw)) continue;      // a duplicate id is one element, not two deletions
                if (!RevitCompat.CanRepresentElementId(raw))
                {
                    rejected.Add(new JObject { ["id"] = raw, ["error"] = RevitCompat.ElementIdRangeError(raw) });
                    continue;
                }
                var eid = RevitCompat.ToElementId(raw);
                var elem = doc.GetElement(eid);
                var t = new Target { RawId = raw, Id = eid, ExistedBefore = elem != null };
                if (elem != null) { t.Name = SafeName(elem); t.Category = SafeCategory(elem); }

                if (prot.Contains(raw))
                {
                    t.Verdict = V_PROTECTED;
                    t.Reason = "In protect_ids. Not touched.";
                }
                else if (elem == null)
                {
                    // Distinct from "deleted". Nobody deleted this; it was never here.
                    t.Verdict = V_NOT_FOUND;
                    t.Reason = "No element with this id in this document — it was already gone, or it belongs to " +
                               "another model. Nothing was deleted for this id.";
                }
                list.Add(t);
            }
            return list;
        }

        /// <summary>
        /// Fold a pass's targets into the run-wide set, one row per element. The newest
        /// Target for an id replaces the older one — same element, later verdict — so a
        /// survivor re-offered on every pass is reported once, not once per pass.
        /// Unreadable candidates have no id to key on and are already deduped by
        /// ElementId identity inside a pass; across passes they can only be listed.
        /// </summary>
        private static void Merge(Dictionary<long, Target> byRawId, List<Target> unreadable, List<Target> passTargets)
        {
            foreach (var t in passTargets)
            {
                if (t.RawId.HasValue) byRawId[t.RawId.Value] = t;
                else if (!unreadable.Any(u => ReferenceEquals(u.Id, t.Id) || SameElementId(u.Id, t.Id)))
                    unreadable.Add(t);
            }
        }

        private static bool SameElementId(ElementId a, ElementId b)
        {
            try { return a != null && b != null && a.Equals(b); } catch { return false; }
        }

        private static List<Target> BuildTargetsFrom(Document doc, ICollection<ElementId> ids, HashSet<long> prot)
        {
            var list = new List<Target>();
            var seen = new HashSet<long>();
            var seenUnreadable = new HashSet<ElementId>();
            foreach (var eid in ids)
            {
                long raw;
                try { raw = RevitCompat.GetId(eid); }
                catch (Exception ex)
                {
                    // Dropping this used to make the candidate vanish from every list —
                    // and an all-unreadable pass then set converged:true, so the caller
                    // read a fully purged model where N candidates were never examined.
                    // Dedupe by ElementId identity: we cannot key it by a number we
                    // could not read, but Revit's own equality still holds.
                    bool fresh;
                    try { fresh = seenUnreadable.Add(eid); } catch { fresh = true; }
                    if (!fresh) continue;
                    list.Add(new Target
                    {
                        RawId = null,
                        Id = eid,
                        IdUnreadable = true,
                        // V_UNEXAMINED, not V_FAILED. As V_FAILED this row was counted
                        // twice — once in failed_total, once in unexamined_unreadable_id —
                        // and it flowed into `residual`, whose note then asserted it
                        // "survived" and that "the next audit will re-flag" it. Its fate is
                        // UNKNOWN; we never touched it. Its own Reason said so while the
                        // total above it said otherwise.
                        Verdict = V_UNEXAMINED,
                        Reason = "Revit offered this element as unused but its id could not be read, so it was " +
                                 "never attempted and its fate is UNKNOWN: " + ex.Message
                    });
                    continue;
                }
                if (!seen.Add(raw)) continue;
                var elem = doc.GetElement(eid);
                var t = new Target { RawId = raw, Id = eid, ExistedBefore = elem != null };
                if (elem != null) { t.Name = SafeName(elem); t.Category = SafeCategory(elem); }
                if (prot.Contains(raw))
                {
                    t.Verdict = V_PROTECTED;
                    t.Reason = "In protect_ids — kept even though Revit called it unused. This is how a view " +
                               "template about to be assigned survives the purge that would have deleted it.";
                }
                else if (elem == null)
                {
                    t.Verdict = V_NOT_FOUND;
                    t.Reason = "Revit offered this id as unused but it no longer resolves; an earlier pass or " +
                               "cascade already took it.";
                }
                list.Add(t);
            }
            return list;
        }

        /// <summary>
        /// Asks Revit what is unused. Reflection on purpose: GetUnusedElements does not
        /// exist on older Revit and its signature has diverged, and a compile-time
        /// reference would either break the R22 build or force a #if that silently
        /// returns an empty list there — i.e. "0 to purge" on a model nobody examined.
        /// Returns null (with a reason) when we could not look. Never an empty list.
        /// </summary>
        private static ICollection<ElementId> GetUnused(Document doc, out string error)
        {
            error = null;
            try
            {
                MethodInfo mi = null;
                foreach (var m in typeof(Document).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "GetUnusedElements", StringComparison.Ordinal)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    if (!ps[0].ParameterType.IsAssignableFrom(typeof(HashSet<ElementId>))) continue;
                    mi = m;
                    break;
                }
                if (mi == null)
                {
                    error = "Document.GetUnusedElements(ISet<ElementId>) is not available in this Revit version " +
                            "(it arrived in Revit 2024). No purge candidates could be computed.";
                    return null;
                }

                var raw = mi.Invoke(doc, new object[] { new HashSet<ElementId>() });
                var set = raw as ICollection<ElementId>;
                if (set == null)
                {
                    error = "GetUnusedElements returned " + (raw == null ? "null" : raw.GetType().Name) +
                            ", which is not a collection of ElementId. Candidates unknown.";
                    return null;
                }
                return set;
            }
            catch (TargetInvocationException ex)
            {
                error = "GetUnusedElements threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                error = "Could not call GetUnusedElements: " + ex.Message;
                return null;
            }
        }

        // ---------------------------------------------------------------------
        // Reporting. Counts come from here; the loop above never gets a vote.
        // ---------------------------------------------------------------------
        private static JObject Report(Document doc, string mode, bool dryRun, string txName,
            Census2 before, Census2 after, List<Target> targets, List<Cascade> cascades,
            int idCap, JArray rejected, string note, WarningLedger ledger)
        {
            int deleted = targets.Count(t => t.Verdict == V_DELETED);
            int notFound = targets.Count(t => t.Verdict == V_NOT_FOUND);
            int failed = targets.Count(t => t.Verdict == V_FAILED);
            int inUse = targets.Count(t => t.Verdict == V_IN_USE);
            int prot = targets.Count(t => t.Verdict == V_PROTECTED);

            // Disjoint from `failed` now. These two used to be a second label on rows that
            // were ALSO counted as failed, so deleted+failed+inUse overshot `attempted` by
            // N and no consumer could reconcile the buckets against requested_total.
            int unexamined = targets.Count(t => t.Verdict == V_UNEXAMINED);
            int unverifiable = targets.Count(t => t.Verdict == V_UNVERIFIABLE);
            int fateUnknown = unexamined + unverifiable;

            var rows = new JArray(targets.Take(idCap).Select(t => (JToken)new JObject
            {
                ["id"] = t.RawId.HasValue ? (JToken)t.RawId.Value : null,
                ["name"] = t.Name,
                ["category"] = t.Category,
                ["existed_before"] = t.ExistedBefore,
                ["verdict"] = t.Verdict,
                ["reason"] = t.Reason,
                ["cascaded_by"] = t.CascadedBy.HasValue ? (JToken)t.CascadedBy.Value : null
            }));

            var casc = new JArray(cascades.Take(idCap).Select(c => (JToken)new JObject
            {
                ["id"] = c.RawId.HasValue ? (JToken)c.RawId.Value : null,
                ["id_unreadable"] = c.IdUnreadable,
                ["cascaded_by"] = c.ParentRawId,
                ["confirmed_gone"] = c.Gone.HasValue ? (JToken)c.Gone.Value : null,
                ["note"] = c.Note
            }));

            int cascGone = cascades.Count(c => c.Gone == true);
            int cascSurviving = cascades.Count(c => c.Gone == false);
            int cascUnknown = cascades.Count(c => !c.Gone.HasValue);
            int cascUnreadable = cascades.Count(c => c.IdUnreadable);

            // residual: what we were asked to remove and the model CONFIRMS is still here.
            // Required, always present — an absent field reads as "clean" to every
            // consumer. V_UNEXAMINED and V_UNVERIFIABLE are deliberately NOT in here: this
            // list's note makes a positive assertion ("survived", "the next audit will
            // re-flag them") and an element whose fate we never established cannot back it.
            var residual = new JArray(targets
                .Where(t => t.Verdict == V_IN_USE || t.Verdict == V_FAILED)
                .Take(idCap)
                .Select(t => (JToken)new JObject
                {
                    ["id"] = t.RawId.HasValue ? (JToken)t.RawId.Value : null,
                    ["name"] = t.Name,
                    ["category"] = t.Category,
                    ["verdict"] = t.Verdict,
                    ["reason"] = t.Reason
                }));
            int residualTotal = inUse + failed;

            var failures = new JArray(targets
                .Where(t => t.Verdict == V_FAILED)
                .Take(idCap)
                .Select(t => (JToken)new JObject
                {
                    ["id"] = t.RawId.HasValue ? (JToken)t.RawId.Value : null,
                    ["error"] = t.Reason
                }));

            var unknowns = new JArray(targets
                .Where(t => t.Verdict == V_UNEXAMINED || t.Verdict == V_UNVERIFIABLE)
                .Take(idCap)
                .Select(t => (JToken)new JObject
                {
                    ["id"] = t.RawId.HasValue ? (JToken)t.RawId.Value : null,
                    ["name"] = t.Name,
                    ["category"] = t.Category,
                    ["verdict"] = t.Verdict,
                    ["reason"] = t.Reason
                }));

            // Never attempted: it was neither tried nor found, so it is not part of the
            // set `verification` reconciles intent against. V_UNVERIFIABLE rows WERE
            // attempted and stay in, which is why deleted+failed+inUse+unverifiable ==
            // attempted, and attempted+notFound+prot+unexamined == requested_total.
            int attempted = targets.Count - notFound - prot - unexamined;

            // A census that threw has no number. Subtracting one unknown from another
            // used to yield a confident integer — a failed AFTER census reported the
            // whole model as shrinkage. Unknown in, unknown out, with the reason.
            JToken shrank = null;
            string shrankReason = null;
            if (before.Total.HasValue && after.Total.HasValue)
                shrank = before.Total.Value - after.Total.Value;
            else
                shrankReason = "Could not count the model " +
                    (!before.Total.HasValue && !after.Total.HasValue ? "before or after" :
                     !before.Total.HasValue ? "before" : "after") +
                    " the delete, so the size change is UNKNOWN — not zero, and not the totals below. " +
                    string.Join(" ", new[] { before.Error, after.Error }.Where(e => e != null).ToArray());

            var o = new JObject
            {
                ["mode"] = mode,
                ["dry_run"] = dryRun,
                ["transaction_name"] = txName,
                ["transaction_state"] = dryRun ? "RolledBack (deliberate: dry run)" : "Committed",
                ["model"] = SafeTitle(doc),

                ["requested_total"] = targets.Count,
                ["attempted"] = attempted,

                // Found by running it, not by reading it: three rounds of review let
                // `deleted_total: 302` ship next to `dry_run: true`. Everything under it
                // was honest — the transaction rolled back, the note said so, the model
                // was unchanged — and the one number a human's eye lands on still said
                // 302 things were deleted. That is the 758 lie's exact shape, rebuilt by
                // a field name. A dry run has no deletions to total, so it must not have
                // the field: the count of what WOULD die is a different fact and gets a
                // different name.
                ["deleted_total"] = dryRun ? null : (JToken)deleted,
                ["would_delete_total"] = dryRun ? (JToken)deleted : null,
                ["counts_note"] = dryRun
                    ? "dry_run: NOTHING was deleted. would_delete_total is what Revit accepted for deletion inside a " +
                      "transaction that was then rolled back on purpose — it is a rehearsal, and deleted_total is null " +
                      "because there are no deletions to count. Every other total below describes that rehearsal."
                    : null,

                ["not_found_total"] = notFound,
                ["failed_total"] = failed,
                ["skipped_still_in_use_total"] = inUse,
                ["skipped_protected_total"] = prot,
                ["unexamined_unreadable_id"] = unexamined,
                ["attempted_fate_unknown_total"] = unverifiable,
                ["fate_unknown_total"] = fateUnknown,
                ["totals_are_disjoint"] = true,
                ["totals_reconcile"] =
                    "deleted_total + failed_total + skipped_still_in_use_total + attempted_fate_unknown_total = attempted; " +
                    "attempted + not_found_total + skipped_protected_total + unexamined_unreadable_id = requested_total. " +
                    "Every id carries exactly one verdict and is counted in exactly one of these.",

                // Three numbers, because "gone", "still here" and "we could not look"
                // are three facts. A single cascaded_total silently dropped the unknowns
                // and under-reported the collateral of a delete.
                ["cascaded_confirmed_gone"] = cascGone,
                ["cascaded_confirmed_surviving"] = cascSurviving,
                ["cascaded_unknown"] = cascUnknown,
                ["cascaded_unreadable_id"] = cascUnreadable,
                ["cascaded_unknown_note"] = cascUnknown == 0
                    ? null
                    : cascUnknown + " element(s) Revit reported as taken along could not be re-resolved afterwards" +
                      (cascUnreadable > 0
                          ? " (" + cascUnreadable + " of them because their ids were never readable, so they could " +
                            "not even be named)"
                          : "") +
                      ". cascaded_confirmed_gone is therefore a LOWER BOUND on the collateral of this delete.",

                ["elements_before"] = before.Instances,
                ["elements_after"] = after.Instances,
                ["types_before"] = before.Types,
                ["types_after"] = after.Types,
                ["model_shrank_by"] = shrank,
                ["model_shrank_by_unknown_reason"] = shrankReason,

                // Verify's mismatch note is FIXED prose: "The difference was NOT applied."
                // That is a positive claim of absence, so it may only ever be handed a
                // difference made of CONFIRMED survivors. Fed `attempted`, the difference
                // was deleted+failed+inUse+unverifiable — and a V_UNVERIFIABLE row's own
                // reason says "neither confirmed deleted nor confirmed surviving". The
                // aggregate flatly contradicted the rows underneath it. So the unknowns
                // are excluded from what Verify reconciles (same exclusion `residual`
                // makes, and for the same reason) and reported beside it instead: this
                // block is now about the rows whose fate the model actually settled.
                ["verification"] = JObject.FromObject(
                    HorizunGuard.Verify("deletions", attempted - unverifiable, deleted)),
                ["verification_scope"] = unverifiable == 0
                    ? (JToken)("All " + attempted + " attempted id(s) were re-resolved after the commit, so " +
                      "verification covers every one of them.")
                    : (JToken)("verification covers only the " + (attempted - unverifiable) + " attempted id(s) whose " +
                      "fate the model confirmed. " + unverifiable + " further id(s) were attempted but could not be " +
                      "re-resolved afterwards (see fate_unknown) and are EXCLUDED: verification.verified means the " +
                      "confirmed rows reconcile, NOT that this delete is fully accounted for."),

                ["results"] = new JObject
                {
                    ["total"] = targets.Count,
                    ["shown"] = rows.Count,
                    ["truncated"] = rows.Count < targets.Count,
                    ["items"] = rows
                },
                ["cascades"] = new JObject
                {
                    ["total"] = cascades.Count,
                    ["shown"] = casc.Count,
                    ["truncated"] = casc.Count < cascades.Count,
                    ["items"] = casc,
                    ["note"] = cascades.Count == 0
                        ? null
                        : cascades.Count + " element(s) you never named were taken along by the ones you did. " +
                          "Revit deletes dependents without asking; each row names the id that took it." +
                          (cascUnknown > 0
                              ? " " + cascUnknown + " of them have confirmed_gone: null — we could not re-resolve " +
                                "them, which means UNKNOWN, not surviving."
                              : "")
                },
                // The same {total, shown, truncated} envelope every other list gets. As a
                // bare array it was silently cut at id_cap: 500 failures shipped as 200
                // rows that a consumer iterates as if they were all of them.
                ["failed"] = new JObject
                {
                    ["total"] = failed,
                    ["shown"] = failures.Count,
                    ["truncated"] = failures.Count < failed,
                    ["items"] = failures
                },
                ["residual"] = new JObject
                {
                    ["total"] = residualTotal,
                    ["shown"] = residual.Count,
                    ["truncated"] = residual.Count < residualTotal,
                    ["items"] = residual,
                    ["note"] = residualTotal == 0
                        ? (fateUnknown == 0
                            ? null
                            : (JToken)("Nothing is CONFIRMED to have survived, but this is not a clean result: " +
                              fateUnknown + " element(s) have an unknown fate (see fate_unknown). Zero here means " +
                              "zero confirmed survivors, not zero survivors."))
                        : (JToken)(residualTotal + " element(s) are CONFIRMED to have survived — the model still " +
                          "resolves them. This CANNOT be reported as a clean result: the next audit will re-flag " +
                          "them, and a caller told 'done' will think the audit is broken." +
                          (fateUnknown == 0
                              ? ""
                              : " This total is a LOWER BOUND: a further " + fateUnknown + " element(s) have an " +
                                "unknown fate (see fate_unknown) and some of them may also still be in the model."))
                },
                // Its own block, not folded into `failed` or `residual`. A row in here is
                // an element this handler could not establish a fact about; both of the
                // blocks above make assertions ("failed", "survived") that such a row
                // cannot support, and quietly borrowing it to inflate them is the same
                // measurement-of-intent this file exists to refuse.
                ["fate_unknown"] = new JObject
                {
                    ["total"] = fateUnknown,
                    ["shown"] = unknowns.Count,
                    ["truncated"] = unknowns.Count < fateUnknown,
                    ["items"] = unknowns,
                    ["note"] = fateUnknown == 0
                        ? null
                        : (JToken)(fateUnknown + " element(s) ended in a state this handler could not measure: " +
                          unexamined + " never attempted (id unreadable) and " + unverifiable + " attempted but " +
                          "impossible to re-resolve after the commit. They are neither deleted nor survivors. " +
                          "deleted_total is a LOWER BOUND on what died and residual.total is a LOWER BOUND on what " +
                          "lived; this model has NOT been fully accounted for.")
                },
                ["rejected_input"] = rejected
            };

            o["warnings_dismissed"] = WarningsBlock(ledger, idCap);

            if (note != null) o["note"] = note;
            if (dryRun && o["note"] == null)
                o["note"] = "Nothing was deleted — the transaction was rolled back on purpose. The verdicts above are " +
                            "real: Revit was actually asked, so the cascades are the true dependent closure, not a guess.";
            return o;
        }

        /// <summary>
        /// Every warning the preprocessor took off the accessor, split by whether it was
        /// ours. It has to be in the response: dismissing a pre-existing warning lowers
        /// the count a later audit reads, and a purge credited for cleanup it never did
        /// is the same shape of lie as a purge credited for 758 deletions it never made.
        /// </summary>
        /// <remarks>
        /// The ledger records one entry per DISMISSAL, and PurgeUnused opens one
        /// transaction per pass, so Revit re-posts the same pre-existing warnings on
        /// every pass. `pre_existing_in_model` published that event tally under a name
        /// (and a note) that promise a count of warnings IN THE MODEL: four passes over
        /// 3 pre-existing warnings shipped `pre_existing_in_model: 12` beside 3 deduped
        /// descriptions, and no truncation flag to explain the gap. The two are different
        /// quantities, so both are reported under their own names — and the list gets the
        /// {total, shown, truncated} envelope every other list in Report() carries.
        /// </remarks>
        private static JObject WarningsBlock(WarningLedger ledger, int idCap)
        {
            if (ledger == null)
                return new JObject { ["counted"] = false, ["note"] = "Warnings were not tracked for this call." };

            // Distinct descriptions = distinct warnings. The raw Count is dismissal events.
            var foreignDistinct = ledger.Foreign.Distinct().ToList();
            var oursDistinct = ledger.Ours.Distinct().ToList();
            var foreign = new JArray(foreignDistinct.Take(idCap).Select(d => (JToken)d));

            return new JObject
            {
                ["caused_by_this_delete"] = oursDistinct.Count,
                ["caused_by_this_delete_dismissal_events"] = ledger.Ours.Count,
                ["pre_existing_in_model"] = foreignDistinct.Count,
                ["pre_existing_dismissal_events"] = ledger.Foreign.Count,
                ["counts_note"] = "*_in_model / caused_by_this_delete count DISTINCT warnings, which is what the " +
                                  "model's warning list holds. *_dismissal_events count times a warning was taken " +
                                  "off the accessor: purge runs one transaction per pass and Revit re-posts the same " +
                                  "warning every pass, so the event count is higher and is NOT a number of warnings.",
                ["pre_existing_descriptions"] = new JObject
                {
                    ["total"] = foreignDistinct.Count,
                    ["shown"] = foreign.Count,
                    ["truncated"] = foreign.Count < foreignDistinct.Count,
                    ["items"] = foreign
                },
                ["note"] = foreignDistinct.Count == 0
                    ? null
                    : foreignDistinct.Count + " distinct warning(s) that this delete did NOT cause were dismissed (" +
                      ledger.Foreign.Count + " dismissal event(s) across the passes) to stop Revit opening a modal, " +
                      "which would hang the bridge and read as a timeout. They are listed because they may now be " +
                      "absent from the model's warning list: if a later audit shows a lower warning count, this " +
                      "purge is not the reason it is cleaner."
            };
        }

        // ---- Counted from the model, before and after. Not from the loop. ----
        private class Census2
        {
            // Nullable, not a sentinel. A -1 here used to reach model_shrank_by's
            // subtraction and come out the other side as a hard number: a failed AFTER
            // census turned a 1500-element model into "model_shrank_by: 1502", a
            // fabricated measurement of a purge that may have deleted nothing.
            public int? Instances;
            public int? Types;
            public string Error;

            public int? Total
            {
                get
                {
                    if (!Instances.HasValue || !Types.HasValue) return null;
                    return Instances.Value + Types.Value;
                }
            }
        }

        private static Census2 Census(Document doc)
        {
            var c = new Census2();
            try { c.Instances = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(); }
            catch (Exception ex) { c.Instances = null; c.Error = "instance count failed: " + ex.Message; }
            try { c.Types = new FilteredElementCollector(doc).WhereElementIsElementType().GetElementCount(); }
            catch (Exception ex)
            {
                c.Types = null;
                c.Error = (c.Error == null ? "" : c.Error + "; ") + "type count failed: " + ex.Message;
            }
            return c;
        }

        private class Target
        {
            // Nullable: a candidate Revit offered whose id we could not even read has
            // no id to report, and reporting 0 for it would collide with a real id.
            public long? RawId;
            public ElementId Id;
            public bool IdUnreadable;
            public bool ExistedBefore;
            public string Name;
            public string Category;
            public string Verdict;      // null until the model says otherwise
            public string Reason;
            public long? CascadedBy;
        }

        private class Cascade
        {
            // Nullable for the same reason Target.RawId is: a collateral element whose id
            // Revit would not give us has no id to report, and 0 would collide with a real
            // one. It still gets a row — it is the blast radius we failed to measure, and
            // a delete that cannot measure its own collateral has to say so.
            public long? RawId;
            public bool IdUnreadable;
            public ElementId Id;
            public long ParentRawId;
            public bool? Gone;          // null = we could not re-resolve it; NOT "it survived"
            public string Note;
        }

        /// <summary>
        /// What the preprocessor dismissed. Every warning we take off the accessor is a
        /// change to the model's warning list, so it goes in the response: a pre-entrega
        /// audit run afterwards shows a lower warning count, and the purge must not be
        /// credited for cleanup it never did.
        /// </summary>
        private class WarningLedger
        {
            public readonly List<string> Ours = new List<string>();
            public readonly List<string> Foreign = new List<string>();
        }

        /// <summary>
        /// Deleting types trips Revit's failure UI ("elements will be deleted"). Left
        /// alone it opens a modal, and a modal on the bridge thread hangs it — which
        /// then reads as a timeout while Revit sits waiting. So every warning has to be
        /// dismissed; leaving a foreign one posted is not an option we have.
        ///
        /// What we CAN refuse is doing it invisibly. A warning is "ours" only when its
        /// failing elements intersect the ids we are actually deleting; everything else
        /// is a pre-existing model warning (duplicate Mark, room not enclosed) that got
        /// re-posted during our transaction. Both are recorded and both are reported —
        /// the foreign ones loudly, because dismissing them lowers the model's warning
        /// count for reasons that have nothing to do with this purge.
        /// </summary>
        private class SwallowNothingPreprocessor : IFailuresPreprocessor
        {
            private readonly HashSet<long> _owned;
            private readonly WarningLedger _ledger;

            public SwallowNothingPreprocessor(HashSet<long> owned, WarningLedger ledger)
            {
                _owned = owned;
                _ledger = ledger;
            }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (var f in a.GetFailureMessages())
                {
                    if (f.GetSeverity() != FailureSeverity.Warning) continue;

                    string desc;
                    try { desc = f.GetDescriptionText(); } catch { desc = "(description unreadable)"; }

                    if (_ledger != null)
                    {
                        if (CausedByUs(f)) _ledger.Ours.Add(desc);
                        else _ledger.Foreign.Add(desc);
                    }
                    a.DeleteWarning(f);
                }
                return FailureProcessingResult.Continue;
            }

            private bool CausedByUs(FailureMessageAccessor f)
            {
                if (_owned == null) return false;
                try
                {
                    foreach (var id in f.GetFailingElementIds())
                    {
                        long raw;
                        try { raw = RevitCompat.GetId(id); } catch { continue; }
                        if (_owned.Contains(raw)) return true;
                    }
                }
                catch { return false; }
                // Could not tie it to anything we touched. That is not proof it is
                // foreign, but it is the direction that over-reports rather than under.
                return false;
            }
        }

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafeName(Element e) { try { return e.Name; } catch { return null; } }
        private static string SafeCategory(Element e) { try { return e.Category != null ? e.Category.Name : null; } catch { return null; } }

        /// <summary>
        /// An id we cannot read is an error, not something to drop. A dropped id is
        /// how a caller believes it purged elements it never touched.
        /// </summary>
        /// <summary>
        /// Merge the per-field reject arrays into the one the response carries. Kept
        /// separate up to this point so no field's rejects can answer another field's
        /// "did the caller name anything" question.
        /// </summary>
        private static JArray Concat(params JArray[] parts)
        {
            var all = new JArray();
            foreach (var p in parts)
                if (p != null)
                    foreach (var tok in p) all.Add(tok);
            return all;
        }

        /// <param name="consequence">
        /// What the rejection MEANS for this field, in this field's own terms. One shared
        /// message used to serve both: "It was NOT deleted and NOT protected". For an
        /// `ids` entry that is true. For a `protect_ids` entry in purge mode it inverted
        /// the outcome — an unprotected element is precisely the one the purge deletes,
        /// so the note asserted "NOT deleted" about the element most likely to be gone.
        /// A rejection note must describe the field it came from, not the other one.
        /// </param>
        private static List<long> ReadIds(JArray arr, JArray rejected, string field, string consequence)
        {
            var ids = new List<long>();
            if (arr == null) return ids;
            foreach (var tok in arr)
            {
                if (tok.Type != JTokenType.Integer)
                {
                    rejected.Add(new JObject
                    {
                        ["field"] = field,
                        ["value"] = tok.ToString(),
                        ["error"] = "Not an integer element id. " + consequence
                    });
                    continue;
                }
                ids.Add(tok.Value<long>());
            }
            return ids;
        }
    }
}
