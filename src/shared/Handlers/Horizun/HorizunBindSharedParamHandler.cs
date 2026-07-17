// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// horizun_bind_shared_param — bind a shared parameter from an SPF, and PROVE the
// binding that actually ended up in the document.
//
// This is the precondition for the whole per-diameter coding mechanism
// (prodesa-codificar-omniclass: PRD_Nota_Clave as an InstanceBinding in
// PG_IDENTITY_DATA, before a single value is written) and it runs once per model
// across 33-model batches. Today it is one try/except with a `break` in it, and
// it can tell three separate lies — each of which has cost real work:
//
//   1. ReInsert with ONLY the new categories DROPS the categories that were
//      already bound, and their values go with them. The default here is to UNION
//      the existing bound categories with the new ones, and — this is the part
//      that matters — the response reports the categories READ BACK from
//      ParameterBindings, never the ones the caller asked for. Echoing the request
//      is precisely how the drop stays invisible: the report looks identical
//      whether ReInsert kept the other categories or threw them away.
//   2. Immediately after Insert/ReInsert the iterator's Key hands back the
//      ExternalDefinition, which has NO VariesAcrossGroups at all. The flag lives
//      on the InternalDefinition, reachable only through a real element's
//      parameter (pp.Definition) after doc.Regenerate() — which itself requires an
//      OPEN transaction. The existing code loops the collector and `break`s on the
//      first hit, so when NO element carries the parameter yet it silently sets
//      nothing and says nothing.
//   3. Without SetAllowVaryBetweenGroups(true), Revit raises the DESAGRUPAR modal
//      the first time two instances inside a Model Group get different values —
//      and a modal does not wait for a human here, it HANGS THE BRIDGE. This is a
//      standing rule in Pablo's notes, not a preference. The `except: pass` around
//      it today means it can fail while the script sails on, and the bill arrives
//      much later, mid-write, far from the cause.
//
// So the contract: after Regenerate + commit, re-read ParameterBindings
// .ForwardIterator() and report (a) whether the resulting binding really IS an
// InstanceBinding or a TypeBinding, from the object's own type, (b) the real
// resulting category list, and (c) VariesAcrossGroups off the InternalDefinition.
// If any of those three could not be MEASURED, this handler may not report
// success — it names which one and why. "I could not look" is its own outcome
// here (`unknown`), never a bool defaulting to false.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class HorizunBindSharedParamHandler : IRevitCommand
    {
        public string Name => "horizun_bind_shared_param";

        public string Description =>
            "Bind a shared parameter from an SPF (by GUID, or by name) to a set of categories as an Instance or " +
            "Type binding in a parameter group, then RE-READ the binding from ParameterBindings after the commit " +
            "and report what is actually there. merge_existing_categories (default true) UNIONs the already-bound " +
            "categories with the new ones: ReInsert with only the new ones drops the rest AND their values, and a " +
            "response that echoed the request could not show it — so the category list returned here is always the " +
            "one read back from the model, and categories_dropped is measured, not assumed. " +
            "allow_vary_between_groups (default true) calls SetAllowVaryBetweenGroups on the real InternalDefinition, " +
            "found through an element's parameter after Regenerate, because the iterator's Key is the " +
            "ExternalDefinition and has no such flag; without the flag Revit throws the DESAGRUPAR modal on the " +
            "first differing write inside a Model Group and hangs the bridge. If NO element carries the parameter " +
            "the flag cannot be set and that is REPORTED, not swallowed. The binding kind, the category list and " +
            "VariesAcrossGroups are three separate measurements: if any of them could not be taken, the outcome is " +
            "'unknown' and never 'confirmed'. A ReInsert that dropped previously-bound categories under " +
            "merge_existing_categories=true reports outcome 'categories_dropped', never 'confirmed' — with merge you " +
            "asked for the UNION, so a binding missing part of it is not what you asked for and the values in the " +
            "dropped categories are gone. The document's SharedParametersFilename is restored afterwards.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""spf_path"", ""categories""],
  ""properties"": {
    ""spf_path"": { ""type"": ""string"", ""description"": ""Absolute path to the shared parameter file. It is loaded via app.SharedParametersFilename + OpenSharedParameterFile(), and the previous filename is restored afterwards — silently repointing the user's SPF is its own bug."" },
    ""param_guid"": { ""type"": ""string"", ""description"": ""GUID of the shared parameter in the SPF. Preferred: a GUID identifies a shared parameter, a name does not."" },
    ""param_name"": { ""type"": ""string"", ""description"": ""Name of the shared parameter in the SPF. Used only if param_guid is absent. A name matching more than one definition is an error, not a guess."" },
    ""categories"": {
      ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"" },
      ""description"": ""BuiltInCategory tokens (OST_PipeFitting) or category display names. A category that does not resolve is an error: nothing is bound.""
    },
    ""binding_kind"": { ""type"": ""string"", ""enum"": [""Instance"", ""Type""], ""default"": ""Instance"",
                        ""description"": ""Instance | Type. What is reported is read off the resulting Binding object's own type, not from this field."" },
    ""group"": { ""type"": ""string"", ""default"": ""PG_IDENTITY_DATA"",
                 ""description"": ""Parameter group: a PG_ name (PG_IDENTITY_DATA, PG_DATA, PG_TEXT...) or a group schema id (autodesk.parameter.group:identityData)."" },
    ""merge_existing_categories"": { ""type"": ""boolean"", ""default"": true,
                                     ""description"": ""true: UNION the categories already bound with the requested ones. false: bind ONLY the requested ones — ReInsert then DROPS every other bound category and LOSES the values stored in them. Leave it true unless you mean exactly that."" },
    ""allow_vary_between_groups"": { ""type"": ""boolean"", ""default"": true,
                                     ""description"": ""Call SetAllowVaryBetweenGroups(true) on the InternalDefinition. Without it, writing different values to instances inside Model Groups raises the DESAGRUPAR modal, which hangs the bridge. Reported as read back from the InternalDefinition, never as assumed."" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: bind shared parameter"" },
    ""target_document_title"": { ""type"": ""string"",
                                 ""description"": ""If given, the bind aborts unless the active document's title matches. Binding into whichever model happened to be in front is how a batch lands in the wrong file."" }
  }
}";

        // The three-way split. `unknown` is not a polite name for `not_bound`: one says
        // the model does not carry the binding, the other says we could not look. Summing
        // them is how "I could not look" gets published as "it is absent".
        private const string OUT_CONFIRMED = "confirmed";
        private const string OUT_NOT_BOUND = "not_bound";
        private const string OUT_UNKNOWN = "unknown";

        // A ReInsert that kept your categories but threw OTHERS away is neither
        // `confirmed` (with merge=true you asked for the UNION, and this is not it) nor
        // `not_bound` (the parameter IS bound). It gets its own name so that a batch
        // gating on outcome=="confirmed" stops on the call that deleted the values, and
        // so that `not_bound` keeps meaning what it says.
        private const string OUT_DROPPED = "categories_dropped";

        public CommandResult Execute(UIApplication uiapp, string paramsJson)
        {
            var doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            var app = uiapp.Application;

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var wantTitle = request.Value<string>("target_document_title");
            if (!string.IsNullOrEmpty(wantTitle))
            {
                var haveTitle = SafeTitle(doc);
                if (!string.Equals(haveTitle, wantTitle, StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail(
                        "Active document is '" + (haveTitle ?? "(title unreadable)") + "', not '" + wantTitle +
                        "'. Nothing was bound. Activate the intended document, or drop target_document_title if you " +
                        "really mean whatever is in front.");
            }

            var spfPath = request.Value<string>("spf_path");
            if (string.IsNullOrWhiteSpace(spfPath)) return CommandResult.Fail("spf_path is required.");
            if (!Path.IsPathRooted(spfPath)) return CommandResult.Fail("spf_path must be an absolute path.");
            if (!File.Exists(spfPath)) return CommandResult.Fail("The shared parameter file does not exist: " + spfPath);

            var guidText = request.Value<string>("param_guid");
            var paramName = request.Value<string>("param_name");
            Guid wantGuid = Guid.Empty;
            bool byGuid = !string.IsNullOrWhiteSpace(guidText);
            if (byGuid && !Guid.TryParse(guidText.Trim(), out wantGuid))
                return CommandResult.Fail("param_guid '" + guidText + "' is not a GUID.");
            if (!byGuid && string.IsNullOrWhiteSpace(paramName))
                return CommandResult.Fail("Either param_guid or param_name is required.");

            var catTokens = request["categories"] as JArray;
            if (catTokens == null || catTokens.Count == 0)
                return CommandResult.Fail("categories is required and must be a non-empty array.");

            var kind = request.Value<string>("binding_kind");
            if (string.IsNullOrWhiteSpace(kind)) kind = "Instance";
            bool wantType;
            if (string.Equals(kind, "Type", StringComparison.OrdinalIgnoreCase)) wantType = true;
            else if (string.Equals(kind, "Instance", StringComparison.OrdinalIgnoreCase)) wantType = false;
            else return CommandResult.Fail("binding_kind must be 'Instance' or 'Type'.");
            kind = wantType ? "Type" : "Instance";

            var groupSpec = request.Value<string>("group");
            if (string.IsNullOrWhiteSpace(groupSpec)) groupSpec = "PG_IDENTITY_DATA";
            string groupWhy;
            ForgeTypeId groupId = ResolveGroup(groupSpec, out groupWhy);
            if (groupId == null) return CommandResult.Fail(groupWhy);

            bool merge = request["merge_existing_categories"] == null || request.Value<bool>("merge_existing_categories");
            bool allowVary = request["allow_vary_between_groups"] == null || request.Value<bool>("allow_vary_between_groups");
            var txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: bind shared parameter";

            // ---- Load the SPF, and put the user's SharedParametersFilename back. ----
            // Leaving app.SharedParametersFilename pointing at our SPF silently repoints
            // every subsequent "Add shared parameter" dialog in the user's session at a
            // file they never chose. Restoring it is part of the job, and a restore that
            // FAILS is reported rather than swallowed — the user's session is now wrong.
            string previousSpf = null;
            bool previousSpfReadable = true;
            string previousSpfError = null;
            try { previousSpf = app.SharedParametersFilename; }
            catch (Exception ex)
            {
                previousSpfReadable = false;
                previousSpf = null;
                previousSpfError = ex.Message;
            }

            DefinitionFile defFile = null;
            string spfError = null;
            string restoreError = null;
            try
            {
                app.SharedParametersFilename = spfPath;
                defFile = app.OpenSharedParameterFile();
            }
            catch (Exception ex) { spfError = ex.Message; }
            finally
            {
                // Only restore what we could read. Writing back a null we never read would
                // CLEAR the setting rather than restore it.
                if (previousSpfReadable)
                {
                    try { app.SharedParametersFilename = previousSpf; }
                    catch (Exception ex) { restoreError = ex.Message; }
                }
                else
                {
                    restoreError = "The previous SharedParametersFilename could not be read before it was changed" +
                                   (previousSpfError == null ? "" : " (" + previousSpfError + ")") +
                                   ", so it could not be restored: this Revit session now points at '" + spfPath +
                                   "'. Set it back in Manage > Shared Parameters if that matters to you.";
                }
            }

            if (defFile == null)
                return CommandResult.Fail("Could not open the shared parameter file '" + spfPath + "'" +
                                          (spfError == null ? "" : ": " + spfError) +
                                          ". Nothing was bound.");

            ExternalDefinition extDef;
            string defWhy;
            if (!FindDefinition(defFile, byGuid, wantGuid, paramName, out extDef, out defWhy))
                return CommandResult.Fail(defWhy);

            string defName = SafeDefName(extDef);
            Guid defGuid;
            try { defGuid = extDef.GUID; }
            catch (Exception ex)
            {
                // Every measurement below identifies the parameter by GUID. Without it we
                // would be binding one parameter and reporting on whatever else matched.
                return CommandResult.Fail("The shared parameter '" + (defName ?? "(name unreadable)") +
                                          "' was found in the SPF but its GUID could not be read: " + ex.Message +
                                          ". Nothing was bound — nothing here could be verified afterwards.");
            }

            // ---- Resolve categories. One that does not resolve aborts the whole call. ----
            var requested = new List<Category>();
            foreach (var t in catTokens)
            {
                var token = t == null ? null : t.ToString();
                var c = ResolveCategory(doc, token);
                if (c == null)
                    return CommandResult.Fail("Category '" + token + "' does not resolve in this document. Nothing " +
                                              "was bound. Skipping it would bind a set you never asked for and report " +
                                              "it as yours.");
                requested.Add(c);
            }
            var requestedIds = new HashSet<long>(requested.Select(c => RevitCompat.GetId(c.Id)));

            // ---- What is bound BEFORE. This is the baseline the ReInsert drop is measured against. ----
            var before = MeasureBinding(doc, defGuid, defName);
            var beforeCats = CategoryIdsOf(before);

            // ---- Build the category set that will actually be inserted. ----
            var finalCats = new Dictionary<long, Category>();
            foreach (var c in requested) finalCats[RevitCompat.GetId(c.Id)] = c;

            var mergedIn = new List<string>();
            if (merge && before.Cats != null)
            {
                foreach (var c in before.Cats)
                {
                    long id = RevitCompat.GetId(c.Id);
                    if (finalCats.ContainsKey(id)) continue;
                    finalCats[id] = c;
                    mergedIn.Add(SafeCatName(c));
                }
            }

            // merge=true with a binding we could not READ is not a merge — it is a
            // ReInsert with only the new categories, i.e. exactly the drop this tool
            // exists to prevent, run blind. Refuse rather than perform it.
            if (merge && before.Exists == true && !before.CatsMeasured)
                return CommandResult.Fail(
                    "'" + (defName ?? guidText) + "' is already bound in this document, but its current categories " +
                    "could not be read (" + (before.CatsError ?? "reason unrecorded") + "), so they cannot be merged. " +
                    "Nothing was bound. ReInserting now would keep ONLY the categories you passed and DROP the rest " +
                    "along with the values stored in them — and this handler cannot tell you which ones those were. " +
                    "Pass merge_existing_categories=false only if losing them is what you mean.");
            if (before.Exists == null)
                return CommandResult.Fail(
                    "Could not read this document's ParameterBindings to find out whether '" + (defName ?? guidText) +
                    "' is already bound: " + (before.ExistsError ?? "reason unrecorded") + ". Nothing was bound. " +
                    "Insert and ReInsert are different operations with different consequences, and choosing between " +
                    "them by guess is how the existing categories get dropped.");

            CategorySet catSet;
            try
            {
                catSet = app.Create.NewCategorySet();
                foreach (var c in finalCats.Values) catSet.Insert(c);
            }
            catch (Exception ex) { return CommandResult.Fail("Could not build the category set: " + ex.Message + ". Nothing was bound."); }

            // ---- Write. ----
            string op = before.Exists == true ? "reinsert" : "insert";
            bool apiReturned = false;
            bool committed = false;
            string txStatus;
            var probe = new DefProbe();
            string varySetError = null;
            bool varySetAttempted = false;

            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    // A modal here does not wait for anyone: it blocks the bridge until the
                    // caller times out, on a document with an open transaction.
                    var opts = tx.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new SilenceModals());
                    opts.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(opts);

                    Binding binding = wantType
                        ? (Binding)app.Create.NewTypeBinding(catSet)
                        : (Binding)app.Create.NewInstanceBinding(catSet);

                    // ReInsert against the key the map already holds, not against our
                    // ExternalDefinition: the bound key is an InternalDefinition and the
                    // map matches on identity.
                    Definition key = before.Key ?? (Definition)extDef;

                    apiReturned = before.Exists == true
                        ? doc.ParameterBindings.ReInsert(key, binding, groupId)
                        : doc.ParameterBindings.Insert(extDef, binding, groupId);

                    if (!apiReturned)
                    {
                        tx.RollBack();
                        return CommandResult.Fail(
                            "ParameterBindings." + (op == "reinsert" ? "ReInsert" : "Insert") + " returned FALSE " +
                            "without throwing: Revit declined to bind '" + (defName ?? guidText) + "'. Nothing was " +
                            "written and the transaction was rolled back. A binding kind change on a parameter that " +
                            "already has values, or a category that cannot carry this parameter's data type, are the " +
                            "usual reasons.");
                    }

                    // The parameter does not exist on any element until the model regenerates,
                    // so the InternalDefinition cannot be reached before this line — and
                    // Regenerate itself needs this transaction to be open.
                    doc.Regenerate();

                    // The iterator's Key is the ExternalDefinition right now and has no
                    // VariesAcrossGroups; the flag lives on the InternalDefinition, and the
                    // only handle on it is a real element's parameter.
                    probe = ProbeInternalDefinition(doc, defGuid, wantType, new HashSet<long>(finalCats.Keys));

                    if (allowVary && probe.Def != null)
                    {
                        varySetAttempted = true;
                        try
                        {
                            if (!probe.Def.VariesAcrossGroups)
                                probe.Def.SetAllowVaryBetweenGroups(doc, true);
                        }
                        catch (Exception ex)
                        {
                            // NOT swallowed. Failing here means the DESAGRUPAR modal is still
                            // armed, and it will fire mid-write in some later batch and hang
                            // the bridge with a transaction open — a long way from this line.
                            varySetError = ex.Message;
                        }
                    }

                    HorizunGuard.Commit(tx, txName);
                    txStatus = "Committed";
                    committed = true;
                }
                catch (HorizunSilentRollbackException ex)
                {
                    // Revit rolled back and returned a status instead of throwing. Nothing
                    // above this line describes the document any more.
                    //
                    // But a rollback UNDOES this call — it does not unbind the parameter. On a
                    // reinsert (before.Exists == true) the PREVIOUS binding survives with its
                    // old categories, so 'not_bound' would be a claim of absence about a
                    // parameter the model still carries, made without a read. Only the insert
                    // case can say 'not_bound': there was nothing before, and this call added
                    // nothing.
                    bool wasBoundBefore = before.Exists == true;
                    return CommandResult.Ok(new JObject
                    {
                        ["outcome"] = wasBoundBefore ? OUT_UNKNOWN : OUT_NOT_BOUND,
                        ["outcome_means"] = wasBoundBefore
                            ? "unknown: this call bound NOTHING (Revit rolled the transaction back), but '" +
                              (defName ?? guidText) + "' was already bound in this document before it, and a " +
                              "rollback restores that previous binding rather than removing it. The document was " +
                              "not re-read after the rollback, so what it carries now is unmeasured — it is NOT " +
                              "absent. Do not record this parameter as missing, and do not re-run an Insert on the " +
                              "strength of this result."
                            : "not_bound: nothing was bound before this call and Revit rolled this call back, so " +
                              "the model does not carry the parameter.",
                        ["was_bound_before_this_call"] = before.Exists,
                        ["transaction_status"] = "RolledBack",
                        ["transaction_name"] = txName,
                        ["document"] = SafeTitle(doc),
                        ["parameter_name"] = defName,
                        ["parameter_guid"] = defGuid.ToString("d"),
                        ["spf_path"] = spfPath,
                        ["spf_filename_restored"] = restoreError == null,
                        ["spf_filename_restore_error"] = restoreError,
                        ["operation"] = op,
                        ["note"] = ex.Message + " Nothing is bound BY THIS CALL, whatever the API return said." +
                                   (wasBoundBefore
                                       ? " That is not the same as the parameter being unbound: it was already bound " +
                                         "in this document before this call, and the rollback leaves that earlier " +
                                         "binding — with its earlier category set, unchanged by this call — in " +
                                         "place. Nothing here re-read the document after the rollback, so this " +
                                         "handler is not telling you what it covers now; read it back before you " +
                                         "act on it."
                                       : " '" + (defName ?? guidText) + "' was not bound in this document before " +
                                         "this call either, so it is not bound now.")
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Binding failed and was rolled back; nothing was written: " + ex.Message);
                }
            }

            // ---- The only evidence that counts: read the model back, after the commit. ----
            var after = MeasureBinding(doc, defGuid, defName);

            // VariesAcrossGroups re-read from a FRESH probe. Reading probe.Def's property
            // again would be reading the handle we just wrote through, inside a transaction
            // that has since closed — our own intent, not the document.
            //
            // The scan filter is the categories read back — but that list is a LOWER BOUND
            // when any of them could not be read, and a carrier sitting in one of the
            // unreadable categories would be filtered out, turning "we did not look there"
            // into "no element carries this parameter". Union in the set we inserted so the
            // filter cannot be narrower than what we know is bound.
            var probeCats = CategoryIdsOf(after);
            if (probeCats == null) probeCats = new HashSet<long>(finalCats.Keys);
            else if (after.CatsUnreadable > 0)
                foreach (var id in finalCats.Keys) probeCats.Add(id);
            var afterProbe = ProbeInternalDefinition(doc, defGuid, wantType, probeCats);
            bool? variesAfter = null;
            string variesError = null;
            if (afterProbe.Def == null)
            {
                variesError = afterProbe.NoCarrierReason();
            }
            else
            {
                try { variesAfter = afterProbe.Def.VariesAcrossGroups; }
                catch (Exception ex) { variesError = "The InternalDefinition was found but VariesAcrossGroups could not be read: " + ex.Message; }
            }

            // Measured: which of the previously-bound categories are gone. This is the
            // ReInsert drop, and it is the reason the response never echoes the request.
            //
            // An unreadable category on EITHER side poisons this diff: CatsMeasured is
            // still true after ReadBinding counted categories it could not read, so both
            // lists are LOWER BOUNDS. Diffing lower bounds manufactures drops — a
            // still-bound-but-unnameable category would be reported as deleted, with its
            // values declared gone. CatsBlock already publishes count_is_lower_bound and
            // says not to diff it; this is the consumer that must obey that.
            JArray dropped = null;
            bool dropMeasurable = before.CatsMeasured && after.CatsMeasured
                                  && before.CatsUnreadable == 0 && after.CatsUnreadable == 0;
            if (dropMeasurable)
            {
                var afterIds = CategoryIdsOf(after);
                dropped = new JArray(before.Cats
                    .Where(c => !afterIds.Contains(RevitCompat.GetId(c.Id)))
                    .Select(c => (JToken)new JObject { ["id"] = RevitCompat.GetId(c.Id), ["name"] = SafeCatName(c) }));
            }

            // Tri-state, because "no drop" and "could not tell whether anything dropped"
            // are different answers and only one of them may be reported as success.
            bool? noUnintendedDrop;
            if (!merge) noUnintendedDrop = true;                 // a drop here is the drop you asked for.
            else if (before.Exists != true) noUnintendedDrop = true; // nothing was bound before: nothing to drop.
            else if (!dropMeasurable) noUnintendedDrop = null;   // unread, not clean.
            else noUnintendedDrop = dropped.Count == 0;

            bool kindMatches = after.KindMeasured && after.IsType == wantType;
            bool catsComplete = after.CatsMeasured && requestedIds.All(id => CategoryIdsOf(after).Contains(id));
            bool varyOk = variesAfter.HasValue && (!allowVary || variesAfter.Value);

            string outcome = Classify(committed, after, kindMatches, catsComplete, noUnintendedDrop,
                                      variesAfter, allowVary);

            var result = new JObject
            {
                ["outcome"] = outcome,
                ["outcome_means"] = "confirmed: the binding was re-read from ParameterBindings after the commit and " +
                                    "its kind, its categories and VariesAcrossGroups were all MEASURED and are what " +
                                    "you asked for, and no previously-bound category was dropped. not_bound: the " +
                                    "model was read and does not carry it. categories_dropped: it IS bound and covers " +
                                    "what you requested, but the ReInsert threw away categories that were bound " +
                                    "before, together with every value this parameter held in them — see " +
                                    "categories_dropped. That is not what merge_existing_categories=true asked for; " +
                                    "do not treat it as done. unknown: one of the measurements could not be taken — " +
                                    "that is not the same as absent and not the same as present.",
                ["transaction_status"] = txStatus,
                ["transaction_name"] = txName,
                ["document"] = SafeTitle(doc),
                ["parameter_name"] = defName,
                ["parameter_guid"] = defGuid.ToString("d"),
                ["spf_path"] = spfPath,
                ["spf_filename_restored"] = restoreError == null,
                ["spf_filename_restore_error"] = restoreError,
                ["operation"] = op,
                ["api_returned"] = apiReturned,
                ["merge_existing_categories"] = merge,
                ["requested_binding_kind"] = kind,
                ["requested_group"] = groupSpec,
                ["requested_categories"] = new JArray(requested.Select(c => (JToken)new JObject
                {
                    ["id"] = RevitCompat.GetId(c.Id),
                    ["name"] = SafeCatName(c)
                })),
                ["requested_categories_note"] = "This is what you ASKED for. It is echoed here for your diff and is " +
                                                "NOT evidence of anything. resulting_categories is the measurement.",
                // (a) — from the Binding object's own type, never from binding_kind.
                ["resulting_binding_kind"] = KindBlock(after, wantType),
                // (b) — read back from ParameterBindings, the drop included.
                ["resulting_categories"] = CatsBlock(after),
                ["categories_added_by_merge"] = merge ? new JArray(mergedIn.Select(n => (JToken)n)) : null,
                ["categories_dropped"] = dropped,
                ["categories_dropped_note"] = DroppedNote(dropMeasurable, dropped, merge, before, after),
                // (c) — off the InternalDefinition, or an explicit reason why not.
                ["varies_across_groups"] = VaryBlock(allowVary, varySetAttempted, varySetError, variesAfter,
                                                     variesError, afterProbe, wantType),
                ["parameter_group"] = GroupBlock(afterProbe.Def, groupSpec),
                ["note"] = Note(outcome, committed, after, kindMatches, catsComplete, noUnintendedDrop, dropped,
                                varyOk, allowVary, variesAfter, variesError, varySetError, afterProbe, defName,
                                kind, restoreError)
            };

            return CommandResult.Ok(result);
        }

        // ---- Classification. -----------------------------------------------------
        private static string Classify(bool committed, BindingState after, bool kindMatches, bool catsComplete,
                                       bool? noUnintendedDrop, bool? varies, bool allowVary)
        {
            // A rollback is the only claim of absence here that does not rest on a read:
            // Revit undid the transaction, so nothing from this call reached the model.
            if (!committed) return OUT_NOT_BOUND;

            // Could not even establish whether a binding exists.
            if (after.Exists == null) return OUT_UNKNOWN;
            if (after.Exists == false) return OUT_NOT_BOUND;

            if (!after.KindMeasured) return OUT_UNKNOWN;
            if (!kindMatches) return OUT_NOT_BOUND;      // it is bound — as the OTHER kind.

            if (!after.CatsMeasured) return OUT_UNKNOWN;
            // A category missing from a list that is admittedly a LOWER BOUND is not a
            // category that is absent — it may be one of the ones we could not read. Only
            // a clean read can carry a claim of absence.
            if (!catsComplete && after.CatsUnreadable > 0) return OUT_UNKNOWN;
            if (!catsComplete) return OUT_NOT_BOUND;     // read back, and your categories are not in it.

            // Your categories are in it — but with merge=true you asked for the UNION, so a
            // binding missing OTHER categories that were there before is not what you asked
            // for either, and the values in them are gone. The subset test above passes that
            // case trivially, which is exactly how the drop stayed invisible.
            if (!noUnintendedDrop.HasValue) return OUT_UNKNOWN;
            if (!noUnintendedDrop.Value) return OUT_DROPPED;

            // The flag we could not read is not the flag that is off. One of those means
            // the desagrupar modal is armed; the other means we do not know if it is.
            if (!varies.HasValue) return OUT_UNKNOWN;
            if (allowVary && !varies.Value) return OUT_NOT_BOUND;

            return OUT_CONFIRMED;
        }

        // ---- The three measurements, each of which may report "I could not look". ----
        private static JObject KindBlock(BindingState s, bool wantType)
        {
            if (!s.KindMeasured)
                return new JObject
                {
                    ["measured"] = false,
                    ["error"] = s.KindError ?? s.ExistsError ??
                                "The binding could not be re-read from ParameterBindings, so its kind is unknown.",
                    ["note"] = "This is NOT 'the binding is the wrong kind' and NOT 'the binding is missing'. It is " +
                               "unread. Do not conclude either."
                };
            return new JObject
            {
                ["measured"] = true,
                ["value"] = s.IsType == true ? "Type" : "Instance",
                ["matches_request"] = s.IsType == wantType,
                ["read_from"] = "the type of the Binding object returned by ParameterBindings.ForwardIterator() " +
                                "after the commit (InstanceBinding / TypeBinding), not from the request."
            };
        }

        private static JObject CatsBlock(BindingState s)
        {
            if (!s.CatsMeasured)
                return new JObject
                {
                    ["measured"] = false,
                    ["error"] = s.CatsError ?? s.ExistsError ??
                                "The bound category list could not be read back from ParameterBindings.",
                    ["note"] = "The categories you passed are NOT reported here in their place. An echo of the " +
                               "request reads identically whether ReInsert kept the other categories or dropped " +
                               "them with their values, which is the whole failure this tool exists to surface."
                };
            var o = new JObject
            {
                ["measured"] = true,
                ["count"] = s.Cats.Count,
                ["categories"] = new JArray(s.Cats
                    .Select(c => new { id = RevitCompat.GetId(c.Id), name = SafeCatName(c) })
                    .OrderBy(c => c.name, StringComparer.Ordinal)
                    .Select(c => (JToken)new JObject { ["id"] = c.id, ["name"] = c.name })),
                ["read_from"] = "ElementBinding.Categories, re-read from ParameterBindings after the commit. This is " +
                                "what the model holds — not what was requested."
            };
            if (s.CatsUnreadable > 0)
            {
                // A category we could not name is still bound. Dropping it from the list in
                // silence would understate the binding and could hide a drop.
                o["categories_unreadable"] = s.CatsUnreadable;
                o["count_is_lower_bound"] = true;
                o["note"] = s.CatsUnreadable + " bound categor(y/ies) could not be read" +
                            (s.CatsUnreadableError == null ? "" : " (first failure: " + s.CatsUnreadableError + ")") +
                            ", so this list is INCOMPLETE: the binding covers at least these, possibly more. Do not " +
                            "diff it against the request and conclude anything about what was dropped.";
            }
            return o;
        }

        private static JObject VaryBlock(bool allowVary, bool attempted, string setError, bool? value,
                                         string readError, DefProbe probe, bool wantType)
        {
            var o = new JObject
            {
                ["requested"] = allowVary,
                ["set_attempted"] = attempted,
                ["set_error"] = setError
            };

            if (!value.HasValue)
            {
                o["measured"] = false;
                o["error"] = readError ?? "VariesAcrossGroups could not be read.";

                if (probe.Def != null)
                {
                    // A carrier WAS found — only the property read failed. Telling the user
                    // the flag is unset, that nothing carries the parameter, and to go place
                    // an element that already exists are three claims this branch's own
                    // control flow contradicts. The flag may be TRUE right now.
                    o["note"] = "The InternalDefinition WAS reached, through element " +
                                (probe.CarrierId.HasValue ? probe.CarrierId.Value.ToString() : "(id unreadable)") +
                                ", but VariesAcrossGroups could not be READ off it" +
                                (readError == null ? "" : " (" + readError + ")") +
                                ". Its state is UNKNOWN: the flag may be set or unset, and this handler cannot tell " +
                                "you which. So whether the DESAGRUPAR modal is armed is also unknown. Do not start " +
                                "writing values until this reads true.";
                    return o;
                }

                // No carrier: the state the old `break`-on-first-hit loop reached in silence.
                o["note"] = "The flag is UNSET and, as far as this document can tell, will stay unset until an " +
                            "element carries this parameter: the flag lives on the InternalDefinition, and the only " +
                            "way to reach it is through a real element's parameter after Regenerate — the binding " +
                            "iterator hands back the ExternalDefinition, which does not have it. The consequence is " +
                            "concrete: the DESAGRUPAR modal will appear on the first write of differing values to " +
                            "instances inside a Model Group, and that modal HANGS THE SESSION — nobody is looking at " +
                            "Revit to dismiss it. Place or load at least one " + (wantType ? "type" : "element") +
                            " of the bound categories and run this tool again BEFORE writing any values." +
                            (probe.Scanned > 0
                                ? " " + probe.Scanned + " element(s) of the bound categories were scanned and none " +
                                  "carried the parameter."
                                : " No element of the bound categories was found in this document.");
                return o;
            }

            o["measured"] = true;
            o["value"] = value.Value;
            o["read_from"] = "InternalDefinition.VariesAcrossGroups, reached through element " +
                             (probe.CarrierId.HasValue ? probe.CarrierId.Value.ToString() : "(id unreadable)") +
                             "'s parameter after the commit — not from the ExternalDefinition, which does not " +
                             "expose it.";
            if (allowVary && !value.Value)
                o["note"] = "SetAllowVaryBetweenGroups was requested and the flag reads FALSE after the commit" +
                            (setError == null ? "" : " (the call failed: " + setError + ")") +
                            ". The DESAGRUPAR modal is armed: the first write of differing values to instances " +
                            "inside a Model Group will raise it and hang the bridge. Do not start writing values.";
            return o;
        }

        /// <summary>
        /// The group the binding actually landed in. Not one of the three the contract turns
        /// on, but the same rule applies: measured off the InternalDefinition, or reported as
        /// unmeasured — never echoed back from the request as though it had been checked.
        /// </summary>
        private static JObject GroupBlock(InternalDefinition def, string requested)
        {
            var o = new JObject { ["requested"] = requested };
            if (def == null)
            {
                o["measured"] = false;
                o["error"] = "No InternalDefinition was reachable (no element carries this parameter), so the group " +
                             "it was filed under could not be read back.";
                return o;
            }
            try
            {
                // Reflected because the API that exposes this moved across the versions this
                // assembly must build for; a hard reference would break one of the two.
                var mi = typeof(Definition).GetMethod("GetGroupTypeId", Type.EmptyTypes);
                if (mi == null)
                {
                    o["measured"] = false;
                    o["error"] = "This Revit API version does not expose Definition.GetGroupTypeId(), so the " +
                                 "resulting group could not be read back.";
                    return o;
                }
                var ftid = mi.Invoke(def, null) as ForgeTypeId;
                if (ftid == null)
                {
                    o["measured"] = false;
                    o["error"] = "Definition.GetGroupTypeId() returned nothing readable.";
                    return o;
                }
                o["measured"] = true;
                o["value"] = ftid.TypeId;
                return o;
            }
            catch (Exception ex)
            {
                o["measured"] = false;
                o["error"] = "Could not read the resulting group: " + ex.Message;
                return o;
            }
        }

        private static string DroppedNote(bool measurable, JArray dropped, bool merge, BindingState before,
                                          BindingState after)
        {
            // Nothing was bound before this call, so no previously-bound category can have
            // been dropped. Saying "could not be measured" about a drop that cannot exist
            // sends the user to check a binding for damage that was never possible.
            if (before.Exists == false) return null;

            if (!measurable)
            {
                string why;
                if (before.CatsUnreadable > 0 || after.CatsUnreadable > 0)
                {
                    // The two lists are lower bounds, so their difference is not a drop —
                    // it is the reading holes. Naming a category here would be inventing
                    // a deletion and telling the user its values are gone.
                    var firstError = before.CatsUnreadableError ?? after.CatsUnreadableError;
                    why = (before.CatsUnreadable + after.CatsUnreadable) + " bound categor(y/ies) could not be read" +
                          (firstError == null ? "" : " (first failure: " + firstError + ")");
                }
                else
                {
                    why = before.CatsError ?? after.CatsError ?? "reason unrecorded";
                }
                return "Whether any previously-bound category was DROPPED could not be measured (" + why +
                       "). The before/after category lists are INCOMPLETE, and their difference would be the gaps in " +
                       "the reading, not a drop — no category is named as dropped here, because a category we could " +
                       "not read may well still be bound. A real drop takes the values stored in those categories " +
                       "with it, so this is not a clean result: check the binding in Manage > Project Parameters " +
                       "before you rely on it.";
            }
            if (dropped.Count == 0) return null;
            return dropped.Count + " categor(y/ies) that WERE bound before this call are NOT in the binding now, " +
                   "measured by re-reading ParameterBindings. Every value this parameter held on elements of those " +
                   "categories is gone with the binding" +
                   (merge
                       ? " — and merge_existing_categories was TRUE, so this is not what you asked for. Revit " +
                         "dropped them anyway; do not treat this call as done."
                       : " — merge_existing_categories was FALSE, so this is the drop you asked for.");
        }

        private static string Note(string outcome, bool committed, BindingState after, bool kindMatches,
                                   bool catsComplete, bool? noUnintendedDrop, JArray dropped, bool varyOk,
                                   bool allowVary, bool? varies, string variesError, string varySetError,
                                   DefProbe probe, string defName, string kind, string restoreError)
        {
            var parts = new List<string>();
            string who = "'" + (defName ?? "the parameter") + "'";
            int dropCount = dropped == null ? 0 : dropped.Count;

            if (outcome == OUT_CONFIRMED)
            {
                parts.Add(who + " is bound as a " + kind + "Binding: the kind, the resulting categories and " +
                          "VariesAcrossGroups were each re-read from the model after the commit, and no " +
                          "previously-bound category was dropped.");
                // Reachable only with merge=false, where the drop is the request. Said out
                // loud so that a `confirmed` never sits silently on top of deleted values.
                if (dropCount > 0)
                    parts.Add(dropCount + " previously-bound categor(y/ies) were dropped, as " +
                              "merge_existing_categories=false asked: every value this parameter held in them is " +
                              "gone. See categories_dropped.");
            }
            else if (!committed)
            {
                parts.Add("The transaction did not commit, so nothing was bound.");
            }
            else
            {
                parts.Add("The transaction COMMITTED, but this call is NOT reported as successful, because:");
                if (after.Exists == null)
                    parts.Add("ParameterBindings could not be read back (" + (after.ExistsError ?? "reason unrecorded") +
                              "), so whether " + who + " is bound is UNKNOWN — not absent, not present.");
                else if (after.Exists == false)
                    parts.Add(who + " was re-read from ParameterBindings and is NOT bound in this document.");
                else
                {
                    if (!after.KindMeasured)
                        parts.Add("the binding kind could not be measured (" +
                                  (after.KindError ?? "reason unrecorded") + "), so whether it is an Instance or a " +
                                  "Type binding is unknown.");
                    else if (!kindMatches)
                        parts.Add("the binding is a " + (after.IsType == true ? "Type" : "Instance") +
                                  "Binding, and you asked for " + kind + ". This is what the model holds.");

                    if (!after.CatsMeasured)
                        parts.Add("the resulting category list could not be read back (" +
                                  (after.CatsError ?? "reason unrecorded") + "), so it is unknown which categories " +
                                  "the binding actually covers — including whether any were dropped.");
                    else if (!catsComplete && after.CatsUnreadable > 0)
                        // Absence claimed off a list this same response labels a lower bound
                        // would be the unknown-vs-not_bound conflation, in prose.
                        parts.Add(after.CatsUnreadable + " of the bound categories could not be read, so the " +
                                  "re-read list is INCOMPLETE and it is UNKNOWN whether the binding covers every " +
                                  "category you requested: the ones missing from resulting_categories may be among " +
                                  "the unreadable ones. This is not evidence that they are unbound.");
                    else if (!catsComplete)
                        parts.Add("the binding was re-read and does NOT cover every category you requested; see " +
                                  "resulting_categories for the ones it does.");

                    if (!noUnintendedDrop.HasValue)
                        parts.Add("whether this ReInsert dropped any previously-bound category could not be " +
                                  "measured; see categories_dropped_note.");
                    else if (!noUnintendedDrop.Value)
                        parts.Add(dropCount + " categor(y/ies) that WERE bound before this call are NOT in the " +
                                  "binding now, and merge_existing_categories was TRUE — you asked for the UNION, " +
                                  "so this is not what you asked for. Every value this parameter held on elements " +
                                  "of those categories is gone with the binding. See categories_dropped; do not " +
                                  "treat this call as done.");

                    if (!varies.HasValue)
                        parts.Add("VariesAcrossGroups could not be measured (" +
                                  (variesError ?? "reason unrecorded") + "), so whether the DESAGRUPAR modal is " +
                                  "armed is UNKNOWN — the flag may be set or unset" +
                                  (probe != null && probe.Def != null
                                      ? "; the InternalDefinition was reached, only the property read failed"
                                      : "") +
                                  ". Do not start writing values until varies_across_groups reads true. See " +
                                  "varies_across_groups.");
                    else if (allowVary && !varies.Value)
                        parts.Add("VariesAcrossGroups reads FALSE after the commit" +
                                  (varySetError == null ? "" : " (SetAllowVaryBetweenGroups failed: " + varySetError + ")") +
                                  ". The desagrupar modal is armed and will hang the bridge mid-write.");
                }
            }

            if (restoreError != null)
                parts.Add("SharedParametersFilename could NOT be restored: " + restoreError);

            return string.Join(" ", parts.ToArray());
        }

        // ---- Reading the binding. "Unread" is a distinct value from "not there". ----
        private class BindingState
        {
            public bool? Exists;              // null == we could not look. Never defaulted to false.
            public string ExistsError;
            public Definition Key;
            public bool KindMeasured;
            public bool? IsType;
            public string KindError;
            public bool CatsMeasured;
            public List<Category> Cats;
            public string CatsError;
            public int CatsUnreadable;
            public string CatsUnreadableError;
        }

        /// <summary>
        /// Re-read the binding from ParameterBindings.ForwardIterator(). The key is matched
        /// by GUID: an ExternalDefinition key exposes it directly, and a bound InternalDefinition
        /// is matched through SharedParameterElement.Lookup, whose id IS the definition's.
        /// Matching by NAME would happily match a different shared parameter that happens to
        /// share the name — and then every measurement below would describe the wrong binding.
        /// </summary>
        private static BindingState MeasureBinding(Document doc, Guid guid, string name)
        {
            var s = new BindingState();

            long? internalId = null;

            // Lookup returning null and Lookup THROWING are opposite facts, and treating
            // them alike made this handler refuse every real project.
            //
            // A shared parameter cannot be bound in a document without a
            // SharedParameterElement carrying its GUID — that element IS how Revit
            // stores it. So Lookup coming back null, without throwing, is Revit
            // answering the question: this GUID is not in this document, therefore it
            // is not bound, therefore Insert is the correct operation.
            //
            // The first version read that null as "I could not identify anything", and
            // then every InternalDefinition in the document — every ordinary project
            // parameter — became an entry that "might be ours". On the class model that
            // is a refusal on the very first bind, which is the only bind that matters.
            // Absence of evidence was being reported as evidence of absence's opposite.
            bool lookupAnswered = false;
            try
            {
                var spe = SharedParameterElement.Lookup(doc, guid);
                lookupAnswered = true;
                if (spe != null)
                {
                    var idf = spe.GetDefinition();
                    if (idf != null) internalId = RevitCompat.GetIdOrNull(idf.Id);
                }
            }
            catch (Exception ex)
            {
                // THIS is the unknown: we asked and got no answer. An ExternalDefinition
                // key still matches on GUID below, but an InternalDefinition key can no
                // longer be ruled out, so the scan below has a hole in it and says so.
                s.ExistsError = "SharedParameterElement.Lookup failed: " + ex.Message;
            }

            DefinitionBindingMapIterator it;
            try { it = doc.ParameterBindings.ForwardIterator(); }
            catch (Exception ex)
            {
                s.Exists = null;
                s.ExistsError = "Could not iterate ParameterBindings: " + ex.Message;
                return s;
            }

            bool sawUnmatchable = false;
            string unmatchableError = null;
            try
            {
                it.Reset();
                while (it.MoveNext())
                {
                    Definition key;
                    try { key = it.Key; }
                    catch (Exception ex)
                    {
                        // A key we cannot read might be ours. Recorded, because concluding
                        // "not bound" from a scan with holes in it is a claim we cannot make.
                        sawUnmatchable = true;
                        if (unmatchableError == null) unmatchableError = ex.Message;
                        continue;
                    }
                    if (key == null) continue;

                    bool isOurs = false;
                    var ext = key as ExternalDefinition;
                    if (ext != null)
                    {
                        try { isOurs = ext.GUID == guid; }
                        catch (Exception ex)
                        {
                            sawUnmatchable = true;
                            if (unmatchableError == null) unmatchableError = ex.Message;
                            continue;
                        }
                    }
                    else
                    {
                        var idf = key as InternalDefinition;
                        if (idf != null && internalId.HasValue)
                        {
                            try { isOurs = RevitCompat.GetId(idf.Id) == internalId.Value; }
                            catch (Exception ex)
                            {
                                sawUnmatchable = true;
                                if (unmatchableError == null) unmatchableError = ex.Message;
                                continue;
                            }
                        }
                        else if (idf != null && !internalId.HasValue)
                        {
                            // No id to match against. WHY there is no id decides everything:
                            //
                            //   * Lookup answered null -> this GUID is not in the document,
                            //     so this key belongs to some OTHER parameter (a project
                            //     parameter, or a different shared one). Definitively not
                            //     ours. Skip it, and do not poison the scan.
                            //   * Lookup threw -> we do not know, and this key might be
                            //     ours. That is a real hole; record it.
                            //
                            // Matching by name is not an option in either case: a different
                            // shared parameter with the same name would match, and every
                            // measurement afterwards would describe the wrong binding.
                            if (!lookupAnswered)
                            {
                                sawUnmatchable = true;
                                if (unmatchableError == null)
                                    unmatchableError = "an InternalDefinition key could not be identified because " +
                                                       "SharedParameterElement.Lookup could not be asked about this GUID";
                            }
                            continue;
                        }
                    }
                    if (!isOurs) continue;

                    s.Exists = true;
                    s.Key = key;
                    ReadBinding(it, s);
                    return s;
                }
            }
            catch (Exception ex)
            {
                s.Exists = null;
                s.ExistsError = "ParameterBindings iteration failed: " + ex.Message;
                return s;
            }

            if (sawUnmatchable)
            {
                // "I scanned the map and did not see it" is only 'not bound' if the scan was
                // complete. It was not.
                s.Exists = null;
                s.ExistsError = "'" + (name ?? guid.ToString("d")) + "' was not matched in ParameterBindings, but at " +
                                "least one entry could not be identified (" + unmatchableError + "), so it may be one " +
                                "of those. This is not evidence that the parameter is unbound.";
                return s;
            }

            s.Exists = false;
            return s;
        }

        private static void ReadBinding(DefinitionBindingMapIterator it, BindingState s)
        {
            Binding b;
            try { b = it.Current as Binding; }
            catch (Exception ex)
            {
                s.KindError = "The Binding object could not be read: " + ex.Message;
                s.CatsError = s.KindError;
                return;
            }
            if (b == null)
            {
                s.KindError = "ParameterBindings returned an entry with no readable Binding object.";
                s.CatsError = s.KindError;
                return;
            }

            // The kind comes from the object Revit handed back. `binding_kind` is what was
            // asked for; a report built on it would say 'Instance' about a TypeBinding.
            if (b is TypeBinding) { s.KindMeasured = true; s.IsType = true; }
            else if (b is InstanceBinding) { s.KindMeasured = true; s.IsType = false; }
            else s.KindError = "The binding is a " + b.GetType().Name + ", which is neither an InstanceBinding nor a " +
                               "TypeBinding. Its kind is not one of the two this tool can report.";

            var eb = b as ElementBinding;
            if (eb == null)
            {
                s.CatsError = "The binding is not an ElementBinding, so it exposes no category list.";
                return;
            }

            CategorySet cs;
            try { cs = eb.Categories; }
            catch (Exception ex) { s.CatsError = "Binding.Categories could not be read: " + ex.Message; return; }
            if (cs == null) { s.CatsError = "Binding.Categories returned null."; return; }

            var list = new List<Category>();
            try
            {
                foreach (Category c in cs)
                {
                    if (c == null) continue;
                    try { var probe = c.Id; }
                    catch (Exception ex)
                    {
                        // Counted, not dropped: a bound category we cannot name is still bound,
                        // and a silently shortened list is a drop that never happened.
                        s.CatsUnreadable++;
                        if (s.CatsUnreadableError == null) s.CatsUnreadableError = ex.Message;
                        continue;
                    }
                    list.Add(c);
                }
            }
            catch (Exception ex) { s.CatsError = "Could not enumerate the bound categories: " + ex.Message; return; }

            s.CatsMeasured = true;
            s.Cats = list;
        }

        private static HashSet<long> CategoryIdsOf(BindingState s)
        {
            if (!s.CatsMeasured || s.Cats == null) return null;
            return new HashSet<long>(s.Cats.Select(c => RevitCompat.GetId(c.Id)));
        }

        // ---- Finding the InternalDefinition through a real element. ---------------
        private class DefProbe
        {
            public InternalDefinition Def;
            public long? CarrierId;
            public int Scanned;
            public int Unreadable;
            public string FirstError;
            public string CollectorError;

            public string NoCarrierReason()
            {
                if (CollectorError != null)
                    return "The model could not be scanned for an element carrying this parameter (" + CollectorError +
                           "), so the InternalDefinition — the only object that exposes VariesAcrossGroups — was " +
                           "never reached.";
                var tail = Unreadable > 0
                    ? " " + Unreadable + " element(s) could not be read while scanning" +
                      (FirstError == null ? "" : " (first failure: " + FirstError + ")") +
                      ", so one of THEM may carry it; this scan did not come back clean."
                    : "";
                return "No element of the bound categories carries this parameter (" + Scanned + " scanned), so the " +
                       "InternalDefinition could not be reached and VariesAcrossGroups could neither be set nor " +
                       "read." + tail;
            }
        }

        /// <summary>
        /// The InternalDefinition, via a real element's parameter — the documented route,
        /// and the only one: right after Insert/ReInsert the binding iterator's Key is the
        /// ExternalDefinition, which does not expose VariesAcrossGroups at all. Must run
        /// after doc.Regenerate(), because until the model regenerates no element carries
        /// the parameter and this returns nothing.
        ///
        /// The element is looked up by GUID, not by name: LookupParameter(name) would match
        /// any parameter that happens to share the name and hand back the wrong definition to
        /// flag. Stopping at the first carrier is correct — the definition is document-wide —
        /// but a scan that finds NO carrier is a reported state, never a silent `break`.
        /// </summary>
        private static DefProbe ProbeInternalDefinition(Document doc, Guid guid, bool typeSide, HashSet<long> catIds)
        {
            var pr = new DefProbe();
            try
            {
                var col = new FilteredElementCollector(doc);
                var src = typeSide
                    ? col.WhereElementIsElementType()
                    : col.WhereElementIsNotElementType();

                foreach (var e in src)
                {
                    Category c;
                    try { c = e.Category; }
                    catch (Exception ex)
                    {
                        pr.Unreadable++;
                        if (pr.FirstError == null) pr.FirstError = ex.Message;
                        continue;
                    }
                    if (c == null) continue;

                    long cid;
                    try { cid = RevitCompat.GetId(c.Id); }
                    catch (Exception ex)
                    {
                        pr.Unreadable++;
                        if (pr.FirstError == null) pr.FirstError = ex.Message;
                        continue;
                    }
                    if (catIds != null && catIds.Count > 0 && !catIds.Contains(cid)) continue;

                    pr.Scanned++;

                    Parameter p;
                    try { p = e.get_Parameter(guid); }
                    catch (Exception ex)
                    {
                        pr.Unreadable++;
                        if (pr.FirstError == null) pr.FirstError = ex.Message;
                        continue;
                    }
                    if (p == null) continue;

                    InternalDefinition idf;
                    try { idf = p.Definition as InternalDefinition; }
                    catch (Exception ex)
                    {
                        pr.Unreadable++;
                        if (pr.FirstError == null) pr.FirstError = ex.Message;
                        continue;
                    }
                    if (idf == null) continue;

                    pr.Def = idf;
                    pr.CarrierId = RevitCompat.GetIdOrNull(e.Id);
                    return pr;
                }
            }
            catch (Exception ex) { pr.CollectorError = ex.Message; }
            return pr;
        }

        // ---- Resolution helpers. Each one fails loudly. --------------------------
        private static bool FindDefinition(DefinitionFile file, bool byGuid, Guid guid, string name,
                                           out ExternalDefinition found, out string why)
        {
            found = null; why = null;
            var hits = new List<ExternalDefinition>();
            int unreadable = 0;
            string firstError = null;

            try
            {
                foreach (DefinitionGroup g in file.Groups)
                {
                    foreach (Definition d in g.Definitions)
                    {
                        var ed = d as ExternalDefinition;
                        if (ed == null) continue;
                        try
                        {
                            if (byGuid) { if (ed.GUID == guid) hits.Add(ed); }
                            else if (string.Equals(ed.Name, name, StringComparison.Ordinal)) hits.Add(ed);
                        }
                        catch (Exception ex)
                        {
                            // A definition we cannot read might be the one asked for, or a
                            // second match. Either way the search is not conclusive.
                            unreadable++;
                            if (firstError == null) firstError = ex.Message;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                why = "Could not read the shared parameter file's groups: " + ex.Message + ". Nothing was bound.";
                return false;
            }

            string blind = unreadable == 0
                ? ""
                : " " + unreadable + " definition(s) in this SPF could not be read" +
                  (firstError == null ? "" : " (first failure: " + firstError + ")") + ".";

            if (hits.Count > 1)
            {
                why = "'" + name + "' matches " + hits.Count + " definitions in this SPF. Picking one would be a " +
                      "guess reported as a fact, and the wrong shared parameter is indistinguishable from the right " +
                      "one in a report. Pass param_guid instead." + blind;
                return false;
            }
            if (hits.Count == 0)
            {
                why = (byGuid ? "No shared parameter with GUID " + guid.ToString("d") : "No shared parameter named '" + name + "'") +
                      " was found in this SPF." + blind + " Nothing was bound.";
                return false;
            }
            if (!byGuid && unreadable > 0)
            {
                // One visible match plus definitions we could not see is not a unique match.
                why = "'" + name + "' matched 1 definition, but" + blind + " A unique match cannot be proven — one of " +
                      "them may carry the same name. Pass param_guid instead.";
                return false;
            }

            found = hits[0];
            return true;
        }

        private static Category ResolveCategory(Document doc, string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();

            if (input.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
            {
                BuiltInCategory bic;
                if (Enum.TryParse(input, true, out bic))
                {
                    try
                    {
                        var c = Category.GetCategory(doc, bic);
                        if (c != null) return c;
                    }
                    catch { /* falls through to the name scan below, which reports its own miss */ }
                }
            }

            try
            {
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (string.Equals(SafeCatName(cat), input, StringComparison.OrdinalIgnoreCase)) return cat;
                }
            }
            catch { return null; }
            return null;
        }

        /// <summary>
        /// PG_ name or group schema id to ForgeTypeId, resolved by reflecting over
        /// GroupTypeId. BuiltInParameterGroup does not exist across every version this
        /// assembly builds for, so the enum cannot be named here at all.
        /// </summary>
        private static ForgeTypeId ResolveGroup(string spec, out string why)
        {
            why = null;
            spec = spec.Trim();

            var known = new List<KeyValuePair<string, ForgeTypeId>>();
            try
            {
                foreach (var p in typeof(GroupTypeId).GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    if (p.PropertyType != typeof(ForgeTypeId)) continue;
                    ForgeTypeId v = null;
                    try { v = p.GetValue(null) as ForgeTypeId; }
                    catch { continue; }
                    if (v != null) known.Add(new KeyValuePair<string, ForgeTypeId>(p.Name, v));
                }
            }
            catch (Exception ex)
            {
                why = "Could not enumerate the parameter groups this Revit version exposes: " + ex.Message +
                      ". Nothing was bound.";
                return null;
            }

            if (spec.IndexOf(':') >= 0)
            {
                foreach (var kv in known)
                {
                    var tid = kv.Value.TypeId ?? "";
                    if (string.Equals(tid, spec, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                    int dash = tid.LastIndexOf('-');
                    if (dash > 0 && string.Equals(tid.Substring(0, dash), spec, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
                why = "group '" + spec + "' is not a group this Revit version exposes. Nothing was bound. Passing it " +
                      "through unvalidated would file the parameter under a group Revit invents, which is not the one " +
                      "you named.";
                return null;
            }

            // PG_IDENTITY_DATA -> IdentityData. The PG_ names are what the skills and the
            // existing python use, so they are the vocabulary this tool has to speak.
            var want = spec;
            if (want.StartsWith("PG_", StringComparison.OrdinalIgnoreCase)) want = want.Substring(3);
            var pascal = string.Concat(want.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant()));

            foreach (var kv in known)
                if (string.Equals(kv.Key, pascal, StringComparison.OrdinalIgnoreCase)) return kv.Value;

            var names = known.Select(k => "PG_" + ToPgName(k.Key)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            why = "group '" + spec + "' does not resolve to a parameter group in this Revit version. Nothing was " +
                  "bound. Known: " + string.Join(", ", names) + ".";
            return null;
        }

        private static string ToPgName(string pascal)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < pascal.Length; i++)
            {
                if (i > 0 && char.IsUpper(pascal[i])) sb.Append('_');
                sb.Append(char.ToUpperInvariant(pascal[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Suppresses Revit's modal dialogs during the bind. A modal here does not wait for
        /// a human — nobody is looking at Revit — it blocks the bridge until the caller times
        /// out, with a transaction open on the document.
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

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafeDefName(Definition d) { try { return d == null ? null : d.Name; } catch { return null; } }
        private static string SafeCatName(Category c) { try { return c == null ? null : c.Name; } catch { return "(name unreadable)"; } }
    }
}
