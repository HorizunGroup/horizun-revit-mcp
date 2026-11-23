// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Measured 2026-11-18 (Revit 2025): plan_from_cad with an outfall on a drawing already fully built from itself
// answered fall_not_applied with the code esquema_invalido. Nothing was malformed: nothing was left to plan.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadPlanNothingLeftTests
    {
        

        

        

        

        [Fact]
        public void The_fall_not_applied_refusal_is_a_precondition_not_a_schema_error()
        {
            // Left to its words ("... then omit the outfall ...") the message is read as esquema_invalido; the detail's own code wins.
            string message = "fall_not_applied: an outfall was named and no planned run took a height from it. If nothing in this drawing falls " +
                             "then omit the outfall and every run is planned at the elevation its rule declares. NOTHING was planned.";
            Assert.Equal(ErrorCodes.EsquemaInvalido, ErrorCodes.Classify(message, null).Code);
            var detail = new JObject { [ErrorCodes.CodeField] = ErrorCodes.PrecondicionNoCumplida, ["refused"] = "fall_not_applied" };
            Assert.Equal(ErrorCodes.PrecondicionNoCumplida, ErrorCodes.Classify(message, detail).Code);
        }

        

        
    }
}
