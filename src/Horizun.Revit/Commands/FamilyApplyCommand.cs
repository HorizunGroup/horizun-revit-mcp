// -----------------------------------------------------------------------------
// Horizun MCP â€” original Horizun code.
//
// horizun_family_apply â€” the whole homologation of ONE .rfa, in ONE transaction.
//
// This replaces an earlier scripted approach that ran over a large family
// library, 2-4 families per call because the bridge cuts at 30 s, and it ended
// with:
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
//     void too, and Revit refuses it for referenced parameters; the earlier
//     script's "borrados 12" is a count of calls that did not throw.
//   * params_added likewise: the AddParameter that silently did nothing is the
//     same lie, and it is the one that ships a family missing its parameter set.
//
// THE GEOMETRY INVARIANT â€” the reason this is a handler and not a script.
//
// The earlier script already knows the rule. Its own comments say "Verifica que
// el conteo de params Double NO cambie" and "IsCustom mueve geometria!". But it
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
// that passes because we could not look is worse than no invariant at all â€” it
// is a guarantee the caller will believe.
//
// THIS HANDLER NEVER OPENS A FILE. It operates on the ACTIVE family document.
// Opening a 2025 .rfa from Revit 2026 upgrades it irreversibly and breaks the
// family catalog â€” that decision belongs to the caller and to
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

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public class FamilyApplyCommand : ICommand
    {
        public string Name => "horizun_family_apply";

        public string Description =>
            "Homologate the ACTIVE family document (.rfa) in ONE transaction: collapse surplus types down to one and " +
            "rename it to family_name, add missing shared parameters from an SPF (respecting instance/type and the " +
            "parameter group), clear formulas on the parameters about to be written (a formula-driven parameter refuses " +
            "a value), set values, remove named parameters (the caller's parameter spec's 'NA' entries), and strip " +
            "vendor junk under the conservative rule: String storage, no formula, matches a junk pattern, not " +
            "excluded, not kept, not in the caller-supplied protected prefix (protected_prefix). THE GEOMETRY " +
            "INVARIANT IS ENFORCED, NOT LOGGED: the count of Double parameters and the presence of IsCustom are captured " +
            "before, re-enumerated fresh after the writes, and if either changed â€” or if either census could not be read " +
            "completely â€” the WHOLE transaction is rolled back and the family is left untouched. Every reported field is a " +
            "fresh read of the family document after the commit: params_set reports value_written vs value_read_back and a " +
            "mismatch is a failure, type_name_after comes from fm.CurrentType.Name, params_added/params_removed are counted " +
            "by re-reading fm.Parameters and never by counting calls that did not throw (FamilyManager.Set and " +
            "RemoveParameter return void â€” there is not even a bool to check). Never opens a file: rfa_path is a guard that " +
            "must match the active document, because opening a 2025 .rfa in Revit 2026 upgrades it irreversibly. " +
            "SEPARATELY, the SHAPE is measured: bounding box, solid volume, surface area, solid count and connector positions of the ACTIVE family type are captured before and compared after, and reported in geometry_check as unchanged / unchanged_where_measured / changed. Only the active type is measured, because activating another type to measure it would itself modify the file - the others are listed as not verified rather than assumed intact. Idempotent: a second run reports nothing to do, not an error. Use dry_run=true to see the plan without a " +
            "transaction.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""rfa_path"": { ""type"": ""string"", ""description"": ""GUARD, not an instruction to open anything. If given, the run aborts unless it resolves to the ACTIVE family document's PathName. This handler never calls OpenDocumentFile: opening a 2025 .rfa from Revit 2026 upgrades the file irreversibly and breaks the family catalog. Open the family yourself (or via horizun_document_session) in the right Revit, then pass its path here to prove this is the one."" },
    ""expected_revit_version"": { ""type"": ""string"", ""description"": ""GUARD, e.g. '2025'. Aborts unless the running Revit reports this VersionNumber. The families are 2025; saving one from 2026 upgrades it with no way back."" },
    ""family_name"": { ""type"": ""string"", ""description"": ""The canonical Family Name (no .rfa). Given, the family is collapsed to exactly ONE type named this. Omitted, no type is created, deleted or renamed."" },
    ""keep_type"": { ""type"": ""string"", ""description"": ""Which existing type survives the collapse. Default: the one already named family_name, else the first. Every other type is deleted, so name it when the family carries real different sizes â€” those must be split into one family per size BEFORE this runs, not collapsed here."" },
    ""collapse_types"": { ""type"": ""boolean"", ""default"": false, ""description"": ""With family_name set: delete the surplus types. false renames the surviving/current type only and leaves the others alone."" },
    ""spf_path"": { ""type"": ""string"", ""description"": ""Your shared parameter file (the .txt Revit exports for a Shared Parameter File) to take add_shared_params from. The app's SharedParametersFilename is restored afterwards."" },
    ""add_shared_params"": {
      ""type"": ""array"",
      ""description"": ""Shared parameters to add if missing. A parameter already present is left exactly as it is (idempotence), never re-added."",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""name""],
        ""properties"": {
          ""name"": { ""type"": ""string"", ""description"": ""Definition name as it reads in the SPF."" },
          ""instance"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = instance parameter, carrying its own value per placed element. false (default) = type parameter, one value shared by every instance of the type. Pick instance only for values that must vary per occurrence."" },
          ""group"": { ""type"": ""string"", ""default"": ""PG_DATA"", ""description"": ""Parameter group: 'PG_DATA', 'PG_IDENTITY_DATA', a GroupTypeId name ('Data', 'IdentityData'), or a full group ForgeTypeId. A group that cannot be resolved is an ERROR for that row â€” never a silent fallback to Data, which would file the parameter in the wrong place and report success."" }
        }
      }
    },
    ""values"": { ""type"": ""object"", ""description"": ""{ parameter_name: value }. Set on the surviving type. String | number | boolean | null. A number on Double/Integer storage is raw Revit internal units; a STRING on Double/Integer goes through SetValueString (unit-aware) and can only be confirmed against a re-read of itself â€” those rows are reported separately and never claimed as verified against your value."" },
    ""clear_formulas"": { ""type"": ""boolean"", ""default"": true, ""description"": ""SetFormula(p, null) on a parameter in 'values' that is driven by a formula, BEFORE writing it. Imported families arrive with Description/Manufacturer/Material governed by a vendor formula, and Revit refuses a value on those ('Cannot set the value of a parameter determined by a formula'). false = such a row is refused and reported, never silently skipped."" },
    ""clear_formulas_on"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Extra parameter names to clear the formula of even though no value is written to them."" },
    ""remove_params"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Parameters to delete by name â€” typically the caller's parameter spec's 'NA' entries. A name that is not in the family is 'nothing to do', not an error (idempotence). Revit refuses to remove a referenced parameter: that is reported as skipped with Revit's reason, never counted as removed."" },
    ""junk_rules"": {
      ""type"": ""object"",
      ""description"": ""Vendor metadata stripping (BIMobject/manufacturer families arrive with dozens â€” a Caleffi valve had 70). Off unless enabled."",
      ""properties"": {
        ""enabled"": { ""type"": ""boolean"", ""default"": false },
        ""patterns"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""REQUIRED when enabled. Lowercase substrings that mark a parameter as junk. There is NO default list: what counts as vendor junk depends on whose families these are, and a built-in list would delete parameters by rules you never read. This command owns HOW to strip safely - match, veto, protect, one transaction, verify by re-reading, roll back if the parameter census moved; WHAT to strip is yours to state."" },
        ""exclude"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Optional. Lowercase substrings that VETO removal even on a junk match. Empty means veto nothing. IsCustom is refused regardless of this list, because it moves geometry - that is a fact about Revit, not a policy."" },
        ""keep"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Optional. Exact names (lowercased) never removed. Empty means keep nothing by name."" }
      }
    },
    ""protected_prefix"": { ""type"": ""string"", ""description"": ""Optional caller-supplied prefix. Parameters whose name starts with it are counted in the census (protected_prefix_count_before/after) and are never removed by the junk sweep â€” remove_params can still delete one by exact name. Omitted: they are not tracked at all, and the counts are reported as null, which is NOT the same as zero."" },
    ""save"": { ""type"": ""boolean"", ""default"": false, ""description"": ""doc.Save() in place after a successful commit. Never SaveAs, never a rename, never a delete of the original â€” an earlier scripted approach lost a family that way. saved_path is reported only after the file is found on disk, re-read from disk as a real family file, AND PROVEN TO HAVE CHANGED: size, timestamp and a SHA-256 of the contents are taken BEFORE the save and compared after, because a valid file that was already there is not evidence that Save wrote anything. A save that leaves the bytes identical is reported as saved=false with both hashes, since the commit already changed the family in memory and the file on disk is now behind it. The response also states whether a recoverable backup was left beside it. A rolled-back run never saves."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true, ""description"": ""DEFAULT TRUE. Resolve everything and report the plan and the before-census. Opens no transaction and saves nothing. This command rewrites a .rfa in place, so a rehearsal is the default and executing is the deliberate act."" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: homologar familia"", ""description"": ""The label of the single undo step this becomes."" }
  }
}";

        // ---- NO DEFAULT LISTS. This is a mechanism, not a policy. ----
        //
        // There used to be three: 71 junk patterns, 37 exclusions and a keep list,
        // "carried over from an earlier scripted approach". Measured against that
        // script on 2026-07-30: 69 of 71 junk patterns and 33 of 37 exclusions were
        // IDENTICAL. That is not a sensible default anybody would arrive at - it is
        // one operator's curated knowledge of which vendor parameters are disposable,
        // built over years, sitting inside a tool meant to be the commodity half.
        //
        // Two reasons it is gone, and the second matters even if the first did not:
        //
        // 1. The list is the method. The tool decides HOW to strip safely - match,
        //    veto, protect, remove inside one transaction, verify by re-reading, roll
        //    back if the census moved. WHAT counts as junk belongs to whoever owns the
        //    families, and shipping it here gives it away.
        //
        // 2. A default list that deletes parameters is a hidden policy. A caller who
        //    sets enabled=true and passes nothing is agreeing to 71 substring rules
        //    they never read, against a family they may not own. Requiring the list
        //    makes the decision visible at the point it is taken.
        //
        // The one thing kept is the technical fact, because it is not anybody's
        // method: IsCustom moves geometry, so it can never be stripped. See
        // GeometryFlagParam below, which the sweep refuses to touch regardless of
        // what the caller passes.

        // The flag an earlier script singled out by name: "IsCustom mueve geometria!".
        // Its disappearance is a geometry change even though it is not a Double.
        private const string GeometryFlagParam = "IsCustom";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            // WHICH family. This handler rewrites the ACTIVE family document and saves over
            // the .rfa, so "whichever family happened to be in front" is the whole risk. It
            // deliberately does not open files either: opening a 2025 .rfa from Revit 2026
            // upgrades it irreversibly, so the right .rfa must already be open in the right
            // Revit.
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            var doc = gate.Document;

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
                    "model has no FamilyManager: the parameters you mean live on the element or the type there â€” use " +
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
                        "with no way back â€” that is how the family catalog gets broken. Run this against the Revit " +
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
                        "This handler does NOT open files â€” writing into whichever family happened to be in front is " +
                        "how a batch homologates the wrong .rfa. Activate the intended family and re-run.");
            }

            var familyName = request.Value<string>("family_name");
            if (familyName != null) familyName = familyName.Trim();
            bool collapse = request["collapse_types"] != null && request.Value<bool>("collapse_types");
            var keepTypeName = request.Value<string>("keep_type");
            bool clearFormulas = request["clear_formulas"] == null || request.Value<bool>("clear_formulas");
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            bool save = request.Value<bool>("save");

            // The SCOPE: which family, which parameter spec, which type policy, whether it
            // is saved over. This rewrites a .rfa in place, so what gets approved is the
            // plan, not the intention.
            // These names must be the ones the CONTRACT declares. They were not:
            // "parameters", "set_values" and "remove" are fields this command has never
            // accepted, so they hashed to the empty marker every time and the three fields
            // that say WHAT TO WRITE - add_shared_params, values, remove_params - were
            // absent from the approval entirely. A token minted for {"Width": 3.5} was
            // measured live accepting {"Width": 9, "Manufacturer": ...}. The rehearsal
            // bound the family and the file, and nothing about the payload.
            string planHash = DocumentGate.PlanHash(request, "family_name", "rfa_path", "collapse_types",
                                                    "keep_type", "add_shared_params", "values", "remove_params",
                                                    "junk_rules", "protected_prefix", "clear_formulas", "save",
                                                    "spf_path", "clear_formulas_on", "expected_revit_version");

            var txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: homologar familia";

            // The caller's own parameter namespace, if they have one. Supplied per call:
            // this handler ships with no organisation's prefix baked in. Parameters whose
            // name starts with it are counted in the census and are never removed by the
            // junk sweep. Absent, they are not tracked at all â€” and "not tracked" is
            // reported as null, never as a count of zero.
            var trackedPrefix = request.Value<string>("protected_prefix");
            if (string.IsNullOrWhiteSpace(trackedPrefix)) trackedPrefix = null;

            FamilyManager fm;
            try { fm = doc.FamilyManager; }
            catch (Exception ex) { return CommandResult.Fail("This family document has no readable FamilyManager: " + ex.Message); }
            if (fm == null) return CommandResult.Fail("This family document has no FamilyManager.");

            // ---- The SHAPE, before. Measured, not inferred from parameter counts. -----
            List<GeometrySignature> geoBefore = CaptureGeometry(doc, fm);

            // ---- The before-census. The schema check's left-hand side. ---------------
            var before = Census.Take(fm, trackedPrefix);
            if (!before.Complete)
                return CommandResult.Fail(
                    "The family's parameters could not be read completely BEFORE any write (" + before.Unreadable +
                    " unreadable" + (before.FirstError == null ? "" : ", first failure: " + before.FirstError) + "), so " +
                    "the geometry invariant has no baseline: there would be no way to prove afterwards that the Double " +
                    "count and IsCustom did not move. Nothing was written. An invariant that passes because we could " +
                    "not look is worse than none â€” the caller would believe it.");

            // ---- Plan. Everything that can fail without touching the model fails here.
            var plan = new Plan();
            string why;
            if (!BuildPlan(app, doc, fm, request, familyName, keepTypeName, collapse, clearFormulas, plan, out why))
                return CommandResult.Fail(why);

            // ---- The MATERIALISED plan: the ROWS this resolved, and what they read now. --
            // planHash binds the REQUEST, and this command's own rehearsal admitted the
            // gap: "the token binds the REQUEST, not the parameters this rehearsal
            // resolved." Three things drift without a character of the request changing,
            // and each of them makes the approval mean something else:
            //
            //   * A VALUE MOVED. `values: {"Width": 3.5}` was approved against a parameter
            //     that read 2.0. If somebody has since set it to 9.0, the caller is
            //     overwriting a change they never saw. So each Set row carries what it
            //     reads NOW, not just what was requested.
            //   * A PARAMETER OR TYPE APPEARED OR VANISHED. add_shared_params resolves
            //     against the family as it stands: a parameter already present is a
            //     no-op, absent it is a write. The rows record which case each one was.
            //   * THE ACTIVE TYPE CHANGED. This is the one nothing else could catch. Only
            //     the ACTIVE type is measured for shape - the rehearsal says so, and lists
            //     which dimensions could be compared. A rehearsal taken with one type
            //     active approved a shape check of THAT type. That is why it goes in
            //     ContextFingerprint rather than in Elements: it is not a row being
            //     written, it is what the verification will be ABOUT.
            var resolvedPlan = BuildResolvedPlan(app, gate, doc, plan, before);

            if (dryRun)
            {
                var dryResult = new JObject
                {
                    ["mode"] = "dry_run",
                    ["transaction_status"] = "not_started",
                    ["document"] = SafeTitle(doc),
                    ["document_path"] = docPath,
                    ["is_family_document"] = true,
                    ["family_category"] = SafeCategory(doc),
                    ["parameter_schema_check"] = new JObject
                    {
                        ["status"] = "not_checked_yet",
                        ["double_count_before"] = before.DoubleCount,
                        ["is_custom_present_before"] = before.GeometryFlagPresent,
                        ["note"] = "This is only the baseline. The invariant is proven by re-reading these AFTER the " +
                                   "writes, inside the transaction, and rolling back if either moved."
                    },
                    ["protected_prefix_count_before"] = before.TrackedCount,
                    ["types_before"] = new JArray(before.TypeNames.Select(n => (JToken)n)),
                    ["plan"] = plan.ToJson(),
                    ["note"] = "Nothing was written; no transaction was opened, nothing was saved. " +
                               (plan.HasRefusals()
                                   ? "Some rows already refused before any write â€” see their 'error'. Those are not rows that would 'probably work'. "
                                   : "") +
                               "Re-run with dry_run=false and the confirmation_token below."
                };

                // WHAT A REAL RUN COULD VERIFY. Measuring the family against itself proves
                // nothing about a write - it says which dimensions of the ACTIVE type this
                // particular family exposes, so the caller sees in advance what would be
                // compared and therefore what would make it roll back. A caller who learns
                // only after the fact that the check could not see the thing that moved has
                // been told too late.
                List<GeometrySignature> geoNow = CaptureGeometry(doc, doc.FamilyManager);
                var measurable = new JArray();
                if (geoNow.Count > 0)
                    foreach (GeoDimension d in geoNow[0].Dimensions)
                        measurable.Add(new JObject { ["name"] = d.Name, ["measurable"] = d.IsMeasured, ["value"] = d.IsMeasured ? (JToken)d.Value.Value : null });
                var otherTypes = new JArray();
                for (int i = 1; i < geoNow.Count; i++) otherTypes.Add(geoNow[i].TypeName);

                dryResult["geometry_baseline"] = new JObject
                {
                    ["type_that_would_be_measured"] = geoNow.Count > 0 ? geoNow[0].TypeName : null,
                    ["types_that_would_NOT_be_measured"] = otherTypes,
                    ["dimensions"] = measurable,
                    ["note"] =
                        "This is the shape as it stands, not evidence about any write. It states which dimensions a " +
                        "real run would compare: one listed with measurable=false CANNOT be checked, so a change to " +
                        "it would not be caught, and the run would roll back rather than claim the shape held."
                };

                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(dryResult, gate, Name, planHash, true,
                    "the token binds the resolved ROWS and the value each parameter reads right now, plus WHICH TYPE " +
                    "was active - so a value somebody else changed, a parameter that appeared or vanished, or a " +
                    "different active type all refuse the apply as a stale plan. Still true, and unchanged by this: " +
                    "only the ACTIVE family type is measured for shape; the others are reported as not verified, " +
                    "never as intact.");
                return CommandResult.Ok(dryResult);
            }

            // Recomputed above from this call's own read of the family. The rehearsed PLAN
            // does not travel in the token, only its fingerprint, so a stale refusal names
            // the drift generically - still refused, still nothing written, and the caller
            // re-runs the rehearsal to see what moved.
            CommandResult refused = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                    resolvedPlan, null);
            if (refused != null) return refused;

            if (plan.IsEmpty() && plan.HasRefusals())
            {
                // IsEmpty() only counts rows with Error == null, so a plan whose EVERY row
                // was refused at plan time is "empty" too. Falling through to the branch
                // below would answer "this family already matches what was asked for" about
                // a family nothing was ever checked against â€” the request was declined in
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
                        ? "  (the refusals carried no reason this handler could render â€” treat the whole request as unapplied)"
                        : string.Join("\n", reasons)) +
                    "\nRe-running this family unchanged produces exactly these refusals again.");
            }

            if (plan.IsEmpty())
            {
                // Idempotence: the second run of a homologated family has nothing to do.
                // Reached only when there are no refusals either (checked above), so
                // "already matches" is a claim about the whole request, not a leftover.
                // Opening a transaction to commit nothing would let this report a clean
                // "Committed" over an untouched family â€” success-shaped noise in a large
                // batch where the operator reads the totals, not the rows.
                return CommandResult.Ok(new JObject
                {
                    ["mode"] = "apply",
                    ["transaction_status"] = "not_started",
                    ["document"] = SafeTitle(doc),
                    ["document_path"] = docPath,
                    ["nothing_to_do"] = true,
                    ["parameter_schema_check"] = InvariantJson("not_checked", before, before, null),
                    // Nothing ran, so this measures the shape against itself. It proves
                    // nothing about a write - what it shows is which dimensions THIS family
                    // exposes, so a caller sees in advance what a real run could and could
                    // not verify, and therefore what would make it roll back.
                    ["geometry_check"] = GeometryJson(GeometryCompare.Compare(geoBefore, CaptureGeometry(doc, fm)), geoBefore),
                    ["type_name_after"] = SafeCurrentTypeName(doc),
                    ["protected_prefix_count_before"] = before.TrackedCount,
                    ["protected_prefix_count_after"] = before.TrackedCount,
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
            List<GeometrySignature> geoAfterTx = null;
            GeometryVerdict geoVerdict = null;
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
                    ApplyAddShared(app, doc, plan);
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

                    // ---- THE SHAPE, measured INSIDE the transaction, after the regen. ----
                    //
                    // This is the check that decides whether the commit happens. It used to
                    // run after the commit, for the report only, while the commit itself was
                    // decided by the count of Double parameters and the presence of
                    // IsCustom - a schema census that a moved dimension sails straight
                    // through. Measuring here, before the commit, is the whole point: a
                    // geometry check that cannot stop the write is a log line.
                    geoAfterTx = CaptureGeometry(doc, doc.FamilyManager);
                    geoVerdict = GeometryCompare.Compare(geoBefore, geoAfterTx);
                    RolledBackGeometry = geoVerdict;
                    RolledBackBaseline = geoBefore;
                    string measuredType = geoBefore.Count > 0 ? geoBefore[0].TypeName : null;

                    // FRESH enumeration off the document â€” not a filter over the list the
                    // before-census built. Diffing two views of one in-memory list is the
                    // lie the spec names: the check would always "pass" and protect
                    // exactly nothing.
                    afterInTx = Census.Take(doc.FamilyManager, trackedPrefix);

                    if (geoVerdict.AnyChange)
                    {
                        invariantStatus = "violated";
                        rollbackReason =
                            "THE GEOMETRY MOVED. " + geoVerdict.Summary() + " The whole transaction was rolled back " +
                            "and the family is untouched on disk and in memory. This was measured - bounding box, " +
                            "solid volume, surface area, solid count and connector positions, before and after a " +
                            "regenerate - not inferred from a parameter census.";
                    }
                    else if (!ActiveTypeFullyMeasured(geoVerdict, measuredType))
                    {
                        // Coverage that fell short is not coverage. An unmeasured dimension
                        // is a dimension that may have moved, and continuing on that basis
                        // is exactly the substitution of unknown for unchanged this refuses.
                        invariantStatus = "unproven";
                        rollbackReason =
                            "The geometry of the type being measured ('" + (measuredType ?? "?") + "') could NOT be " +
                            "measured completely, so whether it moved is UNKNOWN - which is not the same as intact. " +
                            "Rolled back. Unmeasured: " +
                            string.Join("; ", geoVerdict.NotVerified.Take(4)) +
                            (geoVerdict.NotVerified.Count > 4 ? " (and more)" : "") + ".";
                    }
                    else if (!afterInTx.Complete)
                    {
                        invariantStatus = "unproven";
                        rollbackReason =
                            "The family's parameters could not be read completely after the writes (" +
                            afterInTx.Unreadable + " unreadable" +
                            (afterInTx.FirstError == null ? "" : ", first failure: " + afterInTx.FirstError) +
                            "), so whether the geometry moved is UNKNOWN â€” which is not the same as it being intact. " +
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
                        // MEASURED. The status a caller reads must come from Revit, not from a
                        // literal that is true by construction.
                        txStatus = Guard.RollBack(tx).StatusName;
                    }
                    else
                    {
                        // Turns a silent rollback into an error instead of a false success.
                        Guard.Commit(tx, txName);
                        txStatus = "Committed";
                        committed = true;
                    }
                }
                catch (SilentRollbackException ex)
                {
                    // Revit undid everything and returned a status instead of throwing.
                    // Every count taken above is fiction now.
                    return CommandResult.Ok(RolledBackResponse(doc, docPath, before, plan, "RolledBack", "not_checked",
                        afterInTx, ex.Message + " The family is untouched; every row reports nothing written."));
                }
                catch (Exception ex)
                {
                    bool attempted = false; string rb = PlanFailure.NotAttempted;
                    if (tx.HasStarted()) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                    return CommandResult.Fail(
                        "The homologation failed: " + ex.Message + ". " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb,
                            "the family is untouched and was not saved"));
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
                    "family as homologated â€” re-open it and inspect it.");
            }

            var after = Census.Take(fmAfter, trackedPrefix);
            var typeAfter = SafeCurrentType(fmAfter);
            string typeNameAfter = SafeTypeName(typeAfter);

            foreach (var r in plan.Sets) r.ReadBack = ReadFamilyValue(fmAfter, typeAfter, r.Name);
            foreach (var r in plan.Sets) r.Judge();
            foreach (var r in plan.Adds) r.Judge(after);
            foreach (var r in plan.Removals) r.Judge(before, after);
            foreach (var r in plan.FormulaClears) r.Judge(fmAfter);
            plan.JudgeTypes(fmAfter, familyName);

            // The invariant, re-read a THIRD time â€” after the commit, as the contract
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
                    "and cannot be undone from here. Do NOT save this family and do NOT continue the batch â€” re-open " +
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

                ["parameter_schema_check"] = InvariantJson(invariantFinal, before, after, invariantWarning),

                // The SHAPE, measured on both sides of the commit and compared. This is the
                // check the old geometry_invariant only claimed to be: a Double whose value
                // moved, a Double swapped for another, or a dimension driven by a formula
                // all leave the schema check happy and show up here.
                ["geometry_check"] = GeometryJson(geoVerdict, geoBefore),

                // fm.CurrentType.Name, re-read from the document. Never family_name.
                ["type_name_after"] = typeNameAfter,
                ["type_name_matches_family_name"] = familyName == null
                    ? null
                    : (JToken)string.Equals(typeNameAfter, familyName, StringComparison.Ordinal),
                ["types_after"] = new JArray(after.TypeNames.Select(n => (JToken)n)),
                // Filtered to what a fresh re-read of fm.Types says is GONE, exactly like
                // params_removed below. Unfiltered, this field was a list of deletion
                // ATTEMPTS and its length was "the count of calls that did not throw" â€”
                // the very thing the header of this file disclaims.
                ["types_deleted"] = new JArray(plan.TypeDeletes.Where(t => t.Outcome == OUT_CONFIRMED)
                                                               .Select(t => (JToken)t.ToJson())),
                ["types_deleted_count"] = plan.TypeDeletes.Count(t => t.Outcome == OUT_CONFIRMED),
                ["types_delete_failed"] = new JArray(plan.TypeDeletes.Where(t => t.Outcome != OUT_CONFIRMED)
                                                                     .Select(t => (JToken)t.ToJson())),
                ["type_rename"] = plan.Rename == null ? null : plan.Rename.ToJson(),

                ["protected_prefix_count_before"] = before.TrackedCount,
                ["protected_prefix_count_after"] = after.TrackedCount,

                ["params_added"] = new JArray(plan.Adds.Select(a => (JToken)a.ToJson())),
                ["params_added_confirmed"] = plan.Adds.Count(a => a.Outcome == OUT_CONFIRMED),
                // Filtered to the ones a fresh post-commit read of p.Formula says are GONE.
                // Unfiltered, this field carried rows whose formula is demonstrably STILL
                // on the parameter â€” under a field name that says they were cleared.
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
        /// <summary>
        /// The resolved plan for this run. One element per row that would be written, so
        /// create/modify/delete in the reply are the real numbers, plus the ambient state
        /// the verification depends on in ContextFingerprint.
        ///
        /// Synthetic identities ("param:Width", "type:600mm") because a family parameter
        /// has no UniqueId - there is nothing else to key on, and the name IS the identity
        /// the request used. Every read is wrapped: measuring must never be what fails.
        /// </summary>
        private static ResolvedPlan BuildResolvedPlan(UIApplication app, GateResult gate,
                                                      Document doc, Plan plan, Census before)
        {
            var rp = new ResolvedPlan
            {
                Command = "family_apply",
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            try
            {
                foreach (TypeDelete t in plan.TypeDeletes.Where(x => x.Error == null))
                    rp.Elements.Add(Row("type:" + t.Name, PlannedAction.Delete, "op", "delete_type"));

                if (plan.Rename != null && plan.Rename.Error == null && plan.Rename.Needed)
                {
                    PlannedElement r = Row("rename", PlannedAction.Modify, "op", "rename_type");
                    // The rename's own JSON already states from and to; taking it whole
                    // means this cannot fall out of step with what the rehearsal printed.
                    r.BeforeValues["rename"] = Canon(plan.Rename.ToJson());
                    rp.Elements.Add(r);
                }

                foreach (AddRow a in plan.Adds.Where(x => x.Error == null))
                {
                    // A parameter already present is a NO-OP, not a create. Recording it
                    // anyway is the point: if it disappears before the apply, the same
                    // request becomes a write, and that is a different plan.
                    PlannedElement r = Row("param:" + a.Name,
                                           a.AlreadyPresent ? PlannedAction.Modify : PlannedAction.Create,
                                           "op", a.AlreadyPresent ? "add_already_present" : "add");
                    r.BeforeValues["instance"] = a.Instance ? "1" : "0";
                    r.BeforeValues["group"] = a.GroupSpec ?? "";
                    rp.Elements.Add(r);
                }

                foreach (SetRow st in plan.Sets.Where(x => x.Error == null))
                {
                    PlannedElement r = Row("param:" + st.Name, PlannedAction.Modify, "op", "set");
                    // What was asked for AND what is there. The second is what makes an
                    // overwrite of somebody else's change detectable.
                    r.BeforeValues["requested"] = st.Requested == null ? "" : Canon(st.Requested);
                    r.BeforeValues["before"] = st.Before == null ? "" : Canon(st.Before);
                    r.BeforeValues["storage"] = st.Storage ?? "";
                    r.BeforeValues["instance"] = st.IsInstance ? "1" : "0";
                    rp.Elements.Add(r);
                }

                foreach (ClearRow c in plan.FormulaClears.Where(x => x.Error == null))
                {
                    PlannedElement r = Row("formula:" + c.Name, PlannedAction.Modify, "op", "clear_formula");
                    // Clearing a formula that has since been rewritten deletes different
                    // work than the one that was approved.
                    r.BeforeValues["formula_before"] = c.FormulaBefore ?? "";
                    rp.Elements.Add(r);
                }

                foreach (RemoveRow rm in plan.Removals.Where(x => x.Error == null && x.WasPresent))
                {
                    PlannedElement r = Row("param:" + rm.Name, PlannedAction.Delete, "op", "remove");
                    // A removal the junk sweep proposed and one the caller named are the
                    // same deletion with very different provenance, and a caller who
                    // approved the explicit list did not approve the sweep's guess.
                    r.BeforeValues["source"] = rm.Source ?? "";
                    r.BeforeValues["junk_match"] = rm.JunkMatch ?? "";
                    rp.Elements.Add(r);
                }

                rp.ContextFingerprint = ContextOf(doc, before);
            }
            catch (Exception ex)
            {
                // A plan that could not be fully measured must NOT quietly become a
                // shorter plan that then matches. Poison it so the comparison cannot
                // succeed, and let the apply refuse as stale.
                rp.ContextFingerprint = "unmeasurable:" + ex.GetType().Name;
            }
            return rp;
        }

        private static PlannedElement Row(string id, PlannedAction action, string k, string v)
        {
            return new PlannedElement
            {
                UniqueId = id,
                Action = action,
                BeforeValues = new Dictionary<string, string> { { k, v } }
            };
        }

        /// <summary>Stable JSON: Formatting.None so whitespace is never the difference.</summary>
        private static string Canon(JToken t)
        {
            try { return t.ToString(Newtonsoft.Json.Formatting.None); } catch { return "<unreadable>"; }
        }

        /// <summary>
        /// The ambient state the verification is ABOUT: which type is active (the only one
        /// whose shape is measured), the dimensions that could be compared, and the two
        /// census figures the geometry invariant is proven against. All of it can change
        /// while every requested row stays identical.
        /// </summary>
        private static string ContextOf(Document doc, Census before)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                List<GeometrySignature> geo = CaptureGeometry(doc, doc.FamilyManager);
                sb.Append("active=").Append(geo.Count > 0 ? (geo[0].TypeName ?? "") : "<none>").Append('\n');
                if (geo.Count > 0)
                {
                    // Sorted: Revit may enumerate parameters in any order, and that is not
                    // a change to the family.
                    foreach (GeoDimension d in geo[0].Dimensions.OrderBy(x => x.Name, StringComparer.Ordinal))
                        sb.Append("dim=").Append(d.Name).Append('=')
                          .Append(d.IsMeasured
                                  ? System.Math.Round(d.Value.Value, 6).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                  : "<not measurable>").Append('\n');
                }
                sb.Append("types=").Append(geo.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
            catch (Exception ex) { sb.Append("shape=unreadable:").Append(ex.GetType().Name).Append('\n'); }
            try
            {
                sb.Append("doubles=").Append(before.DoubleCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("iscustom=").Append(before.GeometryFlagPresent ? "1" : "0").Append('\n');
            }
            catch (Exception ex) { sb.Append("census=unreadable:").Append(ex.GetType().Name).Append('\n'); }
            return sb.ToString();
        }

        private class Census
        {
            public int DoubleCount;
            public bool GeometryFlagPresent;
            /// <summary>
            /// How many parameters carry the caller's protected_prefix. NULL when no prefix
            /// was given: nothing was tracked, and that is not the same fact as "there are
            /// none". Reporting 0 for "I was not looking" is the kind of confident zero this
            /// handler exists to refuse.
            /// </summary>
            public int? TrackedCount;
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

            public static Census Take(FamilyManager fm, string trackedPrefix = null)
            {
                var c = new Census();
                if (!string.IsNullOrEmpty(trackedPrefix)) c.TrackedCount = 0;
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
                        if (!string.IsNullOrEmpty(trackedPrefix) &&
                            n.StartsWith(trackedPrefix, StringComparison.Ordinal)) c.TrackedCount++;
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
                parts.Add("'" + GeometryFlagParam + "' is GONE. It moves geometry â€” removing it deforms the family.");
            if (!before.GeometryFlagPresent && after.GeometryFlagPresent)
                parts.Add("'" + GeometryFlagParam + "' APPEARED, which this operation has no business doing.");
            return "GEOMETRY INVARIANT BROKEN: " + string.Join(" ", parts.ToArray()) +
                   " The whole transaction was rolled back â€” nothing was written, including the parts that worked, and " +
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
                    "count and IsCustom are identical. violated / unproven: the transaction was ROLLED BACK â€” the " +
                    "family is untouched. violated_after_commit / unknown_after_commit: the check passed inside the " +
                    "transaction and the post-commit re-read says otherwise; the commit is done and cannot be undone. " +
                    "not_checked: no transaction ran.",

                // WHAT THIS IS NOT. This block used to be published as `geometry_invariant`
                // and its best value read `proven_unchanged`. It compares the COUNT of
                // Double parameters and whether IsCustom still exists. Both are worth
                // checking, and neither is geometry: change a Double's VALUE and the count
                // is identical, swap one Double for another and the count is identical,
                // drive a dimension from a formula and no parameter changes at all. In all
                // three the extrusion moves and this says proven_unchanged.
                //
                // A guarantee that can be satisfied by something other than the thing it
                // names is worse than no guarantee, because it gets believed. So it is
                // named for what it measures, and the geometry flag below says plainly that
                // the shape was not looked at.
                ["measures"] = "the parameter SCHEMA: how many Double parameters exist and whether IsCustom is " +
                               "present. It does NOT measure shape.",
                ["geometry_verified_here"] = false,
                ["geometry_verified_note"] =
                    "THIS block does not measure shape, and its passing does not mean the geometry is unchanged - a " +
                    "Double whose value moved, a Double swapped for another, or a dimension driven by a formula all " +
                    "leave these counts identical while the form moves. The shape is measured separately: read the " +
                    "sibling field `geometry_check`, which compares bounding box, solid volume, surface area, solid " +
                    "count and connector positions before and after. Only the ACTIVE family type is measured there; " +
                    "the rest are listed as not verified.",
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
            public string SpfPath;   // kept so the adds can re-open the SPF at apply time (defs go stale after planning restores the filename)

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
            /// caller can see; an earlier scripted approach's `except: pass` made these
            /// disappear entirely, which is how a family shipped with three of its
            /// required parameters missing and a clean-looking report.
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
            /// sat in `params_would_remove` â€” two fields a consumer reads as disjoint. And
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

                // Judged whenever a rename was NEEDED â€” never gated on Rename.Error being
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
                            "'. The rename did not land. The caller's data-template loader matches on Family " +
                            "Name = Type Name; this family will not match.");
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
            /// returns the FamilyParameter it made â€” holding that object and calling it
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
                            "was not among the ones it did read. Whether it was added is UNKNOWN â€” one of the unreadable " +
                            "parameters may be it.";
                    return;
                }
                if (after.Names.Contains(Name)) { Outcome = OUT_CONFIRMED; return; }
                Outcome = OUT_NOT_WRITTEN;
                if (Error == null)
                    Error = "AddParameter did not throw and the parameter is NOT in the family after the commit. " +
                            "This is the failure an earlier scripted approach's `except System.Exception: pass` " +
                            "made invisible: the family ships without its required parameter and the report says OK.";
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
            /// referenced parameter â€” a call that did not throw is not a removal.
            /// </summary>
            public void Judge(Census before, Census after)
            {
                if (!WasPresent) { Outcome = OUT_NOTHING_TO_DO; return; }
                if (!after.Complete && after.Names.Contains(Name) == false)
                {
                    Outcome = OUT_UNKNOWN;
                    Error = "The post-commit census could not read " + after.Unreadable + " parameter(s). This one is " +
                            "not among the ones it read, but one of the unreadable ones may be it â€” whether it is gone " +
                            "is UNKNOWN.";
                    return;
                }
                if (!after.Names.Contains(Name)) { Outcome = OUT_CONFIRMED; return; }
                Outcome = OUT_NOT_WRITTEN;
                if (Error == null)
                    Error = "The parameter is STILL in the family after the commit. RemoveParameter did not remove it â€” " +
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
                            " Whether the value is in the family is UNKNOWN â€” which is not the same as it being absent, " +
                            "and not the same as it being there. The commit is DONE.";
                    return;
                }
                if (!SameValue(ReadBack, Expected))
                {
                    Outcome = OUT_NOT_WRITTEN;
                    Error = "The transaction COMMITTED and the family does not hold what you asked for: the type reads " +
                            Show(ReadBack) + " and " + Show(Expected) + " was requested. FamilyManager.Set() returns " +
                            "VOID â€” it did not throw and it did not write. This is the failure an earlier scripted " +
                            "approach's `except System.Exception: pass` reported as 'OK -> path'.";
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
                                       ? " value_expected is null for the same reason â€” on the SetValueString path it " +
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

                plan.SpfPath = spf;

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
                                    "is nothing to add â€” adding a lookalike would give the family a parameter with the " +
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
                    if (p == null)
                    {
                        // A value for a parameter this same call is about to ADD cannot be
                        // resolved yet â€” it does not exist until apply. Do NOT pre-fail it:
                        // ApplyValues re-resolves every row against the family AFTER the adds
                        // and sets it then (a freshly added shared parameter has no formula and
                        // is not read-only, so the planning reads skipped here are moot). Pre-
                        // failing here is what left a just-added parameter empty while the
                        // report still claimed the family was homologated.
                        bool willBeAdded = false;
                        foreach (var ar in plan.Adds)
                            if (!ar.AlreadyPresent && ar.Error == null &&
                                string.Equals(ar.Name, prop.Name, StringComparison.Ordinal))
                            { willBeAdded = true; break; }
                        if (willBeAdded) continue;   // resolved and set at apply time; row.Error stays null

                        row.Error = pWhy;
                        continue;
                    }

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

            // ---- Removals: the caller's parameter spec's 'NA' entries ----
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
                // The POLICY is the caller's, and it is required. Falling back to a
                // built-in list would mean deleting parameters by rules the caller never
                // read - and those rules were one operator's curated knowledge, not a
                // sensible default. Refuse rather than substitute.
                var patterns = StringArray(junk["patterns"], null);
                if (patterns == null || patterns.Length == 0)
                {
                    why = "junk_rules.enabled is true but no 'patterns' were given, and this command ships NO " +
                          "default list. What counts as vendor junk depends on whose families these are, and a " +
                          "built-in list would delete parameters by rules you never read. Pass the substrings you " +
                          "mean - e.g. patterns: [\"omniclass\", \"bimobject\", \"product \"] - or leave " +
                          "junk_rules.enabled false. Nothing was changed.";
                    return false;
                }
                // exclude and keep are OPTIONAL vetoes: empty means 'veto nothing', which
                // is a coherent request and is exactly what an empty list says. Only the
                // list that DELETES is mandatory.
                var exclude = StringArray(junk["exclude"], new string[0]);
                var keep = new HashSet<string>(StringArray(junk["keep"], new string[0]), StringComparer.Ordinal);
                var junkProtectedPrefix = request.Value<string>("protected_prefix");

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
                    if (!IsJunk(n, st, f, patterns, exclude, keep, junkProtectedPrefix, out match)) continue;
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
        /// The conservative rule. Text only â€” Double is geometry and Integer is a behaviour
        /// flag; nothing with a formula; nothing that matches the behaviour/MEP/material veto
        /// (which starts with 'custom', because IsCustom moves geometry); nothing in the
        /// identity keep-list; and never a parameter in the caller's own protected_prefix
        /// namespace, which this sweep must not touch.
        /// </summary>
        private static bool IsJunk(string name, StorageType st, string formula, string[] patterns,
                                   string[] exclude, HashSet<string> keep, string protectedPrefix, out string match)
        {
            match = null;
            if (name == null) return false;
            var nl = name.ToLowerInvariant();
            if (keep.Contains(nl)) return false;
            if (!string.IsNullOrEmpty(protectedPrefix) &&
                name.StartsWith(protectedPrefix, StringComparison.Ordinal)) return false;
            if (st != StorageType.String) return false;
            if (formula != null) return false;
            foreach (var e in exclude) if (nl.IndexOf(e, StringComparison.Ordinal) >= 0) return false;
            foreach (var j in patterns)
                if (nl.IndexOf(j, StringComparison.Ordinal) >= 0) { match = j; return true; }
            return false;
        }

        // =====================================================================
        // Applying. Inside the transaction, in the order an earlier scripted approach proved.
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

        private static void ApplyAddShared(UIApplication app, Document doc, Plan plan)
        {
            var fm = doc.FamilyManager;

            var pending = new List<AddRow>();
            foreach (var a in plan.Adds)
                if (a.Error == null && !a.AlreadyPresent && a.Group != null) pending.Add(a);
            if (pending.Count == 0) return;

            // An ExternalDefinition is only valid while ITS shared parameter file is the
            // app's CURRENT one. Planning read the definitions and then restored
            // SharedParametersFilename (LoadSpf's finally), so the handles captured on the
            // AddRows are now stale â€” AddParameter on a stale def throws "Shared parameter
            // creation failed" (found by the live test). Point the app back at the SPF HERE,
            // re-fetch each definition fresh, add it while the file is current, then restore.
            string original = null;
            try { original = app.Application.SharedParametersFilename; } catch { }
            try
            {
                DefinitionFile file;
                try
                {
                    app.Application.SharedParametersFilename = plan.SpfPath;
                    file = app.Application.OpenSharedParameterFile();
                }
                catch (Exception ex)
                {
                    foreach (var a in pending) a.Error = "Re-opening the SPF at apply time failed: " + ex.Message;
                    return;
                }
                if (file == null)
                {
                    foreach (var a in pending) a.Error = "The SPF could not be re-opened at apply time; nothing was added.";
                    return;
                }

                var fresh = new Dictionary<string, ExternalDefinition>(StringComparer.Ordinal);
                foreach (DefinitionGroup g in file.Groups)
                    foreach (Definition d in g.Definitions)
                    {
                        var ed = d as ExternalDefinition;
                        if (ed != null && !fresh.ContainsKey(ed.Name)) fresh[ed.Name] = ed;
                    }

                foreach (var a in pending)
                {
                    ExternalDefinition ed;
                    if (!fresh.TryGetValue(a.Name, out ed) || ed == null)
                    {
                        a.Error = "The shared parameter '" + a.Name + "' was not found in the SPF when re-read at apply time.";
                        continue;
                    }
                    try { fm.AddParameter(ed, a.Group, a.Instance); }
                    catch (Exception ex)
                    {
                        // NOT `except: pass`. This is the exact call an earlier scripted approach swallowed.
                        a.Error = "AddParameter threw: " + ex.Message;
                    }
                }
            }
            finally
            {
                try { if (original != null) app.Application.SharedParametersFilename = original; } catch { }
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
                    // gives, and an earlier scripted approach threw that away too.
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
                              "referenced (by a formula, a label or geometry) â€” that refusal is protecting the family.";
                }
            }
        }

        /// <summary>
        /// Coerce and write. `expected` is the load-bearing output: the caller's value in
        /// the shape the family stores it, captured HERE, the only place that still knows
        /// what was asked for. Verify against a second read of the parameter instead and
        /// the check passes by construction â€” Revit ignoring the value moves both reads
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
                    if (isNull) { why = "Storage is String and the value is null. Use \"\" to clear it â€” null and empty are not the same request."; return false; }
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
                        // itself â€” taken now, before Regenerate can move it.
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
                    if (!Rid.CanRepresentElementId(idv)) { why = Rid.ElementIdRangeError(idv); return false; }
                    r.How = "FamilyManager.Set(ElementId)";
                    var eid = Rid.ToElementId(idv);
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
                ["expectation_source"] = "the exact value passed to FamilyManager.Set() â€” what the caller asked for."
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
                // Not "empty". Not "unchanged". Unknown â€” and it must read as unknown.
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
                          ", so a unique match cannot be proven â€” one of them may carry the same name, and writing the " +
                          "wrong one is indistinguishable from writing the right one in this report.";
                    return null;
                }
                return hits[0];
            }

            why = "no parameter named '" + spec + "' exists in this family" +
                  (unreadable > 0 ? " among the " + unreadable + " that could be read" : "") +
                  (bipError == null ? "" : " (the BuiltInParameter lookup did not come back empty â€” it FAILED: " + bipError + ")") +
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
        // the versions this file must compile against â€” and hard-coding two entries would
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
        /// What a file was, so that what it becomes can be compared to it. Size and
        /// timestamp are cheap and can both stay the same across a real write; the hash is
        /// what settles it. A read that failed is recorded as such - unknown is not "same".
        /// </summary>
        /// <summary>
        /// Measure the shape of the ACTIVE family type, and say plainly that the others
        /// were not measured.
        ///
        /// Only the active type, on purpose. Measuring every type means activating each one
        /// and regenerating - and activating a type IS a change to the file, so a check that
        /// did that would be altering the thing it claims to be protecting. The other types
        /// are emitted as signatures whose every dimension is Unmeasured, so they land in
        /// the verdict's not-verified list instead of quietly counting as unchanged.
        /// </summary>
        /// <summary>
        /// Was the type we actually measured measured COMPLETELY? Other types are listed
        /// as unmeasured by design - activating them to measure would itself change the
        /// file - so their absence must not block a commit. A gap in the type we DID
        /// measure must.
        /// </summary>
        /// <summary>
        /// The geometry verdict that CAUSED a rollback, handed to the rolled-back response.
        /// Static because the response builder is static and threading two more parameters
        /// through five call sites buys nothing; the dispatcher admits one command at a
        /// time, so there is exactly one of these in flight.
        /// </summary>
        [ThreadStatic] private static GeometryVerdict RolledBackGeometry;
        [ThreadStatic] private static List<GeometrySignature> RolledBackBaseline;

        private static bool ActiveTypeFullyMeasured(GeometryVerdict v, string activeType)
        {
            if (v == null) return false;
            if (string.IsNullOrEmpty(activeType)) return false;
            foreach (string s in v.NotVerified)
                if (s.StartsWith(activeType + ".", StringComparison.Ordinal)) return false;
            return true;
        }

        private static List<GeometrySignature> CaptureGeometry(Document fam, FamilyManager fm)
        {
            var all = new List<GeometrySignature>();
            string activeName = null;
            try { activeName = fm?.CurrentType?.Name; } catch { }
            if (string.IsNullOrEmpty(activeName)) activeName = "(active type)";

            var sig = new GeometrySignature { TypeName = activeName };
            var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine };

            double volume = 0, area = 0;
            int solids = 0, readFailures = 0;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool anyBox = false;

            try
            {
                foreach (Element e in new FilteredElementCollector(fam).WhereElementIsNotElementType())
                {
                    try
                    {
                        GeometryElement g = e.get_Geometry(opt);
                        if (g != null) HarvestGeometry(g, ref volume, ref area, ref solids);

                        BoundingBoxXYZ bb = e.get_BoundingBox(null);
                        if (bb != null)
                        {
                            anyBox = true;
                            minX = Math.Min(minX, bb.Min.X); minY = Math.Min(minY, bb.Min.Y); minZ = Math.Min(minZ, bb.Min.Z);
                            maxX = Math.Max(maxX, bb.Max.X); maxY = Math.Max(maxY, bb.Max.Y); maxZ = Math.Max(maxZ, bb.Max.Z);
                        }
                    }
                    catch { readFailures++; }
                }

                // A read that threw makes the numbers partial, and a partial sum must not be
                // published as a measurement - it would compare equal to another partial sum
                // that failed on different elements.
                if (readFailures > 0)
                {
                    string why = readFailures + " element(s) would not report geometry, so the totals are short";
                    sig.Add(GeoDimension.Unmeasured("solid_volume", why));
                    sig.Add(GeoDimension.Unmeasured("surface_area", why));
                    sig.Add(GeoDimension.Unmeasured("solid_count", why));
                }
                else
                {
                    sig.Add(GeoDimension.Of("solid_volume", volume));
                    sig.Add(GeoDimension.Of("surface_area", area));
                    sig.Add(GeoDimension.Of("solid_count", solids));
                }

                if (anyBox)
                {
                    sig.Add(GeoDimension.Of("bbox_x", maxX - minX));
                    sig.Add(GeoDimension.Of("bbox_y", maxY - minY));
                    sig.Add(GeoDimension.Of("bbox_z", maxZ - minZ));
                }
                else
                {
                    sig.Add(GeoDimension.Unmeasured("bbox_x", "no element reported a bounding box"));
                    sig.Add(GeoDimension.Unmeasured("bbox_y", "no element reported a bounding box"));
                    sig.Add(GeoDimension.Unmeasured("bbox_z", "no element reported a bounding box"));
                }

                sig.Connectors = CaptureConnectors(fam);
            }
            catch
            {
                sig.Connectors = null;
                if (sig.Dimensions.Count == 0)
                    sig.Add(GeoDimension.Unmeasured("solid_volume", "the family document could not be walked"));
            }

            all.Add(sig);

            // Every OTHER type, present and explicitly not measured.
            try
            {
                foreach (FamilyType t in fm.Types)
                {
                    string n = null;
                    try { n = t.Name; } catch { }
                    if (string.IsNullOrEmpty(n) || string.Equals(n, activeName, StringComparison.Ordinal)) continue;

                    var other = new GeometrySignature { TypeName = n, Connectors = null };
                    other.Add(GeoDimension.Unmeasured("solid_volume",
                        "only the ACTIVE family type is measured - activating another type to measure it would " +
                        "itself modify the file"));
                    all.Add(other);
                }
            }
            catch { }

            return all;
        }

        private static List<string> CaptureConnectors(Document fam)
        {
            try
            {
                var found = new List<string>();
                foreach (Element e in new FilteredElementCollector(fam).OfClass(typeof(ConnectorElement)))
                {
                    var ce = e as ConnectorElement;
                    if (ce == null) continue;
                    XYZ p = ce.Origin;
                    XYZ d = ce.CoordinateSystem?.BasisZ;
                    found.Add("p=" + Round(p) + " d=" + Round(d) + " shape=" + SafeShape(ce));
                }
                return found;
            }
            catch { return null; }   // null = not read. Never an empty list, which would mean "none".
        }

        private static string SafeShape(ConnectorElement ce)
        {
            try { return ce.Shape.ToString(); } catch { return "?"; }
        }

        private static string Round(XYZ p)
        {
            if (p == null) return "?";
            return p.X.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   p.Y.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   p.Z.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void HarvestGeometry(GeometryObject go, ref double volume, ref double area, ref int solids)
        {
            if (go is Solid s)
            {
                if (s.Volume > 1e-12 && s.Faces.Size > 0) { solids++; volume += s.Volume; area += s.SurfaceArea; }
            }
            else if (go is GeometryInstance gi)
            {
                var inner = gi.GetInstanceGeometry();
                if (inner != null) foreach (GeometryObject o in inner) HarvestGeometry(o, ref volume, ref area, ref solids);
            }
            else if (go is GeometryElement ge)
            {
                foreach (GeometryObject o in ge) HarvestGeometry(o, ref volume, ref area, ref solids);
            }
        }

        /// <summary>The measured verdict, rendered for the reply.</summary>
        private static JObject GeometryJson(GeometryVerdict v, List<GeometrySignature> baseline)
        {
            if (v == null)
                return new JObject
                {
                    ["status"] = "not_measured",
                    ["status_means"] = "No transaction ran, so nothing was measured."
                };

            // WHICH types this verdict actually covers. Saying "the geometry is unchanged"
            // without saying whose is the claim this block exists to stop making.
            string measured = baseline != null && baseline.Count > 0 ? baseline[0].TypeName : null;
            var unmeasured = new JArray();
            if (baseline != null)
                for (int i = 1; i < baseline.Count; i++) unmeasured.Add(baseline[i].TypeName);

            return new JObject
            {
                ["type_measured"] = measured,
                ["types_NOT_measured"] = unmeasured,
                ["scope_note"] =
                    "ONLY the type named in type_measured was measured. Measuring another type means activating it " +
                    "and regenerating, which is itself a change to the file - so a check that did it would be " +
                    "altering the thing it exists to protect. The " + unmeasured.Count + " type(s) listed above are " +
                    "NOT covered by this verdict, and this command does not claim the whole family is protected.",
                ["status"] = v.Status,
                ["dimensions_compared"] = v.Unchanged + v.Changed.Count,
                ["dimensions_unchanged"] = v.Unchanged,
                ["dimensions_changed"] = v.Changed.Count,
                ["not_verified_count"] = v.NotVerified.Count,
                ["fully_verified"] = v.FullyVerified,
                ["changes"] = new JArray(v.Changed.Select(c => (JToken)c.Describe())),
                ["types_added"] = new JArray(v.TypesAdded.Select(s => (JToken)s)),
                ["types_removed"] = new JArray(v.TypesRemoved.Select(s => (JToken)s)),
                ["not_verified"] = new JArray(v.NotVerified.Take(40).Select(s => (JToken)s)),
                ["summary"] = v.Summary(),
                ["status_means"] =
                    "unchanged: every dimension of every type was compared and none moved. " +
                    "unchanged_where_measured: nothing that WAS compared moved, but something could not be measured - " +
                    "read not_verified. changed: the shape moved, and `changes` says which dimension. Only the ACTIVE " +
                    "family type is measured: measuring the others means activating each one, which is itself a " +
                    "change to the file, so they are listed as not verified rather than assumed intact."
            };
        }

        private sealed class FileFacts
        {
            public bool Existed;
            public long? Size;
            public DateTime? WrittenUtc;
            public string Sha256;
            public string Error;

            public static FileFacts Read(string path)
            {
                var f = new FileFacts();
                if (string.IsNullOrEmpty(path)) { f.Error = "no path"; return f; }
                try
                {
                    if (!File.Exists(path)) { f.Existed = false; return f; }
                    f.Existed = true;
                    var fi = new FileInfo(path);
                    f.Size = fi.Length;
                    f.WrittenUtc = fi.LastWriteTimeUtc;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    using (var s = File.OpenRead(path))
                        f.Sha256 = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
                }
                catch (Exception ex) { f.Error = ex.Message; }
                return f;
            }

            /// <summary>true changed, false identical, NULL when it cannot be told.</summary>
            public static bool? Changed(FileFacts before, FileFacts after)
            {
                if (before == null || after == null) return null;
                if (!before.Existed) return true;                       // it did not exist; now it does
                if (before.Sha256 == null || after.Sha256 == null) return null;
                return !string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// doc.Save(), then prove it: the path must exist AND the bytes must read back as
        /// an actual OLE compound file (every .rfa is one). File.Exists alone would be
        /// satisfied by the file that was already there before this run â€” which is exactly
        /// what "OK -> path" claimed about families it never wrote.
        /// </summary>
        private static JObject SaveAndVerify(Document doc)
        {
            // Snapshot BEFORE. Existence and a valid header are satisfied by the file that
            // was already there, so they cannot tell "Save wrote this" from "Save did
            // nothing and the old file is still fine". Size, timestamp and a content hash
            // taken beforehand can.
            string pathBefore = null;
            try { pathBefore = doc.PathName; } catch { }
            FileFacts before = FileFacts.Read(pathBefore);

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
                                 "). Whether the file was written is UNKNOWN â€” not reported as saved."
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
                                 " Existence is not evidence â€” not reported as saved."
                };

            // Did the bytes actually move? A Save that quietly does nothing leaves a file
            // that exists, reads back as a valid .rfa, and is the OLD one.
            FileFacts after = FileFacts.Read(path);
            bool? changed = FileFacts.Changed(before, after);

            if (changed == false)
                return new JObject
                {
                    ["saved"] = false,
                    ["saved_path"] = null,
                    ["file_size_bytes"] = size,
                    ["reason"] = "doc.Save() did not throw and '" + path + "' is a valid family file - but it is the " +
                                 "SAME file: identical size, identical timestamp and identical content hash before and " +
                                 "after. The commit changed the family in memory, so the file on disk is now BEHIND it. " +
                                 "Not reported as saved. Do not close the document without saving it.",
                    ["size_before"] = before.Size,
                    ["size_after"] = after.Size,
                    ["sha256_before"] = before.Sha256,
                    ["sha256_after"] = after.Sha256
                };

            return new JObject
            {
                ["saved"] = true,
                ["saved_path"] = path,
                ["file_size_bytes"] = size,
                ["last_write_time_utc"] = written.ToUniversalTime().ToString("o"),
                ["verified_by"] = "File.Exists AND re-reading the file's header off disk: it is a real OLE compound " +
                                  "file (D0 CF 11 E0 ...), which every .rfa is" +
                                  (changed == true
                                    ? ", AND the bytes on disk CHANGED - size, timestamp and content hash were taken " +
                                      "before the save and compared after, so this is not the file that was already there."
                                    : "."),
                ["file_changed"] = changed.HasValue ? (JToken)changed.Value : JValue.CreateNull(),
                ["file_changed_note"] = changed.HasValue ? null
                    : "Whether the bytes changed is UNKNOWN: the file could not be hashed before or after (" +
                      (before.Error ?? after.Error) + "). The save is reported on existence and header alone.",
                ["size_before"] = before.Size,
                ["size_after"] = after.Size,
                ["sha256_before"] = before.Sha256,
                ["sha256_after"] = after.Sha256,
                ["recoverable_copy"] = Backups(path).Any()
                    ? (JToken)true
                    : false,
                ["recoverable_copy_note"] = Backups(path).Any()
                    ? "Revit left at least one 'name.000N.rfa' beside the file; the previous version is recoverable."
                    : "NO backup file was found beside the saved family. Revit normally writes one per save, so its " +
                      "absence means backups are disabled or were cleaned - the previous version of this .rfa may not " +
                      "be recoverable.",
                ["backup_files_now_on_disk"] = new JArray(Backups(path).Select(b => (JToken)b)),
                ["note"] = "Every Save over an existing .rfa leaves a 'name.000N.rfa' backup next to it. They are " +
                           "listed here because an earlier scripted approach's rule is to delete them all at the end " +
                           "of the batch â€” and the single-digit glob '.0001' misses the ones a second save made."
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
            // reported as what they WOULD have done, and the counts are all zero â€” not
            // because we counted, but because Revit undid the transaction.
            //
            // Written was read INSIDE the transaction, before the rollback decision, and
            // ReadBack/Expected on the SetValueString path are in-transaction reads too.
            // The rollback undid every value they read. Emitting them under a field named
            // `value_written`, with readable:true and the value in it, is this object
            // contradicting its own note in the same breath â€” so they are cleared here,
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
                ["parameter_schema_check"] = InvariantJson(invariantStatus, before, afterInTx, reason),
                // A rollback caused by the SHAPE has to say so here too, or the reply
                // reads as a schema problem and the caller looks in the wrong place.
                ["geometry_check"] = GeometryJson(RolledBackGeometry, RolledBackBaseline),
                ["type_name_after"] = SafeCurrentTypeName(doc),
                ["protected_prefix_count_before"] = before.TrackedCount,
                ["protected_prefix_count_after"] = before.TrackedCount,
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
                           "blindly â€” the plan that produced this is the plan that would produce it again."
            };
        }

        private static string FinalNote(string invariant, int confirmed, int failed, int unknown, Plan plan)
        {
            var parts = new List<string>();
            if (invariant == "violated_after_commit" || invariant == "unknown_after_commit")
                parts.Add("READ parameter_schema_check.warning FIRST: the commit is done and the schema check does not " +
                          "hold on the post-commit read. Stop the batch. Note this is a SCHEMA check: the shape was not measured.");
            if (failed > 0)
                parts.Add(failed + " parameter(s) are NOT written: the family was re-read after the commit and does not " +
                          "carry the value.");
            if (unknown > 0)
                parts.Add(unknown + " parameter(s) are UNKNOWN â€” the setter ran, the commit is done, and the value could " +
                          "not be read back to settle it. They are counted as neither written nor failed.");
            var addFail = plan.Adds.Count(a => a.Outcome == OUT_NOT_WRITTEN);
            if (addFail > 0)
                parts.Add(addFail + " shared parameter(s) are NOT in the family after the commit. A family missing its " +
                          "required parameters is not homologated, whatever else here says.");
            var rmFail = plan.Removals.Count(r => r.Outcome == OUT_NOT_WRITTEN);
            if (rmFail > 0)
                parts.Add(rmFail + " parameter(s) are still present after RemoveParameter â€” usually because they are " +
                          "referenced, which is when they must stay.");
            var typeDelFail = plan.TypeDeletes.Count(t => t.Outcome != OUT_CONFIRMED);
            if (typeDelFail > 0)
                parts.Add(typeDelFail + " surplus type(s) are STILL in the family after the commit â€” the collapse did " +
                          "NOT happen. See types_delete_failed; a family that still carries its surplus types is not " +
                          "homologated, whatever the value rows say.");
            var clearFail = plan.FormulaClears.Count(f => f.Outcome == OUT_NOT_WRITTEN);
            if (clearFail > 0)
                parts.Add(clearFail + " formula(s) are STILL on their parameter after the commit. A surviving formula " +
                          "means Revit REFUSED the paired value write in 'values' â€” whatever that row says about " +
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
                          " The caller's data-template loader matches Family Name = Type Name; this family will not match.");
            if (parts.Count == 0) return null;
            parts.Add(confirmed + " value(s) confirmed by a fresh read after the commit. The commit is DONE, so this " +
                      "family may now be partially homologated â€” see each row's 'error'.");
            return string.Join(" ", parts.ToArray());
        }

        private static string ParseOnlyNote(int parseOnly)
        {
            if (parseOnly == 0) return null;
            return parseOnly + " of the confirmed value(s) were applied with SetValueString (a STRING on Double/Integer " +
                   "storage). Their expectation is a re-read of the parameter taken right after the setter, NOT your " +
                   "literal â€” Revit parsed the units internally and never returned the number. For those rows " +
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
        /// A Revit modal here does not wait for a human â€” nobody is looking at Revit â€” it
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
