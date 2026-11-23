// -----------------------------------------------------------------------------
// Horizun Revit MCP - does this set actually FIT, and what should come back out.
// Original Horizun code. No Revit types.
//
// The question this file answers is the one Revit will not ask on your behalf.
// A rebar set whose array is longer than its host is created without complaint:
// the element exists, the host is right, the type is right, and some of the
// steel is in the air outside the beam. Nothing in the reply distinguishes it
// from a correct one.
//
// So the plan projects the host, the bar and every bar POSITION onto the
// distribution direction and compares them - all of them, not the two ends -
// before a transaction is opened. And it says out loud that this is a projection
// onto one axis, not a solid containment test, because a claim about geometry
// that does not say how it was measured is not worth much.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>An interval on the distribution axis, in millimetres.</summary>
    public struct Span
    {
        public double Min;
        public double Max;
        public Span(double min, double max) { Min = Math.Min(min, max); Max = Math.Max(min, max); }
        public double Length { get { return Max - Min; } }
    }
}
