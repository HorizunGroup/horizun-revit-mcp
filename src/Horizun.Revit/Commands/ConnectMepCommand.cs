// -----------------------------------------------------------------------------
// Horizun Revit MCP - connect two MEP connectors, and prove they are connected.
// Original Horizun code.
//
// G08 of the 2026-09-14 competitive inventory: "no confundas crear segmentos con
// construir una red conectada". Re-verified against 1.3.3, most of that gap was
// already closed - horizun_create_elements builds fittings (elbow, union,
// transition, tee, takeoff) with connector resolution and turn-angle checks, and
// horizun_plan_mep's network_census already counts open connectors. What was
// missing is the plainest operation of all:
//
//   TWO THINGS THAT ALREADY EXIST, JOINED DIRECTLY. A pipe meeting a pump. A
//   duct meeting an air terminal. A segment meeting the equipment it serves.
//   There is no fitting between them; Connector.ConnectTo is the whole operation,
//   and nothing in the bridge called it. ConnectTo appeared ZERO times in the
//   source before this file.
//
// WHY THE CHECKS ARE THE BULK OF IT. ConnectTo is a method that will happily
// produce nonsense: connectors of different domains, of different sizes, or
// metres apart. Revit does not always refuse, and what you get is a network that
// LOOKS connected in a schedule and does not flow. Every precondition below is
// therefore measured and reported in the caller's units, before anything is
// written:
//
//   * SAME DOMAIN. A pipe connector and a duct connector are not joinable, and
//     a caller who mixed up two element ids gets that sentence rather than a
//     Revit exception.
//   * PHYSICALLY COINCIDENT. Connectors must be at the same point within a
//     stated tolerance. The measured distance is in the refusal, because "they
//     are 3 mm apart" and "they are 3 m apart" are two completely different
//     mistakes.
//   * FREE, OR DELIBERATELY NOT. An already-connected connector refuses and
//     names what it is connected to. Silently stealing a connection is how a
//     branch disappears from a system nobody was looking at.
//   * SIZE, WHEN BOTH SIDES REPORT ONE. A size mismatch is reported and refused
//     unless allow_size_mismatch says otherwise - some equipment connectors
//     legitimately differ - but never ignored.
//
// AND THE PROOF IS A RE-READ. After the commit, both connectors are asked
// IsConnectedTo each other. "ConnectTo did not throw" is not evidence: it is the
// same class of claim as a count that came from the call rather than the model.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ConnectMepCommand : ICommand
    {
        public string Name => "horizun_connect_mep";

        public string Description =>
            "Connect or disconnect MEP connectors directly, with every precondition measured first and the " +
            "connection re-read from the model afterwards.";

        /// <summary>
        /// How far apart two connectors may be and still be joined. Revit's own snap is
        /// looser than this; the bound is deliberately tight because a connection made
        /// across a visible gap moves geometry, and moving somebody's geometry as a side
        /// effect of "connect these" is not what was asked for.
        /// </summary>
        private const double DefaultToleranceMm = 1.0;

        private const double MaxToleranceMm = 50.0;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            double toFeet;
            if (units == "mm") toFeet = 1 / 304.8;
            else if (units == "m") toFeet = 1 / 0.3048;
            else if (units == "feet") toFeet = 1;
            else return CommandResult.Fail("units must be mm, m or feet.");
            double fromFeet = 1 / toFeet;

            double toleranceMm = request.Value<double?>("tolerance") ?? DefaultToleranceMm;
            if (units == "m") toleranceMm = (request.Value<double?>("tolerance") ?? (DefaultToleranceMm / 1000.0)) * 1000.0;
            else if (units == "feet") toleranceMm = (request.Value<double?>("tolerance") ?? (DefaultToleranceMm / 304.8)) * 304.8;
            if (!(toleranceMm > 0) || toleranceMm > MaxToleranceMm)
                return CommandResult.Fail(
                    "tolerance must be greater than zero and at most " + MaxToleranceMm + " mm. A looser bound " +
                    "would let this command MOVE geometry to close a visible gap, which is not what 'connect " +
                    "these two' asks for.");
            double toleranceFeet = toleranceMm / 304.8;

            // DEFAULT UNCHANGED. `abort` is what this command has always done: one row
            // fails and nothing is written. `skip` has to be asked for, because a network
            // that is partly connected and reports success is worse than one that failed.
            string onRowFailure = (request.Value<string>("on_row_failure") ?? "abort").ToLowerInvariant();
            if (onRowFailure != "abort" && onRowFailure != "skip")
                return CommandResult.Fail(
                    "on_row_failure must be 'abort' (the default: one failed row writes nothing) or 'skip' " +
                    "(commit the rows that verified and report the rest). '" + onRowFailure + "' is neither.");

            JArray raw = request["actions"] as JArray;
            if (raw == null || raw.Count < 1 || raw.Count > 200)
                return CommandResult.Fail("actions must contain 1..200 entries.");

            string error;
            List<Plan> plans = PlanAll(doc, raw, toleranceFeet, fromFeet, out error);
            if (plans == null) return CommandResult.Fail(error + " Nothing was written.");

            string hash = DocumentGate.PlanHash(request, "units", "tolerance", "actions");
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            var resolved = Resolved(gate, app, plans);

            if (dry)
            {
                // The rehearsal is the real operation, provisionally: connectors are
                // joined, the document regenerates, every pair is re-read, and the whole
                // thing is rolled back. A validation that only checked arguments would
                // miss the case this command exists for - Revit accepting a ConnectTo
                // and producing nothing.
                Rehearsal rehearsal = Rehearse(doc, plans, "Horizun: rehearse MEP connections", onRowFailure);
                if (!rehearsal.RollbackConfirmed)
                    return CommandResult.FailWithDetail(
                        "The connection rehearsal could not confirm its rollback; the model state is uncertain.",
                        new JObject
                        {
                            ["state"] = "uncertain",
                            ["rollback_status"] = rehearsal.RollbackStatus,
                            ["write_started"] = true
                        });
                if (!rehearsal.Verified)
                {
                    // WHICH ROWS, AND WHY EACH. The old message asserted one cause - joined
                    // and did not read back - for every way this can fail, including a
                    // deleted element, which sends the reader to look at connectors.
                    Plan firstBad = plans.FirstOrDefault(p => p.AttemptOutcome == Plan.OutcomeFailed);
                    string lead = onRowFailure == "skip"
                        ? "The rehearsal could not get a single row to verify, so there would be nothing to " +
                          "commit even with on_row_failure='skip'."
                        : "The rehearsal refused row " + (firstBad == null ? 0 : firstBad.Index) + ": " +
                          (firstBad == null ? "a row failed without a recorded reason." : firstBad.AttemptReason) +
                          " on_row_failure is 'abort', so this is what the apply would do too.";
                    return CommandResult.FailWithDetail(
                        lead + " Nothing was committed - this is exactly the failure the rehearsal exists to " +
                        "catch, and it was caught before anything was written.",
                        new JObject
                        {
                            ["state"] = "refused",
                            ["rehearsal"] = rehearsal.Json,
                            ["rows"] = new JArray(plans.Select(p => p.Attempt(fromFeet))),
                            ["rows_mean"] =
                                "the states of the REHEARSAL, which was rolled back. Nothing here exists in " +
                                "the model. They are the list of what to fix."
                        });
                }

                DocumentGate.RecordResolvedPlan(resolved);
                var preview = new JObject
                {
                    ["dry_run"] = true,
                    ["valid"] = plans.Count,
                    ["would_connect"] = plans.Count(p => p.AttemptOutcome == Plan.OutcomeDone),
                    ["would_skip"] = plans.Count(p => p.AttemptOutcome != Plan.OutcomeDone),
                    ["on_row_failure"] = onRowFailure,
                    ["rehearsal"] = rehearsal.Json,
                    ["plan"] = new JArray(plans.Select(p => p.Attempt(fromFeet)))
                };
                if (preview.Value<int>("would_skip") > 0)
                    preview["would_skip_means"] =
                        "these rows did not verify in the rehearsal and would NOT be connected by the apply. " +
                        "You are seeing them rather than a refusal because on_row_failure='skip'. Each one " +
                        "carries its reason; a row fixed before the apply will be attempted like any other.";
                ApplicationOutcome.StampRehearsal(preview, plans.Count, 0,
                                                  plans.Count(p => p.AttemptOutcome != Plan.OutcomeDone), 0);
                DocumentGate.StampConfirmation(preview, gate, Name, hash, true,
                    "the token binds both element ids, both connector ids and the measured gap between them; " +
                    "apply re-measures every one of those before it writes.");
                return CommandResult.Ok(preview);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name") ?? "Horizun: connect MEP";
            using (var group = new TransactionGroup(doc, txName))
            {
                if (group.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("Could not start the connection TransactionGroup.");
                var tx = new Transaction(doc, txName);
                TransactionStatus txStatus = TransactionStatus.Uninitialized;
                try
                {
                    if (tx.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("Could not start the connection transaction.");

                    // ROW BY ROW, EACH IN ITS OWN SUB-TRANSACTION. That is what makes a
                    // skip safe: a row that throws half way through, or that connects and
                    // then does not read back, is rolled back on its own and leaves the
                    // others exactly as they were.
                    foreach (Plan p in plans)
                    {
                        string rowFailure = ApplyRow(doc, p);
                        if (rowFailure == null) continue;
                        if (onRowFailure == "abort")
                            throw new InvalidOperationException(
                                "row " + p.Index + " (" + p.Key + ") " + rowFailure +
                                " on_row_failure is 'abort', so the whole batch is being rolled back and " +
                                "NOTHING is written. Send on_row_failure='skip' to commit the rows that do " +
                                "verify.");
                    }

                    int verifiedRows = plans.Count(p => p.AttemptOutcome == Plan.OutcomeDone);
                    if (verifiedRows == 0)
                        throw new InvalidOperationException(
                            "no row verified, so there is nothing to commit. Each row's own reason is in the " +
                            "rows array.");

                    doc.Regenerate();
                    foreach (Plan p in plans)
                        if (p.AttemptOutcome == Plan.OutcomeDone && !Verify(doc, p))
                        {
                            // It verified inside its sub-transaction and does not verify
                            // now: something later in the batch undid it. That is not a
                            // partial result, it is a contradiction, and it aborts.
                            throw new InvalidOperationException(
                                "row " + p.Index + " (" + p.Key + ") verified when it was applied and does " +
                                "not verify now, before the commit. Something later in this batch undid it. " +
                                "Nothing is written.");
                        }

                    txStatus = tx.Commit();
                    if (txStatus != TransactionStatus.Committed)
                        throw new InvalidOperationException("The connection transaction returned " + txStatus + ".");
                    foreach (Plan p in plans)
                        if (p.AttemptOutcome == Plan.OutcomeDone && !Verify(doc, p))
                            throw new InvalidOperationException(
                                "row " + p.Index + " (" + p.Key + ") could not be read back after the " +
                                "transaction committed.");
                    TransactionStatus assimilated = group.Assimilate();
                    if (assimilated != TransactionStatus.Committed)
                        return CommandResult.FailWithDetail(
                            "The connection group did not assimilate; state is uncertain.",
                            new JObject
                            {
                                ["state"] = "uncertain",
                                ["transaction_group_status"] = assimilated.ToString()
                            });
                }
                catch (Exception ex)
                {
                    Guard.RollbackResult? txRollback = null;
                    try { if (tx.GetStatus() == TransactionStatus.Started) txRollback = Guard.RollBack(tx); } catch { }
                    Guard.RollbackResult? groupRollback;
                    try { groupRollback = Guard.RollBack(group); } catch { groupRollback = null; }
                    bool rolledBack = groupRollback.HasValue && groupRollback.Value.Confirmed;
                    var detail = new JObject
                    {
                        ["state"] = rolledBack ? "rolled_back" : "uncertain",
                        ["transaction_status"] = txRollback.HasValue
                            ? txRollback.Value.StatusName : txStatus.ToString(),
                        ["transaction_group_status"] = groupRollback.HasValue
                            ? groupRollback.Value.StatusName : "Error",

                        // WHICH ROW, AND WHY. Without this the caller has one exception
                        // message for up to 200 pairs and no way to aim a fix except
                        // bisecting the batch by hand against a model.
                        ["rows"] = new JArray(plans.Select(p => p.Attempt(fromFeet))),
                        ["rows_that_reached_verified"] = plans.Count(p => p.AttemptOutcome == Plan.OutcomeDone),
                        ["first_failed_row"] = plans.Where(p => p.AttemptOutcome == Plan.OutcomeFailed)
                                                    .Select(p => (JToken)p.Index).FirstOrDefault()
                                               ?? JValue.CreateNull(),
                        ["rows_mean"] = rolledBack
                            ? "these are the states of the ATTEMPT, not of the model. The group rolled back, " +
                              "so NONE of these connections exists - a row reading 'verified' verified inside " +
                              "a transaction that was then undone. They are here so the failing row can be " +
                              "found and fixed, which one exception message cannot support."
                            : "these are the states of the attempt. The rollback could NOT be confirmed, so " +
                              "what is in the model is unknown and must be read before anything is resent."
                    };
                    return CommandResult.FailWithDetail(
                        "The connection batch failed and was rolled back: " + ex.Message, detail);
                }
            }

            if (plans.Any(p => p.AttemptOutcome == Plan.OutcomeDone && !Verify(doc, p)))
                return CommandResult.FailWithDetail(
                    "A connection contradicted the reversible verification after the group assimilated; state " +
                    "is uncertain.",
                    new JObject
                    {
                        ["state"] = "uncertain",
                        ["transaction_group_status"] = "Committed",
                        ["host_verified"] = false
                    });

            int applied = plans.Count(p => p.AttemptOutcome == Plan.OutcomeDone);
            int skipped = plans.Count - applied;
            var done = new JObject
            {
                ["state"] = skipped == 0 ? "committed_verified" : "committed_partial",
                ["host_verified"] = true,
                ["requested"] = plans.Count,
                ["connected"] = applied,
                ["skipped"] = skipped,

                // A TRIMMED RESULT SAYS IT IS TRIMMED. A caller who asked for forty and
                // reads a success reply must not have to count the rows to discover that
                // thirty-nine happened.
                ["coverage_complete"] = skipped == 0,
                ["rows"] = new JArray(plans.Select(p => p.Result(doc, fromFeet))),
                ["open_connectors_after"] = OpenConnectorReport(doc, plans, fromFeet)
            };
            if (skipped > 0)
                done["partial_means"] =
                    skipped + " of " + plans.Count + " rows did not verify and were rolled back individually, " +
                    "under on_row_failure='skip' which you asked for. Each one carries its reason. The network " +
                    "is connected where these rows say so and NOT connected where they do not, which is a " +
                    "state no single number describes - open_connectors_after is measured from the model and " +
                    "is the thing to read next.";
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed,
                                            plans.Count, applied, applied, 0, skipped, 0);
            return CommandResult.Ok(done);
        }

        // =====================================================================
        // Planning - every precondition measured before anything is written
        // =====================================================================

        private static List<Plan> PlanAll(Document doc, JArray raw, double toleranceFeet, double fromFeet,
                                          out string error)
        {
            error = null;
            var plans = new List<Plan>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var claimed = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < raw.Count; i++)
            {
                JObject a = raw[i] as JObject;
                if (a == null) { error = "actions[" + i + "] is not an object."; return null; }

                string key = a.Value<string>("key");
                if (string.IsNullOrWhiteSpace(key) || !keys.Add(key))
                { error = "actions[" + i + "].key is empty or duplicated."; return null; }

                string op = (a.Value<string>("operation") ?? "connect").ToLowerInvariant();
                if (op != "connect" && op != "disconnect")
                { error = "actions[" + i + "].operation must be connect or disconnect."; return null; }

                Connector first, second;
                string why;
                if (!TryConnector(doc, a, "a", i, out first, out why)) { error = why; return null; }
                if (!TryConnector(doc, a, "b", i, out second, out why)) { error = why; return null; }

                if (first.Owner != null && second.Owner != null &&
                    Rid.Value(first.Owner.Id) == Rid.Value(second.Owner.Id) && first.Id == second.Id)
                { error = "actions[" + i + "] names the same connector twice."; return null; }

                // One connector may not appear in two actions of the same batch: the
                // second would silently steal the first's connection, and the reply
                // would report both as done.
                foreach (Connector c in new[] { first, second })
                {
                    string claim = Rid.Value(c.Owner.Id) + ":" + c.Id;
                    if (!claimed.Add(claim))
                    { error = "actions[" + i + "] uses connector " + claim + ", which another action in this " +
                              "batch already uses. One connector joins one thing."; return null; }
                }

                var plan = new Plan
                {
                    Index = i,
                    Key = key,
                    Operation = op,
                    AOwner = first.Owner.Id,
                    BOwner = second.Owner.Id,
                    AConnector = first.Id,
                    BConnector = second.Id,
                    ADomain = MepFacts.DomainName(first),
                    BDomain = MepFacts.DomainName(second),
                    GapFeet = first.Origin.DistanceTo(second.Origin)
                };

                if (op == "connect")
                {
                    string refusal = ConnectRefusal(plan, first, second, a, toleranceFeet, fromFeet);
                    if (refusal != null) { error = "actions[" + i + "]: " + refusal; return null; }
                }
                else
                {
                    if (!first.IsConnectedTo(second))
                    {
                        error = "actions[" + i + "]: those two connectors are not connected to each other, so " +
                                "there is nothing to disconnect.";
                        return null;
                    }
                }

                plans.Add(plan);
            }
            return plans;
        }

        /// <summary>
        /// Why this pair may NOT be joined, in the caller's units, or null.
        ///
        /// Ordered cheapest-first and most-diagnostic-first: a caller who passed two
        /// unrelated ids wants to hear "different domains", not a size comparison
        /// between a pipe and a cable tray.
        /// </summary>
        private static string ConnectRefusal(Plan plan, Connector x, Connector y, JObject a,
                                             double toleranceFeet, double fromFeet)
        {

            if (x.IsConnectedTo(y))
                return "those connectors are ALREADY connected to each other. Nothing was written; this is " +
                       "reported rather than treated as success so a replay cannot look like new work.";

            string xDomain = MepFacts.DomainName(x), yDomain = MepFacts.DomainName(y);
            if (!string.Equals(xDomain, yDomain, StringComparison.Ordinal))
                return "the connectors are in different domains (" + xDomain + " and " + yDomain + "). A " +
                       xDomain + " connector cannot carry a " + yDomain + " one. Check the element ids.";

            if (x.IsConnected && !(a.Value<bool?>("replace_existing") ?? false))
                return "connector " + x.Id + " of element " + Rid.Value(x.Owner.Id) + " is already connected to " +
                       Describe(x) + ". Connecting it here would silently take it away from that, which is how a " +
                       "branch disappears from a system nobody was looking at. Disconnect it first, or pass " +
                       "replace_existing.";
            if (y.IsConnected && !(a.Value<bool?>("replace_existing") ?? false))
                return "connector " + y.Id + " of element " + Rid.Value(y.Owner.Id) + " is already connected to " +
                       Describe(y) + ". Disconnect it first, or pass replace_existing.";

            if (plan.GapFeet > toleranceFeet)
                return "the connectors are " + Round(plan.GapFeet * fromFeet) + " apart, and the tolerance is " +
                       Round(toleranceFeet * fromFeet) + ". They are not at the same point. Nothing was moved: " +
                       "closing a visible gap would relocate somebody's geometry as a side effect of 'connect " +
                       "these two'.";

            string sizeProblem = SizeMismatch(x, y, fromFeet);
            if (sizeProblem != null && !(a.Value<bool?>("allow_size_mismatch") ?? false))
                return sizeProblem + " Nothing was written. Some equipment connectors legitimately differ from " +
                       "the run that serves them; pass allow_size_mismatch when that is the case, and the " +
                       "measured difference stays in the reply either way.";

            plan.SizeNote = sizeProblem;
            return null;
        }

        /// <summary>
        /// A size difference, measured, or null when both sides agree - or when at least
        /// one of them reports no size at all, which is normal for an electrical
        /// connector and is not a mismatch.
        /// </summary>
        private static string SizeMismatch(Connector x, Connector y, double fromFeet)
        {
            ConnectorProfileType xShape, yShape;
            try { xShape = x.Shape; yShape = y.Shape; } catch { return null; }

            if (xShape != yShape)
                return "the connector profiles differ: " + xShape + " and " + yShape + ".";

            try
            {
                if (xShape == ConnectorProfileType.Round)
                {
                    double dx = x.Radius, dy = y.Radius;
                    if (dx <= 0 || dy <= 0) return null;
                    if (Math.Abs(dx - dy) > 1e-6)
                        return "the radii differ: " + Round(dx * fromFeet) + " and " + Round(dy * fromFeet) + ".";
                    return null;
                }
                if (xShape == ConnectorProfileType.Rectangular || xShape == ConnectorProfileType.Oval)
                {
                    double xw = x.Width, xh = x.Height, yw = y.Width, yh = y.Height;
                    if (xw <= 0 || yw <= 0) return null;
                    if (Math.Abs(xw - yw) > 1e-6 || Math.Abs(xh - yh) > 1e-6)
                        return "the profiles differ: " + Round(xw * fromFeet) + " x " + Round(xh * fromFeet) +
                               " and " + Round(yw * fromFeet) + " x " + Round(yh * fromFeet) + ".";
                    return null;
                }
            }
            catch { return null; }   // a connector that does not report a size is not a mismatch
            return null;
        }

        private static bool TryConnector(Document doc, JObject a, string side, int index,
                                         out Connector connector, out string error)
        {
            connector = null;
            error = null;

            long rawId = a.Value<long?>(side + "_element_id") ?? -1;
            if (!Rid.CanRepresent(rawId))
            { error = "actions[" + index + "]." + side + "_element_id is required."; return false; }

            Element owner = doc.GetElement(Rid.Make(rawId));
            if (owner == null)
            { error = "actions[" + index + "]." + side + "_element_id " + rawId + " does not exist."; return false; }

            ConnectorManager manager = MepFacts.ManagerOf(owner);
            if (manager == null)
            {
                error = "actions[" + index + "]." + side + "_element_id " + rawId + " (" +
                        owner.GetType().Name + ") has no connectors, so it cannot be joined to anything.";
                return false;
            }

            List<Connector> ordered = MepFacts.Ordered(manager);
            if (ordered.Count == 0)
            { error = "actions[" + index + "]." + side + "_element_id " + rawId + " reports no connectors."; return false; }

            int? named = a.Value<int?>(side + "_connector");
            if (named != null)
            {
                connector = ordered.FirstOrDefault(c => c.Id == named.Value);
                if (connector == null)
                {
                    error = "actions[" + index + "]." + side + "_connector " + named.Value + " is not a connector " +
                            "of element " + rawId + ". It has: " + string.Join(", ", ordered.Select(c => c.Id.ToString())) + ".";
                    return false;
                }
                return true;
            }

            // NO NAMED CONNECTOR: only acceptable when there is exactly one free one.
            // Choosing among several would be this command picking which end of a pipe
            // the caller meant, and getting that wrong is a network that looks right.
            List<Connector> free = ordered.Where(c => !c.IsConnected).ToList();
            if (free.Count == 1) { connector = free[0]; return true; }

            error = "actions[" + index + "]." + side + "_connector is required: element " + rawId + " has " +
                    ordered.Count + " connector(s), " + free.Count + " of them free, so there is no single " +
                    "unambiguous end to join. Read them with horizun_query_model and name one. Nothing was " +
                    "written - choosing for you would be choosing which end of the pipe you meant.";
            return false;
        }

        // =====================================================================
        // Apply and verify
        // =====================================================================

        private static void Apply(Document doc, Plan p)
        {
            Connector a, b;
            p.Resolve(doc, out a, out b);   // throws with the reason; the batch rolls back

            if (p.Operation == "connect")
            {
                if (a.IsConnected) DisconnectAll(a);
                if (b.IsConnected) DisconnectAll(b);
                a.ConnectTo(b);
            }
            else
            {
                a.DisconnectFrom(b);
            }
        }

        /// <summary>
        /// Apply ONE row inside its own SubTransaction, and record what happened to it.
        ///
        /// Returns null when the row connected and read back, or the reason it did not.
        /// Either way the row keeps its own outcome, so a failure at row 31 does not erase
        /// what is known about rows 1 to 30.
        ///
        /// THE SUB-TRANSACTION IS WHAT MAKES SKIPPING HONEST. A ConnectTo that throws half
        /// way, or a pair that connects and then does not read back, is undone here and
        /// leaves the surrounding transaction exactly as it was. Without it, skipping
        /// would mean committing alongside whatever the failed row left behind.
        /// </summary>
        private static string ApplyRow(Document doc, Plan p)
        {
            var sub = new SubTransaction(doc);
            try
            {
                if (sub.Start() != TransactionStatus.Started)
                    return p.Fail("its sub-transaction would not start.");

                Apply(doc, p);
                doc.Regenerate();

                if (!Verify(doc, p))
                {
                    // WHAT REVIT SAID ABOUT THE UNDO, not what we assume it did.
                    // A status other than RolledBack leaves this row's work in an
                    // UNCERTAIN state, and a caller retrying it would be building
                    // on top of something nobody can see.
                    string undone = Guard.RollBack(sub).StatusName;
                    if (!string.Equals(undone, "RolledBack", StringComparison.Ordinal))
                        return p.Fail("could not be verified, AND the undo of this row returned '" + undone +
                                      "' instead of RolledBack. Whatever it did may still be in the model: " +
                                      "re-read before retrying.");
                    return p.Fail(p.Operation == "connect"
                        ? "was joined and then did not read back as connected. This is the failure the " +
                          "verification exists to catch: ConnectTo returning without throwing says nothing " +
                          "about what is in the document."
                        : "was disconnected and still reads as connected.");
                }

                if (sub.Commit() != TransactionStatus.Committed)
                    return p.Fail("its sub-transaction did not commit.");

                p.AttemptOutcome = Plan.OutcomeDone;
                p.AttemptReason = null;
                return null;
            }
            catch (Exception ex)
            {
                // The same rule on the throwing path: the undo's own status is
                // part of what happened, and "we tried" is not an answer.
                string undone = null;
                try
                {
                    if (sub.GetStatus() == TransactionStatus.Started) undone = Guard.RollBack(sub).StatusName;
                }
                catch (Exception undoFailed) { undone = "threw: " + undoFailed.Message; }

                return p.Fail("failed: " + ex.Message +
                    (undone == null || string.Equals(undone, "RolledBack", StringComparison.Ordinal)
                        ? ""
                        : " The undo of this row returned '" + undone + "' instead of RolledBack, so what it " +
                          "did may still be in the model."));
            }
        }

        private static void DisconnectAll(Connector connector)
        {
            foreach (Connector other in connector.AllRefs.OfType<Connector>().ToList())
            {
                try { if (connector.IsConnectedTo(other)) connector.DisconnectFrom(other); }
                catch { /* a reference that cannot be dropped is reported by the verify */ }
            }
        }

        /// <summary>
        /// Ask the MODEL, not the call. Both connectors are asked whether they are
        /// connected to each other, because ConnectTo returning without throwing says
        /// nothing about what is in the document.
        /// </summary>
        private static bool Verify(Document doc, Plan p)
        {
            Connector a, b;
            if (!p.TryResolve(doc, out a, out b)) return false;
            try
            {
                bool connected = a.IsConnectedTo(b) && b.IsConnectedTo(a);
                return p.Operation == "connect" ? connected : !connected;
            }
            catch { return false; }
        }

        /// <summary>
        /// Rehearse the batch THE WAY THE APPLY WOULD RUN IT, and roll it back.
        ///
        /// Row by row, through the same ApplyRow, so the two cannot drift apart: a
        /// rehearsal that passes and an apply that fails on the same input would make this
        /// command worse than having no rehearsal, because it would have been believed.
        ///
        /// `abort` - the default - verifies only if EVERY row verified, which is what the
        /// apply will require. `skip` verifies if at least one did, because that is what
        /// the apply will commit; a batch where nothing verifies has nothing to commit
        /// under either mode.
        /// </summary>
        private static Rehearsal Rehearse(Document doc, List<Plan> plans, string name, string onRowFailure)
        {
            var rehearsal = new Rehearsal();
            var tx = new Transaction(doc, name);
            try
            {
                if (tx.Start() != TransactionStatus.Started)
                {
                    rehearsal.Json = new JObject { ["error"] = "the rehearsal transaction would not start" };
                    return rehearsal;
                }

                foreach (Plan p in plans)
                {
                    ApplyRow(doc, p);
                    // No early exit even under `abort`. Stopping at the first bad row would
                    // hand back one problem at a time, and a caller with forty pairs and
                    // three bad ones would rehearse three times to learn three things.
                }

                doc.Regenerate();
                int verified = plans.Count(p => p.AttemptOutcome == Plan.OutcomeDone);
                rehearsal.Verified = onRowFailure == "skip" ? verified > 0 : verified == plans.Count;
                rehearsal.Json = new JObject
                {
                    ["rehearsed"] = plans.Count,
                    ["verified"] = verified,
                    ["failed"] = plans.Count - verified,
                    ["on_row_failure"] = onRowFailure,
                    ["means"] = "each connection was made provisionally in its own sub-transaction, the " +
                                "document regenerated, and the pair re-read before the whole rehearsal was " +
                                "rolled back. Nothing above was inferred from a call returning, and every " +
                                "row was attempted so that one rehearsal reports every problem rather than " +
                                "the first."
                };
            }
            catch (Exception ex)
            {
                rehearsal.Verified = false;
                rehearsal.Json = new JObject { ["error"] = ex.Message };
            }
            finally
            {
                Guard.RollbackResult? rollback = null;
                try { if (tx.GetStatus() == TransactionStatus.Started) rollback = Guard.RollBack(tx); }
                catch { rollback = null; }
                rehearsal.RollbackConfirmed = rollback.HasValue && rollback.Value.Confirmed;
                rehearsal.RollbackStatus = rollback.HasValue ? rollback.Value.StatusName : "Error";
            }
            return rehearsal;
        }

        /// <summary>
        /// Every connector still open on the elements this batch touched.
        ///
        /// It is reported on SUCCESS, not only on failure: a run that connected what it
        /// was asked to and left three ends dangling is a correct command and an
        /// incomplete network, and the caller is the one who can tell which.
        /// </summary>
        private static JArray OpenConnectorReport(Document doc, List<Plan> plans, double fromFeet)
        {
            var rows = new JArray();
            var seen = new HashSet<long>();
            foreach (Plan p in plans)
                foreach (ElementId ownerId in new[] { p.AOwner, p.BOwner })
                {
                    if (!seen.Add(Rid.Value(ownerId))) continue;
                    Element owner = doc.GetElement(ownerId);
                    if (owner == null) continue;
                    ConnectorManager manager = MepFacts.ManagerOf(owner);
                    if (manager == null) continue;
                    foreach (Connector each in MepFacts.Ordered(manager))
                    {
                        bool open;
                        try { open = !each.IsConnected; } catch { continue; }
                        if (!open) continue;
                        rows.Add(new JObject
                        {
                            ["element_id"] = Rid.Value(owner.Id),
                            ["connector"] = MepFacts.Json(each, Transform.Identity, fromFeet)
                        });
                    }
                }
            return rows;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static string Describe(Connector connector)
        {
            try
            {
                var names = new List<string>();
                foreach (Connector other in connector.AllRefs.OfType<Connector>())
                {
                    if (other.Owner == null) continue;
                    if (connector.Owner != null && Rid.Value(other.Owner.Id) == Rid.Value(connector.Owner.Id)) continue;
                    names.Add("element " + Rid.Value(other.Owner.Id) + " connector " + other.Id);
                }
                return names.Count == 0 ? "something this command could not name" : string.Join(", ", names);
            }
            catch { return "something this command could not read"; }
        }

        private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

        /// <summary>
        /// The plan as RESOLVED, so the confirmation token binds what will actually be
        /// joined rather than the question that was asked.
        ///
        /// The recorded facts are the ones a stale plan would move: both owners' unique
        /// ids, both connector ids, whether each end was already connected, and the
        /// measured gap. If somebody connects one of those ends between the rehearsal and
        /// the apply, the fingerprint changes and the apply refuses - which is the whole
        /// reason this exists rather than hashing the request.
        /// </summary>
        private static ResolvedPlan Resolved(GateResult gate, UIApplication app, List<Plan> plans)
        {
            var resolved = new ResolvedPlan
            {
                Command = "horizun_connect_mep",
                DocumentKey = gate.Fingerprint,
                RevitVersion = app.Application.VersionNumber,
                DocumentFingerprint = gate.Identity.FingerprintDigest()
            };
            foreach (Plan p in plans)
            {
                Connector a, b;
                bool live = p.TryResolve(gate.Document, out a, out b);
                var before = new Dictionary<string, string>
                {
                    ["operation"] = p.Operation,
                    ["a"] = SafeUniqueId(live ? a.Owner : null) + "#" + p.AConnector,
                    ["b"] = SafeUniqueId(live ? b.Owner : null) + "#" + p.BConnector,
                    ["a_connected"] = live ? SafeConnected(a).ToString() : "unreadable",
                    ["b_connected"] = live ? SafeConnected(b).ToString() : "unreadable",
                    ["gap_feet"] = Math.Round(p.GapFeet, 9)
                        .ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                };
                resolved.Elements.Add(new PlannedElement
                {
                    UniqueId = "connection:" + p.Key,
                    Category = "mep_connection",
                    Action = PlannedAction.Modify,
                    BeforeValues = before
                });
            }
            return resolved;
        }

        private static string SafeUniqueId(Element element)
        {
            try { return element == null ? "<none>" : element.UniqueId; }
            catch { return "<unreadable>"; }
        }

        private static bool SafeConnected(Connector connector)
        {
            try { return connector.IsConnected; }
            catch { return false; }
        }

        private sealed class Rehearsal
        {
            public bool Verified;
            public bool RollbackConfirmed;
            public string RollbackStatus = "Unknown";
            public JObject Json = new JObject();
        }

        /// <summary>
        /// One planned connection, stored as IDENTITY rather than as live objects.
        ///
        /// WHY NOT HOLD THE Connector. A Connector is a view onto the element's connector
        /// manager, obtained from a Document in one state. This command deliberately
        /// rehearses - it connects, regenerates, re-reads and ROLLS BACK - and a rollback
        /// is a document state change. Holding the Connector across it means the apply
        /// phase would be writing through a handle taken from a document that no longer
        /// exists in that form, which is the class of bug that produces either a silent
        /// no-op or an exception nobody can attribute.
        ///
        /// So the plan carries element ids and connector ids, which are stable facts, and
        /// asks the document for the live pair at each phase. Resolution that FAILS is a
        /// failure, never an empty success: an element deleted between the rehearsal and
        /// the apply must stop the batch rather than quietly connect nothing.
        /// </summary>
        private sealed class Plan
        {
            public int Index;
            public string Key;
            public string Operation;

            public ElementId AOwner, BOwner;
            public int AConnector, BConnector;
            public string ADomain, BDomain;

            public double GapFeet;
            public string SizeNote;

            public const string OutcomeNotAttempted = "not_attempted";
            public const string OutcomeDone = "verified_in_transaction";
            public const string OutcomeFailed = "failed";

            /// <summary>
            /// What happened to THIS row during the apply. Never a claim about the model:
            /// a row can reach OutcomeDone inside a transaction that is then rolled back,
            /// and the reply that carries these says so in as many words.
            /// </summary>
            public string AttemptOutcome = OutcomeNotAttempted;

            public string AttemptReason;

            /// <summary>Record a failure and hand the reason back in one move.</summary>
            public string Fail(string reason)
            {
                AttemptOutcome = OutcomeFailed;
                AttemptReason = reason;
                return reason;
            }

            /// <summary>This row's attempt, for a reply that must not claim anything about the model.</summary>
            public JObject Attempt(double fromFeet)
            {
                JObject row = Json(fromFeet);
                row["attempt_outcome"] = AttemptOutcome;
                row["attempt_reason"] = AttemptReason == null ? (JToken)JValue.CreateNull() : AttemptReason;
                return row;
            }

            /// <summary>The live pair, from THIS document state. Throws with the reason.</summary>
            public void Resolve(Document doc, out Connector a, out Connector b)
            {
                a = Live(doc, AOwner, AConnector, "a");
                b = Live(doc, BOwner, BConnector, "b");
            }

            /// <summary>The live pair, or false. For reporting paths that must not throw.</summary>
            public bool TryResolve(Document doc, out Connector a, out Connector b)
            {
                a = null; b = null;
                try { Resolve(doc, out a, out b); return true; }
                catch { return false; }
            }

            private static Connector Live(Document doc, ElementId ownerId, int connectorId, string side)
            {
                Element owner = doc.GetElement(ownerId);
                if (owner == null)
                    throw new InvalidOperationException(
                        "element " + Rid.Value(ownerId) + " (side " + side + ") no longer exists in this " +
                        "document. It was there when the plan was made; connecting nothing would be worse " +
                        "than stopping.");
                ConnectorManager manager = MepFacts.ManagerOf(owner);
                if (manager == null)
                    throw new InvalidOperationException(
                        "element " + Rid.Value(ownerId) + " (side " + side + ") no longer reports a connector " +
                        "manager.");
                foreach (Connector candidate in MepFacts.Ordered(manager))
                    if (candidate.Id == connectorId) return candidate;
                throw new InvalidOperationException(
                    "element " + Rid.Value(ownerId) + " no longer has connector " + connectorId +
                    " (side " + side + ").");
            }

            public JObject Json(double fromFeet) => new JObject
            {
                ["index"] = Index,
                ["key"] = Key,
                ["operation"] = Operation,
                ["a"] = new JObject
                {
                    ["element_id"] = Rid.Value(AOwner),
                    ["connector"] = AConnector,
                    ["domain"] = ADomain
                },
                ["b"] = new JObject
                {
                    ["element_id"] = Rid.Value(BOwner),
                    ["connector"] = BConnector,
                    ["domain"] = BDomain
                },
                ["gap"] = Round(GapFeet * fromFeet),
                ["size_note"] = SizeNote == null ? (JToken)JValue.CreateNull() : SizeNote
            };

            public JObject Result(Document doc, double fromFeet)
            {
                JObject row = Json(fromFeet);
                Connector a, b;
                bool resolved = TryResolve(doc, out a, out b);
                bool connected = false;
                if (resolved)
                {
                    try { connected = a.IsConnectedTo(b); } catch { connected = false; }
                }
                row["attempt_outcome"] = AttemptOutcome;
                row["attempt_reason"] = AttemptReason == null ? (JToken)JValue.CreateNull() : AttemptReason;
                row["connected_after_commit"] = resolved ? (JToken)connected : JValue.CreateNull();
                row["verified"] = resolved && (Operation == "connect" ? connected : !connected);
                row["means"] = resolved
                    ? "connected_after_commit was READ from both connectors after the commit, through a fresh " +
                      "lookup by element and connector id. The call returning without throwing is not evidence " +
                      "and is not reported as any."
                    : "the pair could not be resolved after the commit, so nothing is claimed about it.";
                return row;
            }
        }
    }
}
