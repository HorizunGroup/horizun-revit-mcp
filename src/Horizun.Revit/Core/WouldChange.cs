// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Whether a planned parameter write would CHANGE anything (story 5.14).
//
// family_apply's plan listed every requested value under params_would_set,
// including the ones the parameter already holds - so every caller had to diff
// before.value against requested themselves to avoid presenting a plan that
// appears to touch things it does not. The one place that has both values is
// the plan row; this is the rule it computes, once.
//
// The verdict is a TRI-STATE and the third value is the point:
//   true  - the parameter reads something else; applying would move it.
//   false - the parameter already reads the requested value; applying would
//           rewrite the same value.
//   null  - it cannot be told, and `why` says why. The big case is a unit-aware
//           string ("15 cm") onto Double/Integer storage: Revit parses the units
//           internally at apply time and never hands the number back, so no
//           comparison made HERE can know what it will store. Guessing false
//           would hide a real write from the plan; guessing true would invent
//           one. The same coercions TryApply performs are mirrored exactly -
//           a request the apply will refuse is null here, never a verdict.
//
// Revit-free on purpose: the facts (the before-read) need Revit, the comparison
// is arithmetic over JTokens, and the mistakes worth pinning - "I could not
// look" collapsing into "it matches", a tolerance-less double compare flagging
// the last ulp as drift - are provable without a model open.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class WouldChange
    {
        /// <summary>Same relative tolerance the post-commit verification uses:
        /// bit-equality would report drift on the last ulp of a unit parse.</summary>
        private const double DoubleRelTolerance = 1e-9;

        /// <summary>
        /// true would move it, false already holds it, NULL cannot be told - and
        /// `why` carries the reason whenever the answer is null.
        /// </summary>
        public static bool? Judge(string storage, JToken requested, JObject before, out string why)
        {
            why = null;

            if (before == null)
            {
                why = "no current value was captured for this row.";
                return null;
            }
            if (!Readable(before))
            {
                why = "the current value could not be read: " + Reason(before) +
                      " Unknown never compares equal - 'I could not look' must not read as 'it matches'.";
                return null;
            }
            var beforeStorage = before["storage"];
            if (beforeStorage == null || beforeStorage.Type != JTokenType.String ||
                !string.Equals(before.Value<string>("storage"), storage, StringComparison.Ordinal))
            {
                why = "the captured value's storage type does not match the parameter's; the comparison would " +
                      "be between two different renderings.";
                return null;
            }

            var bv = before["value"];
            bool currentEmpty = bv == null || bv.Type == JTokenType.Null;
            bool reqNull = requested == null || requested.Type == JTokenType.Null;

            switch (storage)
            {
                case "String":
                    if (reqNull)
                    {
                        why = "null is not a value String storage takes; the apply will refuse this row.";
                        return null;
                    }
                    string sv = Text(requested);
                    string cur = currentEmpty ? null : bv.Value<string>();
                    // An empty parameter receiving "" still counts as a write Revit may
                    // record (null and empty are distinct states); compared strictly.
                    return !string.Equals(sv, cur, StringComparison.Ordinal);

                case "Integer":
                    if (reqNull)
                    {
                        why = "null is not a value Integer storage takes; the apply will refuse this row.";
                        return null;
                    }
                    long want;
                    if (requested.Type == JTokenType.Boolean) want = requested.Value<bool>() ? 1L : 0L;
                    else if (requested.Type == JTokenType.Integer) want = requested.Value<long>();
                    else if (requested.Type == JTokenType.String) { why = UnitAware(); return null; }
                    else
                    {
                        why = "a " + requested.Type + " cannot be coerced to Integer; the apply will refuse this row.";
                        return null;
                    }
                    if (currentEmpty) return true;
                    return bv.Value<long>() != want;

                case "Double":
                    if (reqNull)
                    {
                        why = "null is not a value Double storage takes; the apply will refuse this row.";
                        return null;
                    }
                    if (requested.Type == JTokenType.Integer || requested.Type == JTokenType.Float)
                    {
                        if (currentEmpty) return true;
                        return !SameDouble(bv.Value<double>(), requested.Value<double>());
                    }
                    if (requested.Type == JTokenType.String) { why = UnitAware(); return null; }
                    why = "a " + requested.Type + " cannot be coerced to Double; the apply will refuse this row.";
                    return null;

                case "ElementId":
                    long idv;
                    if (reqNull) idv = -1;                       // the apply coerces null to 'no element'
                    else if (requested.Type == JTokenType.Integer) idv = requested.Value<long>();
                    else if (requested.Type == JTokenType.String &&
                             long.TryParse(Text(requested), NumberStyles.Integer, CultureInfo.InvariantCulture, out idv)) { }
                    else
                    {
                        why = "ElementId storage takes an element id (or null / -1 to clear), not a " +
                              requested.Type + "; the apply will refuse this row.";
                        return null;
                    }
                    if (currentEmpty)
                    {
                        // AsElementId normally renders InvalidElementId as "-1"; a null here
                        // is a reading this rule has no precedent for. Unknown, not a verdict.
                        why = "the current ElementId read back null rather than an id; whether " + idv +
                              " equals it cannot be told.";
                        return null;
                    }
                    // The before-read renders ElementId.ToString(); compare the same rendering
                    // or every ElementId row would read as drift.
                    return !string.Equals(bv.Value<string>(), idv.ToString(CultureInfo.InvariantCulture),
                                          StringComparison.Ordinal);

                default:
                    why = "storage type " + (storage ?? "(none)") + " takes no comparable value.";
                    return null;
            }
        }

        private static string UnitAware()
        {
            return "the requested value is a unit-aware string: Revit parses its units internally at apply " +
                   "time (SetValueString) and never hands the number back, so whether it equals the current " +
                   "value cannot be told before the write.";
        }

        private static bool SameDouble(double x, double y)
        {
            double biggest = Math.Max(Math.Abs(x), Math.Abs(y));
            double delta = Math.Abs(x - y);
            return biggest > 1e-9 ? (delta / biggest) <= DoubleRelTolerance : delta <= 1e-9;
        }

        private static bool Readable(JObject v)
        {
            return v != null && v["readable"] != null && v["readable"].Type == JTokenType.Boolean &&
                   v.Value<bool>("readable");
        }

        private static string Reason(JObject v)
        {
            var e = v["error"];
            return e != null && e.Type == JTokenType.String ? v.Value<string>("error") : "reason unrecorded.";
        }

        private static string Text(JToken v)
        {
            return v.Type == JTokenType.String ? v.Value<string>() : v.ToString();
        }
    }
}
