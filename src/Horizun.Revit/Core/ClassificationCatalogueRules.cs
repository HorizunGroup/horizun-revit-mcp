// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// CLASSIFICATION CODES, judged against a catalogue THE CALLER SUPPLIES.
//
// Nothing is compiled in. OmniClass, UniFormat, MasterFormat and every house
// standard belong to somebody and not to everybody, and a bridge that shipped
// one would be quietly enforcing that organisation's taxonomy on everybody
// else's model. The catalogue arrives as an argument or not at all.
//
// SEVEN ANSWERS, because "this code is no good" hides five different problems:
//
//   leaf                    a real code, terminal, and priceable.
//   group_not_terminal      a REAL code that names a group. Nobody prices a
//                           group, and this is the failure that looks most
//                           like success: the code exists, it validates
//                           against a regex, and it cannot be costed.
//   not_in_catalogue        we had a catalogue and this code is not in it.
//   invalid                 it does not even have the shape of a code.
//   catalogue_not_supplied  nobody gave us one. NOT the same as the code
//                           being absent from a catalogue we did have.
//   catalogue_unreadable    one was supplied and could not be parsed.
//   not_required            no rule asked about this code.
//
// The two catalogue states are separated because they lead somewhere different:
// one is a missing argument, the other is a broken one.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CatalogueCodes
    {
        public const string NoVersion = "catalogue_no_version";
        public const string BadShape = "catalogue_bad_shape";
        public const string EmptyCodes = "catalogue_has_no_codes";
        public const string DuplicateCode = "catalogue_duplicate_code";
    }

    public sealed class ClassificationCatalogue
    {
        public bool Ok;
        /// <summary>True when the caller supplied nothing at all.</summary>
        public bool Absent;
        public string Code;
        public string Message;
        public string Version;
        public string Name;

        /// <summary>Code to whether it is terminal. A group is present and not a leaf.</summary>
        public Dictionary<string, bool> Codes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }

    public static class ClassificationCatalogueRules
    {
        public const string Means =
            "codes are judged against a catalogue the CALLER supplies. Nothing is compiled in: OmniClass, " +
            "UniFormat, MasterFormat and every house standard belong to somebody and not to everybody. " +
            "catalogue_not_supplied is therefore a different answer from not_in_catalogue - the first is a " +
            "missing argument, the second is a code we looked for and did not find.";

        public const string GroupMeans =
            "group_not_terminal is the failure that looks most like success: the code is REAL, it passes any " +
            "regex you wrote, and nobody can price it, because it names a group rather than an item. A check " +
            "that only asks 'does this code exist' reports it as fine.";

        /// <summary>
        /// Reads a catalogue. Shape: { version, name, codes: { "A-1": false, "A-1-1": true } }
        /// where the boolean is IS-LEAF. An explicit boolean is required rather than
        /// inferred from prefixes: prefix inference guesses a taxonomy's shape, and
        /// it guesses wrong on every standard that reuses separators.
        /// </summary>
        public static ClassificationCatalogue Read(JToken token)
        {
            var c = new ClassificationCatalogue();
            if (token == null || token.Type == JTokenType.Null)
            {
                c.Absent = true;
                c.Message = "no classification catalogue was supplied, so no code was checked against one. " +
                            "This is NOT the same as a code being absent from a catalogue: nobody gave us one.";
                return c;
            }

            var o = token as JObject;
            if (o == null)
            {
                c.Code = CatalogueCodes.BadShape;
                c.Message = "the catalogue must be an object with 'version' and 'codes'.";
                return c;
            }

            JToken v = o["version"];
            if (v == null || string.IsNullOrWhiteSpace(v.Value<string>()))
            {
                c.Code = CatalogueCodes.NoVersion;
                c.Message = "the catalogue needs a 'version', so a report can say which taxonomy produced it.";
                return c;
            }
            c.Version = v.Value<string>();
            c.Name = o.Value<string>("name");

            var codes = o["codes"] as JObject;
            if (codes == null)
            {
                c.Code = CatalogueCodes.BadShape;
                c.Message = "the catalogue needs a 'codes' object mapping each code to whether it is a LEAF.";
                return c;
            }

            foreach (JProperty p in codes.Properties())
            {
                if (p.Value.Type != JTokenType.Boolean)
                {
                    c.Code = CatalogueCodes.BadShape;
                    c.Message = "the entry for '" + p.Name + "' must be true or false - whether the code is a " +
                                "LEAF. It is declared rather than inferred from the code's shape, because " +
                                "prefix inference guesses a taxonomy's structure and guesses wrong on every " +
                                "standard that reuses its separators.";
                    return c;
                }
                if (c.Codes.ContainsKey(p.Name))
                {
                    c.Code = CatalogueCodes.DuplicateCode;
                    c.Message = "'" + p.Name + "' appears twice in the catalogue.";
                    return c;
                }
                c.Codes[p.Name] = p.Value.Value<bool>();
            }

            if (c.Codes.Count == 0)
            {
                c.Code = CatalogueCodes.EmptyCodes;
                c.Message = "the catalogue lists no codes, so every code in the model would be reported absent " +
                            "from it. Omit the catalogue instead of supplying an empty one.";
                return c;
            }

            c.Ok = true;
            return c;
        }

        /// <summary>
        /// Where one code sits. `required` false short-circuits: a code nobody asked
        /// about is not_required, not invalid.
        /// </summary>
        public static string Classify(string code, ClassificationCatalogue catalogue, bool required = true)
        {
            if (!required) return CodeStatus.NotRequired;
            if (catalogue == null || catalogue.Absent) return CodeStatus.CatalogueNotSupplied;
            if (!catalogue.Ok) return CodeStatus.CatalogueUnreadable;

            // An empty or whitespace code has no shape at all. Distinct from a code
            // that is well formed and simply unknown.
            if (string.IsNullOrWhiteSpace(code)) return CodeStatus.Invalid;

            bool isLeaf;
            if (!catalogue.Codes.TryGetValue(code.Trim(), out isLeaf)) return CodeStatus.NotInCatalogue;
            return isLeaf ? CodeStatus.Leaf : CodeStatus.GroupNotTerminal;
        }

        public static JObject Tally(IEnumerable<string> statuses, ClassificationCatalogue catalogue)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string s in CodeStatus.All) counts[s] = 0;
            foreach (string s in statuses ?? Enumerable.Empty<string>())
                if (s != null && counts.ContainsKey(s)) counts[s]++;

            var o = new JObject();
            foreach (string s in CodeStatus.All) o[s] = counts[s];
            o["catalogue"] = catalogue == null ? "not_supplied"
                           : catalogue.Absent ? "not_supplied"
                           : catalogue.Ok ? "ok" : "refused";
            o["catalogue_version"] = catalogue == null ? null : catalogue.Version;
            o["catalogue_name"] = catalogue == null ? null : catalogue.Name;
            o["catalogue_codes"] = catalogue == null || !catalogue.Ok ? null : (JToken)catalogue.Codes.Count;
            o["means"] = Means;
            o["group_means"] = GroupMeans;
            return o;
        }
    }
}
