// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_annotate load_tag_family: load ONE tag family (.rfa) the USER approved.
//
// FIELD FEEDBACK 2.4: a batch of tags died because the category had no
// tag family loaded, and nothing typed could load one. Loading arbitrary content
// on an agent's word is not acceptable, so approval lives outside the agent:
//   * a path listed in <DataRoot>\approved-tag-families.json (written by the user), or
//   * a file under an installed Autodesk content library, C:\ProgramData\Autodesk\RVT <year>\.
// Anything else is refused with the sentence that says how to approve it.
//
// The dry run LOADS the family inside a transaction that is always rolled back, so
// what it reports (family name, category, types) is what Revit itself made of the
// file, plus the file's SHA-256. The SHA-256 is bound into the confirmation: a
// file changed after the dry run makes the token stale. The apply runs on
// ModelEditRunner (TransactionGroup, re-read after the commit, rollback on any
// disagreement): the family must exist afterwards with the same category and the
// same type names. A family that is not of a tag category is refused, and a family
// whose name is already loaded is refused (Revit would not overwrite it, and the
// loaded content cannot be proven equal to the file).
//
// Not an ICommand: AnnotateCommand routes here, and ModelEditRunner.Run resolves the
// document through DocumentGate.ForMutation BEFORE Plan opens its rehearsal transaction.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class LoadTagFamilyEdit
    {
        private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
        { "target_document", "load_tag_family", "dry_run", "confirmation_token", "transaction_name", "idempotency_key", "units" };

        public static CommandResult Run(UIApplication app, string paramsJson, string name)
            => ModelEditRunner.Run(app, paramsJson, name, "Horizun: load tag family", Validate, Plan);

        private static string Validate(JObject r)
        {
            foreach (var prop in r.Properties())
                if (!Allowed.Contains(prop.Name))
                    return "load_tag_family does not take '" + prop.Name + "'. Nothing was changed.";
            if (r["load_tag_family"]?.Type != JTokenType.String) return "load_tag_family must be a path string.";
            return null;
        }

        private static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static string BuiltInName(Category c)
        {
            if (c == null) return null;
            // Category.BuiltInCategory exists from Revit 2022 (see ModelDiffReader); never through a boxed int.
            try { BuiltInCategory b = c.BuiltInCategory; return b == BuiltInCategory.INVALID ? null : b.ToString(); }
            catch { return null; }
        }

        private static Family FindFamily(Document doc, string familyName) =>
            new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.Ordinal));

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            string path = r.Value<string>("load_tag_family");
            string listPath = AnnotationNoteRules.ApprovedListPath(HorizunPaths.DataRoot());
            string listError = null;
            List<string> approved = new List<string>();
            if (File.Exists(listPath))
                approved = AnnotationNoteRules.ParseApprovedList(File.ReadAllText(listPath), out listError);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string verdict = AnnotationNoteRules.CheckApproved(path, approved, programData, listPath);
            if (verdict != null)
                throw new ArgumentException(verdict + (listError != null ? " (The list itself could not be read: " + listError + ")" : ""));
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new ArgumentException("load_tag_family: the file does not exist: " + full);
            string newer = FamilyVersionGate.NewerError(doc.Application, full, out JObject newerDetail);
            if (newer != null) throw new ArgumentException(newer);
            string sha = Sha256(full);
            string familyName = Path.GetFileNameWithoutExtension(full);
            Family already = FindFamily(doc, familyName);
            if (already != null)
                throw new ArgumentException("A family named '" + familyName + "' is already loaded (id " + Rid.Value(already.Id) +
                    "); Revit would not replace it and its content cannot be proven equal to the file. Use its types directly.");

            // Rehearsal: load inside a transaction that is always rolled back, and read
            // what Revit made of the file.
            string catName = null, bic = null; var types = new List<string>(); string loadedName = null;
            using (var tx = new Transaction(doc, "Horizun: rehearse load tag family"))
            {
                tx.Start();
                try
                {
                    Family fam;
                    if (!doc.LoadFamily(full, out fam) || fam == null)
                        throw new InvalidOperationException("Revit did not load the family from " + full + ".");
                    loadedName = fam.Name;
                    catName = fam.FamilyCategory?.Name;
                    bic = BuiltInName(fam.FamilyCategory);
                    types = fam.GetFamilySymbolIds().Select(id => doc.GetElement(id)?.Name).Where(n => n != null)
                        .OrderBy(n => n, StringComparer.Ordinal).ToList();
                }
                finally
                {
                    var rb = Guard.RollBack(tx);
                    if (!rb.Confirmed)
                        throw new InvalidOperationException("The rehearsal load could not be rolled back (Revit reported '" +
                            rb.StatusName + "'); re-read the model before anything else.");
                }
            }
            if (!AnnotationNoteRules.IsTagCategoryName(bic))
                throw new ArgumentException("'" + loadedName + "' is a " + (catName ?? "category-less") + " family (" + (bic ?? "?") +
                    "), not a tag family; load_tag_family only loads tag families. Nothing was loaded.");

            var edit = new ArchModelEdit();
            edit.Summary = new JObject
            {
                ["path"] = full, ["sha256"] = sha, ["family_name"] = loadedName, ["category"] = catName,
                ["built_in_category"] = bic, ["types"] = new JArray(types),
                ["approved_by"] = AnnotationNoteRules.IsUnderAutodeskLibrary(full, programData) ? "autodesk_library" : listPath,
                ["rehearsal"] = "loaded inside a transaction that was rolled back; the values above are Revit's own reading of the file"
            };
            edit.Planned.Add(new PlannedElement
            {
                UniqueId = "rfa:" + sha, Category = catName, TypeName = loadedName, Action = PlannedAction.Create,
                ProposedValues = new Dictionary<string, string> { { "path", full }, { "sha256", sha } }
            });
            string bicExpected = bic; var typesExpected = types; string nameExpected = loadedName;
            edit.Apply = d =>
            {
                // The file must still be the one approved in the dry run.
                if (Sha256(full) != sha) throw new InvalidOperationException("the .rfa changed after the dry run (SHA-256 differs).");
                Family fam;
                if (!d.LoadFamily(full, out fam) || fam == null) throw new InvalidOperationException("Revit did not load the family.");
                edit.Evidence["family_id"] = Rid.Value(fam.Id);
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("family_loaded", "category", "types");
                Family fam = FindFamily(d, nameExpected);
                check.Compare("family_loaded", true, fam != null);
                check.Compare("category", bicExpected, fam == null ? null : BuiltInName(fam.FamilyCategory));
                List<string> now = fam == null ? new List<string>() : fam.GetFamilySymbolIds()
                    .Select(id => d.GetElement(id)?.Name).Where(n => n != null).OrderBy(n => n, StringComparer.Ordinal).ToList();
                check.Compare("types", string.Join("|", typesExpected), string.Join("|", now));
                if (fam != null)
                {
                    edit.Evidence["family_id"] = Rid.Value(fam.Id);
                    edit.Evidence["symbols"] = new JArray(fam.GetFamilySymbolIds().Select(id => new JObject
                        { ["id"] = Rid.Value(id), ["name"] = d.GetElement(id)?.Name }));
                }
                return check;
            };
            return edit;
        }
    }
}
