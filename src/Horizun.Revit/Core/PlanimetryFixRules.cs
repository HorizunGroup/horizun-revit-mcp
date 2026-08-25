// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE RULES OF CORRECTING A PLANIMETRY FINDING, without a Revit in the room.
//
// horizun_audit_planimetry has eyes; horizun_fix_planimetry is the hands - and a
// hand that guesses is worse than no hand. Every decision that is arithmetic
// rather than API lives here, where it is an ordinary unit test:
//
//   * THE OPERATION CATALOG is closed. Nine operations, each with a closed field
//     set, a named target field, and the finding entity kinds it may address. An
//     operation outside the catalog is a capability gap (the standard fallback
//     contract applies); a FIELD outside an operation's set is the caller's typo
//     and earns no fallback.
//   * A FINDING IS THE ONLY LICENCE TO WRITE. Every action cites the finding it
//     corrects - rule id, requirement set, element ids, sheet/view, and the
//     OBSERVED state the caller read. The identity finds the finding again at
//     apply time; the observed block proves the model still shows what the caller
//     approved a fix for. Identity missing -> stale finding. Observed moved ->
//     stale observation. Neither writes anything.
//   * A UNIVERSAL RULE NAMES ITS OWN REMEDIES. Which operations may cite which
//     universal check is a table here, because "fix an orphaned tag by rescaling
//     a view" is a category error the schema alone cannot catch. Rules from an
//     inline requirement set are unknown to this table and are judged by entity
//     kind instead.
//   * UNKNOWN NEVER BECOMES A CORRECTION. A finding whose recomputed status is
//     `unknown` was not measured; correcting it would write on top of a fact
//     nobody read. Refused per action, before any transaction.
//   * RESOLUTION IS THE AUDITOR'S VERDICT, NOT THE WRITER'S. After the commit the
//     SAME rules re-run, and a selected finding is `resolved` only when its rule
//     stops producing it. `persistent` and `new` are reported beside it, because
//     resolving one finding must not hide that another appeared.
//
// Deliberately NOT here, published as refusals: automatic packing, auto-tagging,
// intent dimensioning, revision generation, visual judgement, and every choice
// of type, position, name or standard. A missing instruction is not permission
// to choose.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One operation the fix command implements, as the closed catalog knows it.</summary>
    public sealed class PlanimetryFixOperation
    {
        public string Name;

        /// <summary>The request field naming the element that is WRITTEN.</summary>
        public string TargetField;

        /// <summary>Fields an action of this operation may carry, beyond operation/finding.</summary>
        public string[] Fields;

        /// <summary>Of those, the ones that must be present.</summary>
        public string[] RequiredFields;

        /// <summary>Finding entity kinds this operation may address (see EntityKindOf).</summary>
        public string[] EntityKinds;

        /// <summary>True when the operation takes points or boxes in the call's units.</summary>
        public bool Geometric;
    }

    public static class PlanimetryFixRules
    {
        // ---------------------------------------------------------------------
        // The catalog. Closed on purpose: packing, tagging, dimensioning-by-
        // intent and revision generation are later phases and are refused BY
        // NAME rather than simulated here.
        // ---------------------------------------------------------------------
        public static readonly PlanimetryFixOperation[] Catalog =
        {
            new PlanimetryFixOperation
            {
                Name = "set_view_template", TargetField = "view_id",
                Fields = new[] { "view_id", "template_id" },
                RequiredFields = new[] { "view_id", "template_id" },
                EntityKinds = new[] { "view" }
            },
            new PlanimetryFixOperation
            {
                Name = "set_view_scale", TargetField = "view_id",
                Fields = new[] { "view_id", "scale" },
                RequiredFields = new[] { "view_id", "scale" },
                EntityKinds = new[] { "view" }
            },
            new PlanimetryFixOperation
            {
                Name = "rename_view", TargetField = "view_id",
                Fields = new[] { "view_id", "new_name" },
                RequiredFields = new[] { "view_id", "new_name" },
                EntityKinds = new[] { "view" }
            },
            new PlanimetryFixOperation
            {
                Name = "rename_sheet", TargetField = "sheet_id",
                Fields = new[] { "sheet_id", "new_number", "new_name" },
                // At least one of new_number/new_name - enforced by RequiredFieldError,
                // which is why neither appears here.
                RequiredFields = new[] { "sheet_id" },
                EntityKinds = new[] { "sheet" }
            },
            new PlanimetryFixOperation
            {
                Name = "place_title_block", TargetField = "sheet_id",
                Fields = new[] { "sheet_id", "title_block_type_id" },
                RequiredFields = new[] { "sheet_id", "title_block_type_id" },
                EntityKinds = new[] { "sheet" }
            },
            new PlanimetryFixOperation
            {
                Name = "move_viewport", TargetField = "viewport_id",
                Fields = new[] { "viewport_id", "point" },
                RequiredFields = new[] { "viewport_id", "point" },
                EntityKinds = new[] { "viewport", "placement" },
                Geometric = true
            },
            new PlanimetryFixOperation
            {
                Name = "move_schedule", TargetField = "schedule_instance_id",
                Fields = new[] { "schedule_instance_id", "point" },
                RequiredFields = new[] { "schedule_instance_id", "point" },
                EntityKinds = new[] { "schedule_placement", "placement" },
                Geometric = true
            },
            new PlanimetryFixOperation
            {
                Name = "clear_element_override", TargetField = "element_id",
                Fields = new[] { "element_id", "view_id" },
                RequiredFields = new[] { "element_id", "view_id" },
                EntityKinds = new[] { "dimension", "tag", "text_note", "detail_2d", "annotation" }
            },
            new PlanimetryFixOperation
            {
                Name = "set_crop", TargetField = "view_id",
                Fields = new[] { "view_id", "crop" },
                RequiredFields = new[] { "view_id", "crop" },
                EntityKinds = new[] { "view", "dimension", "tag", "text_note", "detail_2d", "annotation" },
                Geometric = true
            }
        };

        public static PlanimetryFixOperation Operation(string name)
        {
            if (name == null) return null;
            foreach (PlanimetryFixOperation op in Catalog)
                if (string.Equals(op.Name, name, StringComparison.Ordinal)) return op;
            return null;
        }

        public static string OperationsSentence()
            => string.Join(", ", Catalog.Select(o => o.Name));

        // ---------------------------------------------------------------------
        // Which operations may cite which UNIVERSAL check. The universal catalog
        // is this bridge's own, so the remedies are its to know; a rule id NOT in
        // this table (a requirement-set rule) is judged by entity kind instead.
        //
        // A check that maps to NO operation is deliberately unfixable by this
        // phase - deleting an orphan belongs to horizun_delete_verified, placing
        // an unplaced view is a layout decision (EPIC 7.2), and every `unknown`
        // check is not a finding but the absence of a measurement.
        // ---------------------------------------------------------------------
        private static readonly Dictionary<string, string[]> UniversalRemedies =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "sheet.no-titleblock", new[] { "place_title_block" } },
            { "sheet.viewport-overlap", new[] { "move_viewport" } },
            { "sheet.viewport-schedule-overlap", new[] { "move_viewport", "move_schedule" } },
            { "sheet.schedule-overlap", new[] { "move_schedule" } },
            { "sheet.placement-outside-extent", new[] { "move_viewport", "move_schedule" } },
            { "view.no-template", new[] { "set_view_template" } },
            { "dimension.outside-annotation-crop", new[] { "set_crop" } },
            { "tag.outside-annotation-crop", new[] { "set_crop" } },
            { "text.outside-annotation-crop", new[] { "set_crop" } },
            { "detail_2d.outside-crop", new[] { "set_crop" } }
        };

        /// <summary>
        /// May `operation` cite the finding of `ruleId`? Null when it may; otherwise a
        /// sentence naming what may. Universal rule ids are judged by the remedies
        /// table; anything else (a requirement-set rule) by the operation's entity
        /// kinds against the finding's declared entity kind.
        /// </summary>
        public static string RemedyError(string ruleId, string requirementSetId, string entityKind,
                                         PlanimetryFixOperation operation)
        {
            if (operation == null) return "the operation is not in the catalog";

            bool universal = string.Equals(requirementSetId, PlanimetryRules.UniversalId, StringComparison.Ordinal);
            if (universal)
            {
                PlanimetryCheck check = PlanimetryRules.Check(ruleId);
                if (check == null)
                    return "finding rule '" + ruleId + "' is not a universal planimetry check. The catalog is " +
                           "published by horizun_audit_planimetry.";
                if (string.Equals(check.Severity, "unknown", StringComparison.Ordinal))
                    return "finding rule '" + ruleId + "' reports that something could NOT be measured. An " +
                           "unknown is the absence of a fact, not a defect, and correcting on top of it would " +
                           "write over a state nobody read. Nothing to fix here until the fact can be read.";
                string[] remedies;
                if (!UniversalRemedies.TryGetValue(ruleId, out remedies))
                    return "finding rule '" + ruleId + "' has no remedy in this phase. " + NoRemedySentence(ruleId);
                if (!remedies.Contains(operation.Name, StringComparer.Ordinal))
                    return "operation '" + operation.Name + "' cannot correct finding rule '" + ruleId +
                           "'. Operations that can: " + string.Join(", ", remedies) + ".";
                return null;
            }

            // A requirement-set rule: this table cannot know its meaning, so the check
            // is structural - the operation must address the entity kind the finding is
            // about. "Fix a sheet-number rule by moving a viewport" fails here.
            if (string.IsNullOrWhiteSpace(entityKind))
                return "the finding cites requirement set '" + requirementSetId + "', so its entity_kind is " +
                       "required to judge whether '" + operation.Name + "' addresses it. Copy entity_kind from " +
                       "the audit finding.";
            if (!operation.EntityKinds.Contains(entityKind, StringComparer.Ordinal))
                return "operation '" + operation.Name + "' addresses " +
                       string.Join("/", operation.EntityKinds) + " findings, but this finding is about a " +
                       entityKind + ".";
            return null;
        }

        /// <summary>Why an unfixable universal rule is unfixable, naming the honest path.</summary>
        private static string NoRemedySentence(string ruleId)
        {
            PlanimetryCheck check = PlanimetryRules.Check(ruleId);
            if (check != null && !string.IsNullOrEmpty(check.RecommendedTool))
                return "The typed command for it is " + check.RecommendedTool + ".";
            if (ruleId == "view.not-placed")
                return "Placing a view is a LAYOUT decision (sheet, position) this phase refuses to make; " +
                       "automatic packing is a later phase.";
            return "It requires a deletion, a model repair or a design decision this phase deliberately " +
                   "does not make.";
        }

        /// <summary>Universal rule ids that map to at least one operation. For the reply's
        /// own catalog block, so a caller can see what is fixable without trying.</summary>
        public static IEnumerable<KeyValuePair<string, string[]>> UniversalRemedyCatalog()
            => UniversalRemedies.OrderBy(kv => kv.Key, StringComparer.Ordinal);

        // ---------------------------------------------------------------------
        // Field discipline per action.
        // ---------------------------------------------------------------------

        /// <summary>Fields every action carries besides the operation's own.</summary>
        private static readonly string[] CommonFields = { "operation", "finding" };

        /// <summary>
        /// A field on this action that neither the common set nor the operation
        /// declares, or null. Unknown fields are refused: a silently ignored field is
        /// a request the caller believes was honoured.
        /// </summary>
        public static string UnknownFieldError(PlanimetryFixOperation op, IEnumerable<string> presentFields)
        {
            foreach (string f in presentFields)
            {
                if (CommonFields.Contains(f, StringComparer.Ordinal)) continue;
                if (op.Fields.Contains(f, StringComparer.Ordinal)) continue;
                return "unknown field '" + f + "' for operation '" + op.Name + "'. Known fields: " +
                       string.Join(", ", op.Fields) + ".";
            }
            return null;
        }

        /// <summary>A required field this action is missing, or null.</summary>
        public static string RequiredFieldError(PlanimetryFixOperation op, Func<string, bool> has)
        {
            foreach (string f in op.RequiredFields)
                if (!has(f))
                    return "operation '" + op.Name + "' requires '" + f + "'.";
            if (op.Name == "rename_sheet" && !has("new_number") && !has("new_name"))
                return "operation 'rename_sheet' requires new_number, new_name or both - a rename that names " +
                       "nothing renames nothing.";
            return null;
        }

        // ---------------------------------------------------------------------
        // Values.
        // ---------------------------------------------------------------------

        /// <summary>Revit's own forbidden characters for element names, plus control
        /// characters. Returns the offending character's printable form, or null.</summary>
        public static string InvalidNameCharacter(string name)
        {
            const string forbidden = "\\:{}[]|;<>?`~";
            if (name == null) return null;
            foreach (char c in name)
            {
                if (forbidden.IndexOf(c) >= 0) return "'" + c + "'";
                if (char.IsControl(c)) return "a control character (U+" + ((int)c).ToString("X4") + ")";
            }
            return null;
        }

        /// <summary>Null when usable as a view/sheet name or number; otherwise the refusal.</summary>
        public static string NameError(string field, string value)
        {
            if (value == null) return "'" + field + "' must be a string.";
            if (string.IsNullOrWhiteSpace(value)) return "'" + field + "' must not be empty or whitespace.";
            if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
                return "'" + field + "' has leading or trailing whitespace, which Revit strips silently - the " +
                       "re-read would then differ from the request. Send the trimmed value you mean.";
            string bad = InvalidNameCharacter(value);
            if (bad != null)
                return "'" + field + "' contains " + bad + ", which Revit refuses in element names.";
            return null;
        }

        /// <summary>Revit accepts view scales 1..24000.</summary>
        public const int MinScale = 1;
        public const int MaxScale = 24000;

        public static string ScaleError(long? scale)
        {
            if (!scale.HasValue) return "'scale' must be an integer.";
            if (scale.Value < MinScale || scale.Value > MaxScale)
                return "'scale' must be " + MinScale + ".." + MaxScale + " (it is the denominator of 1:n).";
            return null;
        }

        /// <summary>A finite [x, y] pair, or the refusal. Points are 2D on purpose: sheet
        /// and view-plane frames have no third axis a caller may aim at.</summary>
        public static string PointError(string field, JToken token, out double x, out double y)
        {
            x = 0; y = 0;
            var arr = token as JArray;
            if (arr == null || arr.Count != 2)
                return "'" + field + "' must be [x, y] - two numbers in the call's units.";
            foreach (JToken v in arr)
                if (v.Type != JTokenType.Float && v.Type != JTokenType.Integer)
                    return "'" + field + "' must contain numbers only.";
            x = arr[0].Value<double>(); y = arr[1].Value<double>();
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
                return "'" + field + "' must be finite.";
            return null;
        }

        /// <summary>A rectangular crop: min strictly below max on both axes.</summary>
        public static string CropError(JToken token, out double minX, out double minY,
                                       out double maxX, out double maxY)
        {
            minX = minY = maxX = maxY = 0;
            var o = token as JObject;
            if (o == null) return "'crop' must be an object with min and max.";
            // A caller asking for a non-rectangular crop is a CAPABILITY question, not a
            // typo, and is answered before this - see NonRectangularCrop.
            foreach (JProperty p in o.Properties())
                if (p.Name != "min" && p.Name != "max" && p.Name != "loop")
                    return "'crop' has unknown key '" + p.Name + "'. Known: min, max.";
            string e = PointError("crop.min", o["min"], out minX, out minY);
            if (e != null) return e;
            e = PointError("crop.max", o["max"], out maxX, out maxY);
            if (e != null) return e;
            if (maxX <= minX || maxY <= minY)
                return "'crop' min must be strictly below max on both axes.";
            return null;
        }

        /// <summary>True when the caller asked for a crop shape this phase cannot
        /// reproduce safely. The refusal is BY CAPABILITY - a script could build the
        /// loop - so it earns the standard fallback contract, unlike a typo.</summary>
        public static bool NonRectangularCrop(JToken token)
        {
            // The KEY's presence is the request, not its value. `"loop": null` used to
            // fall through to the rectangular path and be silently ignored - which this
            // file's own rule calls a request the caller believes was honoured.
            var o = token as JObject;
            return o != null && o.Property("loop") != null;
        }

        /// <summary>The declared default tolerance for geometric postconditions: 0.1 mm in
        /// internal feet - the same canonical grid the before-values are rounded to.</summary>
        public const double DefaultToleranceFeet = 0.1 / 304.8;

        public static string ToleranceError(JToken token, double scaleToFeet, out double toleranceFeet)
        {
            toleranceFeet = DefaultToleranceFeet;
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                return "'tolerance' must be a number in the call's units.";
            double v = token.Value<double>();
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0)
                return "'tolerance' must be a finite number greater than zero.";
            toleranceFeet = v * scaleToFeet;
            return null;
        }

        // ---------------------------------------------------------------------
        // The finding an action cites.
        // ---------------------------------------------------------------------

        /// <summary>What an action's `finding` block resolved to, validated pure.</summary>
        public sealed class CitedFinding
        {
            public string RuleId;
            public string RequirementSetId;
            public string RequirementSetVersion;
            public string RequirementSetSha256;
            public string EntityKind;
            public long? SheetId;
            public long? ViewId;
            public List<long> ElementIds = new List<long>();
            public JObject Observed;

            public bool IsUniversal
                => string.Equals(RequirementSetId, PlanimetryRules.UniversalId, StringComparison.Ordinal);

            /// <summary>The identity that finds this finding again: rule, set, sheet, view
            /// and the sorted element set. NOT the observed values - those are the
            /// staleness check, and folding them in would make every drift read as
            /// "finding gone" instead of "finding moved".</summary>
            public string IdentityKey()
                => IdentityOf(RuleId, RequirementSetId, SheetId, ViewId, ElementIds);
        }

        /// <summary>The schema's own bound on a finding's element list, enforced here
        /// too: a schema is what a client is told, not what the command may assume.</summary>
        public const int MaxFindingElementIds = 100;

        public static string IdentityOf(string ruleId, string setId, long? sheetId, long? viewId,
                                        IEnumerable<long> elementIds)
        {
            List<long> ids = (elementIds ?? Enumerable.Empty<long>()).OrderBy(i => i).ToList();
            return (ruleId ?? "") + "|" + (setId ?? "") + "|" +
                   (sheetId.HasValue ? sheetId.Value.ToString(CultureInfo.InvariantCulture) : "-") + "|" +
                   (viewId.HasValue ? viewId.Value.ToString(CultureInfo.InvariantCulture) : "-") + "|" +
                   string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        }

        public static string IdentityOf(PlanimetryFinding f)
            => IdentityOf(f.RuleId, f.RequirementSetId, f.SheetId, f.ViewId, f.ElementIds);

        /// <summary>
        /// Parse and validate one action's `finding` block. Null with `error` set when it
        /// cannot be used; the error names the field, because "invalid finding" sends
        /// somebody diffing JSON by hand.
        /// </summary>
        public static CitedFinding ParseFinding(JToken token, out string error)
        {
            error = null;
            var o = token as JObject;
            if (o == null) { error = "finding is required and must be an object copied from the audit reply."; return null; }

            var known = new[] { "rule_id", "requirement_set", "requirement_set_version", "requirement_set_sha256",
                                "entity_kind", "sheet_id", "view_id", "element_ids", "observed" };
            foreach (JProperty p in o.Properties())
                if (!known.Contains(p.Name, StringComparer.Ordinal))
                { error = "finding has unknown key '" + p.Name + "'. Known: " + string.Join(", ", known) + "."; return null; }

            var f = new CitedFinding
            {
                RuleId = o.Value<string>("rule_id"),
                RequirementSetId = o.Value<string>("requirement_set"),
                RequirementSetVersion = o.Value<string>("requirement_set_version"),
                RequirementSetSha256 = o.Value<string>("requirement_set_sha256"),
                EntityKind = o.Value<string>("entity_kind")
            };
            if (string.IsNullOrWhiteSpace(f.RuleId)) { error = "finding.rule_id is required."; return null; }
            if (string.IsNullOrWhiteSpace(f.RequirementSetId))
            { error = "finding.requirement_set is required - copy it from the audit finding."; return null; }
            if (string.IsNullOrWhiteSpace(f.RequirementSetVersion))
            { error = "finding.requirement_set_version is required."; return null; }

            if (f.IsUniversal)
            {
                if (!string.Equals(f.RequirementSetVersion, PlanimetryRules.UniversalVersion, StringComparison.Ordinal))
                {
                    error = "finding.requirement_set_version is '" + f.RequirementSetVersion + "', but this " +
                            "bridge's universal catalog is version " + PlanimetryRules.UniversalVersion + ". The " +
                            "finding came from different rules than the ones that would re-check it; re-run the " +
                            "audit on THIS bridge and cite the fresh finding.";
                    return null;
                }
            }
            else if (string.IsNullOrWhiteSpace(f.RequirementSetSha256))
            {
                error = "finding.requirement_set_sha256 is required for a requirement-set finding: it is what " +
                        "proves the set this call carries is the set that produced the finding.";
                return null;
            }

            JToken sheet = o["sheet_id"];
            if (sheet != null && sheet.Type != JTokenType.Null)
            {
                if (sheet.Type != JTokenType.Integer) { error = "finding.sheet_id must be an integer or null."; return null; }
                f.SheetId = sheet.Value<long>();
            }
            JToken view = o["view_id"];
            if (view != null && view.Type != JTokenType.Null)
            {
                if (view.Type != JTokenType.Integer) { error = "finding.view_id must be an integer or null."; return null; }
                f.ViewId = view.Value<long>();
            }

            var ids = o["element_ids"] as JArray;
            if (ids == null || ids.Count == 0)
            { error = "finding.element_ids is required and must be non-empty."; return null; }
            if (ids.Count > MaxFindingElementIds)
            {
                error = "finding.element_ids carries " + ids.Count + " ids; the limit is " +
                        MaxFindingElementIds + ". A finding this wide is not one this bridge produced.";
                return null;
            }
            foreach (JToken id in ids)
            {
                if (id.Type != JTokenType.Integer) { error = "finding.element_ids must contain integers."; return null; }
                f.ElementIds.Add(id.Value<long>());
            }

            var observed = o["observed"] as JObject;
            if (observed == null)
            {
                error = "finding.observed is required - it is the state you saw and are approving a fix FOR. " +
                        "Copy the audit finding's observed block verbatim.";
                return null;
            }
            f.Observed = observed;
            return f;
        }

        /// <summary>
        /// Judge one cited finding against the recomputed CURRENT audit. Returns null
        /// when the finding still stands exactly as cited; otherwise the refusal.
        /// `current` is the finding found under the same identity, or null.
        /// </summary>
        public static string StaleError(CitedFinding cited, PlanimetryFinding current)
        {
            if (current == null)
                return "STALE FINDING: no current finding matches rule '" + cited.RuleId + "' over element(s) [" +
                       string.Join(", ", cited.ElementIds) + "]" +
                       (cited.SheetId.HasValue ? " on sheet " + cited.SheetId.Value : "") +
                       (cited.ViewId.HasValue ? " in view " + cited.ViewId.Value : "") +
                       ". The defect was fixed, the elements changed, or the finding was mis-copied. Re-run " +
                       "horizun_audit_planimetry and cite a finding it returns NOW. Nothing was written.";
            if (string.Equals(current.Status, "unknown", StringComparison.Ordinal))
                return "the finding for rule '" + cited.RuleId + "' is currently UNKNOWN: the fact it is about " +
                       "could not be measured. An unmeasured fact cannot be corrected. Nothing was written.";
            // THE ENTITY KIND IS CORROBORATED, NOT TRUSTED. For a requirement-set
            // finding, entity_kind is the ONLY gate deciding which operation may
            // address it - and it arrives as a string the caller typed. Left
            // unchecked, a legitimate finding over a SHEET could be re-sent as
            // entity_kind "view" and driven through rename_view, which renames it
            // through a path that never validates a sheet number. The recomputed
            // finding knows what it is really about; compare against that.
            if (cited.EntityKind != null && current.EntityKind != null &&
                !string.Equals(cited.EntityKind, current.EntityKind, StringComparison.Ordinal))
                return "the finding for rule '" + cited.RuleId + "' is about a " + current.EntityKind +
                       ", but the request declares entity_kind '" + cited.EntityKind + "'. entity_kind is what " +
                       "decides which operation may address a requirement-set finding, so it is corroborated " +
                       "against the recomputed finding rather than believed. Copy the audit finding verbatim. " +
                       "Nothing was written.";
            if (!JToken.DeepEquals(Canonical(cited.Observed), Canonical(current.Observed)))
                return "STALE OBSERVATION for rule '" + cited.RuleId + "' over element(s) [" +
                       string.Join(", ", cited.ElementIds) + "]: the model no longer shows the observed state " +
                       "this fix was approved against. Cited: " +
                       cited.Observed.ToString(Newtonsoft.Json.Formatting.None) + ". Current: " +
                       current.Observed.ToString(Newtonsoft.Json.Formatting.None) + ". Re-run the audit, read " +
                       "the CURRENT finding, and decide again. Nothing was written.";
            return null;
        }

        /// <summary>Numeric noise must not read as drift: canonicalise numbers through the
        /// same invariant rendering on both sides before DeepEquals.</summary>
        public static JToken Canonical(JToken t)
        {
            if (t == null) return JValue.CreateNull();
            // ABSENCE IS ABSENCE, however it arrived. The auditor builds observed
            // blocks from C# values - ["template_name"] = v.TemplateName, where the
            // name is a null string - and Newtonsoft gives that JValue
            // JTokenType.String with a null value. The SAME block parsed back from
            // JSON gives JTokenType.Null. Both render as `null`, and DeepEquals
            // compares the type first, so a finding was refused as a stale
            // observation against a block identical to it, with a message printing
            // both sides as the same text. Measured live on Revit 2023.
            if (t is JValue value && value.Value == null) return JValue.CreateNull();
            if (t is JObject o)
            {
                var copy = new JObject();
                foreach (JProperty p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    copy[p.Name] = Canonical(p.Value);
                return copy;
            }
            if (t is JArray a) return new JArray(a.Select(Canonical));
            if (t.Type == JTokenType.Float)
            {
                double v = t.Value<double>();
                return new JValue(v.ToString("0.######", CultureInfo.InvariantCulture));
            }
            if (t.Type == JTokenType.Integer)
                return new JValue(t.Value<long>().ToString(CultureInfo.InvariantCulture));
            return t;
        }

        // ---------------------------------------------------------------------
        // Batch discipline.
        // ---------------------------------------------------------------------

        /// <summary>
        /// One WRITE per element per batch. Two actions aiming at one element are
        /// order-dependent in a way nobody stated - the same refusal edit_dimensions
        /// makes, for the same reason.
        /// </summary>
        public static string ClaimTargetError(HashSet<long> claimed, long targetId)
        {
            if (claimed.Add(targetId)) return null;
            return "element " + targetId.ToString(CultureInfo.InvariantCulture) + " is the target of more than " +
                   "one action in this batch. Two writes to one element are order-dependent in a way nobody " +
                   "stated; combine them or split the batch.";
        }

        /// <summary>
        /// Final names/numbers must be unique WITHIN the batch too: two renames landing
        /// on one name would commit and then fail verification late, or worse, the
        /// second would fail inside the transaction after the first wrote.
        /// </summary>
        public static string ClaimFinalValueError(HashSet<string> claimed, string kind, string value)
        {
            if (value == null) return null;
            if (claimed.Add(kind + "" + value)) return null;
            return "two actions in this batch both end at " + kind + " '" + value + "'. The second would " +
                   "collide with the first inside the same transaction.";
        }

        // ---------------------------------------------------------------------
        // Reconciliation with the auditor: the fix's own verdict about findings.
        // ---------------------------------------------------------------------

        public sealed class Reconciliation
        {
            public List<string> ResolvedKeys = new List<string>();
            public List<PlanimetryFinding> Persistent = new List<PlanimetryFinding>();
            public List<PlanimetryFinding> New = new List<PlanimetryFinding>();

            /// <summary>
            /// Selected findings the re-audit could not decide about, because a
            /// collection pass DIED and left its population empty. Their absence from
            /// the after-set is absence of collection, not absence of defect.
            /// </summary>
            public List<string> UndeterminedKeys = new List<string>();
            public string UndeterminedReason;

            /// <summary>False when a dead collection pass makes the NEW list a lower
            /// bound rather than the answer.</summary>
            public bool NewIsComplete = true;

            public int SelectedCount;
        }

        /// <summary>
        /// Classify after the commit. A selected finding is RESOLVED only when no
        /// current finding shares its identity; PERSISTENT otherwise (even if its
        /// observed values moved - the rule still fires over those elements). NEW is
        /// every current finding whose identity existed in neither the before set nor
        /// the selection - resolving one finding must not hide that another appeared.
        /// Passed findings (status=passed) are bookkeeping, not defects, and are
        /// ignored on both sides.
        ///
        /// AN UNCOLLECTED POPULATION IS NOT A RESOLVED ONE. PlanimetryInventory does
        /// not throw when a collection pass dies: it records the failure and returns a
        /// snapshot with that population EMPTY - its own JSON says "was NOT collected.
        /// Its contents are unknown, not empty." Classifying purely on absence from the
        /// after-set would then read a dead views pass as "all twenty view findings
        /// resolved", and would zero the NEW list at the same time, which is exactly
        /// what this block's own prose says it exists to prevent. So the caller passes
        /// how many passes died on each side, and a finding that is merely ABSENT while
        /// a pass died is UNDETERMINED rather than resolved. A finding still PRESENT is
        /// a positive observation and stays trustworthy either way.
        /// </summary>
        public static Reconciliation Reconcile(IEnumerable<CitedFinding> selected,
                                               IEnumerable<PlanimetryFinding> before,
                                               IEnumerable<PlanimetryFinding> after,
                                               int beforeCollectionFailures = 0,
                                               int afterCollectionFailures = 0)
        {
            var result = new Reconciliation();
            var beforeKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (PlanimetryFinding f in before ?? Enumerable.Empty<PlanimetryFinding>())
                if (!string.Equals(f.Status, "passed", StringComparison.Ordinal))
                    beforeKeys.Add(IdentityOf(f));

            var afterByKey = new Dictionary<string, PlanimetryFinding>(StringComparer.Ordinal);
            foreach (PlanimetryFinding f in after ?? Enumerable.Empty<PlanimetryFinding>())
            {
                if (string.Equals(f.Status, "passed", StringComparison.Ordinal)) continue;
                string key = IdentityOf(f);
                if (!afterByKey.ContainsKey(key)) afterByKey.Add(key, f);
            }

            bool afterIncomplete = afterCollectionFailures > 0;
            if (afterIncomplete)
                result.UndeterminedReason =
                    afterCollectionFailures + " collection pass(es) DIED during the re-audit, so a population " +
                    "may be empty because nobody read it rather than because it is clean. A selected finding " +
                    "that is merely absent is therefore UNDETERMINED, not resolved. Re-run " +
                    "horizun_audit_planimetry once the model can be read completely.";
            result.NewIsComplete = !afterIncomplete && beforeCollectionFailures == 0;

            var selectedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (CitedFinding c in selected ?? Enumerable.Empty<CitedFinding>())
            {
                string key = c.IdentityKey();
                if (!selectedKeys.Add(key)) continue;
                result.SelectedCount++;
                PlanimetryFinding still;
                if (afterByKey.TryGetValue(key, out still)) result.Persistent.Add(still);
                else if (afterIncomplete) result.UndeterminedKeys.Add(key);
                else result.ResolvedKeys.Add(key);
            }

            foreach (var kv in afterByKey.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (!beforeKeys.Contains(kv.Key) && !selectedKeys.Contains(kv.Key))
                    result.New.Add(kv.Value);

            return result;
        }

        // ---------------------------------------------------------------------
        // Canonical geometry, shared with the dimension rules so one grid exists.
        // ---------------------------------------------------------------------

        public static string CanonicalTenthMillimetre(double feet)
            => DimensionEditRules.CanonicalTenthMillimetre(feet);

        public static string CanonicalPoint2D(double xFeet, double yFeet)
            => DimensionEditRules.CanonicalTenthMillimetre(xFeet) + "," +
               DimensionEditRules.CanonicalTenthMillimetre(yFeet);

        // ---------------------------------------------------------------------
        // The terminal-state matrix - deliberately the SAME matrix the dimension
        // edits earned, because "what may a caller build on" must not have two
        // answers in one bridge.
        // ---------------------------------------------------------------------
        public const string StateVerifiedApplied = DimensionEditRules.StateVerifiedApplied;
        public const string StateRolledBack = DimensionEditRules.StateRolledBack;
        public const string StateRefused = DimensionEditRules.StateRefused;
        public const string StateUncertain = DimensionEditRules.StateUncertain;
        public const string StateStalePlan = DimensionEditRules.StateStalePlan;

        public static string DecideFinalState(string terminalTransactionStatus, bool allVerified)
            => DimensionEditRules.DecideFinalState(terminalTransactionStatus, allVerified);
    }
}
