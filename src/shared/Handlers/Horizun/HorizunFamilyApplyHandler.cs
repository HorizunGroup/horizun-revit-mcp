// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// horizun_family_apply — the whole homologation of ONE .rfa, in ONE transaction.
//
// This replaces snippets §2, §3 and §3b of prodesa-homologar-familias. That blob
// runs over ~1029 families in the HUB/Prodesa library, 2-4 per call because the
// bridge cuts at 30 s, and it ends with:
//
//     try: fm.AddParameter(ed, gid, inst)
//     except System.Exception: pass
//     ...
//     try: fm.Set(p, u"%s" % it["value"])
//     except System.Exception: pass
//     ...
//     __output__ = "OK -> " + newpath
//
// Every write is wrapped in a bare `except: pass`, and the report is a string
// built from the INPUT. A family can come out saying "OK -> path" with zero
// parameters actually written, and nothing downstream can tell. So:
//
//   * FamilyManager.Set() returns VOID. Unlike Parameter.Set() there is not even
//     a bool to ignore: the ONLY evidence a value landed is reading it back off
//     fm.CurrentType after the commit. That is what params_set does, and
//     value_written != value_read_back is a FAILURE, not a warning.
//   * type_name_after comes from fm.CurrentType.Name re-read after the commit,
//     never from the family_name we were handed. RenameCurrentType throwing into
//     an `except: pass` is exactly how the local variable and the model diverge.
//   * params_removed is counted by RE-READING fm.Parameters. RemoveParameter is
//     void too, and Revit refuses it for referenced parameters; the snippet's
//     "borrados 12" is a count of calls that did not throw.
//   * params_added likewise: the AddParameter that silently did nothing is the
//     same lie, and it is the one that ships a family missing its PRD_ set.
//
// THE GEOMETRY INVARIANT — the reason this is a handler and not a script.
//
// The snippet already knows the rule. Its own comments say "Verifica que el
// conteo de params Double NO cambie" and "IsCustom mueve geometria!". But it
// enforces it by printing "borrados 12, saltados 3, Double 41->40" and trusting
// a human to read the arrow. Nothing stops nd0 != nd1; the batch moves on to the
// next family, and the deformed one is saved. Here the invariant is structural:
// the Double count and IsCustom's presence are captured BEFORE, re-enumerated
// FRESH from the family document after the writes, and if either moved the
// transaction is ROLLED BACK and the family is reported untouched. A batch
// cannot continue past a family whose geometry moved, because there is no path
// through this code that commits one.
//
// And "unproven" is not "fine". If any parameter in either census could not be
// read, the invariant cannot be established, so we roll back too: an invariant
// that passes because we could not look is worse than no invariant at all — it
// is a guarantee the caller will believe.
//
// THIS HANDLER NEVER OPENS A FILE. It operates on the ACTIVE family document.
// Opening a Prodesa 2025 .rfa from Revit 2026 upgrades it irreversibly and
// breaks the catalog — that decision belongs to the caller and to
// horizun_document_session, not to a write verb. rfa_path here is a GUARD (it
// must match the active document's PathName) and expected_revit_version is a
// second one, both checked before anything is written.
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
    public class HorizunFamilyApplyHandler : IRevitCommand
    {
        public string Name => "horizun_family_apply";

        public string Description =>
            "Homologate the ACTIVE family document (.rfa) in ONE transaction: collapse surplus types down to one and " +
            "rename it to family_name, add missing shared parameters from an SPF (respecting instance/type and the " +
            "parameter group), clear formulas on the parameters about to be written (a formula-driven parameter refuses " +
            "a value), set values, remove named parameters (the MPDT 'NA's), and strip vendor junk under the conservative " +
            "rule: String storage, no formula, matches a junk pattern, not excluded, not kept, not PRD_*. THE GEOMETRY " +
            "INVARIANT IS ENFORCED, NOT LOGGED: the count of Double parameters and the presence of IsCustom are captured " +
            "before, re-enumerated fresh after the writes, and if either changed — or if either census could not be read " +
            "completely — the WHOLE transaction is rolled back and the family is left untouched. Every reported field is a " +
            "fresh read of the family document after the commit: params_set reports value_written vs value_read_back and a " +
            "mismatch is a failure, type_name_after comes from fm.CurrentType.Name, params_added/params_removed are counted " +
            "by re-reading fm.Parameters and never by counting calls that did not throw (FamilyManager.Set and " +
            "RemoveParameter return void — there is not even a bool to check). Never opens a file: rfa_path is a guard that " +
            "must match the active document, because opening a 2025 .rfa in Revit 2026 upgrades it irreversibly. " +
            "Idempotent: a second run reports nothing to do, not an error. Use dry_run=true to see the plan without a " +
            "transaction.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""rfa_path"": { ""type"": ""string"", ""description"": ""GUARD, not an instruction to open anything. If given, the run aborts unless it resolves to the ACTIVE family document's PathName. This handler never calls OpenDocumentFile: opening a 2025 .rfa from Revit 2026 upgrades the file irreversibly and breaks the Prodesa catalog. Open the family yourself (or via horizun_document_session) in the right Revit, then pass its path here to prove this is the one."" },
    ""expected_revit_version"": { ""type"": ""string"", ""description"": ""GUARD, e.g. '2025'. Aborts unless the running Revit reports this VersionNumber. The families are 2025; saving one from 2026 upgrades it with no way back."" },
    ""family_name"": { ""type"": ""string"", ""description"": ""The canonical Family Name (no .rfa). Given, the family is collapsed to exactly ONE type named this. Omitted, no type is created, deleted or renamed."" },
    ""keep_type"": { ""type"": ""string"", ""description"": ""Which existing type survives the collapse. Default: the one already named family_name, else the first. Every other type is deleted, so name it when the family carries real different sizes — those must be split into one family per size BEFORE this runs, not collapsed here."" },
    ""collapse_types"": { ""type"": ""boolean"", ""default"": true, ""description"": ""With family_name set: delete the surplus types. false renames the surviving/current type only and leaves the others alone."" },
    ""spf_path"": { ""type"": ""string"", ""description"": ""Shared Parameter File (PRD-PARAMETROS_PRODESA_.txt) to take add_shared_params from. The app's SharedParametersFilename is restored afterwards."" },
    ""add_shared_params"": {
      ""type"": ""array"",
      ""description"": ""Shared parameters to add if missing. A parameter already present is left exactly as it is (idempotence), never re-added."",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""name""],
        ""properties"": {
          ""name"": { ""type"": ""string"", ""description"": ""Definition name as it reads in the SPF."" },
          ""instance"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = instance parameter. PRD_Alcance, PRD_Subcapitulo and PRD_Ubicacion are instance; the rest of the PRD_ set is type."" },
          ""group"": { ""type"": ""string"", ""default"": ""PG_DATA"", ""description"": ""Parameter group: 'PG_DATA', 'PG_IDENTITY_DATA', a GroupTypeId name ('Data', 'IdentityData'), or a full group ForgeTypeId. A group that cannot be resolved is an ERROR for that row — never a silent fallback to Data, which would file the parameter in the wrong place and report success."" }
        }
      }
    },
    ""values"": { ""type"": ""object"", ""description"": ""{ parameter_name: value }. Set on the surviving type. String | number | boolean | null. A number on Double/Integer storage is raw Revit internal units; a STRING on Double/Integer goes through SetValueString (unit-aware) and can only be confirmed against a re-read of itself — those rows are reported separately and never claimed as verified against your value."" },
    ""clear_formulas"": { ""type"": ""boolean"", ""default"": true, ""description"": ""SetFormula(p, null) on a parameter in 'values' that is driven by a formula, BEFORE writing it. Imported families arrive with Description/Manufacturer/Material governed by a vendor formula, and Revit refuses a value on those ('Cannot set the value of a parameter determined by a formula'). false = such a row is refused and reported, never silently skipped."" },
    ""clear_formulas_on"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Extra parameter names to clear the formula of even though no value is written to them."" },
    ""remove_params"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Parameters to delete by name — the MPDT 'NA' set. A name that is not in the family is 'nothing to do', not an error (idempotence). Revit refuses to remove a referenced parameter: that is reported as skipped with Revit's reason, never counted as removed."" },
    ""junk_rules"": {
      ""type"": ""object"",
      ""description"": ""Vendor metadata stripping (BIMobject/manufacturer families arrive with dozens — a Caleffi valve had 70). Off unless enabled."",
      ""properties"": {
        ""enabled"": { ""type"": ""boolean"", ""default"": false },
        ""patterns"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Lowercase substrings that mark a parameter as junk. Omit to use the proven Prodesa list (omniclass, uniclass, cobie, product *, brand, manufacturer url, region *, ...)."" },
        ""exclude"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Lowercase substrings that VETO removal even on a junk match — behaviour/MEP/material flags. Omit to use the proven list, which starts with 'custom' because IsCustom moves geometry."" },
        ""keep"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Exact names (lowercased) never removed: the standard identity set. Omit to use the proven list."" }
      }
    },
    ""save"": { ""type"": ""boolean"", ""default"": false, ""description"": ""doc.Save() in place after a successful commit. Never SaveAs, never a rename, never a delete of the original — the skill lost a family that way. saved_path is reported only after the file is found on disk AND re-read from disk. A rolled-back run never saves."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Resolve everything and report the plan and the before-census. Opens no transaction and saves nothing."" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: homologar familia"", ""description"": ""The label of the single undo step this becomes."" }
  }
}";

        // ---- The proven lists, verbatim from the skill that earned them. ----------
        // EXCL leads with "custom" for one reason: IsCustom moves geometry. These are
        // not tidy-ups, they are the fence around a rule that has already been paid for.
        private static readonly string[] DefaultJunk = {
            "omniclass","uniclass","uniformat","masterformat","nbs reference","cobie","unspsc","ean code",
            "gtin","bimobject","product ","brand","manufacturer name","manufacturer url","manufacturer country",
            "manufacturer art","manufacturer product line","region ","country","youtube","qr code","copyright",
            "date of publishing","edition number","family version","revit version","price","creation date",
            "article description","series","certification","warranty","installation instructions",
            "technical description","ifc classification","ifc url"
        };
        private static readonly string[] DefaultExclude = {
            "custom","nominal","annotation","connection","connector","symbolic","loss","coefficient",
            "material","discharge","impeller","poles","phase","color","yellow","black","grey","ifcexport"
        };
        private static readonly string[] DefaultKeep = {
            "keynote","assembly code","type comments","description","manufacturer","model","cost","url",
            "type mark","type image","type ifc predefined type","export type to ifc as","fire rating",
            "section name key","structural material","material","codigo_comercial","default elevation"
        };

        // The flag the skill singles out by name: "IsCustom mueve geometria!". Its
        // disappearance is a geometry change even though it is not a Double.
        private const string GeometryFlagParam = "IsCustom";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail(
                    "No document is open. This handler works on the ACTIVE family document and deliberately does not " +
                    "open files: opening a 2025 .rfa from Revit 2026 upgrades it irreversibly. Open the .rfa in the " +
                    "right Revit first.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            // ---- Refusals. Every one of these is cheaper than the file it protects. --
            bool isFamily;
            try { isFamily = doc.IsFamilyDocument; }
            catch (Exception ex)
            {
                return CommandResult.Fail("Could not determine whether the active document is a family document: " +
                                          ex.Message + ". Nothing was written.");
            }
            if (!isFamily)
                return CommandResult.Fail(
                    "The active document '" + (SafeTitle(doc) ?? "(title unreadable)") + "' is NOT a family document. " +
                    "This handler drives FamilyManager, which only exists in a .rfa. Nothing was written. A project " +
                    "model has no FamilyManager: the parameters you mean live on the element or the type there — use " +
                    "horizun_write_params_verified.");

            var wantVersion = request.Value<string>("expected_revit_version");
            if (!string.IsNullOrWhiteSpace(wantVersion))
            {
                string have = null;
                try { have = app.Application.VersionNumber; } catch { }
                if (!string.Equals(have, wantVersion.Trim(), StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail(
                        "This is Revit " + (have ?? "(version unreadable)") + ", not " + wantVersion + ". Nothing was " +
                        "written. Saving a family from a newer Revit than the one that authored it upgrades the file " +
                        "with no way back — that is how the Prodesa catalog gets broken. Run this against the Revit " +
                        "that owns the family.");
            }

            var wantPath = request.Value<string>("rfa_path");
            string docPath = SafePathName(doc);
            if (!string.IsNullOrWhiteSpace(wantPath))
            {
                if (string.IsNullOrEmpty(docPath))
                    return CommandResult.Fail(
                        "rfa_path was given but the active family has no path on disk (it has never been saved), so " +
                        "there is nothing to match it against. Nothing was written.");
                if (!SamePath(wantPath, docPath))
                    return CommandResult.Fail(
                        "The active family document is '" + docPath + "', not '" + wantPath + "'. Nothing was written. " +
                        "This handler does NOT open files — writing into whichever family happened to be in front is " +
                        "how a batch homologates the wrong .rfa. Activate the intended family and re-run.");
            }

            var familyName = request.Value<string>("family_name");
            if (familyName != null) familyName = familyName.Trim();
            bool collapse = request["collapse_types"] == null || request.Value<bool>("collapse_types");
            var keepTypeName = request.Value<string>("keep_type");
            bool clearFormulas = request["clear_formulas"] == null || request.Value<bool>("clear_formulas");
            bool dryRun = request.Value<bool>("dry_run");
            bool save = request.Value<bool>("save");
            var txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: homologar familia";

            FamilyManager fm;
            try { fm = doc.FamilyManager; }
            catch (Exception ex) { return CommandResult.Fail("This family document has no readable FamilyManager: " + ex.Message); }
            if (fm == null) return CommandResult.Fail("This family document has no FamilyManager.");

            // ---- The before-census. The invariant's left-hand side. ------------------
            var before = Census.Take(fm);
            if (!before.Complete)
                return CommandResult.Fail(
                    "The family's parameters could not be read completely BEFORE any write (" + before.Unreadable +
                    " unreadable" + (before.FirstError == null ? "" : ", first failure: " + before.FirstError) + "), so " +
                    "the geometry invariant has no baseline: there would be no way to prove afterwards that the Double " +
                    "count and IsCustom did not move. Nothing was written. An invariant that passes because we could " +
                    "not look is worse than none — the caller would believe it.");

            // ---- Plan. Everything that can fail without touching the model fails here.
            var plan = new Plan();
            string why;
            if (!BuildPlan(app, doc, fm, request, familyName, keepTypeName, collapse, clearFormulas, plan, out why))
                return CommandResult.Fail(why);

            if (dryRun)
            {
                return CommandResult.Ok(new JObject
                {
                    ["mode"] = "dry_run",
                    ["transaction_status"] = "not_started",
                    ["document"] = SafeTitle(doc),
                    ["document_path"] = docPath,
                    ["is_family_document"] = true,
                    ["family_category"] = SafeCategory(doc),
                    ["geometry_invariant"] = new JObject
                    {
                        ["status"] = "not_checked_yet",
                        ["double_count_before"] = before.DoubleCount,
                        ["is_custom_present_before"] = before.GeometryFlagPresent,
                        ["note"] = "This is only the baseline. The invariant is proven by re-reading these AFTER the " +
                                   "writes, inside the transaction, and rolling back if either moved."
                    },
                    ["prd_count_before"] = before.PrdCount,
                    ["types_before"] = new JArray(before.TypeNames.Select(n => (JToken)n)),
                    ["plan"] = plan.ToJson(),
                    ["note"] = "Nothing was written; no transaction was opened, nothing was saved. " +
                               (plan.HasRefusals()
                                   ? "Some rows already refused before any write — see their 'error'. Those are not rows that would 'probably work'. "
                                   : "") +
                               "Re-run with dry_run=false."
                });
            }

            if (plan.IsEmpty() && plan.HasRefusals())
            {
                // IsEmpty() only counts rows with Error == null, so a plan whose EVERY row
                // was refused at plan time is "empty" too. Falling through to the branch
                // below would answer "this family already matches what was asked for" about
                // a family nothing was ever checked against — the request was declined in
                // full, not satisfied. The two cases are opposites and must not share a
                // response.
                var reasons = plan.Skipped().Select(s =>
                {
                    var nm = s["name"] == null || s["name"].Type == JTokenType.Null
                        ? "(unnamed)" : s.Value<string>("name");
                    var op = s["operation"] == null || s["operation"].Type == JTokenType.Null
                        ? "(operation unrecorded)" : s.Value<string>("operation");
                    return "  - " + op + " '" + nm + "': " + s.Value<string>("reason");
                }).ToArray();

                return CommandResult.Fail(
                    "EVERY row of this request was REFUSED before anything was written, so nothing was left to do. " +
                    "This is NOT the idempotent case and this family does NOT already match what was asked for: not " +
                    "one of the things you asked for was applied, and NOTHING about the family was verified against " +
                    "your request. No transaction was opened, nothing was written, the file was not saved and no " +
                    "backup was created. The refusals, each with the reason it was given:\n" +
                    (reasons.Length == 0
                        ? "  (the refusals carried no reason this handler could render — treat the whole request as unapplied)"
                        : string.Join("\n", reasons)) +
                    "\nRe-running this family unchanged produces exactly these refusals again.");
            }

            if (plan.IsEmpty())
            {
                // Idempotence: the second run of a homologated family has nothing to do.
                // Reached only when there are no refusals either (checked above), so
                // "already matches" is a claim about the whole request, not a leftover.
                // Opening a transaction to commit nothing would let this report a clean
                // "Committed" over an untouched family — success-shaped noise in a batch
                // of 1029 where the operator reads the totals, not the rows.
                return CommandResult.Ok(new JObject
                {
                    ["mode"] = "apply",
                    ["transaction_status"] = "not_started",
                    ["document"] = SafeTitle(doc),
                    ["document_path"] = docPath,
                    ["nothing_to_do"] = true,
                    ["geometry_invariant"] = InvariantJson("not_checked", before, before, null),
                    ["type_name_after"] = SafeCurrentTypeName(doc),
                    ["prd_count_before"] = before.PrdCount,
                    ["prd_count_after"] = before.PrdCount,
                    ["params_added"] = new JArray(),
                    ["params_set"] = new JArray(),
                    ["params_removed"] = new JArray(),
                    ["params_removed_count"] = 0,
                    ["params_skipped"] = new JArray(plan.Skipped().Select(s => (JToken)s)),
                    ["formulas_cleared"] = new JArray(),
                    ["formulas_cleared_count"] = 0,
                    ["formulas_clear_failed"] = new JArray(),
                    ["types_deleted"] = new JArray(),
                    ["types_deleted_count"] = 0,
                    ["types_delete_failed"] = new JArray(),
                    ["saved"] = SaveSkipped("no transaction was opened: there was nothing to do"),
                    ["note"] = "Nothing to do: this family already matches what was asked for. No transaction was " +
                               "opened and the file was not saved, so no .000N.rfa backup was created either. This is " +
                               "the idempotent case, not a failure."
                });
            }

            // ---- Write. ONE transaction, one undo step. -----------------------------
            string txStatus;
            string invariantStatus;
            Census afterInTx = null;
            bool committed = false;
            string rollbackReason = null;

            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    // A Revit modal here waits for a human who is not there: it hangs the
                    // bridge until the 30 s cut, and the caller retries a family that may
                    // already be half done.
                    var opts = tx.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new SilenceModals());
                    opts.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(opts);

                    ApplyTypes(doc, plan);
                    ApplyAddShared(doc, plan);
                    ApplyClearFormulas(doc, plan);
                    ApplyValues(doc, plan);
                    ApplyRemovals(doc, plan);

                    // Formula-driven and derived values do not settle until a regen, so a
                    // read before this reads our own intent. It also forces Revit to work
                    // through the parameter removals before we measure the geometry.
                    doc.Regenerate();

                    // Read inside the transaction, after the regen: this is the drift
                    // baseline. If it disagrees with the post-commit read, something else
                    // in this commit wrote the parameter and this row is not provably the
                    // author of the value it now holds.
                    var fmTx = doc.FamilyManager;
                    var ftTx = SafeCurrentType(fmTx);
                    foreach (var r in plan.Sets)
                        if (r.SetterRan) r.Written = ReadFamilyValue(fmTx, ftTx, r.Name);

                    // FRESH enumeration off the document — not a filter over the list the
                    // before-census built. Diffing two views of one in-memory list is the
                    // lie the spec names: the invariant would always "pass" and protect
                    // exactly nothing.
                    afterInTx = Census.Take(doc.FamilyManager);

                    if (!afterInTx.Complete)
                    {
                        invariantStatus = "unproven";
                        rollbackReason =
                            "The family's parameters could not be read completely after the writes (" +
                            afterInTx.Unreadable + " unreadable" +
                            (afterInTx.FirstError == null ? "" : ", first failure: " + afterInTx.FirstError) +
                            "), so whether the geometry moved is UNKNOWN — which is not the same as it being intact. " +
                            "The whole transaction was rolled back. Unknown is not a licence to continue.";
                    }
                    else if (afterInTx.DoubleCount != before.DoubleCount ||
                             afterInTx.GeometryFlagPresent != before.GeometryFlagPresent)
                    {
                        invariantStatus = "violated";
                        rollbackReason = ViolationReason(before, afterInTx);
                    }
                    else
                    {
                        invariantStatus = "proven_unchanged";
                    }

                    if (rollbackReason != null)
                    {
                        tx.RollBack();
                        txStatus = "RolledBack";
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
                    // Revit undid everything and returned a status instead of throwing.
                    // Every count taken above is fiction now.
                    return CommandResult.Ok(RolledBackResponse(doc, docPath, before, plan, "RolledBack", "not_checked",
                        afterInTx, ex.Message + " The family is untouched; every row reports nothing written."));
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail(
                        "The homologation failed and was rolled back; the family is untouched and was not saved: " +
                        ex.Message);
                }
            }

            if (!committed)
                return CommandResult.Ok(RolledBackResponse(doc, docPath, before, plan, txStatus, invariantStatus,
                    afterInTx, rollbackReason));

            // ---- The only evidence that counts: fresh reads, after the commit. ------
            FamilyManager fmAfter;
            try { fmAfter = doc.FamilyManager; }
            catch (Exception ex)
            {
                return CommandResult.Fail(
                    "The transaction COMMITTED and then the family document became unreadable (" + ex.Message +
                    "). The writes are in the model; this handler cannot report what they did. Do not treat this " +
                    "family as homologated — re-open it and inspect it.");
            }

            var after = Census.Take(fmAfter);
            var typeAfter = SafeCurrentType(fmAfter);
            string typeNameAfter = SafeTypeName(typeAfter);

            foreach (var r in plan.Sets) r.ReadBack = ReadFamilyValue(fmAfter, typeAfter, r.Name);
            foreach (var r in plan.Sets) r.Judge();
            foreach (var r in plan.Adds) r.Judge(after);
            foreach (var r in plan.Removals) r.Judge(before, after);
            foreach (var r in plan.FormulaClears) r.Judge(fmAfter);
            plan.JudgeTypes(fmAfter, familyName);

            // The invariant, re-read a THIRD time — after the commit, as the contract
            // requires every reported field to be. The in-transaction read is what the
            // rollback decision rested on; if the two disagree, something moved during
            // the commit itself and it is far too late to undo. That is not a field we
            // are allowed to smooth over.
            string invariantFinal = invariantStatus;
            string invariantWarning = null;
            if (!after.Complete)
            {
                invariantFinal = "unknown_after_commit";
                invariantWarning =
                    "The transaction COMMITTED after the invariant was proven inside it, but the post-commit census " +
                    "could not read " + after.Unreadable + " parameter(s)" +
                    (after.FirstError == null ? "" : " (first failure: " + after.FirstError + ")") +
                    ", so the reported double_count_after / is_custom_present_after are NOT a complete measurement. " +
                    "Whether the geometry is intact is unknown. The commit is done; it cannot be undone from here.";
            }
            else if (after.DoubleCount != before.DoubleCount || after.GeometryFlagPresent != before.GeometryFlagPresent)
            {
                invariantFinal = "violated_after_commit";
                invariantWarning =
                    "THE GEOMETRY MOVED AND IT IS COMMITTED. The invariant held when it was checked inside the " +
                    "transaction (Double " + before.DoubleCount + "->" + (afterInTx == null ? -1 : afterInTx.DoubleCount) +
                    "), and the post-commit re-read disagrees: " + ViolationReason(before, after) + " The commit is DONE " +
                    "and cannot be undone from here. Do NOT save this family and do NOT continue the batch — re-open " +
                    "the .rfa from disk and check its geometry.";
            }

            // A save is a separate act with its own evidence, and it must not happen at
            // all if the family we are about to write to disk is one we just said we
            // cannot vouch for.
            JObject saved;
            if (!save)
                saved = SaveSkipped("save was not requested");
            else if (invariantFinal != "proven_unchanged")
                saved = SaveSkipped("REFUSED: the geometry invariant is '" + invariantFinal + "'. Saving would put a " +
                                    "family whose geometry we cannot vouch for on disk, over the last good copy.");
            else
                saved = SaveAndVerify(doc);

            int setsConfirmed = plan.Sets.Count(r => r.Outcome == OUT_CONFIRMED);
            int setsParseOnly = plan.Sets.Count(r => r.Outcome == OUT_CONFIRMED && r.ExpectationFromModel);
            int setsFailed = plan.Sets.Count(r => r.Outcome == OUT_NOT_WRITTEN);
            int setsUnknown = plan.Sets.Count(r => r.Outcome == OUT_UNKNOWN);

            return CommandResult.Ok(new JObject
            {
                ["mode"] = "apply",
                ["transaction_status"] = txStatus,
                ["transaction_name"] = txName,
                ["document"] = SafeTitle(doc),
                ["document_path"] = docPath,
                ["family_category"] = SafeCategory(doc),

                ["geometry_invariant"] = InvariantJson(invariantFinal, before, after, invariantWarning),

                // fm.CurrentType.Name, re-read from the document. Never family_name.
                ["type_name_after"] = typeNameAfter,
                ["type_name_matches_family_name"] = familyName == null
                    ? null
                    : (JToken)string.Equals(typeNameAfter, familyName, StringComparison.Ordinal),
                ["types_after"] = new JArray(after.TypeNames.Select(n => (JToken)n)),
                // Filtered to what a fresh re-read of fm.Types says is GONE, exactly like
                // params_removed below. Unfiltered, this field was a list of deletion
                // ATTEMPTS and its length was "the count of calls that did not throw" —
                // the very thing the header of this file disclaims.
                ["types_deleted"] = new JArray(plan.TypeDeletes.Where(t => t.Outcome == OUT_CONFIRMED)
                                                               .Select(t => (JToken)t.ToJson())),
                ["types_deleted_count"] = plan.TypeDeletes.Count(t => t.Outcome == OUT_CONFIRMED),
                ["types_delete_failed"] = new JArray(plan.TypeDeletes.Where(t => t.Outcome != OUT_CONFIRMED)
                                                                     .Select(t => (JToken)t.ToJson())),
                ["type_rename"] = plan.Rename == null ? null : plan.Rename.ToJson(),

                ["prd_count_before"] = before.PrdCount,
                ["prd_count_after"] = after.PrdCount,

                ["params_added"] = new JArray(plan.Adds.Select(a => (JToken)a.ToJson())),
                ["params_added_confirmed"] = plan.Adds.Count(a => a.Outcome == OUT_CONFIRMED),
                // Filtered to the ones a fresh post-commit read of p.Formula says are GONE.
                // Unfiltered, this field carried rows whose formula is demonstrably STILL
                // on the parameter — under a field name that says they were cleared.
                ["formulas_cleared"] = new JArray(plan.FormulaClears.Where(f => f.Outcome == OUT_CONFIRMED)
                                                                    .Select(f => (JToken)f.ToJson())),
                ["formulas_cleared_count"] = plan.FormulaClears.Count(f => f.Outcome == OUT_CONFIRMED),
                ["formulas_clear_failed"] = new JArray(plan.FormulaClears.Where(f => f.Outcome != OUT_CONFIRMED)
                                                                         .Select(f => (JToken)f.ToJson())),
                ["params_set"] = new JArray(plan.Sets.Select(s => (JToken)s.ToJson(true))),
                ["params_set_confirmed"] = setsConfirmed,
                ["params_set_confirmed_against_your_value"] = setsConfirmed - setsParseOnly,
                ["params_set_confirmed_by_parse_read_back_only"] = setsParseOnly,
                ["params_set_note"] = ParseOnlyNote(setsParseOnly),
                ["params_set_failed"] = setsFailed,
                ["params_set_unknown"] = setsUnknown,
                ["params_set_unknown_note"] = UnknownNote(setsUnknown),
                // Counted by re-reading fm.Parameters, never by counting RemoveParameter
                // calls that did not throw.
                ["params_removed"] = new JArray(plan.Removals.Where(r => r.Outcome == OUT_CONFIRMED)
                                                             .Select(r => (JToken)r.ToJson())),
                ["params_removed_count"] = plan.Removals.Count(r => r.Outcome == OUT_CONFIRMED),
                ["params_skipped"] = new JArray(plan.Skipped().Select(s => (JToken)s)),

                ["saved"] = saved,
                ["note"] = FinalNote(invariantFinal, setsConfirmed, setsFailed, setsUnknown, plan)
            });
        }

        // ---- The three-way split, same as horizun_write_params_verified. -----------
        // "I could not look" is a value of its own. It is never a bool defaulting to
        // false and never summed into the failures.
        private const string OUT_CONFIRMED = "confirmed";
        private const string OUT_NOT_WRITTEN = "not_written";
        private const string OUT_UNKNOWN = "unknown";
        private const string OUT_NOTHING_TO_DO = "nothing_to_do";

        // =====================================================================
        // The census. Fresh enumeration, every time, straight off the document.
        // =====================================================================
        private class Census
        {
            public int DoubleCount;
            public bool GeometryFlagPresent;
            public int PrdCount;
            public int Total;
            public HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal);
            public List<string> TypeNames = new List<string>();
            public int Unreadable;
            public string FirstError;

            /// <summary>
            /// A census with even one unreadable parameter cannot decide the invariant:
            /// the unreadable one may be the Double that vanished. Complete is the
            /// difference between "the geometry is intact" and "I could not look".
            /// </summary>
            public bool Complete { get { return Unreadable == 0; } }

            public static Census Take(FamilyManager fm)
            {
                var c = new Census();
                if (fm == null)
                {
                    c.Unreadable = 1;
                    c.FirstError = "The family document has no FamilyManager to enumerate.";
                    return c;
                }
                try
                {
                    foreach (FamilyParameter p in fm.Parameters)
                    {
                        c.Total++;
                        string n;
                        StorageType st;
                        try
                        {
                            n = p.Definition.Name;
                            st = p.StorageType;
                        }
                        catch (Exception ex)
                        {
                            // NOT skipped in silence: an unread parameter is exactly the one
                            // that could be the missing Double, and dropping it here is how
                            // the invariant would pass over a family whose geometry moved.
                            c.Unreadable++;
                            if (c.FirstError == null) c.FirstError = ex.Message;
                            continue;
                        }
                        c.Names.Add(n);
                        if (st == StorageType.Double) c.DoubleCount++;
                        if (string.Equals(n, GeometryFlagParam, StringComparison.OrdinalIgnoreCase))
                            c.GeometryFlagPresent = true;
                        if (n.StartsWith("PRD_", StringComparison.Ordinal)) c.PrdCount++;
                    }
                }
                catch (Exception ex)
                {
                    c.Unreadable++;
                    if (c.FirstError == null) c.FirstError = "Enumerating fm.Parameters failed: " + ex.Message;
                }

                try
                {
                    foreach (FamilyType ft in fm.Types)
                    {
                        try { c.TypeNames.Add(ft.Name); }
                        catch (Exception ex)
                        {
                            c.Unreadable++;
                            if (c.FirstError == null) c.FirstError = "A family type's name is unreadable: " + ex.Message;
                        }
                    }
                }
                catch (Exception ex)
                {
                    c.Unreadable++;
                    if (c.FirstError == null) c.FirstError = "Enumerating fm.Types failed: " + ex.Message;
                }
                return c;
            }
        }

        private static string ViolationReason(Census before, Census after)
        {
            var parts = new List<string>();
            if (after.DoubleCount != before.DoubleCount)
                parts.Add("the count of Double parameters went " + before.DoubleCount + " -> " + after.DoubleCount +
                          ". Double parameters ARE the geometry of this family; one of them is gone or new.");
            if (before.GeometryFlagPresent && !after.GeometryFlagPresent)
                parts.Add("'" + GeometryFlagParam + "' is GONE. It moves geometry — removing it deforms the family.");
            if (!before.GeometryFlagPresent && after.GeometryFlagPresent)
                parts.Add("'" + GeometryFlagParam + "' APPEARED, which this operation has no business doing.");
            return "GEOMETRY INVARIANT BROKEN: " + string.Join(" ", parts.ToArray()) +
                   " The whole transaction was rolled back — nothing was written, including the parts that worked, and " +
                   "the file was not saved. This is not a warning to read in a log: a family whose geometry moved must " +
                   "not pass through a batch.";
        }

        private static JObject InvariantJson(string status, Census before, Census after, string warning)
        {
            return new JObject
            {
                ["status"] = status,
                ["double_count_before"] = before.DoubleCount,
                ["double_count_after"] = after == null ? null : (JToken)after.DoubleCount,
                ["is_custom_present_before"] = before.GeometryFlagPresent,
                ["is_custom_present_after"] = after == null ? null : (JToken)after.GeometryFlagPresent,
                ["params_total_before"] = before.Total,
                ["params_total_after"] = after == null ? null : (JToken)after.Total,
                ["census_complete_before"] = before.Complete,
                ["census_complete_after"] = after == null ? null : (JToken)after.Complete,
                ["status_means"] =
                    "proven_unchanged: both censuses were read in full, fresh off the family document, and the Double " +
                    "count and IsCustom are identical. violated / unproven: the transaction was ROLLED BACK — the " +
                    "family is untouched. violated_after_commit / unknown_after_commit: the check passed inside the " +
                    "transaction and the post-commit re-read says otherwise; the commit is done and cannot be undone. " +
                    "not_checked: no transaction ran.",
                ["warning"] = warning
            };
        }

        // =====================================================================
        // Plan rows.
        // =====================================================================
        private class Plan
        {
            public List<TypeDelete> TypeDeletes = new List<TypeDelete>();
            public RenameOp Rename;
            public FamilyType Keep;
            public List<AddRow> Adds = new List<AddRow>();
            public List<ClearRow> FormulaClears = new List<ClearRow>();
            public List<SetRow> Sets = new List<SetRow>();
            public List<RemoveRow> Removals = new List<RemoveRow>();
            public List<JObject> PreRefused = new List<JObject>();

            public bool IsEmpty()
            {
                return TypeDeletes.Count(t => t.Error == null) == 0
                       && (Rename == null || Rename.Error != null || !Rename.Needed)
                       && Adds.Count(a => a.Error == null) == 0
                       && FormulaClears.Count(f => f.Error == null) == 0
                       && Sets.Count(s => s.Error == null) == 0
                       && Removals.Count(r => r.Error == null) == 0;
            }

            /// <summary>
            /// What WOULD be done. Every verb here is conditional on purpose: nothing has
            /// been read back, because nothing has been written.
            /// </summary>
            public JObject ToJson()
            {
                return new JObject
                {
                    ["types_would_delete"] = new JArray(TypeDeletes.Where(t => t.Error == null)
                                                                   .Select(t => (JToken)t.Name)),
                    ["type_rename_would"] = Rename == null ? null : Rename.ToJson(),
                    ["params_would_add"] = new JArray(Adds.Where(a => a.Error == null && !a.AlreadyPresent)
                                                          .Select(a => (JToken)a.ToJson())),
                    ["params_already_present"] = new JArray(Adds.Where(a => a.AlreadyPresent)
                                                                .Select(a => (JToken)a.Name)),
                    ["formulas_would_clear"] = new JArray(FormulaClears.Where(f => f.Error == null)
                                                                       .Select(f => (JToken)f.ToJson())),
                    ["params_would_set"] = new JArray(Sets.Where(s => s.Error == null)
                                                          .Select(s => (JToken)s.ToJson(false))),
                    ["params_would_remove"] = new JArray(Removals.Where(r => r.WasPresent)
                                                                 .Select(r => (JToken)r.ToJson())),
                    ["refused_now"] = new JArray(Skipped().Select(s => (JToken)s)),
                    ["nothing_to_do"] = IsEmpty()
                };
            }

            public bool HasRefusals()
            {
                return PreRefused.Count > 0
                       || TypeDeletes.Any(t => t.Error != null)
                       || Adds.Any(a => a.Error != null)
                       || Sets.Any(s => s.Error != null)
                       || Removals.Any(r => r.Error != null)
                       || FormulaClears.Any(f => f.Error != null)
                       || (Rename != null && Rename.Error != null);
            }

            /// <summary>
            /// Everything this run declined to touch, and why. A skipped row is a row the
            /// caller can see; the blob's `except: pass` made these disappear entirely,
            /// which is how a family shipped with three of its PRD_ parameters missing and
            /// a clean-looking report.
            /// </summary>
            public IEnumerable<JObject> Skipped()
            {
                foreach (var j in PreRefused) yield return j;
                foreach (var t in TypeDeletes)
                {
                    var why = Refusal(t.Error, t.Outcome);
                    if (why != null) yield return Skip(t.Name, "delete_type", why);
                }
                if (Rename != null && Rename.Needed)
                {
                    var why = Refusal(Rename.Error, Rename.Outcome);
                    if (why != null) yield return Skip(Rename.To, "rename_type", why);
                }
                foreach (var a in Adds)
                {
                    var why = Refusal(a.Error, a.Outcome);
                    if (why != null) yield return Skip(a.Name, "add_shared_param", why);
                }
                foreach (var s in Sets)
                {
                    var why = Refusal(s.Error, s.Outcome);
                    if (why != null) yield return Skip(s.Name, "set_value", why);
                }
                foreach (var r in Removals)
                {
                    var why = Refusal(r.Error, r.Outcome);
                    if (why != null) yield return Skip(r.Name, "remove_param", why);
                }
                foreach (var f in FormulaClears)
                {
                    var why = Refusal(f.Error, f.Outcome);
                    if (why != null) yield return Skip(f.Name, "clear_formula", why);
                }
            }

            /// <summary>
            /// The reason a row is a refusal, or null if it is not one.
            ///
            /// The rule is `Outcome != OUT_CONFIRMED` ONLY once the row has been judged. A
            /// row that has not been judged yet carries Outcome == null and, if nothing
            /// refused it, Error == null: it is a row that is about to be DONE, not one
            /// that was declined. Treating unjudged as refused is how dry_run listed every
            /// planned removal in `refused_now` with `"reason": null` while the same rows
            /// sat in `params_would_remove` — two fields a consumer reads as disjoint. And
            /// a refusal with no reason is not a refusal we are entitled to report at all.
            /// </summary>
            private static string Refusal(string error, string outcome)
            {
                if (error != null) return error;
                if (outcome != null && outcome != OUT_CONFIRMED) return outcome;
                return null;
            }

            internal static string Join(string first, string second)
            {
                return first == null ? second : first + " " + second;
            }

            private static JObject Skip(string name, string what, string reason)
            {
                return new JObject { ["name"] = name, ["operation"] = what, ["reason"] = reason };
            }

            public void JudgeTypes(FamilyManager fm, string familyName)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                bool readable = true;
                string readError = null;
                try { foreach (FamilyType ft in fm.Types) names.Add(ft.Name); }
                catch (Exception ex) { readable = false; readError = ex.Message; }

                foreach (var d in TypeDeletes)
                {
                    if (!readable) { d.Outcome = OUT_UNKNOWN; d.Error = "The family's types could not be re-read after the commit (" + readError + "), so whether this type is gone is UNKNOWN."; continue; }
                    // Counted by re-reading fm.Types, not by counting DeleteCurrentType
                    // calls that did not throw.
                    if (!names.Contains(d.Name)) { d.Outcome = OUT_CONFIRMED; }
                    else { d.Outcome = OUT_NOT_WRITTEN; d.Error = "The type is STILL in the family after the commit. It was not deleted."; }
                }

                // Judged whenever a rename was NEEDED — never gated on Rename.Error being
                // null. An error from the apply phase (RenameCurrentType threw, the
                // surviving type could not be made current, NewType failed) is a reason to
                // look HARDER at what the model now says, not a reason to stop looking:
                // gating on it left Outcome null, and a null Outcome is not OUT_NOT_WRITTEN,
                // so FinalNote fell silent and a run whose only failure was the rename came
                // back shaped exactly like a clean success.
                if (Rename != null && Rename.Needed)
                {
                    string applyError = Rename.Error;   // whatever the apply phase already knew
                    string now = null;
                    try { now = fm.CurrentType == null ? null : fm.CurrentType.Name; }
                    catch (Exception ex)
                    {
                        Rename.Outcome = OUT_UNKNOWN;
                        Rename.Error = Join(applyError,
                            "fm.CurrentType.Name could not be read after the commit (" + ex.Message +
                            "), so whether the surviving type is named '" + familyName + "' is UNKNOWN.");
                        return;
                    }
                    Rename.NameAfter = now;
                    if (string.Equals(now, familyName, StringComparison.Ordinal))
                    {
                        Rename.Outcome = OUT_CONFIRMED;
                        Rename.Error = applyError == null
                            ? null
                            : applyError + " (Kept on the record even though the outcome is confirmed: the " +
                              "post-commit re-read of fm.CurrentType.Name says '" + familyName + "', so the name is " +
                              "right despite that failure. The read decides, not the call.)";
                    }
                    else
                    {
                        Rename.Outcome = OUT_NOT_WRITTEN;
                        Rename.Error = Join(applyError,
                            "The surviving type is named '" + (now ?? "(null)") + "', not '" + familyName +
                            "'. The rename did not land. The MPDT loader matches on Family Name = Type Name; " +
                            "this family will not match.");
                    }
                }
            }
        }

        private class TypeDelete
        {
            public string Name;
            public string Error;
            public string Outcome;
            public JObject ToJson()
            {
                return new JObject { ["name"] = Name, ["outcome"] = Outcome, ["error"] = Error };
            }
        }

        private class RenameOp
        {
            public string From;
            public string To;
            public bool Needed;
            public bool Created;      // there was no type at all; one was created
            public string NameAfter;  // re-read from fm.CurrentType.Name
            public string Error;
            public string Outcome;
            public JObject ToJson()
            {
                return new JObject
                {
                    ["from"] = From,
                    ["to"] = To,
                    ["needed"] = Needed,
                    ["type_created"] = Created,
                    ["name_after_read_from_model"] = NameAfter,
                    ["outcome"] = Outcome,
                    ["error"] = Error
                };
            }
        }

        private class AddRow
        {
            public string Name;
            public bool Instance;
            public string GroupSpec;
            public ForgeTypeId Group;
            public ExternalDefinition Def;
            public bool AlreadyPresent;
            public string Error;
            public string Outcome;

            /// <summary>
            /// Confirmed only if the name is in a fresh post-commit census. AddParameter
            /// returns the FamilyParameter it made — holding that object and calling it
            /// proof is reading our own handle, not the family.
            /// </summary>
            public void Judge(Census after)
            {
                if (AlreadyPresent) { Outcome = OUT_NOTHING_TO_DO; return; }
                if (Error != null && Outcome == null) { Outcome = OUT_NOT_WRITTEN; return; }
                if (!after.Complete && !after.Names.Contains(Name))
                {
                    Outcome = OUT_UNKNOWN;
                    Error = "The post-commit census could not read " + after.Unreadable + " parameter(s), and this one " +
                            "was not among the ones it did read. Whether it was added is UNKNOWN — one of the unreadable " +
                            "parameters may be it.";
                    return;
                }
                if (after.Names.Contains(Name)) { Outcome = OUT_CONFIRMED; return; }
                Outcome = OUT_NOT_WRITTEN;
                if (Error == null)
                    Error = "AddParameter did not throw and the parameter is NOT in the family after the commit. " +
                            "This is the failure the skill's `except System.Exception: pass` made invisible: the " +
                            "family ships without its PRD_ parameter and the report says OK.";
            }

            public JObject ToJson()
            {
                return new JObject
                {
                    ["name"] = Name,
                    ["instance"] = Instance,
                    ["group_requested"] = GroupSpec,
                    ["already_present"] = AlreadyPresent,
                    ["outcome"] = Outcome,
                    ["error"] = Error
                };
            }
        }

        private class ClearRow
        {
            public string Name;
            public string FormulaBefore;
            public string FormulaAfter;
            public string Error;
            public string Outcome;

            public void Judge(FamilyManager fm)
            {
                if (Error != null && Outcome == null) { Outcome = OUT_NOT_WRITTEN; return; }
                string why;
                var p = FindParam(fm, Name, out why);
                if (p == null)
                {
                    Outcome = OUT_UNKNOWN;
                    Error = "The parameter could not be re-resolved after the commit (" + why + "), so whether its " +
                            "formula is gone is UNKNOWN.";
                    return;
                }
                try { FormulaAfter = p.Formula; }
                catch (Exception ex)
                {
                    Outcome = OUT_UNKNOWN;
                    Error = "The formula could not be re-read after the commit: " + ex.Message + ". UNKNOWN.";
                    return;
                }
                if (FormulaAfter == null) Outcome = OUT_CONFIRMED;
                else
                {
                    Outcome = OUT_NOT_WRITTEN;
                    Error = "The formula is STILL there after the commit ('" + FormulaAfter + "'). Any value written " +
                            "to this parameter was refused by Revit, whatever the setter appeared to do.";
                }
            }

            public JObject ToJson()
            {
                return new JObject
                {
                    ["name"] = Name,
                    ["formula_before"] = FormulaBefore,
                    ["formula_after_read_from_model"] = FormulaAfter,
                    ["outcome"] = Outcome,
                    ["error"] = Error
                };
            }
        }

        private class RemoveRow
        {
            public string Name;
            public bool WasPresent;
            public string Error;
            public string Outcome;
            public string Source;   // "remove_params" | "junk_rules"
            public string JunkMatch;

            /// <summary>
            /// Re-reads fm.Parameters. RemoveParameter is void and Revit declines it for a
            /// referenced parameter — a call that did not throw is not a removal.
            /// </summary>
            public void Judge(Census before, Census after)
            {
                if (!WasPresent) { Outcome = OUT_NOTHING_TO_DO; return; }
                if (!after.Complete && after.Names.Contains(Name) == false)
                {
                    Outcome = OUT_UNKNOWN;
                    Error = "The post-commit census could not read " + after.Unreadable + " parameter(s). This one is " +
                            "not among the ones it read, but one of the unreadable ones may be it — whether it is gone " +
                            "is UNKNOWN.";
                    return;
                }
                if (!after.Names.Contains(Name)) { Outcome = OUT_CONFIRMED; return; }
                Outcome = OUT_NOT_WRITTEN;
                if (Error == null)
                    Error = "The parameter is STILL in the family after the commit. RemoveParameter did not remove it — " +
                            "typically because something references it, which is exactly when it must not be touched.";
            }

            public JObject ToJson()
            {
                return new JObject
                {
                    ["name"] = Name,
                    ["source"] = Source,
                    ["junk_pattern_matched"] = JunkMatch,
                    ["was_present_before"] = WasPresent,
                    ["outcome"] = Outcome,
                    ["error"] = Error
                };
            }
        }

        private class SetRow
        {
            public string Name;
            public JToken Requested;
            public string Storage;
            public bool IsInstance;
            public JObject Before;
            // The caller's value in the shape the model stores it, captured at apply time.
            // Comparing the read-back against another read of the same parameter compares
            // the model to itself and can never fail.
            public JObject Expected;
            // True on the SetValueString path, where Expected is itself a read: the row
            // then proves only that nothing drifted, never that Revit stored what the
            // string meant. It must not be counted as verified against the caller.
            public bool ExpectationFromModel;
            public JObject Written;    // read inside the transaction, after Regenerate
            public JObject ReadBack;   // read fresh, after the commit
            // Set when the transaction was rolled back. Written (and Expected, on the
            // SetValueString path) are reads taken INSIDE the transaction: the rollback
            // undid the value they read, so they are not in the family and this row is not
            // entitled to render them.
            public bool RolledBack;
            public bool SetterRan;
            public string How;
            public string Error;
            public string Outcome;

            public void Judge()
            {
                if (Error != null && !SetterRan) { Outcome = OUT_NOT_WRITTEN; return; }
                if (!SetterRan) { Outcome = OUT_NOT_WRITTEN; return; }

                if (!Readable(ReadBack) || !Readable(Expected))
                {
                    Outcome = OUT_UNKNOWN;
                    Error = "The transaction COMMITTED and this write cannot be verified either way: " +
                            (!Readable(Expected) ? Reason(Expected) : Reason(ReadBack)) +
                            " Whether the value is in the family is UNKNOWN — which is not the same as it being absent, " +
                            "and not the same as it being there. The commit is DONE.";
                    return;
                }
                if (!SameValue(ReadBack, Expected))
                {
                    Outcome = OUT_NOT_WRITTEN;
                    Error = "The transaction COMMITTED and the family does not hold what you asked for: the type reads " +
                            Show(ReadBack) + " and " + Show(Expected) + " was requested. FamilyManager.Set() returns " +
                            "VOID — it did not throw and it did not write. This is the failure the blob's " +
                            "`except System.Exception: pass` reported as 'OK -> path'.";
                    return;
                }
                if (Readable(Written) && !SameValue(Written, ReadBack))
                {
                    Outcome = OUT_UNKNOWN;
                    Error = "The family holds the requested value, but it CHANGED between the in-transaction read (" +
                            Show(Written) + ") and the post-commit read (" + Show(ReadBack) + "). Something else in " +
                            "this commit touched this parameter, so this write is not provably the author of the value.";
                    return;
                }
                Outcome = OUT_CONFIRMED;
            }

            public JObject ToJson(bool wrote)
            {
                return new JObject
                {
                    ["name"] = Name,
                    ["storage_type"] = Storage,
                    ["is_instance"] = IsInstance,
                    ["instance_note"] = IsInstance
                        ? (JToken)("This is an INSTANCE parameter: the value written is the family's DEFAULT for new " +
                                   "instances, not a value carried by instances already placed in a project.")
                        : null,
                    ["requested"] = Requested,
                    ["applied_via"] = How,
                    ["before"] = Before,
                    ["value_expected"] = Expected,
                    ["value_written"] = Written,
                    ["value_read_back"] = ReadBack,
                    ["value_written_note"] = RolledBack
                        ? (JToken)("value_written is null because the transaction was ROLLED BACK. It had been read " +
                                   "inside the transaction and the rollback UNDID the value it read, so there is no " +
                                   "value written: nothing about this row is in the family." +
                                   (ExpectationFromModel
                                       ? " value_expected is null for the same reason — on the SetValueString path it " +
                                         "is itself an in-transaction read, not your literal."
                                       : ""))
                        : null,
                    ["outcome"] = Outcome,
                    ["confirmed_against"] = wrote && Outcome == OUT_CONFIRMED
                        ? (JToken)(ExpectationFromModel ? "a_re_read_not_your_value" : "your_value")
                        : null,
                    ["confirmation_caveat"] = wrote && Outcome == OUT_CONFIRMED && ExpectationFromModel
                        ? (JToken)("Confirmed only in the weaker sense: SetValueString parsed your string's units " +
                                   "inside Revit and never returned the number, so value_expected is a re-read of the " +
                                   "parameter, not your value. Nothing compared '" +
                                   (Requested == null ? "" : Requested.ToString()) + "' against the family. Judge " +
                                   "value_read_back yourself.")
                        : null,
                    ["error"] = Error
                };
            }
        }

        // =====================================================================
        // Planning. Nothing here touches the model.
        // =====================================================================
        private bool BuildPlan(UIApplication app, Document doc, FamilyManager fm, JObject request,
                               string familyName, string keepTypeName, bool collapse, bool clearFormulas,
                               Plan plan, out string why)
        {
            why = null;

            // ---- Types ----
            var types = new List<FamilyType>();
            try { foreach (FamilyType ft in fm.Types) types.Add(ft); }
            catch (Exception ex) { why = "Could not enumerate the family's types: " + ex.Message + ". Nothing was written."; return false; }

            if (familyName != null)
            {
                if (familyName.Length == 0) { why = "family_name is empty. A family type cannot be named ''."; return false; }

                FamilyType keep = null;
                if (!string.IsNullOrEmpty(keepTypeName))
                {
                    keep = types.FirstOrDefault(t => string.Equals(SafeTypeName(t), keepTypeName, StringComparison.Ordinal));
                    if (keep == null)
                    {
                        why = "keep_type '" + keepTypeName + "' is not a type of this family (it has: " +
                              string.Join(", ", types.Select(t => "'" + (SafeTypeName(t) ?? "?") + "'").ToArray()) +
                              "). Guessing which type survives would delete the wrong geometry. Nothing was written.";
                        return false;
                    }
                }
                else
                {
                    keep = types.FirstOrDefault(t => string.Equals(SafeTypeName(t), familyName, StringComparison.Ordinal))
                           ?? types.FirstOrDefault();
                }

                plan.Keep = keep;
                string keepName = SafeTypeName(keep);

                if (collapse)
                {
                    foreach (var t in types)
                    {
                        var n = SafeTypeName(t);
                        if (keep != null && ReferenceEquals(t, keep)) continue;
                        if (n == null)
                        {
                            plan.PreRefused.Add(new JObject
                            {
                                ["name"] = null,
                                ["operation"] = "delete_type",
                                ["reason"] = "A type whose name cannot be read is not a type we are willing to delete: " +
                                             "we could not tell you which one it was afterwards."
                            });
                            continue;
                        }
                        plan.TypeDeletes.Add(new TypeDelete { Name = n });
                    }
                }

                plan.Rename = new RenameOp
                {
                    From = keepName,
                    To = familyName,
                    Created = keep == null,
                    Needed = keep == null || !string.Equals(keepName, familyName, StringComparison.Ordinal)
                };
            }

            // ---- Shared parameters from the SPF ----
            var addToken = request["add_shared_params"] as JArray;
            if (addToken != null && addToken.Count > 0)
            {
                var spf = request.Value<string>("spf_path");
                if (string.IsNullOrWhiteSpace(spf))
                { why = "add_shared_params was given without spf_path. A shared parameter must come from the official SPF; inventing a definition here would create a DIFFERENT parameter with the same name."; return false; }
                if (!File.Exists(spf))
                { why = "spf_path '" + spf + "' does not exist on disk. Nothing was written."; return false; }

                Dictionary<string, ExternalDefinition> defs;
                string spfWhy;
                if (!LoadSpf(app, spf, out defs, out spfWhy)) { why = spfWhy; return false; }

                var present = Census.Take(fm).Names;
                foreach (var tok in addToken)
                {
                    var o = tok as JObject;
                    if (o == null) { plan.PreRefused.Add(new JObject { ["operation"] = "add_shared_param", ["reason"] = "Entry is not an object." }); continue; }
                    var row = new AddRow
                    {
                        Name = o.Value<string>("name"),
                        Instance = o.Value<bool>("instance"),
                        GroupSpec = o.Value<string>("group") ?? "PG_DATA"
                    };
                    plan.Adds.Add(row);

                    if (string.IsNullOrWhiteSpace(row.Name)) { row.Error = "name is required."; row.Outcome = OUT_NOT_WRITTEN; continue; }
                    if (present.Contains(row.Name))
                    {
                        // Idempotence. Re-adding an existing parameter throws; and its
                        // group/instance-ness is the family's business now, not ours.
                        row.AlreadyPresent = true;
                        continue;
                    }
                    ExternalDefinition ed;
                    if (!defs.TryGetValue(row.Name, out ed))
                    {
                        row.Error = "No definition named '" + row.Name + "' in the SPF. It is not in the file, so there " +
                                    "is nothing to add — adding a lookalike would give the family a parameter with the " +
                                    "right name and the wrong GUID, which no schedule would ever match.";
                        row.Outcome = OUT_NOT_WRITTEN;
                        continue;
                    }
                    row.Def = ed;
                    ForgeTypeId g;
                    string gWhy;
                    if (!ResolveGroup(row.GroupSpec, out g, out gWhy))
                    {
                        // Never fall back to Data: the parameter would land in the wrong
                        // group and the row would still say added.
                        row.Error = gWhy;
                        row.Outcome = OUT_NOT_WRITTEN;
                        continue;
                    }
                    row.Group = g;
                }
            }

            // ---- Formula clears requested outright ----
            var clearList = request["clear_formulas_on"] as JArray;
            if (clearList != null)
            {
                foreach (var tok in clearList)
                {
                    var n = tok?.ToString();
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    PlanClear(fm, plan, n);
                }
            }

            // ---- Values ----
            var values = request["values"] as JObject;
            if (values != null)
            {
                foreach (var prop in values.Properties())
                {
                    var row = new SetRow { Name = prop.Name, Requested = prop.Value };
                    plan.Sets.Add(row);

                    string pWhy;
                    var p = FindParam(fm, prop.Name, out pWhy);
                    if (p == null) { row.Error = pWhy; continue; }

                    try
                    {
                        row.Storage = p.StorageType.ToString();
                        row.IsInstance = p.IsInstance;
                    }
                    catch (Exception ex) { row.Error = "This parameter's storage type could not be read: " + ex.Message; continue; }

                    bool ro;
                    try { ro = p.IsReadOnly; } catch (Exception ex) { row.Error = "IsReadOnly could not be read: " + ex.Message; continue; }
                    if (ro)
                    {
                        row.Error = "'" + prop.Name + "' is read-only in this family. A read-only parameter skipped in " +
                                    "silence is how a homologation reports OK and writes nothing.";
                        continue;
                    }

                    string formula = null;
                    try { formula = p.Formula; } catch { }
                    if (formula != null)
                    {
                        if (!clearFormulas)
                        {
                            row.Error = "'" + prop.Name + "' is driven by a formula ('" + formula + "') and Revit refuses " +
                                        "a value on it. clear_formulas=false, so this write was NOT attempted. Imported " +
                                        "families arrive with Description/Manufacturer/Material governed by a vendor " +
                                        "formula: clear it first or this value can never land.";
                            continue;
                        }
                        PlanClear(fm, plan, prop.Name);
                    }

                    row.Before = ReadFamilyValue(fm, SafeCurrentType(fm), prop.Name);
                }
            }

            // ---- Removals: the MPDT 'NA' set ----
            var removeList = request["remove_params"] as JArray;
            var censusNow = Census.Take(fm);
            if (removeList != null)
            {
                foreach (var tok in removeList)
                {
                    var n = tok?.ToString();
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    plan.Removals.Add(new RemoveRow
                    {
                        Name = n,
                        Source = "remove_params",
                        // An 'NA' for a parameter the family never had is documentation,
                        // not work. Reporting it as an error would bury the real ones.
                        WasPresent = censusNow.Names.Contains(n)
                    });
                }
            }

            // ---- Junk ----
            var junk = request["junk_rules"] as JObject;
            if (junk != null && junk.Value<bool>("enabled"))
            {
                var patterns = StringArray(junk["patterns"], DefaultJunk);
                var exclude = StringArray(junk["exclude"], DefaultExclude);
                var keep = new HashSet<string>(StringArray(junk["keep"], DefaultKeep), StringComparer.Ordinal);

                List<FamilyParameter> all;
                string enumWhy;
                if (!AllParams(fm, out all, out enumWhy)) { why = enumWhy; return false; }

                foreach (var p in all)
                {
                    string n; StorageType st; string f;
                    try
                    {
                        n = p.Definition.Name;
                        st = p.StorageType;
                        f = p.Formula;
                    }
                    catch (Exception ex)
                    {
                        // Already fatal via the census's Complete check, but say which one.
                        why = "A parameter could not be read while classifying junk (" + ex.Message + "). Nothing was " +
                              "written: a parameter we cannot read is one we cannot prove is not geometry.";
                        return false;
                    }

                    string match;
                    if (!IsJunk(n, st, f, patterns, exclude, keep, out match)) continue;
                    if (plan.Removals.Any(r => string.Equals(r.Name, n, StringComparison.Ordinal))) continue;
                    plan.Removals.Add(new RemoveRow { Name = n, Source = "junk_rules", JunkMatch = match, WasPresent = true });
                }
            }

            return true;
        }

        private static void PlanClear(FamilyManager fm, Plan plan, string name)
        {
            if (plan.FormulaClears.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal))) return;
            var row = new ClearRow { Name = name };
            plan.FormulaClears.Add(row);
            string why;
            var p = FindParam(fm, name, out why);
            if (p == null) { row.Error = why; row.Outcome = OUT_NOT_WRITTEN; return; }
            try { row.FormulaBefore = p.Formula; } catch (Exception ex) { row.Error = "The formula could not be read: " + ex.Message; row.Outcome = OUT_NOT_WRITTEN; return; }
            if (row.FormulaBefore == null)
            {
                // Nothing to clear. Idempotence: the second run of a cleaned family.
                row.Outcome = OUT_NOTHING_TO_DO;
                plan.FormulaClears.Remove(row);
            }
        }

        /// <summary>
        /// The conservative rule the skill already proves in production. Text only —
        /// Double is geometry and Integer is a behaviour flag; nothing with a formula;
        /// nothing that matches the behaviour/MEP/material veto (which starts with
        /// 'custom', because IsCustom moves geometry); nothing in the identity keep-list;
        /// and never a PRD_.
        /// </summary>
        private static bool IsJunk(string name, StorageType st, string formula, string[] patterns,
                                   string[] exclude, HashSet<string> keep, out string match)
        {
            match = null;
            if (name == null) return false;
            var nl = name.ToLowerInvariant();
            if (keep.Contains(nl)) return false;
            if (name.StartsWith("PRD_", StringComparison.Ordinal)) return false;
            if (st != StorageType.String) return false;
            if (formula != null) return false;
            foreach (var e in exclude) if (nl.IndexOf(e, StringComparison.Ordinal) >= 0) return false;
            foreach (var j in patterns)
                if (nl.IndexOf(j, StringComparison.Ordinal) >= 0) { match = j; return true; }
            return false;
        }

        // =====================================================================
        // Applying. Inside the transaction, in the order the skill proved.
        // =====================================================================
        private static void ApplyTypes(Document doc, Plan plan)
        {
            if (plan.Rename == null) return;
            var fm = doc.FamilyManager;

            if (plan.Rename.Created)
            {
                // A family with no types at all cannot be renamed into shape.
                try { plan.Keep = fm.NewType(plan.Rename.To); }
                catch (Exception ex) { plan.Rename.Error = "The family had no types and NewType('" + plan.Rename.To + "') failed: " + ex.Message; return; }
            }

            foreach (var d in plan.TypeDeletes)
            {
                FamilyType victim = null;
                try { foreach (FamilyType ft in fm.Types) if (string.Equals(ft.Name, d.Name, StringComparison.Ordinal)) { victim = ft; break; } }
                catch (Exception ex) { d.Error = "Could not re-find the type to delete: " + ex.Message; continue; }
                if (victim == null) { d.Error = "The type is no longer in the family (something else removed it)."; continue; }
                try
                {
                    fm.CurrentType = victim;
                    fm.DeleteCurrentType();
                }
                catch (Exception ex) { d.Error = "DeleteCurrentType threw: " + ex.Message; }
            }

            if (plan.Keep != null)
            {
                try { fm.CurrentType = plan.Keep; }
                catch (Exception ex) { plan.Rename.Error = "The surviving type could not be made current: " + ex.Message + ". No value can be written without it."; return; }
            }

            if (plan.Rename.Needed && !plan.Rename.Created)
            {
                try { fm.RenameCurrentType(plan.Rename.To); }
                catch (Exception ex) { plan.Rename.Error = "RenameCurrentType('" + plan.Rename.To + "') threw: " + ex.Message; }
            }
        }

        private static void ApplyAddShared(Document doc, Plan plan)
        {
            var fm = doc.FamilyManager;
            foreach (var a in plan.Adds)
            {
                if (a.Error != null || a.AlreadyPresent || a.Def == null || a.Group == null) continue;
                try { fm.AddParameter(a.Def, a.Group, a.Instance); }
                catch (Exception ex)
                {
                    // NOT `except: pass`. This is the exact call the blob swallowed.
                    a.Error = "AddParameter threw: " + ex.Message;
                }
            }
        }

        private static void ApplyClearFormulas(Document doc, Plan plan)
        {
            var fm = doc.FamilyManager;
            foreach (var c in plan.FormulaClears)
            {
                if (c.Error != null) continue;
                string why;
                var p = FindParam(fm, c.Name, out why);
                if (p == null) { c.Error = why; continue; }
                try { fm.SetFormula(p, null); }
                catch (Exception ex) { c.Error = "SetFormula(p, null) threw: " + ex.Message; }
            }
        }

        private static void ApplyValues(Document doc, Plan plan)
        {
            var fm = doc.FamilyManager;
            var ft = SafeCurrentType(fm);
            foreach (var r in plan.Sets)
            {
                if (r.Error != null) continue;
                if (ft == null)
                {
                    r.Error = "The family has no current type, so there is nowhere to write a value.";
                    continue;
                }
                string why;
                var p = FindParam(fm, r.Name, out why);
                if (p == null) { r.Error = why; continue; }

                try
                {
                    string applyWhy;
                    if (!TryApply(fm, p, r, out applyWhy)) { r.Error = applyWhy; continue; }
                }
                catch (Exception ex)
                {
                    // FamilyManager.Set() returns void: throwing is the ONLY signal it
                    // gives, and the blob threw that away too.
                    r.Error = "The setter threw: " + ex.Message;
                    r.SetterRan = false;
                }
            }
        }

        private static void ApplyRemovals(Document doc, Plan plan)
        {
            var fm = doc.FamilyManager;
            foreach (var r in plan.Removals)
            {
                if (!r.WasPresent) continue;
                string why;
                var p = FindParam(fm, r.Name, out why);
                if (p == null) { r.Error = "The parameter could not be resolved to remove it: " + why; continue; }
                try { fm.RemoveParameter(p); }
                catch (Exception ex)
                {
                    // Revit declines a referenced parameter, and referenced is exactly when
                    // it must not go. Reported, never counted.
                    r.Error = "RemoveParameter was refused: " + ex.Message + ". Revit refuses a parameter that is " +
                              "referenced (by a formula, a label or geometry) — that refusal is protecting the family.";
                }
            }
        }

        /// <summary>
        /// Coerce and write. `expected` is the load-bearing output: the caller's value in
        /// the shape the family stores it, captured HERE, the only place that still knows
        /// what was asked for. Verify against a second read of the parameter instead and
        /// the check passes by construction — Revit ignoring the value moves both reads
        /// together.
        /// </summary>
        private static bool TryApply(FamilyManager fm, FamilyParameter p, SetRow r, out string why)
        {
            why = null;
            var v = r.Requested;
            bool isNull = v == null || v.Type == JTokenType.Null;
            var st = p.StorageType;

            switch (st)
            {
                case StorageType.String:
                    if (isNull) { why = "Storage is String and the value is null. Use \"\" to clear it — null and empty are not the same request."; return false; }
                    r.How = "FamilyManager.Set(string)";
                    string sv = TokenText(v);
                    fm.Set(p, sv);
                    r.SetterRan = true;
                    r.Expected = Literal(st, sv);
                    return true;

                case StorageType.Integer:
                    if (isNull) { why = "Storage is Integer; null is not a value it can hold."; return false; }
                    if (v.Type == JTokenType.Boolean)
                    {
                        r.How = "FamilyManager.Set(int) [yes/no]";
                        int bv = v.Value<bool>() ? 1 : 0;
                        fm.Set(p, bv);
                        r.SetterRan = true;
                        r.Expected = Literal(st, bv);
                        return true;
                    }
                    if (v.Type == JTokenType.Integer)
                    {
                        long l = v.Value<long>();
                        if (l < int.MinValue || l > int.MaxValue) { why = "Value " + l + " does not fit an Integer parameter."; return false; }
                        r.How = "FamilyManager.Set(int)";
                        fm.Set(p, (int)l);
                        r.SetterRan = true;
                        r.Expected = Literal(st, (int)l);
                        return true;
                    }
                    if (v.Type == JTokenType.Float) { why = "Storage is Integer but the value is fractional. Round it deliberately; silently truncating a number someone bills is not our call."; return false; }
                    if (v.Type == JTokenType.String)
                    {
                        r.How = "FamilyManager.SetValueString(string) [unit-aware]";
                        fm.SetValueString(p, TokenText(v));
                        r.SetterRan = true;
                        r.Expected = ReadFamilyValue(fm, SafeCurrentType(fm), r.Name);
                        r.ExpectationFromModel = true;
                        return true;
                    }
                    why = "Storage is Integer; cannot coerce a " + v.Type + ".";
                    return false;

                case StorageType.Double:
                    if (isNull) { why = "Storage is Double; null is not a value it can hold."; return false; }
                    if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float)
                    {
                        r.How = "FamilyManager.Set(double) [raw, Revit internal units]";
                        double dv = v.Value<double>();
                        fm.Set(p, dv);
                        r.SetterRan = true;
                        r.Expected = Literal(st, dv);
                        return true;
                    }
                    if (v.Type == JTokenType.String)
                    {
                        r.How = "FamilyManager.SetValueString(string) [unit-aware]";
                        fm.SetValueString(p, TokenText(v));
                        r.SetterRan = true;
                        // Revit parsed the units internally and never handed the number
                        // back, so the only expectation it can be held to is a read of
                        // itself — taken now, before Regenerate can move it.
                        r.Expected = ReadFamilyValue(fm, SafeCurrentType(fm), r.Name);
                        r.ExpectationFromModel = true;
                        return true;
                    }
                    why = "Storage is Double; cannot coerce a " + v.Type + ".";
                    return false;

                case StorageType.ElementId:
                    long idv;
                    if (isNull) idv = -1;
                    else if (v.Type == JTokenType.Integer) idv = v.Value<long>();
                    else if (v.Type == JTokenType.String && long.TryParse(TokenText(v), out idv)) { }
                    else { why = "Storage is ElementId; it takes an element id (or null / -1 to clear), not a " + v.Type + "."; return false; }
                    if (!RevitCompat.CanRepresentElementId(idv)) { why = RevitCompat.ElementIdRangeError(idv); return false; }
                    r.How = "FamilyManager.Set(ElementId)";
                    var eid = RevitCompat.ToElementId(idv);
                    fm.Set(p, eid);
                    r.SetterRan = true;
                    // ReadFamilyValue renders ElementId storage as ElementId.ToString();
                    // expect the same rendering or every ElementId row would read as drift.
                    r.Expected = Literal(st, eid.ToString());
                    return true;

                default:
                    why = "Storage type is " + st + "; there is nothing to write.";
                    return false;
            }
        }

        private static JObject Literal(StorageType st, JToken value)
        {
            return new JObject
            {
                ["readable"] = true,
                ["storage"] = st.ToString(),
                ["value"] = value,
                ["expectation_source"] = "the exact value passed to FamilyManager.Set() — what the caller asked for."
            };
        }

        // =====================================================================
        // Reading. "I could not look" is a DISTINCT value from "it is empty".
        // =====================================================================
        private static JObject ReadFamilyValue(FamilyManager fm, FamilyType ft, string name)
        {
            string why;
            var p = FindParam(fm, name, out why);
            if (p == null)
                return new JObject { ["readable"] = false, ["error"] = "The parameter could not be resolved: " + why };
            return ReadFamilyValue(ft, p);
        }

        private static JObject ReadFamilyValue(FamilyType ft, FamilyParameter p)
        {
            if (ft == null)
                return new JObject { ["readable"] = false, ["error"] = "The family has no current type to read a value from. This is NOT the same as the value being empty." };
            try
            {
                var o = new JObject { ["readable"] = true, ["storage"] = p.StorageType.ToString() };
                switch (p.StorageType)
                {
                    case StorageType.String:
                        o["value"] = ft.AsString(p);
                        break;
                    case StorageType.Integer:
                        int? iv = ft.AsInteger(p);
                        o["value"] = iv.HasValue ? (JToken)iv.Value : JValue.CreateNull();
                        break;
                    case StorageType.Double:
                        double? dv = ft.AsDouble(p);
                        o["value"] = dv.HasValue ? (JToken)dv.Value : JValue.CreateNull();
                        break;
                    case StorageType.ElementId:
                        var e = ft.AsElementId(p);
                        o["value"] = e == null ? null : e.ToString();
                        break;
                    default:
                        o["value"] = null;
                        break;
                }
                try { o["has_value"] = ft.HasValue(p); } catch { }
                try { o["text"] = ft.AsValueString(p); } catch { o["text"] = null; }
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

        // Unreadable never equals anything, including itself: an unknown that compares
        // equal is how "I could not look" becomes "it matches".
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
                // Bit-equality would report drift on the last ulp of a unit parse and send
                // an honest write to a rollback.
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

        // =====================================================================
        // Parameter lookup. A name matching twice is an error, never a guess.
        // =====================================================================
        private static FamilyParameter FindParam(FamilyManager fm, string spec, out string why)
        {
            why = null;
            if (fm == null) { why = "no FamilyManager."; return null; }
            if (string.IsNullOrWhiteSpace(spec)) { why = "the parameter name is empty."; return null; }
            spec = spec.Trim();

            // A BuiltInParameter lookup that THREW did not come back empty. Keep the
            // reason so the final error cannot claim we looked and found nothing.
            string bipError = null;
            if (char.IsLetter(spec[0]))
            {
                BuiltInParameter bip;
                if (Enum.TryParse(spec, false, out bip))
                {
                    FamilyParameter bp = null;
                    try { bp = fm.get_Parameter(bip); }
                    catch (Exception ex) { bipError = ex.Message; }
                    if (bp != null) return bp;
                }
            }

            var hits = new List<FamilyParameter>();
            int unreadable = 0;
            string unreadableError = null;
            try
            {
                foreach (FamilyParameter p in fm.Parameters)
                {
                    string n;
                    try { n = p.Definition.Name; }
                    catch (Exception ex)
                    {
                        // A parameter whose name throws cannot be ruled out as a second
                        // match. Skipping it lets a real ambiguity resolve to one hit and
                        // ship as a fact.
                        unreadable++;
                        if (unreadableError == null) unreadableError = ex.Message;
                        continue;
                    }
                    if (string.Equals(n, spec, StringComparison.Ordinal)) hits.Add(p);
                }
            }
            catch (Exception ex) { why = "the family's parameters could not be enumerated: " + ex.Message; return null; }

            if (hits.Count > 1)
            {
                why = "'" + spec + "' matches " + hits.Count + " parameters in this family. Picking one would be a guess " +
                      "reported as a fact.";
                return null;
            }
            if (hits.Count == 1)
            {
                if (unreadable > 0)
                {
                    why = "'" + spec + "' matched 1 parameter, but " + unreadable + " other parameter(s) could not be " +
                          "read by name" + (unreadableError == null ? "" : " (first failure: " + unreadableError + ")") +
                          ", so a unique match cannot be proven — one of them may carry the same name, and writing the " +
                          "wrong one is indistinguishable from writing the right one in this report.";
                    return null;
                }
                return hits[0];
            }

            why = "no parameter named '" + spec + "' exists in this family" +
                  (unreadable > 0 ? " among the " + unreadable + " that could be read" : "") +
                  (bipError == null ? "" : " (the BuiltInParameter lookup did not come back empty — it FAILED: " + bipError + ")") +
                  ".";
            return null;
        }

        private static bool AllParams(FamilyManager fm, out List<FamilyParameter> list, out string why)
        {
            why = null;
            list = new List<FamilyParameter>();
            try { foreach (FamilyParameter p in fm.Parameters) list.Add(p); }
            catch (Exception ex) { why = "The family's parameters could not be enumerated: " + ex.Message + ". Nothing was written."; return false; }
            return true;
        }

        // =====================================================================
        // The SPF and the parameter group.
        // =====================================================================
        /// <summary>
        /// Reads the SPF's definitions by name. Application.SharedParametersFilename is
        /// global session state: leaving it pointed at our SPF would silently change what
        /// the user's next manual "add shared parameter" picks up, so it is restored.
        /// </summary>
        private static bool LoadSpf(UIApplication app, string spfPath, out Dictionary<string, ExternalDefinition> defs, out string why)
        {
            defs = new Dictionary<string, ExternalDefinition>(StringComparer.Ordinal);
            why = null;
            string original = null;
            try { original = app.Application.SharedParametersFilename; } catch { }
            try
            {
                app.Application.SharedParametersFilename = spfPath;
                var file = app.Application.OpenSharedParameterFile();
                if (file == null)
                {
                    why = "Revit could not open '" + spfPath + "' as a shared parameter file. Nothing was written.";
                    return false;
                }
                foreach (DefinitionGroup g in file.Groups)
                    foreach (Definition d in g.Definitions)
                    {
                        var ed = d as ExternalDefinition;
                        if (ed == null) continue;
                        // A name that appears twice in the SPF is two different GUIDs. Taking
                        // the first would bind the family to whichever one the file happened
                        // to list first.
                        if (defs.ContainsKey(ed.Name))
                        {
                            why = "The SPF defines '" + ed.Name + "' more than once (different GUIDs under different " +
                                  "groups). Adding one of them would be a coin toss the report would present as a fact. " +
                                  "Nothing was written; fix the SPF.";
                            return false;
                        }
                        defs[ed.Name] = ed;
                    }
                return true;
            }
            catch (Exception ex)
            {
                why = "Reading the shared parameter file '" + spfPath + "' failed: " + ex.Message + ". Nothing was written.";
                return false;
            }
            finally
            {
                try { if (original != null) app.Application.SharedParametersFilename = original; } catch { }
            }
        }

        // GroupTypeId's properties, by normalized name and by ForgeTypeId string. Built by
        // reflection because the BuiltInParameterGroup enum this maps from is deprecated in
        // the versions this file must compile against — and hard-coding two entries would
        // silently reject every group the caller has not thought of yet.
        private static Dictionary<string, ForgeTypeId> _groups;
        private static readonly object _groupsLock = new object();

        private static bool ResolveGroup(string spec, out ForgeTypeId group, out string why)
        {
            group = null; why = null;
            if (string.IsNullOrWhiteSpace(spec)) { why = "The parameter group is empty."; return false; }
            var map = GroupMap();
            var key = NormalizeGroup(spec);
            if (map.TryGetValue(key, out group)) return true;
            why = "'" + spec + "' is not a parameter group this Revit knows (tried it as a PG_ name, a GroupTypeId " +
                  "name and a full ForgeTypeId). The parameter was NOT added: filing it under a fallback group would " +
                  "put it in the wrong place in every schedule and property palette, and this report would still say " +
                  "it was added.";
            return false;
        }

        private static string NormalizeGroup(string s)
        {
            var t = s.Trim();
            if (t.StartsWith("PG_", StringComparison.OrdinalIgnoreCase)) t = t.Substring(3);
            return t.Replace("_", "").Replace(" ", "").ToLowerInvariant();
        }

        private static Dictionary<string, ForgeTypeId> GroupMap()
        {
            lock (_groupsLock)
            {
                if (_groups != null) return _groups;
                var m = new Dictionary<string, ForgeTypeId>(StringComparer.Ordinal);
                foreach (var pi in typeof(GroupTypeId).GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    if (pi.PropertyType != typeof(ForgeTypeId)) continue;
                    ForgeTypeId v;
                    try { v = pi.GetValue(null, null) as ForgeTypeId; }
                    catch { continue; }
                    if (v == null) continue;
                    m[pi.Name.ToLowerInvariant()] = v;
                    try { m[v.TypeId.ToLowerInvariant()] = v; } catch { }
                }
                _groups = m;
                return _groups;
            }
        }

        // =====================================================================
        // Saving. A file on disk is a claim, and it needs its own evidence.
        // =====================================================================
        private static JObject SaveSkipped(string reason)
        {
            return new JObject
            {
                ["saved"] = false,
                ["saved_path"] = null,
                ["reason"] = reason
            };
        }

        /// <summary>
        /// doc.Save(), then prove it: the path must exist AND the bytes must read back as
        /// an actual OLE compound file (every .rfa is one). File.Exists alone would be
        /// satisfied by the file that was already there before this run — which is exactly
        /// what "OK -> path" claimed about families it never wrote.
        /// </summary>
        private static JObject SaveAndVerify(Document doc)
        {
            string path;
            try
            {
                doc.Save();
                path = doc.PathName;
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["saved"] = false,
                    ["saved_path"] = null,
                    ["reason"] = "doc.Save() threw: " + ex.Message + ". The COMMIT already happened, so the family in " +
                                 "memory carries the changes and the file on disk does NOT. Do not close it without saving."
                };
            }

            if (string.IsNullOrEmpty(path))
                return new JObject
                {
                    ["saved"] = false,
                    ["saved_path"] = null,
                    ["reason"] = "doc.Save() returned without throwing but the document has no PathName, so there is no " +
                                 "file to point at and nothing to verify. Not reported as saved."
                };

            bool exists;
            try { exists = File.Exists(path); }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["saved"] = false,
                    ["saved_path"] = null,
                    ["reason"] = "The save did not throw, but the path could not be checked on disk (" + ex.Message +
                                 "). Whether the file was written is UNKNOWN — not reported as saved."
                };
            }
            if (!exists)
                return new JObject
                {
                    ["saved"] = false,
                    ["saved_path"] = null,
                    ["reason"] = "doc.Save() did not throw and '" + path + "' is NOT on disk. Nothing was saved."
                };

            long size; DateTime written; string magicWhy;
            if (!ReadsBack(path, out size, out written, out magicWhy))
                return new JObject
                {
                    ["saved"] = false,
                    ["saved_path"] = null,
                    ["reason"] = "The file exists but does not read back as a Revit family: " + magicWhy +
                                 " Existence is not evidence — not reported as saved."
                };

            return new JObject
            {
                ["saved"] = true,
                ["saved_path"] = path,
                ["file_size_bytes"] = size,
                ["last_write_time_utc"] = written.ToUniversalTime().ToString("o"),
                ["verified_by"] = "File.Exists AND re-reading the file's header off disk: it is a real OLE compound " +
                                  "file (D0 CF 11 E0 ...), which every .rfa is.",
                ["backup_files_now_on_disk"] = new JArray(Backups(path).Select(b => (JToken)b)),
                ["note"] = "Every Save over an existing .rfa leaves a 'name.000N.rfa' backup next to it. They are " +
                           "listed here because the skill's rule is to delete them all at the end of the batch — and " +
                           "the single-digit glob '.0001' misses the ones a second save made."
            };
        }

        /// <summary>Reads the file's own bytes back. .rfa/.rvt are OLE2 compound files.</summary>
        private static bool ReadsBack(string path, out long size, out DateTime written, out string why)
        {
            size = 0; written = default(DateTime); why = null;
            try
            {
                var fi = new FileInfo(path);
                size = fi.Length;
                written = fi.LastWriteTimeUtc;
                if (size < 8) { why = "it is " + size + " bytes long."; return false; }
                var head = new byte[8];
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int got = fs.Read(head, 0, 8);
                    if (got < 8) { why = "only " + got + " of its first 8 bytes could be read."; return false; }
                }
                byte[] ole = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
                for (int i = 0; i < 8; i++)
                    if (head[i] != ole[i]) { why = "its header is not an OLE compound file's."; return false; }
                return true;
            }
            catch (Exception ex) { why = "reading it threw: " + ex.Message; return false; }
        }

        private static List<string> Backups(string rfaPath)
        {
            var list = new List<string>();
            try
            {
                var dir = Path.GetDirectoryName(rfaPath);
                var stem = Path.GetFileNameWithoutExtension(rfaPath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return list;
                foreach (var f in Directory.GetFiles(dir, stem + ".????.rfa"))
                {
                    var tail = Path.GetFileNameWithoutExtension(f);
                    int dot = tail.LastIndexOf('.');
                    if (dot < 0) continue;
                    var num = tail.Substring(dot + 1);
                    if (num.Length == 4 && num.All(char.IsDigit)) list.Add(f);
                }
            }
            catch { /* a directory we cannot list is not a claim we make: the list stays as far as it got */ }
            return list;
        }

        // =====================================================================
        // Responses and prose. Tense is a claim; it follows the outcome.
        // =====================================================================
        private static JObject RolledBackResponse(Document doc, string docPath, Census before, Plan plan,
                                                  string txStatus, string invariantStatus, Census afterInTx, string reason)
        {
            // Nothing reached the family, so no row may render a claim about it. Rows are
            // reported as what they WOULD have done, and the counts are all zero — not
            // because we counted, but because Revit undid the transaction.
            //
            // Written was read INSIDE the transaction, before the rollback decision, and
            // ReadBack/Expected on the SetValueString path are in-transaction reads too.
            // The rollback undid every value they read. Emitting them under a field named
            // `value_written`, with readable:true and the value in it, is this object
            // contradicting its own note in the same breath — so they are cleared here,
            // at the one place that knows the rollback happened, before ToJson can render
            // them.
            foreach (var s in plan.Sets)
            {
                s.RolledBack = true;
                s.Written = null;
                s.ReadBack = null;
                if (s.ExpectationFromModel) s.Expected = null;
            }

            return new JObject
            {
                ["mode"] = "apply",
                ["transaction_status"] = txStatus,
                ["document"] = SafeTitle(doc),
                ["document_path"] = docPath,
                ["family_untouched"] = true,
                ["geometry_invariant"] = InvariantJson(invariantStatus, before, afterInTx, reason),
                ["type_name_after"] = SafeCurrentTypeName(doc),
                ["prd_count_before"] = before.PrdCount,
                ["prd_count_after"] = before.PrdCount,
                ["params_added"] = new JArray(),
                ["params_added_confirmed"] = 0,
                ["params_set"] = new JArray(plan.Sets.Select(s => (JToken)s.ToJson(false))),
                ["params_set_confirmed"] = 0,
                ["params_removed"] = new JArray(),
                ["params_removed_count"] = 0,
                ["formulas_cleared"] = new JArray(),
                ["formulas_cleared_count"] = 0,
                ["types_deleted"] = new JArray(),
                ["types_deleted_count"] = 0,
                ["params_skipped"] = new JArray(plan.Skipped().Select(s => (JToken)s)),
                ["saved"] = SaveSkipped("the transaction was rolled back: there is nothing to save, and saving would " +
                                        "overwrite the last good copy of the family with a no-op at best."),
                ["note"] = reason + " The family is EXACTLY as it was: nothing was added, set, removed or renamed, " +
                           "including the parts that worked, and the file was not saved. Do not re-run this family " +
                           "blindly — the plan that produced this is the plan that would produce it again."
            };
        }

        private static string FinalNote(string invariant, int confirmed, int failed, int unknown, Plan plan)
        {
            var parts = new List<string>();
            if (invariant == "violated_after_commit" || invariant == "unknown_after_commit")
                parts.Add("READ geometry_invariant.warning FIRST: the commit is done and the geometry check does not " +
                          "hold on the post-commit read. Stop the batch.");
            if (failed > 0)
                parts.Add(failed + " parameter(s) are NOT written: the family was re-read after the commit and does not " +
                          "carry the value.");
            if (unknown > 0)
                parts.Add(unknown + " parameter(s) are UNKNOWN — the setter ran, the commit is done, and the value could " +
                          "not be read back to settle it. They are counted as neither written nor failed.");
            var addFail = plan.Adds.Count(a => a.Outcome == OUT_NOT_WRITTEN);
            if (addFail > 0)
                parts.Add(addFail + " shared parameter(s) are NOT in the family after the commit. A family missing its " +
                          "PRD_ set is not homologated, whatever else here says.");
            var rmFail = plan.Removals.Count(r => r.Outcome == OUT_NOT_WRITTEN);
            if (rmFail > 0)
                parts.Add(rmFail + " parameter(s) are still present after RemoveParameter — usually because they are " +
                          "referenced, which is when they must stay.");
            var typeDelFail = plan.TypeDeletes.Count(t => t.Outcome != OUT_CONFIRMED);
            if (typeDelFail > 0)
                parts.Add(typeDelFail + " surplus type(s) are STILL in the family after the commit — the collapse did " +
                          "NOT happen. See types_delete_failed; a family that still carries its surplus types is not " +
                          "homologated, whatever the value rows say.");
            var clearFail = plan.FormulaClears.Count(f => f.Outcome == OUT_NOT_WRITTEN);
            if (clearFail > 0)
                parts.Add(clearFail + " formula(s) are STILL on their parameter after the commit. A surviving formula " +
                          "means Revit REFUSED the paired value write in 'values' — whatever that row says about " +
                          "itself, because the setter returns void and cannot tell it otherwise.");
            var clearUnknown = plan.FormulaClears.Count(f => f.Outcome == OUT_UNKNOWN);
            if (clearUnknown > 0)
                parts.Add(clearUnknown + " formula(s) could not be re-read after the commit: whether they are gone is " +
                          "UNKNOWN, and so is whether the paired value write was refused.");
            // Fires on anything that is not a CONFIRMED rename, not just OUT_NOT_WRITTEN:
            // a rename whose apply threw used to leave Outcome null and slip past here.
            if (plan.Rename != null && plan.Rename.Needed && plan.Rename.Outcome != OUT_CONFIRMED)
                parts.Add("The surviving type is NOT confirmed to be named family_name: " +
                          (plan.Rename.Error ?? "outcome " + (plan.Rename.Outcome ?? "was never recorded")) +
                          " The MPDT loader matches Family Name = Type Name; this family will not match.");
            if (parts.Count == 0) return null;
            parts.Add(confirmed + " value(s) confirmed by a fresh read after the commit. The commit is DONE, so this " +
                      "family may now be partially homologated — see each row's 'error'.");
            return string.Join(" ", parts.ToArray());
        }

        private static string ParseOnlyNote(int parseOnly)
        {
            if (parseOnly == 0) return null;
            return parseOnly + " of the confirmed value(s) were applied with SetValueString (a STRING on Double/Integer " +
                   "storage). Their expectation is a re-read of the parameter taken right after the setter, NOT your " +
                   "literal — Revit parsed the units internally and never returned the number. For those rows " +
                   "'confirmed' proves the value did not drift; it CANNOT prove Revit stored what your string meant, " +
                   "because if Revit stored nothing the expectation would be that nothing too and the row would still " +
                   "confirm. Pass a NUMBER in Revit internal units (feet) to get an intent-verified row, or judge " +
                   "value_read_back yourself.";
        }

        private static string UnknownNote(int unknown)
        {
            if (unknown == 0) return null;
            return unknown + " value(s) whose written state could not be established are counted HERE and in neither " +
                   "params_set_confirmed nor params_set_failed. The setter ran and the commit is done, but the " +
                   "parameter could not be re-read, so 'the value is in the family' and 'the value is not in the " +
                   "family' are both unproven. Counting these as failed would publish 'I could not look' as 'it is " +
                   "absent'; counting them as written would be the lie this handler exists to prevent.";
        }

        // =====================================================================
        // Small, boring, and each one honest about failing.
        // =====================================================================
        private static string[] StringArray(JToken t, string[] fallback)
        {
            var a = t as JArray;
            if (a == null || a.Count == 0) return fallback;
            return a.Select(x => (x == null ? "" : x.ToString()).ToLowerInvariant())
                    .Where(s => s.Length > 0).ToArray();
        }

        private static string TokenText(JToken v)
        {
            return v.Type == JTokenType.String ? v.Value<string>() : v.ToString();
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafePathName(Document d) { try { return d.PathName; } catch { return null; } }
        private static string SafeTypeName(FamilyType t) { try { return t == null ? null : t.Name; } catch { return null; } }

        private static FamilyType SafeCurrentType(FamilyManager fm)
        {
            try { return fm == null ? null : fm.CurrentType; } catch { return null; }
        }

        private static string SafeCurrentTypeName(Document d)
        {
            try { return SafeTypeName(d.FamilyManager?.CurrentType); } catch { return null; }
        }

        private static string SafeCategory(Document d)
        {
            try { return d.OwnerFamily?.FamilyCategory?.Name; } catch { return null; }
        }

        /// <summary>
        /// A Revit modal here does not wait for a human — nobody is looking at Revit — it
        /// blocks the bridge until the 30 s cut, and the caller retries a family that may
        /// already be half done. Errors are still resolved/rolled back by Revit; we only
        /// refuse to be asked.
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
    }
}
