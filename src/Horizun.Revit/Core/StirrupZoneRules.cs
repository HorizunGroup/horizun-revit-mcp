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

    /// <summary>
    /// What a cover-aware zone expansion PREDICTED, carried on every expanded rule
    /// so the apply can hold the model to it after the commit.
    ///
    /// The prediction rests on ONE measured rule (ADR-003 item 7): Revit clamps a
    /// hosted array to the host's cover plus the bar's model radius at each end.
    /// So a zone whose first station is at least that far from the host's start,
    /// and whose last is at least that far from its end, is drawn where it was
    /// declared. That is an assumption stated here and proved only by the apply's
    /// post-commit comparison - which is why the flag is called predicted_from_host_cover
    /// rather than verified.
    /// </summary>
    public sealed class StirrupCoverPrediction
    {
        public const string Marker = "predicted_from_host_cover";

        /// <summary>host or declared.</summary>
        public string Source;
        public double CoverMm;
        public double BarRadiusMm;
        /// <summary>cover + radius: how far in from each end of the host span Revit will keep the array.</summary>
        public double ClampEachEndMm;
        public double HostSpanMm;
        /// <summary>HostSpanMm less the clamp at both ends, before any declared offset.</summary>
        public double UsableSpanMm;
        /// <summary>The direction the zones run in, unit length.</summary>
        public double[] Along;
        /// <summary>This zone's first and last bar STATION from the start of the host span.</summary>
        public double ZoneStartMm;
        public double ZoneEndMm;
        public string ZoneName;
    }
}
