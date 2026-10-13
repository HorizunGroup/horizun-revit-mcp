// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The sentence a drawn transition gets, in the machine's culture. MEASURED live
// 2026-10-09 on an es-CO machine: "Revit's 241,4 mm fitting ... (tolerance 25,4 mm)"
// - the numbers followed the OS culture inside an English sentence whose fields
// carry 241.4 and 25.4.
// -----------------------------------------------------------------------------
using System.Globalization;
using System.Threading;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadTransitionWordsTests
    {
        private static string UnderEsCo(System.Func<string> f)
        {
            CultureInfo before = Thread.CurrentThread.CurrentCulture;
            try { Thread.CurrentThread.CurrentCulture = new CultureInfo("es-CO"); return f(); }
            finally { Thread.CurrentThread.CurrentCulture = before; }
        }

        [Fact]
        public void A_transition_that_does_not_fit_is_said_with_invariant_numbers()
        {
            string s = UnderEsCo(() => CadTransitionWords.Means(false, true, true, 300.04, 241.43, 600, 25.4));
            Assert.Contains("Revit's 241.4 mm fitting puts an end 300 mm from where the drawing ends the 600 mm piece (tolerance 25.4 mm)", s);
            Assert.DoesNotContain(",4", s);
        }

        [Fact]
        public void A_transition_that_fits_is_said_with_invariant_numbers()
        {
            string s = UnderEsCo(() => CadTransitionWords.Means(true, true, true, 3.2, 241.4, 241.4, 25.4));
            Assert.Equal("the transition sits where the drawing draws it (ends within 25.4 mm), with the members' sizes at its ends", s);
        }

        [Fact]
        public void Each_reason_is_named()
        {
            string s = CadTransitionWords.Means(false, false, false, 10, 241.4, 250, 25.4);
            Assert.Contains("its end sizes are not the members' sizes; ", s);
            Assert.Contains("it is not on the drawn axis; ", s);
            Assert.DoesNotContain("puts an end", s);   // within tolerance: not a reason
            Assert.EndsWith("it was undone rather than moving the network to make it fit", s);
        }
    }
}
