// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_audit_planimetry: judge the documentation surface
// from the MODEL, and hand back findings rather than prose.
//
// It runs over exactly the object horizun_query_planimetry renders - one
// collector, PlanimetryInventory - so the two tools can never disagree about
// what is on a sheet. Read-only by construction: no Transaction anywhere on this
// path.
//
// TWO SETS OF RULES, and the boundary is the point of the whole design:
//
//   * The UNIVERSAL set is what is true without a company standard: overlapping
//     viewports, a sheet with no title block, a tag pointing at nothing, a
//     dimension whose references Revit will not resolve. It cites itself by id
//     and version like any other set.
//   * Everything with a number or a name in it - margins, allowed scales, sheet
//     numbering, which categories must be tagged - arrives as `requirement_set`,
//     INLINE. There is no path parameter: a read-only auditor that opens
//     arbitrary files on the machine is a file reader wearing an auditor's name.
//
// What it will NOT do, because the answers would be invented rather than
// measured: decide which walls should be dimensioned, whether a dimension chain
// is architecturally right, whether a sheet looks balanced, or whether a note is
// technically correct. A rule that asks for one of those is reported as
// not_covered.
//
// There is no 0-100 score. The findings are the deliverable.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class AuditPlanimetryCommand : ICommand
    {
        public string Name => "horizun_audit_planimetry";

        public string Description =>
            "Audit the documentation surface of the active model directly from the database: sheets, placements, " +
            "views, dimensions, tags, text, 2D detail and references between views. Universal checks plus an " +
            "optional inline requirement set. Read-only, deterministic, paginated; findings are blocking, " +
            "advisory or unknown, and an unreadable fact is never a pass.";

        public const int MaxFindings = 500;
        public const int DefaultFindings = 100;

        private static readonly string[] Scopes = { "model", "sheets", "views" };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            string scopeName = (request.Value<string>("scope") ?? "model").ToLowerInvariant();
            if (!Scopes.Contains(scopeName, StringComparer.Ordinal))
                return CommandResult.Fail("scope must be one of: " + string.Join(", ", Scopes) + ".");

            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            double scale;
            if (!PlanimetryGeometry.TryScaleFromFeet(units, out scale))
                return CommandResult.Fail("units must be mm, m or feet.");

            int maxFindings = Math.Max(1, Math.Min(MaxFindings, request.Value<int?>("max_findings") ?? DefaultFindings));

            var scope = new PlanimetryScope();
            string error;
            if (!QueryPlanimetryCommand.Ids(request, "sheet_ids", out scope.SheetIds, out error))
                return CommandResult.Fail(error);
            if (!QueryPlanimetryCommand.Ids(request, "view_ids", out scope.ViewIds, out error))
                return CommandResult.Fail(error);

            if (scopeName == "sheets" && scope.ViewIds != null)
                return CommandResult.Fail(
                    "scope=sheets with view_ids is ambiguous: it reads either as 'these sheets' or as 'the sheets " +
                    "holding these views'. Name one - scope=sheets with sheet_ids, or scope=views with view_ids.");
            if (scopeName == "views" && scope.SheetIds != null)
                return CommandResult.Fail(
                    "scope=views with sheet_ids is ambiguous: it reads either as 'these views' or as 'the views on " +
                    "these sheets'. Name one - scope=views with view_ids, or scope=sheets with sheet_ids.");

            // Which universal checks to run. A name that is not in the catalog is refused:
            // silently running nothing under a misspelt id would report a clean model.
            HashSet<string> wantedChecks = null;
            JArray checks = request["checks"] as JArray;
            if (checks != null && checks.Count > 0)
            {
                wantedChecks = new HashSet<string>(StringComparer.Ordinal);
                foreach (JToken c in checks)
                {
                    string id = c.Type == JTokenType.String ? (string)c : null;
                    if (id == null || PlanimetryRules.Check(id) == null)
                        return CommandResult.Fail(
                            "checks names '" + (id ?? "(not a string)") + "', which is not a universal check. " +
                            "The catalog is published in this reply's `catalog` field and in docs/PLANIMETRY-AUDIT.md.");
                    wantedChecks.Add(id);
                }
            }

            // The requirement set FIRST: a requires_tag rule decides whether the inventory
            // must do the expensive per-view visible-element pass at all, and a pass that
            // did not run must never look like a view with nothing untagged.
            PlanimetryRequirementSet set = null;
            JToken rawSet = request["requirement_set"];
            if (rawSet != null && rawSet.Type != JTokenType.Null)
            {
                var setObject = rawSet as JObject;
                if (setObject == null)
                    return CommandResult.Fail(
                        "requirement_set must be an inline JSON object. This command takes no file path: a " +
                        "read-only auditor that opens arbitrary paths on the machine is a file reader wearing an " +
                        "auditor's name.");
                try { set = PlanimetryRequirementSet.Load(setObject); }
                catch (PlanimetryRequirementSetException ex)
                {
                    return CommandResult.Fail("The requirement set was REFUSED and nothing was audited against " +
                                              "it: " + ex.Message);
                }
                scope.TagCoverageCategories.AddRange(set.TagCoverageCategories);
                scope.TagCoverageExcludeParameters.AddRange(set.TagCoverageExcludeParameters);
            }

            scope.NeedSheets = scopeName != "views";
            scope.NeedViews = true;
            scope.NeedPlacements = scopeName != "views";
            scope.NeedAnnotations = scopeName != "sheets";
            scope.NeedReferences = scopeName != "sheets";
            scope.IncludeParameters = set != null;
            if (set != null) scope.ParameterNames.AddRange(ParameterNames(set));

            bool includeAdvisory = request.Value<bool?>("include_advisory") ?? true;
            bool includePassed = request.Value<bool?>("include_passed_checks") ?? false;

            PlanimetrySnapshot snap;
            try { snap = PlanimetryInventory.Collect(doc, scope, QueryPlanimetryCommand.RevitYear()); }
            catch (Exception ex)
            {
                return CommandResult.Fail("The planimetry inventory could not be read, so nothing was audited: " +
                                          ex.Message);
            }

            var options = new PlanimetryRuleOptions
            {
                Units = units,
                ScaleFromFeet = scale,
                ToleranceFeet = PlanimetryGeometry.TouchToleranceFeet,
                IncludeAdvisory = includeAdvisory,
                IncludePassedChecks = includePassed
            };

            PlanimetryAuditResult universal;
            try { universal = PlanimetryRules.EvaluateUniversal(snap, options); }
            catch (Exception ex) { return CommandResult.Fail("The universal checks failed to run: " + ex.Message); }

            if (wantedChecks != null)
            {
                universal.Findings = universal.Findings.Where(f => wantedChecks.Contains(f.RuleId)).ToList();
                universal.Checks = universal.Checks.Where(c => wantedChecks.Contains(c.RuleId)).ToList();
            }

            var findings = new List<PlanimetryFinding>(universal.Findings);
            var runs = new List<PlanimetryCheckRun>(universal.Checks);
            var failed = new List<PlanimetryCheckFailure>(snap.ChecksFailed);
            failed.AddRange(universal.ChecksFailed);

            if (set != null)
            {
                PlanimetryAuditResult configured;
                try { configured = PlanimetryRules.EvaluateRequirementSet(snap, set, options); }
                catch (Exception ex)
                {
                    return CommandResult.Fail("The requirement set failed to run: " + ex.Message);
                }
                PlanimetryRules.Attribute(configured, set);
                findings.AddRange(configured.Findings);
                runs.AddRange(configured.Checks);
                failed.AddRange(configured.ChecksFailed);
            }

            findings.Sort(PlanimetryFinding.Compare);

            string queryHash = QueryHash(request);
            string setFingerprint = Fingerprint(findings);
            int offset = 0;
            string cursor = request.Value<string>("cursor");
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                string cursorError;
                if (!TryCursor(cursor, queryHash, setFingerprint, out offset, out cursorError))
                    return CommandResult.Fail(cursorError);
            }
            if (offset > findings.Count)
                return CommandResult.Fail("The cursor starts beyond the current finding set. Re-run without cursor.");

            List<PlanimetryFinding> page = findings.Skip(offset).Take(maxFindings).ToList();
            int nextOffset = offset + page.Count;
            string nextCursor = nextOffset < findings.Count ? MakeCursor(nextOffset, queryHash, setFingerprint) : null;

            int blocking = findings.Count(f => f.Severity == "blocking" && f.Status == "failed");
            int advisory = findings.Count(f => f.Severity == "advisory" && f.Status == "failed");
            int unknown = findings.Count(f => f.Status == "unknown");
            int passed = runs.Count(c => c.Status == "passed");

            var result = new JObject
            {
                ["document"] = snap.DocumentTitle,
                ["scope"] = scopeName,
                ["units"] = new JObject { ["internal"] = "feet", ["display"] = units },
                ["tolerance"] = new JObject
                {
                    ["value"] = PlanimetryGeometry.Display(PlanimetryGeometry.TouchToleranceFeet, scale),
                    ["units"] = units,
                    ["meaning"] = "Two placements whose edges are within this distance are TOUCHING, not " +
                                  "overlapping. An overlap must exceed it on both axes."
                },
                ["universal_set"] = new JObject
                {
                    ["id"] = PlanimetryRules.UniversalId,
                    ["version"] = PlanimetryRules.UniversalVersion,
                    ["checks_available"] = PlanimetryRules.Catalog.Length
                },
                ["requirement_set_id"] = set == null ? (JToken)JValue.CreateNull() : set.Id,
                ["requirement_set_version"] = set == null ? (JToken)JValue.CreateNull() : set.Version,
                ["requirement_set_sha256"] = set == null ? (JToken)JValue.CreateNull() : set.Sha256,
                ["checks_run"] = runs.Count,
                ["checks"] = new JArray(runs.OrderBy(c => c.RuleId, StringComparer.Ordinal)
                                            .Select(c => (JToken)c.ToJson())),
                ["checks_failed"] = new JArray(failed.Select(c => (JToken)c.ToJson())),
                ["populations_examined"] = new JObject
                {
                    ["sheets"] = snap.Sheets.Count,
                    ["views"] = snap.Views.Count,
                    ["placements"] = snap.Placements.Count,
                    ["dimensions"] = snap.Annotations.Count(a => a.Kind == "dimension"),
                    ["tags"] = snap.Annotations.Count(a => a.Kind == "tag" || a.Kind == "revision_tag"),
                    ["text_notes"] = snap.Annotations.Count(a => a.Kind == "text_note"),
                    ["detail_2d"] = snap.Annotations.Count(a =>
                        a.Kind == "detail_curve" || a.Kind == "filled_region" ||
                        a.Kind == "detail_component" || a.Kind == "generic_annotation"),
                    ["view_references"] = snap.References.Count
                },
                ["coverage_complete"] = snap.CoverageComplete,
                ["unreadable_total"] = snap.UnreadableTotal,
                ["visibility_coverage"] = snap.VisibilityCoverage,
                ["link_coverage"] = snap.LinkCoverage,
                ["blocking_total"] = blocking,
                ["advisory_total"] = advisory,
                ["unknown_total"] = unknown,
                ["passed_checks_total"] = passed,
                ["findings_total"] = findings.Count,
                ["findings_returned"] = page.Count,
                ["offset"] = offset,
                ["truncated"] = nextCursor != null,
                ["next_cursor"] = nextCursor == null ? (JToken)JValue.CreateNull() : nextCursor,
                ["finding_set_fingerprint"] = setFingerprint.Substring(0, 16),
                ["include_advisory"] = includeAdvisory,
                ["include_passed_checks"] = includePassed,
                ["findings"] = new JArray(page.Select(f => (JToken)f.ToJson())),
                ["not_covered"] = NotCovered(),
                ["note"] = Note(snap, unknown, blocking, advisory)
            };
            if (wantedChecks == null)
                result["catalog"] = new JArray(PlanimetryRules.Catalog.Select(c => (JToken)new JObject
                {
                    ["rule_id"] = c.Id,
                    ["severity"] = c.Severity,
                    ["entity"] = c.Entity,
                    ["description"] = c.Description
                }));
            return CommandResult.Ok(result);
        }

        /// <summary>
        /// The judgements this phase deliberately does not make. Published on EVERY reply so
        /// a caller never reads their absence as "the model is fine in these respects".
        /// </summary>
        private static JArray NotCovered()
        {
            return new JArray
            {
                new JObject
                {
                    ["capability"] = "which elements should be dimensioned",
                    ["reason"] = "Requires design intent. This phase measures the dimensions that exist; it does " +
                                 "not decide which ones ought to."
                },
                new JObject
                {
                    ["capability"] = "whether a dimension chain is architecturally correct",
                    ["reason"] = "A discipline judgement, not a database fact."
                },
                new JObject
                {
                    ["capability"] = "whether a sheet is visually balanced or a plan 'looks right'",
                    ["reason"] = "Needs visual analysis. horizun_capture_view can produce the image for a human " +
                                 "or a later phase; no rule here reads one."
                },
                new JObject
                {
                    ["capability"] = "whether a note is technically correct, or a construction detail complete",
                    ["reason"] = "Requires domain review. The auditor reports that the note exists and is " +
                                 "non-empty, and stops there."
                },
                new JObject
                {
                    ["capability"] = "applying any correction",
                    ["reason"] = "This phase is read-only by construction. `recommended_tool` names the typed " +
                                 "command a human could use; `fixable` is false on every finding."
                }
            };
        }

        private static string Note(PlanimetrySnapshot snap, int unknown, int blocking, int advisory)
        {
            var bits = new List<string>();
            if (!snap.CoverageComplete) bits.Add(snap.CoverageNote());
            if (unknown > 0)
                bits.Add(unknown + " finding(s) are UNKNOWN: the check could not conclude for those elements. " +
                         "Unknown is not a pass, and a check with unknowns is reported as unknown rather than " +
                         "passed.");
            if (bits.Count == 0 && blocking == 0 && advisory == 0)
                return "Every check that had a population to examine passed, and coverage is complete.";
            if (bits.Count == 0) return null;
            return string.Join(" ", bits) +
                   " Do not report this model as clean while either of the above holds.";
        }

        /// <summary>Parameter names any rule asks about, so the inventory reads those and no
        /// others off the sheets and views it describes. Internal because
        /// horizun_fix_planimetry re-runs this audit and must gather the SAME names.</summary>
        internal static IEnumerable<string> ParameterNames(PlanimetryRequirementSet set)
        {
            var names = new List<string>();
            foreach (PlanimetryRule rule in set.Rules)
            {
                if (rule.Operator == "required_parameter" && rule.Value is JArray required)
                    foreach (JToken v in required)
                        if (v.Type == JTokenType.String && !names.Contains((string)v, StringComparer.Ordinal))
                            names.Add((string)v);
                foreach (string field in new[] { rule.AssertionField }
                             .Concat(rule.Selectors.Select(s => s.Field)))
                {
                    if (field == null || !field.StartsWith("parameter:", StringComparison.Ordinal)) continue;
                    string name = field.Substring("parameter:".Length);
                    if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
                }
            }
            return names;
        }

        // ---------------------------------------------------------------------
        // Cursor, bound to the arguments AND to the finding set it paged.
        // ---------------------------------------------------------------------
        private static string QueryHash(JObject request)
        {
            var copy = (JObject)request.DeepClone();
            copy.Remove("cursor");
            copy.Remove("max_findings");
            return RequestFingerprint.Sha256Hex(RequestFingerprint.Canonical(copy));
        }

        /// <summary>The finding-set fingerprint, exactly as the reply publishes its first 16
        /// characters. Internal because horizun_fix_planimetry recomputes it to report
        /// whether the source audit still describes the model.</summary>
        internal static string Fingerprint(IEnumerable<PlanimetryFinding> findings)
        {
            return RequestFingerprint.Sha256Hex(string.Join("\n", findings.Select(f => f.Signature())));
        }

        private static string MakeCursor(int offset, string queryHash, string setHash)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(
                offset.ToString(CultureInfo.InvariantCulture) + "\n" + queryHash + "\n" + setHash));
        }

        private static bool TryCursor(string cursor, string queryHash, string setHash, out int offset, out string error)
        {
            offset = 0; error = null;
            try
            {
                string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n');
                if (parts.Length != 3 ||
                    !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)
                    throw new FormatException("cursor payload has the wrong shape");
                if (parts[1] != queryHash)
                { error = "The cursor belongs to different audit arguments. Re-run without cursor."; return false; }
                if (parts[2] != setHash)
                {
                    error = "The findings changed since the previous page. The cursor is stale; re-run from the " +
                            "first page.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "cursor is invalid: " + ex.Message + ". Re-run without cursor.";
                return false;
            }
        }
    }
}
