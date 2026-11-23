// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// THE CIRCULAR HOOP AND THE SPIRAL, BUILT FROM A PLACED REBAR SHAPE (acero 3.0, L7).
//
// MEASURED on Revit 2024: Rebar.CreateFromCurves builds a single arc between two
// points, and every chain with an arc in it - a closed circle of two or four arcs,
// an open 350 degree hoop, a line followed by an arc - fails inside Revit with "An
// internal error has occurred", whatever the style. A hoop or a spiral therefore
// cannot be drawn; it has to be PLACED, as an instance of a rebar shape that the
// model already carries (Rebar.CreateFromRebarShape), and then sized.
//
// WHICH SHAPE IS NEVER INFERRED. A template carries shapes called M_T1..M_T9 and
// M_SP, and nothing in a name says which of them is a lapped circle: the shape is
// named by the caller, and its DEFINITION is read before anything is built -
// RebarShapeDefinitionByArc, and its Type - so a name that does not mean what the
// rule asks is refused by name rather than placed.
//
// HOW IT IS SIZED is the documented behaviour of RebarShapeDrivenAccessor.ScaleToBox:
// the width and height of the shape "including bar thickness" are fitted to a box.
// So the box is the hoop's OUTSIDE: the centreline diameter plus one bar. A spiral
// is fitted with ScaleToBoxFor3D, its start in that box and its end in the parallel
// plane at the declared height, and its pitch and finishing turns are then set from
// the declaration. Every one of those numbers is re-read after the commit, and the
// centreline Revit drew is measured against the axis: on its radius, in its plane
// (a hoop) or between its base and its top (a spiral). Live measurement is pending.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// What a section rule's stirrups declare when the hoop is placed from a shape:
    /// the shape's name, the kind it must be, and for a spiral its pitch and finishing
    /// turns. Nothing here is defaulted.
    /// </summary>
    public sealed class StructuralShapePlacement
    {
        public const string KindLappedCircle = "lapped_circle", KindSpiral = "spiral";
        public static readonly string[] Kinds = { KindLappedCircle, KindSpiral };

        public string Shape;
        public string Kind;
        /// <summary>Spiral only: the rise per turn, centre to centre.</summary>
        public double? PitchMm;
        /// <summary>Spiral only: flat turns at the base and at the top, whole numbers.</summary>
        public int? BaseFinishingTurns, TopFinishingTurns;
    }

    /// <summary>
    /// Where one placed hoop or spiral goes, in model millimetres: the centre of the hoop
    /// (or of the spiral's BASE turn), the member's axis, the two directions of the section
    /// plane the shape's own x and y are mapped to, the CENTRELINE radius and - for a
    /// spiral - its height, pitch and finishing turns. Set only by an expansion.
    /// </summary>
    public sealed class PlacedShapeGeometry
    {
        public string Shape;
        public string Kind;
        public double[] CentreMm, AxisMm, XVec, YVec;
        public double CentrelineRadiusMm;
        public double? HeightMm, PitchMm;
        public int? BaseFinishingTurns, TopFinishingTurns;

        public bool IsSpiral => Kind == StructuralShapePlacement.KindSpiral;

        /// <summary>The same placement moved along the axis.</summary>
        public PlacedShapeGeometry MovedAlong(double[] unit, double mm)
        {
            var c = (PlacedShapeGeometry)MemberwiseClone();
            c.CentreMm = new[] { CentreMm[0] + unit[0] * mm, CentreMm[1] + unit[1] * mm, CentreMm[2] + unit[2] * mm };
            return c;
        }

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["shape"] = Shape,
                ["kind"] = Kind,
                ["centre_mm"] = Arr(CentreMm),
                ["axis"] = Arr(AxisMm),
                ["x_direction"] = Arr(XVec),
                ["y_direction"] = Arr(YVec),
                ["centreline_radius_mm"] = Math.Round(CentrelineRadiusMm, 3)
            };
            if (HeightMm.HasValue) o["height_mm"] = Math.Round(HeightMm.Value, 3);
            if (PitchMm.HasValue) o["pitch_mm"] = Math.Round(PitchMm.Value, 3);
            if (BaseFinishingTurns.HasValue) o["base_finishing_turns"] = BaseFinishingTurns.Value;
            if (TopFinishingTurns.HasValue) o["top_finishing_turns"] = TopFinishingTurns.Value;
            o["centre_means"] = IsSpiral
                ? "the centre of the spiral's BASE turn; the spiral rises height_mm along the axis from it."
                : "the centre of the hoop, which lies in the plane square to the axis through it.";
            return o;
        }

        public static PlacedShapeGeometry FromJson(JToken t)
        {
            var o = t as JObject;
            if (o == null) return null;
            return new PlacedShapeGeometry
            {
                Shape = (string)o["shape"],
                Kind = (string)o["kind"],
                CentreMm = Vec(o["centre_mm"]),
                AxisMm = Vec(o["axis"]),
                XVec = Vec(o["x_direction"]),
                YVec = Vec(o["y_direction"]),
                CentrelineRadiusMm = o.Value<double?>("centreline_radius_mm") ?? 0,
                HeightMm = o.Value<double?>("height_mm"),
                PitchMm = o.Value<double?>("pitch_mm"),
                BaseFinishingTurns = o.Value<int?>("base_finishing_turns"),
                TopFinishingTurns = o.Value<int?>("top_finishing_turns")
            };
        }

        private static JToken Arr(double[] v) =>
            v == null ? (JToken)JValue.CreateNull() : new JArray(Math.Round(v[0], 6), Math.Round(v[1], 6), Math.Round(v[2], 6));

        private static double[] Vec(JToken t)
        {
            var a = t as JArray;
            if (a == null || a.Count != 3) return null;
            return new[] { (double)a[0], (double)a[1], (double)a[2] };
        }
    }
}
