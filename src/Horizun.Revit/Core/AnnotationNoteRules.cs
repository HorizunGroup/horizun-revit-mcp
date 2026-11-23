// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of three annotation features asked for in real use
// (FIELD FEEDBACK 2.4 / E5):
//
//   * on_failure for horizun_annotate: "atomic" (the default, all-or-nothing as it
//     always was) or "best_effort" - one row that cannot be built (a tag with no
//     tag family loaded for its category) no longer throws away the other eleven.
//     The confirmation token stays bound to WHICH rows will be applied: the set of
//     constructible indices is folded into the plan hash.
//   * model notes for horizun_plan_annotations: the text of a multilayer note is
//     generated from the element's CompoundStructure (exterior to interior: layer
//     function, material, thickness) or from its parameters, and it carries the
//     source element id so a printed note can be traced back to the model.
//   * load_tag_family for horizun_annotate: only a family the USER approved may be
//     loaded - one listed in <DataRoot>\approved-tag-families.json or one under an
//     installed Autodesk content library (C:\ProgramData\Autodesk\RVT <year>\).
//     An agent cannot approve a file by naming it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One layer of a CompoundStructure, read exterior to interior.</summary>
    public sealed class NoteLayer
    {
        public string Function;      // Revit's MaterialFunctionAssignment name (Structure, Finish1, ...)
        public string Material;      // material name, null when the layer has none
        public double ThicknessMm;
        public bool IsCore;
    }

    public static class AnnotationNoteRules
    {
        public const string Atomic = "atomic";
        public const string BestEffort = "best_effort";
        public const string ApprovedListFileName = "approved-tag-families.json";

        /// <summary>on_failure: null/absent is atomic; anything but the two words is refused, never guessed.</summary>
        public static string ParseOnFailure(JToken token, out string error)
        {
            error = null;
            if (token == null || token.Type == JTokenType.Null) return Atomic;
            string v = token.Type == JTokenType.String ? ((string)token ?? "").Trim().ToLowerInvariant() : null;
            if (v == Atomic || v == BestEffort) return v;
            error = "on_failure must be \"atomic\" (default: one failing row aborts the batch) or \"best_effort\" " +
                    "(rows that cannot be built are skipped and reported; the rest are created and verified).";
            return null;
        }

        /// <summary>
        /// The rows a best_effort dry run would apply and the rows it would not, over ALL <paramref name="actionCount"/> actions.
        /// A batch with invalid actions is rejected whole and rehearses nothing (no token), so NOTHING would be applied:
        /// <paramref name="willApply"/> is empty and every index is in <paramref name="willSkip"/> (field: 3 invalid of 15 gave
        /// the same 12 valid indices in both lists). When the batch was rehearsed (<paramref name="constructible"/> not null),
        /// the constructible rows apply and every other index is skipped.
        /// </summary>
        public static void ApplySets(int actionCount, IEnumerable<int> invalid, IEnumerable<int> constructible, out List<int> willApply, out List<int> willSkip)
        {
            var bad = new HashSet<int>(invalid ?? new int[0]);
            willApply = constructible == null || bad.Count > 0
                ? new List<int>()
                : constructible.Where(i => i >= 0 && i < actionCount).Distinct().OrderBy(i => i).ToList();
            var apply = new HashSet<int>(willApply);
            willSkip = Enumerable.Range(0, Math.Max(0, actionCount)).Where(i => !apply.Contains(i)).ToList();
        }

        /// <summary>
        /// The rows a best_effort token authorises, folded into the plan hash: a
        /// different set of constructible rows at apply time is a different plan.
        /// Atomic keeps the hash it always had, so no existing token changes meaning.
        /// </summary>
        public static string BindApplySet(string hash, string mode, IEnumerable<int> applyIndices)
        {
            if (mode != BestEffort) return hash;
            var ordered = (applyIndices ?? Enumerable.Empty<int>()).Distinct().OrderBy(i => i)
                .Select(i => i.ToString(CultureInfo.InvariantCulture));
            return hash + "|best_effort:" + string.Join(",", ordered);
        }

        // ---- note text ---------------------------------------------------------------

        private static readonly Dictionary<string, string> FunctionLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Structure"] = "Estructura", ["Substrate"] = "Sustrato", ["Insulation"] = "Aislamiento",
            ["Finish1"] = "Acabado 1", ["Finish2"] = "Acabado 2", ["Membrane"] = "Membrana",
            ["StructuralDeck"] = "Tablero estructural", ["None"] = "Sin función"
        };

        public static string FunctionLabel(string function)
        {
            if (string.IsNullOrWhiteSpace(function)) return "Sin función";
            string label;
            return FunctionLabels.TryGetValue(function.Trim(), out label) ? label : function.Trim();
        }

        public static string Mm(double mm)
        {
            if (double.IsNaN(mm) || double.IsInfinity(mm)) return "? mm";
            return Math.Round(mm, 1).ToString("0.#", CultureInfo.InvariantCulture) + " mm";
        }

        public static string IdLine(long sourceId) => "id " + sourceId.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// The multilayer note: a header with the type and total thickness, then one
        /// line per layer EXTERIOR to INTERIOR (the order the caller passes, which is
        /// CompoundStructure.GetLayers order), then the source id unless omitted.
        /// Membranes of zero thickness stay listed: they are real layers of the type.
        /// </summary>
        public static string LayersText(string typeName, IList<NoteLayer> layers, long sourceId, bool includeId)
        {
            if (layers == null || layers.Count == 0) throw new ArgumentException("the element has no layers to describe");
            var sb = new StringBuilder();
            double total = layers.Sum(l => l.ThicknessMm);
            sb.Append(string.IsNullOrWhiteSpace(typeName) ? "Tipo sin nombre" : typeName.Trim())
              .Append(" (").Append(Mm(total)).Append(')');
            for (int i = 0; i < layers.Count; i++)
            {
                NoteLayer l = layers[i];
                sb.Append('\n').Append(i + 1).Append(". ").Append(FunctionLabel(l.Function))
                  .Append(" - ").Append(string.IsNullOrWhiteSpace(l.Material) ? "sin material" : l.Material.Trim())
                  .Append(" - ").Append(Mm(l.ThicknessMm));
                if (l.IsCore) sb.Append(" [núcleo]");
            }
            if (includeId) sb.Append('\n').Append(IdLine(sourceId));
            return sb.ToString();
        }

        /// <summary>The parameter note: type, material, thickness and finish, each said - an unread value is "sin dato", never blank.</summary>
        public static string ParamsText(string typeName, string material, double? thicknessMm, string finish, long sourceId, bool includeId)
        {
            var sb = new StringBuilder();
            sb.Append("Tipo: ").Append(Or(typeName));
            sb.Append('\n').Append("Material: ").Append(Or(material));
            sb.Append('\n').Append("Espesor: ").Append(thicknessMm.HasValue ? Mm(thicknessMm.Value) : "sin dato");
            sb.Append('\n').Append("Acabado: ").Append(Or(finish));
            if (includeId) sb.Append('\n').Append(IdLine(sourceId));
            return sb.ToString();
        }

        private static string Or(string v) => string.IsNullOrWhiteSpace(v) ? "sin dato" : v.Trim();

        /// <summary>Finish of a layered element: the Finish1/Finish2 materials, exterior side first, deduplicated.</summary>
        public static string FinishFromLayers(IList<NoteLayer> layers)
        {
            if (layers == null) return null;
            var names = layers.Where(l => l.Function != null && l.Function.StartsWith("Finish", StringComparison.OrdinalIgnoreCase)
                                          && !string.IsNullOrWhiteSpace(l.Material))
                              .Select(l => l.Material.Trim()).Distinct(StringComparer.Ordinal).ToList();
            return names.Count == 0 ? null : string.Join(" / ", names);
        }

        /// <summary>Estimated paper extent (mm) of a note, for collision reservation: lines x longest line.</summary>
        public static void EstimateExtentMm(string text, double textHeightMm, out double widthMm, out double heightMm)
        {
            string[] lines = (text ?? "").Split('\n');
            int longest = lines.Length == 0 ? 0 : lines.Max(l => l.Length);
            double h = textHeightMm > 0 ? textHeightMm : 2.5;
            widthMm = Math.Max(1, longest) * h * 0.8;
            heightMm = Math.Max(1, lines.Length) * h * 1.7;
        }

        // ---- approved tag families -------------------------------------------------------

        public static string ApprovedListPath(string dataRoot) => Path.Combine(dataRoot ?? "", ApprovedListFileName);

        /// <summary>
        /// Reads the user's list: either a JSON array of paths or {"paths":[...]}.
        /// A list that cannot be parsed approves NOTHING and says why.
        /// </summary>
        public static List<string> ParseApprovedList(string json, out string error)
        {
            error = null;
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            JToken root;
            try { root = JToken.Parse(json); }
            catch (Exception ex) { error = "the approved list is not valid JSON: " + ex.Message; return result; }
            JArray arr = root as JArray;
            if (arr == null && root is JObject obj) arr = obj["paths"] as JArray;
            if (arr == null) { error = "the approved list must be a JSON array of paths or {\"paths\": [...]}"; return result; }
            foreach (JToken t in arr)
                if (t.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)t)) result.Add(((string)t).Trim());
            return result;
        }

        private static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return null; }
        }

        /// <summary>True when the path is under C:\ProgramData\Autodesk\RVT &lt;4-digit year&gt;\ (an installed content library).</summary>
        public static bool IsUnderAutodeskLibrary(string fullPath, string programData)
        {
            string p = Norm(fullPath), root = Norm(Path.Combine(programData ?? "", "Autodesk"));
            if (p == null || root == null || !p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = p.Substring(root.Length + 1);
            int slash = rest.IndexOf(Path.DirectorySeparatorChar);
            if (slash <= 0) return false;
            string first = rest.Substring(0, slash);
            if (!first.StartsWith("RVT ", StringComparison.OrdinalIgnoreCase) || first.Length != 8) return false;
            return first.Substring(4).All(char.IsDigit);
        }

        /// <summary>
        /// Approval verdict for a tag family path. Returns null when approved, else the
        /// refusal sentence - which says how the USER approves it.
        /// </summary>
        public static string CheckApproved(string path, IEnumerable<string> approved, string programData, string listPath)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                return "load_tag_family must be an absolute path to a .rfa file.";
            if (!string.Equals(Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase))
                return "load_tag_family must name a .rfa family file.";
            string full = Norm(path);
            if (full == null) return "load_tag_family is not a valid path.";
            if (IsUnderAutodeskLibrary(full, programData)) return null;
            if ((approved ?? Enumerable.Empty<string>()).Select(Norm).Any(a => a != null && string.Equals(a, full, StringComparison.OrdinalIgnoreCase)))
                return null;
            return "Refused: '" + full + "' is not an approved tag family. Only the USER approves families: add its full " +
                   "path to the JSON array in " + listPath + " (or use a family from the installed Autodesk library under " +
                   Path.Combine(programData ?? "", "Autodesk", "RVT <year>") + "), then call again. Nothing was loaded.";
        }

        /// <summary>A tag family's category is an annotation category whose BuiltInCategory name ends in "Tags" (OST_DoorTags, OST_MaterialTags, OST_MultiCategoryTags...).</summary>
        public static bool IsTagCategoryName(string builtInCategoryName) =>
            !string.IsNullOrEmpty(builtInCategoryName) &&
            builtInCategoryName.StartsWith("OST_", StringComparison.Ordinal) &&
            builtInCategoryName.EndsWith("Tags", StringComparison.Ordinal);
    }
}
