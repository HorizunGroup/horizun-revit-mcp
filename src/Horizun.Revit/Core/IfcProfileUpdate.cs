// -----------------------------------------------------------------------------
// Horizun Revit MCP - editing a closed profile in place. Original Horizun code.
//
// THE PREVIOUS PASS DECLARED THIS UNSUPPORTED, and that was a not-yet-implemented
// wearing the word "unsupported". The two are different and the difference matters:
// `unsupported` tells a reader the thing cannot be done and ends the conversation,
// while the truth was that the API exists and nobody had written the call.
//
// WHAT WAS CHECKED, in the documentation Autodesk ships beside each RevitAPI.dll,
// for 2023 through 2027:
//
//   SketchEditScope(Document, string)                      present in all five
//   SketchEditScope.Start(ElementId)                       present in all five
//   SketchEditScope.IsSketchEditingSupported(ElementId)    present in all five
//   EditScope.Commit(IFailuresPreprocessor)                present in all five
//   Sketch.Profile, Sketch.SketchPlane, GetAllElements()   present in all five
//   Floor.SketchId                                         present in all five
//
// So the profile of a sketch-based element can be replaced, and this does it.
//
// REVIT ANSWERS "CAN THIS BE EDITED" ITSELF. IsSketchEditingSupported is a real
// predicate and it is asked before anything is attempted, so an element that
// genuinely cannot be edited is reported as a MEASURED limit - Revit said no -
// rather than as this build's opinion. That is the difference the brief asks for,
// made by asking rather than by asserting.
//
// WHAT IT STILL WILL NOT DO: guess. A profile is replaced only when the planned row
// carries a closed loop of points. A partial loop, a self-intersecting one or one
// Revit refuses is reported with what Revit said; nothing is straightened,
// simplified or closed on the element's behalf. A floor whose boundary was
// quietly "repaired" by an importer is worse than one that did not change.
//
// NOT RUN. Written against the published API and never executed: no Revit has been
// opened in this campaign.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class IfcProfileUpdate
    {
        private const double MmPerFoot = 304.8;

        /// <summary>Two points closer than this are the same point as far as a sketch is concerned.</summary>
        private const double MinimumSegmentMm = 1.0;

        /// <summary>
        /// Replace the closed profile of a sketch-based element.
        ///
        /// Returns the field outcome. It never throws at the caller: an update batch that
        /// dies on one element's geometry loses the report for every element after it.
        /// </summary>
        public static IfcFieldOutcome Replace(Document doc, Element element, JArray loop)
        {
            var outcome = new IfcFieldOutcome { Field = "profile" };

            if (doc == null || element == null)
            {
                outcome.State = IfcUpdate.Unsupported;
                outcome.Reason = "no element to edit.";
                return outcome;
            }

            List<XYZ> points = Points(loop);
            if (points == null)
            {
                outcome.State = IfcUpdate.Unsupported;
                outcome.Reason = "the planned row does not carry a usable closed loop: at least three points " +
                                 "are needed, each with three coordinates. Nothing is inferred from a partial " +
                                 "one - a boundary this import invented is a boundary nobody drew.";
                return outcome;
            }

            ElementId sketchId = SketchIdOf(element);
            if (sketchId == null || Rid.Value(sketchId) < 0)
            {
                outcome.State = IfcUpdate.Unsupported;
                outcome.Reason = "this element exposes no sketch, so it has no profile to replace. That is a " +
                                 "property of the element, not a gap in this build.";
                return outcome;
            }

            outcome.Was = Describe(doc, sketchId);

            var scope = new SketchEditScope(doc, "Horizun: IFC profile update");
            try
            {
                // REVIT'S OWN ANSWER, asked rather than assumed. A sketch can be uneditable
                // for reasons this code cannot see - a group, a part, a design option, an
                // element somebody else has borrowed - and asking is what makes the refusal
                // a MEASURED limit rather than this build's opinion.
                //
                // Called on the INSTANCE: the method takes one ElementId and no Document,
                // and the scope is the thing that was constructed with one. That reading is
                // from the signature rather than from a compiler, and it is among the first
                // things a build will settle.
                if (!scope.IsSketchEditingSupported(sketchId))
                {
                    outcome.State = IfcUpdate.Refused;
                    outcome.Reason = "Revit reports that this sketch cannot be edited. A sketch inside a " +
                                     "group, a part, or an element borrowed by somebody else answers this " +
                                     "way, and the answer is Revit's rather than this build's.";
                    return outcome;
                }

                scope.Start(sketchId);

                var sketch = doc.GetElement(sketchId) as Sketch;
                if (sketch == null)
                {
                    outcome.State = IfcUpdate.Refused;
                    outcome.Reason = "the sketch could not be read after the edit scope opened.";
                    return outcome;
                }

                SketchPlane plane = sketch.SketchPlane;
                if (plane == null)
                {
                    outcome.State = IfcUpdate.Refused;
                    outcome.Reason = "the sketch has no plane, so new curves cannot be placed on it.";
                    return outcome;
                }

                using (var tx = new Transaction(doc, "Horizun: replace profile"))
                {
                    if (tx.Start() != TransactionStatus.Started)
                    {
                        outcome.State = IfcUpdate.Refused;
                        outcome.Reason = "the profile transaction would not start.";
                        return outcome;
                    }

                    // THE OLD CURVES GO FIRST. Adding to a sketch that still holds its
                    // previous boundary produces two loops, which Revit rejects with a
                    // message about an invalid sketch rather than about what happened.
                    var toDelete = new List<ElementId>();
                    foreach (ElementId id in sketch.GetAllElements())
                        if (doc.GetElement(id) is CurveElement) toDelete.Add(id);
                    if (toDelete.Count > 0) doc.Delete(toDelete);

                    foreach (Curve segment in Segments(points, plane))
                        doc.Create.NewModelCurve(segment, plane);

                    if (tx.Commit() != TransactionStatus.Committed)
                    {
                        outcome.State = IfcUpdate.Refused;
                        outcome.Reason = "the profile transaction did not commit.";
                        return outcome;
                    }
                }

                // COMMITTING THE SCOPE IS WHERE REVIT VALIDATES THE SKETCH - a loop that
                // does not close, crosses itself, or leaves a hosted element stranded is
                // refused HERE, not above. The preprocessor rolls back rather than showing
                // a dialog: nobody is at the keyboard for an import.
                scope.Commit(new RefuseEverything());

                outcome.Now = Describe(doc, sketchId);
                outcome.State = IfcUpdate.Updated;
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.State = IfcUpdate.Refused;
                outcome.Reason = "Revit refused the new profile: " + ex.Message +
                                 " A loop that does not close, that crosses itself, or that would strand a " +
                                 "hosted element is refused at this point, and the element keeps the boundary " +
                                 "it had.";
                return outcome;
            }
            finally
            {
                // IsActive, not IsStarted: the documentation for 2023-2027 lists Cancel,
                // Commit and the IsActive property on EditScope and no IsStarted at all.
                // A scope that committed is no longer active, so this cancels only the one
                // that threw on the way.
                try { if (scope.IsActive) scope.Cancel(); } catch { }
            }
        }

        // =====================================================================

        /// <summary>The sketch of a sketch-based element, or null.</summary>
        private static ElementId SketchIdOf(Element element)
        {
            var floor = element as Floor;
            if (floor != null) return floor.SketchId;

            var wall = element as Wall;
            if (wall != null) return wall.SketchId;

            // A CLOSED LIST, deliberately. Reflecting over a `SketchId` property by name
            // would pick up anything that happens to have one, and a sketch edited on an
            // element this build never plans is an edit nobody asked for.
            return null;
        }

        /// <summary>The loop, in feet, or null when it is not a usable closed boundary.</summary>
        private static List<XYZ> Points(JArray loop)
        {
            if (loop == null || loop.Count < 3) return null;
            var points = new List<XYZ>();
            foreach (JToken token in loop)
            {
                var coordinate = token as JArray;
                if (coordinate == null || coordinate.Count < 3) return null;
                try
                {
                    points.Add(new XYZ(coordinate[0].Value<double>() / MmPerFoot,
                                       coordinate[1].Value<double>() / MmPerFoot,
                                       coordinate[2].Value<double>() / MmPerFoot));
                }
                catch { return null; }
            }

            // A loop written closed - last point equal to first - is the common IFC
            // spelling. The closing segment is created from the list, so the duplicate is
            // dropped rather than becoming a zero-length curve Revit refuses.
            if (points.Count > 3 &&
                points[0].DistanceTo(points[points.Count - 1]) * MmPerFoot < MinimumSegmentMm)
                points.RemoveAt(points.Count - 1);

            return points.Count >= 3 ? points : null;
        }

        /// <summary>The closed chain of lines, projected onto the sketch plane.</summary>
        private static IEnumerable<Curve> Segments(List<XYZ> points, SketchPlane plane)
        {
            // PROJECTED, because a sketch curve that leaves its plane is refused - and an
            // IFC boundary carries the elevation of the slab it came from, which is not
            // necessarily the plane Revit put the sketch on.
            Plane geometry = plane.GetPlane();
            var flat = points.Select(p => Project(geometry, p)).ToList();

            for (int i = 0; i < flat.Count; i++)
            {
                XYZ a = flat[i];
                XYZ b = flat[(i + 1) % flat.Count];
                if (a.DistanceTo(b) * MmPerFoot < MinimumSegmentMm) continue;   // a repeated point
                yield return Line.CreateBound(a, b);
            }
        }

        private static XYZ Project(Plane plane, XYZ point)
        {
            XYZ offset = point - plane.Origin;
            double distance = offset.DotProduct(plane.Normal);
            return point - distance * plane.Normal;
        }

        private static string Describe(Document doc, ElementId sketchId)
        {
            try
            {
                var sketch = doc.GetElement(sketchId) as Sketch;
                if (sketch == null || sketch.Profile == null) return null;
                int loops = 0, curves = 0;
                foreach (CurveArray array in sketch.Profile)
                {
                    loops++;
                    curves += array.Size;
                }
                return loops + " loop(s), " + curves + " curve(s)";
            }
            catch { return null; }
        }

        /// <summary>
        /// Refuses every failure rather than resolving it.
        ///
        /// NOBODY IS AT THE KEYBOARD during an import, and Revit's default resolutions are
        /// choices - deleting a hosted element, unjoining a wall - that a person would want
        /// to make themselves. Rolling back and reporting is the only honest answer.
        /// </summary>
        private sealed class RefuseEverything : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                return accessor.GetFailureMessages().Count > 0
                    ? FailureProcessingResult.ProceedWithRollBack
                    : FailureProcessingResult.Continue;
            }
        }
    }
}
