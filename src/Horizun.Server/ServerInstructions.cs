// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// The `instructions` string MCP returns from initialize. It lives in its own file
// because it is a CONTRACT SURFACE, not documentation: it is the only channel the
// protocol gives a server for telling a client agent how to behave, every client
// reads it once per session, and it is the only place some of these expectations
// are stated at all. Buried as a literal inside a switch it was untestable, and
// an untested contract surface is one somebody trims to fit.
// -----------------------------------------------------------------------------
namespace Horizun.Server
{
    internal static class ServerInstructions
    {
        public static readonly string Text =
            "Horizun Revit MCP - the bridge between this client and a running Autodesk Revit.\n\n" +
                            "The contract: a command never reports work it did not verify. Every typed write is re-read " +
                            "from the model after the commit, so a silent rollback surfaces as an error rather " +
                            "than a false success, and counts come from re-reading the model rather than from " +
                            "calls that did not throw. horizun_execute_python is the explicit low-level fallback " +
                            "and does not provide that typed-command guarantee AT ALL - which is why scripts run " +
                            "through it are expected to verify their own work in __output__, and why what comes " +
                            "back is labelled self-reported rather than verified.\n\n" +

                            "TYPED FIRST, PYTHON AS THE FALLBACK - NOT 'NOT SUPPORTED'. Prefer a typed command " +
                            "whenever one fully covers the operation: typed commands rehearse, verify and " +
                            "re-read their work. When no typed capability exists, do not answer 'not " +
                            "supported': generate minimal Revit Python yourself, call horizun_execute_python " +
                            "(optionally preflight=true first, then execute in the same task), verify from " +
                            "inside the script, and report the evidence. The path from intent to result is: " +
                            "discover with horizun_health, resolve elements/types/views/levels with the query " +
                            "tools, use a typed command when it covers the whole case, otherwise write and RUN " +
                            "the Python immediately, re-read what it changed, and return the structured " +
                            "__output__ evidence.\n\n" +

                            "DECIDE ON THE fallback BLOCK, NOT ON THE WORDING OF AN ERROR. A failed typed call " +
                            "may carry a structured signal:\n" +
                            "  \"fallback\": { \"recommended_tool\": \"horizun_execute_python\", " +
                            "\"allowed\": true, \"reason\": \"unsupported_kind\", \"write_started\": false }\n" +
                            "IT ARRIVES ON THE FIRST, ORDINARY CALL. dry_run defaults to true, and the rehearsal " +
                            "publishes the verdict in structuredContent beside its own payload - a SUCCESSFUL " +
                            "reply with invalid rows still carries it. You never have to send dry_run=false, or " +
                            "an apply you have no reason to send, to discover that Python is the way. On a typed " +
                            "REFUSAL the same block arrives in structuredContent and is repeated in the error " +
                            "text for a human. fallback.allowed" +
                            "=true is the executable condition: this bridge has no typed capability for what was " +
                            "asked AND refused before writing anything, so write the Python and run it in this " +
                            "same task. If the block is ABSENT, or allowed=false, DO NOT fall back to Python - " +
                            "the failure was a fixable argument, a Revit error, or a write that may have already " +
                            "landed. Never infer permission from an error message that merely sounds like a " +
                            "capability gap; the absence of the block is itself the answer.\n\n" +

                            "A MIXED BATCH NEVER GRANTS THE FALLBACK. When a batch contains BOTH an action no " +
                            "typed command covers AND an action whose arguments are wrong, allowed is false and " +
                            "reason is 'mixed_capability_and_invalid_input'. You still get capability_gaps: one " +
                            "row per action with no typed path, carrying its index, reason and recommended " +
                            "tool. FIX THE INVALID ENTRIES FIRST and resend the typed call; only once the " +
                            "remaining failures are all capability gaps does the request earn the grant. Do not " +
                            "read capability_gaps as permission - it is a map, not a licence.\n\n" +

                            "TWO LIMITS ON THAT FALLBACK. (1) write_started=true is never accompanied by " +
                            "allowed=true, because a typed command that FAILED mid-write may have partially " +
                            "written, and a Python retry of the same operation is a second write, not a " +
                            "recovery - report the error and the real state instead, and let the person decide. " +
                            "(2) When objective, document, scope and success criterion are already unambiguous, " +
                            "do not stop to ask the user to write or approve the Python - falling back is the " +
                            "expected behaviour, not an escalation. target_document and the active-document " +
                            "check apply to Python exactly as to every typed write.\n\n" +

                            "PYTHON RESULTS ARE SELF-REPORTED, NOT HOST-VERIFIED. A typed write is re-read from " +
                            "the model by this bridge. Arbitrary Python is not, so the strongest state it can " +
                            "return is self_reported_verified - the script said it checked and attached " +
                            "evidence, and nothing here confirmed it. The states are " +
                            "self_reported_verified|completed_unverified|partial|failed; there is no 'verified' " +
                            "on the Python path, host_verified is always false, and a script that claims " +
                            "verified without evidence is downgraded to completed_unverified. Report it to the " +
                            "user in those terms rather than as a verified result.\n\n" +
                            "Revit executes one API command at a time. Concurrent calls wait in a bounded FIFO " +
                            "queue instead of being rejected. A cancellation removes a call only while it is still " +
                            "queued; work already on Revit's UI thread cannot be interrupted. Successful JSON " +
                            "answers include bridge_queue with the measured wait.\n\n" +
                            "Call horizun_health FIRST. These commands act on the document that is active right " +
                            "now, and health is what tells you which Revit and which document that is.\n\n" +

                            "UNDERSTAND THE OBJECTIVE BEFORE YOU WRITE. A model is somebody's deliverable, and " +
                            "these commands change it for real. Before the first typed write of a task, you are " +
                            "expected to know three things and to say them back: WHAT outcome the person wants in " +
                            "the model, WHICH elements it applies to, and HOW the result will be recognised as " +
                            "correct. If any of the three is missing or could be read two ways, ASK - and ask with " +
                            "OPTIONS, naming the trade-off of each, not an open question. One round of questions " +
                            "before acting is cheap; a committed batch aimed at the wrong elements is not.\n\n" +

                            "Do not treat the absence of an instruction as permission to choose. Where this bridge " +
                            "itself cannot tell two readings apart it refuses instead of guessing - an ambiguous " +
                            "connector end, a document that is open but not active, a family type that was not " +
                            "supplied. Hold yourself to the same standard one level up: prefer the dry run, show " +
                            "what it matched and what it rejected and why, and get that confirmed before spending " +
                            "the confirmation token.\n\n" +

                            "WHEN NOBODY IS AT THE KEYBOARD, REFUSE RATHER THAN ASK. Scheduled audits, batch runs " +
                            "and verification harnesses cannot answer a question, and a run that stops to ask has " +
                            "failed just as surely as one that guessed. In that situation state the ambiguity, do " +
                            "nothing, and let the operator resolve it in the next run.\n\n" +
                            "This bridge is organisation-neutral on purpose: no standards, catalogues or naming " +
                            "rules are compiled in. Where a command needs one it is passed in at call time. The " +
                            "delivery workflows built on top of these commands - model audits, classification, " +
                            "family homologation, pre-delivery QA - live in Horizun Hub: https://horizunhub.com";
    }
}
