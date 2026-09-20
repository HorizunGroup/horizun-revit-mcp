// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// WHAT OF AN IFC THIS BRIDGE CAN REBUILD, decided by REPRESENTATION rather than
// by class name.
//
// The distinction is the whole point. "IfcWall is supported" is not true and
// never was: a wall exported with an Axis polyline and a swept solid is a Revit
// wall, and a wall exported as a faceted BREP by a modeller that had no wall
// concept is a lump of geometry with the word "wall" attached. The same class,
// two completely different answers. So the unit of decision here is the pair
//
//     (what the entity IS, how its shape is REPRESENTED)
//
// and every refusal names the representation it found, not just the class.
//
// THE SUPPORTED SET, and it is closed:
//
//   WALL     · an 'Axis' representation whose item is a 2-point polyline, plus
//              either a swept-solid height or a caller-supplied one → Revit wall.
//   COLUMN   · a 'Body' SweptSolid extruded along the placement's own +Z, or a
//              bare placement with a caller-supplied height → structural column
//              at the placement point, rotated by the placement's plan angle.
//   BEAM     · an 'Axis' 2-point polyline → structural framing between the two
//              points. The section comes from the Revit type, never from the IFC.
//   SLAB     · a 'Body' SweptSolid whose profile is a polygon and whose extrusion
//              is vertical → Revit floor on that polygon, with its holes.
//   OPENING  · an IfcOpeningElement voiding a planned wall, with a rectangular
//              extruded profile → wall opening between two world corners.
//   DOOR /
//   WINDOW   · filling a planned opening in a planned wall → hosted family
//              instance at the opening's centre. The symbol is the caller's.
//
// EVERYTHING ELSE IS NAMED AND REFUSED. Not omitted — named. An importer whose
// report lists only what it managed is an importer that drops a third of a
// building without anybody noticing, and the report is as much the deliverable
// as the model is.
//
// Revit-free. The decisions are about IFC, and they are provable without a
// building open.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What a product's shape turned out to be, once its representations were followed.</summary>
    public sealed class IfcShape
    {
        public IfcTransform Placement;            // world, millimetres
        public string PlacementRefusal;

        public double[] AxisStart;                // world millimetres, when there is a usable Axis
        public double[] AxisEnd;
        public string AxisRefusal;

        public IfcExtrusion Body;                 // when the Body is a single extruded area solid
        public string BodyRefusal;

        public List<string> RepresentationsFound = new List<string>();

        public bool HasAxis => AxisStart != null && AxisEnd != null;
        public bool HasBody => Body != null && Body.Usable;
    }

    /// <summary>The relationships that decide where an element belongs and what it is made of.</summary>
    public sealed class IfcRelations
    {
        /// <summary>element id → the IfcBuildingStorey entity that contains it.</summary>
        public readonly Dictionary<int, IfcEntity> StoreyOf = new Dictionary<int, IfcEntity>();

        /// <summary>host element id → the IfcOpeningElement entities cut into it.</summary>
        public readonly Dictionary<int, List<IfcEntity>> VoidsIn = new Dictionary<int, List<IfcEntity>>();

        /// <summary>opening id → its host element id.</summary>
        public readonly Dictionary<int, int> HostOfOpening = new Dictionary<int, int>();

        /// <summary>opening id → the element filling it (a door, a window).</summary>
        public readonly Dictionary<int, IfcEntity> FilledBy = new Dictionary<int, IfcEntity>();

        /// <summary>element id → the opening it fills.</summary>
        public readonly Dictionary<int, int> FillsOpening = new Dictionary<int, int>();

        /// <summary>element id → material name, as the file states it.</summary>
        public readonly Dictionary<int, string> MaterialOf = new Dictionary<int, string>();

        /// <summary>element id → its IfcTypeObject name (the exporter's type), when declared.</summary>
        public readonly Dictionary<int, string> TypeNameOf = new Dictionary<int, string>();
    }

    public static class IfcSubset
    {
        /// <summary>How far from vertical an extrusion may be and still count as vertical (radians of cosine).</summary>
        public const double VerticalTolerance = 1e-4;

        // =====================================================================
        // Shapes
        // =====================================================================

        /// <summary>
        /// Follow a product's ObjectPlacement and Representation, and report what was
        /// found. Never throws at a caller: every path that cannot continue leaves a
        /// refusal string in the field it belongs to.
        /// </summary>
        public static IfcShape Shape(IfcStepReader.Document ifc, IfcEntity product, double lengthScale)
        {
            var shape = new IfcShape();
            if (product == null) { shape.PlacementRefusal = "there is no product."; return shape; }

            string placementWhy;
            shape.Placement = IfcPlacement.World(ifc, ifc.Resolve(product.At(5)), lengthScale, out placementWhy);
            if (shape.Placement == null)
            {
                shape.PlacementRefusal = placementWhy ?? "the object placement could not be resolved.";
                // WITHOUT A PLACEMENT NOTHING ELSE MATTERS. Continuing with the identity
                // would land the element on the project origin, which is the failure mode
                // that looks most like success.
                return shape;
            }

            IfcEntity definition = ifc.Resolve(product.At(6));       // Representation
            if (definition == null)
            {
                shape.AxisRefusal = shape.BodyRefusal = "the element carries no product representation.";
                return shape;
            }

            foreach (string reference in IfcStepReader.List(definition.At(2)))   // Representations
            {
                IfcEntity representation = ifc.Resolve(reference);
                if (representation == null) continue;

                string identifier = IfcStepReader.Text(representation.At(1)) ?? "";
                string kind = IfcStepReader.Text(representation.At(2)) ?? "";
                shape.RepresentationsFound.Add(identifier + "/" + kind);

                if (string.Equals(identifier, "Axis", StringComparison.OrdinalIgnoreCase))
                    ReadAxis(ifc, representation, shape, lengthScale);
                else if (string.Equals(identifier, "Body", StringComparison.OrdinalIgnoreCase))
                    ReadBody(ifc, representation, kind, shape, lengthScale);
            }

            if (shape.AxisStart == null && shape.AxisRefusal == null)
                shape.AxisRefusal = shape.RepresentationsFound.Count == 0
                    ? "the element declares no representations at all."
                    : "the element has no 'Axis' representation; it declares " +
                      string.Join(", ", shape.RepresentationsFound) + ".";
            if (shape.Body == null && shape.BodyRefusal == null)
                shape.BodyRefusal = "the element has no 'Body' representation; it declares " +
                                    string.Join(", ", shape.RepresentationsFound) + ".";
            return shape;
        }

        private static void ReadAxis(IfcStepReader.Document ifc, IfcEntity representation,
                                     IfcShape shape, double lengthScale)
        {
            foreach (string item in IfcStepReader.List(representation.At(3)))
            {
                IfcEntity candidate = ifc.Resolve(item);
                if (candidate == null) continue;

                if (!string.Equals(candidate.Type, "IFCPOLYLINE", StringComparison.OrdinalIgnoreCase))
                {
                    shape.AxisRefusal = "the 'Axis' representation carries a " + candidate.Type +
                                        " rather than a polyline.";
                    continue;
                }

                IReadOnlyList<string> points = IfcStepReader.List(candidate.At(0));
                if (points.Count < 2) { shape.AxisRefusal = "the axis polyline has fewer than two points."; continue; }
                if (points.Count > 2)
                {
                    shape.AxisRefusal = "the axis polyline has " + points.Count + " points, so this is not a " +
                                        "straight run. Splitting it into segments would invent joins the IFC " +
                                        "does not describe, and joining them into one curve would invent a shape.";
                    continue;
                }

                double[] a = IfcPlacement.Point(ifc, points[0], lengthScale);
                double[] b = IfcPlacement.Point(ifc, points[1], lengthScale);
                if (a == null || b == null) { shape.AxisRefusal = "an axis point is not a cartesian point."; continue; }

                shape.AxisStart = shape.Placement.Apply(a);
                shape.AxisEnd = shape.Placement.Apply(b);
                shape.AxisRefusal = null;

                double dx = shape.AxisEnd[0] - shape.AxisStart[0], dy = shape.AxisEnd[1] - shape.AxisStart[1];
                if (Math.Sqrt(dx * dx + dy * dy) < 1.0)
                {
                    shape.AxisRefusal = "the axis is shorter than a millimetre once placed.";
                    shape.AxisStart = shape.AxisEnd = null;
                }
                return;
            }
        }

        private static void ReadBody(IfcStepReader.Document ifc, IfcEntity representation, string kind,
                                     IfcShape shape, double lengthScale)
        {
            if (!string.Equals(kind, "SweptSolid", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(kind, "AdvancedSweptSolid", StringComparison.OrdinalIgnoreCase))
            {
                shape.BodyRefusal = "the 'Body' representation is of type '" + kind + "'. Only SweptSolid is " +
                                    "evaluated: a Brep, a Tessellation, a CSG or a MappedRepresentation is " +
                                    "named and left alone rather than approximated into something else.";
                return;
            }

            var items = IfcStepReader.List(representation.At(3));
            if (items.Count == 0) { shape.BodyRefusal = "the 'Body' representation has no items."; return; }
            if (items.Count > 1)
            {
                shape.BodyRefusal = "the 'Body' representation holds " + items.Count + " solids. Rebuilding one " +
                                    "of them would produce an element that is part of what the IFC describes.";
                return;
            }

            IfcExtrusion extrusion = IfcProfile.Extrusion(ifc, ifc.Resolve(items[0]), lengthScale);
            if (extrusion == null || !extrusion.Usable)
            {
                shape.BodyRefusal = extrusion?.Refusal ?? "the body solid could not be read.";
                return;
            }
            shape.Body = extrusion;
            shape.BodyRefusal = null;
        }

        // =====================================================================
        // Relationships
        // =====================================================================

        /// <summary>
        /// The four relationship families this subset needs, indexed in one pass.
        ///
        /// IFC states relationships as objectified entities rather than as attributes, so
        /// "which storey is this wall on" is a scan over IfcRelContainedInSpatialStructure
        /// and not a field. Indexing once turns a quadratic import into a linear one.
        /// </summary>
        public static IfcRelations Relations(IfcStepReader.Document ifc)
        {
            var relations = new IfcRelations();
            if (ifc == null) return relations;

            // Containment: RelatedElements(4), RelatingStructure(5)
            foreach (IfcEntity rel in ifc.Of("IFCRELCONTAINEDINSPATIALSTRUCTURE"))
            {
                IfcEntity structure = ifc.Resolve(rel.At(5));
                if (structure == null) continue;
                foreach (string reference in IfcStepReader.List(rel.At(4)))
                {
                    IfcEntity element = ifc.Resolve(reference);
                    if (element != null) relations.StoreyOf[element.Id] = structure;
                }
            }

            // Voids: RelatingBuildingElement(4), RelatedOpeningElement(5)
            foreach (IfcEntity rel in ifc.Of("IFCRELVOIDSELEMENT"))
            {
                IfcEntity host = ifc.Resolve(rel.At(4));
                IfcEntity opening = ifc.Resolve(rel.At(5));
                if (host == null || opening == null) continue;
                List<IfcEntity> openings;
                if (!relations.VoidsIn.TryGetValue(host.Id, out openings))
                    relations.VoidsIn[host.Id] = openings = new List<IfcEntity>();
                openings.Add(opening);
                relations.HostOfOpening[opening.Id] = host.Id;
            }

            // Fills: RelatingOpeningElement(4), RelatedBuildingElement(5)
            foreach (IfcEntity rel in ifc.Of("IFCRELFILLSELEMENT"))
            {
                IfcEntity opening = ifc.Resolve(rel.At(4));
                IfcEntity filler = ifc.Resolve(rel.At(5));
                if (opening == null || filler == null) continue;
                relations.FilledBy[opening.Id] = filler;
                relations.FillsOpening[filler.Id] = opening.Id;
            }

            // Material: RelatedObjects(4), RelatingMaterial(5)
            foreach (IfcEntity rel in ifc.Of("IFCRELASSOCIATESMATERIAL"))
            {
                string name = MaterialName(ifc, ifc.Resolve(rel.At(5)), 0);
                if (string.IsNullOrWhiteSpace(name)) continue;
                foreach (string reference in IfcStepReader.List(rel.At(4)))
                {
                    IfcEntity element = ifc.Resolve(reference);
                    if (element != null) relations.MaterialOf[element.Id] = name;
                }
            }

            // Type: RelatedObjects(4), RelatingType(5)
            foreach (IfcEntity rel in ifc.Of("IFCRELDEFINESBYTYPE"))
            {
                IfcEntity type = ifc.Resolve(rel.At(5));
                string name = type == null ? null : IfcStepReader.Text(type.At(2));
                if (string.IsNullOrWhiteSpace(name)) continue;
                foreach (string reference in IfcStepReader.List(rel.At(4)))
                {
                    IfcEntity element = ifc.Resolve(reference);
                    if (element != null) relations.TypeNameOf[element.Id] = name;
                }
            }

            return relations;
        }

        /// <summary>
        /// A material's name, through the one level of indirection IFC uses for layers and
        /// lists. A LAYER SET is reported by its FIRST layer's material and said to be
        /// one: a multi-layer IFC wall does not become a multi-layer Revit type, and
        /// pretending otherwise would map a 3-layer construction onto a single material.
        /// </summary>
        public static string MaterialName(IfcStepReader.Document ifc, IfcEntity material, int depth)
        {
            if (material == null || depth > 6) return null;
            switch ((material.Type ?? "").ToUpperInvariant())
            {
                case "IFCMATERIAL":
                    return IfcStepReader.Text(material.At(0));
                case "IFCMATERIALLAYER":
                    return MaterialName(ifc, ifc.Resolve(material.At(0)), depth + 1);
                case "IFCMATERIALLAYERSET":
                {
                    var layers = IfcStepReader.List(material.At(0));
                    if (layers.Count == 0) return null;
                    string first = MaterialName(ifc, ifc.Resolve(layers[0]), depth + 1);
                    return layers.Count == 1 ? first
                        : first + " (first of " + layers.Count + " layers; a layered IFC construction is not a " +
                          "Revit compound type and is not rebuilt as one)";
                }
                case "IFCMATERIALLAYERSETUSAGE":
                    return MaterialName(ifc, ifc.Resolve(material.At(0)), depth + 1);
                case "IFCMATERIALLIST":
                {
                    var materials = IfcStepReader.List(material.At(0));
                    return materials.Count == 0 ? null : MaterialName(ifc, ifc.Resolve(materials[0]), depth + 1);
                }
                case "IFCMATERIALCONSTITUENT":
                    return MaterialName(ifc, ifc.Resolve(material.At(2)), depth + 1);
                case "IFCMATERIALCONSTITUENTSET":
                {
                    var constituents = IfcStepReader.List(material.At(2));
                    return constituents.Count == 0 ? null : MaterialName(ifc, ifc.Resolve(constituents[0]), depth + 1);
                }
                case "IFCMATERIALPROFILE":
                    return MaterialName(ifc, ifc.Resolve(material.At(2)), depth + 1);
                case "IFCMATERIALPROFILESET":
                {
                    var profiles = IfcStepReader.List(material.At(2));
                    return profiles.Count == 0 ? null : MaterialName(ifc, ifc.Resolve(profiles[0]), depth + 1);
                }
                case "IFCMATERIALPROFILESETUSAGE":
                    return MaterialName(ifc, ifc.Resolve(material.At(0)), depth + 1);
                default:
                    return null;
            }
        }

        // =====================================================================
        // Measurements taken off a shape
        // =====================================================================

        /// <summary>
        /// The vertical extent of a body extrusion in world millimetres, or null when the
        /// extrusion is not vertical. This is how a wall and a column get their height
        /// from the file rather than from a caller's guess.
        /// </summary>
        public static double? VerticalHeight(IfcShape shape)
        {
            if (shape == null || !shape.HasBody) return null;
            double[] v = IfcProfile.WorldExtrusion(shape.Body, shape.Placement);
            if (v == null) return null;
            double horizontal = Math.Sqrt(v[0] * v[0] + v[1] * v[1]);
            double vertical = Math.Abs(v[2]);
            if (vertical < 1e-6) return null;
            if (horizontal > vertical * VerticalTolerance) return null;   // tilted: not a plain height
            return vertical;
        }

        /// <summary>Is this body a vertical extrusion of a horizontal polygon — a slab, in other words?</summary>
        public static bool IsHorizontalPlate(IfcShape shape, out string why)
        {
            why = null;
            if (shape == null || !shape.HasBody) { why = shape?.BodyRefusal ?? "there is no body."; return false; }

            double? height = VerticalHeight(shape);
            if (!height.HasValue)
            {
                why = "the body's extrusion is not vertical, so this is not a plate lying in plan. A sloped or " +
                      "horizontally extruded solid is a different element and is not rebuilt as a floor.";
                return false;
            }

            List<IfcLoop> loops = IfcProfile.WorldLoops(shape.Body, shape.Placement);
            if (loops == null || loops.Count == 0) { why = "the body has no usable profile."; return false; }

            foreach (IfcLoop loop in loops)
            {
                double min = loop.Points.Min(p => p[2]), max = loop.Points.Max(p => p[2]);
                if (max - min > 1.0)
                {
                    why = "the body's profile is not horizontal: its points span " +
                          IfcProfile.Round(max - min) + " mm vertically. A Revit floor takes a flat sketch.";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// A rectangular opening as two world corners, for a wall opening. Returns null
        /// with a reason for anything that is not a rectangle in the wall's plane: a
        /// circular or arbitrary opening cut as a rectangle is a hole in the wrong shape.
        /// </summary>
        public static double[][] RectangularOpening(IfcShape shape, out string why)
        {
            why = null;
            if (shape == null || !shape.HasBody) { why = shape?.BodyRefusal ?? "the opening has no body."; return null; }

            List<IfcLoop> loops = IfcProfile.WorldLoops(shape.Body, shape.Placement);
            if (loops == null || loops.Count != 1)
            {
                why = "the opening's profile is not a single loop.";
                return null;
            }
            if (loops[0].Points.Count != 4)
            {
                why = "the opening's profile has " + loops[0].Points.Count + " corners, so it is not a rectangle. " +
                      "Cutting a rectangle instead would put a hole of the wrong shape in somebody's wall.";
                return null;
            }

            double[] v = IfcProfile.WorldExtrusion(shape.Body, shape.Placement);
            if (v == null) { why = "the opening's extrusion could not be measured."; return null; }

            // The opening box: the profile's corners, swept by the extrusion. The two
            // world corners Revit's NewOpening wants are the extremes of that box.
            var points = new List<double[]>();
            foreach (double[] p in loops[0].Points)
            {
                points.Add(p);
                points.Add(new[] { p[0] + v[0], p[1] + v[1], p[2] + v[2] });
            }
            return new[]
            {
                new[] { points.Min(p => p[0]), points.Min(p => p[1]), points.Min(p => p[2]) },
                new[] { points.Max(p => p[0]), points.Max(p => p[1]), points.Max(p => p[2]) }
            };
        }

        /// <summary>The centre of a shape's body box in world millimetres, for a hosted placement.</summary>
        public static double[] BodyCentre(IfcShape shape)
        {
            if (shape == null || !shape.HasBody) return null;
            List<IfcLoop> loops = IfcProfile.WorldLoops(shape.Body, shape.Placement);
            if (loops == null || loops.Count == 0) return null;
            double[] v = IfcProfile.WorldExtrusion(shape.Body, shape.Placement) ?? new double[] { 0, 0, 0 };

            var points = new List<double[]>();
            foreach (double[] p in loops[0].Points)
            {
                points.Add(p);
                points.Add(new[] { p[0] + v[0], p[1] + v[1], p[2] + v[2] });
            }
            return new[]
            {
                (points.Min(p => p[0]) + points.Max(p => p[0])) / 2.0,
                (points.Min(p => p[1]) + points.Max(p => p[1])) / 2.0,
                (points.Min(p => p[2]) + points.Max(p => p[2])) / 2.0
            };
        }
    }
}
