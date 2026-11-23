using System;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class AnnotateCommand
    {
        /// <summary>
        /// The same verification, with the reason it failed. "Could not measure the
        /// annotations" and "the tag is in the wrong place" are different findings and
        /// a caller cannot act on the first if both arrive as a bare false.
        /// </summary>
        private static bool Verify(Plan p, Element e, out string reason)
        {
            reason = null;
            // Revit re-encodes a note's line endings and appends a terminating '\r'
            // (measured on 2023: 'D7_PROBE' reads back 'D7_PROBE\r'), so the comparison
            // is on the normalised text - the rule and its evidence live in
            // DimensionPlanRules.StoredTextMatches, where they are unit-tested.
            if (p.Operation == "text")
            {
                TextNote note = e as TextNote;
                bool okText = note != null && DimensionPlanRules.StoredTextMatches(p.Text, note.Text);
                if (okText && note.GetTypeId() != p.Type.Id) okText = false;
                if (okText && p.Input["horizontal_alignment"] != null &&
                    note.HorizontalAlignment != ParseHorizontal(p.Input.Value<string>("horizontal_alignment"))) okText = false;
                if (okText && p.Input["vertical_alignment"] != null &&
                    note.VerticalAlignment != ParseVertical(p.Input.Value<string>("vertical_alignment"))) okText = false;
                if (okText && p.Input["expected_height_mm"] != null)
                {
                    Parameter size = note.TextNoteType?.get_Parameter(BuiltInParameter.TEXT_SIZE);
                    okText = size != null && size.HasValue &&
                        Math.Abs(size.AsDouble() * 304.8 - p.Input.Value<double>("expected_height_mm")) <= 0.1;
                }
                if (okText && p.Input["rotation_degrees"] != null)
                {
                    XYZ basis = note.BaseDirection;
                    double angle = Math.Atan2(basis.DotProduct(p.View.UpDirection),
                                              basis.DotProduct(p.View.RightDirection)) * 180.0 / Math.PI;
                    double expected = p.Input.Value<double>("rotation_degrees");
                    double delta = ((angle - expected + 540.0) % 360.0) - 180.0;
                    okText = Math.Abs(delta) <= 0.1;
                }
                if (!okText) { reason = "the created text note does not read back with the requested text"; return false; }
                // On PAPER: width, printed lines, the leader end, its side and its elbow (TextNoteLayoutRules).
                if (p.TextPlan != null && !VerifyTextLayout(p, note, out reason)) return false;
                return true;
            }
            return VerifyOtherAnnotation(p, e, out reason);
        }

        // The rehearsal and both verification stages use this entry point. In particular,
        // the post-commit verdict must depend on the model under the stored leader end.
        private static bool VerifyTextLayout(Plan p, TextNote note, out string reason)
        {
            bool layoutMatches = ReadTextLayout(p, note, out Leader leader, out reason);
            TextLayout text = p.TextPlan;
            if (leader == null || text.RayNotApplicable) return layoutMatches;

            LeaderHit hit = LeaderTargetReader.CastForLeader(note.Document, p.View, text.RayView, leader.End);
            text.Hit = hit;
            text.Evidence["leader"]["hit"] = HitJson(text);
            bool pointOnly = !text.TargetElementId.HasValue && !text.TargetLinkedId.HasValue;
            string targetProblem = pointOnly
                ? TextNoteLayoutRules.LeaderRayProblem(true, hit.Cast, hit.Coverage, hit.Source != null, hit.Uncertainty)
                : text.PlannedLeaderIdentity != null && text.PlannedLeaderIdentity == hit.TargetIdentity
                    ? null : "did not resolve to the planned target identity: " + (hit.Uncertainty ?? hit.Category ?? hit.Coverage);
            if (targetProblem == null) return layoutMatches;

            string finding = "the leader's re-read end " + (pointOnly ? "failed the ray check: " : "") + targetProblem;
            reason = reason == null ? finding : reason + "; " + finding;
            return false;
        }

        private static JObject HitJson(TextLayout t)
        {
            if (t.RayNotApplicable) return new JObject { ["coverage"] = "not_applicable", ["note"] = "this view shows no model; nothing is claimed about what the arrow points at" };
            LeaderHit h = t.Hit;
            JObject o = h == null ? new JObject { ["coverage"] = "not_cast" } : h.ToJson();
            if (h != null && h.Source != null)
            {
                o["document"] = h.ElementDocument?.Title;
                o["link_depth"] = h.LinkDepth;
            }
            if (h != null && (!h.Cast || h.Coverage != "complete")) o["proven"] = false;
            o["ray_view"] = t.RayView == null ? (JToken)JValue.CreateNull() : IdNameJson(t.RayView);
            return o;
        }

        private static void AddTextEvidence(JObject row, Plan p)
        {
            if (p.Operation != "text" || p.TextPlan == null) return;
            if (p.TextPlan.Evidence != null) row["text_layout"] = p.TextPlan.Evidence.DeepClone();
            if (p.TextPlan.VisibilityWarning != null) row["visibility_warning"] = p.TextPlan.VisibilityWarning;
        }
    }
}
