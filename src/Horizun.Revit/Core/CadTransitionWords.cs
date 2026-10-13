// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// The sentence horizun_cad_connect gives a drawn transition. Its numbers are
// INVARIANT: they were concatenated in the OS culture, and on an es-CO machine
// the reply said "Revit's 241,4 mm fitting ... (tolerance 25,4 mm)" beside fields
// carrying 241.4 and 25.4 (measured live 2026-10-09).
// -----------------------------------------------------------------------------
using System;
using System.Globalization;

namespace Horizun.Revit.Core
{
    public static class CadTransitionWords
    {
        public static string Means(bool fits, bool sizesMatch, bool collinear, double worstMm, double fittingLengthMm,
                                   double drawnLengthMm, double toleranceMm)
        {
            if (fits)
                return "the transition sits where the drawing draws it (ends within " + N(toleranceMm) + " mm), with the members' sizes at its ends";
            return "the transition does not fit the drawn space: " +
                   (!sizesMatch ? "its end sizes are not the members' sizes; " : "") +
                   (!collinear ? "it is not on the drawn axis; " : "") +
                   (worstMm > toleranceMm
                       ? "Revit's " + N(Math.Round(fittingLengthMm, 1)) + " mm fitting puts an end " + N(Math.Round(worstMm, 1)) +
                         " mm from where the drawing ends the " + N(drawnLengthMm) + " mm piece (tolerance " + N(toleranceMm) + " mm); "
                       : "") +
                   "it was undone rather than moving the network to make it fit";
        }

        private static string N(double v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
