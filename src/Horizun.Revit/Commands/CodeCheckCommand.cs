// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_code_check. Original Horizun code. READ-ONLY.
//
// Runs a DECLARATIVE requirement set - the grammar of docs/requirement-set.md,
// extended with geometric `measure` assertions - over the active model. The norm
// arrives as data (standards/co-*.json are examples, not compiled in); this file
// only reads facts: parameters, and the measures in CodeCheckRules.Measures.
//
// WHY A TOOL OF ITS OWN. horizun_audit_model's requirement_set is a gate over the
// audit's aggregate COUNTS; horizun_audit_access takes a flat threshold map with no
// per-rule selector, citation or not_decidable. The per-element selector/assertion
// grammar had a loader (Core/RequirementSet.cs) and no tool. This is that tool, and
// it runs docs/requirement-sets/*.json unchanged beside the code-check sets.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CodeCheckCommand : ICommand
    {
        public string Name => "horizun_code_check";

        public string Description =>
            "Evaluate a declarative requirement set (parameters and geometric measures) over the active model. " +
            "Read-only; outcomes passes, fails, not_decidable, unreadable.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            CommandResult wrongDocument = DocumentGate.ReadGuard(doc, request, Name);
            if (wrongDocument != null) return wrongDocument;

            JObject setJson = request["requirement_set"] as JObject;
            string path = request.Value<string>("requirement_set_path");
            if ((setJson == null) == (path == null))
                return CommandResult.Fail("Give exactly one of requirement_set (inline object) or requirement_set_path (a .json file).");
            string baseDir = null;
            if (path != null)
            {
                if (!Path.IsPathRooted(path) || !File.Exists(path))
                    return CommandResult.Fail("requirement_set_path must be an absolute path to an existing .json file: " + path);
                try { setJson = JObject.Parse(File.ReadAllText(path)); }
                catch (Exception ex) { return CommandResult.Fail("requirement_set_path could not be read as JSON: " + ex.Message); }
                baseDir = Path.GetDirectoryName(path);
            }

            RequirementSet set;
            try
            {
                set = RequirementSet.Load(setJson, source =>
                {
                    string full = Path.IsPathRooted(source) || baseDir == null ? source : Path.Combine(baseDir, source);
                    return File.Exists(full) ? File.ReadAllText(full) : null;
                });
            }
            catch (RequirementSetException ex) { return CommandResult.Fail("The requirement set was refused: " + ex.Message); }

            int maxFindings = Math.Max(1, Math.Min(5000, request.Value<int?>("max_findings") ?? 200));
            bool includePasses = request.Value<bool?>("include_passes") ?? false;

            var coverage = new JObject();
            List<CheckedElement> facts = Collect(doc, set, coverage);
            JObject evaluation = CodeCheckRules.Evaluate(set, facts, maxFindings, includePasses);

            JObject header = setJson["requirement_set"] as JObject;
            var result = new JObject
            {
                ["document"] = doc.Title,
                ["requirement_set"] = new JObject
                {
                    ["id"] = set.Id, ["version"] = set.Version, ["title"] = set.Title,
                    ["sha256"] = Sha256(setJson.ToString(Formatting.None)),
                    ["path"] = path,
                    ["jurisdiction"] = header?["jurisdiction"]?.DeepClone(),
                    ["sources"] = header?["sources"]?.DeepClone()
                },
                ["elements_read"] = facts.Count,
                ["coverage"] = coverage
            };
            foreach (JProperty p in evaluation.Properties()) result[p.Name] = p.Value;
            return CommandResult.Ok(result);
        }

        private static string Sha256(string text)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Everything the rules can select, with the parameters and measures they ask about.</summary>
        private static List<CheckedElement> Collect(Document doc, RequirementSet set, JObject coverage)
        {
            var paramKeys = new HashSet<(string name, string unit)>();
            var measures = new HashSet<string>(StringComparer.Ordinal);
            bool levelExits = false;
            foreach (Requirement r in set.Rules)
            {
                if (r.AssertionParameter != null) paramKeys.Add((r.AssertionParameter, r.AssertionUnit));
                if (r.SelectorParameterExists != null) paramKeys.Add((r.SelectorParameterExists, null));
                if (r.SelectorParameterEqualsName != null) paramKeys.Add((r.SelectorParameterEqualsName, null));
                if (r.AssertionMeasure != null) measures.Add(r.AssertionMeasure);
                if (r.SelectorMeasure != null) measures.Add(r.SelectorMeasure);
                if (r.AssertionMeasure == "exit_count_minus_required") levelExits = true;
            }

            // Which categories: every rule's own, or - when a rule names none - every
            // model instance. A category that resolves to nothing is REPORTED, because a
            // misspelt category otherwise reads as "no elements, nothing wrong".
            var categoryIds = new Dictionary<long, string>();
            bool everything = false;
            var unresolved = new JArray();
            foreach (Requirement r in set.Rules)
            {
                if (r.SelectorCategory == null) { everything = true; continue; }
                Category c = ResolveCategory(doc, r.SelectorCategory);
                if (c == null) { if (!unresolved.Any(t => (string)t == r.SelectorCategory)) unresolved.Add(r.SelectorCategory); continue; }
                categoryIds[Rid.Value(c.Id)] = r.SelectorCategory;
            }
            coverage["categories_unresolved"] = unresolved;

            var elements = new List<Element>();
            if (everything)
                elements.AddRange(new FilteredElementCollector(doc).WhereElementIsNotElementType()
                    .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model));
            else
                foreach (long id in categoryIds.Keys)
                    elements.AddRange(new FilteredElementCollector(doc).OfCategoryId(Rid.Make(id)).WhereElementIsNotElementType());
            elements = elements.GroupBy(e => Rid.Value(e.Id)).Select(g => g.First()).ToList();

            var byCategory = new JObject();
            var facts = new List<CheckedElement>();
            int unreadable = 0;
            foreach (Element e in elements)
            {
                CheckedElement f;
                try { f = Fact(doc, e, paramKeys, measures); }
                catch (Exception) { unreadable++; continue; }
                facts.Add(f);
                string k = f.CategoryToken ?? "(none)";
                byCategory[k] = (byCategory.Value<int?>(k) ?? 0) + 1;
            }
            coverage["elements_by_category"] = byCategory;
            coverage["elements_unreadable"] = unreadable;
            if (levelExits) AttachLevelContents(doc, facts, set, coverage);
            return facts;
        }

        internal static Category ResolveCategory(Document doc, string text)
        {
            if (text.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse(text, true, out BuiltInCategory bic))
            {
                try { return Category.GetCategory(doc, bic); } catch { return null; }
            }
            foreach (Category c in doc.Settings.Categories)
                if (string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }
    }
}
