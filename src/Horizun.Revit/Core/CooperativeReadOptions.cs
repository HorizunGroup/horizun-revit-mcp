// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// COOPERATIVE READING FOR THE READERS THAT ACTUALLY HOLD THE UI THREAD.
//
// CooperativeRead existed and exactly one new command used it. The readers that
// freeze somebody's Revit for minutes — model_scan, query_model, list_elements —
// did not, so the gap the inventory describes was still open while the mechanism
// sat beside it.
//
// THE CONSTRAINT THAT SHAPES THIS FILE: those three are PUBLISHED. Callers depend
// on their exact behaviour, and quietly making them stop early would turn every
// existing integration's complete answer into a partial one that still says 200.
// So the new semantics are OPT-IN, in one place, with one shape:
//
//     "cooperative": { "ui_budget_ms": 20000, "max_units": 50000, "cursor": "..." }
//
// ABSENT MEANS TODAY'S BEHAVIOUR, byte for byte: no scope, no early stop, no extra
// field in the reply. Present means the caller has said, in as many words, that a
// partial answer is more useful to them than a frozen window.
//
// WHAT A DECLARED BUDGET DOES AND DOES NOT PROVE, because this is where the
// temptation is. `ui_budget_ms` bounds how long this command SPENDS. It does not
// demonstrate that Revit's interface stayed fluid, and nothing measured from
// inside the process can: the command holds the UI thread for its whole duration,
// the reply still has to travel back through the pipe, and Revit's own external
// event queue decides when the window repaints. A budget makes the freeze a
// number somebody chose instead of a number nobody knew. The only evidence of
// FLUIDITY is a sampler watching from outside - which is why the benchmark has a
// `timing.samples` oracle and why that oracle refuses to grade this field.
//
// THE CURSOR. A partial read that cannot be continued is a partial read somebody
// has to redo from the start, and the second attempt freezes Revit exactly as
// long as the first. The cursor is opaque, it carries a fingerprint of the
// request, and it is REFUSED against a different request: resuming one query with
// another query's position produces a page of the wrong elements with no sign
// that anything went wrong.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The opt-in reading options, parsed once and shaped the same way everywhere.</summary>
    public sealed class CooperativeOptions
    {
        /// <summary>False when the caller said nothing: the command then behaves exactly as it always has.</summary>
        public bool Requested;

        public int? UiBudgetMs;

        /// <summary>A hard bound on units examined, independent of the clock. Zero = unbounded.</summary>
        public int MaxUnits;

        /// <summary>
        /// The most BYTES this read may return, or 0 for the default.
        ///
        /// NOT THE UI BUDGET IN ANOTHER UNIT. The budget bounds how long the read holds
        /// Revit's thread; this bounds what comes back. A fast read of a large model fits
        /// inside twenty seconds and produces forty megabytes the client cannot process,
        /// and neither limit catches what the other one is for.
        /// </summary>
        public int MaxResponseBytes;

        /// <summary>The default ceiling. Generous enough for a real read, small enough to be parseable.</summary>
        public const int DefaultMaxResponseBytes = 8 * 1024 * 1024;

        /// <summary>The most this may be raised to, whatever a caller asks for.</summary>
        public const int HardMaxResponseBytes = 64 * 1024 * 1024;

        /// <summary>
        /// How often the accumulated size is measured, in units.
        ///
        /// Serialising the whole answer once per element would make the limit the dominant
        /// cost of the read it is protecting.
        /// </summary>
        public const int SizeCheckEvery = 250;

        /// <summary>Where to resume, or 0.</summary>
        public int StartAt;

        /// <summary>The cursor's fingerprint, checked against this request's.</summary>
        public string CursorFingerprint;

        /// <summary>The model epoch the cursor was issued at, or -1 when it carried none.</summary>
        public long CursorEpoch = -1;

        public string Refusal;

        public static CooperativeOptions None => new CooperativeOptions();

        /// <summary>
        /// Read the options from a request. A request with no `cooperative` object gets the
        /// old behaviour and nothing else changes - which is the whole point.
        /// </summary>
        public static CooperativeOptions Read(JObject request, string requestFingerprint)
        {
            var options = new CooperativeOptions();
            JObject node = request == null ? null : request["cooperative"] as JObject;
            if (node == null) return options;

            options.Requested = true;
            options.UiBudgetMs = node.Value<int?>("ui_budget_ms");
            options.MaxUnits = node.Value<int?>("max_units") ?? 0;
            if (options.MaxUnits < 0) options.MaxUnits = 0;

            string cursor = node.Value<string>("cursor");
            if (string.IsNullOrWhiteSpace(cursor)) return options;

            // A CEILING THE CALLER MAY LOWER AND MAY NOT REMOVE. Raising it past the hard
            // maximum is clamped rather than refused: a caller asking for more than the
            // client can parse has made an estimate, not a mistake.
            options.MaxResponseBytes = request?.Value<int?>("max_response_bytes") ?? 0;

            int startAt;
            string fingerprint;
            long epoch;
            options.Refusal = ReadCursor(cursor, out startAt, out fingerprint, out epoch);
            if (options.Refusal != null) return options;

            if (!string.Equals(fingerprint, requestFingerprint, StringComparison.Ordinal))
            {
                // A CURSOR FROM ANOTHER QUESTION IS THE DANGEROUS CASE. It resumes at a
                // position that means nothing here, produces a page of the wrong elements,
                // and looks exactly like a correct page.
                options.Refusal =
                    "this cursor was issued for a DIFFERENT request. Resuming one query at another " +
                    "query's position produces a page of the wrong elements with nothing to show " +
                    "that anything went wrong. Re-run without a cursor, or send back the cursor " +
                    "this exact request returned.";
                return options;
            }

            // AND THE MODEL. The fingerprint above answers "is this the same question?"; it
            // says nothing about whether the thing being asked about still exists. Resuming a
            // partial read after somebody deleted forty walls carries on from position 12,000
            // of a collection that is now shorter - skipping whatever moved up into the gap,
            // and reporting a clean continuation.
            long now = CurrentEpoch();
            if (epoch >= 0 && now >= 0 && epoch != now)
            {
                options.Refusal =
                    "this cursor was issued when the model was at state " + epoch + " and it is now at " +
                    now + ". Something changed - an edit, a save, a synchronise, a document opened or " +
                    "closed, or the active view - so the position this cursor holds no longer points " +
                    "where it did. Re-run without a cursor. The check is deliberately CONSERVATIVE: it " +
                    "fires for changes that could not have affected this read, because re-running costs " +
                    "time and resuming into a shifted collection costs a wrong answer nobody can see.";
                return options;
            }

            options.StartAt = startAt;
            options.CursorFingerprint = fingerprint;
            options.CursorEpoch = epoch;
            return options;
        }

        /// <summary>
        /// The model's current state counter, or -1 when nothing is watching.
        ///
        /// QueryCacheLifecycle bumps it on every document change, open, close, save,
        /// synchronise and view activation. -1 means the watcher was never attached - in a
        /// test, or before startup finished - and a cursor cannot then be checked against it.
        /// That is reported as "not checked" rather than treated as "unchanged".
        /// </summary>
        public static long CurrentEpoch()
        {
            try { return QueryCacheLifecycle.Ready ? QueryCacheLifecycle.Cache.Epoch : -1; }
            catch { return -1; }
        }

        /// <summary>Open a scope for this read, or null when the caller asked for the old behaviour.</summary>
        public CooperativeRead.Scope Begin(string what, int knownUnits)
        {
            if (!Requested) return null;
            return CooperativeRead.Begin(what, UiBudgetMs, knownUnits);
        }

        /// <summary>Has this read hit the caller's unit bound? Separate from the clock on purpose.</summary>
        public bool UnitsExhausted(int done) => MaxUnits > 0 && done >= MaxUnits;

        /// <summary>The byte ceiling in force for this read.</summary>
        public int ResponseCeiling =>
            MaxResponseBytes > 0 ? Math.Min(MaxResponseBytes, HardMaxResponseBytes) : DefaultMaxResponseBytes;

        /// <summary>
        /// Has what has been collected so far outgrown the ceiling?
        ///
        /// Measured every <see cref="SizeCheckEvery"/> units, so a caller passes the count
        /// and the accumulator. Returns the measured size through `bytes` whether or not it
        /// stopped, because a read that finished at 90% of the ceiling is worth knowing
        /// about before the next one does not.
        /// </summary>
        public bool ResponseTooLarge(int done, JToken collected, out int bytes)
        {
            bytes = -1;
            if (collected == null) return false;
            if (done > 0 && done % SizeCheckEvery != 0) return false;

            bytes = collected.ToString(Newtonsoft.Json.Formatting.None).Length;
            return bytes >= ResponseCeiling;
        }

        // =====================================================================
        // Cursors
        // =====================================================================

        /// <summary>
        /// An opaque continuation token. Base64 of "position|fingerprint", which is not
        /// security - it is a shape a caller cannot read a meaning into and then construct
        /// by hand. Its integrity is the fingerprint check, not the encoding.
        /// </summary>
        public static string MakeCursor(int position, string fingerprint)
        {
            string raw = position.ToString(CultureInfo.InvariantCulture) + "|" + (fingerprint ?? "") +
                         "|" + CurrentEpoch().ToString(CultureInfo.InvariantCulture);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        }

        private static string ReadCursor(string cursor, out int position, out string fingerprint,
                                         out long epoch)
        {
            position = 0;
            fingerprint = null;
            epoch = -1;
            try
            {
                string raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                string[] parts = raw.Split('|');
                if (parts.Length < 2) return "this cursor is malformed.";
                if (!int.TryParse(parts[0], NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out position) || position < 0)
                    return "this cursor carries no usable position.";
                fingerprint = parts[1];
                // A TWO-PART CURSOR IS ONE THIS BUILD ISSUED BEFORE THE EPOCH EXISTED. It is
                // accepted and its model state is reported as unchecked, rather than refused:
                // breaking every outstanding cursor to add a check is a worse trade than
                // saying which ones could not be checked.
                if (parts.Length >= 3)
                    long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out epoch);
                return null;
            }
            catch (Exception)
            {
                return "this cursor is not a cursor this build issued.";
            }
        }

        /// <summary>
        /// The fingerprint of WHAT IS BEING ASKED, with the paging keys removed.
        ///
        /// Removing them is the point: a cursor must survive being sent back with the next
        /// page's options and must NOT survive a change to the question. Fingerprinting the
        /// whole request would invalidate every cursor the moment it was used.
        /// </summary>
        /// <summary>
        /// The same, taking the document. Reading Title can throw on a document that has been
        /// closed underneath us, and a fingerprint that threw would fail a read for a reason
        /// that has nothing to do with the read.
        /// </summary>
        public static string FingerprintOf(JObject request, string commandName, Document document)
        {
            string title = null;
            try { title = document == null ? null : document.Title; } catch { title = null; }
            return FingerprintOf(request, commandName, title);
        }

        public static string FingerprintOf(JObject request, string commandName, string documentTitle)
        {
            var copy = request == null ? new JObject() : (JObject)request.DeepClone();
            copy.Remove("cooperative");
            copy.Remove("cursor");
            copy.Remove("idempotency_key");
            return RequestFingerprint.Sha256Hex(
                commandName + "|" + (documentTitle ?? "") + "|" +
                copy.ToString(Formatting.None)).Substring(0, 24);
        }

        // =====================================================================
        // The reply
        // =====================================================================

        /// <summary>
        /// The block every cooperative reader adds, and NOTHING adds when the caller did not
        /// ask. Carries the scope's own account, the continuation, and the sentence about
        /// what a budget does not prove.
        /// </summary>
        /// <param name="ownCursorField">
        /// The name of the paging field this command ALREADY has, when it has one. Passing it
        /// suppresses the cooperative cursor and points the caller at the existing one: two
        /// continuation tokens over different things, with nothing to say which to send back,
        /// is worse than either. Null means this command has no paging of its own.
        /// </param>
        public JObject Report(CooperativeRead.Scope scope, int nextPosition, string fingerprint,
                              bool moreRemains, string ownCursorField = null)
        {
            if (!Requested) return null;

            var report = scope == null ? new JObject() : scope.Report();
            report["max_units"] = MaxUnits > 0 ? (JToken)MaxUnits : JValue.CreateNull();
            report["resumed_at"] = StartAt;
            report["response_ceiling_bytes"] = ResponseCeiling;
            report["model_state"] = CurrentEpoch();
            report["model_state_checked"] = CursorEpoch >= 0 || StartAt == 0;
            if (StartAt > 0 && CursorEpoch < 0)
                report["model_state_means"] =
                    "this cursor carried no model state, so nothing verified that the model is what it " +
                    "was when the cursor was issued. It came from a build before that check existed.";
            report["more_remains"] = moreRemains;
            if (ownCursorField != null)
            {
                report["cursor"] = JValue.CreateNull();
                report["cursor_means"] =
                    "this command pages with its own '" + ownCursorField + "', so no second cursor is " +
                    "issued here. Two continuation tokens over different things - one over the rows " +
                    "matched, one over the elements examined - with nothing to say which to send back " +
                    "is worse than either." +
                    (moreRemains ? " There IS more: continue with '" + ownCursorField + "'." : "");
            }
            else
            {
                report["cursor"] = moreRemains
                    ? (JToken)MakeCursor(nextPosition, fingerprint) : JValue.CreateNull();
                report["cursor_means"] = moreRemains
                    ? "send this back in cooperative.cursor, with the SAME request, to carry on where " +
                      "this stopped. A partial read that cannot be continued is one somebody has to " +
                      "redo from the start, and the second attempt freezes Revit exactly as long as " +
                      "the first."
                    : "there is nothing to continue: this read reached the end of its scope.";
            }
            report["what_the_budget_does_not_prove"] =
                "ui_budget_ms bounds how long this command SPENDS. It does not demonstrate that Revit's " +
                "interface stayed responsive, and nothing measured inside this process can: the command " +
                "holds the UI thread for its whole duration, the reply still travels back through the " +
                "pipe, and Revit's own event queue decides when the window repaints. Stopping work and " +
                "releasing the interface are different events. Evidence of fluidity comes from a sampler " +
                "watching from outside.";
            return report;
        }
    }
}
