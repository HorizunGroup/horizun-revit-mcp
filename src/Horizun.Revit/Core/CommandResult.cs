// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The result of a command. Two shapes only: success carrying a data payload, or
// failure carrying a message. The transport serializes this to
// { "id", "success", "data", "error" } — one envelope, no ambiguity about
// whether a call worked.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    public sealed class CommandResult
    {
        public bool Success { get; private set; }

        /// <summary>Payload on success (any JSON-serializable object). Null on failure.</summary>
        public object Data { get; private set; }

        /// <summary>Message on failure. Null on success.</summary>
        public string Error { get; private set; }

        /// <summary>
        /// MACHINE-READABLE PERMISSION TO FALL BACK TO PYTHON. Null on every result
        /// that does not grant it, which is almost all of them.
        ///
        /// The fallback policy used to live only in the server's instructions - prose
        /// that helps an obedient model and gives a client nothing to branch on. A
        /// client had to tell four different failures apart by reading English: a
        /// capability this bridge does not have, a mistake in the arguments, a Revit
        /// error, and a write that failed halfway. The first is the only one where
        /// generating Python is right, and the last is the one where it is dangerous.
        ///
        /// So the distinction is carried as data. See FallbackSignal for the rules;
        /// the one that matters is that write_started=true can never accompany
        /// allowed=true.
        /// </summary>
        public FallbackSignal Fallback { get; private set; }

        /// <summary>
        /// WHICH actions in a batch were capability gaps, by index. Present whenever any
        /// were, INCLUDING when the request-level grant was refused because other actions
        /// failed for fixable reasons - that is the case where a caller most needs to know
        /// the difference, and the old code told them nothing but "allowed".
        /// </summary>
        public Newtonsoft.Json.Linq.JArray CapabilityGaps { get; private set; }

        /// <summary>
        /// STRUCTURED DIAGNOSTIC that rides beside a failure's message. Null on almost every
        /// result. It exists because some failures - a failed atomic plan above all - carry
        /// facts a caller must branch on (did the TransactionGroup start? did the rollback
        /// actually land, or is the model's state uncertain?) that a prose sentence cannot be
        /// trusted to convey. Serialized into structuredContent on the error path, the same
        /// way the fallback signal is, so a client reads data instead of parsing English.
        /// </summary>
        public Newtonsoft.Json.Linq.JObject Detail { get; private set; }

        /// <summary>
        /// What Revit raised while this ran — warnings, errors, modal dialogs — filled in
        /// by the dispatcher, never by the command. It rides beside the result rather than
        /// inside it so no command has to remember to report it, and so a command that
        /// FAILED still carries what Revit objected to, which is usually the reason.
        /// </summary>
        public object RevitSaid { get; set; }

        private CommandResult() { }

        public static CommandResult Ok(object data)
            => new CommandResult { Success = true, Data = data };

        /// <summary>
        /// Lets the dispatcher normalize an anonymous JSON-serializable payload to a
        /// JObject before attaching transport metadata. Commands still create results
        /// through Ok/Fail and cannot change success into failure after the fact.
        /// </summary>
        internal void ReplaceData(object data) => Data = data;

        /// <summary>
        /// Attach an already-decided verdict to a result that is otherwise unchanged.
        /// Internal, and reachable only through FallbackDecision.Attach, because the
        /// rule about WHEN a verdict may be granted lives there and nowhere else.
        /// </summary>
        internal void CarryFallback(FallbackSignal signal, Newtonsoft.Json.Linq.JArray capabilityGaps)
        {
            Fallback = signal;
            CapabilityGaps = capabilityGaps;
        }

        public static CommandResult Fail(string error)
            => new CommandResult { Success = false, Error = error };

        /// <summary>
        /// REBUILD A RECORDED ANSWER, field for field. The durable idempotency ledger is
        /// the only caller, and it needs this because the ordinary factories cannot
        /// express every shape a real result takes: a SUCCESS carrying a dry-run fallback
        /// grant has no factory, and reassembling one through Ok(...) plus CarryFallback
        /// worked only for the combinations somebody remembered to reassemble.
        ///
        /// That is exactly how three fields went missing from a replay. The ledger's job
        /// is to hand a retry the answer the first caller would have received; anything
        /// this constructor cannot carry is something the retry silently does not learn.
        ///
        /// It does not VALIDATE - the ledger decides whether a recorded combination is
        /// believable before it gets here, and an unbelievable one is in-doubt, not
        /// something to repair on the way past.
        /// </summary>
        internal static CommandResult Restore(bool success, object data, string error, object revitSaid,
                                              FallbackSignal fallback,
                                              Newtonsoft.Json.Linq.JArray capabilityGaps,
                                              Newtonsoft.Json.Linq.JObject detail)
            => new CommandResult
            {
                Success = success,
                Data = data,
                Error = error,
                RevitSaid = revitSaid,
                Fallback = fallback,
                CapabilityGaps = capabilityGaps,
                Detail = detail
            };

        /// <summary>
        /// A failure carrying a structured diagnostic beside its message. Built for the
        /// atomic-plan rollback path, where "what happened to the group" must be a value a
        /// client can read, not a sentence it has to trust. The message is expected to be
        /// derived from the same diagnostic (see PlanFailure.Message) so prose and data agree.
        /// </summary>
        public static CommandResult FailWithDetail(string error, Newtonsoft.Json.Linq.JObject detail)
            => new CommandResult { Success = false, Error = error, Detail = detail };

        /// <summary>
        /// A refusal carrying an already-decided signal - granted or refused - plus the
        /// per-action gaps behind it. Built by FallbackDecision, which is the only place
        /// the rule lives; a command that assembled this itself would be the fourth copy
        /// of a rule that has already been got wrong once.
        /// </summary>
        public static CommandResult FailWithFallback(string error, FallbackSignal signal,
                                                     Newtonsoft.Json.Linq.JArray capabilityGaps)
            => new CommandResult
            {
                Success = false,
                Error = error,
                Fallback = signal,
                CapabilityGaps = capabilityGaps
            };
    }
}
