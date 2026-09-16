// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE CASES WHERE AN IDS IMPLEMENTATION IS PLAUSIBLE AND WRONG.
//
// Each of these is something that produces a clean-looking report from a broken
// evaluator, and none of them is visible by reading the output:
//
//   An unanchored pattern accepts "XXDT01YY" for /DT[0-9]{2}/, so every naming
//   convention passes.
//   A `prohibited` cardinality read as `required` passes every model, because the
//   thing it forbids is usually absent.
//   An applicability of (0,0) whose requirements are evaluated anyway fills the
//   report with the wrong defect, loudly.
//   A property read only off the occurrence reports "no Pset_WallCommon" for a
//   wall whose whole set lives on its type - which is where most exporters put it.
//   A partOf walked one hop answers "no" for a column in a storey in a building.
//   An unsupported construction treated as satisfied turns a gap into a pass.
//
// NOT EXECUTED. Written under a standing instruction that authorises writing
// tests and does not authorise running them. No claim anywhere in this campaign
// rests on any of this passing.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using System.Xml;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class IdsTests
    {
        // =====================================================================
        // Restrictions
        // =====================================================================

        [Fact]
        public void A_pattern_must_match_the_WHOLE_value()
        {
            // THE ONE THAT MATTERS MOST. XML Schema anchors xs:pattern; .NET's IsMatch does
            // not. Without anchoring every naming-convention check ever written passes.
            IdsValue value = Restriction("<xs:pattern value=\"DT[0-9]{2}\"/>");
            Assert.True(IdsRestriction.Satisfies("DT01", value).Satisfied);
            Assert.False(IdsRestriction.Satisfies("XXDT01YY", value).Satisfied);
            Assert.False(IdsRestriction.Satisfies("DT013", value).Satisfied);
            Assert.False(IdsRestriction.Satisfies("ADT01", value).Satisfied);
        }

        [Fact]
        public void An_enumeration_is_exact_and_ordinal()
        {
            IdsValue value = Restriction(
                "<xs:enumeration value=\"concrete\"/><xs:enumeration value=\"steel\"/>");
            Assert.True(IdsRestriction.Satisfies("concrete", value).Satisfied);
            Assert.False(IdsRestriction.Satisfies("Concrete", value).Satisfied);
            Assert.False(IdsRestriction.Satisfies("timber", value).Satisfied);
        }

        [Fact]
        public void Bounds_are_numeric_and_a_non_number_is_not_a_failing_number()
        {
            // "approx 3.2 m" is not "out of range". Reporting it as one sends somebody
            // looking for the wrong defect.
            IdsValue value = Restriction(
                "<xs:minInclusive value=\"3\"/><xs:maxExclusive value=\"10\"/>");
            Assert.True(IdsRestriction.Satisfies("3", value).Satisfied);
            Assert.True(IdsRestriction.Satisfies("9.99", value).Satisfied);
            Assert.False(IdsRestriction.Satisfies("10", value).Satisfied);
            Assert.False(IdsRestriction.Satisfies("2.9", value).Satisfied);

            IdsMatch text = IdsRestriction.Satisfies("approx 3.2 m", value);
            Assert.True(text.Undecidable);
            Assert.Contains("not a number", text.Reason);
        }

        [Fact]
        public void Lengths_count_characters()
        {
            Assert.True(IdsRestriction.Satisfies("ABC", Restriction("<xs:length value=\"3\"/>")).Satisfied);
            Assert.False(IdsRestriction.Satisfies("AB", Restriction("<xs:length value=\"3\"/>")).Satisfied);
            Assert.True(IdsRestriction.Satisfies("ABCDE", Restriction("<xs:maxLength value=\"5\"/>")).Satisfied);
            Assert.False(IdsRestriction.Satisfies("ABCDEF", Restriction("<xs:maxLength value=\"5\"/>")).Satisfied);
        }

        [Fact]
        public void A_restriction_with_no_facets_is_an_authoring_defect_not_a_free_pass()
        {
            IdsValue value = Restriction("");
            Assert.NotNull(value.Defect);
            IdsMatch match = IdsRestriction.Satisfies("anything", value);
            Assert.True(match.Undecidable);
            Assert.False(match.Satisfied);
        }

        [Fact]
        public void An_unsupported_facet_is_never_reported_as_satisfied()
        {
            // A construction this build does not evaluate must not pass. An unevaluated
            // constraint that passes is worse than one that is refused.
            IdsValue value = Restriction("<xs:whiteSpace value=\"collapse\"/>");
            Assert.Contains("whiteSpace", value.Unsupported);
            IdsMatch match = IdsRestriction.Satisfies("anything", value);
            Assert.False(match.Satisfied);
            Assert.True(match.Undecidable);
        }

        [Fact]
        public void An_absent_value_is_undecidable_here_not_a_failure()
        {
            // Whether absence fails is CARDINALITY's decision, not the comparison's.
            IdsMatch match = IdsRestriction.Satisfies(null, Simple("X"));
            Assert.True(match.Undecidable);
        }

        // =====================================================================
        // Cardinality
        // =====================================================================

        [Fact]
        public void Required_optional_and_prohibited_are_not_the_same_rule()
        {
            IdsMatch satisfied = IdsMatch.Yes();
            IdsMatch absent = IdsMatch.Missing("not there");
            IdsMatch wrong = IdsMatch.No("present and wrong");

            Assert.Equal(IdsOutcome.Pass, IdsRun.Decide(IdsCardinality.Required, satisfied));
            Assert.Equal(IdsOutcome.Fail, IdsRun.Decide(IdsCardinality.Required, absent));
            Assert.Equal(IdsOutcome.Fail, IdsRun.Decide(IdsCardinality.Required, wrong));

            // OPTIONAL: absent is fine, present and wrong is not.
            Assert.Equal(IdsOutcome.Pass, IdsRun.Decide(IdsCardinality.Optional, satisfied));
            Assert.Equal(IdsOutcome.Pass, IdsRun.Decide(IdsCardinality.Optional, absent));
            Assert.Equal(IdsOutcome.Fail, IdsRun.Decide(IdsCardinality.Optional, wrong));

            // PROHIBITED INVERTS. Read as "required" - the commonest mistake - it would pass
            // every model, because the thing it forbids is usually absent.
            Assert.Equal(IdsOutcome.Fail, IdsRun.Decide(IdsCardinality.Prohibited, satisfied));
            Assert.Equal(IdsOutcome.Pass, IdsRun.Decide(IdsCardinality.Prohibited, absent));
            Assert.Equal(IdsOutcome.Pass, IdsRun.Decide(IdsCardinality.Prohibited, wrong));
        }

        [Fact]
        public void Undecidable_never_becomes_a_pass_under_any_cardinality()
        {
            IdsMatch unknown = IdsMatch.Unknown("nobody could tell");
            foreach (IdsCardinality cardinality in new[]
                     { IdsCardinality.Required, IdsCardinality.Optional, IdsCardinality.Prohibited })
                Assert.Equal(IdsOutcome.NotDecidable, IdsRun.Decide(cardinality, unknown));
        }

        // =====================================================================
        // Parsing
        // =====================================================================

        [Fact]
        public void A_file_in_another_namespace_is_refused_rather_than_read()
        {
            // The namespace IS the version handshake. Reading one grammar with another's
            // rules produces confident findings nobody agreed to.
            var document = new XmlDocument();
            document.LoadXml("<ids xmlns=\"http://example.com/not-ids\"><info/></ids>");
            string error;
            Assert.Null(IdsReader.Parse(document, out error));
            Assert.Contains("version handshake", error);
        }

        [Fact]
        public void The_applicability_occurs_table_is_read_as_the_manual_states_it()
        {
            Assert.Equal(IdsApplicabilityMode.Required, Spec("minOccurs=\"1\" maxOccurs=\"unbounded\"").Mode);
            Assert.Equal(IdsApplicabilityMode.Optional, Spec("minOccurs=\"0\" maxOccurs=\"unbounded\"").Mode);
            // (0,0) is PROHIBITED, and its requirements must not be evaluated at all.
            Assert.Equal(IdsApplicabilityMode.Prohibited, Spec("minOccurs=\"0\" maxOccurs=\"0\"").Mode);
            // The schema's default, when nothing is stated.
            Assert.Equal(IdsApplicabilityMode.Required, Spec("").Mode);
        }

        [Fact]
        public void Every_one_of_the_six_facets_is_read()
        {
            IdsFile file = Ids(@"
              <specification name=""all six"" ifcVersion=""IFC4"">
                <applicability>
                  <entity><name><simpleValue>IFCWALL</simpleValue></name></entity>
                </applicability>
                <requirements>
                  <attribute><name><simpleValue>Name</simpleValue></name></attribute>
                  <classification><system><simpleValue>Uniclass</simpleValue></system></classification>
                  <property cardinality=""prohibited"">
                    <propertySet><simpleValue>Pset_WallCommon</simpleValue></propertySet>
                    <baseName><simpleValue>LoadBearing</simpleValue></baseName>
                  </property>
                  <material cardinality=""optional""/>
                  <partOf relation=""IFCRELAGGREGATES"">
                    <entity><name><simpleValue>IFCBUILDING</simpleValue></name></entity>
                  </partOf>
                </requirements>
              </specification>");

            IdsSpecification specification = file.Specifications.Single();
            Assert.NotNull(specification.Requirements);
            Assert.Single(specification.Requirements.Attributes);
            Assert.Single(specification.Requirements.Classifications);
            Assert.Single(specification.Requirements.Properties);
            Assert.Single(specification.Requirements.Materials);
            Assert.Single(specification.Requirements.PartOf);

            Assert.Equal(IdsCardinality.Prohibited, specification.Requirements.Properties[0].Cardinality);
            Assert.Equal(IdsCardinality.Optional, specification.Requirements.Materials[0].Cardinality);
            // The schema's default when the attribute is absent.
            Assert.Equal(IdsCardinality.Required, specification.Requirements.Attributes[0].Cardinality);
            Assert.Equal("IFCRELAGGREGATES", specification.Requirements.PartOf[0].Relation);
        }

        [Fact]
        public void Optional_is_refused_on_partOf_because_the_manual_says_it_means_nothing()
        {
            IdsFile file = Ids(@"
              <specification name=""bad cardinality"" ifcVersion=""IFC4"">
                <applicability><entity><name><simpleValue>IFCWALL</simpleValue></name></entity></applicability>
                <requirements>
                  <partOf cardinality=""optional"">
                    <entity><name><simpleValue>IFCBUILDING</simpleValue></name></entity>
                  </partOf>
                </requirements>
              </specification>");
            Assert.Equal(IdsCardinality.Required, file.Specifications[0].Requirements.PartOf[0].Cardinality);
            Assert.Contains(file.Problems, p => p.Contains("optional"));
        }

        [Fact]
        public void An_unknown_element_inside_a_requirements_block_is_named_not_skipped()
        {
            IdsFile file = Ids(@"
              <specification name=""from the future"" ifcVersion=""IFC4"">
                <applicability><entity><name><simpleValue>IFCWALL</simpleValue></name></entity></applicability>
                <requirements><geometry/></requirements>
              </specification>");
            Assert.Contains(file.Problems, p => p.Contains("geometry"));
        }

        [Fact]
        public void An_applicability_with_no_facets_is_refused_rather_than_applied_to_everything()
        {
            IdsFile file = Ids(@"
              <specification name=""everything"" ifcVersion=""IFC4"">
                <applicability/>
              </specification>");
            Assert.NotNull(file.Specifications[0].Undecidable);
            Assert.Contains("EVERY element", file.Specifications[0].Undecidable);
        }

        // =====================================================================
        // IFC evaluation
        // =====================================================================

        [Fact]
        public void A_property_on_the_TYPE_is_found_for_its_occurrences()
        {
            // The defect this catches: reading only the occurrence reports "no
            // Pset_WallCommon" for a wall whose whole set lives on its type, which is where
            // most exporters put it.
            // IfcWallType in IFC4: GlobalId, OwnerHistory, Name, Description,
            // ApplicableOccurrence, HasPropertySets (5), RepresentationMaps, Tag,
            // ElementType, PredefinedType. This fixture once put the set list at 6,
            // where RepresentationMaps lives, and the test failed for the fixture.
            IfcStepReader.Document ifc = Ifc(
                "#1=IFCWALL('0aaaaaaaaaaaaaaaaaaaa1',$,'W1',$,$,$,$,$);\n" +
                "#2=IFCWALLTYPE('0aaaaaaaaaaaaaaaaaaaa2',$,'Basic',$,$,(#3),$,$,$,.STANDARD.);\n" +
                "#3=IFCPROPERTYSET('0aaaaaaaaaaaaaaaaaaaa3',$,'Pset_WallCommon',$,(#4));\n" +
                "#4=IFCPROPERTYSINGLEVALUE('LoadBearing',$,IFCBOOLEAN(.T.),$);\n" +
                "#5=IFCRELDEFINESBYTYPE('0aaaaaaaaaaaaaaaaaaaa5',$,$,$,(#1),#2);");

            IdsIfcEvaluator.Index index = IdsIfcEvaluator.Build(ifc);
            var facet = new IdsPropertyFacet
            {
                PropertySet = Simple("Pset_WallCommon"),
                BaseName = Simple("LoadBearing"),
                Value = Simple("TRUE")
            };
            string observed;
            IdsMatch match = IdsIfcEvaluator.MatchesProperty(index, ifc.ById[1], facet, out observed);
            Assert.True(match.Satisfied);
        }

        [Fact]
        public void An_IFC_boolean_is_compared_as_IDS_spells_it()
        {
            // IFC writes .T. and IDS writes TRUE. Compared raw, every boolean requirement
            // ever written fails.
            Assert.Equal("TRUE", IdsIfcEvaluator.Unwrap("IFCBOOLEAN(.T.)"));
            Assert.Equal("FALSE", IdsIfcEvaluator.Unwrap("IFCBOOLEAN(.F.)"));
            Assert.Equal("Concrete", IdsIfcEvaluator.Unwrap("IFCLABEL('Concrete')"));
            Assert.Equal("2.5", IdsIfcEvaluator.Unwrap("IFCREAL(2.5)"));
            Assert.Null(IdsIfcEvaluator.Unwrap("$"));
        }

        [Fact]
        public void PartOf_is_traversed_recursively()
        {
            // A column contained in a storey aggregated into a building IS part of the
            // building. A single-hop implementation answers no, confidently.
            IfcStepReader.Document ifc = Ifc(
                "#1=IFCCOLUMN('0aaaaaaaaaaaaaaaaaaaa1',$,'C1',$,$,$,$,$,.COLUMN.);\n" +
                "#2=IFCBUILDINGSTOREY('0aaaaaaaaaaaaaaaaaaaa2',$,'L1',$,$,$,$,$,.ELEMENT.,0.);\n" +
                "#3=IFCBUILDING('0aaaaaaaaaaaaaaaaaaaa3',$,'B',$,$,$,$,$,.ELEMENT.,$,$,$);\n" +
                "#4=IFCRELCONTAINEDINSPATIALSTRUCTURE('0aaaaaaaaaaaaaaaaaaaa4',$,$,$,(#1),#2);\n" +
                "#5=IFCRELAGGREGATES('0aaaaaaaaaaaaaaaaaaaa5',$,$,$,#3,(#2));");

            IdsIfcEvaluator.Index index = IdsIfcEvaluator.Build(ifc);
            var facet = new IdsPartOfFacet
            {
                Entity = new IdsEntityFacet { Name = Simple("IFCBUILDING") }
            };
            string observed;
            Assert.True(IdsIfcEvaluator.MatchesPartOf(index, ifc.ById[1], facet, out observed).Satisfied);
        }

        [Fact]
        public void A_prohibited_applicability_does_not_evaluate_its_requirements()
        {
            // (0,0) means no element may match. Evaluating the requirements as well would
            // fill the report with findings about elements that must not exist - the wrong
            // defect, loudly.
            IdsFile ids = Ids(@"
              <specification name=""no load bearing walls"" ifcVersion=""IFC4"">
                <applicability minOccurs=""0"" maxOccurs=""0"">
                  <entity><name><simpleValue>IFCWALL</simpleValue></name></entity>
                </applicability>
                <requirements>
                  <attribute><name><simpleValue>Name</simpleValue></name>
                    <value><simpleValue>never matched</simpleValue></value></attribute>
                </requirements>
              </specification>");

            IfcStepReader.Document ifc = Ifc(
                "#1=IFCWALL('0aaaaaaaaaaaaaaaaaaaa1',$,'W1',$,$,$,$,$);");

            IdsReport report = IdsRun.Validate(ids, ifc, "test.ids");
            IdsSpecificationResult result = report.Results.Single();
            Assert.Equal(IdsOutcome.Fail, result.Outcome);
            Assert.All(result.Findings, f => Assert.Equal("applicability", f.Facet));
        }

        [Fact]
        public void A_schema_mismatch_is_reported_rather_than_evaluated()
        {
            IdsFile ids = Ids(@"
              <specification name=""for 4x3 only"" ifcVersion=""IFC4X3_ADD2"">
                <applicability><entity><name><simpleValue>IFCWALL</simpleValue></name></entity></applicability>
              </specification>");
            IfcStepReader.Document ifc = Ifc("#1=IFCWALL('0aaaaaaaaaaaaaaaaaaaa1',$,'W1',$,$,$,$,$);",
                                             "IFC2X3");
            IdsReport report = IdsRun.Validate(ids, ifc, "test.ids");
            Assert.Equal(IdsOutcome.NotDecidable, report.Results.Single().Outcome);
            Assert.Contains("FILE_SCHEMA", report.Results.Single().Undecidable);
        }

        [Fact]
        public void A_required_specification_with_no_matching_element_fails_the_model_not_an_element()
        {
            IdsFile ids = Ids(@"
              <specification name=""there must be walls"" ifcVersion=""IFC4"">
                <applicability minOccurs=""1"">
                  <entity><name><simpleValue>IFCWALL</simpleValue></name></entity>
                </applicability>
              </specification>");
            IfcStepReader.Document ifc = Ifc("#1=IFCSLAB('0aaaaaaaaaaaaaaaaaaaa1',$,'S1',$,$,$,$,$,.FLOOR.);");
            IdsSpecificationResult result = IdsRun.Validate(ids, ifc, "t.ids").Results.Single();
            Assert.Equal(IdsOutcome.Fail, result.Outcome);
            Assert.Null(result.Findings.Single().GlobalId);   // there is no element to name
        }

        [Fact]
        public void An_unresolvable_attribute_is_not_decidable_rather_than_guessed_at()
        {
            // Resolving an arbitrary IFC attribute by NAME needs the EXPRESS schema. An index
            // guessed from a class name reads a different attribute and reports it
            // confidently.
            IfcStepReader.Document ifc = Ifc("#1=IFCWALL('0aaaaaaaaaaaaaaaaaaaa1',$,'W1',$,$,$,$,'T1');");
            IdsIfcEvaluator.Index index = IdsIfcEvaluator.Build(ifc);
            var facet = new IdsAttributeFacet { Name = Simple("OverallHeight") };
            IdsMatch match = IdsIfcEvaluator.MatchesAttribute(index, ifc.ById[1], facet);
            Assert.True(match.Undecidable);
            Assert.Contains("EXPRESS", match.Reason);
        }

        [Fact]
        public void A_specification_with_one_undecidable_element_is_not_a_pass()
        {
            IdsFile ids = Ids(@"
              <specification name=""height"" ifcVersion=""IFC4"">
                <applicability><entity><name><simpleValue>IFCWALL</simpleValue></name></entity></applicability>
                <requirements>
                  <attribute><name><simpleValue>OverallHeight</simpleValue></name></attribute>
                </requirements>
              </specification>");
            IfcStepReader.Document ifc = Ifc("#1=IFCWALL('0aaaaaaaaaaaaaaaaaaaa1',$,'W1',$,$,$,$,$);");
            IdsSpecificationResult result = IdsRun.Validate(ids, ifc, "t.ids").Results.Single();
            Assert.Equal(IdsOutcome.NotDecidable, result.Outcome);
            Assert.Equal(0, result.Passing);
        }

        // =====================================================================
        // Fixtures
        // =====================================================================

        private static IdsValue Simple(string text)
        {
            var document = new XmlDocument();
            document.LoadXml("<name xmlns=\"http://standards.buildingsmart.org/IDS\">" +
                             "<simpleValue>" + text + "</simpleValue></name>");
            return IdsRestriction.Read(document.DocumentElement);
        }

        private static IdsValue Restriction(string facets)
        {
            var document = new XmlDocument();
            document.LoadXml(
                "<value xmlns=\"http://standards.buildingsmart.org/IDS\" " +
                "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" +
                "<xs:restriction base=\"xs:string\">" + facets + "</xs:restriction></value>");
            return IdsRestriction.Read(document.DocumentElement);
        }

        private static IdsSpecification Spec(string occurs)
        {
            IdsFile file = Ids(@"
              <specification name=""s"" ifcVersion=""IFC4"">
                <applicability " + occurs + @">
                  <entity><name><simpleValue>IFCWALL</simpleValue></name></entity>
                </applicability>
              </specification>");
            return file.Specifications.Single();
        }

        private static IdsFile Ids(string specifications)
        {
            var document = new XmlDocument();
            document.LoadXml(
                "<ids xmlns=\"http://standards.buildingsmart.org/IDS\" " +
                "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" +
                "<info><title>test</title></info>" +
                "<specifications>" + specifications + "</specifications></ids>");
            string error;
            IdsFile file = IdsReader.Parse(document, out error);
            Assert.Null(error);
            return file;
        }

        [Theory]
        [InlineData("FILE_SCHEMA(('IFC4'));", "IFC4")]
        [InlineData("FILE_SCHEMA(('IFC2X3'));", "IFC2X3")]
        [InlineData("FILE_SCHEMA (( 'IFC4X3_ADD2' ));", "IFC4X3_ADD2")]
        [InlineData("FILE_DESCRIPTION(('ViewDefinition (x)'),'2;1');\nFILE_SCHEMA(('IFC4'));", "IFC4")]
        public void The_file_schema_is_read_through_its_nested_parentheses(string header, string expected)
        {
            // MEASURED: the reader cut FILE_SCHEMA at the first ')' and read the
            // schema as "('IFC4'", which no schema family matches - so every
            // specification of every file came back not decidable.
            string error;
            IfcStepReader.Document ifc = IfcStepReader.Parse(
                "ISO-10303-21;\nHEADER;\n" + header + "\nENDSEC;\nDATA;\n" +
                "#1=IFCWALL('0aaaaaaaaaaaaaaaaaaaa1',$,'W1',$,$,$,$,$);\nENDSEC;\nEND-ISO-10303-21;", out error);
            Assert.Null(error);
            Assert.Equal(expected, ifc.SchemaIdentifier);
        }

        private static IfcStepReader.Document Ifc(string data, string schema = "IFC4")
        {
            string error;
            IfcStepReader.Document ifc = IfcStepReader.Parse(
                "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('" + schema + "'));\nENDSEC;\nDATA;\n" +
                data + "\nENDSEC;\nEND-ISO-10303-21;", out error);
            Assert.Null(error);
            return ifc;
        }
    }
}
