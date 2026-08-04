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
                            "calls that did not throw. horizun_execute_python is the explicit low-level escape " +
                            "hatch and does not provide that typed-command guarantee.\n\n" +
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
