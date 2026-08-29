// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE RULES BEHIND horizun_manage_schedules, without a Revit in the room.
//
// A schedule definition is a small program: fields, filters, sorting, grouping,
// totals. Editing one wrong does not crash - it produces a table that is
// quietly about something else, which on a deliverable is the worst outcome
// available. The decisions that make that impossible to do silently are here:
//
//   * FIELDS RESOLVE BY STABLE IDENTITY FIRST. A parameter id names one field;
//     a display name can name two (a shared parameter and a built-in can share
//     spelling). A name that matches twice REFUSES listing the ids - schedules
//     are the place where "the other Comments column" ships to a client.
//
//   * FILTERS AND SORTING ARE DECLARED WHOLE. set_filters/set_sorting REPLACE
//     the list. An additive API reads nicely and is unusable for idempotent
//     production: running the same batch twice must produce the same schedule,
//     not the same schedule with every filter doubled.
//
//   * THE OPERATOR TABLE IS CLOSED and each operator says what value shape it
//     takes. "contains 6" and "greater_than 'yes'" are refused by arithmetic,
//     not discovered by Revit mid-transaction.
//
//   * THE DEFINITION IS SNAPSHOTTED canonically before and after, so "what did
//     this call change" is a diff of two strings, not a memory.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Horizun.Revit.Core
{
    /// <summary>One schedule field as the command read it. Plain facts.</summary>
    public sealed class ScheduleFieldFacts
    {
        public int Index { get; set; }
        public long ParameterId { get; set; }
        public string Name { get; set; }
        public string Heading { get; set; }
        public bool Hidden { get; set; }
        public string FieldType { get; set; }
    }

    public static class ScheduleEditRules
    {
        // ---- operations, closed ---------------------------------------------------

        public const string OpCreate = "create";
        public const string OpDuplicate = "duplicate";
        public const string OpRename = "rename";
        public const string OpAddFields = "add_fields";
        public const string OpRemoveFields = "remove_fields";
        public const string OpSetField = "set_field";
        public const string OpSetFilters = "set_filters";
        public const string OpSetSorting = "set_sorting";
        public const string OpSetOptions = "set_options";

        public static readonly IReadOnlyList<string> KnownOperations = new[]
        {
            OpCreate, OpDuplicate, OpRename, OpAddFields, OpRemoveFields,
            OpSetField, OpSetFilters, OpSetSorting, OpSetOptions
        };

        // ---- the create kinds -----------------------------------------------------
        //
        // Plain category schedules already have a dedicated tool (horizun_create_schedule,
        // which owns the linked-elements option and field aliasing). The kinds here are
        // the ones that tool does NOT make.

        public const string KindMaterialTakeoff = "material_takeoff";
        public const string KindSheetList = "sheet_list";
        public const string KindViewList = "view_list";
        public const string KindRevisionSchedule = "revision_schedule";
        public const string KindKeynoteLegend = "keynote_legend";

        public static readonly IReadOnlyList<string> KnownCreateKinds = new[]
        {
            KindMaterialTakeoff, KindSheetList, KindViewList, KindRevisionSchedule, KindKeynoteLegend
        };

        /// <summary>Which create kinds need a category, decided here so the refusal is uniform.</summary>
        public static bool KindNeedsCategory(string kind) => kind == KindMaterialTakeoff;

        // ---- the filter operator table, closed ------------------------------------

        public enum ValueShape { None, Text, Number, TextOrNumber }

        private static readonly Dictionary<string, ValueShape> Operators =
            new Dictionary<string, ValueShape>(StringComparer.Ordinal)
            {
                { "equal", ValueShape.TextOrNumber },
                { "not_equal", ValueShape.TextOrNumber },
                { "greater_than", ValueShape.Number },
                { "greater_than_or_equal", ValueShape.Number },
                { "less_than", ValueShape.Number },
                { "less_than_or_equal", ValueShape.Number },
                { "contains", ValueShape.Text },
                { "not_contains", ValueShape.Text },
                { "begins_with", ValueShape.Text },
                { "not_begins_with", ValueShape.Text },
                { "ends_with", ValueShape.Text },
                { "not_ends_with", ValueShape.Text },
                { "has_value", ValueShape.None },
                { "has_no_value", ValueShape.None }
            };

        public static IReadOnlyCollection<string> KnownOperators => Operators.Keys;

        /// <summary>
        /// Whether an operator exists and the value the caller sent has the shape it
        /// takes. Refused HERE, by arithmetic - Revit discovering the mismatch would
        /// do so inside the transaction with its least helpful sentence.
        /// </summary>
        public static string ValidateFilter(string op, bool hasTextValue, bool hasNumberValue)
        {
            ValueShape shape;
            if (op == null || !Operators.TryGetValue(op, out shape))
                return "filter operator '" + (op ?? "(null)") + "' is not one this command understands. Known: " +
                       string.Join(", ", Operators.Keys) + ".";
            if (hasTextValue && hasNumberValue)
                return "a filter carries value (text) OR number_value, never both - which one '" + op +
                       "' should compare is not guessable.";
            switch (shape)
            {
                case ValueShape.None:
                    if (hasTextValue || hasNumberValue)
                        return "operator '" + op + "' takes no value; it asks whether the field has one at all.";
                    return null;
                case ValueShape.Text:
                    if (!hasTextValue)
                        return "operator '" + op + "' compares text; pass value (a string).";
                    return null;
                case ValueShape.Number:
                    if (!hasNumberValue)
                        return "operator '" + op + "' compares numbers; pass number_value.";
                    return null;
                default:
                    if (!hasTextValue && !hasNumberValue)
                        return "operator '" + op + "' needs value (text) or number_value.";
                    return null;
            }
        }

        // ---- field resolution ------------------------------------------------------

        /// <summary>
        /// Resolve one caller-named field against the schedule's own fields. By id when
        /// an id was sent (one match or none); by name otherwise, where TWO matches is
        /// a refusal that lists both ids - the other Comments column is the one that
        /// ships. Matching is ordinal-case-insensitive on the field NAME, and heading
        /// is deliberately not consulted: a heading is presentation, renamed freely,
        /// and a reference that broke when somebody retitled a column would be a
        /// puzzle with no clue in it.
        /// </summary>
        public static string ResolveField(IReadOnlyList<ScheduleFieldFacts> fields, long? parameterId, string name,
                                          out ScheduleFieldFacts resolved)
        {
            resolved = null;
            if (fields == null || fields.Count == 0)
                return "the schedule has no fields to resolve against.";
            if (parameterId.HasValue)
            {
                List<ScheduleFieldFacts> byId = fields.Where(f => f.ParameterId == parameterId.Value).ToList();
                if (byId.Count == 1) { resolved = byId[0]; return null; }
                if (byId.Count == 0)
                    return "no field of this schedule has parameter id " + parameterId.Value + ". The fields are: " +
                           Roster(fields);
                return "parameter id " + parameterId.Value + " matches " + byId.Count + " fields (indices " +
                       string.Join(", ", byId.Select(f => f.Index)) + "); name the field_index instead.";
            }
            if (string.IsNullOrWhiteSpace(name))
                return "name a field by parameter_id or by name; neither was sent.";
            List<ScheduleFieldFacts> byName = fields
                .Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) { resolved = byName[0]; return null; }
            if (byName.Count == 0)
                return "no field of this schedule is named '" + name + "'. The fields are: " + Roster(fields);
            return "'" + name + "' names " + byName.Count + " fields of this schedule (parameter ids " +
                   string.Join(", ", byName.Select(f => f.ParameterId)) + "). Two columns can share a spelling - " +
                   "a shared parameter beside a built-in - and choosing one silently is how the wrong Comments " +
                   "column ships. Resolve by parameter_id.";
        }

        private static string Roster(IReadOnlyList<ScheduleFieldFacts> fields)
            => string.Join("; ", fields.Select(f =>
                   "[" + f.Index + "] '" + f.Name + "' (param " + f.ParameterId + ")"));

        // ---- the canonical definition snapshot ------------------------------------

        /// <summary>
        /// One schedule definition, rendered canonically: fields in order with their
        /// visibility and headings, then filters, then sorting, then the options. Two
        /// snapshots compare byte-for-byte, so "what changed" is a fact and "nothing
        /// changed" (an idempotent replay) is provable rather than asserted.
        /// </summary>
        public static string CanonicalDefinition(IEnumerable<ScheduleFieldFacts> fields,
                                                 IEnumerable<string> filterLines,
                                                 IEnumerable<string> sortLines,
                                                 bool itemized, bool grandTotal, bool headers)
        {
            var sb = new StringBuilder();
            sb.Append("fields:\n");
            if (fields != null)
                foreach (ScheduleFieldFacts f in fields)
                    sb.Append("  [").Append(f.Index).Append("] ").Append(f.Name ?? "")
                      .Append(" param=").Append(f.ParameterId)
                      .Append(" heading=").Append(f.Heading ?? "")
                      .Append(" hidden=").Append(f.Hidden ? "true" : "false")
                      .Append('\n');
            sb.Append("filters:\n");
            if (filterLines != null) foreach (string line in filterLines) sb.Append("  ").Append(line).Append('\n');
            sb.Append("sorting:\n");
            if (sortLines != null) foreach (string line in sortLines) sb.Append("  ").Append(line).Append('\n');
            sb.Append("itemized=").Append(itemized ? "true" : "false").Append('\n');
            sb.Append("grand_total=").Append(grandTotal ? "true" : "false").Append('\n');
            sb.Append("headers=").Append(headers ? "true" : "false").Append('\n');
            return sb.ToString();
        }

        /// <summary>SHA-256 of the canonical definition - the identity a before/after diff hangs off.</summary>
        public static string DefinitionFingerprint(string canonical)
            => RequestFingerprint.Sha256Hex(canonical ?? "");

        /// <summary>
        /// The human half of the diff: which SECTIONS differ between two canonical
        /// snapshots. Line-level noise is not the point; "filters changed, fields did
        /// not" is what a reviewer needs.
        /// </summary>
        public static List<string> ChangedSections(string before, string after)
        {
            var result = new List<string>();
            Dictionary<string, string> a = Sections(before), b = Sections(after);
            foreach (string key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                string left, right;
                a.TryGetValue(key, out left);
                b.TryGetValue(key, out right);
                if (!string.Equals(left ?? "", right ?? "", StringComparison.Ordinal)) result.Add(key);
            }
            return result;
        }

        private static Dictionary<string, string> Sections(string canonical)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (canonical == null) return result;
            string current = null;
            var sb = new StringBuilder();
            foreach (string raw in canonical.Split('\n'))
            {
                string line = raw;
                if (line.Length > 0 && !line.StartsWith("  ", StringComparison.Ordinal))
                {
                    if (current != null) result[current] = sb.ToString();
                    int colon = line.IndexOf(':');
                    int equals = line.IndexOf('=');
                    if (colon >= 0 && (equals < 0 || colon < equals))
                    {
                        current = line.Substring(0, colon);
                        sb = new StringBuilder();
                        continue;
                    }
                    // an option line: its own section
                    if (equals > 0)
                    {
                        result[line.Substring(0, equals)] = line.Substring(equals + 1);
                        current = null;
                        continue;
                    }
                }
                if (current != null) sb.Append(line).Append('\n');
            }
            if (current != null) result[current] = sb.ToString();
            return result;
        }

        // ---- misc validation -------------------------------------------------------

        public static string ValidateOperation(string operation)
        {
            if (KnownOperations.Contains(operation)) return null;
            return "operation '" + operation + "' is not one this command understands. Known: " +
                   string.Join(", ", KnownOperations) + ". (Plain category schedules are created by " +
                   "horizun_create_schedule, which owns the linked-elements option.)";
        }

        public static string ValidateCreateKind(string kind)
        {
            if (KnownCreateKinds.Contains(kind)) return null;
            return "kind '" + kind + "' is not one this command creates. Known: " +
                   string.Join(", ", KnownCreateKinds) + ". A plain category schedule is " +
                   "horizun_create_schedule's job.";
        }

        public static string ValidateSortDirection(string direction)
        {
            if (direction == "ascending" || direction == "descending") return null;
            return "sort direction must be ascending or descending; '" + direction + "' was sent.";
        }
    }
}
