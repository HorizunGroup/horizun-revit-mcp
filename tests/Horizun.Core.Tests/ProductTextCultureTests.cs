// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Backlog #45: product texts built with the SYSTEM culture. On a Spanish machine a
// tolerance of 2.5 read "2,5%", a delta "0,25 mm", an overlap "12,5 mm" - in an
// English sentence a reader parses those as two numbers, and a client parsing the
// text gets the wrong one. Every number in these texts is written invariant.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ProductTextCultureTests
    {
        private static T InSpanish<T>(Func<T> f)
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = new CultureInfo("es-ES"); return f(); }
            finally { CultureInfo.CurrentCulture = saved; }
        }

        [Fact]
        public void The_pair_headline_writes_its_tolerance_invariant()
        {
            var tally = new PairTally("A", "B");
            tally.Add(Measurement.Of(1), Measurement.Of(1), (x, y) => true);
            string text = InSpanish(() => tally.Headline(2.5));
            Assert.Contains("2.5%", text);
            Assert.DoesNotContain("2,5", text);
        }

        [Fact]
        public void The_acquire_refusal_writes_its_millimetres_invariant()
        {
            string text = InSpanish(() => LinkSurveyRules.AcquireRefusal(7, new List<long> { 7 }, 0.25, 1.5));
            Assert.Contains("0.25 mm <= 1.5 mm", text);
        }

        [Fact]
        public void The_face_coverage_note_writes_its_share_invariant()
        {
            var deltas = new List<double>();
            for (int i = 0; i < 50; i++) deltas.Add(0.1);
            string note = (string)InSpanish(() => LinkSurveyRules.FaceVerdict(deltas, 5, 10, 0.125))["note"];
            Assert.DoesNotContain("0,1", note ?? "");
        }

        [Fact]
        public void The_root_cause_details_write_their_millimetres_invariant()
        {
            var a = new ElementBoxFact { Id = 1, Category = "Walls", TypeId = 5, MinX = 0, MinY = 0, MinZ = 0, MaxX = 1000, MaxY = 200, MaxZ = 3000 };
            var b = new ElementBoxFact { Id = 2, Category = "Walls", TypeId = 5, MinX = 0, MinY = 0, MinZ = 0, MaxX = 1000, MaxY = 200, MaxZ = 3000 };
            string dup = InSpanish(() => WarningRootCauseRules.Classify(new[] { a, b }, 1.5).Detail);
            Assert.Contains("1.5 mm", dup);

            var c = new ElementBoxFact { Id = 3, Category = "Walls", TypeId = 5, MinX = 0, MinY = 0, MinZ = 0, MaxX = 1000, MaxY = 200, MaxZ = 3000 };
            var d = new ElementBoxFact { Id = 4, Category = "Floors", TypeId = 6, MinX = 500, MinY = 100, MinZ = 2987.5, MaxX = 1500, MaxY = 300, MaxZ = 3300 };
            string overlap = InSpanish(() => WarningRootCauseRules.Classify(new[] { c, d }, 1).Detail);
            Assert.Contains("12.5 mm", overlap);
        }
    }
}
