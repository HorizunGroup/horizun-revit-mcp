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
            int joined = 0, skipped = 0, refused = 0, delegated = 0;

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

                    JObject directRow = DelegateDirect(app, title, j, pa, pb, dryRun, request);
                    rows.Add(directRow);
                    string directState = directRow.Value<string>("state");
                    if (directState == "joined" || directState == "would_join") joined++;
                    else refused++;
                    continue;
                }

                // ---- elbow, tee, cross: the typed command owns this -------------
                JObject delegatedRow = DelegateFitting(app, title, j, dryRun, request);
                rows.Add(delegatedRow);
                string state = delegatedRow.Value<string>("state");
                if (state == "created" || state == "would_create") delegated++;
                else refused++;
            }

            var result = new JObject
            {
                ["document"] = title,
                ["dry_run"] = dryRun,
                ["junctions_given"] = parsed.Count,
                ["direct_joined"] = joined,
                ["fittings_placed"] = delegated,
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

        private JObject DelegateDirect(UIApplication app, string title, Junction j,
                                       MepConnectorPick a, MepConnectorPick b,
                                       bool dryRun, JObject request)
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
                                        bool dryRun, JObject request)
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
            if (!string.IsNullOrWhiteSpace(key)) args["idempotency_key"] = key + "-" + (j.Id ?? "j");

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
