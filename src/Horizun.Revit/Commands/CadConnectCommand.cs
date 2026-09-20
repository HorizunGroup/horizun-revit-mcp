// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_cad_connect — carry out the junctions a network reading found.
//
// This is the last step of the DWG-to-BIM route and the one that was missing.
// horizun_plan_from_cad and horizun_apply_cad_plan build the pipes; until this
// command existed, they were pipes that touched. Nothing flowed, no system
// spanned them, a schedule counted fourteen pieces where the drawing showed one
// run, and the model looked finished.
//
// TWO KINDS OF JUNCTION, HANDLED IN TWO DIFFERENT PLACES, on purpose.
//
//   DIRECT       two collinear ends that the drawing split and Revit should
//                simply join. No fitting belongs there. horizun_connect_mep does
//                exactly this, with every precondition measured and the join
//                re-read from the model afterwards.
//
//   A FITTING    an elbow, a tee, a cross. Revit has a factory for each and
//                horizun_create_elements drives them, with a rehearsal, an
//                atomic batch and a re-read.
//
// BOTH ARE DELEGATED, and this command writes nothing itself. What it adds is the
// step neither of them can do: a drawing names a junction by a POINT, and both of
// those commands take an element and a CONNECTOR INDEX. Turning one into the
// other - and REFUSING when two of an element's connectors are equally near,
// rather than taking whichever comparison won - is the whole of what is here.
// A second writer would be a second set of rules about when a connection is
// legitimate, and the two would drift.
//
// WHAT IT WILL NOT DO. It will not connect a junction the network reading marked
// for review — an irregular three-way, an elevation change, a degree no fitting
// covers. Those arrive here with automatic=false and are reported as skipped
// with the reading's own sentence, because the reason they need a person does
// not stop being true when the call is made again.
//
// It will not connect a CROSSING. A crossing is two lines that meet in plan and
// not in the building; horizun_cad_networks never proposes one, and a caller who
// hand-writes one gets the same refusal as everything else that was not drawn.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class CadConnectCommand : ICommand
    {
        private readonly Func<string, ICommand> _resolve;
        public CadConnectCommand(Func<string, ICommand> resolve) { _resolve = resolve; }

        public string Name => "horizun_cad_connect";

        public string Description =>
            "Carry out the junctions horizun_cad_networks found, on elements that already exist. It writes " +
            "nothing itself: a DIRECT junction goes to horizun_connect_mep and an ELBOW, TEE or CROSS to " +
            "horizun_create_elements, both of which measure their own preconditions, rehearse and re-read. " +
            "What this adds is the step neither can do - a drawing names a junction by a POINT and both of " +
            "those take an element and a CONNECTOR INDEX - and it REFUSES rather than guessing when two of " +
            "an element's connectors are equally near, because on a fitting that is the run outlet and the " +
            "branch outlet and the wrong one flows wrong while looking right. Junctions the reading marked " +
            "for review are skipped with its reason; junctions whose runs the conversion never built are " +
            "skipped as unresolved rather than refusing the whole call. Writes through its delegates: needs " +
            "target_document and honours dry_run.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            string title = SafeTitle(doc);

            // A REFIT is its own operation: see CadRefit. It writes through the same delegates.
            if (request["refit"] is JArray refit && refit.Count > 0)
                return CadRefit.Run(app, doc, title, request, _resolve);

            JArray junctions = request["junctions"] as JArray;
            if (junctions == null || junctions.Count == 0)
                return CommandResult.Fail(
                    "junctions is required and must carry at least one entry. Copy them from " +
                    "horizun_cad_networks' reply, with each run id replaced by the element id that was built " +
                    "from it - the mapping is in horizun_apply_cad_plan's created rows.");

            double tolerance = request.Value<double?>("connector_tolerance_mm") ?? 25.0;
            // how far a drawn transition's ends may sit from where the drawing put them (mm)
            double fitTolerance = request.Value<double?>("transition_fit_tolerance_mm") ?? 25.4;
            // THE TRANSITION TYPES THE CALLER DECLARES, in order. Revit builds a drawn transition with whatever its
            // routing preference says - one family, one angle, one length - and a drawn piece is usually longer than
            // that. Each declared type is TRIED on the fitting and MEASURED against the drawing; the first that fits
            // is kept. Nothing is invented: the types are the caller's, and a type that does not fit is not used.
            JArray declaredTypes = request["transition_types"] as JArray;
            if (tolerance <= 0)
                return CommandResult.Fail(
                    "connector_tolerance_mm must be positive: it is how far a connector may sit from the point " +
                    "the drawing put the junction at, and at zero nothing a real model contains is near enough.");
            bool dryRun = request.Value<bool?>("dry_run") ?? true;
            bool includeReview = request.Value<bool?>("include_needs_review") ?? false;

            // ---- parse first, write nothing ------------------------------------
            var parsed = new List<Junction>();

            // THE INDEX IS BUILT ONCE, AND ONLY WHEN SOMETHING ASKS FOR IT. It reads
            // Extensible Storage off every element in the document; doing that per
            // junction would make the parse cost more than the work.
            Dictionary<string, List<Element>> candidateIndex = null;
            Dictionary<string, List<Element>> semanticIndex = null;
            if (junctions.OfType<JObject>().Any(x => Names(x, "candidate_id")))
            {
                List<string> provenanceProblems;
                candidateIndex = CadProvenanceStore.IndexByCandidate(doc, out provenanceProblems);
            }
            if (junctions.OfType<JObject>().Any(x => Names(x, "semantic_id")))
            {
                List<string> semanticProblems;
                semanticIndex = CadProvenanceStore.IndexBySemanticId(doc, out semanticProblems);
            }

            for (int i = 0; i < junctions.Count; i++)
            {
                string bad;
                Junction j = Junction.Read(junctions[i] as JObject, i, doc, candidateIndex, semanticIndex,
                                           out bad);
                if (bad != null)
                    return CommandResult.FailWithDetail(bad, new JObject
                    {
                        ["refused"] = "malformed_junction",
                        ["index"] = i,
                        ["error"] = bad,
                        ["means"] = "the whole call was refused and NOTHING was written. A run that skipped the " +
                                    "bad entry and joined the rest would leave a model whose network is right " +
                                    "everywhere the caller looked."
                    });
                parsed.Add(j);
            }

            // DRAWN TRANSITIONS FIRST. Placing one brings its runs' ends together, which a run already
            // holding an elbow at its other end cannot do: MEASURED (campaign 7), Revit refused the move
            // with "the family is connected in a network and can no longer keep the connectivity". The
            // order is otherwise the caller's (a stable sort).
            parsed = parsed.Where(x => x.Fitting == "transition" && x.Drawn != null)
                           .Concat(parsed.Where(x => !(x.Fitting == "transition" && x.Drawn != null))).ToList();

            var rows = new JArray();
            int joined = 0, skipped = 0, refused = 0, delegated = 0, existing = 0;

            // WHAT A REHEARSAL PROVES. Rehearsing each fitting on its own proves its arguments
            // and references only: MEASURED on a real plan, eight elbows rehearsed and one could be
            // built, because each elbow trims the ducts it joins and the next one then has less room.
            // So a rehearsal carries the junctions out IN SEQUENCE inside a transaction group that is
            // rolled back - every later junction sees what the earlier ones did - and says so. When
            // the group cannot be opened the scope is declared as arguments-and-references only.
            string rehearsalMode = request.Value<string>("rehearsal") ?? "sequential";
            if (rehearsalMode != "sequential" && rehearsalMode != "isolated")
                return CommandResult.Fail("rehearsal must be \"sequential\" (the default) or \"isolated\".");
            RolledBackRehearsal group = null;
            string rehearsalScope = dryRun ? "arguments_and_references_only" : "applied";
            if (dryRun && rehearsalMode == "sequential")
            {
                group = new RolledBackRehearsal(doc, "Horizun: cad_connect sequential rehearsal");
                if (group.Started) rehearsalScope = "sequential_rolled_back";
                else group = null;
            }
            bool writeNow = !dryRun || group != null;
            string runKey = "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            try
            {
            foreach (Junction j in parsed)
            {
                if (j.Unresolved != null)
                {
                    skipped++;
                    rows.Add(j.Row("skipped_unresolved", null, j.Unresolved));
                    continue;
                }

                if (!j.Automatic && !includeReview)
                {
                    skipped++;
                    // TWO DIFFERENT SILENCES, and they are not the same statement.
                    //
                    // A junction that came from the network reading carries the
                    // reading's own reason. A junction a caller assembled by hand
                    // carries no 'automatic' flag at all - the default is cautious,
                    // which is right, but saying "the reading marked this for a
                    // person" about a junction no reading ever saw is a sentence
                    // that sends somebody looking for a review that does not exist.
                    rows.Add(j.Row("skipped_needs_review", null,
                        j.Says != null
                            ? "the network reading marked this junction for a person: " + j.Says +
                              ". Sending it again does not make the reason untrue - send " +
                              "include_needs_review=true only when somebody has looked."
                            : "this junction is not marked automatic and carries no reason. Nothing here " +
                              "said it needs review - the default is cautious because a junction whose " +
                              "provenance nobody stated is a junction nobody vouched for. Send " +
                              "automatic=true on the junction if you are vouching for it, or " +
                              "include_needs_review=true to carry out every junction in this call."));
                    continue;
                }

                if (j.Fitting == "none")
                {
                    skipped++;
                    rows.Add(j.Row("skipped_no_fitting", null,
                        "this junction proposes no fitting: " + (j.Says ?? "(no reason given)")));
                    continue;
                }

                // A STRAIGHT CONTINUATION BETWEEN TWO DIFFERENT SECTIONS is a transition, not a
                // direct join: joining a 16x8 end to a 12x8 end directly is either refused by Revit
                // or a network that does not flow. The reading proposes "direct" because a plan line
                // carries no size; the built runs do, so the decision is made here, from them.
                string sectionChange = null;
                if (j.Fitting == "direct" && j.Elements.Count == 2)
                {
                    string sa = SectionOf(j.Elements[0]), sb = SectionOf(j.Elements[1]);
                    if (sa != null && sb != null && sa != sb)
                    {
                        sectionChange = "the two runs differ in section (" + sa + " / " + sb + "): a transition, not a direct join";
                        j.Fitting = "transition";
                    }
                }

                // ALREADY THERE? Read from the connectors and the fitting between them - never from
                // "the ends are close". A repeat must not rehearse a fitting that exists and fits.
                JObject was = Existing(j, tolerance);
                if (was != null)
                {
                    string ws = was.Value<string>("state");
                    JObject exRow = j.Row(ws == "already_connected" ? "already_connected" : "refused",
                                          ws == "already_connected" ? null : ws, was.Value<string>("says"));
                    exRow["action"] = j.Fitting;
                    exRow["existing"] = was;
                    rows.Add(exRow);
                    if (ws == "already_connected") existing++; else refused++;
                    continue;
                }
                int mark = Interference.Current?.Seen.Count ?? 0;

                if (j.Fitting == "direct")
                {
                    if (j.Elements.Count != 2)
                    {
                        refused++;
                        rows.Add(j.Row("refused", "direct_needs_two",
                            "a direct join is two ends meeting; this junction names " +
                            j.Elements.Count.ToString(CultureInfo.InvariantCulture) + "."));
                        continue;
                    }

                    // THE POINT BECOMES TWO CONNECTOR INDICES, which is the one
                    // thing horizun_connect_mep cannot do for itself. Everything
                    // after that - domain, coincidence, occupancy, size, the
                    // rehearsal and the re-read - is its business and not this
                    // command's.
                    MepConnectorPick pa = MepConnect.Nearest(j.Elements[0], j.At, tolerance);
                    MepConnectorPick pb = MepConnect.Nearest(j.Elements[1], j.At, tolerance);
                    if (!pa.Found || !pb.Found)
                    {
                        refused++;
                        JObject row = j.Row("refused", pa.Found ? pb.Refusal : pa.Refusal,
                            "the connector the drawing means could not be resolved from its point, so " +
                            "nothing was sent to horizun_connect_mep.");
                        row["connector_detail"] = pa.Found ? pb.RefusalDetail : pa.RefusalDetail;
                        rows.Add(row);
                        continue;
                    }

                    JObject directRow = DelegateDirect(app, title, j, pa, pb, !writeNow, request, group != null ? runKey : "");
                    Attribute(directRow, j, mark, writeNow, tolerance, group != null);
                    rows.Add(directRow);
                    string directState = directRow.Value<string>("state");
                    if (directState == "joined" || directState == "would_join") joined++;
                    else refused++;
                    continue;
                }

                // ---- a DRAWN transition: built, measured against the space the drawing gave it, and
                // kept only if it fits. Its length is Revit's family's, not the drawing's; a transition
                // that needs the ducts moved by more than the declared tolerance is undone and reported
                // with the numbers, never "made to fit" by moving the network.
                if (j.Fitting == "transition" && j.Drawn != null && writeNow)
                {
                    JObject trow;
                    JObject membersBefore = MemberEnds(j);
                    using (var checkedGroup = new CheckedWriteGroup(doc, "Horizun: drawn transition"))
                    {
                        // A FITTING JOINS CONNECTORS THAT MEET. The drawn piece leaves its two runs one piece
                        // apart (MEASURED: 243-283 mm, and horizun_create_elements refuses anything over 1 mm).
                        // Both runs are brought to the piece's midpoint - by horizun_transform_elements, inside
                        // this same group - Revit then cuts them back to its own fitting's length, and the check
                        // below decides whether that fitting sits in the drawn space. Undone together otherwise.
                        JObject meet = MeetAtDrawnMidpoint(app, title, j, request, group != null ? runKey : "");
                        if (meet.Value<bool>("ok"))
                            trow = DelegateFitting(app, title, j, false, request, group != null ? runKey : "");
                        else
                            trow = j.Row("refused", "runs_not_brought_together", meet.Value<string>("why"));
                        trow["brought_together"] = meet;
                        Attribute(trow, j, mark, true, tolerance, group != null);
                        string ts = trow.Value<string>("state");
                        if (ts == "created" || ts == "would_create")
                        {
                            JObject check = TransitionCheck(j, trow, fitTolerance);
                            if (!check.Value<bool>("fits") && declaredTypes != null && declaredTypes.Count > 0)
                            {
                                JArray tried;
                                JObject better = FitByDeclaredType(doc, j, trow, declaredTypes, fitTolerance, out tried);
                                trow["transition_types_tried"] = tried;
                                if (better != null)
                                {
                                    check = better;
                                    check["length_taken_from"] = "a declared transition type whose fitting fills the drawn piece";
                                }
                            }
                            trow["transition_check"] = check;
                            trow["members_after"] = MemberEnds(j);
                            if (check.Value<bool>("fits")) checkedGroup.Keep();
                            else
                            {
                                checkedGroup.Undo();
                                trow["state"] = "refused";
                                trow["refused"] = "does_not_fit";
                                trow["says"] = check.Value<string>("means");
                                trow["undone"] = checkedGroup.Outcome;
                            }
                        }
                        else checkedGroup.Undo();
                    }
                    trow["transition_origin"] = "drawn";
                    trow["members_before"] = membersBefore;
                    trow["members_moved_mm"] = MemberMovement(membersBefore, trow["members_after"] as JObject);
                    trow["drawn"] = j.Drawn;
                    rows.Add(trow);
                    string tstate = trow.Value<string>("state");
                    if (tstate == "created" || tstate == "would_create") delegated++;
                    else refused++;
                    continue;
                }

                // ---- elbow, tee, cross: the typed command owns this -------------
                JObject delegatedRow = DelegateFitting(app, title, j, !writeNow, request, group != null ? runKey : "");
                Attribute(delegatedRow, j, mark, writeNow, tolerance, group != null);
                // A REFUSAL THAT NAMES THE WRONG CAUSE IS WORSE THAN A SHORT ONE. The delegate can only report
                // that its batch rolled back; what is wrong is on the members, and it is measurable. See
                // WhyItWouldNotGoIn: the piece left between this corner and the fitting on its other end,
                // against the arm that fitting takes out of that same piece.
                Explain(delegatedRow, j);
                if (sectionChange != null)
                {
                    delegatedRow["action_reason"] = sectionChange;
                    // THREE KINDS OF TRANSITION, never confused: drawn (the drawing draws the piece), proposed
                    // by a rule (two collinear runs of different section meet, nothing drawn between), and
                    // inserted by Revit on its own (reported by the resize and re-read paths, never here).
                    delegatedRow["transition_origin"] = "proposed_by_rule";
                }
                rows.Add(delegatedRow);
                string state = delegatedRow.Value<string>("state");
                if (state == "created" || state == "would_create") delegated++;
                else refused++;
            }
            }
            finally
            {
                // THE REHEARSAL LEAVES NOTHING. Every fitting it placed to learn what the next
                // junction would face is undone here, on every path.
                if (group != null) group.Dispose();
            }

            // THE CONNECTION IS PART OF BUILDING. A fitting trims or extends the runs it joins, and a record
            // that still says "built at the drawing's line" makes the next update read the connection as a
            // person's move (MEASURED, campaign 7: five runs a drawn transition had extended). So after an
            // apply, every run whose line the connection changed has its provenance re-stamped to the line it
            // now has, marked as_connected with the junction - and a person's edit after that is a divergence
            // from THIS line, which the update keeps as a person's.
            // ONLY what THIS call made: a repeat must never re-stamp a person's later edit as "connected".
            var madeHere = new HashSet<string>(rows.OfType<JObject>()
                .Where(x => x.Value<string>("state") == "created" || x.Value<string>("state") == "joined")
                .Select(x => x.Value<string>("junction") ?? x.Value<string>("id") ?? ""), StringComparer.Ordinal);
            var madeJunctions = parsed.Where(x => madeHere.Contains(x.Id ?? "")).ToList();

            var result = new JObject
            {
                ["document"] = title,
                ["dry_run"] = dryRun,
                ["junctions_given"] = parsed.Count,
                ["direct_joined"] = joined,
                ["fittings_placed"] = delegated,
                ["already_connected"] = existing,
                ["rehearsal_rollback"] = group == null ? null : new JObject
                {
                    ["status"] = group.RollbackStatus,
                    ["confirmed"] = group.RollbackConfirmed,
                    ["means"] = group.RollbackConfirmed
                        ? "every fitting the sequential rehearsal placed was rolled back; the model is as it was."
                        : "the rollback did NOT report RolledBack: the model's state is UNCERTAIN - re-read it before trusting it."
                },
                ["rehearsal_scope"] = rehearsalScope,
                ["rehearsal_scope_means"] = rehearsalScope == "sequential_rolled_back"
                    ? "every junction was CARRIED OUT in order inside a transaction group and then rolled back, so " +
                      "each one met the geometry the earlier ones left - a refusal here is what the apply meets."
                    : rehearsalScope == "arguments_and_references_only"
                        ? "each junction was rehearsed ON ITS OWN: its arguments and references hold. It does NOT " +
                          "prove the network is buildable - each fitting shortens the runs it joins, and a later " +
                          "one can have no room left. Ask rehearsal=\"sequential\" for that."
                        : "the junctions were carried out.",
                ["skipped"] = skipped,
                ["refused"] = refused,
                ["junctions"] = rows,
                ["connector_tolerance_mm"] = tolerance,
                // UNDER dry_run THESE ARE "WOULD", NOT "DID". The key names do not
                // change with the mode, because a client that has to branch on the
                // shape of a reply reads two replies; so the sentence says it
                // instead, beside the numbers rather than further down.
                ["counts_mean"] = dryRun
                    ? "direct_joined and fittings_placed count what the apply WOULD do. Nothing was written."
                    : "direct_joined and fittings_placed count what the delegated commands verified in the " +
                      "model after their commits.",
                ["state"] = refused == 0 ? (dryRun ? "rehearsed" : "applied") : "partial",
                ["means"] = dryRun
                    ? "NOTHING WAS WRITTEN. Every connector was resolved from the drawing's point and every " +
                      "row was rehearsed by the command that would carry it out, so a junction refused here " +
                      "will refuse on the apply for the same reason. Send dry_run=false to carry it out."
                    : "every join was made by horizun_connect_mep and every fitting by " +
                      "horizun_create_elements, each under its own rehearsal and its own re-read of the " +
                      "model. 'joined' means those commands verified it, not that a call did not throw."
            };
            result["skipped_unresolved"] = rows.OfType<JObject>()
                .Count(x => x.Value<string>("state") == "skipped_unresolved");
            result["skipped_unresolved_means"] =
                "a junction whose members could not be resolved to elements was skipped, not refused. In a " +
                "real route that is the NORMAL case: the network reading covers the whole drawing and the " +
                "conversion builds only what nobody had to argue about, so junctions naming deferred runs " +
                "arrive here and the ones that can be made are still made.";

            if (refused > 0)
                result["partial_means"] =
                    "some junctions were made and some were not. What was joined IS in the model. A junction " +
                    "refused for an ambiguous or occupied connector is refused because joining the wrong end " +
                    "would be invisible afterwards, and re-sending it unchanged will refuse again.";
            // A REHEARSAL RETURNS HERE: the provenance write below belongs to an apply only.
            if (dryRun) return CommandResult.Ok(result);
            result["provenance_as_connected"] = RecordAsConnected(doc, madeJunctions, tolerance);
            // AND THE FITTINGS THEMSELVES. Nothing stamped one until now, so an update needing a fitting
            // released could not tell one this bridge had placed from one somebody put there and tuned -
            // and had to hold every one of them. See Core/CadFittingProvenance.cs.
            result["provenance_fittings"] = RecordFittingsMade(doc, madeJunctions, tolerance);
            return CommandResult.Ok(result);
        }

        /// <summary>
        /// Send one direct join to horizun_connect_mep, with the connectors this
        /// command resolved from the drawing's point.
        /// </summary>
        /// <summary>
        /// The confirmation token a delegated rehearsal issued, or null.
        ///
        /// Read from the structured payload rather than parsed out of prose: the
        /// token is a field, and a command that scrapes its delegate's sentences
        /// breaks the day the sentence improves.
        /// </summary>
        private static string TokenOf(CommandResult r)
        {
            try
            {
                var payload = r.Data as JObject;
                if (payload == null) return null;
                string token = payload.Value<string>("confirmation_token");
                return string.IsNullOrWhiteSpace(token) ? null : token;
            }
            catch { return null; }
        }

        /// <summary>
        /// What Revit raised while THIS junction ran, and - when it was carried out - the state
        /// re-read from the model afterwards.
        /// </summary>
        private static void Attribute(JObject row, Junction j, int mark, bool written, double tolerance, bool rehearsed)
        {
            row["action"] = j.Fitting;
            Interference watch = Interference.Current;
            if (watch != null && watch.Seen.Count > mark)
                row["revit_raised"] = RaisedRecord.Window(watch.Seen, mark);
            if (!written) return;
            row["reread"] = Reread(j, tolerance);
            if (rehearsed)
            {
                string s = row.Value<string>("state");
                if (s == "created") row["state"] = "would_create";
                else if (s == "joined") row["state"] = "would_join";
                row["rehearsed_in_sequence"] = true;
            }
        }

        /// <summary>"W x H mm" or "D mm" of a run, read from the model; null when it has neither.</summary>
        private static string SectionOf(Element e)
        {
            try
            {
                Parameter w = e.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                Parameter h = e.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                if (w != null && h != null && w.HasValue && h.HasValue && w.AsDouble() > 0)
                    return Math.Round(w.AsDouble() * 304.8, 1).ToString(CultureInfo.InvariantCulture) + "x" +
                           Math.Round(h.AsDouble() * 304.8, 1).ToString(CultureInfo.InvariantCulture) + " mm";
                foreach (BuiltInParameter d in new[] { BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM })
                {
                    Parameter p = e.get_Parameter(d);
                    if (p != null && p.HasValue && p.AsDouble() > 0)
                        return "D" + Math.Round(p.AsDouble() * 304.8, 1).ToString(CultureInfo.InvariantCulture) + " mm";
                }
            }
            catch { }
            return null;
        }

        private static bool IsFitting(Element e)
        {
            var fi = e as FamilyInstance;
            if (fi == null || fi.Category == null) return false;
            long cat = Rid.Value(fi.Category.Id);
            return cat == (long)BuiltInCategory.OST_DuctFitting || cat == (long)BuiltInCategory.OST_PipeFitting ||
                   cat == (long)BuiltInCategory.OST_ConduitFitting || cat == (long)BuiltInCategory.OST_CableTrayFitting;
        }

        /// <summary>The physical neighbours of a connector: owners of the end connectors it is joined to.</summary>
        private static List<Element> Neighbours(Connector c)
        {
            var list = new List<Element>();
            try
            {
                foreach (Connector other in c.AllRefs)
                {
                    if (other == null || other.Owner == null || other.Owner.Id == c.Owner.Id) continue;
                    if (other.ConnectorType != ConnectorType.End && other.ConnectorType != ConnectorType.Curve) continue;
                    if (!list.Any(x => x.Id == other.Owner.Id)) list.Add(other.Owner);
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// Is this junction ALREADY made? Null when nothing is there yet. Otherwise a row whose state
        /// is already_connected (every member's connector at the point joins the other member directly,
        /// or all of them join ONE fitting of the proposed kind), existing_fitting_differs, or
        /// occupied_by_other.
        /// </summary>
        /// <summary>Every physical neighbour of an element, over ALL its connectors - a fitting trims the
        /// run it joins, so the joining end is no longer where the drawing put the junction.</summary>
        private static List<Element> AllNeighbours(Element e)
        {
            var list = new List<Element>();
            ConnectorManager m = MepFacts.ManagerOf(e);
            if (m == null) return list;
            foreach (Connector c in MepFacts.Ordered(m))
                foreach (Element n in Neighbours(c))
                    if (!list.Any(x => x.Id == n.Id)) list.Add(n);
            return list;
        }

        /// <summary>
        /// The junction's members and the fittings around them, as the pure evidence graph
        /// (Core/CadJunctionEvidence): every end connector with its position and what it is joined to,
        /// explored through fittings only, one step past the evidence bound so "beyond" can be said.
        /// </summary>
        private static JunctionGraph GraphOf(Junction j)
        {
            var g = new JunctionGraph();
            var queue = new Queue<Tuple<Element, int>>();
            foreach (Element e in j.Elements) queue.Enqueue(Tuple.Create(e, 0));
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                Element e = item.Item1;
                long id = Rid.Value(e.Id);
                if (g.Nodes.ContainsKey(id)) continue;
                bool fitting = IsFitting(e);
                JunctionGraph.Node node = g.Add(id, fitting, fitting ? PartOf((FamilyInstance)e) : null);
                ConnectorManager m = MepFacts.ManagerOf(e);
                if (m == null) continue;
                int k = 0;
                foreach (Connector c in MepFacts.Ordered(m))
                {
                    if (c.ConnectorType != ConnectorType.End) { k++; continue; }
                    var end = new JunctionGraph.End { Index = k++, X = c.Origin.X * 304.8, Y = c.Origin.Y * 304.8 };
                    foreach (Element n in Neighbours(c))
                    {
                        end.JoinedTo.Add(Rid.Value(n.Id));
                        if (IsFitting(n) && item.Item2 <= CadJunctionEvidence.MaxChain) queue.Enqueue(Tuple.Create(n, item.Item2 + 1));
                    }
                    node.Ends.Add(end);
                }
            }
            return g;
        }

        private static JObject Evidence(Junction j, double tolerance) =>
            CadJunctionEvidence.Decide(GraphOf(j), j.Elements.Select(e => Rid.Value(e.Id)).ToList(), j.At.X, j.At.Y,
                                       j.Fitting, tolerance);

        private static string PartOf(FamilyInstance f)
        {
            try { return ((PartType)(f.Symbol.Family.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)?.AsInteger() ?? -1)).ToString(); }
            catch { return ""; }
        }

        private static JObject Existing(Junction j, double tolerance)
        {
            if (j.Elements.Count < 2) return null;
            // A FITTING THAT ALREADY JOINS EVERY MEMBER - found through the members' connectors, not by
            // where their ends are. MEASURED: after an elbow is placed the runs' ends move by its radius,
            // so looking for a connector at the drawing's point finds nothing and a repeat rehearsed a
            // second elbow.
            if (j.Fitting != "direct")
            {
                // THE EVIDENCE IS AT THIS JUNCTION'S END of each member, not anywhere on the runs
                // (Core/CadJunctionEvidence). Anything it cannot show is said, not guessed.
                JObject ev = Evidence(j, tolerance);
                string st = ev.Value<string>("state");
                if (st != "not_connected")
                {
                    if (st == "existing_fitting_differs")
                        ev["says"] = ev.Value<string>("says") + ". It is left as it is and reported - replacing it is a decision, not a repair.";
                    if (st == "already_connected") ev["says"] = ev.Value<string>("says") + "; nothing was sent.";
                    return ev;
                }
            }
            else if (AllNeighbours(j.Elements[0]).Any(n => n.Id == j.Elements[1].Id))
                return new JObject
                {
                    ["state"] = "already_connected",
                    ["says"] = "the two runs are joined to each other in the model already; nothing was sent."
                };
            var picks = j.Elements.Select(e => MepConnect.Nearest(e, j.At, tolerance)).ToList();
            if (picks.Any(p => !p.Found || p.Connector == null)) return null;
            var conns = picks.Select(p => p.Connector).ToList();
            if (conns.All(c => !c.IsConnected)) return null;
            var detail = new JArray(picks.Select(p => (JToken)new JObject
            {
                ["element_id"] = Rid.Value(p.Connector.Owner.Id), ["connector"] = p.ConnectorId,
                ["connected"] = p.Connector.IsConnected,
                ["to"] = new JArray(Neighbours(p.Connector).Select(x => (JToken)Rid.Value(x.Id)))
            }));
            if (j.Fitting == "direct")
            {
                bool direct = false;
                try { direct = conns[0].IsConnectedTo(conns[1]); } catch { }
                return new JObject
                {
                    ["state"] = direct ? "already_connected" : "occupied_by_other",
                    ["connectors"] = detail,
                    ["says"] = direct ? "the two ends are joined to each other in the model already; nothing was sent."
                                      : "a connector at this point is joined to something else; joining it here would take it away."
                };
            }
            List<Element> common = null;
            foreach (Connector c in conns)
            {
                var fits = Neighbours(c).Where(IsFitting).ToList();
                common = common == null ? fits : common.Where(x => fits.Any(f => f.Id == x.Id)).ToList();
            }
            if (conns.All(c => c.IsConnected) && common != null && common.Count == 1)
            {
                var fitting = (FamilyInstance)common[0];
                string part = "";
                try { part = ((PartType)(fitting.Symbol.Family.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)?.AsInteger() ?? -1)).ToString(); } catch { }
                bool same = string.Equals(part, j.Fitting, StringComparison.OrdinalIgnoreCase);
                return new JObject
                {
                    ["state"] = same ? "already_connected" : "existing_fitting_differs",
                    ["fitting_id"] = Rid.Value(fitting.Id),
                    ["fitting_part_type"] = part,
                    ["connectors"] = detail,
                    ["says"] = same
                        ? "one " + part.ToLowerInvariant() + " already joins every member at this point; nothing was sent."
                        : "a " + part + " joins these members where the drawing proposes a " + j.Fitting +
                          ". It is left as it is and reported - replacing it is a decision, not a repair."
                };
            }
            return new JObject
            {
                ["state"] = "occupied_by_other",
                ["connectors"] = detail,
                ["says"] = "at least one member's connector at this point is already joined to something that is not " +
                           "one shared fitting for this junction. Nothing was sent; joining it would take it away."
            };
        }

        /// <summary>The members' connectors at the junction, re-read after the operation.</summary>
        private static JObject Reread(Junction j, double tolerance)
        {
            // Through the members' connectors: which fitting joins them all (or, for a direct join,
            // whether they touch), not where their ends now are.
            JObject ev = j.Fitting == "direct" ? null : Evidence(j, tolerance);
            long? evFitting = ev?.Value<long?>("fitting_id");
            FamilyInstance shared = ev != null && ev.Value<string>("state") == "already_connected" && evFitting.HasValue
                ? j.Elements[0].Document.GetElement(Rid.Make(evFitting.Value)) as FamilyInstance : null;
            bool joined = j.Fitting == "direct"
                ? j.Elements.Count == 2 && AllNeighbours(j.Elements[0]).Any(n => n.Id == j.Elements[1].Id)
                : shared != null;
            var members = new JArray(j.Elements.Select(e => (JToken)new JObject
            {
                ["element_id"] = Rid.Value(e.Id),
                ["joined_to"] = new JArray(AllNeighbours(e).Select(n => (JToken)Rid.Value(n.Id)))
            }));
            return new JObject
            {
                ["members"] = members,
                ["fitting_id"] = shared == null ? null : (JToken)Rid.Value(shared.Id),
                ["fitting_part_type"] = shared == null ? null : PartOf(shared),
                ["all_connected"] = joined,
                ["evidence"] = ev
            };
        }

        /// <summary>
        /// Stamp every fitting this call placed with the junction it serves, the members it joins and a
        /// print of what it is - so a later revision can say whether it is ours, ours-and-since-touched,
        /// or of unknown origin, instead of holding all three alike.
        ///
        /// The fitting is found the same way everything else here is: from the members' connectors at the
        /// drawn point, re-read from the model after the commit. A junction whose members turn out not to
        /// be joined to anything is not stamped and says so.
        /// </summary>
        private static JArray RecordFittingsMade(Document doc, List<Junction> junctions, double tolerance)
        {
            var rows = new JArray();
            if (junctions == null || junctions.Count == 0) return rows;
            using (var t = new Transaction(doc, "Horizun: record the fittings this call placed"))
            {
                if (t.Start() != TransactionStatus.Started) return rows;
                foreach (Junction j in junctions)
                {
                    if (j.Unresolved != null || j.Elements.Count < 2) continue;
                    if (j.Fitting == "none" || j.Fitting == "direct") continue;
                    Element fitting = null;
                    var memberIds = new List<long>();
                    foreach (Element e in j.Elements) memberIds.Add(Rid.Value(e.Id));
                    foreach (Element e in j.Elements)
                    {
                        MepConnectorPick pick = MepConnect.Nearest(e, j.At, Math.Max(tolerance, 50.0));
                        if (!pick.Found || pick.Connector == null) continue;
                        try
                        {
                            foreach (Connector r in pick.Connector.AllRefs)
                            {
                                if (r?.Owner == null || memberIds.Contains(Rid.Value(r.Owner.Id))) continue;
                                fitting = r.Owner;
                                break;
                            }
                        }
                        catch { }
                        if (fitting != null) break;
                    }
                    if (fitting == null)
                    {
                        rows.Add(new JObject
                        {
                            ["junction"] = j.Id, ["stamped"] = false,
                            ["means"] = "no fitting was found joined to these members at the drawn point, so " +
                                        "there was nothing to stamp. What the junction row says stands."
                        });
                        continue;
                    }
                    string problem;
                    CadProvenance from = null;
                    try { from = CadProvenanceStore.Read(j.Elements[0], out problem); } catch { }
                    bool ok = CadFittingProvenance.Stamp(doc, fitting, j.Id, j.Fitting, memberIds, from, out problem);
                    rows.Add(new JObject
                    {
                        ["junction"] = j.Id, ["element_id"] = Rid.Value(fitting.Id), ["kind"] = j.Fitting,
                        ["members"] = new JArray(memberIds.Select(x => (JToken)x)),
                        ["stamped"] = ok, ["error"] = ok ? null : problem
                    });
                }
                Guard.Commit(t, "record the fittings this call placed");
            }
            return rows;
        }

        private static JArray RecordAsConnected(Document doc, List<Junction> junctions, double tolerance)
        {
            var rows = new JArray();
            var members = new Dictionary<long, string>();
            foreach (Junction j in junctions)
            {
                if (j.Unresolved != null || j.Elements.Count < 2) continue;
                JObject reread;
                try { reread = Reread(j, tolerance); } catch { continue; }
                if (reread.Value<bool?>("all_connected") != true) continue;
                foreach (Element e in j.Elements)
                    if (!members.ContainsKey(Rid.Value(e.Id))) members[Rid.Value(e.Id)] = j.Id ?? "junction";
            }
            if (members.Count == 0) return rows;
            using (var t = new Transaction(doc, "Horizun: record as-connected geometry"))
            {
                if (t.Start() != TransactionStatus.Started) return rows;
                foreach (var kv in members)
                {
                    Element e = doc.GetElement(Rid.Make(kv.Key));
                    var lc = e?.Location as LocationCurve;
                    if (lc?.Curve == null) continue;
                    string problem;
                    CadProvenance p = CadProvenanceStore.Read(e, out problem);
                    if (p == null) continue;
                    var now = new List<CadPoint>
                    {
                        new CadPoint(lc.Curve.GetEndPoint(0).X * 304.8, lc.Curve.GetEndPoint(0).Y * 304.8, lc.Curve.GetEndPoint(0).Z * 304.8),
                        new CadPoint(lc.Curve.GetEndPoint(1).X * 304.8, lc.Curve.GetEndPoint(1).Y * 304.8, lc.Curve.GetEndPoint(1).Z * 304.8)
                    };
                    string was = p.BuiltGeometry;
                    string encoded = CadUpdateRules.Encode(now);
                    if (string.Equals(was, encoded, StringComparison.Ordinal)) continue;
                    p.BuiltGeometry = encoded;
                    string mark = "as_connected:" + kv.Value;
                    if (p.SourceEntities == null || !p.SourceEntities.Contains(mark))
                        p.SourceEntities = CadProvenanceStore.Capped(string.IsNullOrEmpty(p.SourceEntities) ? mark : p.SourceEntities + ";" + mark);
                    p.WrittenUtc = DateTime.UtcNow.ToString("o");
                    string why;
                    bool ok = CadProvenanceStore.Write(e, p, out why);
                    rows.Add(new JObject
                    {
                        ["element_id"] = kv.Key, ["junction"] = kv.Value, ["written"] = ok, ["was_built_at_mm"] = was,
                        ["now_at_mm"] = encoded, ["error"] = ok ? null : why
                    });
                }
                Guard.Commit(t, "record as-connected geometry");
            }
            return rows;
        }

        /// <summary>
        /// Bring each member's end nearest the drawn piece to the piece's midpoint, through
        /// horizun_transform_elements set_curve (rehearsed, then applied with its token). Z is each run's own.
        /// </summary>
        private JObject MeetAtDrawnMidpoint(UIApplication app, string title, Junction j, JObject request, string keySuffix)
        {
            var o = new JObject { ["ok"] = false };
            var f = j.Drawn["from_mm"] as JArray; var t = j.Drawn["to_mm"] as JArray;
            if (f == null || t == null || j.Elements.Count != 2) { o["why"] = "the drawn piece or its two runs are missing"; return o; }
            double mx = (f[0].Value<double>() + t[0].Value<double>()) / 2, my = (f[1].Value<double>() + t[1].Value<double>()) / 2;
            ICommand child = _resolve == null ? null : _resolve("horizun_transform_elements");
            if (child == null) { o["why"] = "horizun_transform_elements could not be resolved"; return o; }
            var moved = new JArray();
            foreach (Element e in j.Elements)
            {
                var lc = e.Location as LocationCurve;
                if (lc?.Curve == null) { o["why"] = "run " + Rid.Value(e.Id) + " has no location line"; return o; }
                XYZ p0 = lc.Curve.GetEndPoint(0), p1 = lc.Curve.GetEndPoint(1);
                double d0 = Math.Sqrt(Math.Pow(p0.X * 304.8 - mx, 2) + Math.Pow(p0.Y * 304.8 - my, 2));
                double d1 = Math.Sqrt(Math.Pow(p1.X * 304.8 - mx, 2) + Math.Pow(p1.Y * 304.8 - my, 2));
                XYZ keep = d0 <= d1 ? p1 : p0, near = d0 <= d1 ? p0 : p1;

                // REPLACING THE CURVE RE-EVALUATES EVERY CONNECTION THE RUN HAS, including the one at the end
                // that is not moving. Where that far end is already in a network, Revit asks to disconnect it -
                // a modal question with nobody at the keyboard - and the write rolls back. Moving the free
                // connector's own origin moves this end and nothing else, so it is tried FIRST in exactly that
                // case and nowhere else: the route that has always worked here is still the route everywhere else.
                string whyNotByConnector;
                JObject byConnector = MoveFreeEndOnly(e, mx, my, out whyNotByConnector);
                if (byConnector != null) { moved.Add(byConnector); continue; }

                var start = new JArray(Math.Round(keep.X * 304.8, 4), Math.Round(keep.Y * 304.8, 4), Math.Round(keep.Z * 304.8, 4));
                var end = new JArray(Math.Round(mx, 4), Math.Round(my, 4), Math.Round(near.Z * 304.8, 4));
                var args = new JObject
                {
                    ["target_document"] = title, ["units"] = "mm", ["dry_run"] = true,
                    ["operations"] = new JArray(new JObject
                    {
                        ["operation"] = "set_curve", ["element_ids"] = new JArray(Rid.Value(e.Id)), ["start"] = start, ["end"] = end
                    })
                };
                string idem = request.Value<string>("idempotency_key");
                if (!string.IsNullOrWhiteSpace(idem)) args["idempotency_key"] = idem + "-" + (j.Id ?? "j") + "-meet-" + Rid.Value(e.Id) + keySuffix;
                CommandResult dry = child.Execute(app, args.ToString(Formatting.None));
                string token = dry.Success ? TokenOf(dry) : null;
                if (!dry.Success || token == null)
                { o["why"] = "set_curve on run " + Rid.Value(e.Id) + " did not rehearse: " + (dry.Error ?? "no token"); return o; }
                args["dry_run"] = false;
                args["confirmation_token"] = token;
                CommandResult done = child.Execute(app, args.ToString(Formatting.None));
                if (!done.Success)
                {
                    o["why"] = "set_curve on run " + Rid.Value(e.Id) + " failed: " + done.Error;
                    o["the_connector_route_was_not_taken_because"] = whyNotByConnector;
                    return o;
                }
                moved.Add(new JObject
                {
                    ["element_id"] = Rid.Value(e.Id),
                    ["end_moved_mm"] = Math.Round(Math.Min(d0, d1), 1),
                    ["to_mm"] = end,
                    ["moved_by"] = "the run's whole curve, through horizun_transform_elements",
                    ["why_not_the_connector"] = whyNotByConnector
                });
            }
            o["ok"] = true;
            o["runs"] = moved;
            o["midpoint_mm"] = new JArray(Math.Round(mx, 1), Math.Round(my, 1));
            o["means"] = "both runs were brought to the drawn piece's midpoint so the fitting has connectors that meet; " +
                         "Revit then cut them back to its fitting's own length";
            return o;
        }

        /// <summary>
        /// A built transition against the drawn one: sizes at both ends, axis, and where its two ends sit
        /// relative to the drawn piece's ends. "fits" only when every end is within the tolerance.
        /// </summary>
        private static JObject TransitionCheck(Junction j, JObject row, double fitTolerance)
        {
            var o = new JObject();
            long? fid = (row["reread"] as JObject)?.Value<long?>("fitting_id");
            var fitting = fid.HasValue && j.Elements.Count > 0 ? j.Elements[0].Document.GetElement(Rid.Make(fid.Value)) as FamilyInstance : null;
            var drawnFrom = j.Drawn["from_mm"] as JArray; var drawnTo = j.Drawn["to_mm"] as JArray;
            if (fitting?.MEPModel?.ConnectorManager == null || drawnFrom == null || drawnTo == null)
            {
                o["fits"] = false;
                o["means"] = "the fitting joining both members could not be re-read, so the fit was not measured and it is not kept";
                return o;
            }
            var f0 = new XYZ(drawnFrom[0].Value<double>() / 304.8, drawnFrom[1].Value<double>() / 304.8, 0);
            var f1 = new XYZ(drawnTo[0].Value<double>() / 304.8, drawnTo[1].Value<double>() / 304.8, 0);
            XYZ axis = (f1 - f0).GetLength() > 1e-9 ? (f1 - f0).Normalize() : XYZ.BasisX;
            var ends = new JArray();
            double worst = 0;
            bool collinear = true;
            var sizes = new List<string>();
            foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors)
            {
                XYZ p = new XYZ(c.Origin.X, c.Origin.Y, 0);
                double d = Math.Min(p.DistanceTo(f0), p.DistanceTo(f1)) * 304.8;
                worst = Math.Max(worst, d);
                XYZ dir = c.CoordinateSystem.BasisZ;
                if (Math.Abs(Math.Abs(dir.X * axis.X + dir.Y * axis.Y) - 1) > 0.01) collinear = false;
                string size = c.Shape == ConnectorProfileType.Round
                    ? "D" + Math.Round(c.Radius * 2 * 304.8, 1).ToString(CultureInfo.InvariantCulture)
                    : Math.Round(c.Width * 304.8, 1).ToString(CultureInfo.InvariantCulture) + "x" +
                      Math.Round(c.Height * 304.8, 1).ToString(CultureInfo.InvariantCulture);
                sizes.Add(size);
                ends.Add(new JObject
                {
                    ["at_mm"] = new JArray(Math.Round(c.Origin.X * 304.8, 1), Math.Round(c.Origin.Y * 304.8, 1), Math.Round(c.Origin.Z * 304.8, 1)),
                    ["size_mm"] = size, ["from_drawn_end_mm"] = Math.Round(d, 1)
                });
            }
            var memberSizes = j.Elements.Select(e => (SectionOf(e) ?? "").Replace(" mm", "")).OrderBy(x => x).ToList();
            bool sizesMatch = sizes.OrderBy(x => x).SequenceEqual(memberSizes);
            double fittingLength = 0;
            var cs = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>().ToList();
            if (cs.Count == 2) fittingLength = cs[0].Origin.DistanceTo(cs[1].Origin) * 304.8;
            bool fits = sizesMatch && collinear && worst <= fitTolerance;
            o["fitting_id"] = fid.Value;
            o["fitting"] = fitting.Symbol?.FamilyName + ": " + fitting.Name;
            o["ends"] = ends;
            o["sizes_match_members"] = sizesMatch;
            o["member_sizes_mm"] = new JArray(memberSizes);
            o["collinear_with_drawn_axis"] = collinear;
            o["drawn_length_mm"] = j.Drawn["length_mm"];
            o["fitting_length_mm"] = Math.Round(fittingLength, 1);
            o["worst_end_offset_mm"] = Math.Round(worst, 1);
            o["tolerance_mm"] = fitTolerance;
            o["fits"] = fits;
            o["means"] = fits
                ? "the transition sits where the drawing draws it (ends within " + fitTolerance + " mm), with the members' sizes at its ends"
                : "the transition does not fit the drawn space: " +
                  (!sizesMatch ? "its end sizes are not the members' sizes; " : "") +
                  (!collinear ? "it is not on the drawn axis; " : "") +
                  (worst > fitTolerance ? "Revit's " + Math.Round(fittingLength, 1) + " mm fitting puts an end " + Math.Round(worst, 1) +
                                          " mm from where the drawing ends the " + j.Drawn["length_mm"] + " mm piece (tolerance " + fitTolerance + " mm); " : "") +
                  "it was undone rather than moving the network to make it fit";
            return o;
        }

        /// <summary>
        /// From the fitting's own centre to the centre of the drawn piece, in feet: what would put a fitting of
        /// this length where the drawing draws it. Null when the fitting does not have two readable connectors.
        /// </summary>
        private static XYZ CentringOffset(FamilyInstance fitting, Junction j)
        {
            var cs = fitting?.MEPModel?.ConnectorManager?.Connectors?.Cast<Connector>().ToList();
            var from = j.Drawn?["from_mm"] as JArray;
            var to = j.Drawn?["to_mm"] as JArray;
            if (cs == null || cs.Count != 2 || from == null || to == null) return null;
            XYZ mid = (cs[0].Origin + cs[1].Origin) / 2.0;
            var drawn = new XYZ((from[0].Value<double>() + to[0].Value<double>()) / 2.0 / 304.8,
                                (from[1].Value<double>() + to[1].Value<double>()) / 2.0 / 304.8,
                                mid.Z);
            return drawn - mid;
        }

        /// <summary>Where each member's curve ends are now, so what a fitting did to them can be MEASURED, not assumed.</summary>
        private static JObject MemberEnds(Junction j)
        {
            var o = new JObject();
            foreach (Element e in j.Elements)
            {
                var lc = e?.Location as LocationCurve;
                if (lc?.Curve == null) continue;
                XYZ a = lc.Curve.GetEndPoint(0), b = lc.Curve.GetEndPoint(1);
                o[Rid.Value(e.Id).ToString(CultureInfo.InvariantCulture)] = new JArray(
                    new JArray(Math.Round(a.X * 304.8, 2), Math.Round(a.Y * 304.8, 2), Math.Round(a.Z * 304.8, 2)),
                    new JArray(Math.Round(b.X * 304.8, 2), Math.Round(b.Y * 304.8, 2), Math.Round(b.Z * 304.8, 2)));
            }
            return o;
        }

        /// <summary>
        /// WHY a fitting Revit refused would not go in, MEASURED on the members themselves.
        ///
        /// Revit's words for this case are "the duct/pipe has been modified to be in the opposite direction"
        /// and "the family is connected in a network and can no longer keep the connectivity". Both are true
        /// and neither is a diagnosis: they say what Revit did with the request, not what is wrong with the
        /// drawing. What was reported instead - "usually one bad element poisoning a batch, retry in smaller
        /// batches" - is a guess about a batch, and it sent the reader to look for the wrong thing.
        ///
        /// The cause is measurable. A turning fitting takes a length of STRAIGHT duct out of each member it
        /// holds - its arm - and that arm is the distance from the connector it holds to where its two axes
        /// cross. MEASURED across two models and fifteen installed elbows: 228.6 mm out of a 152.4 mm duct at
        /// a square corner, 126.2 mm out of a 203.2 mm duct at a 45 degree one, the same number every time.
        /// When the piece the drawing left between two corners is shorter than the two arms its corners want,
        /// whichever corner is placed first takes its arm and the second one cannot: the end it would pull
        /// back is already past the other end, which is exactly the duct Revit says has been reversed.
        ///
        /// So this reads what is LEFT of each member between this junction and its far end, and what the
        /// fitting already holding that far end took out of this same piece, and reports both. It calls a
        /// member short only when the piece has less left than that measured arm. It never decides the
        /// junction and never invents the fitting's needs: where there is no fitting on the far end, or its
        /// connectors are collinear and have no corner, the arm is reported as null and no verdict is drawn.
        /// </summary>
        private static JObject WhyItWouldNotGoIn(Junction j)
        {
            var members = new JArray();
            string shortest = null; double worstShort = 0, leftOnWorst = 0, armOnWorst = 0; string whatHeldIt = null;
            foreach (Element e in j.Elements)
            {
                if (e == null) continue;
                List<Connector> all = MepConnect.ConnectorsOf(e);
                Connector near = null, far = null;
                double dn = double.MaxValue, df = -1;
                foreach (Connector c in all)
                {
                    XYZ org; try { org = c.Origin; } catch { continue; }
                    if (org == null) continue;
                    double d = Math.Sqrt(Math.Pow(CadUnits.FeetToMm(org.X) - j.At.X, 2) +
                                         Math.Pow(CadUnits.FeetToMm(org.Y) - j.At.Y, 2));
                    if (d < dn) { dn = d; near = c; }
                    if (d > df) { df = d; far = c; }
                }
                if (near == null || far == null || ReferenceEquals(near, far)) continue;

                var row = new JObject
                {
                    ["element"] = Rid.Value(e.Id),
                    ["at_this_junction_mm"] = Math.Round(dn, 1),
                    ["left_to_its_far_end_mm"] = Math.Round(df, 1)
                };
                try { row["width_mm"] = Math.Round(CadUnits.FeetToMm(near.Width), 1); } catch { }

                Element holder = null; Connector holderEnd = null;
                try
                {
                    foreach (Connector other in far.AllRefs)
                    {
                        if (other == null || other.Owner == null || other.Owner.Id == e.Id) continue;
                        if (other.ConnectorType != ConnectorType.End) continue;
                        holder = other.Owner; holderEnd = other; break;
                    }
                }
                catch { }

                var farEnd = new JObject { ["connected"] = holder != null };
                if (holder != null)
                {
                    farEnd["held_by"] = Rid.Value(holder.Id);
                    farEnd["what"] = NameOfType(holder);
                    double turnThere;
                    double? arm = ArmOfMm(holder, holderEnd, out turnThere);
                    double turnHere = TurnAtMm(j);
                    farEnd["arm_it_takes_mm"] = arm.HasValue ? (JToken)Math.Round(arm.Value, 1) : JValue.CreateNull();
                    farEnd["arm_measured_how"] = arm.HasValue
                        ? "the distance from the connector it holds to where that fitting's two axes cross"
                        : "not measurable: this fitting's connectors are collinear, so it has no corner and takes no arm";
                    // THE SCOPE OF THE NUMBER, beside the number. This arm belongs to THIS family, THIS
                    // type, at THIS turn: nothing here is a law about fittings in general, and a reader
                    // must not carry it to a corner it never described. Where the turn asked for differs
                    // from the turn measured, the comparison is indicative and says so.
                    farEnd["measured_on"] = new JObject
                    {
                        ["fitting"] = NameOfType(holder),
                        ["turn_degrees"] = double.IsNaN(turnThere) ? (JToken)JValue.CreateNull() : Math.Round(turnThere, 1),
                        ["member_width_mm"] = row["width_mm"],
                        ["means"] = "the arm of a turning fitting depends on its family, its type, the sizes " +
                                    "it joins and the angle it turns through. This one was measured on the " +
                                    "fitting standing at the other end of this very piece; it is not a law " +
                                    "about fittings, and it describes no other family or angle."
                    };
                    if (!double.IsNaN(turnHere))
                    {
                        farEnd["turn_at_this_junction_degrees"] = Math.Round(turnHere, 1);
                        bool same = !double.IsNaN(turnThere) && Math.Abs(turnHere - turnThere) <= 2.0;
                        farEnd["comparison"] = same ? "like_for_like" : "indicative_only";
                        if (!same)
                            farEnd["comparison_means"] =
                                "the drawing turns through a different angle here than the fitting that was " +
                                "measured, so its arm is not the arm this junction needs. What is left is a " +
                                "fact; the shortfall computed from it is an indication, not a measurement.";
                    }
                    if (arm.HasValue)
                    {
                        row["short_by_mm"] = Math.Round(Math.Max(0, arm.Value - df), 1);
                        row["has_room_for_the_same_fitting"] = df >= arm.Value;
                        if (df < arm.Value && arm.Value - df > worstShort)
                        {
                            worstShort = arm.Value - df; shortest = Rid.Value(e.Id).ToString(CultureInfo.InvariantCulture);
                            leftOnWorst = df; armOnWorst = arm.Value; whatHeldIt = NameOfType(holder);
                        }
                    }
                }
                row["far_end"] = farEnd;
                members.Add(row);
            }

            var o = new JObject { ["members"] = members };
            if (shortest != null)
            {
                o["too_short"] = shortest;
                o["reading"] = "member " + shortest + " has " + Math.Round(leftOnWorst, 1).ToString(CultureInfo.InvariantCulture) +
                               " mm left between this junction and the fitting on its other end, and that fitting (" +
                               whatHeldIt + ") takes " + Math.Round(armOnWorst, 1).ToString(CultureInfo.InvariantCulture) +
                               " mm out of this same piece. A second fitting of that size does not fit in what is left: " +
                               "it is short by " + Math.Round(worstShort, 1).ToString(CultureInfo.InvariantCulture) + " mm.";
                o["what_would_make_it_possible"] = "a fitting whose arm is at most " +
                               Math.Round(leftOnWorst, 1).ToString(CultureInfo.InvariantCulture) +
                               " mm at this corner, or a longer piece between the two corners in the drawing. " +
                               "Which fitting is acceptable is a project decision and this command does not make it.";
                // WHAT THIS VERDICT IS NOT. The arm it compared against was measured on one fitting in
                // this model, at that fitting's own turn. Another family, another type, or a shorter-armed
                // elbow may well fit where this one does not, and nothing here has measured that.
                o["scope"] = "measured on the fitting standing at the other end of this same piece, at its " +
                             "own angle. It is not a law about fittings: a different family, a different " +
                             "type or a different turn takes a different arm, and declaring one of those in " +
                             "the connect call is how to find out. This refusal says THIS fitting does not " +
                             "fit here - never that no fitting does.";
            }
            else
                o["reading"] = "every member has room for a fitting the size of the one already on its far end, " +
                               "so the refusal is not this. Revit's own words are in revit_said.";
            return o;
        }

        /// <summary>
        /// Put the measured reading on a refused row, and let it re-label the refusal ONLY when the
        /// measurement demonstrates the cause. The delegate's own code and error are kept beside it:
        /// nothing is paraphrased away, and a caller keying on the old code still finds it.
        /// </summary>
        private static void Explain(JObject row, Junction j)
        {
            if (row == null || row.Value<string>("state") != "refused") return;
            JObject why;
            try { why = WhyItWouldNotGoIn(j); }
            catch (Exception ex) { row["why_refused"] = new JObject { ["not_measured"] = ex.Message }; return; }
            row["why_refused"] = why;
            if (why.Value<string>("too_short") == null) return;
            row["delegated_refusal"] = row.Value<string>("refused");
            row["delegated_said"] = row.Value<string>("says");
            row["refused"] = "member_too_short_for_the_fitting";
            row["says"] = why.Value<string>("reading") + " " + why.Value<string>("what_would_make_it_possible");
        }

        private static string NameOfType(Element e)
        {
            if (e == null) return null;
            var sym = e.Document.GetElement(e.GetTypeId()) as ElementType;
            if (sym == null) return e.Name;
            return sym.FamilyName + ": " + sym.Name;
        }

        /// <summary>
        /// The straight length a turning fitting takes out of the member on <paramref name="held"/>: the
        /// distance from that connector to where its axis crosses the axis of the fitting's other end.
        /// Null when the two axes are parallel - a transition, which has no corner and takes no arm.
        /// </summary>
        private static double? ArmOfMm(Element fitting, Connector held) { double turn; return ArmOfMm(fitting, held, out turn); }

        /// <summary>
        /// The arm, and the TURN it was measured at. Both travel together on purpose: the arm of a
        /// turning fitting depends on the angle it turns through, and a number quoted without its angle
        /// invites the reader to apply it to a corner it never described.
        /// </summary>
        private static double? ArmOfMm(Element fitting, Connector held, out double turnDegrees)
        {
            turnDegrees = double.NaN;
            if (fitting == null || held == null) return null;
            XYZ a0, da;
            try { a0 = held.Origin; da = held.CoordinateSystem.BasisZ; } catch { return null; }
            if (a0 == null || da == null) return null;
            double best = 0, bestTurn = double.NaN; bool found = false;
            foreach (Connector other in MepConnect.ConnectorsOf(fitting))
            {
                if (other == null || other.Id == held.Id) continue;
                XYZ b0, db;
                try { b0 = other.Origin; db = other.CoordinateSystem.BasisZ; } catch { continue; }
                if (b0 == null || db == null) continue;
                XYZ cross = da.CrossProduct(db);
                double denom = cross.GetLength();
                if (denom < 1e-9) continue;                       // parallel: no corner
                double t = (b0 - a0).CrossProduct(db).DotProduct(cross) / (denom * denom);
                double mm = Math.Abs(t) * 304.8;                  // da is a unit vector, so |t| is in feet
                double dot = Math.Max(-1.0, Math.Min(1.0, da.Normalize().DotProduct(db.Normalize())));
                double turn = 180.0 - Math.Acos(dot) * 180.0 / Math.PI;
                if (!found || mm > best) { best = mm; bestTurn = turn; found = true; }
            }
            turnDegrees = bestTurn;
            return found ? (double?)best : null;
        }

        /// <summary>The angle the drawing turns through at this junction, from the two members' lines.</summary>
        private static double TurnAtMm(Junction j)
        {
            try
            {
                if (j.Elements.Count != 2) return double.NaN;
                var dirs = new List<XYZ>();
                foreach (Element e in j.Elements)
                {
                    var lc = e.Location as LocationCurve;
                    if (lc?.Curve == null) return double.NaN;
                    XYZ a = lc.Curve.GetEndPoint(0), b = lc.Curve.GetEndPoint(1);
                    double da = Math.Sqrt(Math.Pow(CadUnits.FeetToMm(a.X) - j.At.X, 2) + Math.Pow(CadUnits.FeetToMm(a.Y) - j.At.Y, 2));
                    double db = Math.Sqrt(Math.Pow(CadUnits.FeetToMm(b.X) - j.At.X, 2) + Math.Pow(CadUnits.FeetToMm(b.Y) - j.At.Y, 2));
                    XYZ near = da <= db ? a : b, far = da <= db ? b : a;
                    XYZ v = (far - near);
                    if (v.GetLength() < 1e-9) return double.NaN;
                    dirs.Add(v.Normalize());
                }
                double dot = Math.Max(-1.0, Math.Min(1.0, dirs[0].DotProduct(dirs[1])));
                return 180.0 - Math.Acos(dot) * 180.0 / Math.PI;
            }
            catch { return double.NaN; }
        }

        /// <summary>
        /// Move ONE end of a run to a point WITHOUT rewriting its curve.
        ///
        /// Setting LocationCurve.Curve replaces the whole line, and Revit re-evaluates EVERY connection the
        /// run has - including the one at the end that is not moving. On a run whose far end is already in a
        /// network that is "the family is connected in a network and can no longer keep the connectivity",
        /// a modal question nobody is there to answer, and the write rolls back. MEASURED in campaign 8: the
        /// third drawn transition of M102 was refused for exactly this, on a 6000 mm run whose near end had
        /// to move 106.6 mm and whose far end was held by a transition.
        ///
        /// A connector's own origin moves that end and nothing else. This takes that route only where the
        /// curve route cannot work - the far end connected - and verifies all three things afterwards: the
        /// near end landed, the far end did not move, and the far end is still connected to what held it.
        /// Anything else undoes itself and hands the case back to the curve route, which then refuses it the
        /// way it always did.
        /// </summary>
        private static JObject MoveFreeEndOnly(Element e, double mx, double my, out string why)
        {
            why = null;
            if (e == null) { why = "no element"; return null; }
            List<Connector> all = MepConnect.ConnectorsOf(e);
            Connector near = null, far = null;
            double dn = double.MaxValue, df = -1;
            foreach (Connector c in all)
            {
                XYZ org; try { org = c.Origin; } catch { continue; }
                if (org == null) continue;
                double d = Math.Sqrt(Math.Pow(CadUnits.FeetToMm(org.X) - mx, 2) + Math.Pow(CadUnits.FeetToMm(org.Y) - my, 2));
                if (d < dn) { dn = d; near = c; }
                if (d > df) { df = d; far = c; }
            }
            if (near == null || far == null || ReferenceEquals(near, far)) { why = "this run does not expose two end connectors"; return null; }
            bool farConnected; try { farConnected = far.IsConnected; } catch { farConnected = false; }
            if (!farConnected) { why = "the far end is free, so the curve route applies"; return null; }
            bool nearConnected; try { nearConnected = near.IsConnected; } catch { nearConnected = true; }
            if (nearConnected) { why = "the end that must move is itself connected"; return null; }

            XYZ farWas = far.Origin;
            var heldBy = new List<long>();
            try { foreach (Connector r in far.AllRefs) if (r != null && r.Owner != null && r.Owner.Id != e.Id) heldBy.Add(Rid.Value(r.Owner.Id)); }
            catch { }
            XYZ target = new XYZ(mx / 304.8, my / 304.8, near.Origin.Z);
            double moved = dn;

            Document doc = e.Document;
            using (var t = new Transaction(doc, "Horizun: move a run's free end"))
            {
                t.Start();
                try
                {
                    near.Origin = target;
                    Guard.Commit(t, "move a run's free end");
                }
                catch (Exception ex)
                {
                    // WHAT THE ROLLBACK ACTUALLY DID, never what we hoped: a status other than RolledBack
                    // leaves the model uncertain, and the caller has to be told which of the two it is.
                    string undone = "not_started";
                    if (t.HasStarted() && !t.HasEnded())
                    {
                        Guard.RollbackResult back = Guard.RollBack(t);
                        undone = back.Confirmed ? "rolled_back" : "UNCERTAIN: " + back.StatusName;
                    }
                    why = "moving the free connector did not commit: " + ex.Message + " (" + undone + ")";
                    return null;
                }
            }

            // RE-READ, all three: the end that had to move, the end that had to stay, and the connection
            // that had to survive. A move that reports itself is not a move.
            List<Connector> after = MepConnect.ConnectorsOf(e);
            Connector nearNow = null, farNow = null;
            double dn2 = double.MaxValue, df2 = -1;
            foreach (Connector c in after)
            {
                XYZ org; try { org = c.Origin; } catch { continue; }
                if (org == null) continue;
                double d = Math.Sqrt(Math.Pow(CadUnits.FeetToMm(org.X) - mx, 2) + Math.Pow(CadUnits.FeetToMm(org.Y) - my, 2));
                if (d < dn2) { dn2 = d; nearNow = c; }
                if (d > df2) { df2 = d; farNow = c; }
            }
            double farDrift = farNow == null ? double.MaxValue
                : Math.Sqrt(Math.Pow(CadUnits.FeetToMm(farNow.Origin.X - farWas.X), 2) +
                            Math.Pow(CadUnits.FeetToMm(farNow.Origin.Y - farWas.Y), 2) +
                            Math.Pow(CadUnits.FeetToMm(farNow.Origin.Z - farWas.Z), 2));
            var heldNow = new List<long>();
            try { if (farNow != null) foreach (Connector r in farNow.AllRefs) if (r != null && r.Owner != null && r.Owner.Id != e.Id) heldNow.Add(Rid.Value(r.Owner.Id)); }
            catch { }
            bool kept = heldBy.Count == heldNow.Count && heldBy.TrueForAll(heldNow.Contains);
            if (dn2 > 0.2 || farDrift > 0.2 || !kept)
            {
                why = "the free-end move did not verify: the end landed " + Math.Round(dn2, 2).ToString(CultureInfo.InvariantCulture) +
                      " mm from the target, the far end moved " + Math.Round(farDrift, 2).ToString(CultureInfo.InvariantCulture) +
                      " mm, and its connection was " + (kept ? "kept" : "lost") + ".";
                return null;
            }
            return new JObject
            {
                ["element_id"] = Rid.Value(e.Id),
                ["end_moved_mm"] = Math.Round(moved, 1),
                ["to_mm"] = new JArray(Math.Round(mx, 4), Math.Round(my, 4), Math.Round(CadUnits.FeetToMm(target.Z), 4)),
                ["moved_by"] = "the free connector's own origin",
                ["why_not_the_curve"] = "the far end is connected, and replacing the curve makes Revit re-evaluate that connection",
                ["far_end_held_by"] = new JArray(heldNow.Select(x => (JToken)x)),
                ["far_end_drift_mm"] = Math.Round(farDrift, 2),
                ["verified"] = true
            };
        }

        /// <summary>How far each end of each member moved between two readings of it.</summary>
        private static JObject MemberMovement(JObject before, JObject after)
        {
            var o = new JObject();
            if (before == null || after == null) return o;
            foreach (JProperty p in before.Properties())
            {
                var was = p.Value as JArray;
                var now = after[p.Name] as JArray;
                if (was == null || now == null) { o[p.Name] = "not_re_read"; continue; }
                // ENDS ARE PAIRED BY PROXIMITY, not by index: Revit hands back a curve it rebuilt with its ends
                // the other way round, and comparing index to index read a 124 mm trim as a 1318 mm move.
                Func<int, int, double> gap = (i, k) =>
                {
                    var w = was[i] as JArray; var n = now[k] as JArray;
                    return Math.Sqrt(Math.Pow(w[0].Value<double>() - n[0].Value<double>(), 2) +
                                     Math.Pow(w[1].Value<double>() - n[1].Value<double>(), 2) +
                                     Math.Pow(w[2].Value<double>() - n[2].Value<double>(), 2));
                };
                bool straight = gap(0, 0) + gap(1, 1) <= gap(0, 1) + gap(1, 0);
                var moved = new JArray(Math.Round(straight ? gap(0, 0) : gap(0, 1), 2),
                                       Math.Round(straight ? gap(1, 1) : gap(1, 0), 2));
                o[p.Name] = moved;
            }
            return o;
        }

        /// <summary>
        /// The declared transition types, tried in order ON the fitting Revit built and MEASURED against the drawing.
        /// Returns the check of the first that fits, or null when none does; every attempt is reported with the
        /// length it produced. A name that names no loaded type is reported as such - never substituted.
        /// </summary>
        /// <summary>
        /// The declared transition types, each tried FROM THE SAME STATE and measured against the drawing.
        ///
        /// MEASURED (campaign 7): trying them one after another on the same fitting compared each candidate against
        /// the model the previous one had left, so the answer depended on the order they were written in. Every
        /// attempt now runs inside its own transaction group, is rolled back, and the rollback is CONFIRMED by
        /// re-reading the fitting's type, its two ends and both members; a state that cannot be restored stops the
        /// comparison instead of producing a ranking nobody can trust. The winner - the best measured fit, not the
        /// first declared - is then applied once from that same base state and re-read.
        ///
        /// A name that no loaded type answers to, a bare type name two families answer to, and a type that is not a
        /// transition are all REFUSED by name; none of them is substituted.
        /// </summary>
        private static JObject FitByDeclaredType(Document doc, Junction j, JObject trow, JArray declared,
                                                 double fitTolerance, out JArray attempts)
        {
            attempts = new JArray();
            JObject best = null;
            FamilySymbol bestSymbol = null;
            long? fid = (trow["reread"] as JObject)?.Value<long?>("fitting_id");
            var fitting = fid.HasValue ? doc.GetElement(Rid.Make(fid.Value)) as FamilyInstance : null;
            if (fitting == null)
            {
                attempts.Add(new JObject { ["means"] = "the fitting could not be re-read, so no declared type was tried" });
                return null;
            }
            List<FamilySymbol> loaded = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>().Where(s => s.Category != null &&
                    (BuiltInCategory)(int)Rid.Value(s.Category.Id) == BuiltInCategory.OST_DuctFitting).ToList();

            JObject baseState = FittingState(fitting);
            JObject baseMembers = MemberEnds(j);
            foreach (JToken t in declared)
            {
                JObject refusal;
                FamilySymbol sym = ResolveDeclaredType(t, loaded, out refusal);
                if (sym == null) { attempts.Add(refusal); continue; }

                var attempt = new JObject
                {
                    ["declared"] = t.ToString(),
                    ["resolved"] = true,
                    ["type"] = sym.FamilyName + ": " + sym.Name,
                    ["type_id"] = Rid.Value(sym.Id),
                    ["part_type"] = PartTypeOf(sym) ?? "unknown"
                };
                string words = null;
                double centred = 0;
                JObject check = null;
                var group = new TransactionGroup(doc, "Horizun: try a declared transition type");
                group.Start();
                try
                {
                    using (var tx = new Transaction(doc, "Horizun: declared transition type"))
                    {
                        tx.Start();
                        if (FaultInjected("cad-transition-change-type"))
                            throw new InvalidOperationException("injected fault: cad-transition-change-type");
                        if (!sym.IsActive) sym.Activate();
                        fitting.ChangeTypeId(sym.Id);
                        doc.Regenerate();
                        // THE FITTING GOES WHERE THE DRAWING PUT THE PIECE. Both runs were brought to the drawn
                        // midpoint so Revit could create it, so it grows from there and one end stays ON the
                        // midpoint - 124 mm from the drawn end on M106, whatever its length.
                        if (FaultInjected("cad-transition-centre"))
                            throw new InvalidOperationException("injected fault: cad-transition-centre");
                        XYZ delta = CentringOffset(fitting, j);
                        if (delta != null && delta.GetLength() > 0.1 / 304.8)
                        {
                            ElementTransformUtils.MoveElement(doc, fitting.Id, delta);
                            doc.Regenerate();
                            centred = Math.Round(delta.GetLength() * 304.8, 2);
                        }
                        tx.Commit();
                    }
                    check = TransitionCheck(j, trow, fitTolerance);
                }
                catch (Exception ex) { words = ex.Message; }

                TransactionStatus rolled = TransactionStatus.Uninitialized;
                // THROUGH Guard, which is what reports the status instead of assuming it - and what the
                // guard test looks for. Measured either way, but a raw call is a habit that eventually forgets.
                try { rolled = Guard.RollBack(group).Status; }
                catch (Exception ex) { words = (words == null ? "" : words + "; ") + "rolling back: " + ex.Message; }
                group.Dispose();
                fitting = fid.HasValue ? doc.GetElement(Rid.Make(fid.Value)) as FamilyInstance : null;
                JObject nowState = fitting != null ? FittingState(fitting) : null;
                JObject nowMembers = MemberEnds(j);
                bool restored = rolled == TransactionStatus.RolledBack && nowState != null &&
                                JToken.DeepEquals(nowState, baseState) && JToken.DeepEquals(nowMembers, baseMembers);

                attempt["revit_said"] = words;
                attempt["centred_on_the_drawn_piece_mm"] = centred;
                attempt["fitting_length_mm"] = check?["fitting_length_mm"];
                attempt["worst_end_offset_mm"] = check?["worst_end_offset_mm"];
                attempt["sizes_match_members"] = check?["sizes_match_members"];
                attempt["collinear_with_drawn_axis"] = check?["collinear_with_drawn_axis"];
                attempt["members_moved_mm"] = MemberMovement(baseMembers, check == null ? baseMembers : nowMembers);
                attempt["rolled_back"] = rolled.ToString();
                attempt["state_restored"] = restored;
                attempt["fits"] = check?["fits"] ?? false;
                attempts.Add(attempt);

                if (!restored)
                {
                    attempts.Add(new JObject
                    {
                        ["stopped"] = true,
                        ["means"] = "the model was not put back the way it was after this attempt (" + rolled +
                                    "), so the candidates that follow would not be measured from the same state. " +
                                    "None of them was tried and no winner was applied."
                    });
                    return null;
                }
                if (check != null && check.Value<bool>("fits") &&
                    (best == null || check.Value<double>("worst_end_offset_mm") < best.Value<double>("worst_end_offset_mm")))
                {
                    best = check;
                    bestSymbol = sym;
                }
            }

            // THE BEST MEASURED FIT, applied ONCE from the same base state every candidate was measured from. The
            // order a caller writes its types in is not a fact about the drawing.
            if (best == null || bestSymbol == null) return null;
            try
            {
                using (var tx = new Transaction(doc, "Horizun: best declared transition type"))
                {
                    tx.Start();
                    if (FaultInjected("cad-transition-apply-winner"))
                        throw new InvalidOperationException("injected fault: cad-transition-apply-winner");
                    if (!bestSymbol.IsActive) bestSymbol.Activate();
                    fitting.ChangeTypeId(bestSymbol.Id);
                    doc.Regenerate();
                    XYZ delta = CentringOffset(fitting, j);
                    if (delta != null && delta.GetLength() > 0.1 / 304.8)
                    {
                        ElementTransformUtils.MoveElement(doc, fitting.Id, delta);
                        doc.Regenerate();
                    }
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                attempts.Add(new JObject
                {
                    ["applying_the_winner"] = bestSymbol.FamilyName + ": " + bestSymbol.Name,
                    ["revit_said"] = ex.Message,
                    ["means"] = "the winner did not go in; the drawn transition is refused and its whole group undone"
                });
                return null;
            }
            JObject applied = TransitionCheck(j, trow, fitTolerance);
            applied["chosen_among"] = attempts.Count(a => a.Value<bool?>("resolved") == true);
            applied["chosen_type"] = bestSymbol.FamilyName + ": " + bestSymbol.Name;
            applied["applied_from_the_base_state"] = true;
            if (!applied.Value<bool>("fits"))
            {
                attempts.Add(new JObject
                {
                    ["applying_the_winner"] = bestSymbol.FamilyName + ": " + bestSymbol.Name,
                    ["means"] = "applied from the base state the winner no longer fits (" +
                                applied.Value<double?>("worst_end_offset_mm") + " mm), so it is not kept: what was " +
                                "measured in an isolated attempt has to hold in the model that is kept"
                });
                return null;
            }
            return applied;
        }

        /// <summary>The declared name or id as ONE loaded duct-fitting type, or the refusal that says why not.</summary>
        private static FamilySymbol ResolveDeclaredType(JToken t, List<FamilySymbol> loaded, out JObject refusal)
        {
            refusal = null;
            string want = t.Type == JTokenType.Integer ? null : (t.ToString() ?? "").Trim();
            long id = t.Type == JTokenType.Integer ? t.Value<long>() : -1;
            List<FamilySymbol> hits = id >= 0
                ? loaded.Where(s => Rid.Value(s.Id) == id).ToList()
                : loaded.Where(s => string.Equals(s.FamilyName + ": " + s.Name, want, StringComparison.OrdinalIgnoreCase)).ToList();
            bool bare = false;
            if (hits.Count == 0 && id < 0)
            {
                // A BARE TYPE NAME IS ACCEPTED ONLY WHEN ONE FAMILY ANSWERS TO IT. Taking the first the collector
                // returns makes the answer depend on Revit's iteration order.
                hits = loaded.Where(s => string.Equals(s.Name, want, StringComparison.OrdinalIgnoreCase)).ToList();
                bare = true;
            }
            if (hits.Count > 1)
            {
                refusal = new JObject
                {
                    ["declared"] = t.ToString(),
                    ["resolved"] = false,
                    ["refused"] = bare ? "ambiguous_type_name" : "ambiguous_type_id",
                    ["candidates"] = new JArray(hits.Select(x => (JToken)(x.FamilyName + ": " + x.Name))),
                    ["means"] = hits.Count + " loaded duct-fitting types answer to '" + (want ?? id.ToString(CultureInfo.InvariantCulture)) +
                                "'. Name the family too (\"Family: Type\"): choosing one of them here would make the " +
                                "result depend on the order Revit lists them in."
                };
                return null;
            }
            if (hits.Count == 0)
            {
                refusal = new JObject
                {
                    ["declared"] = t.ToString(),
                    ["resolved"] = false,
                    ["refused"] = "type_not_found",
                    ["means"] = "no duct-fitting type of this model is named that; " +
                                CadCatalogCheck.LoadedOfFamily(want ?? "(by id)",
                                    loaded.Where(s => string.Equals(s.FamilyName, CadCatalogCheck.FamilyOf(want ?? ""), StringComparison.OrdinalIgnoreCase))
                                          .Select(s => s.FamilyName + ": " + s.Name).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                                    "duct fitting",
                                    loaded.Select(s => s.FamilyName + ": " + s.Name).Distinct(StringComparer.Ordinal)
                                          .OrderBy(x => x, StringComparer.Ordinal).Take(25).ToList())
                };
                return null;
            }
            FamilySymbol sym = hits[0];
            string part = PartTypeOf(sym);
            if (part != null && !string.Equals(part, "Transition", StringComparison.OrdinalIgnoreCase))
            {
                refusal = new JObject
                {
                    ["declared"] = t.ToString(),
                    ["resolved"] = false,
                    ["refused"] = "not_a_transition",
                    ["type"] = sym.FamilyName + ": " + sym.Name,
                    ["part_type"] = part,
                    ["means"] = "this type's family is a " + part + ", not a Transition: putting it where a transition " +
                                "goes would build a different fitting and measure it happily"
                };
                return null;
            }
            return sym;
        }

        /// <summary>A family's declared part type (Transition, Elbow, Tee...), or null when the family does not say.</summary>
        private static string PartTypeOf(FamilySymbol sym)
        {
            try
            {
                Parameter p = sym?.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                if (p == null || p.StorageType != StorageType.Integer) return null;
                return ((PartType)p.AsInteger()).ToString();
            }
            catch { return null; }
        }

        /// <summary>What an isolated attempt must leave exactly as it found it: the fitting's type and where its ends are.</summary>
        private static JObject FittingState(FamilyInstance fitting)
        {
            var o = new JObject { ["type_id"] = Rid.Value(fitting.GetTypeId()) };
            var ends = new JArray();
            try
            {
                var cs = fitting.MEPModel?.ConnectorManager?.Connectors?.Cast<Connector>()
                    .OrderBy(c => c.Origin.X).ThenBy(c => c.Origin.Y).ThenBy(c => c.Origin.Z).ToList();
                if (cs != null)
                    foreach (Connector c in cs)
                        ends.Add(new JArray(Math.Round(c.Origin.X * 304.8, 3), Math.Round(c.Origin.Y * 304.8, 3),
                                            Math.Round(c.Origin.Z * 304.8, 3)));
            }
            catch { }
            o["ends_mm"] = ends;
            return o;
        }

        /// <summary>A named failure point, for the tests that have to see a failure handled. Never set in a real run.</summary>
        private static bool FaultInjected(string key)
        {
            string named = Environment.GetEnvironmentVariable("HORIZUN_TEST_FAIL_ACTION");
            return !string.IsNullOrWhiteSpace(named) && string.Equals(named.Trim(), key, StringComparison.Ordinal);
        }

        private JObject DelegateDirect(UIApplication app, string title, Junction j,
                                       MepConnectorPick a, MepConnectorPick b,
                                       bool dryRun, JObject request, string keySuffix = "")
        {
            ICommand child = _resolve == null ? null : _resolve("horizun_connect_mep");
            if (child == null)
                return j.Row("refused", "connect_mep_unavailable",
                    "the typed connection command could not be resolved, and this command does not join " +
                    "connectors itself - a second implementation would be a second set of rules about when " +
                    "a connection is legitimate.");

            var args = new JObject
            {
                ["target_document"] = title,
                ["units"] = "mm",
                ["dry_run"] = dryRun,
                ["actions"] = new JArray(new JObject
                {
                    ["key"] = j.Id ?? ("j" + j.ElementIds[0].ToString(CultureInfo.InvariantCulture)),
                    ["operation"] = "connect",
                    ["a_element_id"] = j.ElementIds[0],
                    ["a_connector"] = a.ConnectorId,
                    ["b_element_id"] = j.ElementIds[1],
                    ["b_connector"] = b.ConnectorId
                })
            };

            CommandResult r;
            try
            {
                // REHEARSE, THEN SPEND THE TOKEN - horizun_connect_mep's own protocol. MEASURED (campaign 7):
                // the sequential rehearsal sent dry_run=false straight away and every direct join was
                // refused "No such confirmation token"; no earlier case had two built collinear runs.
                string idem = request.Value<string>("idempotency_key");
                if (!string.IsNullOrWhiteSpace(idem)) args["idempotency_key"] = idem + "-" + (j.Id ?? "j") + "-direct" + keySuffix;
                args["dry_run"] = true;
                r = child.Execute(app, args.ToString(Formatting.None));
                if (!dryRun && r.Success)
                {
                    string token = TokenOf(r);
                    if (token == null)
                        return j.Row("refused", "connect_mep_gave_no_token",
                            "the delegated rehearsal succeeded and issued no confirmation token, so the join " +
                            "cannot be authorised. Nothing was written.");
                    args["dry_run"] = false;
                    args["confirmation_token"] = token;
                    r = child.Execute(app, args.ToString(Formatting.None));
                }
            }
            catch (Exception ex)
            {
                return j.Row("refused", "connect_mep_threw", ex.Message);
            }

            JObject row = j.Row(r.Success ? (dryRun ? "would_join" : "joined") : "refused",
                                r.Success ? null : "connect_mep_refused",
                                r.Success ? null : r.Error);
            row["delegated_to"] = "horizun_connect_mep";
            row["connectors"] = new JObject
            {
                ["a"] = a.ConnectorId,
                ["a_distance_mm"] = Math.Round(a.DistanceMm, 3, MidpointRounding.AwayFromZero),
                ["b"] = b.ConnectorId,
                ["b_distance_mm"] = Math.Round(b.DistanceMm, 3, MidpointRounding.AwayFromZero)
            };
            if (!r.Success) row["error"] = r.Error;
            return row;
        }

        /// <summary>Turn a fitting junction into horizun_create_elements' own typed row and send it.</summary>
        private JObject DelegateFitting(UIApplication app, string title, Junction j,
                                        bool dryRun, JObject request, string keySuffix = "")
        {
            int need = j.Fitting == "cross" ? 4 : j.Fitting == "tee" ? 3 : 2;
            if (j.Elements.Count != need)
                return j.Row("refused", "wrong_member_count",
                    "a " + j.Fitting + " takes " + need.ToString(CultureInfo.InvariantCulture) +
                    " elements and this junction names " +
                    j.Elements.Count.ToString(CultureInfo.InvariantCulture) + ".");

            ICommand child = _resolve == null ? null : _resolve("horizun_create_elements");
            if (child == null)
                return j.Row("refused", "create_elements_unavailable",
                    "the typed creation command could not be resolved, and this command does not build " +
                    "fittings itself - a second builder would be a second set of rules about which connector " +
                    "is chosen, and the two would drift.");

            // THE ORDER IS REVIT'S. A tee lists the two through-run elements and
            // then the branch; a cross lists the first through pair and then the
            // second. The network reading already sorted them that way, and
            // re-sorting here would put the branch in the run.
            var members = new JArray(j.Elements.Select(e => (JToken)new JObject
            {
                ["element_id"] = Rid.Value(e.Id)
            }));

            // ALWAYS REHEARSED FIRST. On an apply this is the call that earns the
            // token; on a rehearsal it is the whole of the work.
            var args = new JObject
            {
                ["target_document"] = title,
                ["dry_run"] = true,
                ["elements"] = new JArray(new JObject
                {
                    ["kind"] = "fitting",
                    ["fitting"] = j.Fitting,
                    ["elements"] = members
                })
            };
            string key = request.Value<string>("idempotency_key");
            if (!string.IsNullOrWhiteSpace(key)) args["idempotency_key"] = key + "-" + (j.Id ?? "j") + keySuffix;

            CommandResult r;
            try
            {
                r = child.Execute(app, args.ToString(Formatting.None));

                // THE CHILD'S PROTOCOL IS REHEARSE-THEN-APPLY, and it is not
                // optional: horizun_create_elements refuses a fitting without a
                // confirmation token issued by its own dry run for this exact
                // request. Forwarding dry_run alone produced a route that
                // rehearsed perfectly and could never write - measured in Revit
                // against two real pipes.
                //
                // So an apply rehearses first and spends the token it is given.
                // The alternative would be to build the fitting here, which is
                // the second builder this command exists to avoid.
                if (!dryRun && r.Success)
                {
                    string token = TokenOf(r);
                    if (token == null)
                    {
                        JObject noToken = j.Row("refused", "create_elements_gave_no_token",
                            "the delegated rehearsal succeeded and issued no confirmation token, so the " +
                            "apply cannot be authorised. Nothing was written.");
                        // WHY there was no token is in the rehearsal's own reply - kept, not paraphrased.
                        var rehearsalReply = r.Data as JObject;
                        if (rehearsalReply != null)
                            noToken["rehearsal"] = new JObject
                            {
                                ["application"] = rehearsalReply["application"], ["state"] = rehearsalReply["state"],
                                ["rows"] = rehearsalReply["rows"] ?? rehearsalReply["elements"], ["warnings"] = rehearsalReply["warnings"],
                                ["confirmation_note"] = rehearsalReply["confirmation_note"] ?? rehearsalReply["confirmation"]
                            };
                        return noToken;
                    }

                    var apply = (JObject)args.DeepClone();
                    apply["dry_run"] = false;
                    apply["confirmation_token"] = token;
                    r = child.Execute(app, apply.ToString(Formatting.None));
                }
            }
            catch (Exception ex)
            {
                return j.Row("refused", "create_elements_threw", ex.Message);
            }

            JObject row = j.Row(r.Success ? (dryRun ? "would_create" : "created") : "refused",
                                r.Success ? null : "create_elements_refused",
                                r.Success ? null : r.Error);
            row["delegated_to"] = "horizun_create_elements";
            row["fitting"] = j.Fitting;
            if (!r.Success) row["error"] = r.Error;
            return row;
        }

        // ---------------------------------------------------------------------
        private sealed class Junction
        {
            public string Id;
            public string Fitting = "none";
            public bool Automatic;
            public string Says;
            public CadPoint At;
            /// <summary>A drawn transition: where the drawing put it (from_mm, to_mm, length_mm). Null otherwise.</summary>
            public JObject Drawn;
            public List<Element> Elements = new List<Element>();
            public List<long> ElementIds = new List<long>();

            /// <summary>
            /// Set when a member could not be resolved to an element. The junction
            /// is SKIPPED with this sentence; the call is not refused.
            ///
            /// The distinction is the difference between a caller who sent the
            /// wrong shape - who has misunderstood something, and whose other
            /// junctions should not be made either - and the normal case, where the
            /// network reading covers the whole drawing and the conversion built
            /// only what nobody had to argue about.
            /// </summary>
            public string Unresolved;

            /// <summary>
            /// <paramref name="candidates"/> is the provenance index, built at most
            /// once per call and shared by every junction. Reading Extensible Storage
            /// off every element in a model once per junction would make the parse
            /// the dominant cost of the command.
            /// </summary>
            public static Junction Read(JObject o, int index, Document doc,
                                        Dictionary<string, List<Element>> candidates,
                                        Dictionary<string, List<Element>> semantics, out string error)
            {
                error = null;
                if (o == null) { error = "junctions[" + index + "] is not an object."; return null; }

                var j = new Junction
                {
                    Id = o.Value<string>("id") ?? o.Value<string>("junction_node"),
                    Fitting = (o.Value<string>("fitting") ?? "none").ToLowerInvariant(),
                    Automatic = o.Value<bool?>("automatic") ?? false,
                    Says = o.Value<string>("says")
                };

                if (j.Fitting != "none" && j.Fitting != "direct" && j.Fitting != "elbow" &&
                    j.Fitting != "tee" && j.Fitting != "cross" && j.Fitting != "transition")
                {
                    error = "junctions[" + index + "].fitting is '" + j.Fitting +
                            "'; it must be one of none, direct, elbow, tee, cross, transition.";
                    return null;
                }
                j.Drawn = o["drawn"] as JObject;

                var at = o["at"] as JArray;
                if (at == null || at.Count < 2)
                {
                    error = "junctions[" + index + "] needs 'at' as [x, y] or [x, y, z] in millimetres - the " +
                            "point the drawing put this junction at. Without it the connector to join cannot " +
                            "be told from the one at the other end of the same pipe.";
                    return null;
                }
                j.At = new CadPoint(at[0].Value<double>(), at[1].Value<double>(),
                                    at.Count > 2 ? at[2].Value<double>() : 0);

                var elements = o["elements"] as JArray;
                if (elements == null || elements.Count < 2)
                {
                    error = "junctions[" + index + "] needs 'elements' with at least two element ids.";
                    return null;
                }
                foreach (JToken t in elements)
                {
                    var holder = t as JObject;

                    // A SEMANTIC ID: what the thing IS and on which layer.
                    //
                    // This is the one that closes the route. A run from
                    // horizun_cad_networks carries the same semantic id the
                    // conversion stamps on the element built from it - same merge,
                    // same tolerance, same identity function - so a junction can
                    // name its members by what the DRAWING says and the model
                    // resolves them. Nobody has to map run ids to element ids by
                    // reading two replies side by side, which is the step a person
                    // gets wrong in a way a plan view does not show.
                    string semantic = holder == null ? null : holder.Value<string>("semantic_id");
                    if (!string.IsNullOrWhiteSpace(semantic))
                    {
                        List<Element> bySemantic = null;
                        if (semantics == null || !semantics.TryGetValue(semantic, out bySemantic) ||
                            bySemantic == null || bySemantic.Count == 0)
                        {
                            j.Unresolved = "no element in this document remembers being built from semantic " +
                                           "id '" + semantic + "'. Either that run was deferred for review " +
                                           "and never built, or the network reading was taken at different " +
                                           "tolerances from the conversion - horizun_cad_networks reports " +
                                           "run_identity.matches_the_conversion for exactly this.";
                            return j;
                        }
                        if (bySemantic.Count > 1)
                        {
                            j.Unresolved = bySemantic.Count.ToString(CultureInfo.InvariantCulture) +
                                           " elements remember being built from semantic id '" + semantic +
                                           "' - the same thing on the same layer, built twice. Joining one " +
                                           "and not the others would be a guess about which duplicate is " +
                                           "the real run. Name the element id, or delete the duplicate.";
                            return j;
                        }
                        j.Elements.Add(bySemantic[0]);
                        j.ElementIds.Add(Rid.Value(bySemantic[0].Id));
                        continue;
                    }

                    // A CANDIDATE ID INSTEAD OF AN ELEMENT ID.
                    //
                    // Element ids belong to one run of one apply. The candidate id
                    // belongs to the DRAWING, and it is what horizun_apply_cad_plan
                    // stamped on everything it created - so a caller who planned the
                    // conversion can name the junction by what the drawing said, and
                    // it still resolves after the model has been saved and reopened.
                    string candidate = holder == null ? null : holder.Value<string>("candidate_id");
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        List<Element> hits = null;
                        if (candidates == null || !candidates.TryGetValue(candidate, out hits) ||
                            hits == null || hits.Count == 0)
                        {
                            j.Unresolved = "no element in this document remembers being built from candidate " +
                                           "'" + candidate + "'. Either the conversion has not been applied " +
                                           "here, or it was applied without provenance - " +
                                           "horizun_execute_plan creates elements that remember nothing.";
                            return j;
                        }
                        if (hits.Count > 1)
                        {
                            j.Unresolved = hits.Count.ToString(CultureInfo.InvariantCulture) + " elements " +
                                           "remember being built from candidate '" + candidate + "'. Joining " +
                                           "one of them and not the others would be a guess about which " +
                                           "duplicate is the real run. Name the element id.";
                            return j;
                        }
                        j.Elements.Add(hits[0]);
                        j.ElementIds.Add(Rid.Value(hits[0].Id));
                        continue;
                    }

                    long id = t.Type == JTokenType.Integer
                        ? t.Value<long>()
                        : holder == null ? -1 : (holder.Value<long?>("element_id") ?? -1);
                    if (id < 0 || !Rid.CanRepresent(id))
                    {
                        error = "junctions[" + index + "] names none of element_id, candidate_id or " +
                                "semantic_id: " + t;
                        return null;
                    }
                    Element e = doc.GetElement(Rid.Make(id));
                    if (e == null)
                    {
                        // AN ID THAT IS GONE IS ONE JUNCTION, NOT THE CALL. It usually
                        // means the plan was applied to a different model, or that this
                        // element was deleted - and either way the other junctions are
                        // still the ones the drawing shows.
                        j.Unresolved = "element " + id.ToString(CultureInfo.InvariantCulture) +
                                       " is not in this document. An id that has gone usually means the plan " +
                                       "was applied to a different model, or that somebody deleted it.";
                        return j;
                    }
                    j.Elements.Add(e);
                    j.ElementIds.Add(id);
                }
                return j;
            }

            public JObject Row(string state, string refusal, string says)
            {
                var o = new JObject
                {
                    ["id"] = Id,
                    ["state"] = state,
                    ["elements"] = new JArray(ElementIds.Select(x => (JToken)x)),
                    ["at_mm"] = new JArray(Math.Round(At.X, 4, MidpointRounding.AwayFromZero),
                                           Math.Round(At.Y, 4, MidpointRounding.AwayFromZero),
                                           Math.Round(At.Z, 4, MidpointRounding.AwayFromZero))
                };
                if (refusal != null) o["refused"] = refusal;
                if (says != null) o["says"] = says;
                return o;
            }
        }

        /// <summary>
        /// Does this junction name any member by the given key rather than by
        /// element id? Each index costs a full pass over the model's Extensible
        /// Storage, so neither is built unless something asks for it.
        /// </summary>
        private static bool Names(JObject junction, string key)
        {
            var elements = junction["elements"] as JArray;
            if (elements == null) return false;
            foreach (JToken t in elements)
            {
                var holder = t as JObject;
                if (holder != null && !string.IsNullOrWhiteSpace(holder.Value<string>(key))) return true;
            }
            return false;
        }

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
    }
}
