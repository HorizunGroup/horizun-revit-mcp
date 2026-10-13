using System;

namespace Horizun.Revit.Core
{
    public static class LinkViewVisibilityRules
    {
        public static bool OverlapsZ(double elementMin, double elementMax, double viewMin, double viewMax)
        {
            return !double.IsNaN(elementMin) && !double.IsNaN(elementMax) &&
                !double.IsNaN(viewMin) && !double.IsNaN(viewMax) &&
                elementMin <= elementMax && viewMin <= viewMax &&
                elementMax >= viewMin && elementMin <= viewMax;
        }
    }
}
