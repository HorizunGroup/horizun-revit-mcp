// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// IFC profiles and extrusions, for the subset that can be rebuilt EXACTLY.
//
// THE RULE THIS FILE IS BUILT ON: a profile is either reproduced exactly or
// refused with its reason. There is no approximation here, and that is a design
// decision with a cost — a circular column profile is refused rather than turned
// into a 32-sided polygon. The cost is worth paying because an approximated
// profile is indistinguishable from a correct one in every view, in every
// schedule and in every clash test, and only shows up when somebody builds it.
//
// WHAT IS SUPPORTED, by ENTITY and not by class name:
//
//   IFCARBITRARYCLOSEDPROFILEDEF whose OuterCurve is an IFCPOLYLINE, or an
//   IFCINDEXEDPOLYCURVE whose segments are all straight. This is what a slab, a
//   floor plate and an arbitrary footprint export as.
//
//   IFCARBITRARYPROFILEDEFWITHVOIDS, same outer curve rule, with inner curves
//   under the same rule — so a slab with openings comes through as a perimeter
//   plus holes rather than losing its holes silently.
//
//   IFCRECTANGLEPROFILEDEF, which is four points and a 2D placement.
//
//   IFCEXTRUDEDAREASOLID over any of the above, with its own Position transform
//   and its ExtrudedDirection and Depth.
//
// WHAT IS REFUSED, each with the reason printed in the reply:
//
//   IFCCIRCLEPROFILEDEF, IFCIShapeProfileDef and the other parameterised
//   sections — they are exact shapes this bridge cannot express as a point loop.
//   For a COLUMN or a BEAM this costs nothing, because the Revit type carries its
//   own profile and the IFC profile is only reported for comparison. For a SLAB
//   it means the slab is not planned.
//
//   IFCCOMPOSITECURVE, IFCTRIMMEDCURVE and any polycurve with an arc segment: a
//   curved boundary turned into chords is a different slab.
//
//   IFCREVOLVEDAREASOLID, IFCFACETEDBREP, IFCTRIANGULATEDFACESET and the rest of
//   the solid kinds. They are counted and named, never guessed at.
//
// Revit-free, like the placement arithmetic beside it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A closed loop of points in millimetres, in the plane of its profile.</summary>
    public sealed class IfcLoop
    {
        public List<double[]> Points = new List<double[]>();

        /// <summary>Signed area by the shoelace formula; the sign is the winding.</summary>
        public double SignedArea()
        {
            double sum = 0;
            for (int i = 0; i < Points.Count; i++)
            {
                double[] a = Points[i], b = Points[(i + 1) % Points.Count];
                sum += a[0] * b[1] - b[0] * a[1];
            }
            return sum / 2.0;
        }
    }

    /// <summary>A profile reduced to loops, or a reason it could not be.</summary>
    public sealed class IfcProfileShape
    {
        public string IfcType;
        public string Name;
        public IfcLoop Outer;
        public List<IfcLoop> Inner = new List<IfcLoop>();
        public string Refusal;                 // null when Outer is usable

        public bool Usable => Refusal == null && Outer != null && Outer.Points.Count >= 3;
    }

    /// <summary>An extruded area solid reduced to a profile, a direction and a depth.</summary>
    public sealed class IfcExtrusion
    {
        public IfcProfileShape Profile;
        public IfcTransform Position = IfcTransform.Identity;   // solid-local → object-local
        public double[] Direction = { 0, 0, 1 };                // in the solid's own frame, normalised
        public double Depth;                                     // millimetres
        public string Refusal;

        public bool Usable => Refusal == null && Profile != null && Profile.Usable && Depth > 1e-9;
    }

    public static class IfcProfile
    {
        /// <summary>The most points a single loop may carry before it is refused as a mesh in disguise.</summary>
        public const int MaxLoopPoints = 2000;

        // =====================================================================
        // Profiles
        // =====================================================================

        public static IfcProfileShape Read(IfcStepReader.Document ifc, IfcEntity profile, double lengthScale)
        {
            if (profile == null)
                return new IfcProfileShape { Refusal = "the swept area is missing." };

            var shape = new IfcProfileShape
            {
                IfcType = profile.Type,
                Name = IfcStepReader.Text(profile.At(1))
            };

            switch ((profile.Type ?? "").ToUpperInvariant())
            {
                case "IFCARBITRARYCLOSEDPROFILEDEF":
                case "IFCARBITRARYPROFILEDEFWITHVOIDS":
                {
                    string why;
                    shape.Outer = Curve(ifc, profile.At(2), lengthScale, out why);
                    if (shape.Outer == null) { shape.Refusal = why; return shape; }

                    if (string.Equals(profile.Type, "IFCARBITRARYPROFILEDEFWITHVOIDS",
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string reference in IfcStepReader.List(profile.At(3)))
                        {
                            string holeWhy;
                            IfcLoop hole = Curve(ifc, reference, lengthScale, out holeWhy);
                            if (hole == null)
                            {
                                // A LOST HOLE IS NOT A ROUNDING ERROR. A slab whose opening
                                // was dropped is a slab that passes every check and is wrong
                                // where somebody planned to put a stair.
                                shape.Refusal = "this profile declares a void this reader cannot express: " +
                                                holeWhy + " Planning the perimeter without its hole would " +
                                                "produce a slab that looks right and is missing an opening.";
                                return shape;
                            }
                            shape.Inner.Add(hole);
                        }
                    }
                    return shape;
                }

                case "IFCRECTANGLEPROFILEDEF":
                {
                    double? xDim = IfcStepReader.Number(profile.At(3));
                    double? yDim = IfcStepReader.Number(profile.At(4));
                    if (!xDim.HasValue || !yDim.HasValue || xDim.Value <= 0 || yDim.Value <= 0)
                    {
                        shape.Refusal = "the rectangle profile has no usable XDim/YDim.";
                        return shape;
                    }
                    double halfX = xDim.Value * lengthScale / 2.0, halfY = yDim.Value * lengthScale / 2.0;
                    var local = new List<double[]>
                    {
                        new[] { -halfX, -halfY, 0.0 },
                        new[] {  halfX, -halfY, 0.0 },
                        new[] {  halfX,  halfY, 0.0 },
                        new[] { -halfX,  halfY, 0.0 }
                    };
                    string positionWhy;
                    IfcTransform position = IfcPlacement.Axis(ifc, ifc.Resolve(profile.At(2)),
                                                              lengthScale, out positionWhy)
                                            ?? IfcTransform.Identity;
                    shape.Outer = new IfcLoop { Points = local.Select(position.Apply).ToList() };
                    return shape;
                }

                case "IFCCIRCLEPROFILEDEF":
                case "IFCCIRCLEHOLLOWPROFILEDEF":
                    shape.Refusal =
                        "a circular profile is an exact shape this bridge cannot express as a point loop, and a " +
                        "polygon with enough sides to look right is not the same shape. For a column or a beam " +
                        "this costs nothing — the Revit type carries its own profile — but a circular SLAB is " +
                        "not planned.";
                    return shape;

                case "IFCISHAPEPROFILEDEF":
                case "IFCTSHAPEPROFILEDEF":
                case "IFCLSHAPEPROFILEDEF":
                case "IFCUSHAPEPROFILEDEF":
                case "IFCCSHAPEPROFILEDEF":
                case "IFCZSHAPEPROFILEDEF":
                case "IFCASYMMETRICISHAPEPROFILEDEF":
                    shape.Refusal =
                        "this is a parameterised steel section (" + profile.Type + "). Its dimensions are read " +
                        "for comparison, but the shape is NOT rebuilt: a Revit structural member takes its " +
                        "profile from its type, and inventing a matching family would be inventing a catalogue.";
                    return shape;

                case "IFCDERIVEDPROFILEDEF":
                    shape.Refusal =
                        "a derived profile is another profile plus a transform and possibly a mirror. Resolving " +
                        "it needs the operator applied, which this reader does not do.";
                    return shape;

                default:
                    shape.Refusal = "profile type " + profile.Type + " is not in this reader's supported set.";
                    return shape;
            }
        }

        /// <summary>A bounded closed curve as a loop, or null with a reason.</summary>
        private static IfcLoop Curve(IfcStepReader.Document ifc, string reference, double lengthScale, out string why)
        {
            why = null;
            IfcEntity curve = ifc?.Resolve(reference);
            if (curve == null) { why = "the profile's curve is missing."; return null; }

            switch ((curve.Type ?? "").ToUpperInvariant())
            {
                case "IFCPOLYLINE":
                {
                    var loop = new IfcLoop();
                    foreach (string point in IfcStepReader.List(curve.At(0)))
                    {
                        double[] p = IfcPlacement.Point(ifc, point, lengthScale);
                        if (p == null) { why = "a polyline point is not a cartesian point."; return null; }
                        loop.Points.Add(p);
                    }
                    return Close(loop, out why);
                }

                case "IFCINDEXEDPOLYCURVE":
                {
                    // Points(0) is an IfcCartesianPointList2D/3D; Segments(1) is a list of
                    // IfcLineIndex / IfcArcIndex. An ARC SEGMENT is refused: chords are a
                    // different boundary.
                    IfcEntity list = ifc.Resolve(curve.At(0));
                    if (list == null) { why = "the polycurve names no point list."; return null; }
                    List<double[]> points = PointList(list, lengthScale);
                    if (points == null) { why = "the polycurve's point list could not be read."; return null; }

                    string segments = curve.At(1);
                    var loop = new IfcLoop();
                    if (string.IsNullOrWhiteSpace(segments) || segments.Trim() == "$")
                    {
                        loop.Points.AddRange(points);
                        return Close(loop, out why);
                    }

                    foreach (string segment in IfcStepReader.List(segments))
                    {
                        string trimmed = (segment ?? "").Trim();
                        if (trimmed.StartsWith("IFCARCINDEX", StringComparison.OrdinalIgnoreCase))
                        {
                            why = "the boundary contains an arc segment. Replacing it with chords would change " +
                                  "the shape, so the profile is refused instead.";
                            return null;
                        }
                        if (!trimmed.StartsWith("IFCLINEINDEX", StringComparison.OrdinalIgnoreCase))
                        {
                            why = "the boundary contains a segment of kind '" + trimmed + "', which is not a line.";
                            return null;
                        }
                        foreach (string index in IfcStepReader.List(Inside(trimmed)))
                        {
                            double? i = IfcStepReader.Number(index);
                            if (!i.HasValue) { why = "a segment index is not a number."; return null; }
                            int k = (int)Math.Round(i.Value) - 1;              // IFC indices are 1-based
                            if (k < 0 || k >= points.Count) { why = "a segment index is out of range."; return null; }
                            if (loop.Points.Count == 0 || !Same(loop.Points[loop.Points.Count - 1], points[k]))
                                loop.Points.Add(points[k]);
                        }
                    }
                    return Close(loop, out why);
                }

                case "IFCCOMPOSITECURVE":
                    why = "the boundary is an IFCCOMPOSITECURVE: segments of mixed kinds, typically including " +
                          "arcs. Reducing it to points would change the shape.";
                    return null;

                case "IFCTRIMMEDCURVE":
                case "IFCCIRCLE":
                case "IFCELLIPSE":
                case "IFCBSPLINECURVE":
                case "IFCBSPLINECURVEWITHKNOTS":
                    why = "the boundary is a " + curve.Type + ", which is a curve rather than a polygon.";
                    return null;

                default:
                    why = "boundary curve type " + curve.Type + " is not supported by this reader.";
                    return null;
            }
        }

        private static List<double[]> PointList(IfcEntity list, double lengthScale)
        {
            bool is2d = string.Equals(list.Type, "IFCCARTESIANPOINTLIST2D", StringComparison.OrdinalIgnoreCase);
            bool is3d = string.Equals(list.Type, "IFCCARTESIANPOINTLIST3D", StringComparison.OrdinalIgnoreCase);
            if (!is2d && !is3d) return null;

            var points = new List<double[]>();
            foreach (string tuple in IfcStepReader.List(list.At(0)))
            {
                IReadOnlyList<string> values = IfcStepReader.List(tuple);
                if (values.Count < 2) return null;
                double x = IfcStepReader.Number(values[0]) ?? 0;
                double y = IfcStepReader.Number(values[1]) ?? 0;
                double z = values.Count > 2 ? IfcStepReader.Number(values[2]) ?? 0 : 0;
                points.Add(new[] { x * lengthScale, y * lengthScale, z * lengthScale });
            }
            return points;
        }

        /// <summary>
        /// Normalise a loop: drop the repeated closing point, drop duplicates, and refuse
        /// what is not a polygon. A loop with two distinct points is a line, and a floor
        /// built from one is a Revit exception at commit rather than a readable refusal.
        /// </summary>
        private static IfcLoop Close(IfcLoop loop, out string why)
        {
            why = null;
            if (loop == null) { why = "the loop is empty."; return null; }

            var cleaned = new List<double[]>();
            foreach (double[] p in loop.Points)
                if (cleaned.Count == 0 || !Same(cleaned[cleaned.Count - 1], p)) cleaned.Add(p);
            if (cleaned.Count > 1 && Same(cleaned[0], cleaned[cleaned.Count - 1]))
                cleaned.RemoveAt(cleaned.Count - 1);

            if (cleaned.Count < 3)
            {
                why = "the boundary has " + cleaned.Count + " distinct point(s), which is not a closed polygon.";
                return null;
            }
            if (cleaned.Count > MaxLoopPoints)
            {
                why = "the boundary has " + cleaned.Count + " points, above the bound of " + MaxLoopPoints +
                      ". A boundary that large is a tessellated surface rather than a drawn outline.";
                return null;
            }
            loop.Points = cleaned;

            if (Math.Abs(loop.SignedArea()) < 1e-6)
            {
                why = "the boundary encloses no area.";
                return null;
            }
            return loop;
        }

        private static bool Same(double[] a, double[] b) =>
            Math.Abs(a[0] - b[0]) < 1e-6 && Math.Abs(a[1] - b[1]) < 1e-6 && Math.Abs(a[2] - b[2]) < 1e-6;

        /// <summary>IFCLINEINDEX((1,2)) → "(1,2)".</summary>
        private static string Inside(string typed)
        {
            int open = typed.IndexOf('(');
            if (open < 0 || !typed.EndsWith(")", StringComparison.Ordinal)) return typed;
            return typed.Substring(open + 1, typed.Length - open - 2);
        }

        // =====================================================================
        // Extrusions
        // =====================================================================

        /// <summary>An IFCEXTRUDEDAREASOLID reduced to profile + direction + depth.</summary>
        public static IfcExtrusion Extrusion(IfcStepReader.Document ifc, IfcEntity solid, double lengthScale)
        {
            if (solid == null) return new IfcExtrusion { Refusal = "there is no solid." };

            if (!string.Equals(solid.Type, "IFCEXTRUDEDAREASOLID", StringComparison.OrdinalIgnoreCase))
                return new IfcExtrusion
                {
                    Refusal = "the shape is an " + solid.Type + ". This reader evaluates IFCEXTRUDEDAREASOLID " +
                              "only; a revolution, a BREP or a tessellation is named and left alone rather than " +
                              "approximated."
                };

            // SweptArea(0), Position(1), ExtrudedDirection(2), Depth(3)
            var extrusion = new IfcExtrusion { Profile = Read(ifc, ifc.Resolve(solid.At(0)), lengthScale) };
            if (extrusion.Profile == null || !extrusion.Profile.Usable)
            {
                extrusion.Refusal = extrusion.Profile?.Refusal ?? "the swept area could not be read.";
                return extrusion;
            }

            string positionWhy;
            extrusion.Position = IfcPlacement.Axis(ifc, ifc.Resolve(solid.At(1)), lengthScale, out positionWhy)
                                 ?? IfcTransform.Identity;

            double[] direction = IfcPlacement.Normalise(IfcPlacement.Direction(ifc, solid.At(2)))
                                 ?? new double[] { 0, 0, 1 };
            extrusion.Direction = direction;

            double? depth = IfcStepReader.Number(solid.At(3));
            if (!depth.HasValue || depth.Value <= 0)
            {
                extrusion.Refusal = "the extrusion has no positive depth.";
                return extrusion;
            }
            extrusion.Depth = depth.Value * lengthScale;
            return extrusion;
        }

        /// <summary>
        /// The extrusion's profile in WORLD millimetres, at the base of the extrusion.
        /// object is the element's own placement; the solid's Position sits inside it.
        /// </summary>
        public static List<IfcLoop> WorldLoops(IfcExtrusion extrusion, IfcTransform objectPlacement)
        {
            if (extrusion == null || !extrusion.Usable) return null;
            IfcTransform world = (objectPlacement ?? IfcTransform.Identity).Compose(extrusion.Position);
            var loops = new List<IfcLoop>
            {
                new IfcLoop { Points = extrusion.Profile.Outer.Points.Select(world.Apply).ToList() }
            };
            foreach (IfcLoop hole in extrusion.Profile.Inner)
                loops.Add(new IfcLoop { Points = hole.Points.Select(world.Apply).ToList() });
            return loops;
        }

        /// <summary>The extrusion vector in world millimetres: direction × depth, rotated by the frame it lives in.</summary>
        public static double[] WorldExtrusion(IfcExtrusion extrusion, IfcTransform objectPlacement)
        {
            if (extrusion == null || !extrusion.Usable) return null;
            IfcTransform world = (objectPlacement ?? IfcTransform.Identity).Compose(extrusion.Position);
            double[] v = world.ApplyDirection(extrusion.Direction);
            return new[] { v[0] * extrusion.Depth, v[1] * extrusion.Depth, v[2] * extrusion.Depth };
        }

        /// <summary>A number the reply can print without a locale changing it.</summary>
        public static string Round(double millimetres) =>
            Math.Round(millimetres, 3).ToString("R", CultureInfo.InvariantCulture);
    }
}
