// -----------------------------------------------------------------------------
// Horizun Revit MCP - stirrups the way an engineer declares them.
// Original Horizun code. No Revit types.
//
// What a schedule says is "10 at 100 over the first metre each end, 200 in the
// middle". What this bridge could express before was three separate rules with
// three hand-computed array origins, and the arithmetic that turns one into the
// other is exactly where a person makes a mistake nobody catches: a middle zone
// that starts 100 mm before the end zone finishes puts two stirrups in the same
// place, and the model shows one line.
//
// A zone rule EXPANDS into ordinary rebar rules - one per zone, each with the
// same profile translated along the beam. Everything downstream is unchanged:
// containment, the point-by-point audit, provenance and idempotency all apply
// because there is nothing new for them to know about.
//
// Nothing here decides a spacing, a length or a bar. It refuses instead:
//   - two zones with no length, because "the rest" cannot mean two things
//   - zones longer than the span they were given
//   - a zone too short for the layout it declares
//   - two bars in the same place at a zone boundary
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Horizun.Revit.Core
{
    /// <summary>One zone as declared: a length and a layout, in that zone's own terms.</summary>
    public sealed class StirrupZoneRequest
    {
        public string Name;
        /// <summary>How long this zone is. Null means "the rest", and only one zone may say it.</summary>
        public double? LengthMm;
        public RebarLayoutRequest Layout = new RebarLayoutRequest();
        /// <summary>Overrides the parent mark for this zone. Null keeps the parent's.</summary>
        public string Mark;
    }

    /// <summary>One zone as planned: where it starts, how long it is, and what it lays out.</summary>
    public sealed class StirrupZonePlan
    {
        public string Name;
        public double StartMm;
        public double LengthMm;
        public double EndMm { get { return StartMm + LengthMm; } }
        public RebarLayoutPlan Layout;
        /// <summary>The layout exactly as this zone declared it, kept so the expansion cannot paraphrase it.</summary>
        public RebarLayoutRequest Declared;
        public string Mark;
        /// <summary>Every array STATION of this zone measured from the start of the span.</summary>
        public List<double> AbsolutePositionsMm = new List<double>();
        /// <summary>
        /// The stations that actually carry a bar - the array minus a suppressed
        /// first or last. Revit keeps the position either way, which is why the two
        /// lists are different and why comparing the wrong one puts a phantom bar at
        /// every zone boundary.
        /// </summary>
        public List<double> AbsoluteBarPositionsMm = new List<double>();
    }

    public sealed class StirrupZoneResult
    {
        public bool Ok { get { return Code == null; } }
        public string Code;
        public string Why;
        public List<StirrupZonePlan> Zones = new List<StirrupZonePlan>();

        public double SpanMm;
        public double UsableSpanMm;
        public double StartOffsetMm;
        public double EndOffsetMm;

        /// <summary>The closest two stirrups from DIFFERENT zones come, or null when there is only one zone.</summary>
        public double? ClosestBetweenZonesMm;
        public string ClosestBetweenZonesWhere;

        public int TotalBars
        {
            get
            {
                int n = 0;
                foreach (StirrupZonePlan z in Zones) n += z.Layout == null ? 0 : z.Layout.Quantity;
                return n;
            }
        }
    }

    public static class StirrupZoneRules
    {
        public const string CodeNoZones = "no_zones_declared";
        public const string CodeTwoRemainders = "more_than_one_zone_without_a_length";
        public const string CodeZoneNotPositive = "zone_length_not_positive";
        public const string CodeZonesTooLong = "zones_longer_than_the_span";
        public const string CodeRemainderEmpty = "remainder_zone_has_no_length_left";
        public const string CodeSpanNotUsable = "span_not_usable";
        public const string CodeOffsetsNotUsable = "offsets_not_usable";
        public const string CodeSymmetricConflict = "symmetric_conflicts_with_the_declared_zones";
        public const string CodeLayoutRefused = "zone_layout_refused";
        public const string CodeBarsCoincide = "two_zones_put_a_bar_in_the_same_place";
        public const string CodeBarsTooClose = "two_zones_put_bars_closer_than_declared";
        public const string CodeNameRepeated = "zone_name_repeated";
        public const string CodeLayoutLongerThanZone = "zone_layout_longer_than_the_zone";

        public static readonly string[] AllCodes =
        {
            CodeNoZones, CodeTwoRemainders, CodeZoneNotPositive, CodeZonesTooLong, CodeRemainderEmpty,
            CodeSpanNotUsable, CodeOffsetsNotUsable, CodeSymmetricConflict, CodeLayoutRefused,
            CodeBarsCoincide, CodeBarsTooClose, CodeNameRepeated, CodeLayoutLongerThanZone
        };

        /// <summary>Two bars closer than this are the same bar twice, whatever anyone declared.</summary>
        public const double CoincidentMm = 1e-6;

        private static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>
        /// Lay the declared zones along a span and compute each one's layout.
        ///
        /// `symmetric` mirrors the FIRST zone at the far end, which is what "1 m at
        /// 100 each end" means. It requires the last declared zone to be the
        /// remainder, because a symmetric run with a declared end zone is two
        /// statements about the same metre of beam.
        /// </summary>
        public static StirrupZoneResult Plan(double spanMm, IList<StirrupZoneRequest> zones,
            bool symmetric, double startOffsetMm, double endOffsetMm,
            double? minimumClearBetweenZonesMm, double barModelDiameterMm)
        {
            var r = new StirrupZoneResult
            {
                SpanMm = spanMm,
                StartOffsetMm = startOffsetMm,
                EndOffsetMm = endOffsetMm
            };

            if (!Finite(spanMm) || spanMm <= 0)
            {
                r.Code = CodeSpanNotUsable;
                r.Why = "the span the zones lay out along is not a positive, finite length.";
                return r;
            }
            if (!Finite(startOffsetMm) || !Finite(endOffsetMm) || startOffsetMm < 0 || endOffsetMm < 0)
            {
                r.Code = CodeOffsetsNotUsable;
                r.Why = "the offsets at the ends of the span must be finite and not negative.";
                return r;
            }
            if (zones == null || zones.Count == 0)
            {
                r.Code = CodeNoZones;
                r.Why = "no stirrup zones were declared, and this bridge does not invent one.";
                return r;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (StirrupZoneRequest z in zones)
            {
                string n = z == null ? null : z.Name;
                if (string.IsNullOrWhiteSpace(n)) n = "zone" + (zones.IndexOf(z) + 1);
                if (!names.Add(n))
                {
                    r.Code = CodeNameRepeated;
                    r.Why = "two zones are called '" + n + "'. Each zone becomes its own bar set with its own " +
                            "provenance, so the names have to tell them apart.";
                    return r;
                }
            }

            r.UsableSpanMm = spanMm - startOffsetMm - endOffsetMm;
            if (r.UsableSpanMm <= 0)
            {
                r.Code = CodeOffsetsNotUsable;
                r.Why = "the offsets at the two ends leave no span for stirrups: " +
                        Mm(startOffsetMm) + " plus " + Mm(endOffsetMm) + " over a span of " + Mm(spanMm) + ".";
                return r;
            }

            // ---------------------------------------------------------- symmetry
            var declared = new List<StirrupZoneRequest>(zones);
            if (symmetric)
            {
                if (declared.Count < 2 || declared[declared.Count - 1].LengthMm.HasValue)
                {
                    r.Code = CodeSymmetricConflict;
                    r.Why = "symmetric mirrors the FIRST zone at the far end, so the last declared zone has to " +
                            "be the one without a length - the middle. Declaring an end zone as well says two " +
                            "things about the same metre of beam.";
                    return r;
                }
                StirrupZoneRequest first = declared[0];
                if (!first.LengthMm.HasValue)
                {
                    r.Code = CodeSymmetricConflict;
                    r.Why = "symmetric mirrors the first zone, so the first zone needs a length.";
                    return r;
                }
                // THE MIRROR NEEDS A NAME NOBODY ELSE HAS. The duplicate check
                // above ran over the DECLARED zones, before this one existed, so a
                // run with an unnamed first zone and a zone actually called "end"
                // produced two bar sets in different places sharing one rule id -
                // which provenance keys on, and which the audit matches on.
                string mirrorName = (string.IsNullOrWhiteSpace(first.Name) ? "zone1" : first.Name) + "_mirrored";
                if (!names.Add(mirrorName))
                {
                    r.Code = CodeNameRepeated;
                    r.Why = "symmetric adds a mirror of the first zone called '" + mirrorName +
                            "', and a zone of that name is already declared. Each zone becomes its own bar set " +
                            "with its own provenance, so two of them cannot share a name.";
                    return r;
                }
                declared.Add(new StirrupZoneRequest
                {
                    Name = mirrorName,
                    LengthMm = first.LengthMm,
                    Layout = first.Layout,
                    Mark = first.Mark
                });
            }

            // ------------------------------------------------------- the lengths
            int remainderAt = -1;
            double declaredTotal = 0;
            for (int i = 0; i < declared.Count; i++)
            {
                StirrupZoneRequest z = declared[i];
                if (z == null)
                {
                    r.Code = CodeZoneNotPositive;
                    r.Why = "zone " + i + " is not a zone.";
                    return r;
                }
                if (!z.LengthMm.HasValue)
                {
                    if (remainderAt >= 0)
                    {
                        r.Code = CodeTwoRemainders;
                        r.Why = "zones '" + Name(declared[remainderAt], remainderAt) + "' and '" +
                                Name(z, i) + "' both leave their length out. Only one zone can be the rest.";
                        return r;
                    }
                    remainderAt = i;
                    continue;
                }
                if (!Finite(z.LengthMm.Value) || z.LengthMm.Value <= 0)
                {
                    r.Code = CodeZoneNotPositive;
                    r.Why = "zone '" + Name(z, i) + "' declares a length that is not positive and finite.";
                    return r;
                }
                declaredTotal += z.LengthMm.Value;
            }

            if (declaredTotal > r.UsableSpanMm + 1e-9)
            {
                r.Code = CodeZonesTooLong;
                r.Why = "the declared zones come to " + Mm(declaredTotal) + " over a usable span of " +
                        Mm(r.UsableSpanMm) + ". Nothing here shortens a zone to make it fit.";
                return r;
            }

            double remainder = r.UsableSpanMm - declaredTotal;
            if (remainderAt >= 0 && remainder <= 0)
            {
                r.Code = CodeRemainderEmpty;
                r.Why = "zone '" + Name(declared[remainderAt], remainderAt) + "' is the rest of the span, and " +
                        "the other zones use all of it.";
                return r;
            }
            if (remainderAt < 0 && remainder > 1e-6)
            {
                // Every zone has a length and they do not fill the span. That is
                // allowed - the stirrups simply stop - but it is stated.
                r.Why = "the declared zones cover " + Mm(declaredTotal) + " of a usable " +
                        Mm(r.UsableSpanMm) + "; the remaining " + Mm(remainder) + " carries no stirrups.";
            }

            // ------------------------------------------------------ the layouts
            double at = startOffsetMm;
            for (int i = 0; i < declared.Count; i++)
            {
                StirrupZoneRequest z = declared[i];
                double length = z.LengthMm ?? remainder;
                var plan = new StirrupZonePlan
                {
                    Name = Name(z, i),
                    StartMm = at,
                    LengthMm = length,
                    Mark = z.Mark,
                    Declared = z.Layout
                };

                RebarLayoutPlan layout = RebarLayoutRules.Resolve(ForZone(z.Layout, length, barModelDiameterMm));
                if (!layout.Ok)
                {
                    r.Code = CodeLayoutRefused;
                    r.Why = "zone '" + plan.Name + "', " + Mm(length) + " long: " + layout.Error;
                    r.Zones.Add(plan);
                    return r;
                }

                // A LAYOUT THAT DERIVES ITS OWN EXTENT can be longer than the zone
                // it was given - number_with_spacing computes spacing x (n - 1) and
                // does not know about zones. Left alone it spills into the next one.
                if (layout.ArrayLengthMm > length + RebarLayoutRules.LengthToleranceMm)
                {
                    r.Code = CodeLayoutLongerThanZone;
                    r.Why = "zone '" + plan.Name + "' is " + Mm(length) + " long and its layout comes to " +
                            Mm(layout.ArrayLengthMm) + ", which runs into the next zone. Nothing here shortens " +
                            "it: either the zone is too short or the layout declares too many bars.";
                    r.Zones.Add(plan);
                    return r;
                }
                plan.Layout = layout;
                for (int k = 0; k < layout.PositionsMm.Count; k++)
                {
                    double station = at + layout.PositionsMm[k];
                    plan.AbsolutePositionsMm.Add(station);
                    bool suppressed = (k == 0 && !layout.IncludeFirstBar) ||
                                      (k == layout.PositionsMm.Count - 1 && !layout.IncludeLastBar);
                    if (!suppressed) plan.AbsoluteBarPositionsMm.Add(station);
                }
                r.Zones.Add(plan);
                at += length;
            }

            // -------------------------------------------- bars in the same place
            for (int i = 1; i < r.Zones.Count; i++)
            {
                StirrupZonePlan prev = r.Zones[i - 1], next = r.Zones[i];
                // The bars that EXIST, not the array stations. A zone whose first
                // bar is suppressed still owns that station, and comparing stations
                // reports a duplicate where there is no bar at all.
                if (prev.AbsoluteBarPositionsMm.Count == 0 || next.AbsoluteBarPositionsMm.Count == 0) continue;
                double last = prev.AbsoluteBarPositionsMm[prev.AbsoluteBarPositionsMm.Count - 1];
                double first = next.AbsoluteBarPositionsMm[0];
                double gap = first - last;
                if (!r.ClosestBetweenZonesMm.HasValue || gap < r.ClosestBetweenZonesMm.Value)
                {
                    r.ClosestBetweenZonesMm = gap;
                    r.ClosestBetweenZonesWhere = prev.Name + " -> " + next.Name;
                }

                if (Math.Abs(gap) <= CoincidentMm)
                {
                    r.Code = CodeBarsCoincide;
                    r.Why = "zone '" + prev.Name + "' finishes with a stirrup at " + Mm(last) + " and zone '" +
                            next.Name + "' starts with one at the same place. That is two bars in one line on a " +
                            "drawing and two bars in the quantities. Turn off the last bar of one zone or the " +
                            "first bar of the other.";
                    return r;
                }
                if (gap < 0)
                {
                    // A layout may derive an extent a tenth of a millimetre longer
                    // than its zone - the tolerance that absorbs float noise - and
                    // that tenth puts the previous zone's last bar PAST the next
                    // zone's first. Two stirrups a tenth of a millimetre apart on a
                    // ten millimetre bar are the same bar twice, and the coincidence
                    // test above only fires below a millionth.
                    r.Code = CodeBarsCoincide;
                    r.Why = "zone '" + prev.Name + "' finishes with a stirrup at " + Mm(last) + ", which is PAST " +
                            "the first stirrup of zone '" + next.Name + "' at " + Mm(first) + ". They overlap by " +
                            Mm(-gap) + ": the two bars are in each other, whatever the drawing shows.";
                    return r;
                }
                if (minimumClearBetweenZonesMm.HasValue && Finite(minimumClearBetweenZonesMm.Value) &&
                    gap < minimumClearBetweenZonesMm.Value)
                {
                    r.Code = CodeBarsTooClose;
                    r.Why = "zone '" + prev.Name + "' and zone '" + next.Name + "' put stirrups " + Mm(gap) +
                            " apart, and " + Mm(minimumClearBetweenZonesMm.Value) + " was declared as the least " +
                            "they may be.";
                    return r;
                }
            }

            return r;
        }

        /// <summary>
        /// The zone's layout declaration, with the two things a zone knows and a
        /// layout does not: how long it is, and how fat the bar is.
        ///
        /// number_with_spacing DERIVES its extent from number and spacing, and
        /// refuses a declared array length that disagrees - correctly, since they
        /// would be two statements about the same distance. So the zone length is
        /// not pushed into it; the check afterwards catches the case where what it
        /// derives does not fit.
        /// </summary>
        public static RebarLayoutRequest ForZone(RebarLayoutRequest declared, double zoneLengthMm,
                                                 double barModelDiameterMm)
        {
            var q = new RebarLayoutRequest
            {
                Layout = declared == null ? null : declared.Layout,
                Number = declared == null ? null : declared.Number,
                SpacingMm = declared == null ? null : declared.SpacingMm,
                ArrayLengthMm = declared == null ? null : declared.ArrayLengthMm,
                IncludeFirstBar = declared == null || declared.IncludeFirstBar,
                IncludeLastBar = declared == null || declared.IncludeLastBar,
                BarDiameterMm = declared == null ? null : declared.BarDiameterMm
            };
            if (q.Layout != RebarLayout.NumberWithSpacing && q.Layout != RebarLayout.Single &&
                !q.ArrayLengthMm.HasValue)
                q.ArrayLengthMm = zoneLengthMm;

            // THE MODEL DIAMETER WINS over whatever the declaration carries. It used
            // to fill in only when the declaration was silent - and the requirement
            // set parser always seeds the NOMINAL diameter from bar_types, so the
            // model diameter never got through on a set that declared one. ADR-003
            // measured the cost: minimum_clear_spacing counted with nominal predicts
            // 9 positions where Revit builds 8, and the verified apply then reports a
            // correct set as a failure.
            if (Finite(barModelDiameterMm) && barModelDiameterMm > 0)
                q.BarDiameterMm = barModelDiameterMm;
            return q;
        }

        /// <summary>
        /// Turn one zone rule into the ordinary reinforcement rules it means - one
        /// per zone, each the same profile moved along the run, each with its own
        /// layout. Ids are <c>parent#zone</c>, which is what provenance records and
        /// what the audit matches on, so a zone is a first-class thing downstream
        /// without anything downstream knowing about zones.
        ///
        /// The result carries the plan as well, because the plan is what says where
        /// the zones landed and how close the boundary bars came.
        /// </summary>
        public static StirrupZoneResult Expand(StructuralStirrupZoneRule rule, double spanMm,
            double barModelDiameterMm, out List<StructuralRebarRule> expanded)
        {
            expanded = new List<StructuralRebarRule>();
            if (rule == null)
            {
                return new StirrupZoneResult { Code = CodeNoZones, Why = "there was no stirrup zone rule." };
            }

            StirrupZoneResult r = Plan(spanMm, rule.Zones, rule.Symmetric, rule.StartOffsetMm,
                                       rule.EndOffsetMm, rule.MinimumClearBetweenZonesMm, barModelDiameterMm);
            if (!r.Ok) return r;

            double[] unit = RebarContainment.Unit(rule.AlongMm);
            if (unit == null)
            {
                r.Code = CodeSpanNotUsable;
                r.Why = "the direction the zones run in is not a usable vector.";
                return r;
            }

            foreach (StirrupZonePlan z in r.Zones)
            {
                var moved = new List<double[]>(rule.ProfileMm.Count);
                foreach (double[] p in rule.ProfileMm)
                    moved.Add(new[]
                    {
                        p[0] + unit[0] * z.StartMm,
                        p[1] + unit[1] * z.StartMm,
                        p[2] + unit[2] * z.StartMm
                    });

                var rebar = new StructuralRebarRule
                {
                    Id = rule.Id + "#" + z.Name,
                    Host = rule.Host,
                    BarTypeId = rule.BarTypeId,
                    ShapeName = rule.ShapeName,
                    Style = rule.Style,
                    CurvesMm = moved,
                    Closed = rule.Closed,
                    NormalMm = new[] { unit[0], unit[1], unit[2] },
                    BarsOnNormalSide = true,
                    Layout = ForZone(z.Declared, z.LengthMm, barModelDiameterMm),
                    Start = rule.Start,
                    End = rule.End,
                    Mark = string.IsNullOrWhiteSpace(z.Mark) ? rule.Mark : z.Mark,
                    Required = rule.Required,
                    AllowNewShape = rule.AllowNewShape,
                    Raw = rule.Raw
                };
                expanded.Add(rebar);
            }
            return r;
        }

        private static string Name(StirrupZoneRequest z, int i)
        {
            return z != null && !string.IsNullOrWhiteSpace(z.Name) ? z.Name : "zone" + (i + 1);
        }

        private static string Mm(double v)
        {
            return Math.Round(v, 3).ToString(CultureInfo.InvariantCulture) + " mm";
        }
    }
}
