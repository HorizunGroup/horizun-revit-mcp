using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Revit-free formatting shared by the command and its regression tests.
    public static class TransformFailure
    {
        public static JObject ClassifyUnspecified(string message, JObject detail)
        {
            var result = detail == null ? new JObject() : (JObject)detail.DeepClone();
            string code = ErrorCodes.Classify(message, result).Code;
            result["error_code"] = code == ErrorCodes.NoClasificado ? ErrorCodes.ErrorInterno : code;
            return result;
        }

        public static JObject Planning(int index, string message)
        {
            string code = ErrorCodes.Classify(message, null).Code;
            if (code == ErrorCodes.NoClasificado) code = ErrorCodes.PrecondicionNoCumplida;
            return new JObject { ["index"] = index, ["error"] = message,
                ["error_code"] = code };
        }

        public static JObject SketchFailure(string ids, string reason)
        {
            string cause = string.IsNullOrWhiteSpace(reason) ? "the cause was not reported" : reason;
            return new JObject { ["error_code"] = ErrorCodes.PrecondicionNoCumplida,
                ["error"] = "Element(s) " + ids + ": " + cause + ". Inspect the element and its sketch constraints before retrying." };
        }

        public static string AtomicMessage(string ids, string reason, string outcome)
        {
            return "Atomic transform of element(s) " + ids + " failed: " +
                (string.IsNullOrWhiteSpace(reason) ? "the cause was not reported" : reason) +
                ". Inspect the elements and constraints before retrying. " + outcome;
        }

        public static JObject AtomicDetail(Exception ex, bool rolledBack)
        {
            return ErrorCodes.Detail(rolledBack ? ErrorCodes.TransaccionRevertida :
                ErrorCodes.IsRevitApiType(ex?.GetType().FullName) ? ErrorCodes.ApiRevitExcepcion : ErrorCodes.ErrorInterno,
                ErrorCodes.TypeNameOf(ex), extra: new JObject { ["rollback_confirmed"] = rolledBack });
        }

        public static JObject RevertedDetail(string status, bool confirmed)
        {
            return new JObject { ["error_code"] = confirmed ? ErrorCodes.TransaccionRevertida : ErrorCodes.ErrorInterno,
                ["rollback_confirmed"] = confirmed, ["transaction_status"] = status };
        }

        public static string BudgetMessage(string ids)
        {
            return "The estimated rehearsal for element(s) " + ids +
                " exceeds its 15 second budget; split the operations into smaller batches. Nothing was started.";
        }
    }
}
