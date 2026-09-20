// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// IDS VALUE CONSTRAINTS, from the published schema rather than from a guess.
//
// Read 2026-09-15 from buildingSMART/IDS: Schema/ids.xsd (version 1.0.0) and
// Documentation/UserManual/restrictions.md. Every facet parameter in IDS is an
// `idsValue`, and an idsValue is a CHOICE of exactly two things:
//
//     <simpleValue>text</simpleValue>
//     <xs:restriction base="xs:string"> …facets… </xs:restriction>
//
// and the restriction may be one of four kinds, which the manual names:
// enumeration, pattern, bounds, length. Nothing else is a constraint in IDS, and
// a construction that is not one of these is REPORTED as unsupported rather than
// quietly treated as "no constraint" — which is the failure that matters here,
// because "no constraint" passes everything.
//
// THREE DETAILS THAT DECIDE WHETHER THIS IS CORRECT OR MERELY PLAUSIBLE:
//
//   XML SCHEMA PATTERNS ARE ANCHORED. `xs:pattern` matches the WHOLE value; .NET's
//   Regex.IsMatch matches a substring. Ported without anchoring, the pattern
//   "DT[0-9]{2}" accepts "XXDT01YY", and every naming-convention check in every
//   IDS ever written becomes a check that passes.
//
//   BOUNDS ARE NUMERIC, and a value that is not a number does not "fail the
//   bound" — it fails to be comparable. Those are different findings: the first
//   says the number is wrong, the second says the field is not a number.
//
//   A RESTRICTION WITH NO FACETS CONSTRAINS NOTHING. It is well-formed XML and it
//   is almost certainly an authoring mistake, so it is reported as one rather
//   than silently satisfied by every value in the model.
//
// Revit-free on purpose: this is string and number arithmetic over a published
// grammar, and it has to be provable without a building open.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What a value is compared against: one simple value, or one restriction.</summary>
    public sealed class IdsValue
    {
        /// <summary>Set when the parameter was a &lt;simpleValue&gt;.</summary>
        public string Simple;

        /// <summary>The xs:restriction base, e.g. xs:string, xs:double, xs:boolean.</summary>
        public string BaseType;

        public readonly List<string> Enumeration = new List<string>();
        public readonly List<string> Patterns = new List<string>();

        public double? MinInclusive, MaxInclusive, MinExclusive, MaxExclusive;
        public int? Length, MinLength, MaxLength;

        /// <summary>Constructions inside this value that this build does not evaluate, by name.</summary>
        public readonly List<string> Unsupported = new List<string>();

        /// <summary>Why this value cannot be used at all, or null. An authoring defect, not a model finding.</summary>
        public string Defect;

        public bool IsSimple => Simple != null;

        public bool HasAnyFacet =>
            Enumeration.Count > 0 || Patterns.Count > 0 ||
            MinInclusive.HasValue || MaxInclusive.HasValue ||
            MinExclusive.HasValue || MaxExclusive.HasValue ||
            Length.HasValue || MinLength.HasValue || MaxLength.HasValue;

        /// <summary>A human sentence for the reply. The reader must be able to see what was asked.</summary>
        public string Describe()
        {
            if (Defect != null) return "(unusable: " + Defect + ")";
            if (IsSimple) return "= '" + Simple + "'";

            var parts = new List<string>();
            if (Enumeration.Count > 0)
                parts.Add("one of [" + string.Join(", ", Enumeration.Take(12)) +
                          (Enumeration.Count > 12 ? ", …" : "") + "]");
            foreach (string pattern in Patterns) parts.Add("matches /" + pattern + "/ (whole value)");
            if (MinInclusive.HasValue) parts.Add(">= " + Number(MinInclusive.Value));
            if (MinExclusive.HasValue) parts.Add("> " + Number(MinExclusive.Value));
            if (MaxInclusive.HasValue) parts.Add("<= " + Number(MaxInclusive.Value));
            if (MaxExclusive.HasValue) parts.Add("< " + Number(MaxExclusive.Value));
            if (Length.HasValue) parts.Add("exactly " + Length.Value + " character(s)");
            if (MinLength.HasValue) parts.Add("at least " + MinLength.Value + " character(s)");
            if (MaxLength.HasValue) parts.Add("at most " + MaxLength.Value + " character(s)");
            if (parts.Count == 0) parts.Add("(a restriction with no facets: it constrains nothing)");
            return string.Join(" and ", parts);
        }

        public JObject ToJson() => new JObject
        {
            ["kind"] = IsSimple ? "simple_value" : "restriction",
            ["base_type"] = BaseType,
            ["describes"] = Describe(),
            ["unsupported"] = new JArray(Unsupported),
            ["defect"] = Defect
        };

        private static string Number(double value) =>
            value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>What comparing a value against a constraint produced.</summary>
    public sealed class IdsMatch
    {
        public bool Satisfied;

        /// <summary>True when no claim could be made: the value was absent, or not comparable.</summary>
        public bool Undecidable;

        /// <summary>
        /// True when the thing the facet is about IS NOT THERE AT ALL - no property set, no
        /// classification, no material, no containment, an unset attribute.
        ///
        /// SEPARATE FROM "does not satisfy", because cardinality turns on exactly this
        /// difference. `optional` means "absent is fine, present must comply"; `prohibited`
        /// means "absent is the only acceptable state". Both are unanswerable from a boolean,
        /// and inferring absence from the wording of a reason string is how a cardinality
        /// implementation quietly stops working when somebody rewords an error.
        /// </summary>
        public bool Absent;

        public string Reason;

        public static IdsMatch Yes() => new IdsMatch { Satisfied = true };
        public static IdsMatch No(string why) => new IdsMatch { Satisfied = false, Reason = why };
        public static IdsMatch Missing(string why) =>
            new IdsMatch { Satisfied = false, Absent = true, Reason = why };
        public static IdsMatch Unknown(string why) =>
            new IdsMatch { Satisfied = false, Undecidable = true, Reason = why };
    }

    public static class IdsRestriction
    {
        public const string Namespace = "http://standards.buildingsmart.org/IDS";
        public const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";

        /// <summary>The four restriction kinds the manual names. Anything else is reported.</summary>
        public static readonly string[] SupportedFacets =
        {
            "enumeration", "pattern",
            "minInclusive", "maxInclusive", "minExclusive", "maxExclusive",
            "length", "minLength", "maxLength"
        };

        // =====================================================================
        // Parsing
        // =====================================================================

        /// <summary>
        /// Read an idsValue container: the element that holds either a simpleValue or an
        /// xs:restriction. Returns null when the container itself is absent, which is a
        /// DIFFERENT thing from an empty constraint and is treated as such by every caller.
        /// </summary>
        public static IdsValue Read(XmlNode container)
        {
            if (container == null) return null;

            var value = new IdsValue();
            XmlNode simple = FirstChild(container, "simpleValue", Namespace);
            if (simple != null)
            {
                value.Simple = simple.InnerText;
                return value;
            }

            XmlNode restriction = FirstChild(container, "restriction", XsdNamespace);
            if (restriction == null)
            {
                value.Defect =
                    "this parameter holds neither <simpleValue> nor <xs:restriction>. The IDS schema " +
                    "allows exactly those two, so nothing can be compared against it.";
                return value;
            }

            XmlAttribute baseType = restriction.Attributes == null ? null : restriction.Attributes["base"];
            value.BaseType = baseType == null ? null : baseType.Value;

            foreach (XmlNode facet in restriction.ChildNodes)
            {
                if (facet.NodeType != XmlNodeType.Element) continue;
                string name = facet.LocalName;
                string raw = facet.Attributes == null || facet.Attributes["value"] == null
                    ? null : facet.Attributes["value"].Value;

                switch (name)
                {
                    case "enumeration": if (raw != null) value.Enumeration.Add(raw); break;
                    case "pattern": if (raw != null) value.Patterns.Add(raw); break;
                    case "minInclusive": value.MinInclusive = AsDouble(raw, value, name); break;
                    case "maxInclusive": value.MaxInclusive = AsDouble(raw, value, name); break;
                    case "minExclusive": value.MinExclusive = AsDouble(raw, value, name); break;
                    case "maxExclusive": value.MaxExclusive = AsDouble(raw, value, name); break;
                    case "length": value.Length = AsInt(raw, value, name); break;
                    case "minLength": value.MinLength = AsInt(raw, value, name); break;
                    case "maxLength": value.MaxLength = AsInt(raw, value, name); break;
                    case "annotation": break;      // documentation, never a constraint
                    default:
                        // NAMED, NOT IGNORED. An unevaluated facet treated as absent is a
                        // constraint that silently passes, which is the one direction this
                        // must never be wrong in.
                        value.Unsupported.Add(name);
                        break;
                }
            }

            if (!value.HasAnyFacet && value.Unsupported.Count == 0)
                value.Defect =
                    "this xs:restriction declares no facets at all, so it constrains nothing and every " +
                    "value would satisfy it. That is almost certainly an authoring mistake, and it is " +
                    "reported as one rather than passed.";
            return value;
        }

        // =====================================================================
        // Evaluation
        // =====================================================================

        /// <summary>
        /// Does `actual` satisfy `constraint`?
        ///
        /// `actual` null means the value was ABSENT. That is undecidable here, not a failure:
        /// whether an absent value is a failure depends on the facet's cardinality, which is
        /// the caller's decision and not this function's.
        /// </summary>
        public static IdsMatch Satisfies(string actual, IdsValue constraint)
        {
            if (constraint == null) return IdsMatch.Yes();      // no constraint declared: anything goes
            if (constraint.Defect != null) return IdsMatch.Unknown(constraint.Defect);

            if (actual == null)
                return IdsMatch.Unknown("no value was found to compare. Whether that is a failure is " +
                                        "decided by the facet's cardinality, not here.");

            if (constraint.IsSimple)
                return string.Equals(actual, constraint.Simple, StringComparison.Ordinal)
                    ? IdsMatch.Yes()
                    : IdsMatch.No("is '" + actual + "' and must be '" + constraint.Simple + "'");

            if (constraint.Unsupported.Count > 0 && !constraint.HasAnyFacet)
                return IdsMatch.Unknown(
                    "this restriction uses only constructions this build does not evaluate (" +
                    string.Join(", ", constraint.Unsupported) + "), so no claim is made. It is NOT " +
                    "reported as satisfied: an unevaluated constraint that passes is worse than one " +
                    "that is refused.");

            var failures = new List<string>();

            if (constraint.Enumeration.Count > 0 &&
                !constraint.Enumeration.Contains(actual, StringComparer.Ordinal))
                failures.Add("is '" + actual + "', which is not one of [" +
                             string.Join(", ", constraint.Enumeration.Take(12)) +
                             (constraint.Enumeration.Count > 12 ? ", …" : "") + "]");

            foreach (string pattern in constraint.Patterns)
            {
                bool matched;
                string problem;
                if (!TryMatchWholeValue(actual, pattern, out matched, out problem))
                    return IdsMatch.Unknown("the pattern /" + pattern + "/ could not be evaluated: " +
                                            problem + ". No claim is made about this element.");
                if (!matched)
                    failures.Add("is '" + actual + "', which does not match /" + pattern +
                                 "/ over the WHOLE value");
            }

            bool wantsNumber = constraint.MinInclusive.HasValue || constraint.MaxInclusive.HasValue ||
                               constraint.MinExclusive.HasValue || constraint.MaxExclusive.HasValue;
            if (wantsNumber)
            {
                double number;
                if (!double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    // NOT A FAILING NUMBER. A NON-NUMBER. Reporting "3200 is out of range" for the
                    // text "approx 3.2 m" would send somebody looking for the wrong defect.
                    return IdsMatch.Unknown(
                        "the value '" + actual + "' is not a number, and this constraint is a numeric " +
                        "bound. That is a different finding from a number out of range, and it is " +
                        "reported as its own.");

                if (constraint.MinInclusive.HasValue && number < constraint.MinInclusive.Value)
                    failures.Add("is " + actual + " and must be >= " + Show(constraint.MinInclusive.Value));
                if (constraint.MinExclusive.HasValue && number <= constraint.MinExclusive.Value)
                    failures.Add("is " + actual + " and must be > " + Show(constraint.MinExclusive.Value));
                if (constraint.MaxInclusive.HasValue && number > constraint.MaxInclusive.Value)
                    failures.Add("is " + actual + " and must be <= " + Show(constraint.MaxInclusive.Value));
                if (constraint.MaxExclusive.HasValue && number >= constraint.MaxExclusive.Value)
                    failures.Add("is " + actual + " and must be < " + Show(constraint.MaxExclusive.Value));
            }

            if (constraint.Length.HasValue && actual.Length != constraint.Length.Value)
                failures.Add("is " + actual.Length + " character(s) long and must be exactly " +
                             constraint.Length.Value);
            if (constraint.MinLength.HasValue && actual.Length < constraint.MinLength.Value)
                failures.Add("is " + actual.Length + " character(s) long and must be at least " +
                             constraint.MinLength.Value);
            if (constraint.MaxLength.HasValue && actual.Length > constraint.MaxLength.Value)
                failures.Add("is " + actual.Length + " character(s) long and must be at most " +
                             constraint.MaxLength.Value);

            if (failures.Count > 0) return IdsMatch.No(string.Join("; ", failures));

            if (constraint.Unsupported.Count > 0)
                // PART of the constraint was evaluated and part was not. Neither a pass nor a
                // fail: the element satisfies what could be checked, and the reply says which
                // part nobody checked.
                return IdsMatch.Unknown(
                    "everything this build can evaluate is satisfied, and the restriction also uses " +
                    string.Join(", ", constraint.Unsupported) + ", which it does not evaluate. The " +
                    "element is NOT reported as passing on the strength of a partial check.");

            return IdsMatch.Yes();
        }

        /// <summary>
        /// XML Schema regex semantics: the pattern must match the ENTIRE value.
        ///
        /// .NET's Regex.IsMatch is a substring search. Every naming-convention check in every
        /// IDS ever written would pass without this anchoring, and the failure is invisible:
        /// "DT[0-9]{2}" would accept "XXDT01YY".
        /// </summary>
        public static bool TryMatchWholeValue(string actual, string pattern,
                                              out bool matched, out string problem)
        {
            matched = false;
            problem = null;
            try
            {
                matched = Regex.IsMatch(actual, "^(?:" + pattern + ")$",
                                        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                return true;
            }
            catch (RegexMatchTimeoutException)
            {
                // A CATASTROPHIC PATTERN IS AN AUTHORING DEFECT, and it must not take the whole
                // validation with it. Two seconds against one value is already far past anything
                // a naming convention needs.
                problem = "it did not finish within two seconds against this value, which means the " +
                          "pattern backtracks catastrophically";
                return false;
            }
            catch (ArgumentException ex)
            {
                problem = "it is not a valid regular expression (" + ex.Message + ")";
                return false;
            }
        }

        // =====================================================================

        private static double? AsDouble(string raw, IdsValue owner, string facet)
        {
            double value;
            if (raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return value;
            owner.Defect = "the " + facet + " facet holds '" + raw + "', which is not a number.";
            return null;
        }

        private static int? AsInt(string raw, IdsValue owner, string facet)
        {
            int value;
            if (raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                && value >= 0)
                return value;
            owner.Defect = "the " + facet + " facet holds '" + raw + "', which is not a length.";
            return null;
        }

        private static string Show(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        internal static XmlNode FirstChild(XmlNode parent, string localName, string namespaceUri)
        {
            if (parent == null) return null;
            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                if (!string.Equals(child.LocalName, localName, StringComparison.Ordinal)) continue;
                if (namespaceUri != null && !string.Equals(child.NamespaceURI, namespaceUri, StringComparison.Ordinal))
                    continue;
                return child;
            }
            return null;
        }

        internal static IEnumerable<XmlNode> Children(XmlNode parent, string localName, string namespaceUri)
        {
            if (parent == null) yield break;
            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                if (!string.Equals(child.LocalName, localName, StringComparison.Ordinal)) continue;
                if (namespaceUri != null && !string.Equals(child.NamespaceURI, namespaceUri, StringComparison.Ordinal))
                    continue;
                yield return child;
            }
        }
    }
}
