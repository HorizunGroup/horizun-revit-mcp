using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class JunctionRuleClassifierTests
    {
        private static List<JunctionRuleInfo> Rules(params (string name, string part)[] r)
        {
            var l = new List<JunctionRuleInfo>();
            for (int i = 0; i < r.Length; i++) l.Add(new JunctionRuleInfo(i, r[i].name, r[i].part));
            return l;
        }

        [Fact]
        public void Spanish_pvc_dwv_rules_resolve_without_english_names()
        {
            var c = JunctionRuleClassifier.Choose(Rules(
                ("FabricanteX_UnionesDeTuberia_SanitariaYee: Var.", "Tee"),
                ("M_Sanitario con te - PVC - Serie 40 - DWV: Estándar", "Tee")));
            Assert.Null(c.Problem);
            Assert.Equal(0, c.WyeIndex);
            Assert.Equal(1, c.TeeIndex);
        }

        [Fact]
        public void English_wye_and_tee_resolve()
        {
            var c = JunctionRuleClassifier.Choose(Rules(("M_Wye - PVC: Standard", "Tee"), ("M_Tee - PVC: Standard", "Tee")));
            Assert.Null(c.Problem);
            Assert.Equal(0, c.WyeIndex);
            Assert.Equal(1, c.TeeIndex);
        }

        [Fact]
        public void Wye_part_type_is_wye_whatever_its_name()
        {
            Assert.Equal(JunctionRuleKind.Wye, JunctionRuleClassifier.Classify("Wye", "Ramal"));
            var c = JunctionRuleClassifier.Choose(Rules(("Ramal: A", "Wye"), ("Derivación: B", "Tee")));
            Assert.Equal(0, c.WyeIndex);
            Assert.Equal(1, c.TeeIndex);
        }

        [Fact]
        public void LateralTee_is_wye_like()
        {
            Assert.Equal(JunctionRuleKind.Wye, JunctionRuleClassifier.Classify("LateralTee", "X: Y"));
        }

        [Fact]
        public void Spanish_Y_named_manufacturer_wye_marked_Tee_is_promoted()
        {
            Assert.Equal(JunctionRuleKind.Wye, JunctionRuleClassifier.Classify("Tee", "M_Conexión en Y - PVC: Estándar"));
            Assert.Equal(JunctionRuleKind.Wye, JunctionRuleClassifier.Classify("Tee", "Te a 45\u00b0: 100"));
            Assert.Equal(JunctionRuleKind.Tee, JunctionRuleClassifier.Classify("Tee", "Te reducción y tapón"));
            Assert.Equal(JunctionRuleKind.Tee, JunctionRuleClassifier.Classify("Tee", "TE RECTA Y REDUCIDA: 50"));
            Assert.Equal(JunctionRuleKind.Tee, JunctionRuleClassifier.Classify("Tee", "Te Reducción Y Tapón"));
            Assert.Equal(JunctionRuleKind.Tee, JunctionRuleClassifier.Classify("Tee", "Te PVC 45 mm: Estándar"));
            Assert.Equal(JunctionRuleKind.Wye, JunctionRuleClassifier.Classify("Tee", "Conexión tipo Y: 50"));
            Assert.Equal(JunctionRuleKind.Wye, JunctionRuleClassifier.Classify("Tee", "Tee Y - PVC"));
            Assert.Equal(JunctionRuleKind.Tee, JunctionRuleClassifier.Classify("Tee", "Serie 40 - DWV: Estándar"));
        }

        [Fact]
        public void A_name_never_creates_a_tee_from_another_part_type()
        {
            Assert.Equal(JunctionRuleKind.Other, JunctionRuleClassifier.Classify("Elbow", "Tee elbow"));
            Assert.Equal(JunctionRuleKind.Other, JunctionRuleClassifier.Classify("", "tee"));
        }

        [Fact]
        public void No_tee_available_refuses_naming_the_rules_seen()
        {
            var c = JunctionRuleClassifier.Choose(Rules(("Yee X: Var.", "Tee"), ("Cruz: A", "Cross")));
            Assert.Equal(0, c.WyeIndex);
            Assert.Equal(-1, c.TeeIndex);
            Assert.Contains("perpendicular tee", c.Problem);
            Assert.Contains("[0]", c.Problem);
            Assert.Contains("Yee X: Var.", c.Problem);
            Assert.Contains("[1]", c.Problem);
            Assert.Contains("Cross", c.Problem);
        }

        [Fact]
        public void No_wye_refuses_naming_the_rules_seen()
        {
            var c = JunctionRuleClassifier.Choose(Rules(("Te: A", "Tee")));
            Assert.Equal(-1, c.WyeIndex);
            Assert.Contains("no junction rule is a wye", c.Problem);
            Assert.Contains("Te: A", c.Problem);
        }

        [Fact]
        public void Part_type_wye_beats_a_name_promoted_tee_whatever_the_index()
        {
            var c = JunctionRuleClassifier.Choose(Rules(("Yee fabricante: A", "Tee"), ("Ramal: B", "Wye"), ("Te: C", "Tee")));
            Assert.Equal(1, c.WyeIndex);
            Assert.Equal(2, c.TeeIndex);
        }

        [Fact]
        public void Wye_at_index_above_zero_with_tee_at_zero()
        {
            var c = JunctionRuleClassifier.Choose(Rules(("Te: A", "Tee"), ("Ramal: B", "LateralTee")));
            Assert.Null(c.Problem);
            Assert.Equal(1, c.WyeIndex);
            Assert.Equal(0, c.TeeIndex);
        }

        [Fact]
        public void Unknown_part_type_is_never_promoted_by_name()
        {
            Assert.Equal(JunctionRuleKind.Other, JunctionRuleClassifier.Classify("", "Yee: A"));
            var c = JunctionRuleClassifier.Choose(Rules(("Yee: A", ""), ("Te: B", "Tee")));
            Assert.Equal(-1, c.WyeIndex);
            Assert.Contains("part type unknown", c.Problem);
        }
    }
}
