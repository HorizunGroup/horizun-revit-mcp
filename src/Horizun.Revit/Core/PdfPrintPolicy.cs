// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The PDF print policy: a closed, organisation-neutral contract over the
// options Revit's PDFExportOptions actually exposes, and the honest account of
// what happened to each one.
//
// Measured against the installed RevitAPI.dll of every supported year
// (2023-2027) with a metadata reader: PDFExportOptions carries the SAME 21
// properties in all five, and the only per-year member is
// Get/SetExportInBackground, which appears in 2025. So one policy serves every
// year, and export_in_background is refused - not ignored - where it does not
// exist.
//
// Every option ends in exactly one of four states, and the reply says which:
//
//   requested    the caller sent it (absent options are 'defaulted', never
//                silently requested on the caller's behalf);
//   applied      the value re-read from the option object after it was set;
//   verified     proved from the produced file - paper size, orientation and
//                combine are the only options a PDF page can testify to;
//   requested_unverifiable
//                passed to the exporter, read back as applied, and NOT claimed
//                as proved: colour depth, raster quality, DPI, hide flags,
//                zoom and placement leave no reliable trace PdfPig can read.
//   applied_mismatch
//                the option object read back a DIFFERENT value than the one
//                set. The export fails: an option the exporter silently
//                rewrote is exactly the kind of option this policy exists to
//                refuse.
//
// Two facts measured on Revit 2026 (2026-09-08) shape the contract:
//   * PaperPlacementType.Margins and PaperPlacementType.LowerLeft are the SAME
//     enum value (1); Margins reads back as LowerLeft. So placement offers
//     center|lower_left, and offsets apply to lower_left.
//   * export_in_background=true makes Document.Export return before the file
//     exists; this bridge verifies every file it reports, so a true value is
//     refused by name on every year rather than accepted and then reported as
//     "no file produced".
//
// Revit-free on purpose: the parsing, the expected page sizes, the geometry
// verdicts and the classification are unit-tested at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class PdfPrintPolicy
    {
        public const string Schema = "horizun.pdf-print-policy/1";

        public const string StatusRequested = "requested";
        public const string StatusDefaulted = "defaulted";
        public const string StatusVerified = "verified";
        public const string StatusVerifiedMismatch = "verified_mismatch";
        public const string StatusRequestedUnverifiable = "requested_unverifiable";
        public const string StatusAppliedMismatch = "applied_mismatch";
        public const string StatusNotApplicable = "not_applicable";

        /// <summary>Revit's own ExportPaperFormat names, the same 23 in 2023-2027.</summary>
        public static readonly string[] PaperFormats =
        {
            "Default", "ANSI_A", "ANSI_B", "ANSI_C", "ANSI_D", "ANSI_E",
            "ISO_A4", "ISO_A3", "ISO_A2", "ISO_A1", "ISO_A0",
            "ISO_B4", "ISO_B3", "ISO_B2", "ISO_B1",
            "ARCH_A", "ARCH_B", "ARCH_C", "ARCH_D", "ARCH_E", "ARCH_E1", "ARCH_E2", "ARCH_E3"
        };

        public static readonly string[] Orientations = { "portrait", "landscape", "auto" };
        /// <summary>center or lower_left. Revit's Margins is the same enum value as LowerLeft (measured), so it is not offered as a third thing.</summary>
        public static readonly string[] Placements = { "center", "lower_left" };
        public static readonly string[] ZoomTypes = { "fit_to_page", "zoom" };
        public static readonly string[] ColorDepths = { "black_line", "grayscale", "color" };
        public static readonly string[] RasterQualities = { "low", "medium", "high", "presentation" };
        public static readonly int[] ExportQualityDpis = { 72, 144, 300, 600, 1200, 2400, 3600, 4000 };

        /// <summary>The closed field set. Anything else is refused by name.</summary>
        public static readonly string[] Fields =
        {
            "paper_format", "orientation", "placement", "origin_offset_x", "origin_offset_y",
            "zoom", "zoom_percentage", "color_depth", "raster_quality", "export_quality_dpi",
            "always_use_raster", "hide_crop_boundaries", "hide_scope_boxes", "hide_reference_planes",
            "hide_unreferenced_view_tags", "mask_coincident_lines", "replace_halftone_with_thin_lines",
            "view_links_in_blue", "stop_on_error", "export_in_background"
        };

        /// <summary>The options a produced PDF page can testify to. Everything else is unverifiable.</summary>
        public static readonly string[] VerifiableFromOutput = { "paper_format", "orientation" };

        private const double PointsPerMm = 72.0 / 25.4;

        // Paper sizes in millimetres, portrait (width < height).
        private static readonly Dictionary<string, double[]> PaperMm = new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            { "ISO_A4", new[] { 210.0, 297.0 } }, { "ISO_A3", new[] { 297.0, 420.0 } },
            { "ISO_A2", new[] { 420.0, 594.0 } }, { "ISO_A1", new[] { 594.0, 841.0 } },
            { "ISO_A0", new[] { 841.0, 1189.0 } },
            { "ISO_B4", new[] { 250.0, 353.0 } }, { "ISO_B3", new[] { 353.0, 500.0 } },
            { "ISO_B2", new[] { 500.0, 707.0 } }, { "ISO_B1", new[] { 707.0, 1000.0 } },
            { "ANSI_A", new[] { 8.5 * 25.4, 11.0 * 25.4 } }, { "ANSI_B", new[] { 11.0 * 25.4, 17.0 * 25.4 } },
            { "ANSI_C", new[] { 17.0 * 25.4, 22.0 * 25.4 } }, { "ANSI_D", new[] { 22.0 * 25.4, 34.0 * 25.4 } },
            { "ANSI_E", new[] { 34.0 * 25.4, 44.0 * 25.4 } },
            { "ARCH_A", new[] { 9.0 * 25.4, 12.0 * 25.4 } }, { "ARCH_B", new[] { 12.0 * 25.4, 18.0 * 25.4 } },
            { "ARCH_C", new[] { 18.0 * 25.4, 24.0 * 25.4 } }, { "ARCH_D", new[] { 24.0 * 25.4, 36.0 * 25.4 } },
            { "ARCH_E", new[] { 36.0 * 25.4, 48.0 * 25.4 } }, { "ARCH_E1", new[] { 30.0 * 25.4, 42.0 * 25.4 } },
            { "ARCH_E2", new[] { 26.0 * 25.4, 38.0 * 25.4 } }, { "ARCH_E3", new[] { 27.0 * 25.4, 39.0 * 25.4 } }
        };

        // ---- The parsed policy -------------------------------------------------
        public string PaperFormat = "Default";
        public string Orientation = "auto";
        public string Placement = "center";
        public double? OriginOffsetXFeet, OriginOffsetYFeet;
        public string Zoom = "fit_to_page";
        public int? ZoomPercentage;
        public string ColorDepth = "color";
        public string RasterQuality = "high";
        public int ExportQualityDpi = 600;
        public bool AlwaysUseRaster, HideCropBoundaries = true, HideScopeBoxes = true, HideReferencePlanes = true,
                    HideUnreferencedViewTags = true, MaskCoincidentLines, ReplaceHalftoneWithThinLines,
                    ViewLinksInBlue, StopOnError = true, ExportInBackground;

        /// <summary>Exactly the fields the caller sent, in their requested form.</summary>
        public JObject Requested = new JObject();

        public bool IsRequested(string field) { return Requested[field] != null; }

        /// <summary>
        /// Parse pdf_print. Absent → the policy's own defaults with an empty Requested
        /// set. Every refusal names the field. hostYear gates the options that do not
        /// exist in every supported Revit.
        /// </summary>
        public static PdfPrintPolicy Parse(JToken token, string units, int hostYear)
        {
            var policy = new PdfPrintPolicy();
            if (token == null || token.Type == JTokenType.Null) return policy;
            JObject o = token as JObject;
            if (o == null) throw new ArgumentException("pdf_print must be an object.");
            double toFeet;
            if (!TryUnitScale(units, out toFeet)) throw new ArgumentException("pdf_print units must be mm, m or feet.");

            foreach (JProperty p in o.Properties())
                if (!Fields.Contains(p.Name))
                    throw new ArgumentException("pdf_print has an unknown field '" + p.Name + "'. Known fields: " +
                                                string.Join(", ", Fields) + ".");

            policy.PaperFormat = Choice(o, "paper_format", PaperFormats, policy.PaperFormat, false);
            policy.Orientation = Choice(o, "orientation", Orientations, policy.Orientation, true);
            policy.Placement = Choice(o, "placement", Placements, policy.Placement, true);
            policy.Zoom = Choice(o, "zoom", ZoomTypes, policy.Zoom, true);
            policy.ColorDepth = Choice(o, "color_depth", ColorDepths, policy.ColorDepth, true);
            policy.RasterQuality = Choice(o, "raster_quality", RasterQualities, policy.RasterQuality, true);

            if (o["export_quality_dpi"] != null)
            {
                int dpi = Integer(o, "export_quality_dpi");
                if (!ExportQualityDpis.Contains(dpi))
                    throw new ArgumentException("pdf_print.export_quality_dpi must be one of " + string.Join(", ", ExportQualityDpis) + ".");
                policy.ExportQualityDpi = dpi;
            }
            if (o["zoom_percentage"] != null)
            {
                int pct = Integer(o, "zoom_percentage");
                if (pct < 1 || pct > 1000) throw new ArgumentException("pdf_print.zoom_percentage must be 1..1000.");
                policy.ZoomPercentage = pct;
            }
            if (policy.Zoom == "zoom" && policy.ZoomPercentage == null)
                throw new ArgumentException("pdf_print.zoom='zoom' requires zoom_percentage; a zoom without a percentage is not a decision.");
            if (policy.Zoom == "fit_to_page" && policy.ZoomPercentage != null)
                throw new ArgumentException("pdf_print.zoom_percentage is meaningless with zoom='fit_to_page' and would be silently ignored; remove one.");

            foreach (string axis in new[] { "origin_offset_x", "origin_offset_y" })
            {
                if (o[axis] == null) continue;
                if (o[axis].Type != JTokenType.Integer && o[axis].Type != JTokenType.Float)
                    throw new ArgumentException("pdf_print." + axis + " must be a number in the requested units.");
                double v = (double)o[axis];
                if (double.IsNaN(v) || double.IsInfinity(v)) throw new ArgumentException("pdf_print." + axis + " must be finite.");
                if (axis == "origin_offset_x") policy.OriginOffsetXFeet = v * toFeet; else policy.OriginOffsetYFeet = v * toFeet;
            }
            bool anyOffset = policy.OriginOffsetXFeet.HasValue || policy.OriginOffsetYFeet.HasValue;
            if (anyOffset && !(policy.OriginOffsetXFeet.HasValue && policy.OriginOffsetYFeet.HasValue))
                throw new ArgumentException("pdf_print.origin_offset_x and origin_offset_y come together.");
            if (policy.Placement != "lower_left" && anyOffset)
                throw new ArgumentException("pdf_print.origin_offset_x/y apply only with placement='lower_left' (Revit's Margins placement is the same " +
                                            "value as LowerLeft) and would otherwise be silently ignored.");
            // Measured on Revit 2026 (c8b, 2026-09-08, rendered and looked at): with
            // placement=lower_left Revit fits the sheet to the WHOLE paper, not to the
            // printable area, and then shifts it by the offsets - a 10/20 mm offset on an
            // A3 landscape print of a 42x30 in sheet lost 20 mm at the top and 6 mm at
            // the right. A fit-to-page plus an offset is therefore a clipped print by
            // construction; offsets are honest only beside a zoom the caller chose.
            if (anyOffset && policy.Zoom != "zoom")
                throw new ArgumentException("pdf_print.origin_offset_x/y need zoom='zoom' with an explicit zoom_percentage: with fit_to_page Revit " +
                                            "fits the sheet to the whole paper and the offset pushes it off the page (measured on 2026: 10/20 mm " +
                                            "offsets clipped 20 mm at the top and 6 mm at the right of an A3 print). Choose the zoom that leaves " +
                                            "room for the offset, or drop the offsets.");

            policy.AlwaysUseRaster = Flag(o, "always_use_raster", policy.AlwaysUseRaster);
            policy.HideCropBoundaries = Flag(o, "hide_crop_boundaries", policy.HideCropBoundaries);
            policy.HideScopeBoxes = Flag(o, "hide_scope_boxes", policy.HideScopeBoxes);
            policy.HideReferencePlanes = Flag(o, "hide_reference_planes", policy.HideReferencePlanes);
            policy.HideUnreferencedViewTags = Flag(o, "hide_unreferenced_view_tags", policy.HideUnreferencedViewTags);
            policy.MaskCoincidentLines = Flag(o, "mask_coincident_lines", policy.MaskCoincidentLines);
            policy.ReplaceHalftoneWithThinLines = Flag(o, "replace_halftone_with_thin_lines", policy.ReplaceHalftoneWithThinLines);
            policy.ViewLinksInBlue = Flag(o, "view_links_in_blue", policy.ViewLinksInBlue);
            policy.StopOnError = Flag(o, "stop_on_error", policy.StopOnError);
            if (o["export_in_background"] != null)
            {
                bool background = Flag(o, "export_in_background", false);
                if (hostYear < 2025)
                    throw new ArgumentException("pdf_print.export_in_background does not exist in Revit " + hostYear +
                                                " (PDFExportOptions gained Get/SetExportInBackground in 2025); it would be " +
                                                "silently dropped, so it is refused. Remove it for this host.");
                if (background)
                    throw new ArgumentException("pdf_print.export_in_background=true is refused: measured on Revit 2026, a background " +
                                                "export returns from Document.Export before the file exists, and this bridge reports only " +
                                                "files it re-read. Export in the foreground (omit the option or send false).");
                policy.ExportInBackground = false;
            }

            policy.Requested = (JObject)o.DeepClone();
            return policy;
        }

        // ---- Expected geometry -------------------------------------------------

        /// <summary>Portrait [width, height] in points for a named format; null for Default.</summary>
        public static double[] ExpectedPointsPortrait(string paperFormat)
        {
            double[] mm;
            if (paperFormat == null || !PaperMm.TryGetValue(paperFormat, out mm)) return null;
            return new[] { mm[0] * PointsPerMm, mm[1] * PointsPerMm };
        }

        public static double MmToPoints(double mm) { return mm * PointsPerMm; }
        public static double FeetToPoints(double feet) { return feet * 304.8 * PointsPerMm; }

        /// <summary>
        /// Judge one produced page against the policy. sheetPoints is the source
        /// sheet's own [width,height] in points when known (Default format prints
        /// the sheet at its own size), otherwise null.
        /// </summary>
        public JObject VerifyPage(double pageWidthPoints, double pageHeightPoints, double[] sheetPoints,
                                  double tolerancePoints = 2.0, double[] titleblockPoints = null)
        {
            double[] expected = PaperFormat == "Default" ? sheetPoints : ExpectedPointsPortrait(PaperFormat);
            var verdict = new JObject
            {
                ["page_width_points"] = Math.Round(pageWidthPoints, 3),
                ["page_height_points"] = Math.Round(pageHeightPoints, 3),
                ["page_width_mm"] = Math.Round(pageWidthPoints / PointsPerMm, 2),
                ["page_height_mm"] = Math.Round(pageHeightPoints / PointsPerMm, 2),
                ["tolerance_points"] = tolerancePoints
            };
            // INSIDE THE PAPER IS NOT INSIDE THE TITLEBLOCK. With Default paper Revit
            // sizes the page to the sheet's outline, and the outline grows with
            // anything that sticks out of the titleblock - measured live 2026-09-08:
            // a sheet number overflowing its cell made the page 70.8 mm wider than
            // its ARCH E1 titleblock. A page larger than the titleblock is therefore
            // a composition defect the page geometry can prove, and it is one.
            if (titleblockPoints != null && titleblockPoints.Length == 2 && titleblockPoints[0] > 0 && titleblockPoints[1] > 0)
            {
                double tw = Math.Min(titleblockPoints[0], titleblockPoints[1]), th = Math.Max(titleblockPoints[0], titleblockPoints[1]);
                double pw = Math.Min(pageWidthPoints, pageHeightPoints), ph = Math.Max(pageWidthPoints, pageHeightPoints);
                double overW = pw - tw, overH = ph - th;
                bool exceeds = PaperFormat == "Default" && (overW > tolerancePoints || overH > tolerancePoints);
                verdict["titleblock_width_mm"] = Math.Round(tw / PointsPerMm, 2);
                verdict["titleblock_height_mm"] = Math.Round(th / PointsPerMm, 2);
                verdict["page_exceeds_titleblock"] = exceeds;
                if (exceeds)
                    verdict["page_exceeds_titleblock_reason"] = "the page is " + Mm(Math.Max(overW, 0)) + " x " +
                        Mm(Math.Max(overH, 0)) + " mm larger than the titleblock: something on the sheet " +
                        "(an overflowing label, a viewport, a text) lies outside the titleblock and enlarged the printed page";
            }
            if (expected == null)
            {
                verdict["paper_format"] = StatusRequestedUnverifiable;
                verdict["paper_format_reason"] = PaperFormat == "Default"
                    ? "Default prints the source at its own size and the source size was not readable"
                    : "no expected size is known for " + PaperFormat;
            }
            else
            {
                double ew = Math.Min(expected[0], expected[1]), eh = Math.Max(expected[0], expected[1]);
                double pw = Math.Min(pageWidthPoints, pageHeightPoints), ph = Math.Max(pageWidthPoints, pageHeightPoints);
                bool sizeMatches = Math.Abs(ew - pw) <= tolerancePoints && Math.Abs(eh - ph) <= tolerancePoints;
                verdict["expected_width_points"] = Math.Round(ew, 3);
                verdict["expected_height_points"] = Math.Round(eh, 3);
                verdict["paper_format"] = sizeMatches ? StatusVerified : StatusVerifiedMismatch;
                if (!sizeMatches)
                    verdict["paper_format_reason"] = "the produced page is " + Mm(pw) + "x" + Mm(ph) + " mm; " + PaperFormat +
                        " expects " + Mm(ew) + "x" + Mm(eh) + " mm";
            }
            bool landscape = pageWidthPoints > pageHeightPoints + tolerancePoints;
            bool portrait = pageHeightPoints > pageWidthPoints + tolerancePoints;
            verdict["page_orientation_observed"] = landscape ? "landscape" : (portrait ? "portrait" : "square");
            if (Orientation == "auto")
            {
                verdict["orientation"] = StatusRequestedUnverifiable;
                verdict["orientation_reason"] = "auto lets Revit choose per page; the observed orientation is reported, not judged";
            }
            else
            {
                bool ok = Orientation == "landscape" ? landscape : portrait;
                verdict["orientation"] = ok ? StatusVerified : StatusVerifiedMismatch;
                if (!ok) verdict["orientation_reason"] = "requested " + Orientation + ", produced " + verdict["page_orientation_observed"];
            }
            verdict["page_verified"] = verdict.Value<string>("paper_format") != StatusVerifiedMismatch &&
                                       verdict.Value<string>("orientation") != StatusVerifiedMismatch &&
                                       verdict.Value<bool?>("page_exceeds_titleblock") != true;
            return verdict;
        }

        // ---- The account ------------------------------------------------------

        /// <summary>
        /// One row per option: requested/defaulted, the applied value read back
        /// from the option object, and the verification status. pageVerdicts are
        /// the VerifyPage results of every produced page.
        /// </summary>
        public JObject Report(JObject applied, IEnumerable<JObject> pageVerdicts)
        {
            List<JObject> pages = (pageVerdicts ?? Enumerable.Empty<JObject>()).ToList();
            var rows = new JArray();
            bool allVerifiableHeld = true;
            foreach (string field in Fields)
            {
                var row = new JObject
                {
                    ["option"] = field,
                    ["source"] = IsRequested(field) ? StatusRequested : StatusDefaulted,
                    ["requested"] = IsRequested(field) ? Requested[field].DeepClone() : JValue.CreateNull(),
                    ["applied"] = applied != null && applied[field] != null ? applied[field].DeepClone() : JValue.CreateNull()
                };
                if (VerifiableFromOutput.Contains(field))
                {
                    if (pages.Count == 0)
                    {
                        row["status"] = StatusRequestedUnverifiable;
                        row["reason"] = "no produced page was inspected";
                    }
                    else
                    {
                        string[] statuses = pages.Select(p => p.Value<string>(field)).ToArray();
                        if (statuses.Any(s => s == StatusVerifiedMismatch)) { row["status"] = StatusVerifiedMismatch; allVerifiableHeld = false; }
                        else if (statuses.All(s => s == StatusVerified)) row["status"] = StatusVerified;
                        else row["status"] = StatusRequestedUnverifiable;
                        row["pages"] = new JArray(statuses);
                        string reason = pages.Select(p => p.Value<string>(field + "_reason")).FirstOrDefault(r => r != null);
                        if (reason != null) row["reason"] = reason;
                    }
                }
                else if (field == "export_in_background" && applied != null && applied[field] == null)
                {
                    row["status"] = StatusNotApplicable;
                    row["reason"] = "this host has no export-in-background option";
                }
                else if ((field == "origin_offset_x" || field == "origin_offset_y") && Placement != "lower_left")
                {
                    row["status"] = StatusNotApplicable;
                    row["reason"] = "offsets apply only with placement='lower_left'";
                }
                else if (applied != null && applied[field] != null && !AppliedMatches(field, applied[field]))
                {
                    // The exporter rewrote what was set. Reported as a mismatch and the
                    // export fails: a silently rewritten option is the failure mode this
                    // policy exists to catch.
                    row["status"] = StatusAppliedMismatch;
                    row["reason"] = "the option object read back " + applied[field].ToString(Newtonsoft.Json.Formatting.None) +
                                    " after " + Canonical()[field].ToString(Newtonsoft.Json.Formatting.None) + " was set";
                    allVerifiableHeld = false;
                }
                else if (field == "zoom_percentage" && Zoom != "zoom")
                {
                    row["status"] = StatusNotApplicable;
                    row["reason"] = "a percentage applies only with zoom='zoom'";
                }
                else
                {
                    row["status"] = StatusRequestedUnverifiable;
                    row["reason"] = "passed to the exporter and read back as applied; the produced PDF carries no " +
                                    "trace this bridge can read, so it is NOT claimed as proved";
                }
                rows.Add(row);
            }
            List<JObject> exceeding = pages.Where(p => p.Value<bool?>("page_exceeds_titleblock") == true).ToList();
            if (exceeding.Count > 0) allVerifiableHeld = false;
            return new JObject
            {
                ["schema"] = Schema,
                ["options"] = rows,
                ["pages"] = new JArray(pages.Select(p => p.DeepClone())),
                ["composition"] = new JObject
                {
                    ["pages_exceeding_titleblock"] = new JArray(exceeding.Select(p => new JObject
                    {
                        ["page"] = p["page"]?.DeepClone() ?? JValue.CreateNull(),
                        ["source_view_id"] = p["source_view_id"]?.DeepClone() ?? JValue.CreateNull(),
                        ["reason"] = p["page_exceeds_titleblock_reason"]?.DeepClone()
                    })),
                    ["held"] = exceeding.Count == 0,
                    ["means"] = "with Default paper the page follows the sheet outline; a page larger than its titleblock proves " +
                                "something on the sheet lies outside it (inside the paper is not inside the frame)"
                },
                ["verifiable_options_held"] = allVerifiableHeld,
                ["verified_from_output"] = new JArray(VerifiableFromOutput),
                ["means"] = "verified = proved from the produced page geometry; requested_unverifiable = applied to " +
                            "the exporter, not provable from the file; verified_mismatch = the file contradicts the request."
            };
        }

        /// <summary>Does the value the option object read back equal what the policy set?</summary>
        private bool AppliedMatches(string field, JToken applied)
        {
            JToken wanted = Canonical()[field];
            if (wanted == null || wanted.Type == JTokenType.Null) return true;
            if (field == "origin_offset_x" || field == "origin_offset_y")
            {
                double a, w;
                // The read-back arrives as a numeric token. It is compared as a number:
                // JValue.ToString() formats with the CURRENT culture, and on a host whose
                // decimal separator is a comma the invariant parse of that text fails and
                // every offset became an applied_mismatch with an empty reason (c7, 2026-09-08).
                if (applied.Type == JTokenType.Float || applied.Type == JTokenType.Integer) a = applied.Value<double>();
                else if (!double.TryParse(applied.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out a)) return false;
                w = field == "origin_offset_x" ? (OriginOffsetXFeet ?? 0) : (OriginOffsetYFeet ?? 0);
                return Math.Abs(a - w) <= 1e-9;
            }
            if (wanted.Type == JTokenType.Boolean || applied.Type == JTokenType.Boolean)
                return string.Equals(wanted.ToString(), applied.ToString(), StringComparison.OrdinalIgnoreCase);
            return string.Equals(wanted.ToString(), applied.ToString(), StringComparison.Ordinal);
        }

        /// <summary>The policy as a canonical object, so it can join a plan hash.</summary>
        public JObject Canonical()
        {
            var o = new JObject
            {
                ["paper_format"] = PaperFormat, ["orientation"] = Orientation, ["placement"] = Placement,
                ["origin_offset_x"] = OriginOffsetXFeet.HasValue ? (JToken)Math.Round(OriginOffsetXFeet.Value, 9) : JValue.CreateNull(),
                ["origin_offset_y"] = OriginOffsetYFeet.HasValue ? (JToken)Math.Round(OriginOffsetYFeet.Value, 9) : JValue.CreateNull(),
                ["zoom"] = Zoom, ["zoom_percentage"] = ZoomPercentage.HasValue ? (JToken)ZoomPercentage.Value : JValue.CreateNull(),
                ["color_depth"] = ColorDepth, ["raster_quality"] = RasterQuality, ["export_quality_dpi"] = ExportQualityDpi,
                ["always_use_raster"] = AlwaysUseRaster, ["hide_crop_boundaries"] = HideCropBoundaries,
                ["hide_scope_boxes"] = HideScopeBoxes, ["hide_reference_planes"] = HideReferencePlanes,
                ["hide_unreferenced_view_tags"] = HideUnreferencedViewTags, ["mask_coincident_lines"] = MaskCoincidentLines,
                ["replace_halftone_with_thin_lines"] = ReplaceHalftoneWithThinLines, ["view_links_in_blue"] = ViewLinksInBlue,
                ["stop_on_error"] = StopOnError, ["export_in_background"] = ExportInBackground
            };
            return o;
        }

        // ---- Helpers -----------------------------------------------------------
        /// <summary>Points to millimetres, one decimal, INVARIANT culture: a product message never carries the machine's decimal comma.</summary>
        private static string Mm(double points)
        {
            return Math.Round(points / PointsPerMm, 1).ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string Choice(JObject o, string field, string[] allowed, string fallback, bool lowerCase)
        {
            if (o[field] == null) return fallback;
            if (o[field].Type != JTokenType.String)
                throw new ArgumentException("pdf_print." + field + " must be one of " + string.Join(", ", allowed) + ".");
            string v = (string)o[field];
            if (lowerCase) v = v.ToLowerInvariant();
            if (!allowed.Contains(v))
                throw new ArgumentException("pdf_print." + field + " '" + (string)o[field] + "' is not one of " + string.Join(", ", allowed) + ".");
            return v;
        }

        private static bool Flag(JObject o, string field, bool fallback)
        {
            if (o[field] == null) return fallback;
            if (o[field].Type != JTokenType.Boolean) throw new ArgumentException("pdf_print." + field + " must be a boolean.");
            return (bool)o[field];
        }

        private static int Integer(JObject o, string field)
        {
            if (o[field].Type != JTokenType.Integer) throw new ArgumentException("pdf_print." + field + " must be an integer.");
            return (int)o[field];
        }

        private static bool TryUnitScale(string units, out double toFeet)
        {
            switch ((units ?? "mm").ToLowerInvariant())
            {
                case "mm": toFeet = 1.0 / 304.8; return true;
                case "m": toFeet = 1000.0 / 304.8; return true;
                case "feet": toFeet = 1.0; return true;
                default: toFeet = 0; return false;
            }
        }
    }
}
