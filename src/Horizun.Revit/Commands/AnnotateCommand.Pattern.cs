// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Surface-pattern joints (tile layouts) for horizun_annotate (backlog P4).
//
// A joint is referenced as '<face stable>/k', and k is not predictable from the
// pattern. '<face stable>/?' with a pattern point asks for the joint NEAREST that
// point, measured along the dimension line. Revit computes dimension values only
// after a commit (measured, see AnnotateCommand), so the search runs inside a
// TransactionGroup that is ALWAYS rolled back: a temporary reference plane through
// the point, one probe dimension per k (each in its own SubTransaction, so an index
// Revit refuses costs nothing), commit, regenerate, read every value, roll back.
// Whether the rollback was confirmed is checked; an unconfirmed one refuses the
// call as uncertain instead of carrying on.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class AnnotateCommand
    {
        /// <summary>
        /// Replace every '&lt;face&gt;/?' in <paramref name="stable"/> with the nearest joint
        /// '&lt;face&gt;/k'. pattern_points[j] is the point of the j-th '/?' entry. Records
        /// one resolution row per entry on the plan; throws with an actionable message.
        /// </summary>
        private static void ResolvePatternRequests(Document doc, Plan p, JObject a, List<string> stable, double scale)
        {
            var wanted = new List<int>();
            for (int i = 0; i < stable.Count; i++)
            {
                string face;
                if (stable[i] != null && DimensionPatternRules.IsNearestRequest(stable[i], out face)) wanted.Add(i);
            }
            JArray points = a["pattern_points"] as JArray;
            if (wanted.Count == 0)
            {
                if (points != null)
                    throw new ArgumentException("pattern_points is given but no reference ends in '/?'. Write the face " +
                                                "reference as '<face stable>/?' where the nearest joint is wanted.");
                return;
            }
            if (p.Operation != DimensionPlanRules.OpDimension)
                throw new ArgumentException("'<face>/?' (nearest pattern joint) is only for operation=dimension.");
            if (points == null || points.Count != wanted.Count)
                throw new ArgumentException("references has " + wanted.Count + " entr" + (wanted.Count == 1 ? "y" : "ies") +
                    " ending in '/?' but pattern_points has " + (points == null ? 0 : points.Count) +
                    ": give one point per '/?' entry, in order. Nothing was written.");

            XYZ x = Point(a["line_start"], scale, true), y = Point(a["line_end"], scale, true);
            XYZ n = p.View.ViewDirection.Normalize();
            XYZ along = y.Subtract(x);
            along = along.Subtract(n.Multiply(along.DotProduct(n)));
            if (along.GetLength() < 1e-9)
                throw new ArgumentException("the dimension line is perpendicular to the view; no joint can be measured along it.");
            along = along.Normalize();

            p.PatternResolutions = new JArray();
            for (int j = 0; j < wanted.Count; j++)
            {
                int i = wanted[j];
                string face;
                DimensionPatternRules.IsNearestRequest(stable[i], out face);
                XYZ probe = Point(points[j], scale, true);
                JObject row;
                int k = NearestJoint(doc, p.View, face, probe, along, i, out row);
                stable[i] = face + "/" + k.ToString(CultureInfo.InvariantCulture);
                row["reference_index"] = i;
                row["resolved"] = stable[i];
                p.PatternResolutions.Add(row);
            }
        }

        private static int NearestJoint(Document doc, View view, string face, XYZ probe, XYZ along, int index,
                                        out JObject evidence)
        {
            string field = "references[" + index + "]";
            try { Reference.ParseFromStableRepresentation(doc, face); }
            catch (Exception ex)
            { throw new ArgumentException(field + ": '" + face + "' does not parse as a face reference: " + ex.Message); }

            XYZ n = view.ViewDirection.Normalize();
            XYZ perp = n.CrossProduct(along).Normalize();
            // The temporary plane is NOT put through the point: a joint under the point
            // would then be a zero-length dimension, which Revit may refuse or report as
            // 0 - and the joint under the point is the commonest request. It goes outside
            // the face (or, when the face extent is unreadable, a small odd offset away),
            // each joint's POSITION is read from the probe's midpoint, and the distance
            // to the point is computed from that position.
            double probeS = probe.DotProduct(along);
            double fmin, fmax; string extentWhy;
            double planeS = DimensionPaper.TryFaceExtent(doc, face, along, out fmin, out fmax, out extentWhy)
                ? Math.Min(fmin, probeS) - DimensionPatternRules.ProbePlaneClearanceFeet
                : probeS - DimensionPatternRules.ProbePlaneFallbackOffsetFeet;
            XYZ planeAt = probe.Add(along.Multiply(planeS - probeS));
            Line line = Line.CreateBound(planeAt, planeAt.Add(along));
            var measured = new List<DimensionPatternRules.JointCandidate>();
            int tried = 0, unreadable = 0;
            string rollback;
            Exception searchFailure = null;
            using (var group = new TransactionGroup(doc, "Horizun: pattern joint search"))
            {
                group.Start();
                var probes = new Dictionary<int, ElementId>();
                try
                {
                    using (var tx = new Transaction(doc, "Horizun: pattern joint probes"))
                    {
                        tx.Start();
                        var opts = tx.GetFailureHandlingOptions();
                        opts.SetFailuresPreprocessor(new SilenceWarnings());
                        opts.SetClearAfterRollback(true);
                        tx.SetFailureHandlingOptions(opts);
                        ReferencePlane plane = doc.Create.NewReferencePlane(planeAt, planeAt.Add(perp), n, view);
                        Reference planeRef = plane.GetReference();
                        int lastHit = -1;
                        for (int k = 0; k < DimensionPatternRules.MaxIndex; k++)
                        {
                            if (k - lastHit > DimensionPatternRules.GiveUpAfterMisses) break;
                            tried++;
                            Reference joint;
                            try { joint = Reference.ParseFromStableRepresentation(doc, face + "/" + k.ToString(CultureInfo.InvariantCulture)); }
                            catch { continue; }
                            using (var st = new SubTransaction(doc))
                            {
                                st.Start();
                                try
                                {
                                    var ra = new ReferenceArray();
                                    ra.Append(planeRef); ra.Append(joint);
                                    Dimension d = doc.Create.NewDimension(view, line, ra);
                                    if (d == null || d.References.Size != 2) { st.RollBack(); continue; }
                                    st.Commit();
                                    probes[k] = d.Id;
                                    lastHit = k;
                                }
                                catch { try { if (st.GetStatus() == TransactionStatus.Started) st.RollBack(); } catch { } }
                            }
                        }
                        Guard.Commit(tx, "pattern joint probes");
                    }
                    using (var regen = new Transaction(doc, "Horizun: pattern joint values"))
                    {
                        regen.Start();
                        doc.Regenerate();
                        Guard.Commit(regen, "pattern joint values");
                    }
                    foreach (var kv in probes)
                    {
                        var d = doc.GetElement(kv.Value) as Dimension;
                        double? v = null, mid = null;
                        try { v = d?.Value; } catch { v = null; }
                        try { mid = d?.Origin.DotProduct(along); } catch { mid = null; }
                        DimensionPatternRules.JointCandidate c = DimensionPatternRules.FromProbe(kv.Key, planeS, v, mid, probeS);
                        if (c != null) measured.Add(c); else unreadable++;
                    }
                }
                catch (Exception ex) { searchFailure = ex; }
                finally
                {
                    rollback = Guard.RollBack(group).StatusName;
                }
            }
            if (searchFailure != null)
                throw new InvalidOperationException(field + ": the pattern joint search failed (" + searchFailure.Message +
                    "); search_rollback_status: '" + rollback + "'" +
                    (PlanFailure.IsConfirmedRollback(rollback)
                        ? ", nothing of the search remains in the model."
                        : " - NOT confirmed: the model may still hold probe dimensions. Re-read it before anything else."),
                    searchFailure);
            if (!PlanFailure.IsConfirmedRollback(rollback))
                throw new InvalidOperationException(field + ": the pattern joint search could not confirm its rollback " +
                    "(Revit reported '" + rollback + "'); the model may still hold probe dimensions. Re-read it before " +
                    "anything else.");

            string error;
            DimensionPatternRules.JointChoice best = DimensionPatternRules.Nearest(measured, DimensionPlanRules.CurveToleranceFeet, out error);
            if (best == null) throw new ArgumentException(field + ": " + error + " Nothing was written.");

            double min, max; string why;
            bool hasExtent = DimensionPaper.TryFaceExtent(doc, face, along, out min, out max, out why);
            bool hasPos = best.Position.HasValue;
            double pos = best.Position ?? 0;
            if (hasExtent && hasPos &&
                (pos < min - DimensionGeometryRules.DefaultToleranceFeet || pos > max + DimensionGeometryRules.DefaultToleranceFeet))
                throw new ArgumentException(field + ": the joint nearest the point (/" + best.K + ") lies OUTSIDE the " +
                    "patterned face along the dimension (a joint is an infinite line). Move the pattern point onto the " +
                    "face. Nothing was written.");

            XYZ jointPoint = hasPos ? probe.Add(along.Multiply(pos - probe.DotProduct(along))) : null;
            evidence = new JObject
            {
                ["face"] = face,
                ["k"] = best.K,
                ["same_joint_indices"] = new JArray(best.Aliases),
                ["exact"] = best.Exact,
                ["distance_internal_feet"] = best.Distance,
                ["joint_point_internal_feet"] = jointPoint == null ? null : new JArray(jointPoint.X, jointPoint.Y, jointPoint.Z),
                ["indices_tried"] = tried,
                ["joints_measured"] = measured.Count,
                ["probes_unreadable"] = unreadable,
                ["probe_plane"] = extentWhy == null ? "outside_face" : "offset_from_point (" + extentWhy + ")",
                ["inside_face"] = hasExtent && hasPos ? (JToken)true : JValue.CreateNull(),
                ["inside_face_note"] = hasExtent && hasPos ? null : (why ?? "joint position unreadable"),
                ["search_rollback_status"] = rollback
            };
            return best.K;
        }
    }
}
