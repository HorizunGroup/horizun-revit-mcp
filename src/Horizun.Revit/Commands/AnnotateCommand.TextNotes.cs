// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_annotate operation=text, verified ON PAPER (5.2, field feedback from construction
// details documented in Revit 2024). The API accepted every one of these notes and re-read
// what was written, while the sheet showed something else. So a text note now also carries:
//
//   width_mm          the box width in PAPER millimetres (TextElement.Width is paper feet),
//                     refused outside the type's GetMinimumAllowedWidth/GetMaximumAllowedWidth;
//   expected_lines    the lines that must PRINT. The note's paper height (TextElement.Height)
//                     is converted to lines with a one-line and a two-line probe of the same
//                     type, created and rolled back inside the rehearsal; the view box (checked
//                     for the scale trap, AnnotationBoxReader) is a second, independent count;
//   leader_target     the arrow aimed by a ray into the model (LeaderTargetReader: host, links,
//                     nested links) from a point, or at a named element (host or link); a ray
//                     that meets another element first is refused naming it, and the element
//                     reached is reported;
//   leader_side       which edge the leader leaves from; an end inside the box span is refused,
//                     the elbow is set outside the box and re-read there (with the elbow inside
//                     the box Revit draws the arrow from the other side, across the sheet);
//   visibility        a view whose annotation categories (or Text Notes) are hidden gets a
//                     warning in the rehearsal: the note would be created and never seen.
//
// The rules are Core/TextNoteLayoutRules; everything here reads or writes Revit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class AnnotateCommand
    {
        private const double MmFt = 1.0 / 304.8;

        private sealed class TextLayout
        {
            /// <summary>Any of width_mm, expected_lines, leader_target was asked: the note is created by CreateText.</summary>
            public bool Any;
            public double? WidthFeet; public double? MinWidthFeet, MaxWidthFeet;
            public int? ExpectedLines; public int HardLines = 1;
            public bool HasLeader; public string Side = "auto"; public string Attachment; public string TargetForm;
            public XYZ End; public bool EndAimed; public XYZ EndSet;
            public long? TargetElementId, TargetLinkedId; public RevitLinkInstance TargetLink; public bool TargetInDirectLink;
            public string PlannedLeaderIdentity;
            public LeaderHit Hit; public View3D RayView; public bool RayNotApplicable;
            public string VisibilityWarning;
            public double? OneLineFeet, PitchFeet, OneLineBoxFeet, PitchBoxFeet; public string CalibrationNote;
            public double? BoxHeightPaperFeet; public string BoxMethod;
            public string ResolvedSide;
            public JObject Evidence;
        }

        /// <summary>The paper width (or height), in millimetres, of the view's ACTIVE crop (crop size over the scale); null with no active crop, on a sheet or a schedule.</summary>
        private static double? CropPaperMm(View v, bool height)
        {
            try
            {
                if (v is ViewSheet || v is ViewSchedule || !v.CropBoxActive) return null;
                BoundingBoxXYZ cb = v.CropBox;
                if (cb == null) return null;
                double w = (height ? cb.Max.Y - cb.Min.Y : cb.Max.X - cb.Min.X) * 304.8 / SafeScale(v);
                return w > 0 ? w : (double?)null;
            }
            catch { return null; }
        }

        /// <summary>The widest title block on the sheet, in paper millimetres (its box in the sheet view); the ARCH E long edge when none can be read.</summary>
        private static double SheetPaperWidthMm(Document doc, ViewSheet sheet)
        {
            double best = 0;
            try
            {
                foreach (Element tb in new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
                {
                    BoundingBoxXYZ bb = tb.get_BoundingBox(sheet);
                    if (bb != null) best = Math.Max(best, (bb.Max.X - bb.Min.X) * 304.8);
                }
            }
            catch { best = 0; }
            return best > 0 ? best : TextNoteLayoutRules.SheetPaperLimitMm;
        }

        private static double SafeScale(View v) { try { return Math.Max(1, v.Scale); } catch { return 1; } }

        private static XYZ OnViewPlane(View v, XYZ p) => p - v.ViewDirection * (p - v.Origin).DotProduct(v.ViewDirection);

        private static double[] Mm3(XYZ p) => p == null ? null : new[] { Math.Round(p.X * 304.8, 3), Math.Round(p.Y * 304.8, 3), Math.Round(p.Z * 304.8, 3) };

        // ---- planning (read only) ------------------------------------------------------------------

        private static readonly string[] LeaderTargetKeys = { "point", "element_id", "link_instance_id", "linked_element_id" };

        private static TextLayout PlanText(Document doc, Plan p, JObject a, double scale)
        {
            var t = new TextLayout { HardLines = TextNoteLayoutRules.HardLineCount(p.Text) };
            t.Any = a["width_mm"] != null || a["expected_lines"] != null || a["leader_target"] != null;
            View view = p.View;
            double viewScale = SafeScale(view);

            // 5. A note nobody will see: said in the plan, the rehearsal and the apply.
            try
            {
                bool annotationsHidden = view.AreAnnotationCategoriesHidden;
                bool textHidden = false;
                try { textHidden = view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_TextNotes)); } catch { }
                t.VisibilityWarning = TextNoteLayoutRules.HiddenWarning(annotationsHidden, textHidden, false, view is View3D);
            }
            catch { }

            if ((a["leader_side"] != null || a["leader_attachment"] != null) && a["leader_target"] == null)
                throw new ArgumentException("leader_side and leader_attachment describe a leader: send leader_target too.");

            // allow_outside_crop: its type first (a wrong type says so), then a TRUE one needs the width it lifts the check of;
            // a false one asks for nothing and is ignored without width_mm.
            JToken outside = a["allow_outside_crop"];
            if (outside != null && outside.Type != JTokenType.Boolean) throw new ArgumentException("allow_outside_crop must be true or false.");
            p.AllowOutsideCrop = outside != null && outside.Value<bool>();
            if (p.AllowOutsideCrop && a["width_mm"] == null)
                throw new ArgumentException("allow_outside_crop: true on a text note only lifts the check of width_mm against the view's crop: send width_mm too, or remove it.");
            if (a["width_mm"] != null)
            {
                JToken w = a["width_mm"];
                if (w.Type != JTokenType.Integer && w.Type != JTokenType.Float) throw new ArgumentException("width_mm must be a number (paper millimetres).");
                t.MinWidthFeet = TextElement.GetMinimumAllowedWidth(doc, p.Type.Id);
                t.MaxWidthFeet = TextElement.GetMaximumAllowedWidth(doc, p.Type.Id);
                string problem = TextNoteLayoutRules.WidthProblem(w.Value<double>(), t.MinWidthFeet.Value, t.MaxWidthFeet.Value, viewScale);
                if (problem != null) throw new ArgumentException(problem);
                // The type accepts it, but does it fit the VIEW? (field: 2000 mm of paper in a 153 mm crop was accepted)
                double rotation = a["rotation_degrees"] != null && (a["rotation_degrees"].Type == JTokenType.Integer || a["rotation_degrees"].Type == JTokenType.Float) ? a.Value<double>("rotation_degrees") : 0.0;
                string frame = TextNoteLayoutRules.WidthFrameProblem(w.Value<double>(), viewScale, CropPaperMm(view, false), CropPaperMm(view, true), rotation,
                    view is ViewSheet sheet ? SheetPaperWidthMm(doc, sheet) : TextNoteLayoutRules.UncroppedPaperLimitMm, p.AllowOutsideCrop);
                if (frame != null) throw new ArgumentException(frame);
                t.WidthFeet = TextNoteLayoutRules.PaperFeet(w.Value<double>());
            }
            if (a["expected_lines"] != null)
            {
                JToken l = a["expected_lines"];
                if (l.Type != JTokenType.Integer || l.Value<long>() < 1 || l.Value<long>() > 500)
                    throw new ArgumentException("expected_lines must be an integer from 1 to 500.");
                t.ExpectedLines = l.Value<int>();
                if (t.ExpectedLines.Value < t.HardLines)
                    throw new ArgumentException("the text has " + (t.HardLines - 1) + " line break(s), so it prints at least " + t.HardLines +
                        " lines; expected_lines " + t.ExpectedLines.Value + " can never hold. Nothing was written.");
                if (t.ExpectedLines.Value > t.HardLines && t.WidthFeet == null)
                    throw new ArgumentException("expected_lines " + t.ExpectedLines.Value + " is more than the text's own " + t.HardLines +
                        " line(s): only a box width makes Revit wrap. Send width_mm (paper millimetres). Nothing was written.");
            }

            if (a["leader_target"] != null)
            {
                PlanLeader(doc, p, a, scale, t);
                CapturePlannedLeaderIdentity(t);
            }
            return t;
        }

        private static void PlanLeader(Document doc, Plan p, JObject a, double scale, TextLayout t)
        {
            JObject lt = a["leader_target"] as JObject ?? throw new ArgumentException("leader_target must be an object.");
            foreach (JProperty prop in lt.Properties())
                if (!LeaderTargetKeys.Contains(prop.Name))
                    throw new ArgumentException("leader_target does not take '" + prop.Name + "'; it takes " + string.Join(", ", LeaderTargetKeys) + ".");
            string error;
            string form = TextNoteLayoutRules.LeaderTargetForm(lt["point"] != null, lt["element_id"] != null,
                lt["link_instance_id"] != null, lt["linked_element_id"] != null, out error);
            if (form == null) throw new ArgumentException(error);
            t.HasLeader = true; t.TargetForm = form;
            t.Side = (a.Value<string>("leader_side") ?? "auto").ToLowerInvariant();
            if (t.Side != "auto" && t.Side != "left" && t.Side != "right") throw new ArgumentException("leader_side must be auto, left or right.");
            if (a["leader_attachment"] != null)
            {
                t.Attachment = a.Value<string>("leader_attachment").ToLowerInvariant();
                if (t.Attachment != "top" && t.Attachment != "middle" && t.Attachment != "bottom")
                    throw new ArgumentException("leader_attachment must be top, middle or bottom.");
            }
            View view = p.View;
            bool rayless = view is ViewSheet || view is ViewSchedule || view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.Legend;
            if (rayless && form != "point")
                throw new ArgumentException("view " + Rid.Value(view.Id) + " (" + view.ViewType + ") shows no model to aim a leader at: give leader_target.point " +
                    "alone, and nothing is claimed about what it points at. Nothing was written.");

            Element target = null; Document targetDoc = doc;
            if (form == "element")
            {
                long id = lt.Value<long>("element_id");
                target = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (target == null || target is View || target is ElementType)
                    throw new ArgumentException("leader_target.element_id " + id + " is not a model element of this document.");
                t.TargetElementId = id;
            }
            else if (form == "linked_element")
            {
                long instId = lt.Value<long>("link_instance_id"), linkedId = lt.Value<long>("linked_element_id");
                t.TargetLink = (Rid.CanRepresent(instId) ? doc.GetElement(Rid.Make(instId)) : null) as RevitLinkInstance
                    ?? throw new ArgumentException("leader_target.link_instance_id " + instId + " is not a Revit link instance of this document.");
                targetDoc = t.TargetLink.GetLinkDocument()
                    ?? throw new ArgumentException("link instance " + instId + " is not loaded, so nothing in it can be aimed at. Reload the link first.");
                target = Rid.CanRepresent(linkedId) ? targetDoc.GetElement(Rid.Make(linkedId)) : null;
                t.TargetInDirectLink = target != null && !(target is RevitLinkInstance);
                if (!t.TargetInDirectLink) target = null;
                t.TargetLinkedId = linkedId;
            }

            if (lt["point"] != null) t.End = OnViewPlane(view, Point(lt["point"], scale, false));
            if (rayless) { t.RayNotApplicable = true; return; }

            t.RayView = LeaderTargetReader.RayViewFor(view, null) ?? LeaderTargetReader.FindView3D(doc);
            if (t.End == null)
            {
                if (target == null)
                    throw new ArgumentException("linked_element_id " + t.TargetLinkedId + " is not an element of link '" + targetDoc.Title + "' itself; " +
                        "for an element of a NESTED link give leader_target.point on it too, and the ray decides. Nothing was written.");
                if (t.RayView == null && !(view is ViewPlan))
                    throw new ArgumentException("the model has no usable 3D view to cast the aiming ray in (a read does not create one), so the arrow " +
                        "cannot be aimed at an element. Give leader_target.point, or create a 3D view first. Nothing was written.");
                LeaderHit firstHit;
                t.End = AimAt(doc, view, t, target, out firstHit, out int samples);
                t.Hit = firstHit;
                if (t.End == null)
                    throw new ArgumentException("no sampled point on this element identifies it as the FIRST thing a ray meets. " +
                        samples + " points tested for element " + Rid.Value(target.Id) +
                        (t.TargetLink == null ? "" : " in link instance " + Rid.Value(t.TargetLink.Id)) +
                        " in view " + Rid.Value(view.Id) + ". " +
                        (firstHit?.Uncertainty != null ? "First sample: " + firstHit.Uncertainty + ". " :
                         firstHit?.Source != null && !firstHit.DepthMeasured ? "First sample: " + firstHit.Note + ". " :
                         firstHit?.Source != null ? FormattableString.Invariant(
                             $"First sample reached {firstHit.Category} {firstHit.ElementId} at {firstHit.DistanceFeet * 304.8:0.##} mm along the view direction. ") :
                         TextNoteLayoutRules.AimMissDetail(samples)) + "Nothing was written.");
                t.EndAimed = true;
                return;
            }
            t.Hit = LeaderTargetReader.CastForLeader(doc, view, t.RayView, t.End);
            JudgeHit(t, form);
        }

        private static bool ConfirmsTarget(TextLayout t, LeaderHit h)
            => TextNoteLayoutRules.ConfirmsTarget(h.Cast, h.Coverage,
                t.TargetElementId.HasValue
                    ? h.Source == "host" && h.ElementId == t.TargetElementId
                    : h.Source == "link" && h.LinkInstanceId == Rid.Value(t.TargetLink.Id) &&
                      h.ElementId == t.TargetLinkedId && (!t.TargetInDirectLink || h.LinkDepth == 1));

        private static void JudgeHit(TextLayout t, string form)
        {
            LeaderHit h = t.Hit;
            string problem = TextNoteLayoutRules.LeaderRayProblem(form == "point", h.Cast, h.Coverage, h.Source != null, h.Uncertainty);
            if (problem != null) throw new ArgumentException(problem + " Nothing was written.");
            if (form != "point" && !ConfirmsTarget(t, h))
            {
                string expected = form == "element" ? "element " + t.TargetElementId
                    : "element " + t.TargetLinkedId + " of link instance " + Rid.Value(t.TargetLink.Id);
                throw new ArgumentException(TextNoteLayoutRules.HitMismatch(expected, h.Category, h.ElementId, h.TypeName,
                    h.ElementDocument?.Title, h.Source == "link" ? (long?)h.LinkInstanceId : null, h.LinkDepth));
            }
        }

        private static XYZ AimAt(Document doc, View view, TextLayout t, Element target, out LeaderHit firstHit, out int samples)
        {
            int tested = 0;
            var graphics = view is ViewPlan plan ? new LeaderTargetReader.PlanLinkGraphics(plan) : null;
            XYZ result = TextNoteLayoutRules.FirstAim(AimPoints(view, target, t.TargetLink, graphics), point =>
            {
                tested++;
                return LeaderTargetReader.CastForLeader(doc, view, t.RayView, point, graphics);
            }, hit => ConfirmsTarget(t, hit), out firstHit);
            samples = tested;
            return result;
        }

        private static IEnumerable<XYZ> AimPoints(View view, Element target, RevitLinkInstance link,
            LeaderTargetReader.PlanLinkGraphics graphics)
        {
            Transform elementToHost = link?.GetTotalTransform() ?? Transform.Identity;
            var points = new List<XYZ>();
            if (graphics != null && link != null)
            {
                try
                {
                    points.AddRange(graphics.AimPoints(link, target).Take(128).Select(p => OnViewPlane(view, p)));
                }
                catch { /* The shared reader reports unreadable output when the probe reaches a linked candidate. */ }
            }
            try
            {
                foreach (XYZ sample in LeaderTargetReader.GeometryAimPoints(target, view, link != null).Take(128))
                {
                    XYZ point = OnViewPlane(view, elementToHost.OfPoint(sample));
                    if (!points.Any(p => p.DistanceTo(point) < 1.0 / 304.8)) points.Add(point);
                }
            }
            catch { /* The box can still suggest points. Only the shared reader can prove a hit. */ }
            foreach (XYZ point in points) yield return point;
            BoundingBoxXYZ bb = null;
            try { bb = link == null ? (target.get_BoundingBox(view) ?? target.get_BoundingBox(null)) : target.get_BoundingBox(null); } catch { }
            if (bb == null) yield break;
            Transform toHost = elementToHost.Multiply(bb.Transform);
            XYZ right = view.RightDirection, up = view.UpDirection;
            double minR = double.MaxValue, minU = double.MaxValue, maxR = double.MinValue, maxU = double.MinValue;
            foreach (double x in new[] { bb.Min.X, bb.Max.X }) foreach (double y in new[] { bb.Min.Y, bb.Max.Y }) foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                    {
                        XYZ c = toHost.OfPoint(new XYZ(x, y, z));
                        minR = Math.Min(minR, c.DotProduct(right)); maxR = Math.Max(maxR, c.DotProduct(right));
                        minU = Math.Min(minU, c.DotProduct(up)); maxU = Math.Max(maxU, c.DotProduct(up));
                    }
            XYZ o = view.Origin;
            foreach (var f in TextNoteLayoutRules.AimFractions())
            {
                double r = minR + (maxR - minR) * f.Right, u = minU + (maxU - minU) * f.Up;
                yield return o + right * (r - o.DotProduct(right)) + up * (u - o.DotProduct(up));
            }
        }

        // ---- the rehearsal's paper calibration -----------------------------------------------------

        /// <summary>
        /// Per text type in the batch: the paper height of a one-line ("Hg") and a two-line ("Hg\rHg") note at the widest
        /// allowed box, by TextElement.Height and by the view box. Inside a sub-transaction that is rolled back, so no
        /// probe survives into the rows. A failure leaves the calibration null and the line count unmeasured.
        /// </summary>
        private static void CalibrateText(Document doc, List<Plan> plans)
        {
            var done = new Dictionary<long, TextLayout>();
            foreach (Plan p in plans)
            {
                p.Budget?.Check();
                if (p.Operation != "text" || p.TextPlan == null || !p.TextPlan.Any || p.TextPlan.ExpectedLines == null || p.Type == null) continue;
                long key = Rid.Value(p.Type.Id);
                TextLayout cal;
                if (!done.TryGetValue(key, out cal))
                {
                    cal = new TextLayout();
                    try
                    {
                        using (var st = new SubTransaction(doc))
                        {
                            st.Start();
                            try
                            {
                                double w = TextElement.GetMaximumAllowedWidth(doc, p.Type.Id);
                                var opts = new TextNoteOptions(p.Type.Id);
                                TextNote one = TextNote.Create(doc, p.View.Id, p.Point, w, "Hg", opts);
                                TextNote two = TextNote.Create(doc, p.View.Id, p.Point, w, "Hg\rHg", opts);
                                p.Budget?.Check();
                                doc.Regenerate();
                                p.Budget?.Check();
                                double h1 = one.Height, h2 = two.Height;
                                if (h1 > 0 && h2 > h1) { cal.OneLineFeet = h1; cal.PitchFeet = h2 - h1; }
                                else cal.CalibrationNote = "TextElement.Height did not grow with a second line (" + h1.ToString("0.######", CultureInfo.InvariantCulture) +
                                                           " / " + h2.ToString("0.######", CultureInfo.InvariantCulture) + " ft)";
                                double? b1 = PaperBoxHeight(one, p.View, out string m1), b2 = PaperBoxHeight(two, p.View, out string m2);
                                if (b1.HasValue && b2.HasValue && b2.Value > b1.Value) { cal.OneLineBoxFeet = b1; cal.PitchBoxFeet = b2.Value - b1.Value; }
                            }
                            finally { if (st.GetStatus() == TransactionStatus.Started) Guard.RollBack(st); }
                        }
                    }
                    catch (Exception ex) when (!(ex is AnnotationBudgetExceeded))
                    { cal.CalibrationNote = "the calibration probes could not be created here: " + ex.Message; }
                    done[key] = cal;
                }
                p.TextPlan.OneLineFeet = cal.OneLineFeet; p.TextPlan.PitchFeet = cal.PitchFeet;
                p.TextPlan.OneLineBoxFeet = cal.OneLineBoxFeet; p.TextPlan.PitchBoxFeet = cal.PitchBoxFeet;
                p.TextPlan.CalibrationNote = cal.CalibrationNote;
            }
        }

        /// <summary>The note's box height on PAPER (feet) from its view box, corrected for the scale trap; null when unreliable.</summary>
        private static double? PaperBoxHeight(TextNote n, View v, out string method)
        {
            double[] box = AnnotationBoxReader.ViewBox(n, v, out method);
            if (box == null) return null;
            return (box[3] - box[1]) / SafeScale(v);
        }

        // ---- creation -------------------------------------------------------------------------------

        private static TextNote CreateText(Document doc, Plan p)
        {
            TextLayout t = p.TextPlan;
            var options = new TextNoteOptions(p.Type.Id);
            if (p.Input["horizontal_alignment"] != null) options.HorizontalAlignment = ParseHorizontal(p.Input.Value<string>("horizontal_alignment"));
            if (p.Input["vertical_alignment"] != null) options.VerticalAlignment = ParseVertical(p.Input.Value<string>("vertical_alignment"));
            if (p.Input["rotation_degrees"] != null) options.Rotation = p.Input.Value<double>("rotation_degrees") * Math.PI / 180.0;
            TextNote note = t.WidthFeet.HasValue
                ? TextNote.Create(doc, p.View.Id, p.Point, t.WidthFeet.Value, p.Text, options)
                : TextNote.Create(doc, p.View.Id, p.Point, p.Text, options);
            t.BoxHeightPaperFeet = null; t.BoxMethod = null; t.EndSet = null; t.ResolvedSide = null;
            if (!t.HasLeader) return note;

            // The box BEFORE the leader (a leader widens the view box), then the side, then the leader.
            doc.Regenerate();
            string method;
            t.BoxHeightPaperFeet = PaperBoxHeight(note, p.View, out method); t.BoxMethod = method;
            double s = SafeScale(p.View);
            XYZ baseDir = note.BaseDirection;
            // The end lies on the note's own plane (in an isometric the screen position is what counts).
            XYZ end = t.End - p.View.ViewDirection * (t.End - note.Coord).DotProduct(p.View.ViewDirection);
            double[] span = TextNoteLayoutRules.BoxSpan(note.HorizontalAlignment.ToString(), note.Coord.DotProduct(baseDir) / s * 304.8, note.Width * 304.8);
            string error;
            string side = TextNoteLayoutRules.ResolveSide(t.Side, span[0], span[1], end.DotProduct(baseDir) / s * 304.8, TextNoteLayoutRules.ElbowClearanceMm, out error);
            if (side == null) throw new InvalidOperationException(error);
            if (t.Attachment != null)
            {
                LeaderAtachement att = t.Attachment == "top" ? LeaderAtachement.TopLine : t.Attachment == "bottom" ? LeaderAtachement.BottomLine : LeaderAtachement.Midpoint;
                if (side == "left") note.LeaderLeftAttachment = att; else note.LeaderRightAttachment = att;
            }
            Leader leader = note.AddLeader(side == "left" ? TextNoteLeaderTypes.TNLT_STRAIGHT_L : TextNoteLeaderTypes.TNLT_STRAIGHT_R);
            leader.End = end;
            doc.Regenerate();
            leader = note.GetLeaders()[0];
            XYZ anchor = leader.Anchor;
            double along = (anchor.DotProduct(baseDir) + end.DotProduct(baseDir)) / 2.0;
            leader.Elbow = anchor + baseDir * (along - anchor.DotProduct(baseDir));
            t.EndSet = end; t.ResolvedSide = side;
            return note;
        }

        // ---- verification ---------------------------------------------------------------------------

        /// <summary>Width, printed lines, the leader end, side, elbow and attachment of a created note, re-read. Fills TextPlan.Evidence.</summary>
        private static bool ReadTextLayout(Plan p, TextNote note, out Leader leader, out string reason)
        {
            leader = null;
            reason = null;
            TextLayout t = p.TextPlan;
            View v = p.View;
            double s = SafeScale(v);
            var ev = new JObject();
            var problems = new List<string>();

            double width = note.Width;
            var widthRow = new JObject { ["read_paper_mm"] = Math.Round(width * 304.8, 3) };
            if (t.WidthFeet.HasValue)
            {
                widthRow["requested_paper_mm"] = Math.Round(t.WidthFeet.Value * 304.8, 3);
                widthRow["allowed_paper_mm"] = new JArray(Math.Round((t.MinWidthFeet ?? 0) * 304.8, 3), Math.Round((t.MaxWidthFeet ?? 0) * 304.8, 3));
                bool wOk = TextNoteLayoutRules.WidthMatches(t.WidthFeet.Value, width);
                widthRow["match"] = wOk;
                if (!wOk) problems.Add("the note's width re-reads " + (width * 304.8).ToString("0.###", CultureInfo.InvariantCulture) + " mm on paper, not the requested " + (t.WidthFeet.Value * 304.8).ToString("0.###", CultureInfo.InvariantCulture) + " mm");
            }
            ev["width"] = widthRow;
            try { ev["wrapping_active"] = note.IsTextWrappingActive; } catch { }

            // Lines: by the paper height, and by the view box (only measurable without a leader, or captured before it).
            double height; try { height = note.Height; } catch { height = 0; }
            ev["box_height_paper_mm"] = Math.Round(height * 304.8, 3);
            int? byHeight = t.OneLineFeet.HasValue && t.PitchFeet.HasValue ? TextNoteLayoutRules.LinesFromHeight(height, t.OneLineFeet.Value, t.PitchFeet.Value) : null;
            // The view box is axis-aligned: a rotated note, or any note in a 3D view, cannot be counted by it.
            double baseDotRight; try { baseDotRight = note.BaseDirection.DotProduct(v.RightDirection); } catch { baseDotRight = double.NaN; }
            string boxSkip = TextNoteLayoutRules.BoxCountSkip(baseDotRight, v is View3D);
            double? boxHeight = boxSkip == null ? t.BoxHeightPaperFeet : null; string boxMethod = boxSkip == null ? t.BoxMethod : null;
            if (boxSkip == null && !t.HasLeader) boxHeight = PaperBoxHeight(note, v, out boxMethod);
            int? byBox = boxHeight.HasValue && t.OneLineBoxFeet.HasValue && t.PitchBoxFeet.HasValue
                ? TextNoteLayoutRules.LinesFromHeight(boxHeight.Value, t.OneLineBoxFeet.Value, t.PitchBoxFeet.Value) : null;
            bool linesOk;
            JObject lines = TextNoteLayoutRules.LinesVerdict(t.ExpectedLines, byHeight, byBox, t.HardLines, out linesOk);
            lines["box_count"] = boxSkip ?? (byBox.HasValue ? "measured" : "unavailable");
            lines["view_box"] = new JObject { ["method"] = boxMethod, ["height_paper_mm"] = boxHeight.HasValue ? (JToken)Math.Round(boxHeight.Value * 304.8, 3) : JValue.CreateNull() };
            if (boxSkip == null && t.HasLeader) lines["view_box"]["box_measured_before_leader"] = true;
            lines["calibration"] = t.OneLineFeet.HasValue
                ? (JToken)new JObject { ["one_line_paper_mm"] = Math.Round(t.OneLineFeet.Value * 304.8, 3), ["line_pitch_paper_mm"] = Math.Round(t.PitchFeet.Value * 304.8, 3),
                                        ["method"] = "one-line and two-line probes of this type, created and rolled back in the rehearsal" }
                : new JObject { ["unavailable"] = t.CalibrationNote ?? (t.ExpectedLines == null ? "not needed: no expected_lines" : "no calibration was taken") };
            ev["lines"] = lines;
            if (!linesOk) problems.Add(lines.Value<string>("lines_reason"));

            if (t.HasLeader)
            {
                IList<Leader> leaders = note.GetLeaders();
                var lj = new JObject { ["requested_side"] = t.Side, ["side"] = t.ResolvedSide, ["leaders"] = leaders.Count, ["form"] = t.TargetForm, ["aimed_at_element_box"] = t.EndAimed };
                if (leaders.Count != 1) problems.Add("the note has " + leaders.Count + " leader(s), not 1");
                else
                {
                    Leader l = leader = leaders[0];
                    XYZ baseDir = note.BaseDirection;
                    double[] span = TextNoteLayoutRules.BoxSpan(note.HorizontalAlignment.ToString(), note.Coord.DotProduct(baseDir) / s * 304.8, width * 304.8);
                    double anchorAlong = l.Anchor.DotProduct(baseDir) / s * 304.8, elbowAlong = l.Elbow.DotProduct(baseDir) / s * 304.8;
                    XYZ want = t.EndSet ?? t.End;
                    double dR = (l.End - want).DotProduct(v.RightDirection), dU = (l.End - want).DotProduct(v.UpDirection);
                    double endDev = Math.Sqrt(dR * dR + dU * dU) * 304.8;
                    bool endOk = endDev <= NoteLeaderRules.LeaderEndToleranceMm;
                    double mid = (span[0] + span[1]) / 2.0;
                    bool sideOk = t.ResolvedSide == "left" ? anchorAlong <= mid : anchorAlong >= mid;
                    bool elbowOk = !TextNoteLayoutRules.ElbowInsideBox(elbowAlong, span[0], span[1], TextNoteLayoutRules.ElbowClearanceMm);
                    lj["end_mm"] = new JArray(Mm3(l.End)); lj["end_deviation_mm"] = Math.Round(endDev, 4); lj["end_match"] = endOk;
                    lj["anchor_mm"] = new JArray(Mm3(l.Anchor)); lj["elbow_mm"] = new JArray(Mm3(l.Elbow));
                    lj["box_span_paper_mm"] = new JArray(Math.Round(span[0], 3), Math.Round(span[1], 3));
                    lj["anchor_on_requested_side"] = sideOk; lj["elbow_outside_box"] = elbowOk;
                    try { lj["shape"] = l.LeaderShape.ToString(); } catch { }
                    if (!endOk) problems.Add("the leader end re-reads " + endDev.ToString("0.###", CultureInfo.InvariantCulture) + " mm from where it was set");
                    if (!sideOk) problems.Add("the leader leaves from the " + (t.ResolvedSide == "left" ? "right" : "left") + " half of the note, not the " + t.ResolvedSide + " side: Revit flipped it");
                    if (!elbowOk) problems.Add("the leader elbow is inside the text box: Revit draws the arrow across the note");
                    if (t.Attachment != null)
                    {
                        LeaderAtachement read = t.ResolvedSide == "left" ? note.LeaderLeftAttachment : note.LeaderRightAttachment;
                        string readName = read == LeaderAtachement.TopLine ? "top" : read == LeaderAtachement.BottomLine ? "bottom" : "middle";
                        lj["attachment"] = readName;
                        if (readName != t.Attachment) problems.Add("leader_attachment re-reads " + readName + ", not " + t.Attachment);
                    }
                }
                lj["hit"] = HitJson(t);
                ev["leader"] = lj;
            }
            if (t.VisibilityWarning != null) ev["visibility_warning"] = t.VisibilityWarning;
            ev["checked_on"] = "paper: TextElement.Width/Height are paper feet; the view box is checked against the note's anchor (scale trap)";
            t.Evidence = ev;
            if (problems.Count == 0) return true;
            reason = string.Join("; ", problems);
            return false;
        }

        // ---- rows, plan and binding -------------------------------------------------------------------

        private static JObject TextPlanJson(Plan p)
        {
            TextLayout t = p.TextPlan;
            var o = new JObject { ["view"] = IdNameJson(p.View), ["hard_line_breaks"] = t.HardLines - 1 };
            if (t.WidthFeet.HasValue)
                o["width_paper_mm"] = new JObject
                {
                    ["requested"] = Math.Round(t.WidthFeet.Value * 304.8, 3),
                    ["allowed"] = new JArray(Math.Round((t.MinWidthFeet ?? 0) * 304.8, 3), Math.Round((t.MaxWidthFeet ?? 0) * 304.8, 3)),
                    ["model_length_at_view_scale_mm"] = Math.Round(t.WidthFeet.Value * 304.8 * SafeScale(p.View), 1)
                };
            if (t.ExpectedLines.HasValue) o["expected_lines"] = t.ExpectedLines.Value;
            if (t.HasLeader)
                o["leader"] = new JObject
                {
                    ["form"] = t.TargetForm, ["side"] = t.Side, ["attachment"] = t.Attachment,
                    ["end_mm"] = t.End == null ? null : new JArray(Mm3(t.End)), ["aimed_at_element_box"] = t.EndAimed,
                    ["hit"] = HitJson(t)
                };
            if (t.VisibilityWarning != null) o["visibility_warning"] = t.VisibilityWarning;
            return o;
        }

    }
}
