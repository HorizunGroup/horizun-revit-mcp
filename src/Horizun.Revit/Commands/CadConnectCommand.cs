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

            JArray junctions = request["junctions"] as JArray;
            if (junctions == null || junctions.Count == 0)
                return CommandResult.Fail(
                    "junctions is required and must carry at least one entry. Copy them from " +
                    "horizun_cad_networks' reply, with each run id replaced by the element id that was built " +
                    "from it - the mapping is in horizun_apply_cad_plan's created rows.");

            double tolerance = request.Value<double?>("connector_tolerance_mm") ?? 25.0;
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

                // ---- elbow, tee, cross: the typed command owns this -------------
                JObject delegatedRow = DelegateFitting(app, title, j, !writeNow, request, group != null ? runKey : "");
                Attribute(delegatedRow, j, mark, writeNow, tolerance, group != null);
                if (sectionChange != null) delegatedRow["action_reason"] = sectionChange;
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
        /// Fittings reachable from a run through fittings only (at most three deep). MEASURED on the
        /// rectangular synthetic: a tee between a 16x8 and a 12x8 run got Revit's own transitions on
        /// its legs, so the runs touch the transitions and only reach the tee through them.
        /// </summary>
        private static List<Element> ReachableFittings(Element run)
        {
            var seen = new List<Element>();
            var frontier = AllNeighbours(run).Where(IsFitting).ToList();
            for (int depth = 0; depth < 3 && frontier.Count > 0; depth++)
            {
                var next = new List<Element>();
                foreach (Element f in frontier)
                {
                    if (seen.Any(x => x.Id == f.Id)) continue;
                    seen.Add(f);
                    next.AddRange(AllNeighbours(f).Where(IsFitting));
                }
                frontier = next;
            }
            return seen;
        }

        /// <summary>
        /// The fitting of the junction's kind that every member reaches, when there is exactly one;
        /// <paramref name="anyShared"/> is set when members share fittings but none of that kind.
        /// </summary>
        private static FamilyInstance CommonFitting(Junction j, out bool anyShared)
        {
            anyShared = false;
            List<Element> common = null;
            foreach (Element e in j.Elements)
            {
                var fits = ReachableFittings(e);
                common = common == null ? fits : common.Where(x => fits.Any(f => f.Id == x.Id)).ToList();
            }
            if (common == null || common.Count == 0) return null;
            anyShared = true;
            var ofKind = common.OfType<FamilyInstance>()
                .Where(f => string.Equals(PartOf(f), j.Fitting, StringComparison.OrdinalIgnoreCase)).ToList();
            return ofKind.Count == 1 ? ofKind[0] : (common.Count == 1 ? common[0] as FamilyInstance : null);
        }

        private static FamilyInstance CommonFitting(Junction j)
        {
            bool unused;
            return CommonFitting(j, out unused);
        }

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
                FamilyInstance shared = CommonFitting(j);
                if (shared != null)
                {
                    string part = PartOf(shared);
                    bool same = string.Equals(part, j.Fitting, StringComparison.OrdinalIgnoreCase);
                    return new JObject
                    {
                        ["state"] = same ? "already_connected" : "existing_fitting_differs",
                        ["fitting_id"] = Rid.Value(shared.Id),
                        ["fitting_part_type"] = part,
                        ["says"] = same
                            ? "one " + part.ToLowerInvariant() + " already joins every member of this junction; nothing was sent."
                            : "a " + part + " joins these members where the drawing proposes a " + j.Fitting +
                              ". It is left as it is and reported - replacing it is a decision, not a repair."
                    };
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
            FamilyInstance shared = j.Fitting == "direct" ? null : CommonFitting(j);
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
                ["all_connected"] = joined
            };
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
            try { r = child.Execute(app, args.ToString(Formatting.None)); }
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
                        return j.Row("refused", "create_elements_gave_no_token",
                            "the delegated rehearsal succeeded and issued no confirmation token, so the " +
                            "apply cannot be authorised. Nothing was written.");

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
                    j.Fitting != "tee" && j.Fitting != "cross")
                {
                    error = "junctions[" + index + "].fitting is '" + j.Fitting +
                            "'; it must be one of none, direct, elbow, tee, cross.";
                    return null;
                }

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
