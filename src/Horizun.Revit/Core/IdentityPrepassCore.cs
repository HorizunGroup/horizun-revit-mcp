// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free (so it is unit-tested with a fake command).
//
// The logic of the identity pre-pass of the dispatcher, with everything that needs Revit passed in:
//   * Execute: what IdentityPrepass.Execute does around a command (is there anything to inspect, the early check, the normalized
//     request handed to the command, the identity_check stamp on the reply, the reset of the call's binding on every exit).
//   * Early: what DocumentGate.CheckIdentityEarly does (shape check without a model, then the document, then the verification).
// IdentityPrepass and DocumentGate supply the Revit side (the document and the lookups).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What the early check needs from the model: a refusal (no document, wrong document) or the two lookups.</summary>
    public sealed class IdentityLookups
    {
        public CommandResult Refusal;
        public Func<long, ElementIdentityFacts> ById;
        public Func<string, ElementIdentityFacts> ByUniqueId;
        /// <summary>
        /// True when the active document is workshared (Document.IsWorkshared: also a local file of a central). Only there does a synchronize renumber
        /// ElementIds and do other people's elements live. A reader that is missing or throws counts as collaborative (fail closed).
        /// </summary>
        public Func<bool> IsCollaborative;
    }

    public static class IdentityPrepassCore
    {
        /// <summary>Reads whether the document is collaborative; a reader that is missing or throws means "collaborative" (fails closed).</summary>
        public static bool CollaborativeOrUnknown(Func<bool> read) => ElementIdentityGuard.CollaborativeOrUnknown(read);

        private static readonly string[] Needles = { "unique_id", "expected_category", "expected_type", "identity", "uniqueid", "expectedcategory", "expectedtype" };

        private static bool MayCarryIdentity(string json)
        {
            foreach (string n in Needles)
                if (json.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 || json.IndexOf(n.Replace("_", "-"), StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Runs <paramref name="run"/> after the call's identity (if any) was verified and consumed. Resets the call's identity binding
        /// first and last, so nothing a call established leaks into the next one, even when the command throws.
        /// </summary>
        public static CommandResult Execute(string tool, string json, Func<JObject, CommandResult> early, Func<string, CommandResult> run)
        {
            IdentityBinding.Reset();
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return run(json);
                if (!ElementIdentityGuard.Covers(tool))
                {
                    // Backstop of the server's rule: an identity field a tool does not cover and does not declare is refused, not ignored.
                    if (!MayCarryIdentity(json)) return run(json);
                    JObject plain;
                    try { plain = JsonText.ParseObject(json); }
                    catch { return run(json); }
                    JObject undeclared = ElementIdentityGuard.RejectUndeclaredTopLevel(plain, tool, ElementIdentityGuard.SchemaNames(Contract.Find(tool)?.InputSchema));
                    return undeclared != null ? Refusal(undeclared) : run(json);
                }
                JObject request;
                try { request = JsonText.ParseObject(json); }
                catch { return run(json); }   // the command reports its own parse error

                // A near miss ("uniqueId") has to be caught even when no canonical identity field is present.
                bool inspect = ElementIdentityGuard.NearMiss(request, tool) != null || ElementIdentityGuard.NeedsCheck(request, tool);
                if (!inspect) return run(json);

                CommandResult refusal = early(request);
                if (refusal != null) return refusal;
                return Stamp(run(request.ToString(Newtonsoft.Json.Formatting.None)));
            }
            finally { IdentityBinding.Reset(); }
        }

        /// <summary>The early check: shape (no model needed), then the document, then the identity against the model. Null when all is well.</summary>
        public static CommandResult Early(JObject request, string tool, Func<IdentityLookups> resolve)
        {
            JObject shape = ElementIdentityGuard.ShapeCheck(request, tool);
            if (shape != null) return Refusal(shape);
            IdentityLookups lookups = resolve();
            if (lookups.Refusal != null) return lookups.Refusal;
            return Verify(request, tool, lookups);
        }

        /// <summary>The verification alone (the write path's gate, after it resolved the document).</summary>
        public static CommandResult Verify(JObject request, string tool, IdentityLookups lookups)
        {
            JObject mismatch = ElementIdentityGuard.Covers(tool)
                ? ElementIdentityGuard.ResolveRequest(request, tool, lookups.ById, lookups.ByUniqueId, lookups.IsCollaborative)
                : ElementIdentityGuard.RejectUnsupported(request, tool);
            return mismatch == null ? null : Refusal(mismatch);
        }

        public static CommandResult Refusal(JObject mismatch) =>
            (string)mismatch["error_code"] == ElementIdentityGuard.ArgumentErrorCode
                ? CommandResult.FailCoded(ErrorCodes.EntradaInvalida,
                    "The identity fields are not usable here (" + (string)mismatch["detail"] + "). Nothing was written.", extra: mismatch)
                : CommandResult.FailCoded(ErrorCodes.PrecondicionNoCumplida,
                    "The expected element identity does not match the element found (elemento_no_coincide). Nothing was written.", extra: mismatch);

        /// <summary>Adds identity_check (and, at the top level, identity_strength: unique_id | categoria) to the reply of a call that carried an identity.</summary>
        public static CommandResult Stamp(CommandResult result)
        {
            try
            {
                JObject notice = IdentityBinding.Notice();
                if (result == null || notice == null) return result;
                if (result.Success)
                {
                    JObject data = result.Data as JObject;
                    if (data == null && result.Data != null) data = JObject.FromObject(result.Data);
                    if (data == null) return result;
                    data["identity_check"] = notice;
                    // Visible without opening identity_check: only a unique_id identifies an element.
                    data["identity_strength"] = notice["identity_strength"];
                    if (notice["note"] != null) data["identity_warning"] = notice["note"];
                    result.ReplaceData(data);
                }
                else if (result.Detail != null) result.Detail["identity_check"] = notice;
            }
            catch (Exception ex) { Log.Error("identity_check stamp failed", ex); }
            return result;
        }
    }
}
