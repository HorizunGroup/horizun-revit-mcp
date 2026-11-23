using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TransformFailureTests
    {
        [Fact]
        public void UnclassifiedFailureHasExplicitInternalCodeAndPreservesEvidence()
        {
            var detail = TransformFailure.ClassifyUnspecified("the transaction group would not start. Nothing was changed.",
                new Newtonsoft.Json.Linq.JObject { ["rollback_confirmed"] = false });
            Assert.NotEqual(ErrorCodes.NoClasificado, (string)detail["error_code"]);
            Assert.False((bool)detail["rollback_confirmed"]);
            Assert.Equal(ErrorCodes.VerificacionFallida, (string)TransformFailure.ClassifyUnspecified("failure",
                new Newtonsoft.Json.Linq.JObject { ["error_code"] = ErrorCodes.VerificacionFallida })["error_code"]);
        }

        [Theory]
        [InlineData("ElementId 42 is pinned and cannot be transformed; unpin it deliberately first.")]
        [InlineData("ElementId 42 has no readable location or bounding box, so move cannot be verified; inspect the element and its owner view before choosing another operation.")]
        [InlineData("Filled region 42 has no readable owner view; its contours cannot be verified. Check the owner view and retry.")]
        public void PlanningFailuresCarryCodeAndAction(string message)
        {
            var row = TransformFailure.Planning(0, message);
            Assert.True(ErrorCodes.IsStable((string)row["error_code"]));
            Assert.NotEqual(ErrorCodes.NoClasificado, (string)row["error_code"]);
            Assert.Contains("42", (string)row["error"]);
        }

        [Theory]
        [InlineData("Revit reports that this sketch cannot be edited (a group, a part, or an element borrowed by somebody else answers this way).")]
        [InlineData("the sketch's plane or profile could not be read")]
        [InlineData("")]
        public void SketchRehearsalFailuresIdentifyElementAndNextStep(string reason)
        {
            var diagnostic = TransformFailure.SketchFailure("42", reason);
            Assert.Equal(ErrorCodes.PrecondicionNoCumplida, (string)diagnostic["error_code"]);
            Assert.Contains("42", (string)diagnostic["error"]);
            Assert.Contains("Inspect", (string)diagnostic["error"]);
            if (reason == "") Assert.Contains("cause was not reported", (string)diagnostic["error"]);
        }

        [Fact]
        public void GenericExceptionNamesTargetsAndDoesNotInventRollback()
        {
            string message = TransformFailure.AtomicMessage("42, 43", "", "Rollback was not confirmed.");
            Assert.Contains("42, 43", message);
            Assert.Contains("cause was not reported", message);
            Assert.Equal(ErrorCodes.ErrorInterno, (string)TransformFailure.AtomicDetail(new Exception(), false)["error_code"]);
            Assert.Equal(ErrorCodes.TransaccionRevertida, (string)TransformFailure.AtomicDetail(new Exception(), true)["error_code"]);
        }

        [Theory]
        [InlineData("RolledBack", true, ErrorCodes.TransaccionRevertida)]
        [InlineData("Pending", false, ErrorCodes.ErrorInterno)]
        public void SketchApplyFailureReportsActualRollback(string status, bool confirmed, string code)
        {
            var detail = TransformFailure.RevertedDetail(status, confirmed);
            Assert.Equal(code, (string)detail["error_code"]);
            Assert.Equal(confirmed, (bool)detail["rollback_confirmed"]);
            Assert.Equal(status, (string)detail["transaction_status"]);
        }

        [Fact]
        public void LongRehearsalNamesTargetsAndExplainsHowToProceed()
        {
            string message = TransformFailure.BudgetMessage("42, 43");
            Assert.Contains("42, 43", message);
            Assert.Contains("15 second budget", message);
            Assert.Contains("split", message);
            Assert.Contains("Nothing was started", message);
        }
    }
}
