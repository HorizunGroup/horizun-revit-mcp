// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// SHEETS AND ANNOTATIONS, audited from the model rather than from a PDF.
//
// Two sentences this file exists to refuse:
//
//   "the sheet is complete because it is not empty"
//   "the view is documented because it has a dimension"
//
// Both are the same mistake: treating the presence of one thing as evidence
// about a whole. Not-empty is a FACT with a count beside it; complete is a
// judgement, and it needs a rule somebody wrote. With no rules this area
// returns counts and nothing else, and none of those counts is a pass.
//
// A SCHEDULE ON A SHEET IS NOT A VIEWPORT. Revit places it as a
// ScheduleSheetInstance, so a sheet holding nothing but schedules has zero
// viewports - and a check that counts only viewports calls it empty. That sheet
// is the reason viewport_count and schedule_instance_count are separate numbers
// here and are never added together into "contents".
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class SheetFindingCodes
    {
        public const string NumberEmpty = "sheet_number_empty";
        public const string NumberDuplicate = "sheet_number_duplicate";
        public const string NameEmpty = "sheet_name_empty";
        public const string TitleBlockMissing = "title_block_missing";
        public const string TitleBlockMultiple = "title_block_multiple";
        public const string SheetEmpty = "sheet_empty";
        public const string TooFewViewports = "too_few_viewports";
        public const string TooManyViewports = "too_many_viewports";
        public const string RevisionMissing = "revision_missing";
    }

    public static class SheetRuleCodes
    {
        public const string NoVersion = "sheet_rules_no_version";
        public const string UnknownKey = "sheet_rules_unknown_key";
        public const string BadRule = "sheet_rules_bad_rule";
        public const string Absent = "sheet_rules_absent";
    }

    /// <summary>
    /// One sheet as the model reports it.
    ///
    /// Named SheetStateFact because Core/PlanimetryFacts already owns
    /// SheetFact for the planimetry row - the same collision ViewStateFact
    /// avoided. Two types of one name in one namespace is a build error
    /// today and a silent mis-read the day one of them moves.
    /// </summary>
    public sealed class SheetStateFact
    {
        public long ElementId;
        public string UniqueId;
        public string Number;
        public string Name;
        public bool NumberReadable = true;
        public bool NameReadable = true;

        public int TitleBlockCount;
        /// <summary>Views placed through a Viewport. NOT the sheet's contents.</summary>
        public int ViewportCount;
        /// <summary>Schedules placed on the sheet. These are NOT viewports.</summary>
        public int ScheduleInstanceCount;
        public int RevisionCount;
        public bool? IsPlaceholder;

        public HashSet<string> Unreadable = new HashSet<string>(StringComparer.Ordinal);

        public bool NumberEmpty { get { return NumberReadable && string.IsNullOrWhiteSpace(Number); } }
        public bool NameEmpty { get { return NameReadable && string.IsNullOrWhiteSpace(Name); } }

        /// <summary>
        /// Nothing placed at all - neither a view nor a schedule. A FACT, and the
        /// only thing it proves. Its opposite proves nothing whatsoever.
        /// </summary>
        public bool IsEmpty { get { return ViewportCount + ScheduleInstanceCount == 0; } }
    }

    public sealed class ViewportFact
    {
        public long ElementId;
        public long ViewId;
        public long SheetId;
        public string ViewName;
        public string SheetNumber;
        public string TypeName;
        public string DetailNumber;
        public double? X, Y;
        public int? Rotation;
        public bool LabelReadable = true;
        public HashSet<string> Unreadable = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Annotations in one view, counted by kind and kept apart by where they live.
    /// A dimension is view-specific; a door tag is view-specific; the door is not.
    /// Merging them produces a "documentation" count that grows when somebody
    /// models a wall.
    /// </summary>
    public sealed class AnnotationCensus
    {
        public long ViewId;
        public string ViewName;
        public string ViewType;
        public Dictionary<string, long> ByKind = new Dictionary<string, long>(StringComparer.Ordinal);
        public long Unreadable;

        public long Total { get { return ByKind.Values.Sum(); } }
    }

    public static class AnnotationKinds
    {
        public const string Dimensions = "dimensions";
        public const string Text = "text";
        public const string Tags = "tags";
        public const string GenericAnnotations = "generic_annotations";
        public const string DetailItems = "detail_items";
        public const string FilledRegions = "filled_regions";
        public const string MaskingRegions = "masking_regions";
        public const string RevisionClouds = "revision_clouds";
        public const string Callouts = "callouts";
        public const string Sections = "sections";
        public const string Elevations = "elevations";
        public const string Keynotes = "keynotes";

        public static readonly string[] All =
        {
            Dimensions, Text, Tags, GenericAnnotations, DetailItems, FilledRegions,
            MaskingRegions, RevisionClouds, Callouts, Sections, Elevations, Keynotes
        };
    }

    public sealed class SheetRules
    {
        public bool Ok;
        public bool Absent;
        public string Code;
        public string Message;
        public string Version;

        public bool? TitleBlockRequired;
        public bool? ForbidMultipleTitleBlocks;
        public bool? ForbidEmptySheets;
        public bool? ForbidDuplicateNumbers;
        public bool? RevisionsRequired;
        public int? MinViewports;
        public int? MaxViewports;
        // RequiredScheduleNames is GONE, not merely unassigned. It was declared, parsed
        // and never judged; leaving the field behind would invite the next reader to
        // believe a rule exists for it. The key is refused where it is parsed.
        /// <summary>Minimum annotations per view - ONLY when the caller asks for it.</summary>
        public Dictionary<string, long> MinAnnotationsByViewType =
            new Dictionary<string, long>(StringComparer.Ordinal);
        public HashSet<string> Exceptions = new HashSet<string>(StringComparer.Ordinal);
    }

    public sealed class SheetFinding
    {
        public string Code;
        public long SheetId;
        public string SheetNumber;
        public string Detail;
    }

    public static class SheetAnnotationRules
    {
        public const string EmptinessMeans =
            "is_empty means nothing is placed on the sheet - no viewport and no schedule. Its OPPOSITE proves " +
            "nothing: a sheet with one viewport on it is not a complete sheet, and this bridge will not say it " +
            "is without a rule that defines completeness. A schedule placed on a sheet is a " +
            "ScheduleSheetInstance and NOT a viewport, so a sheet of schedules has zero viewports and is not " +
            "empty; the two counts are kept apart for exactly that sheet.";

        public const string AnnotationMeans =
            "a count of annotations is not a measure of documentation. A view with one dimension is not a " +
            "documented view, and no minimum is applied unless the caller declares one - how much annotation a " +
            "section needs is one organisation's decision. Counts are per KIND because they are not " +
            "interchangeable, and view-specific annotation is never mixed with model elements.";

        // ------------------------------------------------------------- profile

        public static SheetRules Read(JToken token)
        {
            var r = new SheetRules();
            if (token == null || token.Type == JTokenType.Null)
            {
                r.Absent = true;
                r.Code = SheetRuleCodes.Absent;
                r.Message = "no sheet rules were supplied, so no sheet was judged. The counts below are facts; " +
                            "NONE of them is a pass, and a sheet that is not empty has not been called complete.";
                return r;
            }

            var o = token as JObject;
            if (o == null)
            {
                r.Code = SheetRuleCodes.BadRule;
                r.Message = "sheet_rules must be an object.";
                return r;
            }

            JToken v = o["version"];
            if (v == null || string.IsNullOrWhiteSpace(v.Value<string>()))
            {
                r.Code = SheetRuleCodes.NoVersion;
                r.Message = "sheet_rules needs a 'version'.";
                return r;
            }
            r.Version = v.Value<string>();

            foreach (JProperty p in o.Properties())
            {
                switch (p.Name)
                {
                    case "version": break;
                    case "title_block_required": r.TitleBlockRequired = B(p, r); break;
                    case "forbid_multiple_title_blocks": r.ForbidMultipleTitleBlocks = B(p, r); break;
                    case "forbid_empty_sheets": r.ForbidEmptySheets = B(p, r); break;
                    case "forbid_duplicate_numbers": r.ForbidDuplicateNumbers = B(p, r); break;
                    case "revisions_required": r.RevisionsRequired = B(p, r); break;
                    case "min_viewports": r.MinViewports = I(p, r); break;
                    case "max_viewports": r.MaxViewports = I(p, r); break;

                    // REFUSED, BECAUSE NOTHING EVER JUDGED IT.
                    //
                    // This key was parsed into RequiredScheduleNames and read by no one:
                    // Judge never looked at it, and the sheet facts carry only
                    // ScheduleInstanceCount - HOW MANY schedules sit on a sheet, never
                    // WHICH - so there was nothing to compare a required name against.
                    //
                    // A caller who sent required_schedule_names got a scan that reported no
                    // finding about it and concluded their sheets carried the schedules
                    // they require. That is a false clean produced by the diagnostic tool
                    // itself, on a key whose name is a REQUIREMENT: the caller was asking
                    // for enforcement and was given silence that reads like compliance.
                    //
                    // Refusing is the honest answer until the facts carry schedule names.
                    // It breaks no caller who was getting a real check, because there was
                    // never a real check to get, and it fails LOUDLY at the moment of the
                    // request rather than quietly inside a report.
                    case "required_schedule_names":
                        Bad(r, "'required_schedule_names' is NOT IMPLEMENTED and is refused rather than " +
                               "ignored. It was previously accepted and never evaluated: the sheet facts " +
                               "record how many schedules sit on a sheet, never which ones, so no required " +
                               "name was ever compared against anything and a scan reported nothing about " +
                               "it. Silence there reads as compliance, which is the one answer this scan " +
                               "must not give. Remove the key; check the schedules on your sheets with " +
                               "horizun_list_schedules or a schedule section until this is implemented.");
                        return r;

                    case "min_annotations_by_view_type":
                    {
                        var body = p.Value as JObject;
                        if (body == null) { Bad(r, "'min_annotations_by_view_type' must be an object."); return r; }
                        foreach (JProperty c in body.Properties())
                        {
                            if (c.Value.Type != JTokenType.Integer || c.Value.Value<long>() < 0)
                            { Bad(r, "the minimum for '" + c.Name + "' must be zero or more."); return r; }
                            r.MinAnnotationsByViewType[c.Name] = c.Value.Value<long>();
                        }
                        break;
                    }

                    case "exceptions":
                    {
                        var arr = p.Value as JArray;
                        if (arr == null) { Bad(r, "'exceptions' must be an array of sheet numbers."); return r; }
                        foreach (JToken t in arr) r.Exceptions.Add(t.Value<string>());
                        break;
                    }

                    default:
                        r.Code = SheetRuleCodes.UnknownKey;
                        r.Message = "'" + p.Name + "' is not a key sheet_rules defines. Known keys: version, " +
                                    "title_block_required, forbid_multiple_title_blocks, forbid_empty_sheets, " +
                                    "forbid_duplicate_numbers, revisions_required, min_viewports, " +
                                    "max_viewports, min_annotations_by_view_type, " +
                                    "exceptions.";
                        return r;
                }
                if (r.Code != null) return r;
            }

            if (r.MinViewports.HasValue && r.MaxViewports.HasValue && r.MinViewports > r.MaxViewports)
            {
                Bad(r, "min_viewports is above max_viewports, so nothing can satisfy it.");
                return r;
            }

            r.Ok = true;
            return r;
        }

        private static void Bad(SheetRules r, string m) { r.Code = SheetRuleCodes.BadRule; r.Message = m; }

        private static bool? B(JProperty p, SheetRules r)
        {
            if (p.Value.Type != JTokenType.Boolean) { Bad(r, "'" + p.Name + "' must be true or false."); return null; }
            return p.Value.Value<bool>();
        }

        private static int? I(JProperty p, SheetRules r)
        {
            if (p.Value.Type != JTokenType.Integer || p.Value.Value<long>() < 0)
            { Bad(r, "'" + p.Name + "' must be a whole number of zero or more."); return null; }
            return p.Value.Value<int>();
        }

        // ------------------------------------------------------------ findings

        /// <summary>
        /// Sheet numbers that more than one sheet carries. A FACT about the
        /// population, computed whether or not a rule asks about it - two sheets
        /// with one number is not a matter of opinion. Whether it is a FINDING is.
        /// </summary>
        public static List<string> DuplicateNumbers(IEnumerable<SheetStateFact> sheets)
        {
            return (sheets ?? Enumerable.Empty<SheetStateFact>())
                .Where(s => s != null && s.NumberReadable && !string.IsNullOrWhiteSpace(s.Number))
                .GroupBy(s => s.Number, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }

        public static List<SheetFinding> Judge(IEnumerable<SheetStateFact> sheets, SheetRules r)
        {
            var findings = new List<SheetFinding>();
            if (sheets == null || r == null || !r.Ok) return findings;

            List<SheetStateFact> all = sheets.Where(s => s != null).ToList();
            var duplicates = new HashSet<string>(DuplicateNumbers(all), StringComparer.Ordinal);

            foreach (SheetStateFact s in all)
            {
                if (s.Number != null && r.Exceptions.Contains(s.Number)) continue;

                if (s.NumberEmpty)
                    findings.Add(F(SheetFindingCodes.NumberEmpty, s, "this sheet has no number."));
                if (s.NameEmpty)
                    findings.Add(F(SheetFindingCodes.NameEmpty, s, "this sheet has no name."));

                if (r.ForbidDuplicateNumbers == true && s.Number != null && duplicates.Contains(s.Number))
                    findings.Add(F(SheetFindingCodes.NumberDuplicate, s,
                        "more than one sheet carries the number '" + s.Number + "'."));

                // TitleBlockCount is only meaningful when it could be read at all.
                if (!s.Unreadable.Contains("title_blocks"))
                {
                    if (r.TitleBlockRequired == true && s.TitleBlockCount == 0)
                        findings.Add(F(SheetFindingCodes.TitleBlockMissing, s, "no title block is placed."));
                    if (r.ForbidMultipleTitleBlocks == true && s.TitleBlockCount > 1)
                        findings.Add(F(SheetFindingCodes.TitleBlockMultiple, s,
                            s.TitleBlockCount + " title blocks are placed on one sheet."));
                }

                if (r.ForbidEmptySheets == true && s.IsEmpty)
                    findings.Add(F(SheetFindingCodes.SheetEmpty, s,
                        "nothing is placed on this sheet - no viewport and no schedule."));

                if (r.MinViewports.HasValue && s.ViewportCount < r.MinViewports.Value)
                    findings.Add(F(SheetFindingCodes.TooFewViewports, s,
                        s.ViewportCount + " viewport(s), fewer than the " + r.MinViewports.Value +
                        " you require. Schedules are counted separately and are not viewports."));

                if (r.MaxViewports.HasValue && s.ViewportCount > r.MaxViewports.Value)
                    findings.Add(F(SheetFindingCodes.TooManyViewports, s,
                        s.ViewportCount + " viewport(s), more than the " + r.MaxViewports.Value + " you allow."));

                if (r.RevisionsRequired == true && s.RevisionCount == 0)
                    findings.Add(F(SheetFindingCodes.RevisionMissing, s, "no revision is recorded on this sheet."));
            }
            return findings;
        }

        private static SheetFinding F(string code, SheetStateFact s, string detail)
        {
            return new SheetFinding
            {
                Code = code,
                SheetId = s.ElementId,
                SheetNumber = s.Number,
                Detail = detail
            };
        }

        /// <summary>
        /// Views whose annotation count is below a minimum THE CALLER DECLARED. With
        /// no declaration this returns nothing - not because every view passed, but
        /// because nobody said what enough would be.
        /// </summary>
        public static List<AnnotationCensus> BelowMinimum(IEnumerable<AnnotationCensus> views, SheetRules r)
        {
            var below = new List<AnnotationCensus>();
            if (views == null || r == null || !r.Ok || r.MinAnnotationsByViewType.Count == 0) return below;
            foreach (AnnotationCensus v in views)
            {
                if (v == null || v.ViewType == null) continue;
                long min;
                if (!r.MinAnnotationsByViewType.TryGetValue(v.ViewType, out min)) continue;
                if (v.Total < min) below.Add(v);
            }
            return below;
        }

        public static JObject ToJson(SheetStateFact s)
        {
            if (s == null) return null;
            return new JObject
            {
                ["sheet_id"] = s.ElementId,
                ["unique_id"] = s.UniqueId,
                ["number"] = s.Number,
                ["number_readable"] = s.NumberReadable,
                ["name"] = s.Name,
                ["name_readable"] = s.NameReadable,
                ["is_placeholder"] = s.IsPlaceholder,
                ["title_block_count"] = s.Unreadable.Contains("title_blocks") ? null : (JToken)s.TitleBlockCount,
                ["viewport_count"] = s.ViewportCount,
                ["schedule_instance_count"] = s.ScheduleInstanceCount,
                ["revision_count"] = s.RevisionCount,
                ["is_empty"] = s.IsEmpty,
                ["unreadable_properties"] = new JArray(
                    s.Unreadable.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JToken)x))
            };
        }

        public static JObject ToJson(AnnotationCensus c)
        {
            if (c == null) return null;
            var kinds = new JObject();
            foreach (string k in AnnotationKinds.All)
            {
                long n;
                kinds[k] = c.ByKind.TryGetValue(k, out n) ? n : 0;
            }
            return new JObject
            {
                ["view_id"] = c.ViewId,
                ["view_name"] = c.ViewName,
                ["view_type"] = c.ViewType,
                ["total"] = c.Total,
                ["unreadable"] = c.Unreadable,
                ["by_kind"] = kinds
            };
        }
    }
}
