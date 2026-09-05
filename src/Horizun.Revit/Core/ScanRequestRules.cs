// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT A SCAN REQUEST MAY SAY, and what it must be refused for.
//
// ModelScanCommand already refuses an unknown SECTION name, and the comment
// beside that check states the reason exactly:
//
//     "An unknown section name silently doing nothing is how a caller thinks it
//      checked something it never checked."
//
// The same reasoning applies one level up and was not applied there. Measured on
// this tree at v1.1.6:
//
//   * The tool's InputSchema has no `additionalProperties: false` - the only
//     schema in that file without it - and nothing in the server or the
//     dispatcher validates arguments against the schema anyway. A caller that
//     sends `sectons` gets a full, successful, clean-looking scan of everything.
//   * `top` is CLAMPED, not checked: `Math.Max(1, ...)`. `top: 0` and `top: -5`
//     silently become 1, and there is no ceiling at all.
//   * `sections` is read as `request["sections"] as JArray`, so anything that is
//     not an array - a bare string like "health", an object - yields null, and
//     null means every section. The most plausible client mistake asks for one
//     section and runs the most expensive call the tool has.
//   * An empty array means all twelve too.
//
// Every one of those returns a reply that looks clean. That is the failure this
// file exists to make impossible: a request nobody can satisfy is refused by
// name, and the refusal says what was wrong and what the alternatives are.
//
// Revit-free on purpose. Deciding whether a request is answerable is arithmetic
// over a JObject, and it is exactly the decision that must be provable without a
// model in the room.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ScanRequestCodes
    {
        public const string UnknownKey = "unknown_request_key";
        public const string BadTop = "invalid_top";
        public const string BadSections = "invalid_sections";
        public const string EmptySections = "empty_sections";

        public static readonly string[] All = { UnknownKey, BadTop, BadSections, EmptySections };
    }

    public sealed class ScanRequestVerdict
    {
        public bool Ok;
        public string Code;
        public string Message;

        public static ScanRequestVerdict Fine() => new ScanRequestVerdict { Ok = true };
        public static ScanRequestVerdict Refused(string code, string message) =>
            new ScanRequestVerdict { Ok = false, Code = code, Message = message };
    }

    public static class ScanRequestRules
    {
        /// <summary>
        /// Every key the scan understands. A request naming anything else is
        /// refused - it is not ignored, because a misspelt option produces a
        /// reply indistinguishable from one where the option did what was asked.
        /// </summary>
        public static readonly string[] KnownKeys =
        {
            "target_document_title",
            "top",
            "sections",
            "target_parameter",
            "section_limits",
            "cursor",
            "weight_profile",
            "warning_profile",
            "naming_profile",
            "family_profile",
            "family_budget",
            "view_profile",
            "sheet_rules",
            "parameter_profile",
            "spatial_rules",
            "documentary_profile",
            "fourd_profile",
            "fived_profile",
            "classification_catalogue",
        };

        /// <summary>
        /// Every option horizun_audit_model understands. Its `top` had exactly the
        /// same clamp - `Math.Max(1, ...)` - and the same silence about anything
        /// misspelt, so it reads the SAME rules below rather than a second copy of
        /// them. Two tables of one fact is how the two halves came to disagree
        /// about a parameter elsewhere in this bridge.
        /// </summary>
        public static readonly string[] AuditKnownKeys =
        {
            "target_document",
            "top",
            "requirement_set",
            "tolerances",
            "readiness_roles",
            "warning_profile",
            "workset_rules",
            "propose_corrections",
            "prevention_gate",
            "store_snapshot",
            "health_profile",
        };

        /// <summary>Unknown option names, refused with the real list. Shared.</summary>
        public static ScanRequestVerdict CheckUnknownKeys(JObject request, IReadOnlyCollection<string> knownKeys,
                                                          string what)
        {
            if (request == null) return ScanRequestVerdict.Fine();
            var known = new HashSet<string>(knownKeys, StringComparer.Ordinal);
            List<string> unknown = request.Properties()
                .Select(p => p.Name)
                .Where(n => !known.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            if (unknown.Count == 0) return ScanRequestVerdict.Fine();

            return ScanRequestVerdict.Refused(ScanRequestCodes.UnknownKey,
                "this request names " + (unknown.Count == 1 ? "an option" : "options") + " " + what + " does not " +
                "have: " + string.Join(", ", unknown.Select(u => "'" + u + "'")) + ". The options are: " +
                string.Join(", ", knownKeys.OrderBy(k => k, StringComparer.Ordinal)) + ". Nothing was read - " +
                "an option accepted and ignored produces a reply that cannot be told apart from one where it " +
                "did what you asked.");
        }

        /// <summary>`top`, checked rather than clamped. Shared by both tools.</summary>
        public static ScanRequestVerdict CheckTop(JObject request)
        {
            JToken top = request?["top"];
            if (top == null || top.Type == JTokenType.Null) return ScanRequestVerdict.Fine();

            if (top.Type != JTokenType.Integer)
                return ScanRequestVerdict.Refused(ScanRequestCodes.BadTop,
                    "'top' must be a whole number and this is a " + top.Type.ToString().ToLowerInvariant() +
                    ". It used to be read straight into an integer, so a value of the wrong type surfaced as a " +
                    "cast exception with no mention of 'top'.");

            int n = top.Value<int>();
            if (n < 1)
                return ScanRequestVerdict.Refused(ScanRequestCodes.BadTop,
                    "'top' is " + n.ToString(CultureInfo.InvariantCulture) + ". It used to be clamped up to 1 " +
                    "in silence; a limit nobody can honour is refused instead, because a reply shaped by a " +
                    "number the caller did not choose is not the reply they asked for.");
            if (n > SectionBudgets.MaxLimit)
                return ScanRequestVerdict.Refused(ScanRequestCodes.BadTop,
                    "'top' is " + n.ToString(CultureInfo.InvariantCulture) + " and the ceiling is " +
                    SectionBudgets.MaxLimit.ToString(CultureInfo.InvariantCulture) + ". There was no ceiling at " +
                    "all before, so one call could return every element in the model. Page through it with " +
                    "'cursor', or raise one section with 'section_limits'.");

            return ScanRequestVerdict.Fine();
        }

        /// <summary>The audit's whole shape check, from the shared pieces.</summary>
        public static ScanRequestVerdict CheckAudit(JObject request)
        {
            ScanRequestVerdict v = CheckUnknownKeys(request, AuditKnownKeys, "the audit");
            if (!v.Ok) return v;
            return CheckTop(request);
        }

        /// <summary>
        /// Check the shape of the request before any of it is acted on.
        ///
        /// Order matters: unknown keys first, because a caller who misspelt an
        /// option wants to hear about THAT rather than about a consequence of it.
        /// </summary>
        public static ScanRequestVerdict Check(JObject request, IReadOnlyCollection<string> knownSections)
        {
            if (request == null) return ScanRequestVerdict.Fine();

            ScanRequestVerdict keys = CheckUnknownKeys(request, KnownKeys, "the scan");
            if (!keys.Ok) return keys;

            ScanRequestVerdict top = CheckTop(request);
            if (!top.Ok) return top;

            JToken sections = request["sections"];
            if (sections != null && sections.Type != JTokenType.Null)
            {
                if (sections.Type != JTokenType.Array)
                    return ScanRequestVerdict.Refused(ScanRequestCodes.BadSections,
                        "'sections' must be an array of section names and this is a " +
                        sections.Type.ToString().ToLowerInvariant() + ". Anything that was not an array used to be " +
                        "read as 'run every section', so asking for one section ran the most expensive call the " +
                        "tool has and the reply said twelve were requested.");

                if (((JArray)sections).Count == 0)
                    return ScanRequestVerdict.Refused(ScanRequestCodes.EmptySections,
                        "'sections' is empty. An empty list used to mean ALL TWELVE, which is the opposite of what " +
                        "it looks like it means. Omit 'sections' to run them all, or name the ones you want: " +
                        string.Join(", ", (knownSections ?? new string[0]).OrderBy(s => s, StringComparer.Ordinal)) + ".");

                foreach (JToken t in (JArray)sections)
                    if (t.Type != JTokenType.String)
                        return ScanRequestVerdict.Refused(ScanRequestCodes.BadSections,
                            "'sections' must contain section names as strings, and one entry is a " +
                            t.Type.ToString().ToLowerInvariant() + ".");
            }

            return ScanRequestVerdict.Fine();
        }

        /// <summary>
        /// Which sections a request actually asks for, once it has passed Check.
        /// Omitting the key means all of them; an empty array never gets here.
        /// </summary>
        public static bool TargetParameterWouldBeIgnored(JObject request, IReadOnlyCollection<string> requestedSections)
        {
            if (request == null) return false;
            JToken tp = request["target_parameter"];
            if (tp == null || tp.Type == JTokenType.Null) return false;
            if (string.IsNullOrWhiteSpace(tp.Value<string>())) return false;
            // It is read by the 'types' section and by nothing else. Accepting it
            // while that section is not running returns a clean-looking reply that
            // never looked at a single parameter.
            return requestedSections != null &&
                   !requestedSections.Contains("types", StringComparer.OrdinalIgnoreCase);
        }
    }
}
