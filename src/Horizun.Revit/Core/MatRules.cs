// -----------------------------------------------------------------------------
// Horizun Revit MCP - slab and wall reinforcement as a mat.
// Original Horizun code. No Revit types.
//
// "Top X at 150, top Y at 200" is one sentence. Expressing it before meant four
// reinforcement rules with hand-computed centrelines, and every one of those
// centrelines is a number somebody had to derive from the slab's own geometry -
// which the model already knows.
//
// So a mat rule reads the host's boundary and works out, per component:
//   where the FACE is            the outermost plane along the declared normal
//   how long each bar is         the host's extent along the bar direction,
//                                less the end cover at each end
//   where the array runs         the host's extent across it, less the side cover
//   how deep the bar sits        the declared offset from the face
//
// and then expands into ordinary reinforcement rules, exactly as stirrup zones
// do, so containment and the audit apply without knowing mats exist.
//
// Nothing here decides a bar size, a spacing, a cover or which face carries what.
// It refuses instead - and the case it refuses that nothing else would catch is
// two layers of a mat sharing one plane, which is not a drawing error anybody
// sees until the bars are in the model on top of each other.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class MatComponentRequest
    {
        public string Name;
        /// <summary>The direction the BARS run in. Must lie in the face.</summary>
        public double[] DirectionMm;
        public string BarTypeId;
        public RebarLayoutRequest Layout = new RebarLayoutRequest();
        /// <summary>Centreline distance from the face, inwards. Declared, never derived.</summary>
        public double OffsetFromFaceMm;
        /// <summary>How far short of the host's edge each bar stops, at both ends.</summary>
        public double EndCoverMm;
        /// <summary>How far in from the host's edges the array starts and finishes.</summary>
        public double SideCoverMm;
        public string Mark;
        public string ShapeName;
        public bool AllowNewShape;
    }

    public sealed class MatComponentPlan
    {
        public string Name;
        public double BarLengthMm;
        public double ArrayLengthMm;
        public double[] StartPointMm;
        public double[] EndPointMm;
        public double[] DistributionDirection;
        public double OffsetFromFaceMm;
        public RebarLayoutPlan Layout;
        /// <summary>
        /// The bar's model radius, carried here rather than looked up again later.
        /// It WAS looked up again, by name - and the name had already been
        /// defaulted to componentN while the lookup compared against the declared
        /// name, which was null. Every unnamed component came back with radius
        /// zero, and the same-plane check became `if (separation >= 0) continue`,
        /// which is true for an absolute value every time.
        /// </summary>
        public double RadiusMm;
        /// <summary>The bar direction after it was squared up to the face. See Why.</summary>
        public double[] AlongUsed;
        public string Why;
        /// <summary>What the openings policy did to this component. Null when the host has no opening.</summary>
        public MatOpeningReport Openings;
        /// <summary>How many reinforcement rules this component became - one, unless openings split it.</summary>
        public int RulesExpanded;
    }

    public sealed class MatResult
    {
        public bool Ok { get { return Code == null; } }
        public string Code;
        public string Why;
        public List<MatComponentPlan> Components = new List<MatComponentPlan>();
        /// <summary>Where the declared face sits, as a distance along the face normal.</summary>
        public double FaceOffsetMm;
        public string HowTheFaceWasFound;
        /// <summary>How many rings other than the outline the face carries. Zero for a solid slab.</summary>
        public int OpeningsFound;
        /// <summary>Null when the face's rings were read; why they could not be otherwise.</summary>
        public string FaceLoopsWhy;
    }

    public static class MatRules
    {
        public const string CodeNoComponents = "no_mat_components_declared";
        public const string CodeNoBoundary = "host_boundary_not_available";
        public const string CodeNormalNotUsable = "face_normal_not_usable";
        public const string CodeDirectionNotUsable = "bar_direction_not_usable";
        public const string CodeDirectionNotInFace = "bar_direction_is_not_in_the_face";
        public const string CodeNoRoomAlong = "no_room_left_along_the_bar";
        public const string CodeNoRoomAcross = "no_room_left_across_the_array";
        public const string CodeLayoutRefused = "mat_layout_refused";
        public const string CodeLayersShareAPlane = "two_layers_occupy_the_same_plane";
        public const string CodeNameRepeated = "component_name_repeated";
        public const string CodeOffsetNotUsable = "offset_from_face_not_usable";
        public const string CodeCoverNotUsable = "mat_cover_not_usable";
        public const string CodeCoverBelowHostCover = "mat_cover_below_the_host_cover";
        public const string CodeEndCoverNotHostCover = "mat_end_cover_is_not_the_host_cover";
        public const string CodeOpeningsNeedAPolicy = "openings_present_and_no_policy_declared";
        public const string CodeOpeningsNotExtractable = "face_loops_not_extractable";
        public const string CodeOpeningsLeaveNoBars = "openings_leave_no_bars";

        public static readonly string[] AllCodes =
        {
            CodeNoComponents, CodeNoBoundary, CodeNormalNotUsable, CodeDirectionNotUsable,
            CodeDirectionNotInFace, CodeNoRoomAlong, CodeNoRoomAcross, CodeLayoutRefused,
            CodeLayersShareAPlane, CodeNameRepeated, CodeOffsetNotUsable, CodeCoverNotUsable,
            CodeCoverBelowHostCover, CodeEndCoverNotHostCover, CodeOpeningsNeedAPolicy,
            CodeOpeningsNotExtractable, CodeOpeningsLeaveNoBars
        };

        /// <summary>A direction more than this far from the face is not in it.</summary>
        public const double PerpendicularToleranceDegrees = 1.0;

        private static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>
        /// Turn one mat rule into the reinforcement rules it means.
        ///
        /// `diameterOf` answers the MODEL diameter of a bar type id, in millimetres,
        /// or zero when the model will not say - the caller has the document, this
        /// does not.
        /// </summary>
        public static MatResult Expand(StructuralMatRule rule, HostMesh mesh,
            Func<string, double> diameterOf, out List<StructuralRebarRule> expanded)
        {
            return Expand(rule, mesh, diameterOf, null, out expanded);
        }

        /// <summary>
        /// The same, told the host's own COVER.
        ///
        /// MEASURED on Revit 2026, 2026-08-28, to the tenth of a millimetre on four
        /// independent numbers: Revit does not put a hosted bar where you ask if the
        /// host's cover says otherwise. It clamps the BAR to the host cover and the
        /// ARRAY to the host cover plus the bar's radius, whatever the declaration
        /// said. A slab 6000 x 4000 with a 25.4 mm cover, asked for 25 mm covers:
        ///
        ///     bar length   6000 - 2*25.4        = 5949.2   asked 5950
        ///     array        4000 - 2*(25.4 + 6)  = 3937.2   asked 3950
        ///
        /// Both components then failed the apply's own verification - correctly,
        /// because the model does not carry what was asked for. But the refusal
        /// arrived AFTER the commit, and it would arrive after every commit, which
        /// makes a mat declared below its host's cover permanently unbuildable with
        /// no explanation. So it is refused in the rehearsal, by name, with both
        /// numbers.
        /// </summary>
        public static MatResult Expand(StructuralMatRule rule, HostMesh mesh,
            Func<string, double> diameterOf, double? hostCoverMm, out List<StructuralRebarRule> expanded)
        {
            expanded = new List<StructuralRebarRule>();
            var r = new MatResult();

            if (rule == null || rule.Components.Count == 0)
            {
                r.Code = CodeNoComponents;
                r.Why = "a mat rule declares what its components are - top X, bottom Y - and this one declares " +
                        "none. Nothing here invents a mat.";
                return r;
            }

            MeshDiagnosis d = SolidContainment.Diagnose(mesh);
            if (!d.Usable)
            {
                r.Code = CodeNoBoundary;
                r.Why = "the host's boundary is what a mat is measured from, and it is not usable: " + d.Why;
                return r;
            }

            double[] up = RebarContainment.Unit(rule.FaceNormalMm);
            if (up == null)
            {
                r.Code = CodeNormalNotUsable;
                r.Why = "face_normal must be a direction pointing OUT of the face the mat sits under.";
                return r;
            }

            // WHERE THE FACE IS: the outermost plane along the normal. For a flat
            // slab that is the top or the bottom exactly; for a host with a step in
            // it, it is the highest step, and the containment check afterwards is
            // what catches a bar that then sits above the lower one.
            double faceAt = double.MinValue;
            foreach (double[] v in mesh.Vertices)
            {
                double along = v[0] * up[0] + v[1] * up[1] + v[2] * up[2];
                if (along > faceAt) faceAt = along;
            }
            r.FaceOffsetMm = faceAt;
            r.HowTheFaceWasFound =
                "the outermost plane of the host along the declared face normal. A host with a step in it has " +
                "more than one plane facing that way, and this is the outermost - the containment check is what " +
                "catches a bar left hanging over a lower one.";

            // THE HOLES IN THE FACE, read once for the rule. A face whose rings
            // cannot be read is a refusal when the rule declared an openings
            // policy - the policy cannot be applied to openings nobody found - and
            // today's behaviour otherwise, where containment refuses a bar over a
            // void after the fact.
            FaceLoops loops = MatOpenings.ExtractFaceLoops(mesh, up, faceAt, MatOpenings.PlaneToleranceMm);
            r.FaceLoopsWhy = loops.Why;
            r.OpeningsFound = loops.Ok ? loops.Openings.Count : 0;
            if (!loops.Ok && rule.Openings != null)
            {
                r.Code = CodeOpeningsNotExtractable;
                r.Why = "this mat declares an openings policy and the rings of the host's face could not be " +
                        "read, so there is nothing to apply the policy to: " + loops.Why + " Remove the block " +
                        "to build the mat against the outer extents alone, and rely on containment to refuse " +
                        "any bar over a void.";
                return r;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            var layers = new List<MatComponentPlan>();

            for (int i = 0; i < rule.Components.Count; i++)
            {
                MatComponentRequest c = rule.Components[i];
                if (c == null)
                {
                    r.Code = CodeNoComponents;
                    r.Why = "component " + (i + 1) + " of this mat rule is not a component.";
                    expanded = new List<StructuralRebarRule>();
                    return r;
                }
                string name = !string.IsNullOrWhiteSpace(c.Name) ? c.Name : "component" + (i + 1);
                if (!names.Add(name))
                {
                    r.Code = CodeNameRepeated;
                    r.Why = "two mat components are called '" + name + "'. Each becomes its own bar set with " +
                            "its own provenance, so the names have to tell them apart.";
                    return r;
                }

                if (!Finite(c.OffsetFromFaceMm) || c.OffsetFromFaceMm < 0)
                {
                    r.Code = CodeOffsetNotUsable;
                    r.Why = "component '" + name + "' needs offset_from_face_mm: how deep its centreline sits " +
                            "under the face, as a finite distance that is not negative. It is declared rather " +
                            "than derived, because the second layer of a mat sits under the first by an amount " +
                            "that is a decision, not a measurement.";
                    return r;
                }
                if (!Finite(c.EndCoverMm) || c.EndCoverMm < 0 || !Finite(c.SideCoverMm) || c.SideCoverMm < 0)
                {
                    r.Code = CodeCoverNotUsable;
                    r.Why = "component '" + name + "' has an end or side cover that is not a finite distance " +
                            "of zero or more.";
                    expanded = new List<StructuralRebarRule>();
                    return r;
                }

                double compRadius = 0;
                if (diameterOf != null)
                {
                    double dm = diameterOf(c.BarTypeId);
                    if (Finite(dm) && dm > 0) compRadius = dm / 2.0;
                }
                if (hostCoverMm.HasValue && Finite(hostCoverMm.Value) && hostCoverMm.Value > 0)
                {
                    // THE END COVER IS NOT A REQUEST. Revit sets a hosted bar's
                    // length from the HOST's cover and ignores what the declaration
                    // asked for - measured both ways on Revit 2026, 2026-08-28:
                    // asked 25 got 25.4, asked 30 got 25.4, on a slab whose cover is
                    // 25.4, to the tenth of a millimetre over 21 and 25 bars. So a
                    // declaration that disagrees is not a preference Revit will bend
                    // to; it is a number that will silently not happen.
                    if (Math.Abs(c.EndCoverMm - hostCoverMm.Value) > 1e-6)
                    {
                        r.Code = CodeEndCoverNotHostCover;
                        r.Why = "component '" + name + "' declares an end cover of " + Mm(c.EndCoverMm) +
                                " and the host's own cover is " + Mm(hostCoverMm.Value) + ". Revit sets a " +
                                "hosted bar's length from the HOST's cover and ignores the declaration - " +
                                "measured in both directions, asking for less AND asking for more. It would " +
                                "build a bar " + Mm(Math.Abs(2 * (hostCoverMm.Value - c.EndCoverMm))) +
                                " different from this, and the write would not verify. Declare " +
                                Mm(hostCoverMm.Value) + " to match the host, or change the host's cover.";
                        expanded = new List<StructuralRebarRule>();
                        return r;
                    }
                    double needed = hostCoverMm.Value + compRadius;
                    if (c.SideCoverMm < needed - 1e-9)
                    {
                        r.Code = CodeCoverBelowHostCover;
                        r.Why = "component '" + name + "' declares a side cover of " + Mm(c.SideCoverMm) +
                                " and Revit clamps the ARRAY to the host's cover plus the bar's radius, which " +
                                "here is " + Mm(hostCoverMm.Value) + " + " + Mm(compRadius) + " = " +
                                Mm(needed) + ". It would build the array " +
                                Mm(2 * (needed - c.SideCoverMm)) + " shorter than this asks for and the write " +
                                "would not verify. Declare at least that, or change the host's cover.";
                        expanded = new List<StructuralRebarRule>();
                        return r;
                    }
                }

                double[] along = RebarContainment.Unit(c.DirectionMm);
                if (along == null)
                {
                    r.Code = CodeDirectionNotUsable;
                    r.Why = "component '" + name + "' needs direction: the way its bars RUN, as [x, y, z].";
                    return r;
                }

                double dot = along[0] * up[0] + along[1] * up[1] + along[2] * up[2];
                double degrees = Math.Abs(90.0 - Math.Acos(Math.Max(-1, Math.Min(1, dot))) * 180.0 / Math.PI);
                if (degrees > PerpendicularToleranceDegrees)
                {
                    r.Code = CodeDirectionNotInFace;
                    r.Why = "component '" + name + "' runs " + Deg(degrees) + " out of the face it is declared " +
                            "on. A mat bar lies in its face; this one dives into the concrete or out of it.";
                    expanded = new List<StructuralRebarRule>();
                    return r;
                }

                // SQUARE IT UP BEFORE USING IT AS AN AXIS. A direction within the
                // tolerance is accepted, and the point arithmetic below rebuilds a
                // model point as along*da + across*db + up*dc - which is only that
                // point if the three are orthonormal. The leaked term is
                // da * (along . up), and `da` is a MODEL coordinate: a slab fifty
                // metres from the project origin, with a direction half a degree
                // off, put the bar four hundred millimetres above a two hundred
                // millimetre slab. Measured.
                double straightened = 0;
                if (Math.Abs(dot) > 1e-15)
                {
                    var squared = new[]
                    {
                        along[0] - up[0] * dot,
                        along[1] - up[1] * dot,
                        along[2] - up[2] * dot
                    };
                    double[] u = RebarContainment.Unit(squared);
                    if (u == null)
                    {
                        r.Code = CodeDirectionNotInFace;
                        r.Why = "component '" + name + "' has nothing left once its out-of-face part is " +
                                "removed, so it runs along the face normal.";
                        expanded = new List<StructuralRebarRule>();
                        return r;
                    }
                    straightened = degrees;
                    along = u;
                }

                // The third axis: across the bars, in the face.
                double[] across = Cross(up, along);   // exactly perpendicular to both, now
                double[] acrossUnit = RebarContainment.Unit(across);
                if (acrossUnit == null)
                {
                    r.Code = CodeDirectionNotInFace;
                    r.Why = "component '" + name + "' runs along the face normal, so there is no direction left " +
                            "for the array to march in.";
                    expanded = new List<StructuralRebarRule>();
                    return r;
                }

                double minAlong, maxAlong, minAcross, maxAcross;
                Extent(mesh, along, out minAlong, out maxAlong);
                Extent(mesh, acrossUnit, out minAcross, out maxAcross);

                double barLength = (maxAlong - c.EndCoverMm) - (minAlong + c.EndCoverMm);
                if (barLength <= 0)
                {
                    r.Code = CodeNoRoomAlong;
                    r.Why = "component '" + name + "': the host measures " + Mm(maxAlong - minAlong) +
                            " along its bars and the end cover takes " + Mm(c.EndCoverMm * 2) +
                            ", which leaves nothing.";
                    expanded = new List<StructuralRebarRule>();
                    return r;
                }
                double arrayLength = (maxAcross - c.SideCoverMm) - (minAcross + c.SideCoverMm);
                if (arrayLength <= 0)
                {
                    r.Code = CodeNoRoomAcross;
                    r.Why = "component '" + name + "': the host measures " + Mm(maxAcross - minAcross) +
                            " across its bars and the side cover takes " + Mm(c.SideCoverMm * 2) +
                            ", which leaves nothing for the array.";
                    expanded = new List<StructuralRebarRule>();
                    return r;
                }

                double depth = faceAt - c.OffsetFromFaceMm;
                double startAlong = minAlong + c.EndCoverMm;
                double startAcross = minAcross + c.SideCoverMm;

                var start = Point(along, startAlong, acrossUnit, startAcross, up, depth);
                var end = Point(along, startAlong + barLength, acrossUnit, startAcross, up, depth);

                var request = new RebarLayoutRequest
                {
                    Layout = c.Layout == null ? null : c.Layout.Layout,
                    Number = c.Layout == null ? null : c.Layout.Number,
                    SpacingMm = c.Layout == null ? null : c.Layout.SpacingMm,
                    ArrayLengthMm = c.Layout == null ? null : c.Layout.ArrayLengthMm,
                    IncludeFirstBar = c.Layout == null || c.Layout.IncludeFirstBar,
                    IncludeLastBar = c.Layout == null || c.Layout.IncludeLastBar,
                    BarDiameterMm = c.Layout == null ? null : c.Layout.BarDiameterMm
                };
                if (request.Layout != RebarLayout.NumberWithSpacing && request.Layout != RebarLayout.Single &&
                    !request.ArrayLengthMm.HasValue)
                    request.ArrayLengthMm = arrayLength;
                // THE MODEL DIAMETER WINS. It used to fill in only when the
                // declaration was silent - and the requirement-set parser always
                // seeds the NOMINAL diameter from bar_types, so the model diameter
                // was discarded on every set that declared one. ADR-003 records what
                // that costs: nominal arithmetic predicted 9 positions where Revit
                // built 8, and the apply then reported a correct set as a failure.
                // The plain reinforcement path already overwrites it for this reason.
                double dia = compRadius * 2.0;
                if (Finite(dia) && dia > 0) request.BarDiameterMm = dia;

                RebarLayoutPlan layout = RebarLayoutRules.Resolve(request);
                if (!layout.Ok)
                {
                    r.Code = CodeLayoutRefused;
                    r.Why = "component '" + name + "', across " + Mm(arrayLength) + ": " + layout.Error;
                    expanded = new List<StructuralRebarRule>();
                    return r;
                }

                var plan = new MatComponentPlan
                {
                    Name = name,
                    BarLengthMm = barLength,
                    ArrayLengthMm = arrayLength,
                    StartPointMm = start,
                    EndPointMm = end,
                    DistributionDirection = acrossUnit,
                    OffsetFromFaceMm = c.OffsetFromFaceMm,
                    Layout = layout,
                    RadiusMm = Finite(dia) && dia > 0 ? dia / 2.0 : 0,
                    AlongUsed = along,
                    Why = (straightened > 0
                              ? "the declared direction was " + Deg(straightened) + " out of the face and was " +
                                "squared up to it before anything was measured; "
                              : "") +
                          "bars run " + Mm(barLength) + " along the host's " + Mm(maxAlong - minAlong) +
                          " extent, " + Mm(c.OffsetFromFaceMm) + " under the face, distributed over " +
                          Mm(arrayLength) + "."
                };
                layers.Add(plan);
                r.Components.Add(plan);

                string baseId = rule.Id + "#" + name;
                Func<double, double, double, RebarLayoutRequest, StructuralRebarRule> make = (ua, ub, v, lay) =>
                    new StructuralRebarRule
                    {
                        Id = baseId,
                        Host = rule.Host,
                        BarTypeId = c.BarTypeId,
                        ShapeName = c.ShapeName,
                        Style = StructuralStyle.Standard,
                        CurvesMm = new List<double[]>
                        {
                            Point(along, ua, acrossUnit, v, up, depth),
                            Point(along, ub, acrossUnit, v, up, depth)
                        },
                        Closed = false,
                        NormalMm = new[] { acrossUnit[0], acrossUnit[1], acrossUnit[2] },
                        BarsOnNormalSide = true,
                        Layout = lay,
                        Mark = string.IsNullOrWhiteSpace(c.Mark) ? rule.Mark : c.Mark,
                        Required = rule.Required,
                        AllowNewShape = c.AllowNewShape,
                        Raw = rule.Raw
                    };

                // NO OPENING IN THE FACE: one rule, exactly as before this file knew
                // about holes, so every set and every live probe written against a
                // solid slab reads the same.
                if (!loops.Ok || loops.Openings.Count == 0)
                {
                    plan.RulesExpanded = 1;
                    expanded.Add(make(startAlong, startAlong + barLength, startAcross, request));
                    continue;
                }

                // THE OPENINGS, IN THIS COMPONENT'S FRAME. Every ring is sized and
                // judged against minimum_size_mm; with no block declared nothing is
                // ignored, because the question below is whether a policy is NEEDED.
                double minSize = rule.Openings == null ? 0 : rule.Openings.MinimumSizeMm;
                var ctx = new MatOpeningContext
                {
                    Policy = rule.Openings == null ? null : rule.Openings.Policy,
                    MinimumSizeMm = minSize,
                    ClearanceMm = rule.Openings != null && rule.Openings.ClearanceMm.HasValue ? rule.Openings.ClearanceMm.Value : 0,
                    BarRadiusMm = compRadius,
                    Along = new[] { along[0], along[1], along[2] },
                    Across = new[] { acrossUnit[0], acrossUnit[1], acrossUnit[2] }
                };
                for (int k = 0; k < loops.Openings.Count; k++)
                    ctx.Openings.Add(MatOpenings.Region(loops.Openings[k], k, along, acrossUnit, minSize));
                var report = new MatOpeningReport
                {
                    Policy = ctx.Policy ?? "not_declared",
                    OpeningsConsidered = ctx.Openings.Count(o => o.Considered),
                    OpeningsIgnored = ctx.Openings.Count(o => !o.Considered)
                };
                ctx.Report = report;
                plan.Openings = report;

                // EVERY BAR THAT EXISTS, and where its body meets a void. The bar's
                // station is v; the stretch(es) of u where a bar of this radius at
                // that station is over a considered opening are its crossings.
                double u0 = startAlong, u1 = startAlong + barLength;
                var slots = new List<BarSlot>();
                for (int k = 0; k < layout.PositionsMm.Count; k++)
                {
                    bool suppressed = (k == 0 && !layout.IncludeFirstBar) ||
                                      (k == layout.PositionsMm.Count - 1 && !layout.IncludeLastBar);
                    if (suppressed) continue;
                    var slot = new BarSlot { Index = k, PositionMm = layout.PositionsMm[k], V = startAcross + layout.PositionsMm[k] };
                    var body = new List<double[]>();
                    foreach (MatOpeningRegion reg in ctx.Considered)
                        body.AddRange(MatOpenings.CrossingIntervals(reg.Uv, slot.V, compRadius));
                    slot.Body = MatOpenings.Clip(MatOpenings.Merge(body), u0, u1);
                    slots.Add(slot);
                }
                report.BarsPlanned = slots.Count;
                foreach (BarSlot s in slots) if (s.Body.Count > 0) report.BarsCrossing.Add(s.Index);

                if (rule.Openings == null)
                {
                    // NO POLICY DECLARED. A bar that would cross a hole is a design
                    // question, and it is refused here by name rather than built
                    // and refused by containment with a message about geometry.
                    if (report.BarsCrossing.Count > 0)
                    {
                        r.Code = CodeOpeningsNeedAPolicy;
                        r.Why = "component '" + name + "': the host's face has " + loops.Openings.Count +
                                " opening(s) - " + Sizes(ctx) + " - and " + report.BarsCrossing.Count + " of " +
                                slots.Count + " bar(s) would cross one, and this mat declares no openings block. " +
                                "What happens at a hole is a design decision: declare openings: { policy: omit " +
                                "| trim | ignore, minimum_size_mm, clearance_mm (trim) }. omit drops the bars " +
                                "that cross; trim stops each of them short of the opening by the clearance and " +
                                "builds the remaining stretches as their own bars; ignore builds them as declared " +
                                "and reports the crossings - containment then refuses any bar really over the void.";
                        expanded = new List<StructuralRebarRule>();
                        return r;
                    }
                    // Openings exist and no bar meets one: today's mat, with the
                    // openings reported and the context carried so the apply can
                    // confirm the drawn bars miss them too.
                    report.BarsKept = slots.Count;
                    report.Runs.Add(new MatOpeningRun { RuleId = baseId, FirstBar = slots.Count > 0 ? slots[0].Index : 0,
                        LastBar = slots.Count > 0 ? slots[slots.Count - 1].Index : 0, Bars = slots.Count, FromMm = u0, ToMm = u1 });
                    plan.RulesExpanded = 1;
                    StructuralRebarRule whole = make(startAlong, startAlong + barLength, startAcross, request);
                    whole.OpeningContext = ctx;
                    expanded.Add(whole);
                    continue;
                }

                if (rule.Openings.Policy == StructuralMatOpenings.PolicyIgnore)
                {
                    report.BarsKept = slots.Count;
                    report.Runs.Add(new MatOpeningRun { RuleId = baseId, FirstBar = slots.Count > 0 ? slots[0].Index : 0,
                        LastBar = slots.Count > 0 ? slots[slots.Count - 1].Index : 0, Bars = slots.Count, FromMm = u0, ToMm = u1 });
                    plan.RulesExpanded = 1;
                    StructuralRebarRule asDeclared = make(startAlong, startAlong + barLength, startAcross, request);
                    asDeclared.OpeningContext = ctx;
                    expanded.Add(asDeclared);
                    continue;
                }

                // OMIT or TRIM: each bar keeps a list of stretches - the whole bar,
                // some of it, or none - and bars with the SAME stretches next to
                // each other form a run that is one Revit set.
                bool trim = rule.Openings.Policy == StructuralMatOpenings.PolicyTrim;
                foreach (BarSlot s in slots)
                {
                    if (s.Body.Count == 0) { s.Segments = new List<double[]> { new[] { u0, u1 } }; continue; }
                    if (!trim) { s.Segments = new List<double[]>(); report.BarsOmitted.Add(s.Index); continue; }
                    List<double[]> removed = MatOpenings.Clip(MatOpenings.Widen(s.Body, ctx.ClearanceMm), u0, u1);
                    var trimmed = new MatTrimmedBar { Bar = s.Index, PositionMm = s.PositionMm, RemovedMm = removed };
                    s.Segments = new List<double[]>();
                    foreach (double[] seg in MatOpenings.Complement(removed, u0, u1))
                    {
                        if (seg[1] - seg[0] < MatOpenings.MinimumSegmentMm) trimmed.DroppedMm.Add(seg);
                        else s.Segments.Add(seg);
                    }
                    trimmed.SegmentsMm = s.Segments;
                    s.Trimmed = true;
                    if (s.Segments.Count == 0) report.BarsOmitted.Add(s.Index);
                    else report.BarsTrimmed.Add(trimmed);
                }

                var runs = new List<List<BarSlot>>();
                foreach (BarSlot s in slots)
                {
                    if (s.Segments.Count == 0) continue;
                    List<BarSlot> last = runs.Count > 0 ? runs[runs.Count - 1] : null;
                    if (last != null && last[last.Count - 1].Index == s.Index - 1 &&
                        MatOpenings.SameIntervals(last[0].Segments, s.Segments))
                        last.Add(s);
                    else runs.Add(new List<BarSlot> { s });
                }
                report.BarsKept = runs.Sum(x => x.Count);
                if (runs.Count == 0)
                {
                    r.Code = CodeOpeningsLeaveNoBars;
                    r.Why = "component '" + name + "': every one of its " + slots.Count + " bar(s) meets an opening " +
                            "and the " + rule.Openings.Policy + " policy leaves nothing to build. Nothing here " +
                            "invents a bar; the openings are " + Sizes(ctx) + ".";
                    expanded = new List<StructuralRebarRule>();
                    return r;
                }

                bool untouched = runs.Count == 1 && runs[0].Count == slots.Count && !runs[0][0].Trimmed;
                int runNo = 0;
                foreach (List<BarSlot> run in runs)
                {
                    runNo++;
                    BarSlot first = run[0];
                    int segNo = 0;
                    foreach (double[] seg in first.Segments)
                    {
                        segNo++;
                        string id = untouched ? baseId
                            : baseId + "#run" + runNo.ToString(CultureInfo.InvariantCulture) +
                              (first.Trimmed ? "#seg" + segNo.ToString(CultureInfo.InvariantCulture) : "");
                        // THE SAME PITCH, as a count and a spacing: number_with_spacing
                        // reproduces the original stations exactly, and a run of one
                        // bar is a single. The whole-component layout is what the
                        // caller declared; a run is what is left of it.
                        RebarLayoutRequest lay = run.Count == 1
                            ? new RebarLayoutRequest { Layout = RebarLayout.Single, BarDiameterMm = request.BarDiameterMm }
                            : new RebarLayoutRequest
                            {
                                Layout = RebarLayout.NumberWithSpacing,
                                Number = run.Count,
                                SpacingMm = layout.ResultingSpacingMm,
                                BarDiameterMm = request.BarDiameterMm
                            };
                        StructuralRebarRule piece = make(seg[0], seg[1], first.V, lay);
                        piece.Id = id;
                        piece.OpeningContext = ctx;
                        expanded.Add(piece);
                        report.Runs.Add(new MatOpeningRun
                        {
                            RuleId = id, FirstBar = first.Index, LastBar = run[run.Count - 1].Index,
                            Bars = run.Count, Trimmed = first.Trimmed, FromMm = seg[0], ToMm = seg[1]
                        });
                    }
                }
                plan.RulesExpanded = report.Runs.Count;
                plan.Why += " Openings: " + report.BarsOmitted.Count + " bar(s) omitted, " +
                            report.BarsTrimmed.Count + " trimmed, " + report.Runs.Count + " rule(s) expanded.";
            }

            // TWO LAYERS IN ONE PLANE. Crossing bars cannot share an elevation, and
            // nothing downstream would say so: both sets are inside the host, both
            // meet their cover, and both re-read exactly as asked. The model simply
            // has steel inside steel.
            for (int i = 0; i < layers.Count; i++)
                for (int k = i + 1; k < layers.Count; k++)
                {
                    MatComponentPlan a = layers[i], b = layers[k];
                    double sep = Math.Abs(a.OffsetFromFaceMm - b.OffsetFromFaceMm);
                    double ra = a.RadiusMm, rb = b.RadiusMm;
                    if (ra + rb <= 0)
                    {
                        // No diameter, so no separation can be judged. Two crossing
                        // layers at the SAME depth are still wrong whatever the bar
                        // size, and that much is still said.
                        if (sep > 0) continue;
                    }
                    else if (sep >= ra + rb) continue;
                    double cross = Math.Abs(a.DistributionDirection[0] * b.DistributionDirection[0] +
                                            a.DistributionDirection[1] * b.DistributionDirection[1] +
                                            a.DistributionDirection[2] * b.DistributionDirection[2]);
                    if (cross > 0.999) continue;   // parallel layers, not crossing
                    r.Code = CodeLayersShareAPlane;
                    r.Why = "components '" + a.Name + "' and '" + b.Name + "' cross each other and their " +
                            "centrelines are " + Mm(sep) + " apart, which is less than the " + Mm(ra + rb) +
                            " their two radii need. They would be built inside one another. Nothing else in " +
                            "this bridge would report it: both sets sit inside the host and both meet their " +
                            "cover. Declare a different offset_from_face_mm for one of them.";
                    expanded = new List<StructuralRebarRule>();
                    return r;
                }

            return r;
        }

        /// <summary>One bar of a component while the openings policy is being applied to it.</summary>
        private sealed class BarSlot
        {
            public int Index;
            public double PositionMm;
            /// <summary>The bar's station across the face, in the component's v.</summary>
            public double V;
            /// <summary>Where the bar's body is over a considered opening, along u, clipped to the bar.</summary>
            public List<double[]> Body = new List<double[]>();
            /// <summary>What is built of it: the whole bar, its trimmed stretches, or nothing.</summary>
            public List<double[]> Segments = new List<double[]>();
            public bool Trimmed;
        }

        private static string Sizes(MatOpeningContext ctx)
        {
            return string.Join("; ", ctx.Openings.Select(o =>
                "opening " + o.Index + ": " + Mm(o.UMax - o.UMin) + " along the bars by " + Mm(o.VMax - o.VMin) +
                " across, largest dimension " + Mm(o.DiameterMm) + (o.Considered ? "" : " (ignored)")));
        }

        private static void Extent(HostMesh mesh, double[] dir, out double min, out double max)
        {
            min = double.MaxValue;
            max = double.MinValue;
            foreach (double[] v in mesh.Vertices)
            {
                double p = v[0] * dir[0] + v[1] * dir[1] + v[2] * dir[2];
                if (p < min) min = p;
                if (p > max) max = p;
            }
        }

        private static double[] Point(double[] a, double da, double[] b, double db, double[] c, double dc)
        {
            return new[]
            {
                a[0] * da + b[0] * db + c[0] * dc,
                a[1] * da + b[1] * db + c[1] * dc,
                a[2] * da + b[2] * db + c[2] * dc
            };
        }

        private static double[] Cross(double[] u, double[] v)
        {
            return new[]
            {
                u[1] * v[2] - u[2] * v[1],
                u[2] * v[0] - u[0] * v[2],
                u[0] * v[1] - u[1] * v[0]
            };
        }

        private static string Mm(double v)
        {
            return Math.Round(v, 3).ToString(CultureInfo.InvariantCulture) + " mm";
        }

        private static string Deg(double v)
        {
            return Math.Round(v, 3).ToString(CultureInfo.InvariantCulture) + " degrees";
        }
    }
}
