// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// A COORDINATION VIEW IS SHOWN WHEN ITS VIEW, ITS BOX AND ITS COLOURS ARE THERE.
//
// horizun_coordination operation=show judged the result by the view alone: the
// section box and the colour overrides were checked on a sample and reported, but
// the outcome stayed "applied" with every override silently refused (audit
// 2026-10-08, 6.10). The verdict now needs all three, and every override is read
// back, not a sample.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    public static class CoordinationShowRules
    {
        /// <summary>The view exists by name, its section box is active at the asked extent, and every requested override reads back.</summary>
        public static bool Shown(bool viewOk, bool sectionBoxActive, int overridesRequested, int overridesApplied) =>
            viewOk && sectionBoxActive && overridesRequested > 0 && overridesApplied == overridesRequested;
    }
}
