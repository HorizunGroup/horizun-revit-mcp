// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A HELD CHANGE HAS A WAY OUT: A PERSON'S DECISION, CARRIED OUT BY A TYPED COMMAND.
//
// MEASURED on a face-hosted receptacle in a test model: a turn in its own face and
// a type change keep the element; a turn onto the other face is refused by Revit,
// a mirror leaves it hosted on the old face, and a move off the face detaches it.
// So a person can resolve a retype or a reorientation in place, keep what stands,
// or ask for a replacement - which is a migration plan, never an automatic action.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadDecisionTests
    {
        private static CadUpdate Held(params (long id, string classification, string kind)[] rows)
        {
            var u = new CadUpdate();
            foreach (var r in rows)
                u.Actions.Add(new CadUpdateAction
                {
                    ElementId = r.id, Classification = r.classification, Kind = r.kind, Automatic = r.kind == "leave",
                    Says = "held."
                });
            return u;
        }

        private static List<CadDecision> D(params (long id, string decision)[] rows) =>
            rows.Select(r => new CadDecision { ElementId = r.id, Decision = r.decision }).ToList();

        [Fact]
        public void A_retype_and_a_turn_in_the_face_become_automatic_kinds()
        {
            CadUpdate u = Held((1, CadChange.Resized, "review"), (2, CadChange.Reoriented, "review"));
            Assert.Empty(CadDecisions.Apply(u, D((1, "retype"), (2, "rotate_in_face"))));
            Assert.Equal("retype", u.Actions[0].Kind);
            Assert.True(u.Actions[0].Automatic);
            Assert.Equal("rotate_in_face", u.Actions[1].Kind);
            Assert.Equal("a person, through resolve", (string)u.Actions[1].Evidence["decided_by"]);
        }

        [Fact]
        public void Keep_leaves_the_element_and_replace_stays_held_as_a_migration()
        {
            CadUpdate u = Held((1, CadChange.ManuallyDiverged, "review"), (2, CadChange.Rehosted, "review"),
                               (3, CadChange.Removed, "orphan"));
            Assert.Empty(CadDecisions.Apply(u, D((1, "keep"), (2, "replace"), (3, "keep"))));
            Assert.Equal("leave", u.Actions[0].Kind);
            Assert.True(u.Actions[0].Automatic);
            Assert.Contains("KEPT", u.Actions[0].Says);
            Assert.Equal("replace", u.Actions[1].Kind);
            Assert.False(u.Actions[1].Automatic);
            Assert.Equal("leave", u.Actions[2].Kind);
        }

        [Fact]
        public void A_decision_the_change_does_not_admit_or_an_element_not_held_is_refused_by_name()
        {
            CadUpdate u = Held((1, CadChange.ManuallyDiverged, "review"), (2, CadChange.Unchanged, "leave"),
                               (3, CadChange.Moved, "set_curve"));
            List<string> errors = CadDecisions.Apply(u, D((1, "retype"), (2, "keep"), (3, "keep"), (4, "keep"),
                                                          (1, "keep"), (5, "erase")));
            Assert.Equal(6, errors.Count);
            Assert.Contains(errors, e => e.Contains("element 1 is manually_diverged") && e.Contains("'retype'"));
            Assert.Contains(errors, e => e.Contains("element 2 is not held"));
            Assert.Contains(errors, e => e.Contains("element 3 is not held"));
            Assert.Contains(errors, e => e.Contains("element 4 is not held"));
            Assert.Contains(errors, e => e.Contains("names element 1 twice"));
            Assert.Contains(errors, e => e.Contains("'erase' is not a decision"));
            Assert.Equal("review", u.Actions[0].Kind);
            Assert.Null(u.Actions[0].Evidence["decision"]);
        }

        [Fact]
        public void Delete_is_a_decision_on_an_orphan_and_on_nothing_else()
        {
            CadUpdate u = Held((1, CadChange.Removed, "orphan"), (2, CadChange.ManuallyDiverged, "review"),
                               (3, CadChange.Conflict, "orphan"), (4, CadChange.Conflict, "review"));
            List<string> errors = CadDecisions.Apply(u, D((1, "delete"), (2, "delete"), (3, "delete"), (4, "delete")));
            Assert.Equal(2, errors.Count);
            Assert.Contains(errors, e => e.Contains("element 2 is not an orphan"));
            Assert.Contains(errors, e => e.Contains("element 4 is not an orphan"));
            Assert.Equal("delete", u.Actions[0].Kind);
            Assert.True(u.Actions[0].Automatic);
            Assert.Contains("DELETE", u.Actions[0].Says);
            Assert.Equal("delete", u.Actions[2].Kind);
            Assert.Equal("review", u.Actions[1].Kind);
        }

        [Fact]
        public void Replace_is_offered_only_where_placing_again_is_the_answer()
        {
            Assert.DoesNotContain(CadChange.Resized, CadDecisions.AllowedFor[CadDecisions.Replace]);
            Assert.Contains(CadChange.Rehosted, CadDecisions.AllowedFor[CadDecisions.Replace]);
            Assert.Equal(new[] { CadChange.Reoriented }, CadDecisions.AllowedFor[CadDecisions.RotateInFace]);
            foreach (string[] classes in CadDecisions.AllowedFor.Values)
                Assert.All(classes, c => Assert.Contains(c, CadChange.All));
        }

        [Fact]
        public void The_plan_reads_the_decisions_emits_typed_actions_and_the_apply_restamps_what_was_kept()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            Assert.NotNull(dir);
            Func<string, string> read = rel => File.ReadAllText(Path.Combine(dir.FullName, rel));
            string plan = read(@"src\Horizun.Revit\Commands\PlanCadUpdateCommand.cs");
            string apply = read(@"src\Horizun.Revit\Commands\ApplyCadUpdateCommand.cs");
            string contract = read(@"src\Horizun.Contracts\Contract.cs");
            Assert.Contains("string decisionError = Decisions(request, out decisions);", plan);
            Assert.Contains("List<string> decisionErrors = CadDecisions.Apply(update, decisions);", plan);
            Assert.Contains("[\"key\"] = \"cad-update-resolve-\" + (d++)", plan);
            Assert.Contains("[\"operation\"] = \"change_type\"", plan);
            Assert.Contains("[\"operation\"] = \"rotate\"", plan);
            Assert.Contains("JArray migrations = MigrationPlans(doc, update);", plan);
            Assert.Contains("reason = CadPlacementRules.RestampAccepted;", plan);
            Assert.Contains("if (reason == CadPlacementRules.RestampAccepted)", apply);
            Assert.Contains("p.BuiltGeometry = CadUpdateRules.Encode(PlanGeometry(e));", apply);
            Assert.Contains("\"\"enum\"\": [\"\"retype\"\", \"\"rotate_in_face\"\", \"\"keep\"\", \"\"replace\"\", \"\"delete\"\"]", contract);
            Assert.Contains("[\"tool\"] = \"horizun_delete_verified\"", plan);
            Assert.Contains("!string.Equals(operation, \"change_type\", StringComparison.Ordinal)) continue;", apply);
            Assert.Contains("string wasVersion = wasV1 ? \"v1\" : existing.SchemaVersion >= 3 ? \"v3\" : \"v2\";", apply);
        }
    }
}
