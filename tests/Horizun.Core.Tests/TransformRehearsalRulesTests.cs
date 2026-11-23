using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TransformRehearsalRulesTests
    {
        private static List<TransformRehearsalRules.RowRef> Rows(params string[] ops)
            => ops.Select((o, i) => new TransformRehearsalRules.RowRef { Index = i, Operation = o }).ToList();

        [Theory]
        [InlineData(true, true, true, "clean")]
        [InlineData(false, true, true, "failed")]
        [InlineData(true, false, true, "uncertain")]
        [InlineData(false, false, true, "uncertain")]
        [InlineData(true, true, false, "uncertain")]
        [InlineData(false, true, false, "uncertain")]
        public void TheModelStateIsUncertainUnlessTheRollbackAndTheAbsenceOfCopiesAreProven(bool applied, bool rolledBack, bool absent, string expected)
        {
            Assert.Equal(expected, TransformRehearsalRules.Decide(applied, rolledBack, absent));
        }

        [Fact]
        public void ACleanRehearsalPassesEveryRow()
        {
            JArray rows = TransformRehearsalRules.RowsForSuccess(Rows("move", "pin"));
            Assert.Equal(2, rows.Count);
            Assert.All(rows.OfType<JObject>(), r => Assert.Equal("passed", (string)r["rehearsal"]));
        }

        [Fact]
        public void AnExceptionWhileApplyingRowOneFailsItAndLeavesTheLaterRowsNotReached()
        {
            JArray rows = TransformRehearsalRules.RowsForFailure(Rows("move", "rotate", "pin"), new[] { 1 }, 1,
                TransformRehearsalRules.PhaseApply, ErrorCodes.TransaccionRevertida, "boom");
            Assert.Equal("rolled_back_with_batch", (string)rows[0]["rehearsal"]);
            Assert.Equal("failed", (string)rows[1]["rehearsal"]);
            Assert.Equal(ErrorCodes.TransaccionRevertida, (string)rows[1]["error_code"]);
            Assert.Equal("boom", (string)rows[1]["reason"]);
            Assert.Equal("not_reached", (string)rows[2]["rehearsal"]);
            Assert.Null(rows[0]["error_code"]);
        }

        [Fact]
        public void APostCommitFailureNamesOnlyTheRowsThatDidNotHoldAndNeverMarksTheOthersPassed()
        {
            JArray rows = TransformRehearsalRules.RowsForFailure(Rows("move", "move", "move"), new[] { 0, 2 }, null,
                TransformRehearsalRules.PhasePostcommit, ErrorCodes.VerificacionFallida, "did not hold");
            Assert.Equal(new[] { "failed", "rolled_back_with_batch", "failed" }, rows.Select(r => (string)r["rehearsal"]).ToArray());
            Assert.DoesNotContain(rows.OfType<JObject>(), r => (string)r["rehearsal"] == "passed");
        }

        [Fact]
        public void AFailureNobodyAttributedBlamesNoRowButRollsAllBackWithTheBatch()
        {
            JArray rows = TransformRehearsalRules.RowsForFailure(Rows("move", "rotate"), new int[0], null,
                TransformRehearsalRules.PhaseCommit, ErrorCodes.TransaccionRevertida, "did not commit");
            Assert.All(rows.OfType<JObject>(), r => Assert.Equal("rolled_back_with_batch", (string)r["rehearsal"]));
        }

        [Fact]
        public void WithoutAnyNamedRowAnApplyFailureDoesNotInventNotReached()
        {
            JArray rows = TransformRehearsalRules.RowsForFailure(Rows("move", "rotate"), null, 0,
                TransformRehearsalRules.PhaseApply, ErrorCodes.ErrorInterno, "x");
            Assert.All(rows.OfType<JObject>(), r => Assert.Equal("rolled_back_with_batch", (string)r["rehearsal"]));
        }

        [Fact]
        public void FailedRowsReadsTheVerificationRowsByIndexAndTreatsAMissingVerdictAsFailed()
        {
            var verification = new JArray(
                new JObject { ["index"] = 0, ["verified"] = true },
                new JObject { ["index"] = 1, ["verified"] = false },
                new JObject { ["index"] = 2 });
            Assert.Equal(new[] { 1, 2 }, TransformRehearsalRules.FailedRows(verification).ToArray());
            Assert.Empty(TransformRehearsalRules.FailedRows(null));
        }

        [Fact]
        public void TheRefusalKeepsTheErrorCodeOfTheApplyAndSaysNothingWasWritten()
        {
            var applyDetail = new JObject { ["error_code"] = ErrorCodes.VerificacionFallida, ["transaction_status"] = "Committed" };
            var invalid = new JArray(new JObject { ["index"] = 3, ["error"] = "x" });
            JObject detail = TransformRehearsalRules.RefusalDetail(applyDetail, TransformRehearsalRules.PhasePostcommit,
                new JArray(), "RolledBack", invalid);
            Assert.Equal(ErrorCodes.VerificacionFallida, (string)detail["error_code"]);
            Assert.True((bool)detail["dry_run"]);
            Assert.Equal("revit_rehearsal", (string)detail["refused_by"]);
            Assert.True((bool)detail["apply_would_refuse"]);
            Assert.False((bool)detail["write_started"]);
            Assert.True((bool)detail["rollback_confirmed"]);
            Assert.Single((JArray)detail["invalid_rows"]);
            Assert.Equal("Committed", (string)detail["transaction_status"]);
            // The detail of the apply itself is not mutated.
            Assert.Null(applyDetail["dry_run"]);
        }

        [Fact]
        public void TheRowsOfARehearsalNeverCarryTheIdsOfCopiesItRolledBack()
        {
            var rows = new JArray(new JObject
            {
                ["index"] = 0, ["operation"] = "copy", ["verified"] = false, ["targets"] = 2, ["copies_expected"] = 2,
                ["created_ids"] = new JArray(900, 901), ["source_to_copy"] = new JArray(new JObject { ["copy"] = 900 }),
                ["unexplained_created_ids"] = new JArray(902), ["array_id"] = 903
            });
            JArray clean = TransformRehearsalRules.SanitizeRows(rows);
            var row = (JObject)clean[0];
            Assert.Equal(new[] { "index", "operation", "verified", "targets", "copies_expected" },
                row.Properties().Select(p => p.Name).ToArray());
            Assert.DoesNotContain("900", clean.ToString());
            Assert.DoesNotContain("903", clean.ToString());
            Assert.Empty(TransformRehearsalRules.SanitizeRows(null));
        }

        [Fact]
        public void ARefusalSanitizesTheApplyRowsAndWarnsAboutProvisionalIdsOnlyWhenCopiesWereMade()
        {
            var applyDetail = new JObject { ["error_code"] = ErrorCodes.VerificacionFallida,
                ["rows"] = new JArray(new JObject { ["index"] = 0, ["verified"] = false, ["created_ids"] = new JArray(55) }) };
            JObject withCopies = TransformRehearsalRules.RefusalDetail(applyDetail, TransformRehearsalRules.PhasePostcommit, null, "RolledBack", null, true);
            Assert.Null(withCopies["rows"][0]["created_ids"]);
            Assert.NotNull(withCopies["provisional_ids_note"]);
            Assert.NotNull(applyDetail["rows"][0]["created_ids"]);
            JObject without = TransformRehearsalRules.RefusalDetail(applyDetail, TransformRehearsalRules.PhasePostcommit, null, "RolledBack", null);
            Assert.Null(without["provisional_ids_note"]);
        }

        [Fact]
        public void TheRefusalOfAnApplyWithNoDetailStillCarriesTheRehearsalFacts()
        {
            JObject detail = TransformRehearsalRules.RefusalDetail(null, TransformRehearsalRules.PhaseApply, null, "RolledBack", null);
            Assert.False((bool)detail["write_started"]);
            Assert.Null(detail["invalid_rows"]);
            Assert.Empty((JArray)detail["rehearsal_rows"]);
        }

        [Fact]
        public void AnUncertainRehearsalSaysWriteStartedAndNamesTheInternalCode()
        {
            JObject detail = TransformRehearsalRules.UncertainDetail("Started", "boom", false);
            Assert.True((bool)detail["write_started"]);
            Assert.False((bool)detail["rollback_confirmed"]);
            Assert.Equal("uncertain", (string)detail["state"]);
            Assert.Equal(ErrorCodes.ErrorInterno, (string)detail["error_code"]);
            Assert.False((bool)detail["created_elements_absent"]);
        }

        [Fact]
        public void TheCleanBlockCapsTheWarningsAndReportsTheLevelOfValidation()
        {
            var warnings = Enumerable.Range(0, 25).Select(i => "w" + i);
            JObject block = TransformRehearsalRules.CleanBlock(Rows("move"), "RolledBack", warnings);
            Assert.Equal("revit_transform_rolled_back", (string)block["validation_level"]);
            Assert.Equal(TransformRehearsalRules.MaxWarnings, ((JArray)block["revit_warnings"]).Count);
            Assert.True((bool)block["rollback_confirmed"]);
            Assert.Equal("passed", (string)block["rows"][0]["rehearsal"]);
            Assert.Empty((JArray)TransformRehearsalRules.CleanBlock(Rows("move"), "RolledBack", null)["revit_warnings"]);
        }

        [Fact]
        public void TheNoteSaysWhetherRevitRanAndThatInvalidEntriesBlockTheToken()
        {
            Assert.Contains("no transaction was opened", TransformRehearsalRules.Note(false, 2));
            Assert.Contains("rolled back", TransformRehearsalRules.Note(true, 0));
            Assert.DoesNotContain("block the confirmation token", TransformRehearsalRules.Note(true, 0));
            Assert.Contains("1 invalid entry block the confirmation token", TransformRehearsalRules.Note(true, 1));
            Assert.Contains("2 invalid entries block the confirmation token", TransformRehearsalRules.Note(true, 2));
        }
    }
}
