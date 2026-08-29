// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE CHANGES A DRAWING CAN NEVER REPORT.
//
// A grid renamed by hand. A room renumbered. A fire rating edited from 60 to 30.
// None of them moves a line, so every comparison of geometry - which is what the
// incremental update was, entirely - reports the model as unchanged. And they are
// exactly the changes that matter most, because the requirement set is the SOLE
// source of those values: a difference is not a drawing that moved, it is a
// person who decided something.
//
// The audit reports them with a code each. The update reports them as ONE
// classification, and that is deliberate: the update's question is not "what
// differs" but "what should happen", and the answer to all of them is the same -
// nobody in an unattended process can reconcile a value a person chose.
//
// Two cases are deliberately NOT divergences, and the tests below pin both:
// a parameter the element does not carry at all, and one that could not be read.
// Reporting either as a person's decision sends somebody looking for a decision
// that was never made.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadUpdateDivergenceTests
    {
        private const string Rev = "sha-of-the-drawing";

        private static CadRequirementSet Set(string extra = "")
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'walls', 'version': '1.0.0', 'title': 'Walls' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'walls', 'precedence': 10, 'layers': ['A-WALL*'], 'produces': 'wall',
                          'category': 'OST_Walls', 'height_mm': 3000EXTRA,
                          'geometry': { 'from': 'double_lines', 'min_thickness_mm': 100,
                                        'max_thickness_mm': 400, 'min_overlap_fraction': 0.5 } }]
            }".Replace('\'', '"').Replace("EXTRA", extra);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static List<CadSegment> Wall(double x0, double x1)
        {
            return new List<CadSegment>
            {
                new CadSegment(new CadPoint(x0, -100), new CadPoint(x1, -100), "A-WALL"),
                new CadSegment(new CadPoint(x0, 100), new CadPoint(x1, 100), "A-WALL")
            };
        }

        private static List<CadCandidate> Read(CadRequirementSet set)
        {
            return CadInterpretationRules.Interpret(Wall(0, 6000), set, Rev).Candidates.ToList();
        }

        /// <summary>The element as built, sitting exactly where it was put.</summary>
        private static CadAuditSubject Built(CadCandidate from, CadRequirementSet set)
        {
            return new CadAuditSubject
            {
                ElementId = 1001,
                Category = "Walls",
                TypeName = "Generic - 200mm",
                Geometry = new List<CadPoint>(from.Geometry),
                Provenance = new CadProvenance
                {
                    SchemaVersion = 1,
                    CandidateId = from.Id,
                    GeometryId = from.GeometryId,
                    SemanticId = from.SemanticId,
                    RuleId = from.RuleId,
                    Layer = from.Layer,
                    RequirementSetSha256 = set.Sha256,
                    SourceFileSha256 = Rev,
                    BuiltGeometry = CadUpdateRules.Encode(from.Geometry)
                }
            };
        }

        private static CadUpdate Plan(CadRequirementSet set, CadAuditSubject held)
        {
            return CadUpdateRules.Plan(Read(set), new List<CadAuditSubject> { held }, set, Rev);
        }

        private static CadUpdateAction Diverged(CadUpdate update)
        {
            return update.Actions.FirstOrDefault(a => a.Classification == CadChange.ManuallyDiverged);
        }

        // ------------------------------------------------------------- parameters

        [Fact]
        public void A_value_the_SET_declares_and_the_model_does_not_hold_is_a_person_having_changed_it()
        {
            CadRequirementSet set = Set(", 'parameters': { 'Fire Rating': '60' }");
            CadCandidate c = Read(set)[0];
            CadAuditSubject held = Built(c, set);
            held.ParameterValues["Fire Rating"] = "30";

            CadUpdateAction action = Diverged(Plan(set, held));

            Assert.NotNull(action);
            Assert.Equal("review", action.Kind);
            Assert.False(action.Automatic);
            Assert.Contains("Fire Rating", (string)action.Evidence["field"]);
            Assert.Equal("60", (string)action.Evidence["set_says"]);
            Assert.Equal("30", (string)action.Evidence["model_holds"]);
        }

        [Fact]
        public void And_it_is_NOT_applied_automatically_however_certain_the_set_is()
        {
            // Overwriting discards the decision; staying silent hides it. Neither
            // is something an unattended run may choose on somebody's behalf.
            CadRequirementSet set = Set(", 'parameters': { 'Fire Rating': '60' }");
            CadAuditSubject held = Built(Read(set)[0], set);
            held.ParameterValues["Fire Rating"] = "30";

            CadUpdate update = Plan(set, held);

            Assert.Equal(0, update.Actions.Count(a => a.Automatic));
            // The reason is in the action, because a reader deciding whether to
            // act needs both halves of it and not just the verdict.
            Assert.Contains("overwriting it would discard", Diverged(update).Says);
            Assert.Contains("hide it", Diverged(update).Says);
        }

        [Fact]
        public void A_value_that_AGREES_is_not_news()
        {
            CadRequirementSet set = Set(", 'parameters': { 'Fire Rating': '60' }");
            CadAuditSubject held = Built(Read(set)[0], set);
            held.ParameterValues["Fire Rating"] = "60";

            CadUpdate update = Plan(set, held);

            Assert.Null(Diverged(update));
            Assert.Equal(CadChange.Unchanged, update.Actions[0].Classification);
        }

        [Fact]
        public void A_parameter_the_element_does_NOT_CARRY_is_not_a_person_having_changed_it()
        {
            // That is the audit's parameter_missing and it means something else
            // entirely: nobody changed it, the element cannot hold it. Reporting
            // it here would send somebody looking for a decision never made.
            CadRequirementSet set = Set(", 'parameters': { 'Fire Rating': '60' }");
            CadAuditSubject held = Built(Read(set)[0], set);

            Assert.Null(Diverged(Plan(set, held)));
        }

        [Fact]
        public void A_parameter_that_could_not_be_READ_is_not_one_either()
        {
            // "Could not look" is not "somebody changed it", and the difference
            // is the whole reason unreadable is tracked separately at all.
            CadRequirementSet set = Set(", 'parameters': { 'Fire Rating': '60' }");
            CadAuditSubject held = Built(Read(set)[0], set);
            held.ParametersUnreadable.Add("Fire Rating");

            Assert.Null(Diverged(Plan(set, held)));
        }

        // ------------------------------------------------------------------ names

        [Fact]
        public void A_NAME_the_set_assigned_and_the_model_no_longer_holds_is_reported()
        {
            CadRequirementSet set = Set();
            CadCandidate c = Read(set)[0];
            c.AssignedName = "A";
            CadAuditSubject held = Built(c, set);
            held.ElementName = "A1";

            CadUpdateAction action = Diverged(CadUpdateRules.Plan(
                new List<CadCandidate> { c }, new List<CadAuditSubject> { held }, set, Rev));

            Assert.NotNull(action);
            Assert.Equal("name", (string)action.Evidence["field"]);
            Assert.Equal("A", (string)action.Evidence["set_says"]);
            Assert.Equal("A1", (string)action.Evidence["model_holds"]);
        }

        [Fact]
        public void A_NUMBER_is_reported_separately_from_a_name()
        {
            // A room carries both and they are edited for different reasons; a
            // report that folded them together would name the wrong one.
            CadRequirementSet set = Set();
            CadCandidate c = Read(set)[0];
            c.AssignedName = "Office";
            c.AssignedNumber = "101";
            CadAuditSubject held = Built(c, set);
            held.ElementName = "Office";
            held.ElementNumber = "102";

            CadUpdateAction action = Diverged(CadUpdateRules.Plan(
                new List<CadCandidate> { c }, new List<CadAuditSubject> { held }, set, Rev));

            Assert.NotNull(action);
            Assert.Equal("number", (string)action.Evidence["field"]);
            Assert.Equal("102", (string)action.Evidence["model_holds"]);
        }

        [Fact]
        public void A_name_the_set_never_assigned_is_never_compared()
        {
            // Most rules assign no name at all, and a wall's name is its type's.
            // Comparing one the set never chose would report a difference against
            // nothing.
            CadRequirementSet set = Set();
            CadAuditSubject held = Built(Read(set)[0], set);
            held.ElementName = "whatever Revit calls this";

            Assert.Null(Diverged(Plan(set, held)));
        }

        [Fact]
        public void Only_ONE_divergence_is_reported_per_element()
        {
            // One review is one decision about one element. Six differences on the
            // same wall are six entries somebody has to reconcile into the same
            // single answer, and the next run surfaces the next one.
            CadRequirementSet set = Set(", 'parameters': { 'Fire Rating': '60', 'Comments': 'as drawn' }");
            CadCandidate c = Read(set)[0];
            c.AssignedName = "A";
            CadAuditSubject held = Built(c, set);
            held.ElementName = "B";
            held.ParameterValues["Fire Rating"] = "30";
            held.ParameterValues["Comments"] = "edited";

            CadUpdate update = CadUpdateRules.Plan(
                new List<CadCandidate> { c }, new List<CadAuditSubject> { held }, set, Rev);

            Assert.Single(update.Actions, a => a.Classification == CadChange.ManuallyDiverged);
        }

        [Fact]
        public void The_classification_is_one_the_published_vocabulary_already_carries()
        {
            // A thirteenth classification would break every reader that switches
            // exhaustively over the twelve, and this is not a thirteenth kind of
            // news - it is the one that already means "a person changed this".
            Assert.Contains(CadChange.ManuallyDiverged, CadChange.All);
        }
    }
}
