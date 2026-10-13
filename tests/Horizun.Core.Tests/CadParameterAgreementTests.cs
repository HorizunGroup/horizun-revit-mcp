// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Audit 2026-10-08: the CAD audit and update compared a rule's parameter value,
// formatted with the CURRENT culture (1.5 -> "1,5", true -> "True"), against the
// element's AsValueString() ("1,5 m", "Sí"). A numeric or boolean value - which
// horizun_write_params_verified writes as Set(double) in internal units / as the
// integer 1 or 0 - could therefore never agree, and every such parameter was
// reported as edited by somebody. The reader now keeps the STORED value beside
// the display text, and agreement is decided by the rule value's own type.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadParameterAgreementTests
    {
        private static void UnderEachCulture(Action check)
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                foreach (string c in new[] { "en-US", "es-CO" })
                {
                    CultureInfo.CurrentCulture = new CultureInfo(c);
                    check();
                }
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }

        [Fact]
        public void A_number_agrees_with_the_stored_double_not_the_display_text()
        {
            UnderEachCulture(() =>
            {
                Assert.True(CadParameterValues.Agrees(new JValue(1.5), "1.5", "1,5 m"));
                Assert.True(CadParameterValues.Agrees(new JValue(2), "2", "2"));
                Assert.False(CadParameterValues.Agrees(new JValue(1.5), "15", "15"));
            });
        }

        [Fact]
        public void A_boolean_agrees_with_the_stored_yes_no_integer()
        {
            UnderEachCulture(() =>
            {
                Assert.True(CadParameterValues.Agrees(new JValue(true), "1", "Sí"));
                Assert.True(CadParameterValues.Agrees(new JValue(false), "0", "No"));
                Assert.False(CadParameterValues.Agrees(new JValue(true), "0", "No"));
            });
        }

        [Fact]
        public void A_string_agrees_with_the_stored_or_the_displayed_text_exactly()
        {
            Assert.True(CadParameterValues.Agrees(new JValue("60"), "60", null));
            // A string is written through SetValueString, which reads back as display text.
            Assert.True(CadParameterValues.Agrees(new JValue("200 mm"), "0.656167979002625", "200 mm"));
            Assert.False(CadParameterValues.Agrees(new JValue("EI-60"), "ei-60", "ei-60"));
        }

        [Fact]
        public void The_expected_text_in_a_finding_is_culture_free()
        {
            UnderEachCulture(() =>
            {
                Assert.Equal("1.5", CadParameterValues.ExpectedText(new JValue(1.5)));
                Assert.Equal("1", CadParameterValues.ExpectedText(new JValue(true)));
                Assert.Null(CadParameterValues.ExpectedText(JValue.CreateNull()));
            });
        }

        [Fact]
        public void The_update_plan_sees_no_divergence_for_a_number_written_as_a_number()
        {
            UnderEachCulture(() =>
            {
                var held = new CadAuditSubject();
                held.ParameterValues["Width"] = "0.5";
                held.ParameterDisplay["Width"] = "152,4 mm";
                var write = new CadParameterWrite { Parameter = "Width", Value = new JValue(0.5) };
                Assert.Null(CadUpdateRules.ParameterDivergence(write, held));
                held.ParameterValues["Width"] = "0.75";
                Assert.NotNull(CadUpdateRules.ParameterDivergence(write, held));
            });
        }
    }
}
