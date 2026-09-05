// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// QUANTITIES AGAINST A BUDGET BASELINE: the join nobody else makes, made honestly.
//
// A takeoff is a set of numbers wearing codes. A budget is a set of priced lines
// wearing the same codes. Every pipeline between them - the Excel somebody
// keeps, the Power BI table somebody refreshes - performs the join by hand and
// loses, on the way, exactly the facts that decide whether the number is worth
// anything: how many elements the model quantity actually covers, whether the
// two sides even measure in the same unit, and whether a price was ever agreed.
//
// This file performs the join and refuses to lose those facts:
//
//   * A UNIT IS NEVER CONVERTED SILENTLY. m3 against m2 is not a delta, it is a
//     category error; m3 against ft3 is a delta only if the caller declared the
//     factor. Undeclared pairs come back unit_incompatible.
//   * A PRICE IS NEVER INVENTED. The model amount is model quantity times the
//     BASELINE unit price, so the amount delta isolates quantity drift at an
//     agreed rate. When the baseline carries no price the price delta is
//     not_available, and the sheet says so instead of showing zero.
//   * AN INCOMPLETE READ IS NEVER A ZERO. A code whose elements could not all be
//     measured has a lower bound, not a quantity; it is reported not_comparable
//     with the partial sum beside the count it fails to cover.
//   * EVERY LINE KEEPS ITS TRACE: the element ids and documents behind the model
//     quantity, and the baseline row indices behind the budget one.
//
// Revit-free, because whether two numbers may be subtracted is a judgement about
// the numbers and their labels, and a judgement that needs a Document to be
// made cannot be proved at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The closed vocabulary a compared code can land in.</summary>
    public static class BudgetLineStatus
    {
        public const string Added = "added";
        public const string Removed = "removed";
        public const string Modified = "modified";
        public const string Unchanged = "unchanged";
        public const string NotComparable = "not_comparable";
    }

    /// <summary>Why a code present on both sides still produced no delta.</summary>
    public static class BudgetNotComparable
    {
        public const string UnitIncompatible = "unit_incompatible";
        public const string AmbiguousQuantity = "ambiguous_quantity";
        public const string IncompleteRead = "incomplete_read";
        public const string PartialCoverage = "partial_coverage";
        public const string ModelAbsent = "model_absent";
        public const string ModelInvalid = "model_invalid";
        public const string BaselineAbsent = "baseline_absent";
        public const string BaselineInvalid = "baseline_invalid";
        public const string BaselineAmbiguousUnit = "baseline_ambiguous_unit";
    }

    /// <summary>The per-element quantity states a takeoff row may carry.</summary>
    public static class QuantityState
    {
        public const string Measured = "measured";
        public const string Absent = "absent";
        public const string Empty = "empty";
        public const string Unreadable = "unreadable";
        public const string Invalid = "invalid";
    }

    /// <summary>The three non-values a classification code may be. Same strings the takeoff writes.</summary>
    public static class ClassificationNonValue
    {
        public const string NoSuchParameter = "(no such parameter)";
        public const string Empty = "(empty)";
        public const string Unreadable = "(unreadable)";

        public static bool IsNonValue(string code) =>
            code == null || code == NoSuchParameter || code == Empty || code == Unreadable;
    }

    /// <summary>
    /// Where a code sits in a catalogue the caller supplied. The SAME strings as
    /// CodeStatus in DeliveryReadinessRules.cs - pinned by a Core test that compiles
    /// both - restated here so the server can link this file without the readiness
    /// chain (ParameterRule and friends) that CodeStatus lives beside.
    /// </summary>
    public static class BudgetCodeStatus
    {
        public const string Leaf = "leaf";
        public const string GroupNotTerminal = "group_not_terminal";
        public const string NotInCatalogue = "not_in_catalogue";
        public const string Invalid = "invalid";
        public const string CatalogueNotSupplied = "catalogue_not_supplied";
    }

    /// <summary>
    /// The catalogue shape ClassificationCatalogueRules reads: { version, name?, codes:
    /// { "A-1": false, "A-1-1": true } } where the boolean is IS-LEAF, declared rather
    /// than inferred - prefix inference guesses a taxonomy's shape and guesses wrong on
    /// every standard that reuses its separators.
    /// </summary>
    public sealed class BudgetCatalogue
    {
        public string Version;
        public string Name;
        public Dictionary<string, bool> Codes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public static BudgetCatalogue Read(JToken token, out string problem)
        {
            problem = null;
            var o = token as JObject;
            if (o == null) { problem = "the catalogue must be an object with 'version' and 'codes'."; return null; }
            var c = new BudgetCatalogue();
            JToken v = o["version"];
            if (v == null || v.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)v))
            { problem = "the catalogue needs a 'version', so a report can say which taxonomy produced it."; return null; }
            c.Version = (string)v;
            c.Name = o["name"] == null || o["name"].Type != JTokenType.String ? null : (string)o["name"];
            foreach (JProperty p in o.Properties())
                if (p.Name != "version" && p.Name != "name" && p.Name != "codes")
                { problem = "catalogue." + p.Name + " is not a known key (version, name, codes)."; return null; }
            var codes = o["codes"] as JObject;
            if (codes == null) { problem = "the catalogue needs a 'codes' object mapping each code to whether it is a LEAF."; return null; }
            foreach (JProperty p in codes.Properties())
            {
                if (p.Value.Type != JTokenType.Boolean)
                { problem = "the catalogue entry for '" + p.Name + "' must be true or false - whether the code is a LEAF."; return null; }
                if (c.Codes.ContainsKey(p.Name)) { problem = "'" + p.Name + "' appears twice in the catalogue."; return null; }
                c.Codes[p.Name] = (bool)p.Value;
            }
            if (c.Codes.Count == 0)
            { problem = "the catalogue lists no codes, so every code would be reported absent from it. Omit the catalogue instead of supplying an empty one."; return null; }
            return c;
        }

        public string Classify(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return BudgetCodeStatus.Invalid;
            bool isLeaf;
            if (!Codes.TryGetValue(code.Trim(), out isLeaf)) return BudgetCodeStatus.NotInCatalogue;
            return isLeaf ? BudgetCodeStatus.Leaf : BudgetCodeStatus.GroupNotTerminal;
        }
    }

    public sealed class BudgetComparisonMapping
    {
        public string CodeField = "classification_code";
        /// <summary>Pins the model quantity to compare; null lets the unit choose, refusing ties.</summary>
        public string QuantityField;
        /// <summary>from|to (lower-cased) to factor. Only declared pairs, only the declared direction.</summary>
        public Dictionary<string, double> UnitConversions = new Dictionary<string, double>(StringComparer.Ordinal);
        public double? QuantityPct;
        public double? QuantityAbs;
        /// <summary>Opt-in: compare a code whose elements do not all carry the quantity.</summary>
        public bool ComparePartialCoverage;
        public BudgetCatalogue Catalogue;

        public static string ConversionKey(string from, string to) =>
            Normalise(from) + "|" + Normalise(to);

        public static string Normalise(string unit) => (unit ?? "").Trim().ToLowerInvariant();
    }

    public static class BudgetComparisonRules
    {
        public const string Means =
            "Per code: added = in the model, not in the baseline; removed = in the baseline, not in the model; " +
            "unchanged / modified = both present, both complete, both in one unit, and the quantity delta inside / " +
            "outside the declared tolerance; not_comparable = both present but no honest subtraction exists, with " +
            "the reason named. The model amount is the model quantity at the BASELINE unit price - no price is " +
            "ever invented, and a baseline line without one reports price.state = not_available. An incomplete " +
            "read is a lower bound, never a zero, and is not compared.";

        // ------------------------------------------------------------------
        // Mapping: what the caller declared, refused where it is malformed.
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads the mapping block. Unknown keys are refused - a misspelt tolerance is a
        /// tolerance that silently became "exact", and a caller cannot see that from the
        /// answer.
        /// </summary>
        public static BudgetComparisonMapping ReadMapping(JToken token, out string problem)
        {
            problem = null;
            var m = new BudgetComparisonMapping();
            if (token == null || token.Type == JTokenType.Null) return m;
            var o = token as JObject;
            if (o == null) { problem = "mapping must be an object."; return null; }

            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "code_field", "quantity_field", "unit_conversions", "tolerances", "rules", "catalogue"
            };
            foreach (JProperty p in o.Properties())
                if (!allowed.Contains(p.Name))
                {
                    problem = "mapping." + p.Name + " is not a known key. Known: " + string.Join(", ", allowed.OrderBy(a => a, StringComparer.Ordinal)) + ".";
                    return null;
                }

            if (o["code_field"] != null)
            {
                if (o["code_field"].Type != JTokenType.String || string.IsNullOrWhiteSpace((string)o["code_field"]))
                { problem = "mapping.code_field must be a non-empty string."; return null; }
                m.CodeField = ((string)o["code_field"]).Trim();
            }
            if (o["quantity_field"] != null)
            {
                if (o["quantity_field"].Type != JTokenType.String || string.IsNullOrWhiteSpace((string)o["quantity_field"]))
                { problem = "mapping.quantity_field must be a non-empty string naming one of the takeoff quantities."; return null; }
                m.QuantityField = ((string)o["quantity_field"]).Trim();
            }

            JToken conv = o["unit_conversions"];
            if (conv != null && conv.Type != JTokenType.Null)
            {
                var arr = conv as JArray;
                if (arr == null) { problem = "mapping.unit_conversions must be an array of {from, to, factor}."; return null; }
                for (int i = 0; i < arr.Count; i++)
                {
                    var c = arr[i] as JObject;
                    if (c == null) { problem = "mapping.unit_conversions[" + i + "] must be an object {from, to, factor}."; return null; }
                    foreach (JProperty p in c.Properties())
                        if (p.Name != "from" && p.Name != "to" && p.Name != "factor")
                        { problem = "mapping.unit_conversions[" + i + "]." + p.Name + " is not a known key (from, to, factor)."; return null; }
                    string from = (string)c["from"], to = (string)c["to"];
                    if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                    { problem = "mapping.unit_conversions[" + i + "] needs non-empty 'from' and 'to'."; return null; }
                    double factor;
                    if (!TryNumber(c["factor"], out factor) || factor <= 0 || double.IsInfinity(factor))
                    { problem = "mapping.unit_conversions[" + i + "].factor must be a finite number greater than zero."; return null; }
                    string key = BudgetComparisonMapping.ConversionKey(from, to);
                    if (m.UnitConversions.ContainsKey(key))
                    { problem = "mapping.unit_conversions declares " + from + " -> " + to + " twice."; return null; }
                    if (BudgetComparisonMapping.Normalise(from) == BudgetComparisonMapping.Normalise(to))
                    { problem = "mapping.unit_conversions[" + i + "] converts " + from + " to itself."; return null; }
                    m.UnitConversions[key] = factor;
                }
            }

            JToken tol = o["tolerances"];
            if (tol != null && tol.Type != JTokenType.Null)
            {
                var t = tol as JObject;
                if (t == null) { problem = "mapping.tolerances must be an object {quantity_pct?, quantity_abs?}."; return null; }
                foreach (JProperty p in t.Properties())
                {
                    double v;
                    if (p.Name != "quantity_pct" && p.Name != "quantity_abs")
                    { problem = "mapping.tolerances." + p.Name + " is not a known key (quantity_pct, quantity_abs)."; return null; }
                    if (!TryNumber(p.Value, out v) || v < 0 || double.IsInfinity(v))
                    { problem = "mapping.tolerances." + p.Name + " must be a finite number >= 0."; return null; }
                    if (p.Name == "quantity_pct") m.QuantityPct = v; else m.QuantityAbs = v;
                }
            }

            JToken rules = o["rules"];
            if (rules != null && rules.Type != JTokenType.Null)
            {
                var r = rules as JObject;
                if (r == null) { problem = "mapping.rules must be an object."; return null; }
                foreach (JProperty p in r.Properties())
                {
                    if (p.Name != "compare_partial_coverage")
                    { problem = "mapping.rules." + p.Name + " is not a known rule (compare_partial_coverage)."; return null; }
                    if (p.Value.Type != JTokenType.Boolean)
                    { problem = "mapping.rules.compare_partial_coverage must be true or false."; return null; }
                    m.ComparePartialCoverage = (bool)p.Value;
                }
            }

            if (o["catalogue"] != null && o["catalogue"].Type != JTokenType.Null)
            {
                string catalogueProblem;
                BudgetCatalogue cat = BudgetCatalogue.Read(o["catalogue"], out catalogueProblem);
                if (cat == null) { problem = "mapping.catalogue: " + catalogueProblem; return null; }
                m.Catalogue = cat;
            }
            return m;
        }

        // ------------------------------------------------------------------
        // Baseline: what the budget says, one parsed line per row.
        // ------------------------------------------------------------------

        public sealed class BaselineLine
        {
            public int RowIndex;
            public string Code;
            public string Description;
            public string Unit;
            /// <summary>measured | absent | invalid.</summary>
            public string QuantityState;
            public double? Quantity;
            public string QuantityRaw;
            /// <summary>measured | absent | invalid.</summary>
            public string UnitPriceState;
            public double? UnitPrice;
            public string Currency;
        }

        /// <summary>
        /// Reads the baseline lines: [{code, description?, unit, quantity, unit_price?, currency?, row_index?}].
        /// A blank code is skipped and COUNTED, because a subtotal row is not a budget line and
        /// silently dropping it is different from silently pricing it.
        /// </summary>
        public static List<BaselineLine> ReadBaseline(JToken token, out int skippedBlankCode, out string problem)
        {
            problem = null;
            skippedBlankCode = 0;
            var arr = token as JArray;
            if (arr == null) { problem = "baseline must be an array of budget lines {code, description?, unit, quantity, unit_price?, currency?}."; return null; }
            var lines = new List<BaselineLine>();
            for (int i = 0; i < arr.Count; i++)
            {
                var o = arr[i] as JObject;
                if (o == null) { problem = "baseline[" + i + "] must be an object."; return null; }
                foreach (JProperty p in o.Properties())
                    if (p.Name != "code" && p.Name != "description" && p.Name != "unit" && p.Name != "quantity" &&
                        p.Name != "unit_price" && p.Name != "currency" && p.Name != "row_index")
                    { problem = "baseline[" + i + "]." + p.Name + " is not a known key."; return null; }

                string code = TextOf(o["code"]);
                if (string.IsNullOrWhiteSpace(code)) { skippedBlankCode++; continue; }

                var line = new BaselineLine
                {
                    RowIndex = o["row_index"] != null && o["row_index"].Type == JTokenType.Integer ? (int)o["row_index"] : i,
                    Code = code.Trim(),
                    Description = TextOf(o["description"]),
                    Unit = TextOf(o["unit"]),
                    Currency = TextOf(o["currency"])
                };
                ReadNumberState(o["quantity"], out line.QuantityState, out line.Quantity, out line.QuantityRaw);
                string priceRaw;
                ReadNumberState(o["unit_price"], out line.UnitPriceState, out line.UnitPrice, out priceRaw);
                lines.Add(line);
            }
            return lines;
        }

        // ------------------------------------------------------------------
        // Model rows: what the takeoff measured, per element.
        // ------------------------------------------------------------------

        public sealed class ModelRow
        {
            public string ElementId;
            public string Document;
            public string LinkInstanceId;
            public string Code;
            /// <summary>quantity name -> reading.</summary>
            public Dictionary<string, QuantityReading> Quantities = new Dictionary<string, QuantityReading>(StringComparer.Ordinal);
        }

        public sealed class QuantityReading
        {
            public string State;
            public double? Value;
            public string Unit;
            public string Reason;
        }

        /// <summary>
        /// Reads takeoff rows. Accepts the bare row array or the whole horizun_quantities
        /// takeoff reply (its 'rows'); a reply whose rows were TRUNCATED is refused, because
        /// a comparison over a prefix of the model is a comparison of a smaller building.
        /// </summary>
        public static List<ModelRow> ReadModelRows(JToken token, string codeField, out string problem)
        {
            problem = null;
            JArray arr = token as JArray;
            if (arr == null && token is JObject reply)
            {
                if (reply["mode"] != null && (string)reply["mode"] != "takeoff")
                { problem = "model_rows is a horizun_quantities reply in mode '" + (string)reply["mode"] + "'; the comparison needs mode 'takeoff' rows (per-element classification_code plus named quantities)."; return null; }
                if (reply["truncated"] != null && reply["truncated"].Type == JTokenType.Boolean && (bool)reply["truncated"])
                { problem = "model_rows is a horizun_quantities reply whose rows were TRUNCATED (rows_matching=" + (reply["rows_matching"] ?? "?") + ", shown=" + (reply["shown"] ?? "?") + "). Re-run the takeoff with 'top' at least rows_matching; a comparison over a prefix of the model would price a smaller building."; return null; }
                arr = reply["rows"] as JArray;
                if (arr == null) { problem = "model_rows is an object without a 'rows' array."; return null; }
            }
            if (arr == null) { problem = "model_rows must be an array of takeoff rows, or the horizun_quantities takeoff reply that carries them."; return null; }

            var rows = new List<ModelRow>();
            for (int i = 0; i < arr.Count; i++)
            {
                var o = arr[i] as JObject;
                if (o == null) { problem = "model_rows[" + i + "] must be an object."; return null; }
                JToken codeTok = o[codeField];
                if (codeTok == null)
                { problem = "model_rows[" + i + "] has no '" + codeField + "' field (mapping.code_field). The takeoff writes classification_code; pass the field your rows actually carry."; return null; }
                var row = new ModelRow
                {
                    ElementId = TextOf(o["element_id"]),
                    Document = TextOf(o["document"]),
                    LinkInstanceId = TextOf(o["link_instance_id"]),
                    Code = codeTok.Type == JTokenType.Null ? ClassificationNonValue.Empty : TextOf(codeTok)
                };
                if (string.IsNullOrWhiteSpace(row.ElementId))
                { problem = "model_rows[" + i + "] has no element_id; traceability is not optional."; return null; }
                var q = o["quantities"] as JObject;
                if (q == null)
                { problem = "model_rows[" + i + "] has no 'quantities' object. Rows must come from horizun_quantities mode 'takeoff'."; return null; }
                foreach (JProperty p in q.Properties())
                {
                    var r = p.Value as JObject;
                    if (r == null) { problem = "model_rows[" + i + "].quantities." + p.Name + " must be an object {value, state, unit, reason}."; return null; }
                    var reading = new QuantityReading
                    {
                        State = TextOf(r["state"]),
                        Unit = TextOf(r["unit"]),
                        Reason = TextOf(r["reason"])
                    };
                    double v;
                    if (reading.State == null) { problem = "model_rows[" + i + "].quantities." + p.Name + " has no state."; return null; }
                    if (reading.State == QuantityState.Measured)
                    {
                        if (!TryNumber(r["value"], out v))
                        { problem = "model_rows[" + i + "].quantities." + p.Name + " says measured but carries no finite number."; return null; }
                        reading.Value = v;
                    }
                    else if (reading.State != QuantityState.Absent && reading.State != QuantityState.Empty &&
                             reading.State != QuantityState.Unreadable && reading.State != QuantityState.Invalid)
                    { problem = "model_rows[" + i + "].quantities." + p.Name + ".state '" + reading.State + "' is not one of measured, absent, empty, unreadable, invalid."; return null; }
                    if (string.IsNullOrWhiteSpace(reading.Unit))
                    { problem = "model_rows[" + i + "].quantities." + p.Name + " declares no unit; the takeoff writes the caller-declared unit on every reading."; return null; }
                    row.Quantities[p.Name] = reading;
                }
                rows.Add(row);
            }
            return rows;
        }

        // ------------------------------------------------------------------
        // The comparison.
        // ------------------------------------------------------------------

        private sealed class CodeAggregate
        {
            public List<ModelRow> Rows = new List<ModelRow>();
        }

        private sealed class QuantityAggregate
        {
            public string Name;
            public string Unit;
            public double Total;
            public int Elements, Measured, Absent, Empty, Unreadable, Invalid;
            public HashSet<string> Units = new HashSet<string>(StringComparer.Ordinal);
        }

        public static JObject Compare(List<ModelRow> modelRows, List<BaselineLine> baseline, BudgetComparisonMapping mapping)
        {
            if (modelRows == null) throw new ArgumentNullException(nameof(modelRows));
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (mapping == null) mapping = new BudgetComparisonMapping();

            // Model rows by code; the non-values are pooled apart so a rollup never
            // carries "(empty)" as though it were a budget line somebody could price.
            var byCode = new Dictionary<string, CodeAggregate>(StringComparer.Ordinal);
            var unclassified = new Dictionary<string, List<ModelRow>>(StringComparer.Ordinal)
            {
                [ClassificationNonValue.NoSuchParameter] = new List<ModelRow>(),
                [ClassificationNonValue.Empty] = new List<ModelRow>(),
                [ClassificationNonValue.Unreadable] = new List<ModelRow>()
            };
            foreach (ModelRow r in modelRows)
            {
                string code = r.Code == null ? ClassificationNonValue.Empty : r.Code.Trim();
                if (code.Length == 0) code = ClassificationNonValue.Empty;
                if (unclassified.ContainsKey(code)) { unclassified[code].Add(r); continue; }
                CodeAggregate agg;
                if (!byCode.TryGetValue(code, out agg)) byCode[code] = agg = new CodeAggregate();
                agg.Rows.Add(r);
            }

            var baseByCode = new Dictionary<string, List<BaselineLine>>(StringComparer.Ordinal);
            foreach (BaselineLine b in baseline)
            {
                List<BaselineLine> list;
                if (!baseByCode.TryGetValue(b.Code, out list)) baseByCode[b.Code] = list = new List<BaselineLine>();
                list.Add(b);
            }

            var codes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string c in byCode.Keys) codes.Add(c);
            foreach (string c in baseByCode.Keys) codes.Add(c);

            var lines = new JArray();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [BudgetLineStatus.Added] = 0, [BudgetLineStatus.Removed] = 0, [BudgetLineStatus.Modified] = 0,
                [BudgetLineStatus.Unchanged] = 0, [BudgetLineStatus.NotComparable] = 0
            };
            var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
            double baselineAmountKnown = 0, modelAmountKnown = 0;
            int pricedLines = 0;

            foreach (string code in codes)
            {
                CodeAggregate model;
                List<BaselineLine> baseLines;
                byCode.TryGetValue(code, out model);
                baseByCode.TryGetValue(code, out baseLines);

                JObject line = CompareOne(code, model, baseLines, mapping);
                string status = (string)line["status"];
                counts[status]++;
                if (status == BudgetLineStatus.NotComparable)
                {
                    string reason = (string)line["reason"];
                    int n; reasons.TryGetValue(reason, out n); reasons[reason] = n + 1;
                }
                JObject price = line["price"] as JObject;
                if (price != null && (string)price["state"] == "measured")
                {
                    pricedLines++;
                    baselineAmountKnown += (double)price["baseline_amount"];
                    modelAmountKnown += (double)price["model_amount"];
                }
                lines.Add(line);
            }

            var reasonsJson = new JObject();
            foreach (var kv in reasons.OrderBy(k => k.Key, StringComparer.Ordinal)) reasonsJson[kv.Key] = kv.Value;

            int unclassifiedTotal = unclassified.Values.Sum(l => l.Count);
            var unclassifiedJson = new JObject
            {
                ["elements"] = unclassifiedTotal,
                ["no_such_parameter"] = Trace(unclassified[ClassificationNonValue.NoSuchParameter]),
                ["empty"] = Trace(unclassified[ClassificationNonValue.Empty]),
                ["unreadable"] = Trace(unclassified[ClassificationNonValue.Unreadable]),
                ["means"] = "elements whose classification code is a NON-VALUE. They are in the model and in no " +
                            "budget line, and they are not 'added' codes: nobody can price '(empty)'. Fix the " +
                            "classification and run the takeoff again; a comparison that pooled them under a " +
                            "code would under-count every code they belong to."
            };

            return new JObject
            {
                ["code_field"] = mapping.CodeField,
                ["quantity_selection"] = mapping.QuantityField ?? "by unit: the model quantity whose declared unit equals the baseline unit, or has a declared conversion to it; a tie is refused",
                ["tolerances"] = new JObject
                {
                    ["quantity_pct"] = mapping.QuantityPct.HasValue ? (JToken)mapping.QuantityPct.Value : JValue.CreateNull(),
                    ["quantity_abs"] = mapping.QuantityAbs.HasValue ? (JToken)mapping.QuantityAbs.Value : JValue.CreateNull(),
                    ["means"] = "a line is unchanged when |delta| <= quantity_abs OR |delta| / baseline <= quantity_pct / 100; with neither declared the quantities must match exactly."
                },
                ["unit_conversions_declared"] = mapping.UnitConversions.Count,
                ["compare_partial_coverage"] = mapping.ComparePartialCoverage,
                ["catalogue"] = mapping.Catalogue == null ? (JToken)JValue.CreateNull() : new JObject
                {
                    ["version"] = mapping.Catalogue.Version, ["name"] = mapping.Catalogue.Name, ["codes"] = mapping.Catalogue.Codes.Count
                },
                ["summary"] = new JObject
                {
                    ["codes"] = codes.Count,
                    ["added"] = counts[BudgetLineStatus.Added],
                    ["removed"] = counts[BudgetLineStatus.Removed],
                    ["modified"] = counts[BudgetLineStatus.Modified],
                    ["unchanged"] = counts[BudgetLineStatus.Unchanged],
                    ["not_comparable"] = counts[BudgetLineStatus.NotComparable],
                    ["not_comparable_reasons"] = reasonsJson,
                    ["model_rows"] = modelRows.Count,
                    ["model_elements_classified"] = modelRows.Count - unclassifiedTotal,
                    ["model_elements_unclassified"] = unclassifiedTotal,
                    ["baseline_lines"] = baseline.Count,
                    ["priced_lines_compared"] = pricedLines,
                    ["baseline_amount_over_priced_compared_lines"] = Round(baselineAmountKnown),
                    ["model_amount_over_priced_compared_lines"] = Round(modelAmountKnown),
                    ["amount_delta_over_priced_compared_lines"] = Round(modelAmountKnown - baselineAmountKnown),
                    ["amounts_are_complete"] = pricedLines == counts[BudgetLineStatus.Modified] + counts[BudgetLineStatus.Unchanged] &&
                                               counts[BudgetLineStatus.NotComparable] == 0 &&
                                               counts[BudgetLineStatus.Added] == 0 && counts[BudgetLineStatus.Removed] == 0 &&
                                               unclassifiedTotal == 0,
                    ["amounts_note"] = "the amount totals cover ONLY the lines that were compared at a baseline price. Added, removed, not_comparable and unpriced lines are outside them, and amounts_are_complete says whether anything was."
                },
                ["lines"] = lines,
                ["unclassified"] = unclassifiedJson,
                ["means"] = Means
            };
        }

        private static JObject CompareOne(string code, CodeAggregate model, List<BaselineLine> baseLines, BudgetComparisonMapping mapping)
        {
            var line = new JObject { ["code"] = code };

            // ---- the baseline side, folded over its rows for this code. ----
            JObject baseJson = null;
            string baseUnit = null, baseState = null, baseProblem = null;
            double baseQty = 0;
            string priceState = "not_available", priceReason = null, currency = null;
            double? unitPrice = null;
            if (baseLines != null)
            {
                var rowIdx = new JArray();
                var units = new HashSet<string>(StringComparer.Ordinal);
                var prices = new HashSet<double>();
                var descriptions = new List<string>();
                bool anyInvalid = false, anyAbsent = false, anyPriceInvalid = false, anyPriceAbsent = false;
                foreach (BaselineLine b in baseLines)
                {
                    rowIdx.Add(b.RowIndex);
                    units.Add(BudgetComparisonMapping.Normalise(b.Unit));
                    if (!string.IsNullOrWhiteSpace(b.Description) && !descriptions.Contains(b.Description)) descriptions.Add(b.Description);
                    if (b.QuantityState == "measured") baseQty += b.Quantity.Value;
                    else if (b.QuantityState == "invalid") anyInvalid = true;
                    else anyAbsent = true;
                    if (b.UnitPriceState == "measured") prices.Add(b.UnitPrice.Value);
                    else if (b.UnitPriceState == "invalid") anyPriceInvalid = true;
                    else anyPriceAbsent = true;
                    if (currency == null && !string.IsNullOrWhiteSpace(b.Currency)) currency = b.Currency;
                }
                baseUnit = baseLines[0].Unit;
                if (units.Count > 1) { baseState = "ambiguous_unit"; baseProblem = "the baseline lists this code in " + units.Count + " different units (" + string.Join(", ", units.OrderBy(u => u, StringComparer.Ordinal)) + "); split the codes or fix the sheet."; }
                else if (anyInvalid) { baseState = "invalid"; baseProblem = "a baseline quantity for this code is not a number."; }
                else if (anyAbsent) { baseState = "absent"; baseProblem = "a baseline quantity for this code is blank."; }
                else baseState = "measured";

                if (anyPriceInvalid) { priceState = "invalid"; priceReason = "a baseline unit_price for this code is not a number."; }
                else if (prices.Count > 1) { priceState = "not_available"; priceReason = "the baseline rows for this code disagree on unit_price (" + string.Join(", ", prices.OrderBy(p => p).Select(p => p.ToString("0.####", CultureInfo.InvariantCulture))) + "); no single rate exists to price the model against."; }
                else if (prices.Count == 1 && !anyPriceAbsent) { priceState = "measured"; unitPrice = prices.First(); }
                else if (prices.Count == 1) { priceState = "not_available"; priceReason = "some baseline rows for this code carry a unit_price and some do not."; }
                else priceReason = "the baseline carries no unit_price for this code, so no amount is computed - a price is never invented.";

                baseJson = new JObject
                {
                    ["rows"] = rowIdx,
                    ["unit"] = baseUnit,
                    ["quantity"] = baseState == "measured" ? (JToken)Round(baseQty) : JValue.CreateNull(),
                    ["state"] = baseState,
                    ["problem"] = baseProblem,
                    ["description"] = descriptions.Count == 0 ? null : string.Join(" | ", descriptions),
                    ["unit_price"] = unitPrice.HasValue ? (JToken)unitPrice.Value : JValue.CreateNull(),
                    ["currency"] = currency
                };
                line["description"] = descriptions.Count == 0 ? null : string.Join(" | ", descriptions);
                line["unit"] = baseUnit;
            }

            // ---- the model side. ----
            JObject modelJson = null;
            Dictionary<string, QuantityAggregate> aggregates = null;
            JObject trace = null;
            if (model != null)
            {
                aggregates = Aggregate(model.Rows);
                trace = Trace(model.Rows);
                var quantities = new JObject();
                foreach (var kv in aggregates.OrderBy(k => k.Key, StringComparer.Ordinal))
                    quantities[kv.Key] = AggregateJson(kv.Value);
                modelJson = new JObject
                {
                    ["elements"] = model.Rows.Count,
                    ["quantities"] = quantities
                };
            }

            line["classification"] = ClassificationJson(code, model != null, baseLines != null, mapping.Catalogue);
            line["trace"] = new JObject
            {
                ["element_ids"] = trace == null ? new JArray() : trace["element_ids"],
                ["documents"] = trace == null ? new JArray() : trace["documents"],
                ["link_instance_ids"] = trace == null ? new JArray() : trace["link_instance_ids"],
                ["baseline_rows"] = baseJson == null ? new JArray() : baseJson["rows"]
            };
            line["baseline"] = baseJson;
            line["model"] = modelJson;

            if (model == null)
            {
                line["status"] = BudgetLineStatus.Removed;
                line["reason"] = "the baseline carries this code and no model element does.";
                line["price"] = PriceJson(priceState, priceReason, unitPrice, currency, baseState == "measured" ? (double?)baseQty : null, null);
                return line;
            }
            if (baseLines == null)
            {
                line["status"] = BudgetLineStatus.Added;
                line["reason"] = "model elements carry this code and the baseline has no line for it.";
                line["price"] = PriceJson("not_available", "no baseline line, so no agreed unit price exists for this code.", null, null, null, null);
                return line;
            }

            // ---- both present: choose the model quantity by unit, or refuse. ----
            if (baseState != "measured")
            {
                string reason = baseState == "ambiguous_unit" ? BudgetNotComparable.BaselineAmbiguousUnit
                              : baseState == "invalid" ? BudgetNotComparable.BaselineInvalid
                              : BudgetNotComparable.BaselineAbsent;
                return NotComparable(line, reason, baseProblem, priceState, priceReason, unitPrice, currency, null);
            }

            QuantityAggregate chosen;
            double factor;
            string selectionProblem;
            if (!Select(aggregates, baseUnit, mapping, out chosen, out factor, out selectionProblem))
            {
                string reason = selectionProblem.StartsWith("ambiguous", StringComparison.Ordinal)
                    ? BudgetNotComparable.AmbiguousQuantity : BudgetNotComparable.UnitIncompatible;
                return NotComparable(line, reason, selectionProblem, priceState, priceReason, unitPrice, currency, baseQty);
            }

            var selected = new JObject
            {
                ["quantity_name"] = chosen.Name,
                ["unit"] = chosen.Unit,
                ["conversion_factor"] = factor,
                ["quantity_in_model_unit"] = Round(chosen.Total),
                ["quantity_in_baseline_unit"] = Round(chosen.Total * factor),
                ["coverage"] = AggregateJson(chosen)
            };
            modelJson["selected"] = selected;

            // Completeness before arithmetic. An unreadable element makes the sum a lower
            // bound; an element the quantity does not apply to makes it a fragment.
            if (chosen.Unreadable > 0 || chosen.Invalid > 0)
                return NotComparable(line,
                    chosen.Invalid > 0 && chosen.Unreadable == 0 ? BudgetNotComparable.ModelInvalid : BudgetNotComparable.IncompleteRead,
                    chosen.Unreadable + " element(s) could not be read and " + chosen.Invalid + " carried a non-numeric value, so " +
                    Round(chosen.Total * factor).ToString("0.####", CultureInfo.InvariantCulture) + " " + baseUnit +
                    " over " + chosen.Measured + " of " + chosen.Elements + " element(s) is a LOWER BOUND, not the quantity. It was not compared.",
                    priceState, priceReason, unitPrice, currency, baseQty);
            if (chosen.Measured == 0)
                return NotComparable(line, BudgetNotComparable.ModelAbsent,
                    "none of the " + chosen.Elements + " element(s) under this code carries the quantity '" + chosen.Name + "' (" +
                    chosen.Absent + " absent, " + chosen.Empty + " empty). No model quantity exists; a zero here would be a fabrication.",
                    priceState, priceReason, unitPrice, currency, baseQty);
            if (chosen.Measured < chosen.Elements && !mapping.ComparePartialCoverage)
                return NotComparable(line, BudgetNotComparable.PartialCoverage,
                    "only " + chosen.Measured + " of " + chosen.Elements + " element(s) under this code carry the quantity '" + chosen.Name +
                    "' (" + chosen.Absent + " absent, " + chosen.Empty + " empty); the sum is a fragment wearing the code's name. " +
                    "Pass mapping.rules.compare_partial_coverage=true to compare it anyway, knowingly.",
                    priceState, priceReason, unitPrice, currency, baseQty);

            double modelQty = chosen.Total * factor;
            double delta = modelQty - baseQty;
            double? pct = Math.Abs(baseQty) > 1e-12 ? (double?)(delta / baseQty * 100.0) : null;
            bool within = WithinTolerance(delta, pct, mapping);
            line["quantity_delta"] = new JObject
            {
                ["baseline"] = Round(baseQty),
                ["model"] = Round(modelQty),
                ["unit"] = baseUnit,
                ["abs"] = Round(delta),
                ["pct"] = pct.HasValue ? (JToken)Round(pct.Value) : JValue.CreateNull(),
                ["pct_note"] = pct.HasValue ? null : "the baseline quantity is zero, so no percentage exists.",
                ["within_tolerance"] = within,
                ["coverage_complete"] = chosen.Measured == chosen.Elements
            };
            line["status"] = within ? BudgetLineStatus.Unchanged : BudgetLineStatus.Modified;
            line["reason"] = within ? "the model quantity is inside the declared tolerance of the baseline." : "the model quantity is outside the declared tolerance of the baseline.";
            line["price"] = PriceJson(priceState, priceReason, unitPrice, currency, baseQty, modelQty);
            return line;
        }

        private static JObject NotComparable(JObject line, string reason, string detail, string priceState, string priceReason,
                                             double? unitPrice, string currency, double? baseQty)
        {
            line["status"] = BudgetLineStatus.NotComparable;
            line["reason"] = reason;
            line["detail"] = detail;
            line["quantity_delta"] = null;
            line["price"] = PriceJson(priceState, priceReason, unitPrice, currency, baseQty, null);
            return line;
        }

        /// <summary>
        /// Which model quantity feeds a baseline line, decided by UNIT: the one whose
        /// declared unit equals the baseline's, or has a declared conversion to it. Two
        /// candidates are a tie, and a tie is refused rather than broken by list order.
        /// </summary>
        private static bool Select(Dictionary<string, QuantityAggregate> aggregates, string baseUnit, BudgetComparisonMapping mapping,
                                   out QuantityAggregate chosen, out double factor, out string problem)
        {
            chosen = null; factor = 1; problem = null;
            string target = BudgetComparisonMapping.Normalise(baseUnit);
            if (target.Length == 0) { problem = "the baseline line declares no unit, so no model quantity can be matched to it."; return false; }

            IEnumerable<QuantityAggregate> pool = aggregates.Values;
            if (mapping.QuantityField != null)
            {
                QuantityAggregate pinned;
                if (!aggregates.TryGetValue(mapping.QuantityField, out pinned))
                { problem = "mapping.quantity_field '" + mapping.QuantityField + "' is not a quantity these rows carry (" + string.Join(", ", aggregates.Keys.OrderBy(k => k, StringComparer.Ordinal)) + ")."; return false; }
                pool = new[] { pinned };
            }

            var candidates = new List<KeyValuePair<QuantityAggregate, double>>();
            foreach (QuantityAggregate q in pool)
            {
                if (q.Units.Count > 1)
                { problem = "the model quantity '" + q.Name + "' carries more than one unit across its rows (" + string.Join(", ", q.Units.OrderBy(u => u, StringComparer.Ordinal)) + "), which no single factor can convert."; return false; }
                string unit = BudgetComparisonMapping.Normalise(q.Unit);
                if (unit == target) { candidates.Add(new KeyValuePair<QuantityAggregate, double>(q, 1.0)); continue; }
                double f;
                if (mapping.UnitConversions.TryGetValue(BudgetComparisonMapping.ConversionKey(unit, target), out f))
                    candidates.Add(new KeyValuePair<QuantityAggregate, double>(q, f));
            }
            if (candidates.Count == 0)
            {
                problem = "the baseline unit '" + baseUnit + "' matches no model quantity: the rows carry " +
                          string.Join(", ", pool.OrderBy(q => q.Name, StringComparer.Ordinal).Select(q => q.Name + " [" + q.Unit + "]")) +
                          " and no mapping.unit_conversions entry declares a factor INTO '" + baseUnit + "'. Nothing is converted silently; declare {from, to, factor} if the units are convertible.";
                return false;
            }
            if (candidates.Count > 1)
            {
                problem = "ambiguous: " + candidates.Count + " model quantities can feed the baseline unit '" + baseUnit + "' (" +
                          string.Join(", ", candidates.Select(c => c.Key.Name + " [" + c.Key.Unit + "]")) +
                          "). Pin one with mapping.quantity_field.";
                return false;
            }
            chosen = candidates[0].Key;
            factor = candidates[0].Value;
            return true;
        }

        private static bool WithinTolerance(double delta, double? pct, BudgetComparisonMapping mapping)
        {
            double abs = Math.Abs(delta);
            if (!mapping.QuantityAbs.HasValue && !mapping.QuantityPct.HasValue) return abs <= 1e-9;
            if (mapping.QuantityAbs.HasValue && abs <= mapping.QuantityAbs.Value + 1e-12) return true;
            if (mapping.QuantityPct.HasValue && pct.HasValue && Math.Abs(pct.Value) <= mapping.QuantityPct.Value + 1e-12) return true;
            return false;
        }

        private static Dictionary<string, QuantityAggregate> Aggregate(List<ModelRow> rows)
        {
            var result = new Dictionary<string, QuantityAggregate>(StringComparer.Ordinal);
            foreach (ModelRow r in rows)
                foreach (var kv in r.Quantities)
                {
                    QuantityAggregate a;
                    if (!result.TryGetValue(kv.Key, out a))
                        result[kv.Key] = a = new QuantityAggregate { Name = kv.Key, Unit = kv.Value.Unit };
                    a.Elements++;
                    a.Units.Add(BudgetComparisonMapping.Normalise(kv.Value.Unit));
                    switch (kv.Value.State)
                    {
                        case QuantityState.Measured: a.Measured++; a.Total += kv.Value.Value.Value; break;
                        case QuantityState.Absent: a.Absent++; break;
                        case QuantityState.Empty: a.Empty++; break;
                        case QuantityState.Invalid: a.Invalid++; break;
                        default: a.Unreadable++; break;
                    }
                }
            // A quantity some rows carry and others do not: the rows that do not are
            // absent for it, so every aggregate counts every element.
            foreach (QuantityAggregate a in result.Values)
                if (a.Elements < rows.Count) { a.Absent += rows.Count - a.Elements; a.Elements = rows.Count; }
            return result;
        }

        private static JObject AggregateJson(QuantityAggregate a) => new JObject
        {
            ["unit"] = a.Unit,
            ["known_total"] = Round(a.Total),
            ["elements"] = a.Elements,
            ["measured"] = a.Measured,
            ["absent"] = a.Absent,
            ["empty"] = a.Empty,
            ["unreadable"] = a.Unreadable,
            ["invalid"] = a.Invalid,
            ["complete"] = a.Measured == a.Elements,
            ["state"] = a.Unreadable > 0 ? QuantityState.Unreadable
                      : a.Invalid > 0 ? QuantityState.Invalid
                      : a.Measured == 0 ? QuantityState.Absent
                      : a.Measured == a.Elements ? QuantityState.Measured : "partial"
        };

        private static JObject ClassificationJson(string code, bool inModel, bool inBaseline, BudgetCatalogue catalogue)
        {
            var o = new JObject
            {
                ["in_model"] = inModel,
                ["in_baseline"] = inBaseline
            };
            if (catalogue != null)
            {
                string status = catalogue.Classify(code);
                o["catalogue_status"] = status;
                o["is_leaf"] = status == BudgetCodeStatus.Leaf ? (JToken)true
                             : status == BudgetCodeStatus.GroupNotTerminal ? (JToken)false
                             : JValue.CreateNull();
            }
            else
            {
                o["catalogue_status"] = BudgetCodeStatus.CatalogueNotSupplied;
                o["is_leaf"] = JValue.CreateNull();
            }
            o["delta"] = !inBaseline ? "not_in_baseline"
                       : !inModel ? "not_in_model"
                       : catalogue != null && (string)o["catalogue_status"] == BudgetCodeStatus.GroupNotTerminal ? "group_not_terminal"
                       : catalogue != null && (string)o["catalogue_status"] == BudgetCodeStatus.NotInCatalogue ? "not_in_catalogue"
                       : "none";
            return o;
        }

        private static JObject PriceJson(string state, string reason, double? unitPrice, string currency, double? baseQty, double? modelQty)
        {
            var o = new JObject
            {
                ["state"] = state,
                ["reason"] = reason,
                ["unit_price"] = unitPrice.HasValue ? (JToken)unitPrice.Value : JValue.CreateNull(),
                ["currency"] = currency
            };
            if (state == "measured" && baseQty.HasValue && modelQty.HasValue)
            {
                double baseAmount = baseQty.Value * unitPrice.Value;
                double modelAmount = modelQty.Value * unitPrice.Value;
                o["baseline_amount"] = Round(baseAmount);
                o["model_amount"] = Round(modelAmount);
                o["amount_delta"] = Round(modelAmount - baseAmount);
                o["basis"] = "model quantity at the BASELINE unit price; the delta isolates quantity drift at the agreed rate.";
            }
            else if (state == "measured" && baseQty.HasValue)
            {
                // A priced baseline line that found no comparable model quantity still
                // carries its budget. The model amount stays null: an absent quantity is
                // not a zero quantity, and pricing it at zero would book a saving nobody
                // measured.
                o["state"] = "baseline_only";
                o["baseline_amount"] = Round(baseQty.Value * unitPrice.Value);
                o["model_amount"] = JValue.CreateNull();
                o["amount_delta"] = JValue.CreateNull();
                o["reason"] = reason ?? "no model quantity was compared, so no amount delta exists; baseline_amount is the line's budget.";
            }
            else
            {
                if (state == "measured") o["state"] = "not_available";
                o["baseline_amount"] = JValue.CreateNull();
                o["model_amount"] = JValue.CreateNull();
                o["amount_delta"] = JValue.CreateNull();
                if (state == "measured" && reason == null)
                    o["reason"] = "the quantities were not compared, so no amount delta exists.";
            }
            return o;
        }

        private static JObject Trace(List<ModelRow> rows)
        {
            var ids = new JArray();
            var docs = new SortedSet<string>(StringComparer.Ordinal);
            var links = new SortedSet<string>(StringComparer.Ordinal);
            foreach (ModelRow r in rows)
            {
                ids.Add(r.ElementId);
                if (!string.IsNullOrEmpty(r.Document)) docs.Add(r.Document);
                if (!string.IsNullOrEmpty(r.LinkInstanceId)) links.Add(r.LinkInstanceId);
            }
            return new JObject
            {
                ["elements"] = rows.Count,
                ["element_ids"] = ids,
                ["documents"] = new JArray(docs.Cast<object>().ToArray()),
                ["link_instance_ids"] = new JArray(links.Cast<object>().ToArray())
            };
        }

        // ------------------------------------------------------------------
        // The sheet: one row per compared code, in a fixed column order.
        // ------------------------------------------------------------------

        public static readonly string[] SheetHeader =
        {
            "status", "code", "description", "unit", "baseline_quantity", "model_quantity", "quantity_delta",
            "quantity_delta_pct", "unit_price", "baseline_amount", "model_amount", "amount_delta", "elements",
            "reason", "trace"
        };

        /// <summary>
        /// The rows the Excel destination writes. A missing number is a blank cell, never
        /// a zero; the header is the first row so the sheet opens as a table.
        /// </summary>
        public static List<IList<object>> SheetRows(JObject comparison)
        {
            var rows = new List<IList<object>> { SheetHeader.Cast<object>().ToList() };
            foreach (JObject line in comparison["lines"].OfType<JObject>())
            {
                JObject delta = line["quantity_delta"] as JObject;
                JObject price = line["price"] as JObject;
                JObject baseJson = line["baseline"] as JObject;
                JObject modelJson = line["model"] as JObject;
                JObject selected = modelJson == null ? null : modelJson["selected"] as JObject;
                JObject trace = line["trace"] as JObject;
                rows.Add(new List<object>
                {
                    (string)line["status"],
                    (string)line["code"],
                    (string)line["description"],
                    (string)line["unit"] ?? (selected == null ? null : (string)selected["unit"]),
                    Num(baseJson == null ? null : baseJson["quantity"]),
                    Num(delta != null ? delta["model"] : selected == null ? null : selected["quantity_in_baseline_unit"]),
                    Num(delta == null ? null : delta["abs"]),
                    Num(delta == null ? null : delta["pct"]),
                    Num(price == null ? null : price["unit_price"]),
                    Num(price == null ? null : price["baseline_amount"]),
                    Num(price == null ? null : price["model_amount"]),
                    Num(price == null ? null : price["amount_delta"]),
                    modelJson == null ? null : (object)(long)modelJson["elements"],
                    (string)line["detail"] ?? (string)line["reason"],
                    TraceText(trace)
                });
            }
            return rows;
        }

        /// <summary>The same rows as flat objects, for a Power BI push table.</summary>
        public static JArray PowerBiRows(JObject comparison, string runId)
        {
            var rows = new JArray();
            List<IList<object>> sheet = SheetRows(comparison);
            for (int i = 1; i < sheet.Count; i++)
            {
                var o = new JObject { ["run_id"] = runId };
                for (int c = 0; c < SheetHeader.Length; c++)
                {
                    object v = sheet[i][c];
                    o[SheetHeader[c]] = v == null ? JValue.CreateNull() : JToken.FromObject(v);
                }
                rows.Add(o);
            }
            return rows;
        }

        private static string TraceText(JObject trace)
        {
            if (trace == null) return null;
            var parts = new List<string>();
            var ids = trace["element_ids"] as JArray;
            if (ids != null && ids.Count > 0) parts.Add("elements: " + string.Join(",", ids.Select(t => (string)t)));
            var docs = trace["documents"] as JArray;
            if (docs != null && docs.Count > 0) parts.Add("documents: " + string.Join(" | ", docs.Select(t => (string)t)));
            var links = trace["link_instance_ids"] as JArray;
            if (links != null && links.Count > 0) parts.Add("link instances: " + string.Join(",", links.Select(t => (string)t)));
            var rows = trace["baseline_rows"] as JArray;
            if (rows != null && rows.Count > 0) parts.Add("baseline rows: " + string.Join(",", rows.Select(t => (string)t)));
            return parts.Count == 0 ? null : string.Join(" ; ", parts);
        }

        private static object Num(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer) return (long)t;
            if (t.Type == JTokenType.Float) return (double)t;
            return null;
        }

        // ------------------------------------------------------------------
        // Small honest readers.
        // ------------------------------------------------------------------

        /// <summary>A number, a blank, or something else - and which one it was.</summary>
        internal static void ReadNumberState(JToken t, out string state, out double? value, out string raw)
        {
            value = null; raw = null;
            if (t == null || t.Type == JTokenType.Null) { state = "absent"; return; }
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            {
                double d = (double)t;
                if (double.IsNaN(d) || double.IsInfinity(d)) { state = "invalid"; raw = t.ToString(); return; }
                state = "measured"; value = d; return;
            }
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim();
                raw = s;
                if (s.Length == 0) { state = "absent"; return; }
                double d;
                // Invariant only. A decimal comma is a locale, not a number, and guessing
                // it would read "1,250" as either 1.25 or 1250 depending on who typed it.
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsInfinity(d))
                { state = "measured"; value = d; return; }
                state = "invalid"; return;
            }
            if (t.Type == JTokenType.Object && t["formula"] != null)
            {
                // ExcelReadRows shape: a formula cell {value, formula:true}.
                ReadNumberState(t["value"], out state, out value, out raw);
                return;
            }
            state = "invalid"; raw = t.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static string TextOf(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Object && t["formula"] != null) return TextOf(t["value"]);
            if (t.Type == JTokenType.Float)
                return ((double)t).ToString("0.############", CultureInfo.InvariantCulture);
            if (t.Type == JTokenType.Integer) return ((long)t).ToString(CultureInfo.InvariantCulture);
            if (t.Type == JTokenType.Boolean) return (bool)t ? "true" : "false";
            if (t.Type == JTokenType.String) return (string)t;
            return t.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static bool TryNumber(JToken t, out double value)
        {
            value = 0;
            if (t == null) return false;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) return false;
            value = (double)t;
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Round(double v) => Math.Round(v, 6);
    }
}
