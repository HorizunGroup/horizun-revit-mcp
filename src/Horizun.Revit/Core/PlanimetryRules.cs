// -----------------------------------------------------------------------------
// Horizun Revit MCP - what the planimetry auditor CONCLUDES, with no Revit in it.
//
// Two halves, and the line between them is the whole design:
//
//   UNIVERSAL rules are the ones that are true without a company's standard. A
//   viewport that overlaps another viewport is wrong on any sheet in any office
//   in any country. A tag pointing at nothing is broken. A dimension whose
//   references Revit will not resolve is not measuring anything. Those are
//   findings this bridge is entitled to make on its own.
//
//   Everything with a NUMBER or a NAME in it - the margin, the allowed scales,
//   the sheet-number format, which categories must be tagged - is a standard,
//   and a standard arrives as an argument. AGENTS.md: no company's catalogues
//   are compiled in.
//
// Three rules run through every check below:
//
//   1. AN UNREADABLE FACT IS `unknown`, NEVER A PASS. Every check that depends
//      on something Revit would not answer emits an `unknown` finding for that
//      element instead of quietly leaving it out. A check with unknowns is
//      reported as unknown, not passed.
//   2. NO SCORE. There is no 0-100 anywhere in this file. The findings are the
//      deliverable; a single number invites the reader to stop reading.
//   3. GEOMETRY IS ONLY EVIDENCE WHEN IT WAS MEASURED. "Outside the crop" is a
//      finding only when the crop is active AND its shape was read AND the
//      element's box was read. Otherwise the situation is unknown, and it says
//      so, because a documentation defect asserted from geometry nobody could
//      read is worse than no finding at all.
//
// Pure, so every one of these decisions is an ordinary unit test.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One thing the auditor found, or could not determine.</summary>
    public sealed class PlanimetryFinding
    {
        public string RuleId;
        public string RequirementSetId;
        public string RequirementSetVersion;
        public string RequirementSetSha256;

        /// <summary>blocking | advisory | unknown.</summary>
        public string Severity;

        /// <summary>failed | unknown | passed.</summary>
        public string Status;

        public string EntityKind;
        public long? SheetId;
        public string SheetNumber;
        public long? ViewId;
        public List<long> ElementIds = new List<long>();

        public JObject Observed = new JObject();
        public JObject Expected = new JObject();

        public string CoordinateSystem;
        public string Units;
        public double[] Point;

        public bool Fixable;
        public string RecommendedTool;
        public bool CoverageComplete = true;
        public JObject Evidence = new JObject();

        public JObject ToJson()
        {
            return new JObject
            {
                ["rule_id"] = RuleId,
                ["requirement_set"] = RequirementSetId,
                ["requirement_set_version"] = RequirementSetVersion,
                ["requirement_set_sha256"] = RequirementSetSha256,
                ["severity"] = Severity,
                ["status"] = Status,
                ["entity_kind"] = EntityKind,
                ["sheet_id"] = SheetId.HasValue ? (JToken)SheetId.Value : JValue.CreateNull(),
                ["sheet_number"] = SheetNumber,
                ["view_id"] = ViewId.HasValue ? (JToken)ViewId.Value : JValue.CreateNull(),
                ["element_ids"] = new JArray(ElementIds.Select(i => (JToken)i)),
                ["observed"] = Observed,
                ["expected"] = Expected,
                ["location"] = Point == null ? (JToken)JValue.CreateNull() : new JObject
                {
                    ["coordinate_system"] = CoordinateSystem,
                    ["units"] = Units,
                    ["point"] = new JArray(Point.Select(v => (JToken)v))
                },
                ["fixable"] = Fixable,
                ["recommended_tool"] = RecommendedTool,
                ["coverage_complete"] = CoverageComplete,
                ["evidence"] = Evidence
            };
        }

        /// <summary>
        /// THE order, exactly as published: severity, then rule id, then sheet number, then
        /// view id, then element id. Deterministic to the last tiebreak, because two runs
        /// over an unchanged model must page identically or a cursor means nothing.
        /// </summary>
        public static int Compare(PlanimetryFinding a, PlanimetryFinding b)
        {
            int c = SeverityRank(a.Severity).CompareTo(SeverityRank(b.Severity));
            if (c != 0) return c;
            c = string.CompareOrdinal(a.RuleId ?? "", b.RuleId ?? "");
            if (c != 0) return c;
            c = string.CompareOrdinal(a.SheetNumber ?? "￿", b.SheetNumber ?? "￿");
            if (c != 0) return c;
            c = (a.ViewId ?? long.MaxValue).CompareTo(b.ViewId ?? long.MaxValue);
            if (c != 0) return c;
            long ae = a.ElementIds.Count > 0 ? a.ElementIds.Min() : long.MaxValue;
            long be = b.ElementIds.Count > 0 ? b.ElementIds.Min() : long.MaxValue;
            c = ae.CompareTo(be);
            if (c != 0) return c;
            // Last resort so two findings that are equal on every published key still have
            // ONE order: the whole element list, then the status.
            c = string.CompareOrdinal(string.Join(",", a.ElementIds), string.Join(",", b.ElementIds));
            if (c != 0) return c;
            return string.CompareOrdinal(a.Status ?? "", b.Status ?? "");
        }

        private static int SeverityRank(string s)
        {
            switch (s)
            {
                case "blocking": return 0;
                case "advisory": return 1;
                default: return 2;   // unknown
            }
        }

        /// <summary>A stable text form of one finding, for the result-set fingerprint.</summary>
        public string Signature()
        {
            return string.Join("|", new[]
            {
                RuleId, RequirementSetId, RequirementSetVersion, Severity, Status, EntityKind,
                SheetId?.ToString(CultureInfo.InvariantCulture) ?? "-",
                ViewId?.ToString(CultureInfo.InvariantCulture) ?? "-",
                string.Join(",", ElementIds),
                RequestFingerprint.Canonical(Observed),
                RequestFingerprint.Canonical(Expected)
            });
        }
    }

    /// <summary>One check that was evaluated, and over how much.</summary>
    public sealed class PlanimetryCheckRun
    {
        public string RuleId;
        public string Severity;
        public string Entity;
        public int Population;
        public int Findings;
        public int Unknowns;

        /// <summary>passed | failed | unknown | not_applicable. `passed` requires a non-empty
        /// population AND no unknowns: a check that examined nothing has not passed.</summary>
        public string Status;
        public string Description;

        public JObject ToJson()
        {
            return new JObject
            {
                ["rule_id"] = RuleId,
                ["severity"] = Severity,
                ["entity"] = Entity,
                ["population"] = Population,
                ["findings"] = Findings,
                ["unknowns"] = Unknowns,
                ["status"] = Status,
                ["description"] = Description
            };
        }
    }

    /// <summary>One universal check's identity, fixed so a report can be diffed over time.</summary>
    public sealed class PlanimetryCheck
    {
        public string Id;
        public string Severity;
        public string Entity;
        public string Description;
        public string RecommendedTool;

        public PlanimetryCheck(string id, string severity, string entity, string description,
                               string recommendedTool = null)
        {
            Id = id; Severity = severity; Entity = entity; Description = description;
            RecommendedTool = recommendedTool;
        }
    }

    public sealed class PlanimetryRuleOptions
    {
        public string Units = "mm";
        public double ScaleFromFeet = 304.8;
        public double ToleranceFeet = PlanimetryGeometry.TouchToleranceFeet;
        public bool IncludeAdvisory = true;
        public bool IncludePassedChecks;
    }

    public sealed class PlanimetryAuditResult
    {
        public List<PlanimetryFinding> Findings = new List<PlanimetryFinding>();
        public List<PlanimetryCheckRun> Checks = new List<PlanimetryCheckRun>();
        public List<PlanimetryCheckFailure> ChecksFailed = new List<PlanimetryCheckFailure>();
    }

    public static class PlanimetryRules
    {
        /// <summary>
        /// The universal set's identity. It is a requirement set like any other - it just
        /// happens to be the one this bridge is entitled to hold, because every rule in it
        /// is true without a company's standard. Findings cite it by id and version exactly
        /// as they cite an external set.
        /// </summary>
        public const string UniversalId = "horizun-universal-planimetry";
        public const string UniversalVersion = "1.0.0";

        // ---------------------------------------------------------------------
        // THE CATALOG. Every universal check, once. `checks_run` is derived from
        // this, so a check cannot exist without being counted or be counted
        // without existing.
        // ---------------------------------------------------------------------
        public static readonly PlanimetryCheck[] Catalog =
        {
            // ---- sheets and placements ----
            new PlanimetryCheck("sheet.no-titleblock", "blocking", "sheet",
                "A non-placeholder sheet carries no title-block instance. It cannot be issued: there is nothing on it that says what it is."),
            new PlanimetryCheck("sheet.multiple-titleblocks", "blocking", "sheet",
                "A sheet carries more than one title block. Two title blocks means two answers to the same question - number, revision, scale."),
            new PlanimetryCheck("sheet.unreadable", "unknown", "sheet",
                "The sheet itself could not be interrogated. Its contents are unknown, not clean."),
            new PlanimetryCheck("sheet.extent-unreadable", "unknown", "sheet",
                "Neither the title block's extent nor the sheet outline could be read, so no placement on this sheet can be measured against it."),
            new PlanimetryCheck("placement.bounds-unreadable", "unknown", "placement",
                "A placement's outline could not be read, so it is in no overlap or containment answer on its sheet."),
            new PlanimetryCheck("sheet.viewport-overlap", "blocking", "placement",
                "Two viewports share area on the same sheet. Touching edges are NOT an overlap; this is measured beyond an explicit tolerance on both axes."),
            new PlanimetryCheck("sheet.viewport-schedule-overlap", "blocking", "placement",
                "A viewport and a placed schedule share area on the same sheet."),
            new PlanimetryCheck("sheet.schedule-overlap", "blocking", "placement",
                "Two placed schedules share area on the same sheet."),
            new PlanimetryCheck("sheet.placement-outside-extent", "blocking", "placement",
                "A placement lies entirely outside the sheet's own extent - it is on the sheet in the database and on no plot."),
            new PlanimetryCheck("viewport.view-missing", "blocking", "placement",
                "A viewport names a view that is not in this document.", "horizun_manage_views"),
            new PlanimetryCheck("schedule-placement.target-missing", "blocking", "placement",
                "A schedule placement names a schedule that is not in this document.", "horizun_manage_views"),

            // ---- views ----
            new PlanimetryCheck("view.placed-on-multiple-sheets", "blocking", "view",
                "A view is held by more than one viewport. Revit places a view on ONE sheet; more than one is a contradiction in the model."),
            new PlanimetryCheck("view.template-unreadable", "unknown", "view",
                "The view's template could not be read, so nothing that depends on the template is known for it."),
            new PlanimetryCheck("view.crop-geometry-unreadable", "unknown", "view",
                "The crop is active but its geometry could not be read, so no 'inside/outside the crop' answer is available for this view."),
            new PlanimetryCheck("view.parent-view-missing", "blocking", "view",
                "A dependent or callout view names a parent that is not in this document."),
            new PlanimetryCheck("view.unclassifiable", "unknown", "view",
                "The view's own type could not be read, so it was examined by no view rule."),
            new PlanimetryCheck("view.no-template", "advisory", "view",
                "A view carries no view template. Working views legitimately do not; this is a list to review before issue, not a universal defect."),
            new PlanimetryCheck("view.not-placed", "advisory", "view",
                "A graphical, printable view is on no sheet. Working views legitimately are not; this is a list to review, not a universal defect."),

            // ---- dimensions ----
            new PlanimetryCheck("dimension.references-unavailable", "blocking", "dimension",
                "Revit reports AreReferencesAvailable=false: the dimension is not measuring what it claims to.", "horizun_annotate"),
            new PlanimetryCheck("dimension.broken-reference", "blocking", "dimension",
                "A dimension references an element that no longer exists. References into RVT links are NOT counted here.", "horizun_annotate"),
            new PlanimetryCheck("dimension.reference-unreadable", "unknown", "dimension",
                "A dimension's references could not be read, so whether it still measures anything is unknown."),
            new PlanimetryCheck("dimension.value-override", "advisory", "dimension",
                "A dimension displays text instead of its measured value. This is sometimes deliberate; a requirement set with forbid_numeric_override makes it blocking.", "horizun_edit_dimensions"),
            new PlanimetryCheck("dimension.no-owner-view", "advisory", "dimension",
                "A non-view-specific Dimension is a model CONSTRAINT (a locked alignment, sketch EQ), not annotation. Listed so a reader can tell the two apart."),
            new PlanimetryCheck("dimension.type-unreadable", "unknown", "dimension",
                "The dimension's type could not be read."),
            new PlanimetryCheck("dimension.geometry-unreadable", "unknown", "dimension",
                "The dimension's geometry could not be read, so it is in no crop or layout answer."),
            new PlanimetryCheck("dimension.outside-annotation-crop", "blocking", "dimension",
                "A dimension lies entirely outside its view's ACTIVE annotation crop: it exists and does not print."),

            // ---- tags ----
            new PlanimetryCheck("tag.orphaned", "blocking", "tag",
                "Revit reports the tag as orphaned: it labels nothing.", "horizun_delete_verified"),
            new PlanimetryCheck("tag.target-unreadable", "unknown", "tag",
                "A tag's targets could not be read, so whether it labels anything is unknown."),
            new PlanimetryCheck("tag.linked-target-not-inspected", "unknown", "tag",
                "A tag labels an element inside a Revit LINK. Not inspected is not broken - this is reported so the two are never confused."),
            new PlanimetryCheck("tag.duplicate", "advisory", "tag",
                "Two or more tags of the same type label the same element set in the same view. Each one works; together they are redundant."),
            new PlanimetryCheck("tag.no-owner-view", "blocking", "tag",
                "A tag has no owner view. A tag is view-specific by definition."),
            new PlanimetryCheck("tag.outside-annotation-crop", "blocking", "tag",
                "A tag lies entirely outside its view's ACTIVE annotation crop: it exists and does not print."),

            // ---- text ----
            new PlanimetryCheck("text.empty", "blocking", "text_note",
                "A text note is empty or contains only whitespace. It occupies the model and says nothing.", "horizun_delete_verified"),
            new PlanimetryCheck("text.no-owner-view", "blocking", "text_note",
                "A text note has no owner view."),
            new PlanimetryCheck("text.bounds-unreadable", "unknown", "text_note",
                "A text note's bounds could not be read, so it is in no crop or layout answer."),
            new PlanimetryCheck("text.outside-annotation-crop", "blocking", "text_note",
                "A text note lies entirely outside its view's ACTIVE annotation crop: it exists and does not print."),

            // ---- 2D detail ----
            new PlanimetryCheck("detail_2d.no-owner-view", "blocking", "detail_2d",
                "A view-specific detail element has no owner view."),
            new PlanimetryCheck("detail_2d.owner-view-missing", "blocking", "detail_2d",
                "A detail element names an owner view that is not in this document."),
            new PlanimetryCheck("detail_2d.geometry-unreadable", "unknown", "detail_2d",
                "A detail element's geometry could not be read."),
            new PlanimetryCheck("detail_2d.degenerate-curve", "blocking", "detail_2d",
                "A detail curve has effectively zero length. It draws nothing and is selectable by nobody.", "horizun_delete_verified"),
            new PlanimetryCheck("detail_2d.region-read-incomplete", "unknown", "detail_2d",
                "A filled region's boundary could not be read completely."),
            new PlanimetryCheck("detail_2d.outside-crop", "blocking", "detail_2d",
                "A detail element lies entirely outside its view's ACTIVE crop region: it exists and does not print."),

            // ---- references between views ----
            new PlanimetryCheck("reference.target-missing", "blocking", "view_reference",
                "A section, elevation, callout or view reference names a target view that is not in this document."),
            new PlanimetryCheck("reference.target-unreadable", "unknown", "view_reference",
                "A reference's target could not be read."),
            new PlanimetryCheck("reference.target-unidentifiable", "unknown", "view_reference",
                "The API exposes no relation from this marker to a target view. Reported as unknown and NEVER inferred from a name."),
            new PlanimetryCheck("reference.target-not-placed", "advisory", "view_reference",
                "A referenced view is on no sheet, so the reference points at nothing a reader can turn to. Mid-project this is normal.")
        };

        public static PlanimetryCheck Check(string id)
        {
            return Catalog.FirstOrDefault(c => c.Id == id);
        }

        // =====================================================================
        // THE UNIVERSAL PASS
        // =====================================================================
        public static PlanimetryAuditResult EvaluateUniversal(PlanimetrySnapshot snap, PlanimetryRuleOptions opt)
        {
            var result = new PlanimetryAuditResult();
            var tally = new Dictionary<string, PlanimetryCheckRun>(StringComparer.Ordinal);
            foreach (PlanimetryCheck c in Catalog)
                tally[c.Id] = new PlanimetryCheckRun
                {
                    RuleId = c.Id,
                    Severity = c.Severity,
                    Entity = c.Entity,
                    Description = c.Description,
                    Status = "not_applicable"
                };

            List<AnnotationFact> dimensions = snap.Annotations.Where(a => a.Kind == "dimension").ToList();
            List<AnnotationFact> tags = snap.Annotations.Where(a => a.Kind == "tag" || a.Kind == "revision_tag").ToList();
            List<AnnotationFact> texts = snap.Annotations.Where(a => a.Kind == "text_note").ToList();
            List<AnnotationFact> detail = snap.Annotations.Where(a => IsDetailKind(a.Kind)).ToList();

            Population(tally, snap.Sheets.Count, "sheet.no-titleblock", "sheet.multiple-titleblocks",
                       "sheet.unreadable", "sheet.extent-unreadable");
            Population(tally, snap.Placements.Count, "placement.bounds-unreadable", "sheet.viewport-overlap",
                       "sheet.viewport-schedule-overlap", "sheet.schedule-overlap",
                       "sheet.placement-outside-extent", "viewport.view-missing",
                       "schedule-placement.target-missing");
            Population(tally, snap.Views.Count, "view.placed-on-multiple-sheets", "view.template-unreadable",
                       "view.crop-geometry-unreadable", "view.parent-view-missing", "view.unclassifiable",
                       "view.no-template", "view.not-placed");
            Population(tally, dimensions.Count, "dimension.references-unavailable", "dimension.broken-reference",
                       "dimension.reference-unreadable", "dimension.value-override", "dimension.no-owner-view",
                       "dimension.type-unreadable", "dimension.geometry-unreadable",
                       "dimension.outside-annotation-crop");
            Population(tally, tags.Count, "tag.orphaned", "tag.target-unreadable", "tag.linked-target-not-inspected",
                       "tag.duplicate", "tag.no-owner-view", "tag.outside-annotation-crop");
            Population(tally, texts.Count, "text.empty", "text.no-owner-view", "text.bounds-unreadable",
                       "text.outside-annotation-crop");
            Population(tally, detail.Count, "detail_2d.no-owner-view", "detail_2d.owner-view-missing",
                       "detail_2d.geometry-unreadable", "detail_2d.degenerate-curve",
                       "detail_2d.region-read-incomplete", "detail_2d.outside-crop");
            Population(tally, snap.References.Count, "reference.target-missing", "reference.target-unreadable",
                       "reference.target-unidentifiable", "reference.target-not-placed");

            Sheets(snap, opt, result);
            Placements(snap, opt, result);
            Views(snap, opt, result);
            Dimensions(snap, dimensions, opt, result);
            Tags(snap, tags, opt, result);
            Texts(snap, texts, opt, result);
            Detail2D(snap, detail, opt, result);
            References(snap, opt, result);

            Finish(result, tally, snap, opt);
            return result;
        }

        private static bool IsDetailKind(string kind)
        {
            return kind == "detail_curve" || kind == "filled_region" ||
                   kind == "detail_component" || kind == "generic_annotation";
        }

        private static void Population(Dictionary<string, PlanimetryCheckRun> tally, int n, params string[] ids)
        {
            foreach (string id in ids) tally[id].Population = n;
        }

        /// <summary>
        /// Fold the findings into the check tally, drop advisories the caller did not want,
        /// add the passed entries when asked, and sort. One place, so a check's status and
        /// the findings a reader sees can never disagree.
        /// </summary>
        private static void Finish(PlanimetryAuditResult result, Dictionary<string, PlanimetryCheckRun> tally,
                                   PlanimetrySnapshot snap, PlanimetryRuleOptions opt)
        {
            if (!opt.IncludeAdvisory)
                result.Findings = result.Findings.Where(f => f.Severity != "advisory").ToList();

            foreach (PlanimetryFinding f in result.Findings)
            {
                PlanimetryCheckRun run;
                if (!tally.TryGetValue(f.RuleId, out run)) continue;
                if (f.Status == "unknown") run.Unknowns++;
                else run.Findings++;
            }

            foreach (PlanimetryCheckRun run in tally.Values)
            {
                if (run.Findings > 0) run.Status = "failed";
                else if (run.Unknowns > 0) run.Status = "unknown";
                else if (run.Population > 0) run.Status = "passed";
                else run.Status = "not_applicable";
            }

            result.Checks = tally.Values.OrderBy(c => c.RuleId, StringComparer.Ordinal).ToList();

            if (opt.IncludePassedChecks)
            {
                foreach (PlanimetryCheckRun run in result.Checks.Where(c => c.Status == "passed"))
                {
                    if (!opt.IncludeAdvisory && run.Severity == "advisory") continue;
                    result.Findings.Add(new PlanimetryFinding
                    {
                        RuleId = run.RuleId,
                        RequirementSetId = UniversalId,
                        RequirementSetVersion = UniversalVersion,
                        Severity = run.Severity,
                        Status = "passed",
                        EntityKind = run.Entity,
                        CoverageComplete = snap.CoverageComplete,
                        Observed = new JObject { ["population_examined"] = run.Population, ["findings"] = 0 },
                        Evidence = new JObject { ["description"] = run.Description }
                    });
                }
            }

            result.Findings.Sort(PlanimetryFinding.Compare);
        }

        // ---------------------------------------------------------------------
        // SHEETS
        // ---------------------------------------------------------------------
        private static void Sheets(PlanimetrySnapshot snap, PlanimetryRuleOptions opt, PlanimetryAuditResult r)
        {
            foreach (SheetFact s in snap.Sheets)
            {
                if (!s.Readable)
                {
                    r.Findings.Add(Unknown(snap, "sheet.unreadable", "sheet", s.Id, s.Id, s.SheetNumber, null,
                        new JObject { ["reason"] = Reasons(s) }));
                    continue;
                }

                if (!s.TitleblocksReadable)
                {
                    r.Findings.Add(Unknown(snap, "sheet.unreadable", "sheet", s.Id, s.Id, s.SheetNumber, null,
                        new JObject { ["reason"] = "the sheet's title-block instances could not be enumerated" }));
                }
                else if (s.IsPlaceholder != true)
                {
                    if (s.TitleblockIds.Count == 0)
                        r.Findings.Add(Fail(snap, "sheet.no-titleblock", "sheet", s.Id, s.Id, s.SheetNumber, null,
                            new JObject { ["titleblock_count"] = 0 },
                            new JObject { ["titleblock_count"] = 1 }));
                    else if (s.TitleblockIds.Count > 1)
                    {
                        var many = Fail(snap, "sheet.multiple-titleblocks", "sheet", s.Id, s.Id, s.SheetNumber, null,
                            new JObject { ["titleblock_count"] = s.TitleblockIds.Count },
                            new JObject { ["titleblock_count"] = 1 });
                        // The sheet AND every title block on it: the reader has to be able to
                        // delete the right one, and "one of these is extra" needs the list.
                        many.ElementIds = new List<long> { s.Id };
                        many.ElementIds.AddRange(s.TitleblockIds.OrderBy(i => i));
                        r.Findings.Add(many);
                    }
                }

                if (!s.Extent.Valid && (s.ViewportIds.Count > 0 || s.SchedulePlacementIds.Count > 0))
                    r.Findings.Add(Unknown(snap, "sheet.extent-unreadable", "sheet", s.Id, s.Id, s.SheetNumber, null,
                        new JObject
                        {
                            ["reason"] = "neither the title-block extent nor the sheet outline could be read",
                            ["placements_not_measurable"] = s.ViewportIds.Count + s.SchedulePlacementIds.Count
                        }));
            }
        }

        // ---------------------------------------------------------------------
        // PLACEMENTS - the layout arithmetic
        // ---------------------------------------------------------------------
        private static void Placements(PlanimetrySnapshot snap, PlanimetryRuleOptions opt, PlanimetryAuditResult r)
        {
            foreach (PlacementFact p in snap.Placements)
            {
                if (!p.BoundsReadable || !p.Extent.Valid)
                    r.Findings.Add(Unknown(snap, "placement.bounds-unreadable", "placement", p.Id, p.SheetId,
                        p.SheetNumber, p.ViewId,
                        new JObject { ["class"] = p.Class, ["reason"] = Reasons(p) }));

                if (p.Class == "viewport" && p.TargetExists == false)
                    r.Findings.Add(Fail(snap, "viewport.view-missing", "viewport", p.Id, p.SheetId, p.SheetNumber,
                        null, new JObject { ["view_id"] = p.ViewId.HasValue ? (JToken)p.ViewId.Value : JValue.CreateNull() },
                        new JObject { ["view_exists"] = true }));
                if (p.Class == "schedule_placement" && p.TargetExists == false)
                    r.Findings.Add(Fail(snap, "schedule-placement.target-missing", "schedule_placement", p.Id,
                        p.SheetId, p.SheetNumber, null,
                        new JObject { ["schedule_id"] = p.ScheduleId.HasValue ? (JToken)p.ScheduleId.Value : JValue.CreateNull() },
                        new JObject { ["schedule_exists"] = true }));
            }

            foreach (IGrouping<long, PlacementFact> onSheet in snap.Placements.GroupBy(p => p.SheetId))
            {
                SheetFact sheet = snap.SheetById(onSheet.Key);
                List<PlacementFact> list = onSheet.OrderBy(p => p.Id).ToList();

                // Outside the sheet entirely - only answerable when BOTH extents were read.
                if (sheet != null && sheet.Extent.Valid)
                {
                    foreach (PlacementFact p in list)
                    {
                        if (!p.Extent.Valid) continue;
                        if (!PlanimetryGeometry.Disjoint(sheet.Extent, p.Extent, opt.ToleranceFeet)) continue;
                        r.Findings.Add(Fail(snap, "sheet.placement-outside-extent", "placement", p.Id, p.SheetId,
                            p.SheetNumber, p.ViewId,
                            new JObject
                            {
                                ["class"] = p.Class,
                                ["placement_extent"] = Box(p.Extent, opt),
                                ["sheet_extent"] = Box(sheet.Extent, opt),
                                ["sheet_extent_source"] = sheet.ExtentSource
                            },
                            new JObject { ["inside_sheet_extent"] = true },
                            Point(p.Extent, opt), opt));
                    }
                }

                // Pairwise overlap. Ordered by id on both loops so the pair, the finding and
                // the page it lands on are the same on every run.
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        PlacementFact a = list[i], b = list[j];
                        if (!a.Extent.Valid || !b.Extent.Valid) continue;   // already reported unknown
                        if (!PlanimetryGeometry.Overlaps(a.Extent, b.Extent, opt.ToleranceFeet)) continue;

                        string rule = a.Class == "viewport" && b.Class == "viewport" ? "sheet.viewport-overlap"
                                    : a.Class == "schedule_placement" && b.Class == "schedule_placement"
                                        ? "sheet.schedule-overlap"
                                        : "sheet.viewport-schedule-overlap";

                        double ox = PlanimetryGeometry.OverlapX(a.Extent, b.Extent);
                        double oy = PlanimetryGeometry.OverlapY(a.Extent, b.Extent);
                        var f = Fail(snap, rule, "viewport", a.Id, a.SheetId, a.SheetNumber, a.ViewId,
                            new JObject
                            {
                                ["overlap_x"] = PlanimetryGeometry.Display(ox, opt.ScaleFromFeet),
                                ["overlap_y"] = PlanimetryGeometry.Display(oy, opt.ScaleFromFeet),
                                ["overlap_area"] = PlanimetryGeometry.Display(
                                    PlanimetryGeometry.OverlapArea(a.Extent, b.Extent), opt.ScaleFromFeet * opt.ScaleFromFeet),
                                ["units"] = opt.Units,
                                ["a"] = Placement(a, opt),
                                ["b"] = Placement(b, opt)
                            },
                            new JObject
                            {
                                ["overlap_area"] = 0,
                                ["tolerance"] = PlanimetryGeometry.Display(opt.ToleranceFeet, opt.ScaleFromFeet),
                                ["note"] = "Edges that touch within the tolerance are not an overlap."
                            });
                        f.ElementIds = new List<long> { a.Id, b.Id };
                        f.EntityKind = rule == "sheet.schedule-overlap" ? "schedule_placement" : "viewport";
                        PlanBox shared = PlanBox.FromCorners(
                            Math.Max(a.Extent.MinX, b.Extent.MinX), Math.Max(a.Extent.MinY, b.Extent.MinY),
                            Math.Min(a.Extent.MaxX, b.Extent.MaxX), Math.Min(a.Extent.MaxY, b.Extent.MaxY));
                        SetPoint(f, Point(shared, opt), opt);
                        r.Findings.Add(f);
                    }
            }
        }

        private static JObject Placement(PlacementFact p, PlanimetryRuleOptions opt)
        {
            return new JObject
            {
                ["placement_id"] = p.Id,
                ["class"] = p.Class,
                ["view_id"] = p.ViewId.HasValue ? (JToken)p.ViewId.Value : JValue.CreateNull(),
                ["schedule_id"] = p.ScheduleId.HasValue ? (JToken)p.ScheduleId.Value : JValue.CreateNull(),
                ["extent"] = Box(p.Extent, opt),
                ["label_included"] = p.LabelBox.Valid
            };
        }

        // ---------------------------------------------------------------------
        // VIEWS
        // ---------------------------------------------------------------------
        private static void Views(PlanimetrySnapshot snap, PlanimetryRuleOptions opt, PlanimetryAuditResult r)
        {
            foreach (ViewFact v in snap.Views)
            {
                if (string.IsNullOrEmpty(v.ViewType) || !v.Readable)
                {
                    r.Findings.Add(Unknown(snap, "view.unclassifiable", "view", v.Id, null, null, v.Id,
                        new JObject { ["reason"] = Reasons(v) }));
                    continue;
                }
                if (v.IsTemplate == true) continue;   // a template owns no placement and is on no sheet

                if (!v.TemplateReadable)
                    r.Findings.Add(Unknown(snap, "view.template-unreadable", "view", v.Id, null, null, v.Id,
                        new JObject { ["reason"] = "the view's ViewTemplateId could not be read" }));
                else if (!v.TemplateId.HasValue)
                    r.Findings.Add(Advisory(snap, "view.no-template", "view", v.Id, null, null, v.Id,
                        new JObject { ["template_id"] = JValue.CreateNull(), ["view_type"] = v.ViewType },
                        new JObject { ["note"] = "A template is a project convention, not a Revit requirement. A requirement set with allowed_template makes it blocking." }));

                if (v.CropBoxActive == true && !v.CropGeometryReadable)
                    r.Findings.Add(Unknown(snap, "view.crop-geometry-unreadable", "view", v.Id, null, null, v.Id,
                        new JObject { ["crop_box_active"] = true }));

                if (v.ViewportIds.Count > 1)
                {
                    var f = Fail(snap, "view.placed-on-multiple-sheets", "view", v.Id, null, null, v.Id,
                        new JObject
                        {
                            ["viewport_count"] = v.ViewportIds.Count,
                            ["sheet_ids"] = new JArray(v.SheetIds.Select(i => (JToken)i))
                        },
                        new JObject { ["viewport_count"] = 1 });
                    f.ElementIds = new List<long> { v.Id };
                    f.ElementIds.AddRange(v.ViewportIds);
                    r.Findings.Add(f);
                }
                else if (v.SheetIds.Count == 0 && v.IsGraphical == true && v.CanBePrinted == true)
                {
                    r.Findings.Add(Advisory(snap, "view.not-placed", "view", v.Id, null, null, v.Id,
                        new JObject { ["view_type"] = v.ViewType, ["sheet_ids"] = new JArray() },
                        new JObject { ["note"] = "Working views legitimately live off-sheet mid-project." }));
                }

                if (v.PrimaryViewId.HasValue && snap.ViewById(v.PrimaryViewId.Value) == null &&
                    snap.Scoped == false)
                {
                    r.Findings.Add(Fail(snap, "view.parent-view-missing", "view", v.Id, null, null, v.Id,
                        new JObject { ["parent_view_id"] = v.PrimaryViewId.Value, ["is_callout"] = v.IsCallout.HasValue ? (JToken)v.IsCallout.Value : JValue.CreateNull() },
                        new JObject { ["parent_view_exists"] = true }));
                }
            }
        }

        // ---------------------------------------------------------------------
        // DIMENSIONS
        // ---------------------------------------------------------------------
        private static void Dimensions(PlanimetrySnapshot snap, List<AnnotationFact> dims,
                                       PlanimetryRuleOptions opt, PlanimetryAuditResult r)
        {
            foreach (AnnotationFact d in dims)
            {
                if (!d.Readable)
                {
                    r.Findings.Add(Unknown(snap, "dimension.reference-unreadable", "dimension", d.Id, null, null,
                        d.OwnerViewId, new JObject { ["reason"] = Reasons(d) }));
                    continue;
                }
                if (d.TypeId == null && d.Notes.Any(n => n.Field == "type"))
                    r.Findings.Add(Unknown(snap, "dimension.type-unreadable", "dimension", d.Id, null, null,
                        d.OwnerViewId, new JObject { ["reason"] = Reason(d, "type") }));
                if (!d.BoundsReadable)
                    r.Findings.Add(Unknown(snap, "dimension.geometry-unreadable", "dimension", d.Id, null, null,
                        d.OwnerViewId, new JObject { ["reason"] = Reason(d, "bounding_box") }));

                if (d.UnreadableReferenceCount.GetValueOrDefault() > 0 || !d.ReferenceCount.HasValue)
                    r.Findings.Add(Unknown(snap, "dimension.reference-unreadable", "dimension", d.Id, null, null,
                        d.OwnerViewId,
                        new JObject
                        {
                            ["unreadable_reference_count"] = d.UnreadableReferenceCount.HasValue
                                ? (JToken)d.UnreadableReferenceCount.Value : JValue.CreateNull(),
                            ["reference_count"] = d.ReferenceCount.HasValue ? (JToken)d.ReferenceCount.Value : JValue.CreateNull()
                        }));

                if (d.AreReferencesAvailable == false)
                    r.Findings.Add(Fail(snap, "dimension.references-unavailable", "dimension", d.Id, null, null,
                        d.OwnerViewId,
                        new JObject { ["references_available"] = false },
                        new JObject { ["references_available"] = true }));

                if (d.BrokenReferenceCount.GetValueOrDefault() > 0)
                    r.Findings.Add(Fail(snap, "dimension.broken-reference", "dimension", d.Id, null, null,
                        d.OwnerViewId,
                        new JObject
                        {
                            ["broken_reference_count"] = d.BrokenReferenceCount.Value,
                            ["reference_count"] = d.ReferenceCount.HasValue ? (JToken)d.ReferenceCount.Value : JValue.CreateNull(),
                            ["linked_reference_count"] = d.LinkedReferenceCount.HasValue ? (JToken)d.LinkedReferenceCount.Value : JValue.CreateNull()
                        },
                        new JObject
                        {
                            ["broken_reference_count"] = 0,
                            ["note"] = "References into RVT links are labelled linked and are never counted broken."
                        }));

                if (d.HasValueOverride == true)
                    r.Findings.Add(Advisory(snap, "dimension.value-override", "dimension", d.Id, null, null,
                        d.OwnerViewId,
                        new JObject { ["value_overrides"] = new JArray(d.ValueOverrides.Select(v => (JToken)v)) },
                        new JObject { ["note"] = "Advisory by default. forbid_numeric_override in a requirement set makes it blocking." }));

                if (d.IsViewSpecific == false || !d.OwnerViewId.HasValue)
                    r.Findings.Add(Advisory(snap, "dimension.no-owner-view", "dimension", d.Id, null, null, null,
                        new JObject { ["view_specific"] = d.IsViewSpecific.HasValue ? (JToken)d.IsViewSpecific.Value : JValue.CreateNull() },
                        new JObject { ["note"] = "A non-view-specific Dimension is a model constraint, not a sheet annotation." }));

                OutsideCrop(snap, d, opt, r, "dimension.outside-annotation-crop", "dimension", true);
            }
        }

        // ---------------------------------------------------------------------
        // TAGS
        // ---------------------------------------------------------------------
        private static void Tags(PlanimetrySnapshot snap, List<AnnotationFact> tags,
                                 PlanimetryRuleOptions opt, PlanimetryAuditResult r)
        {
            foreach (AnnotationFact t in tags)
            {
                if (!t.Readable || t.TargetsReadable == false)
                {
                    r.Findings.Add(Unknown(snap, "tag.target-unreadable", "tag", t.Id, null, null, t.OwnerViewId,
                        new JObject { ["reason"] = Reasons(t) }));
                    continue;
                }
                if (!t.OwnerViewId.HasValue)
                    r.Findings.Add(Fail(snap, "tag.no-owner-view", "tag", t.Id, null, null, null,
                        new JObject { ["owner_view_id"] = JValue.CreateNull() },
                        new JObject { ["owner_view_id"] = "a view id" }));

                if (t.IsOrphaned == true)
                    r.Findings.Add(Fail(snap, "tag.orphaned", "tag", t.Id, null, null, t.OwnerViewId,
                        new JObject { ["orphaned"] = true, ["target_count"] = t.TargetCount.HasValue ? (JToken)t.TargetCount.Value : JValue.CreateNull() },
                        new JObject { ["orphaned"] = false }));
                else if (t.TargetsLinked == true)
                    r.Findings.Add(Unknown(snap, "tag.linked-target-not-inspected", "tag", t.Id, null, null,
                        t.OwnerViewId,
                        new JObject
                        {
                            ["targets_linked"] = true,
                            ["target_count"] = t.TargetCount.HasValue ? (JToken)t.TargetCount.Value : JValue.CreateNull(),
                            ["note"] = "The target lives in a Revit link this pass did not open. Not inspected is not broken."
                        }));

                OutsideCrop(snap, t, opt, r, "tag.outside-annotation-crop", "tag", true);
            }

            // Duplicates: same owner view, same tag type, same TARGET SET. The whole sorted
            // set is the key, so a multi-reference tag is never collapsed onto one target and
            // never matches a single-target tag that happens to share one of them.
            var groups = tags
                .Where(t => t.Readable && t.TargetsReadable != false && t.IsOrphaned != true &&
                            t.OwnerViewId.HasValue && t.TypeId.HasValue && t.TaggedElementIds.Count > 0)
                .GroupBy(t => t.OwnerViewId.Value.ToString(CultureInfo.InvariantCulture) + "" +
                              t.TypeId.Value.ToString(CultureInfo.InvariantCulture) + "" +
                              string.Join(",", t.TaggedElementIds.OrderBy(i => i)), StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            foreach (var g in groups)
            {
                List<AnnotationFact> dup = g.OrderBy(t => t.Id).ToList();
                AnnotationFact first = dup[0];
                var f = Advisory(snap, "tag.duplicate", "tag", first.Id, null, null, first.OwnerViewId,
                    new JObject
                    {
                        ["duplicate_count"] = dup.Count,
                        ["tag_type_id"] = first.TypeId.Value,
                        ["tag_type"] = first.TypeName,
                        ["tagged_element_ids"] = new JArray(first.TaggedElementIds.OrderBy(i => i).Select(i => (JToken)i))
                    },
                    new JObject { ["duplicate_count"] = 1 });
                f.ElementIds = dup.Select(t => t.Id).ToList();
                r.Findings.Add(f);
            }
        }

        // ---------------------------------------------------------------------
        // TEXT
        // ---------------------------------------------------------------------
        private static void Texts(PlanimetrySnapshot snap, List<AnnotationFact> texts,
                                  PlanimetryRuleOptions opt, PlanimetryAuditResult r)
        {
            foreach (AnnotationFact t in texts)
            {
                if (!t.Readable)
                {
                    r.Findings.Add(Unknown(snap, "text.bounds-unreadable", "text_note", t.Id, null, null,
                        t.OwnerViewId, new JObject { ["reason"] = Reasons(t) }));
                    continue;
                }
                if (!t.BoundsReadable)
                    r.Findings.Add(Unknown(snap, "text.bounds-unreadable", "text_note", t.Id, null, null,
                        t.OwnerViewId, new JObject { ["reason"] = Reason(t, "bounding_box") }));

                if (!t.OwnerViewId.HasValue)
                    r.Findings.Add(Fail(snap, "text.no-owner-view", "text_note", t.Id, null, null, null,
                        new JObject { ["owner_view_id"] = JValue.CreateNull() },
                        new JObject { ["owner_view_id"] = "a view id" }));

                if (t.TextIsEmptyOrWhitespace == true)
                    r.Findings.Add(Fail(snap, "text.empty", "text_note", t.Id, null, null, t.OwnerViewId,
                        new JObject { ["text_length"] = (t.Text ?? "").Length, ["empty_or_whitespace"] = true },
                        new JObject { ["empty_or_whitespace"] = false }));

                OutsideCrop(snap, t, opt, r, "text.outside-annotation-crop", "text_note", true);
            }
        }

        // ---------------------------------------------------------------------
        // 2D DETAIL
        // ---------------------------------------------------------------------
        private static void Detail2D(PlanimetrySnapshot snap, List<AnnotationFact> detail,
                                     PlanimetryRuleOptions opt, PlanimetryAuditResult r)
        {
            foreach (AnnotationFact d in detail)
            {
                if (!d.Readable || d.GeometryReadable == false)
                {
                    r.Findings.Add(Unknown(snap, "detail_2d.geometry-unreadable", "detail_2d", d.Id, null, null,
                        d.OwnerViewId, new JObject { ["reason"] = Reasons(d), ["kind"] = d.Kind }));
                    continue;
                }
                if (!d.OwnerViewId.HasValue)
                    r.Findings.Add(Fail(snap, "detail_2d.no-owner-view", "detail_2d", d.Id, null, null, null,
                        new JObject { ["kind"] = d.Kind, ["owner_view_id"] = JValue.CreateNull() },
                        new JObject { ["owner_view_id"] = "a view id" }));
                else if (d.OwnerViewExists == false)
                    r.Findings.Add(Fail(snap, "detail_2d.owner-view-missing", "detail_2d", d.Id, null, null,
                        d.OwnerViewId,
                        new JObject { ["kind"] = d.Kind, ["owner_view_id"] = d.OwnerViewId.Value },
                        new JObject { ["owner_view_exists"] = true }));

                if (d.Degenerate == true)
                    r.Findings.Add(Fail(snap, "detail_2d.degenerate-curve", "detail_2d", d.Id, null, null,
                        d.OwnerViewId,
                        new JObject
                        {
                            ["kind"] = d.Kind,
                            ["length"] = d.CurveLength.HasValue
                                ? (JToken)PlanimetryGeometry.Display(d.CurveLength.Value, opt.ScaleFromFeet)
                                : JValue.CreateNull(),
                            ["units"] = opt.Units
                        },
                        new JObject { ["length_greater_than"] = 0 }));

                if (d.Kind == "filled_region" && d.LoopCount == null)
                    r.Findings.Add(Unknown(snap, "detail_2d.region-read-incomplete", "detail_2d", d.Id, null, null,
                        d.OwnerViewId, new JObject { ["reason"] = Reason(d, "loops") }));

                OutsideCrop(snap, d, opt, r, "detail_2d.outside-crop", "detail_2d", false);
            }
        }

        // ---------------------------------------------------------------------
        // REFERENCES
        // ---------------------------------------------------------------------
        private static void References(PlanimetrySnapshot snap, PlanimetryRuleOptions opt, PlanimetryAuditResult r)
        {
            foreach (ReferenceFact f in snap.References)
            {
                if (!f.Readable)
                {
                    r.Findings.Add(Unknown(snap, "reference.target-unreadable", "view_reference", f.Id, null, null,
                        f.OwnerViewId, new JObject { ["reason"] = Reasons(f), ["kind"] = f.Kind }));
                    continue;
                }
                switch (f.TargetState)
                {
                    case "missing":
                        r.Findings.Add(Fail(snap, "reference.target-missing", "view_reference", f.Id, null, null,
                            f.OwnerViewId,
                            new JObject
                            {
                                ["kind"] = f.Kind,
                                ["target_view_id"] = f.TargetViewId.HasValue ? (JToken)f.TargetViewId.Value : JValue.CreateNull(),
                                ["reason"] = f.TargetStateReason
                            },
                            new JObject { ["target_view_exists"] = true }));
                        break;
                    case "unreadable":
                        r.Findings.Add(Unknown(snap, "reference.target-unreadable", "view_reference", f.Id, null,
                            null, f.OwnerViewId,
                            new JObject { ["kind"] = f.Kind, ["reason"] = f.TargetStateReason }));
                        break;
                    case "unknown":
                        r.Findings.Add(Unknown(snap, "reference.target-unidentifiable", "view_reference", f.Id, null,
                            null, f.OwnerViewId,
                            new JObject
                            {
                                ["kind"] = f.Kind,
                                ["reason"] = f.TargetStateReason ??
                                    "the API exposes no relation from this marker to a target view",
                                ["note"] = "Never inferred from a name."
                            }));
                        break;
                    case "resolved":
                        if (f.TargetPlaced == false)
                            r.Findings.Add(Advisory(snap, "reference.target-not-placed", "view_reference", f.Id,
                                null, null, f.OwnerViewId,
                                new JObject
                                {
                                    ["kind"] = f.Kind,
                                    ["target_view_id"] = f.TargetViewId.HasValue ? (JToken)f.TargetViewId.Value : JValue.CreateNull(),
                                    ["target_view_name"] = f.TargetViewName,
                                    ["target_sheet_ids"] = new JArray()
                                },
                                new JObject { ["note"] = "Mid-project a referenced view legitimately has no sheet yet." }));
                        break;
                }
            }
        }

        // ---------------------------------------------------------------------
        // "Outside the crop", and the three conditions that make it demonstrable
        // ---------------------------------------------------------------------
        private static void OutsideCrop(PlanimetrySnapshot snap, AnnotationFact a, PlanimetryRuleOptions opt,
                                        PlanimetryAuditResult r, string ruleId, string entityKind,
                                        bool useAnnotationCrop)
        {
            if (!a.OwnerViewId.HasValue || !a.Box.Valid) return;
            ViewFact v = snap.ViewById(a.OwnerViewId.Value);
            if (v == null) return;

            PlanBox crop = useAnnotationCrop ? v.AnnotationCrop : v.CropBox;
            bool active = useAnnotationCrop ? v.AnnotationCropActive == true : v.CropBoxActive == true;
            if (!active || !crop.Valid) return;                       // not demonstrable: say nothing
            if (!PlanimetryGeometry.Disjoint(crop, a.Box, opt.ToleranceFeet)) return;

            var f = Fail(snap, ruleId, entityKind, a.Id, null, null, a.OwnerViewId,
                new JObject
                {
                    ["kind"] = a.Kind,
                    ["element_box"] = Box(a.Box, opt),
                    [useAnnotationCrop ? "annotation_crop" : "crop_region"] = Box(crop, opt),
                    ["units"] = opt.Units
                },
                new JObject
                {
                    ["inside_crop"] = true,
                    ["note"] = "Reported only because the crop is ACTIVE and both its shape and the element's " +
                               "box were read. Where any of the three is missing, this rule stays silent rather " +
                               "than guessing."
                },
                Point(a.Box, opt), opt);
            r.Findings.Add(f);
        }

        // =====================================================================
        // THE CONFIGURABLE PASS
        // =====================================================================
        public static PlanimetryAuditResult EvaluateRequirementSet(PlanimetrySnapshot snap,
                                                                   PlanimetryRequirementSet set,
                                                                   PlanimetryRuleOptions opt)
        {
            var result = new PlanimetryAuditResult();
            var tally = new Dictionary<string, PlanimetryCheckRun>(StringComparer.Ordinal);

            foreach (PlanimetryRule rule in set.Rules)
            {
                var run = new PlanimetryCheckRun
                {
                    RuleId = rule.Id,
                    Severity = rule.Blocking ? "blocking" : "advisory",
                    Entity = rule.Entity,
                    Description = rule.Message ?? (rule.Entity + " " + rule.Operator),
                    Status = "not_applicable"
                };
                tally[rule.Id] = run;

                List<RuleTarget> targets;
                try { targets = Select(snap, rule, run, result, set, opt); }
                catch (Exception ex)
                {
                    result.ChecksFailed.Add(new PlanimetryCheckFailure { Check = rule.Id, Error = ex.Message });
                    continue;
                }
                run.Population = targets.Count;

                try { Assert(snap, rule, targets, set, opt, result); }
                catch (Exception ex)
                {
                    result.ChecksFailed.Add(new PlanimetryCheckFailure { Check = rule.Id, Error = ex.Message });
                }
            }

            if (!opt.IncludeAdvisory)
                result.Findings = result.Findings.Where(f => f.Severity != "advisory").ToList();

            foreach (PlanimetryFinding f in result.Findings)
            {
                PlanimetryCheckRun run;
                if (!tally.TryGetValue(f.RuleId, out run)) continue;
                if (f.Status == "unknown") run.Unknowns++; else run.Findings++;
            }
            foreach (PlanimetryCheckRun run in tally.Values)
            {
                if (run.Findings > 0) run.Status = "failed";
                else if (run.Unknowns > 0) run.Status = "unknown";
                else if (run.Population > 0) run.Status = "passed";
                else run.Status = "not_applicable";
            }
            result.Checks = tally.Values.OrderBy(c => c.RuleId, StringComparer.Ordinal).ToList();

            if (opt.IncludePassedChecks)
                foreach (PlanimetryCheckRun run in result.Checks.Where(c => c.Status == "passed"))
                {
                    if (!opt.IncludeAdvisory && run.Severity == "advisory") continue;
                    result.Findings.Add(new PlanimetryFinding
                    {
                        RuleId = run.RuleId,
                        RequirementSetId = set.Id,
                        RequirementSetVersion = set.Version,
                        RequirementSetSha256 = set.Sha256,
                        Severity = run.Severity,
                        Status = "passed",
                        EntityKind = run.Entity,
                        CoverageComplete = snap.CoverageComplete,
                        Observed = new JObject { ["population_examined"] = run.Population, ["findings"] = 0 }
                    });
                }

            result.Findings.Sort(PlanimetryFinding.Compare);
            return result;
        }

        /// <summary>One entity a rule may examine, flattened so selectors and assertions do
        /// not each need to know nine shapes.</summary>
        private sealed class RuleTarget
        {
            public long Id;
            public long? SheetId;
            public string SheetNumber;
            public long? ViewId;
            public SheetFact Sheet;
            public ViewFact View;
            public PlacementFact Placement;
            public AnnotationFact Annotation;
            public ReferenceFact Reference;
        }

        private static List<RuleTarget> Select(PlanimetrySnapshot snap, PlanimetryRule rule,
                                               PlanimetryCheckRun run, PlanimetryAuditResult result,
                                               PlanimetryRequirementSet set, PlanimetryRuleOptions opt)
        {
            IEnumerable<RuleTarget> candidates;
            switch (rule.Entity)
            {
                case "sheet":
                    candidates = snap.Sheets.Select(s => new RuleTarget
                    { Id = s.Id, SheetId = s.Id, SheetNumber = s.SheetNumber, Sheet = s });
                    break;
                case "view":
                    candidates = snap.Views.Where(v => v.IsTemplate != true).Select(v => new RuleTarget
                    { Id = v.Id, ViewId = v.Id, View = v });
                    break;
                case "viewport":
                    candidates = snap.Placements.Where(p => p.Class == "viewport").Select(p => new RuleTarget
                    { Id = p.Id, SheetId = p.SheetId, SheetNumber = p.SheetNumber, ViewId = p.ViewId, Placement = p });
                    break;
                case "schedule_placement":
                    candidates = snap.Placements.Where(p => p.Class == "schedule_placement").Select(p => new RuleTarget
                    { Id = p.Id, SheetId = p.SheetId, SheetNumber = p.SheetNumber, Placement = p });
                    break;
                case "dimension":
                    candidates = snap.Annotations.Where(a => a.Kind == "dimension").Select(Annotation);
                    break;
                case "tag":
                    candidates = snap.Annotations.Where(a => a.Kind == "tag" || a.Kind == "revision_tag").Select(Annotation);
                    break;
                case "text_note":
                    candidates = snap.Annotations.Where(a => a.Kind == "text_note").Select(Annotation);
                    break;
                case "detail_2d":
                    candidates = snap.Annotations.Where(a => IsDetailKind(a.Kind)).Select(Annotation);
                    break;
                case "view_reference":
                    candidates = snap.References.Select(f => new RuleTarget
                    { Id = f.Id, ViewId = f.OwnerViewId, Reference = f });
                    break;
                default:
                    throw new InvalidOperationException("entity '" + rule.Entity + "' escaped load validation");
            }

            var selected = new List<RuleTarget>();
            foreach (RuleTarget t in candidates.OrderBy(x => x.Id))
            {
                bool matched = true;
                foreach (PlanimetrySelector s in rule.Selectors)
                {
                    if (s.Operator == "applies_to")
                    {
                        matched = ((JArray)s.Value).Any(v => (long)v == t.Id);
                        if (!matched) break;
                        continue;
                    }
                    bool readable;
                    JToken value = Field(t, s.Field, out readable);
                    if (!readable)
                    {
                        // A selector that cannot be evaluated does not silently exclude the
                        // element: the element becomes an unknown for this rule and is not
                        // asserted over. Excluding it would be the quiet pass.
                        result.Findings.Add(UnknownForRule(snap, rule, t,
                            new JObject
                            {
                                ["selector_field"] = s.Field,
                                ["reason"] = "the selector's field could not be read on this element"
                            }));
                        matched = false;
                        break;
                    }
                    if (!SelectorMatches(s, value, out bool timedOut))
                    {
                        if (timedOut)
                            result.Findings.Add(UnknownForRule(snap, rule, t,
                                new JObject
                                {
                                    ["selector_field"] = s.Field,
                                    ["reason"] = "the selector's regular expression exceeded the " +
                                                 PlanimetryRequirementSet.RegexTimeout.TotalMilliseconds +
                                                 " ms match timeout on this element"
                                }));
                        matched = false;
                        break;
                    }
                }
                if (matched) selected.Add(t);
            }
            return selected;
        }

        private static RuleTarget Annotation(AnnotationFact a)
        {
            return new RuleTarget { Id = a.Id, ViewId = a.OwnerViewId, Annotation = a };
        }

        private static bool SelectorMatches(PlanimetrySelector s, JToken value, out bool timedOut)
        {
            timedOut = false;
            if (s.Operator == "matches")
                return PlanimetryRequirementSet.IsMatch(s.Pattern, AsText(value), out timedOut);
            if (s.Operator == "in_list")
                return ((JArray)s.Value).Any(v => SameValue(v, value));
            return SameValue(s.Value, value);
        }

        private static void Assert(PlanimetrySnapshot snap, PlanimetryRule rule, List<RuleTarget> targets,
                                   PlanimetryRequirementSet set, PlanimetryRuleOptions opt,
                                   PlanimetryAuditResult r)
        {
            foreach (RuleTarget t in targets)
            {
                if (PlanimetryRequirementSet.IsWholeEntityOperator(rule.Operator))
                { WholeEntity(snap, rule, t, targets, set, opt, r); continue; }

                bool readable;
                JToken value = Field(t, rule.AssertionField, out readable);
                if (!readable)
                {
                    r.Findings.Add(UnknownForRule(snap, rule, t, new JObject
                    {
                        ["field"] = rule.AssertionField,
                        ["reason"] = "the asserted field could not be read on this element"
                    }));
                    continue;
                }

                bool passes;
                bool timedOut = false;
                switch (rule.Operator)
                {
                    case "matches":
                        passes = PlanimetryRequirementSet.IsMatch(rule.Pattern, AsText(value), out timedOut); break;
                    case "not_matches":
                        passes = !PlanimetryRequirementSet.IsMatch(rule.Pattern, AsText(value), out timedOut); break;
                    case "equals": passes = SameValue(rule.Value, value); break;
                    case "not_equals": passes = !SameValue(rule.Value, value); break;
                    case "in_list": passes = ((JArray)rule.Value).Any(v => SameValue(v, value)); break;
                    case "not_in_list": passes = !((JArray)rule.Value).Any(v => SameValue(v, value)); break;
                    case "required": passes = value != null && value.Type != JTokenType.Null; break;
                    case "not_empty": passes = !string.IsNullOrWhiteSpace(AsText(value)); break;
                    case "greater_than": passes = Number(value).HasValue && Number(value).Value > rule.Value.Value<double>(); break;
                    case "less_than": passes = Number(value).HasValue && Number(value).Value < rule.Value.Value<double>(); break;
                    case "between":
                        double? n = Number(value);
                        JArray pair = (JArray)rule.Value;
                        passes = n.HasValue && n.Value >= pair[0].Value<double>() && n.Value <= pair[1].Value<double>();
                        break;
                    default:
                        throw new InvalidOperationException("operator '" + rule.Operator + "' escaped load validation");
                }

                if (timedOut)
                {
                    r.Findings.Add(UnknownForRule(snap, rule, t, new JObject
                    {
                        ["field"] = rule.AssertionField,
                        ["reason"] = "the assertion's regular expression exceeded the " +
                                     PlanimetryRequirementSet.RegexTimeout.TotalMilliseconds + " ms match timeout"
                    }));
                    continue;
                }
                if (passes) continue;

                r.Findings.Add(FailForRule(snap, rule, t,
                    new JObject { ["field"] = rule.AssertionField, ["value"] = value },
                    new JObject
                    {
                        ["operator"] = rule.Operator,
                        ["value"] = rule.Value ?? JValue.CreateNull()
                    }));
            }
        }

        private static void WholeEntity(PlanimetrySnapshot snap, PlanimetryRule rule, RuleTarget t,
                                        List<RuleTarget> all, PlanimetryRequirementSet set,
                                        PlanimetryRuleOptions opt, PlanimetryAuditResult r)
        {
            switch (rule.Operator)
            {
                case "allowed_type": AllowedType(snap, rule, t, r); return;
                case "allowed_template": AllowedTemplate(snap, rule, t, r); return;
                case "allowed_scale": AllowedScale(snap, rule, t, r); return;
                case "required_parameter": RequiredParameter(snap, rule, t, r); return;
                case "forbid_numeric_override": ForbidOverride(snap, rule, t, r); return;
                case "inside_extent": InsideExtent(snap, rule, t, opt, r); return;
                case "minimum_gap": MinimumGap(snap, rule, t, opt, r); return;
                case "requires_tag": RequiresTag(snap, rule, t, opt, r); return;
            }
        }

        private static void AllowedType(PlanimetrySnapshot snap, PlanimetryRule rule, RuleTarget t,
                                        PlanimetryAuditResult r)
        {
            string field = t.Sheet != null ? "titleblock_type"
                         : t.Placement != null ? "viewport_type"
                         : "type";
            bool readable;
            JToken value = Field(t, field, out readable);
            if (!readable)
            {
                r.Findings.Add(UnknownForRule(snap, rule, t,
                    new JObject { ["field"] = field, ["reason"] = "the type name could not be read" }));
                return;
            }
            string name = AsText(value);
            if (((JArray)rule.Value).Any(v => string.Equals((string)v, name, StringComparison.Ordinal))) return;
            r.Findings.Add(FailForRule(snap, rule, t,
                new JObject { ["type"] = name },
                new JObject { ["allowed_type"] = rule.Value }));
        }

        private static void AllowedTemplate(PlanimetrySnapshot snap, PlanimetryRule rule, RuleTarget t,
                                            PlanimetryAuditResult r)
        {
            ViewFact v = t.View;
            if (v == null) return;
            if (!v.TemplateReadable)
            {
                r.Findings.Add(UnknownForRule(snap, rule, t,
                    new JObject { ["reason"] = "the view's template could not be read" }));
                return;
            }
            if (v.TemplateId.HasValue &&
                ((JArray)rule.Value).Any(x => string.Equals((string)x, v.TemplateName, StringComparison.Ordinal)))
                return;
            r.Findings.Add(FailForRule(snap, rule, t,
                new JObject
                {
                    ["template_id"] = v.TemplateId.HasValue ? (JToken)v.TemplateId.Value : JValue.CreateNull(),
                    ["template_name"] = v.TemplateName
                },
                new JObject
                {
                    ["allowed_template"] = rule.Value,
                    ["note"] = "A view with NO template does not satisfy allowed_template: null is not in the list."
                }));
        }

        private static void AllowedScale(PlanimetrySnapshot snap, PlanimetryRule rule, RuleTarget t,
                                         PlanimetryAuditResult r)
        {
            ViewFact v = t.View;
            if (v == null) return;
            if (!v.Scale.HasValue)
            {
                r.Findings.Add(UnknownForRule(snap, rule, t,
                    new JObject { ["reason"] = "the view has no readable scale" }));
                return;
            }
            if (((JArray)rule.Value).Any(x => x.Value<double>() == v.Scale.Value)) return;
            r.Findings.Add(FailForRule(snap, rule, t,
                new JObject { ["scale"] = v.Scale.Value },
                new JObject { ["allowed_scale"] = rule.Value }));
        }

        private static void RequiredParameter(PlanimetrySnapshot snap, PlanimetryRule rule, RuleTarget t,
                                              PlanimetryAuditResult r)
        {
            Dictionary<string, JToken> parameters = t.Sheet != null ? t.Sheet.Parameters
                                                  : t.View != null ? t.View.Parameters : null;
            if (parameters == null) return;
            foreach (JToken want in (JArray)rule.Value)
            {
                string name = (string)want;
                JToken have;
                bool present = parameters.TryGetValue(name, out have) &&
                               have != null && have.Type != JTokenType.Null &&
                               !string.IsNullOrWhiteSpace(AsText(have));
                if (present) continue;
                r.Findings.Add(FailForRule(snap, rule, t,
                    new JObject
                    {
                        ["parameter"] = name,
                        ["present"] = parameters.ContainsKey(name),
                        ["value"] = have ?? JValue.CreateNull()
                    },
                    new JObject { ["required_parameter"] = name, ["non_empty"] = true }));
            }
        }

        private static void ForbidOverride(PlanimetrySnapshot snap, PlanimetryRule rule, RuleTarget t,
                                           PlanimetryAuditResult r)
        {
            AnnotationFact d = t.Annotation;
            if (d == null) return;
            if (!d.HasValueOverride.HasValue)
            {
                r.Findings.Add(UnknownForRule(snap, rule, t,
                    new JObject { ["reason"] = "the dimension's overrides could not be read" }));
                return;
            }
            if (d.HasValueOverride == false) return;
            r.Findings.Add(FailForRule(snap, rule, t,
                new JObject { ["value_overrides"] = new JArray(d.ValueOverrides.Select(v => (JToken)v)) },
                new JObject { ["value_overrides"] = new JArray() }));
        }

        private static void InsideExtent(PlanimetrySnapshot snap, PlanimetryRule rule, RuleTarget t,
                                         PlanimetryRuleOptions opt, PlanimetryAuditResult r)
        {
            PlacementFact p = t.Placement;
            if (p == null) return;
            SheetFact sheet = snap.SheetById(p.SheetId);
            double marginFeet = rule.Value.Value<double>() / opt.ScaleFromFeet;
            if (sheet == null || !sheet.Extent.Valid || !p.Extent.Valid)
            {
                r.Findings.Add(UnknownForRule(snap, rule, t, new JObject
                {
                    ["reason"] = "the sheet extent or the placement extent could not be read, so containment " +
                                 "cannot be measured"
                }));
                return;
            }
            PlanBox allowed = PlanimetryGeometry.Expand(sheet.Extent, -marginFeet);
            if (PlanimetryGeometry.Contains(allowed, p.Extent, opt.ToleranceFeet)) return;
            var f = FailForRule(snap, rule, t,
                new JObject
                {
                    ["placement_extent"] = Box(p.Extent, opt),
                    ["allowed_extent"] = Box(allowed, opt),
                    ["sheet_extent"] = Box(sheet.Extent, opt),
                    ["sheet_extent_source"] = sheet.ExtentSource,
                    ["units"] = opt.Units
                },
                new JObject { ["margin"] = rule.Value, ["units"] = opt.Units });
            SetPoint(f, Point(p.Extent, opt), opt);
            r.Findings.Add(f);
        }

        private static void MinimumGap(PlanimetrySnapshot snap, PlanimetryRule rule, RuleTarget t,
                                       PlanimetryRuleOptions opt, PlanimetryAuditResult r)
        {
            PlacementFact p = t.Placement;
            if (p == null) return;
            double wantFeet = rule.Value.Value<double>() / opt.ScaleFromFeet;
            if (!p.Extent.Valid)
            {
                r.Findings.Add(UnknownForRule(snap, rule, t,
                    new JObject { ["reason"] = "this placement's extent could not be read" }));
                return;
            }
            foreach (PlacementFact other in snap.Placements
                         .Where(o => o.SheetId == p.SheetId && o.Id != p.Id).OrderBy(o => o.Id))
            {
                if (!other.Extent.Valid)
                {
                    r.Findings.Add(UnknownForRule(snap, rule, t, new JObject
                    {
                        ["other_placement_id"] = other.Id,
                        ["reason"] = "the neighbouring placement's extent could not be read, so the gap to it " +
                                     "is unknown"
                    }));
                    continue;
                }
                // Report each unordered pair once: the lower id owns the finding.
                if (other.Id < p.Id) continue;
                double gap = PlanimetryGeometry.Separation(p.Extent, other.Extent);
                if (gap >= wantFeet) continue;
                var f = FailForRule(snap, rule, t,
                    new JObject
                    {
                        ["gap"] = PlanimetryGeometry.Display(gap, opt.ScaleFromFeet),
                        ["units"] = opt.Units,
                        ["a"] = Placement(p, opt),
                        ["b"] = Placement(other, opt)
                    },
                    new JObject { ["minimum_gap"] = rule.Value, ["units"] = opt.Units });
                f.ElementIds = new List<long> { p.Id, other.Id };
                r.Findings.Add(f);
            }
        }

        private static void RequiresTag(PlanimetrySnapshot snap, PlanimetryRule rule, RuleTarget t,
                                        PlanimetryRuleOptions opt, PlanimetryAuditResult r)
        {
            ViewFact v = t.View;
            if (v == null) return;
            foreach (TagRequirement req in rule.TagRequirements)
            {
                TagCoverageFact cov = v.TagCoverage == null
                    ? null
                    : v.TagCoverage.FirstOrDefault(c =>
                          string.Equals(c.Category, req.Category, StringComparison.OrdinalIgnoreCase));
                if (cov == null)
                {
                    r.Findings.Add(UnknownForRule(snap, rule, t, new JObject
                    {
                        ["category"] = req.Category,
                        ["reason"] = "the visible set for this category was not gathered for this view, so " +
                                     "whether anything is untagged is unknown"
                    }));
                    continue;
                }

                List<UntaggedElement> untagged = cov.Untagged
                    .Where(u => string.Equals(u.Category, req.Category, StringComparison.OrdinalIgnoreCase))
                    .Where(u => !Excluded(u, req))
                    .OrderBy(u => u.Id)
                    .ToList();

                foreach (UntaggedElement u in untagged)
                {
                    var f = FailForRule(snap, rule, t,
                        new JObject
                        {
                            ["category"] = u.Category,
                            ["untagged_element_id"] = u.Id,
                            ["type"] = u.TypeName,
                            ["family"] = u.FamilyName,
                            ["visible_total"] = cov.VisibleTotal,
                            ["tagged_total"] = cov.TaggedTotal
                        },
                        new JObject { ["tagged"] = true, ["category"] = req.Category });
                    f.ElementIds = new List<long> { u.Id };
                    r.Findings.Add(f);
                }

                if (!cov.Complete)
                    r.Findings.Add(UnknownForRule(snap, rule, t, new JObject
                    {
                        ["category"] = req.Category,
                        ["reason"] = cov.IncompleteReason,
                        ["untagged_total"] = cov.UntaggedTotal,
                        ["untagged_listed"] = cov.Untagged.Count,
                        ["note"] = "The listed findings are a LOWER BOUND: what was not enumerated is unknown, " +
                                   "not tagged."
                    }));

                if (cov.LinkedVisibleTotal > 0)
                    r.Findings.Add(UnknownForRule(snap, rule, t, new JObject
                    {
                        ["category"] = req.Category,
                        ["linked_element_total"] = cov.LinkedVisibleTotal,
                        ["reason"] = "loaded Revit LINKS carry " + cov.LinkedVisibleTotal + " element(s) of this " +
                                     "category (counted per linked document, not per view). They are not this " +
                                     "model's to tag and were not counted as untagged - reported so their " +
                                     "absence from the findings is a decision, not an oversight."
                    }));
            }
        }

        private static bool Excluded(UntaggedElement u, TagRequirement req)
        {
            if (req.ExcludeTypes.Contains(u.TypeName ?? "", StringComparer.Ordinal)) return true;
            if (req.ExcludeFamilies.Contains(u.FamilyName ?? "", StringComparer.Ordinal)) return true;
            if (req.ExcludeTypeMatches != null)
            {
                bool timedOut;
                if (PlanimetryRequirementSet.IsMatch(req.ExcludeTypeMatches, u.TypeName ?? "", out timedOut))
                    return true;
            }
            if (!string.IsNullOrWhiteSpace(req.ExcludeWhenParameterSet))
            {
                string value;
                if (u.ExclusionParameters.TryGetValue(req.ExcludeWhenParameterSet, out value) &&
                    !string.IsNullOrWhiteSpace(value))
                    return true;
            }
            return false;
        }

        // ---------------------------------------------------------------------
        // Field access. ONE table, so a selector and an assertion can never read
        // the same field name differently.
        // ---------------------------------------------------------------------
        private static JToken Field(RuleTarget t, string field, out bool readable)
        {
            readable = true;
            if (field != null && field.StartsWith("parameter:", StringComparison.Ordinal))
            {
                string name = field.Substring("parameter:".Length);
                Dictionary<string, JToken> p = t.Sheet != null ? t.Sheet.Parameters
                                             : t.View != null ? t.View.Parameters : null;
                if (p == null) { readable = false; return null; }
                JToken v;
                return p.TryGetValue(name, out v) ? v : JValue.CreateNull();
            }

            if (t.Sheet != null)
            {
                SheetFact s = t.Sheet;
                switch (field)
                {
                    case "sheet_number": return Text(s.SheetNumber, s, "sheet_number", ref readable);
                    case "name": return Text(s.Name, s, "name", ref readable);
                    case "placeholder": return s.IsPlaceholder.HasValue ? (JToken)s.IsPlaceholder.Value : Miss(ref readable);
                    case "titleblock_type": return Text(s.TitleblockTypeName, s, "titleblock_type", ref readable);
                    case "titleblock_family": return Text(s.TitleblockFamilyName, s, "titleblock_family", ref readable);
                    case "titleblock_count":
                        if (!s.TitleblocksReadable) return Miss(ref readable);
                        return s.TitleblockIds.Count;
                    case "revision_count": return s.RevisionIds.Count;
                    case "viewport_count": return s.ViewportIds.Count;
                    case "schedule_placement_count": return s.SchedulePlacementIds.Count;
                }
            }
            else if (t.View != null)
            {
                ViewFact v = t.View;
                switch (field)
                {
                    case "name": return Text(v.Name, v, "name", ref readable);
                    case "view_type": return Text(v.ViewType, v, "view_type", ref readable);
                    case "discipline": return Text(v.Discipline, v, "discipline", ref readable);
                    case "detail_level": return Text(v.DetailLevel, v, "detail_level", ref readable);
                    case "scale": return v.Scale.HasValue ? (JToken)v.Scale.Value : Miss(ref readable);
                    case "template_name":
                        if (!v.TemplateReadable) return Miss(ref readable);
                        return v.TemplateName == null ? JValue.CreateNull() : (JToken)v.TemplateName;
                    case "template_id":
                        if (!v.TemplateReadable) return Miss(ref readable);
                        return v.TemplateId.HasValue ? (JToken)v.TemplateId.Value : JValue.CreateNull();
                    case "level": return v.LevelName == null ? JValue.CreateNull() : (JToken)v.LevelName;
                    case "phase": return v.Phase == null ? JValue.CreateNull() : (JToken)v.Phase;
                    case "phase_filter": return v.PhaseFilter == null ? JValue.CreateNull() : (JToken)v.PhaseFilter;
                    case "crop_box_active": return v.CropBoxActive.HasValue ? (JToken)v.CropBoxActive.Value : Miss(ref readable);
                    case "placed_on_sheet": return v.SheetIds.Count > 0;
                    case "sheet_count": return v.SheetIds.Count;
                    case "is_template": return v.IsTemplate.HasValue ? (JToken)v.IsTemplate.Value : Miss(ref readable);
                }
            }
            else if (t.Placement != null)
            {
                PlacementFact p = t.Placement;
                switch (field)
                {
                    case "sheet_number": return p.SheetNumber == null ? JValue.CreateNull() : (JToken)p.SheetNumber;
                    case "view_name": return p.TargetName == null ? JValue.CreateNull() : (JToken)p.TargetName;
                    case "schedule_name": return p.TargetName == null ? JValue.CreateNull() : (JToken)p.TargetName;
                    case "viewport_type": return Text(p.TypeName, p, "viewport_type", ref readable);
                    case "detail_number": return p.DetailNumber == null ? JValue.CreateNull() : (JToken)p.DetailNumber;
                    case "title": return p.Title == null ? JValue.CreateNull() : (JToken)p.Title;
                    case "rotation": return p.Rotation == null ? JValue.CreateNull() : (JToken)p.Rotation;
                    case "pinned": return p.Pinned.HasValue ? (JToken)p.Pinned.Value : Miss(ref readable);
                }
            }
            else if (t.Annotation != null)
            {
                AnnotationFact a = t.Annotation;
                switch (field)
                {
                    case "type": return Text(a.TypeName, a, "type", ref readable);
                    case "family": return a.FamilyName == null ? JValue.CreateNull() : (JToken)a.FamilyName;
                    case "category": return a.Category == null ? JValue.CreateNull() : (JToken)a.Category;
                    case "owner_view_name": return a.OwnerViewName == null ? JValue.CreateNull() : (JToken)a.OwnerViewName;
                    case "value_override": return new JArray(a.ValueOverrides.Select(v => (JToken)v));
                    case "has_value_override": return a.HasValueOverride.HasValue ? (JToken)a.HasValueOverride.Value : Miss(ref readable);
                    case "references_available": return a.AreReferencesAvailable.HasValue ? (JToken)a.AreReferencesAvailable.Value : Miss(ref readable);
                    case "segment_count": return a.SegmentCount.HasValue ? (JToken)a.SegmentCount.Value : Miss(ref readable);
                    case "target_categories": return new JArray(a.TargetCategories.Select(c => (JToken)c));
                    case "orphaned": return a.IsOrphaned.HasValue ? (JToken)a.IsOrphaned.Value : Miss(ref readable);
                    case "has_leader": return a.HasLeader.HasValue ? (JToken)a.HasLeader.Value : Miss(ref readable);
                    case "has_view_overrides": return a.HasViewOverrides.HasValue ? (JToken)a.HasViewOverrides.Value : Miss(ref readable);
                    case "text": return a.Text == null ? JValue.CreateNull() : (JToken)a.Text;
                    case "alignment": return a.Alignment == null ? JValue.CreateNull() : (JToken)a.Alignment;
                    case "width": return a.Width.HasValue ? (JToken)a.Width.Value : Miss(ref readable);
                }
            }
            else if (t.Reference != null)
            {
                ReferenceFact f = t.Reference;
                switch (field)
                {
                    case "kind": return f.Kind == null ? JValue.CreateNull() : (JToken)f.Kind;
                    case "owner_view_name": return f.OwnerViewName == null ? JValue.CreateNull() : (JToken)f.OwnerViewName;
                    case "target_view_name": return f.TargetViewName == null ? JValue.CreateNull() : (JToken)f.TargetViewName;
                    case "target_state": return f.TargetState == null ? JValue.CreateNull() : (JToken)f.TargetState;
                    case "target_placed": return f.TargetPlaced.HasValue ? (JToken)f.TargetPlaced.Value : Miss(ref readable);
                }
            }

            // A field the load validation accepted for this entity but this row cannot
            // supply. Unknown, never a pass.
            readable = false;
            return null;
        }

        private static JToken Miss(ref bool readable) { readable = false; return null; }

        private static JToken Text(string value, PlanimetryRow row, string field, ref bool readable)
        {
            if (value == null && row.Notes.Any(n => n.Field == field)) { readable = false; return null; }
            return value == null ? JValue.CreateNull() : (JToken)value;
        }

        private static string AsText(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) return (string)t;
            if (t.Type == JTokenType.Boolean) return ((bool)t) ? "true" : "false";
            if (t.Type == JTokenType.Float) return ((double)t).ToString("0.######", CultureInfo.InvariantCulture);
            if (t.Type == JTokenType.Integer) return ((long)t).ToString(CultureInfo.InvariantCulture);
            return t.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static double? Number(JToken t)
        {
            if (t == null) return null;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return t.Value<double>();
            double parsed;
            if (t.Type == JTokenType.String &&
                double.TryParse((string)t, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return null;
        }

        private static bool SameValue(JToken want, JToken have)
        {
            if (want == null || have == null) return false;
            if (want.Type == JTokenType.Boolean || have.Type == JTokenType.Boolean)
                return string.Equals(AsText(want), AsText(have), StringComparison.OrdinalIgnoreCase);
            double? a = Number(want), b = Number(have);
            if (a.HasValue && b.HasValue) return a.Value == b.Value;
            if (have is JArray list) return list.Any(v => string.Equals(AsText(want), AsText(v), StringComparison.Ordinal));
            return string.Equals(AsText(want), AsText(have), StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------
        // Finding constructors. One place each, so no rule invents its own shape.
        // ---------------------------------------------------------------------
        private static PlanimetryFinding Base(PlanimetrySnapshot snap, string ruleId, string severity,
                                              string status, string entity, long elementId,
                                              long? sheetId, string sheetNumber, long? viewId)
        {
            PlanimetryCheck c = Check(ruleId);
            return new PlanimetryFinding
            {
                RuleId = ruleId,
                RequirementSetId = UniversalId,
                RequirementSetVersion = UniversalVersion,
                Severity = severity,
                Status = status,
                EntityKind = entity,
                SheetId = sheetId,
                SheetNumber = sheetNumber ?? snap.SheetNumberOf(sheetId),
                ViewId = viewId,
                ElementIds = new List<long> { elementId },
                CoverageComplete = snap.CoverageComplete,
                Fixable = false,
                RecommendedTool = c == null ? null : c.RecommendedTool,
                Evidence = c == null ? new JObject() : new JObject { ["description"] = c.Description }
            };
        }

        private static PlanimetryFinding Fail(PlanimetrySnapshot snap, string ruleId, string entity, long id,
                                              long? sheetId, string sheetNumber, long? viewId,
                                              JObject observed, JObject expected,
                                              double[] point = null, PlanimetryRuleOptions opt = null)
        {
            PlanimetryCheck c = Check(ruleId);
            var f = Base(snap, ruleId, c == null ? "blocking" : c.Severity, "failed", entity, id,
                         sheetId, sheetNumber, viewId);
            f.Observed = observed; f.Expected = expected;
            if (point != null && opt != null) SetPoint(f, point, opt);
            return f;
        }

        private static PlanimetryFinding Advisory(PlanimetrySnapshot snap, string ruleId, string entity, long id,
                                                  long? sheetId, string sheetNumber, long? viewId,
                                                  JObject observed, JObject expected)
        {
            var f = Base(snap, ruleId, "advisory", "failed", entity, id, sheetId, sheetNumber, viewId);
            f.Observed = observed; f.Expected = expected;
            return f;
        }

        private static PlanimetryFinding Unknown(PlanimetrySnapshot snap, string ruleId, string entity, long id,
                                                 long? sheetId, string sheetNumber, long? viewId, JObject observed)
        {
            var f = Base(snap, ruleId, "unknown", "unknown", entity, id, sheetId, sheetNumber, viewId);
            f.Observed = observed;
            f.Expected = new JObject
            {
                ["note"] = "This element was NOT examined by this check. Unknown is not a pass."
            };
            // coverage_complete stays the RUN's read coverage - it answers "did the auditor
            // see the whole model", not "did this check conclude". The latter is what
            // status=unknown says, and conflating the two would make every capability gap
            // look like a model that could not be read.
            return f;
        }

        private static PlanimetryFinding ForRule(PlanimetrySnapshot snap, PlanimetryRule rule, RuleTarget t,
                                                 string severity, string status)
        {
            return new PlanimetryFinding
            {
                RuleId = rule.Id,
                Severity = severity,
                Status = status,
                EntityKind = rule.Entity,
                SheetId = t.SheetId,
                SheetNumber = t.SheetNumber ?? snap.SheetNumberOf(t.SheetId),
                ViewId = t.ViewId,
                ElementIds = new List<long> { t.Id },
                CoverageComplete = snap.CoverageComplete,
                Fixable = false,
                Evidence = rule.Message == null ? new JObject() : new JObject { ["message"] = rule.Message }
            };
        }

        private static PlanimetryFinding FailForRule(PlanimetrySnapshot snap, PlanimetryRule rule, RuleTarget t,
                                                     JObject observed, JObject expected)
        {
            var f = ForRule(snap, rule, t, rule.Blocking ? "blocking" : "advisory", "failed");
            f.Observed = observed; f.Expected = expected;
            return f;
        }

        private static PlanimetryFinding UnknownForRule(PlanimetrySnapshot snap, PlanimetryRule rule, RuleTarget t,
                                                        JObject observed)
        {
            var f = ForRule(snap, rule, t, "unknown", "unknown");
            f.Observed = observed;
            f.Expected = new JObject
            {
                ["note"] = "This element was NOT examined by this rule. Unknown is not a pass."
            };
            return f;
        }

        /// <summary>Stamp the requirement set's identity onto every finding it produced.
        /// One pass, so a finding can never cite a set it did not come from.</summary>
        public static void Attribute(PlanimetryAuditResult result, PlanimetryRequirementSet set)
        {
            foreach (PlanimetryFinding f in result.Findings)
            {
                f.RequirementSetId = set.Id;
                f.RequirementSetVersion = set.Version;
                f.RequirementSetSha256 = set.Sha256;
            }
        }

        private static JToken Box(PlanBox b, PlanimetryRuleOptions opt)
        {
            double[] a = PlanimetryGeometry.ToDisplayArray(b, opt.ScaleFromFeet);
            return a == null ? (JToken)JValue.CreateNull() : new JArray(a.Select(v => (JToken)v));
        }

        private static double[] Point(PlanBox b, PlanimetryRuleOptions opt)
        {
            if (!b.Valid) return null;
            return new[]
            {
                PlanimetryGeometry.Display(b.CenterX, opt.ScaleFromFeet),
                PlanimetryGeometry.Display(b.CenterY, opt.ScaleFromFeet)
            };
        }

        private static void SetPoint(PlanimetryFinding f, double[] point, PlanimetryRuleOptions opt)
        {
            if (point == null) return;
            f.Point = point;
            f.Units = opt.Units;
            f.CoordinateSystem = f.EntityKind == "viewport" || f.EntityKind == "schedule_placement" ||
                                 f.EntityKind == "placement" || f.EntityKind == "sheet"
                ? "sheet" : "view_plane";
        }

        private static string Reasons(PlanimetryRow row)
        {
            if (row.Notes.Count == 0) return "the element could not be interrogated";
            return string.Join("; ", row.Notes.Select(n => n.Field + ": " + n.Reason));
        }

        private static string Reason(PlanimetryRow row, string field)
        {
            FieldNote n = row.Notes.FirstOrDefault(x => x.Field == field);
            return n == null ? "not read" : n.Reason;
        }
    }
}
