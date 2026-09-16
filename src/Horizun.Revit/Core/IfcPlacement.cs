// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// IFC placements, WITH their rotations.
//
// The first version of the IFC reader accumulated placement ORIGINS and declared
// rotations out of scope, on the grounds that a half-applied transform looks
// plausible and is wrong. That reasoning is right about half-applied transforms
// and wrong as a conclusion: a transform is not hard, it is just a basis and a
// translation, and refusing to apply it does not make the import correct — it
// makes every rotated storey, every rotated building and every rotated site
// silently wrong, in exactly the way that looks plausible.
//
// So this applies the whole thing:
//
//   IFCAXIS2PLACEMENT3D carries Location, Axis (the local +Z) and RefDirection
//   (which fixes +X). Either direction may be absent, and the defaults are the
//   global ones. RefDirection is NOT the X axis: it is projected off Axis and
//   normalised, because IFC allows a RefDirection that is not perpendicular to
//   Axis and a reader that uses it raw produces a non-orthogonal basis.
//
//   IFCAXIS2PLACEMENT2D carries Location and RefDirection only, in the XY plane.
//
//   IFCLOCALPLACEMENT chains: child relative to parent, up to the project. The
//   composition order is parent ∘ child, and getting it backwards puts the
//   building in the right place with the wrong orientation — which, again, looks
//   plausible.
//
//   IFCGRIDPLACEMENT is NOT supported and says so rather than being treated as
//   an identity: a grid placement resolved as identity puts the element at the
//   project origin.
//
// A CYCLE IN THE CHAIN IS A REAL FILE DEFECT, not a hypothetical: exporters have
// produced them. The walk is depth-bounded AND visits are recorded, so a cycle
// is reported as one rather than hanging inside Revit's UI thread.
//
// REVIT-FREE ON PURPOSE. Everything here is doubles, so the arithmetic can be
// tested without a building — which matters more here than anywhere else in this
// file tree, because these numbers decide where somebody's building lands.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// A rigid placement: an orthonormal basis and a translation, in millimetres.
    /// Rigid, not affine — IFC object placements do not scale, and accepting a
    /// scale here would let a mirrored or stretched placement through unnoticed.
    /// </summary>
    public sealed class IfcTransform
    {
        public double[] X = { 1, 0, 0 };
        public double[] Y = { 0, 1, 0 };
        public double[] Z = { 0, 0, 1 };
        public double[] Origin = { 0, 0, 0 };

        public static IfcTransform Identity => new IfcTransform();

        /// <summary>This transform applied to a local point, giving a world point.</summary>
        public double[] Apply(double[] local)
        {
            if (local == null) return (double[])Origin.Clone();
            return new[]
            {
                Origin[0] + X[0] * local[0] + Y[0] * local[1] + Z[0] * local[2],
                Origin[1] + X[1] * local[0] + Y[1] * local[1] + Z[1] * local[2],
                Origin[2] + X[2] * local[0] + Y[2] * local[1] + Z[2] * local[2]
            };
        }

        /// <summary>This transform applied to a local DIRECTION: the basis, without the translation.</summary>
        public double[] ApplyDirection(double[] local)
        {
            if (local == null) return new double[] { 0, 0, 1 };
            return new[]
            {
                X[0] * local[0] + Y[0] * local[1] + Z[0] * local[2],
                X[1] * local[0] + Y[1] * local[1] + Z[1] * local[2],
                X[2] * local[0] + Y[2] * local[1] + Z[2] * local[2]
            };
        }

        /// <summary>parent ∘ child: the child expressed in the parent's frame, then in the parent's parent's, and so on.</summary>
        public IfcTransform Compose(IfcTransform child)
        {
            if (child == null) return this;
            return new IfcTransform
            {
                X = ApplyDirection(child.X),
                Y = ApplyDirection(child.Y),
                Z = ApplyDirection(child.Z),
                Origin = Apply(child.Origin)
            };
        }

        /// <summary>The rotation about +Z in degrees, or null when the basis is tilted out of plan.</summary>
        public double? PlanRotationDegrees(double tolerance = 1e-6)
        {
            if (Math.Abs(Z[0]) > 1e-6 || Math.Abs(Z[1]) > 1e-6 || Z[2] < 0) return null;
            double angle = Math.Atan2(X[1], X[0]) * 180.0 / Math.PI;
            if (Math.Abs(angle) < tolerance) angle = 0;
            return angle;
        }

        /// <summary>Is this basis the global one, to within a tolerance?</summary>
        public bool IsAxisAligned(double tolerance = 1e-9)
        {
            return Math.Abs(X[0] - 1) < tolerance && Math.Abs(X[1]) < tolerance && Math.Abs(X[2]) < tolerance &&
                   Math.Abs(Y[0]) < tolerance && Math.Abs(Y[1] - 1) < tolerance && Math.Abs(Y[2]) < tolerance &&
                   Math.Abs(Z[0]) < tolerance && Math.Abs(Z[1]) < tolerance && Math.Abs(Z[2] - 1) < tolerance;
        }
    }

    public static class IfcPlacement
    {
        /// <summary>How deep a placement chain may be before it is called a defect.</summary>
        public const int MaxChain = 64;

        /// <summary>
        /// The world transform of an object placement, in millimetres. Returns null with a
        /// reason: an unsupported placement must not silently become the identity, because
        /// the identity is the project origin and a building at the project origin looks
        /// like a successful import.
        /// </summary>
        public static IfcTransform World(IfcStepReader.Document ifc, IfcEntity placement,
                                         double lengthScale, out string why)
        {
            why = null;
            var seen = new HashSet<int>();
            return Walk(ifc, placement, lengthScale, seen, 0, out why);
        }

        private static IfcTransform Walk(IfcStepReader.Document ifc, IfcEntity placement, double lengthScale,
                                         HashSet<int> seen, int depth, out string why)
        {
            why = null;
            if (placement == null) return IfcTransform.Identity;

            if (depth > MaxChain)
            {
                why = "the placement chain is deeper than " + MaxChain + " links, which no real building needs; " +
                      "this file's placements are defective and were not followed.";
                return null;
            }
            if (!seen.Add(placement.Id))
            {
                why = "the placement chain loops back on itself at #" + placement.Id + ". A cycle is a defect in " +
                      "the file, not a shape, and following it would hang Revit rather than produce a wrong answer.";
                return null;
            }

            if (string.Equals(placement.Type, "IFCGRIDPLACEMENT", StringComparison.OrdinalIgnoreCase))
            {
                why = "this element is placed on an IFC grid (IFCGRIDPLACEMENT), which needs the grid's own " +
                      "geometry resolved. Treating it as the identity would put the element on the project " +
                      "origin, so it is refused instead.";
                return null;
            }
            if (!string.Equals(placement.Type, "IFCLOCALPLACEMENT", StringComparison.OrdinalIgnoreCase))
            {
                why = "the object placement is a " + placement.Type + ", which this reader does not resolve.";
                return null;
            }

            // PlacementRelTo (0), RelativePlacement (1)
            IfcTransform parent = IfcTransform.Identity;
            IfcEntity relTo = ifc.Resolve(placement.At(0));
            if (relTo != null)
            {
                string parentWhy;
                parent = Walk(ifc, relTo, lengthScale, seen, depth + 1, out parentWhy);
                if (parent == null) { why = parentWhy; return null; }
            }

            IfcEntity relative = ifc.Resolve(placement.At(1));
            if (relative == null) return parent;

            string localWhy;
            IfcTransform local = Axis(ifc, relative, lengthScale, out localWhy);
            if (local == null) { why = localWhy; return null; }

            return parent.Compose(local);
        }

        /// <summary>
        /// An IFCAXIS2PLACEMENT2D or 3D as a transform. This is where RefDirection is made
        /// perpendicular to Axis instead of being trusted: IFC permits a RefDirection that
        /// is merely "in the plane", and a reader that uses it raw builds a skewed basis.
        /// </summary>
        public static IfcTransform Axis(IfcStepReader.Document ifc, IfcEntity placement,
                                        double lengthScale, out string why)
        {
            why = null;
            if (placement == null) return IfcTransform.Identity;

            bool is3d = string.Equals(placement.Type, "IFCAXIS2PLACEMENT3D", StringComparison.OrdinalIgnoreCase);
            bool is2d = string.Equals(placement.Type, "IFCAXIS2PLACEMENT2D", StringComparison.OrdinalIgnoreCase);
            if (!is3d && !is2d)
            {
                why = "a placement of type " + placement.Type + " is not an axis placement this reader resolves.";
                return null;
            }

            double[] origin = Point(ifc, placement.At(0), lengthScale) ?? new double[] { 0, 0, 0 };
            double[] z = is3d ? Direction(ifc, placement.At(1)) : null;
            double[] refDirection = is3d ? Direction(ifc, placement.At(2)) : Direction(ifc, placement.At(1));

            if (z == null) z = new double[] { 0, 0, 1 };
            z = Normalise(z);
            if (z == null)
            {
                why = "the placement's Axis direction has zero length.";
                return null;
            }

            if (refDirection == null)
            {
                // The IFC default: +X, unless that is parallel to the axis, in which case
                // any perpendicular will do and this picks a deterministic one. A reader
                // that returns a degenerate basis here produces NaN coordinates two steps
                // later, which surface as an unhelpful Revit exception.
                refDirection = Math.Abs(z[0]) > 0.9 ? new double[] { 0, 1, 0 } : new double[] { 1, 0, 0 };
            }

            double dot = refDirection[0] * z[0] + refDirection[1] * z[1] + refDirection[2] * z[2];
            double[] x = Normalise(new[]
            {
                refDirection[0] - dot * z[0],
                refDirection[1] - dot * z[1],
                refDirection[2] - dot * z[2]
            });
            if (x == null)
            {
                why = "the placement's RefDirection is parallel to its Axis, so it fixes no X direction.";
                return null;
            }

            double[] y = Cross(z, x);
            return new IfcTransform { X = x, Y = y, Z = z, Origin = origin };
        }

        /// <summary>An IFCCARTESIANPOINT in millimetres, or null.</summary>
        public static double[] Point(IfcStepReader.Document ifc, string reference, double lengthScale)
        {
            IfcEntity point = ifc?.Resolve(reference);
            if (point == null ||
                !string.Equals(point.Type, "IFCCARTESIANPOINT", StringComparison.OrdinalIgnoreCase)) return null;
            IReadOnlyList<string> values = IfcStepReader.List(point.At(0));
            if (values.Count < 2) return null;
            double x = IfcStepReader.Number(values[0]) ?? 0;
            double y = IfcStepReader.Number(values[1]) ?? 0;
            double z = values.Count > 2 ? IfcStepReader.Number(values[2]) ?? 0 : 0;
            return new[] { x * lengthScale, y * lengthScale, z * lengthScale };
        }

        /// <summary>An IFCDIRECTION as a raw (unnormalised) vector, or null. Directions are UNITLESS: no scale.</summary>
        public static double[] Direction(IfcStepReader.Document ifc, string reference)
        {
            IfcEntity direction = ifc?.Resolve(reference);
            if (direction == null ||
                !string.Equals(direction.Type, "IFCDIRECTION", StringComparison.OrdinalIgnoreCase)) return null;
            IReadOnlyList<string> values = IfcStepReader.List(direction.At(0));
            if (values.Count < 2) return null;
            return new[]
            {
                IfcStepReader.Number(values[0]) ?? 0,
                IfcStepReader.Number(values[1]) ?? 0,
                values.Count > 2 ? IfcStepReader.Number(values[2]) ?? 0 : 0
            };
        }

        public static double[] Normalise(double[] v)
        {
            if (v == null) return null;
            double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (length < 1e-12 || double.IsNaN(length) || double.IsInfinity(length)) return null;
            return new[] { v[0] / length, v[1] / length, v[2] / length };
        }

        public static double[] Cross(double[] a, double[] b) => new[]
        {
            a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0]
        };

        /// <summary>
        /// How many millimetres one IFC length unit is.
        ///
        /// A file in metres and a file in millimetres differ by a thousand, and an importer
        /// that assumes one produces a building either the size of a city or of a coin. The
        /// SI prefix is read; a file that declares a CONVERSION-BASED unit (feet, inches)
        /// is read through its conversion factor rather than silently treated as metres.
        /// </summary>
        public static double LengthScale(IfcStepReader.Document ifc, out string basis)
        {
            basis = "assumed metres (IFC's default when no length unit is declared)";
            if (ifc == null) return 1000.0;

            foreach (IfcEntity assignment in ifc.Of("IFCUNITASSIGNMENT"))
            {
                foreach (string reference in IfcStepReader.List(assignment.At(0)))
                {
                    IfcEntity unit = ifc.Resolve(reference);
                    if (unit == null) continue;

                    if (string.Equals(unit.Type, "IFCSIUNIT", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsLength(unit.At(1))) continue;
                        string prefix = IfcStepReader.Enumeration(unit.At(2)) ?? "";
                        double scale = PrefixScale(prefix);
                        basis = "declared IFCSIUNIT " + (prefix == "" ? "METRE" : prefix + "METRE");
                        return scale;
                    }

                    if (string.Equals(unit.Type, "IFCCONVERSIONBASEDUNIT", StringComparison.OrdinalIgnoreCase))
                    {
                        // Dimensions(0), UnitType(1), Name(2), ConversionFactor(3)
                        if (!IsLength(unit.At(1))) continue;
                        IfcEntity measure = ifc.Resolve(unit.At(3));
                        if (measure == null) continue;
                        double? factor = IfcStepReader.Number(StripMeasure(measure.At(0)));
                        IfcEntity baseUnit = ifc.Resolve(measure.At(1));
                        double baseScale = 1000.0;
                        if (baseUnit != null &&
                            string.Equals(baseUnit.Type, "IFCSIUNIT", StringComparison.OrdinalIgnoreCase))
                            baseScale = PrefixScale(IfcStepReader.Enumeration(baseUnit.At(2)) ?? "");
                        if (factor.HasValue && factor.Value > 0)
                        {
                            basis = "declared IFCCONVERSIONBASEDUNIT '" +
                                    (IfcStepReader.Text(unit.At(2)) ?? "unnamed") + "' = " +
                                    factor.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                                    " of its base unit";
                            return factor.Value * baseScale;
                        }
                    }
                }
            }

            // No unit assignment: fall back to any SI length unit in the file rather than
            // guessing, and say which path produced the number.
            foreach (IfcEntity unit in ifc.Of("IFCSIUNIT"))
            {
                if (!IsLength(unit.At(1))) continue;
                string prefix = IfcStepReader.Enumeration(unit.At(2)) ?? "";
                basis = "IFCSIUNIT " + (prefix == "" ? "METRE" : prefix + "METRE") +
                        " found outside a unit assignment";
                return PrefixScale(prefix);
            }
            return 1000.0;
        }

        private static bool IsLength(string raw) =>
            string.Equals(IfcStepReader.Enumeration(raw), "LENGTHUNIT", StringComparison.OrdinalIgnoreCase);

        private static double PrefixScale(string prefix)
        {
            switch ((prefix ?? "").ToUpperInvariant())
            {
                case "MILLI": return 1.0;
                case "CENTI": return 10.0;
                case "DECI": return 100.0;
                case "": return 1000.0;         // metre
                case "DECA": return 10000.0;
                case "HECTO": return 100000.0;
                case "KILO": return 1000000.0;
                case "MICRO": return 0.001;
                default: return 1000.0;
            }
        }

        /// <summary>IFCLENGTHMEASURE(0.3048) → "0.3048". A typed measure wrapping a number.</summary>
        private static string StripMeasure(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            string trimmed = raw.Trim();
            int open = trimmed.IndexOf('(');
            if (open > 0 && trimmed.EndsWith(")", StringComparison.Ordinal))
                return trimmed.Substring(open + 1, trimmed.Length - open - 2).Trim();
            return trimmed;
        }
    }
}
