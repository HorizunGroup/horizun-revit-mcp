// -----------------------------------------------------------------------------
// Horizun Revit MCP - compact, typed authoring surface for common BIM elements.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class CreateElementsCommand : ICommand
    {
        public string Name => "horizun_create_elements";
        public string Description => "Create architectural, structural and MEP BIM elements atomically, then re-read every created id.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            JArray input = request["elements"] as JArray;
            JObject tabularBlock = null;
            if (request["tabular_source"] is JObject tabularSource)
            {
                // A CSV becomes family_instance entries, one per data row, expanded
                // HERE on every call: a file edited between rehearsal and apply
                // expands differently, the resolved plans differ, and the token
                // refuses stale - the same binding the parameter writer proved.
                if (input != null)
                    return CommandResult.Fail("give elements OR tabular_source, not both: a batch with two " +
                        "sources of truth cannot say which file row produced which element.");
                double preScale;
                if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out preScale))
                    return CommandResult.Fail("units must be mm, m or feet.");
                string expandError = ExpandTabularPlacements(doc, tabularSource, preScale, out input, out tabularBlock);
                if (expandError != null) return CommandResult.Fail(expandError + " Nothing ran.");
            }
            if (input == null || input.Count == 0) return CommandResult.Fail("elements is required and must be non-empty.");
            if (input.Count > 2000) return CommandResult.Fail("elements exceeds the 2000 item atomic-batch limit.");
            double scale;
            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (!Scale(units, out scale)) return CommandResult.Fail("units must be mm, m or feet.");

            var plans = new List<Plan>();
            var errors = new JArray();
            // Every action's outcome, so the FALLBACK decision is made once, centrally,
            // over the whole batch - a mixed batch must not inherit one entry's
            // capability gap as permission for the request. See FallbackDecision.
            var outcomes = new List<ActionOutcome>();
            for (int i = 0; i < input.Count; i++)
            {
                JObject item = input[i] as JObject;
                string error = null, reason = null;
                Plan plan = item == null ? null : PlanItem(doc, i, item, scale, out error, out reason);
                if (plan == null)
                {
                    string message = item == null ? "entry is not an object" : error;
                    errors.Add(new JObject { ["index"] = i, ["error"] = message });
                    outcomes.Add(new ActionOutcome { Index = i, Error = message, UnsupportedReason = reason });
                }
                else plans.Add(plan);
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "elements");

            // ---- The MATERIALISED plan: what the request's NAMES resolved to. -----------
            // planHash binds the batch as written, and for a creation command the batch is
            // names and ids: a wall type called "Muro 200", a level called "N.E 10", a
            // piping system picked by name. None of those meanings is frozen by the
            // request. Between the rehearsal and the apply somebody can rename a type,
            // swap what a name resolves to, or move a level's elevation - and the same
            // batch then creates different elements in different places. The plan records
            // each row's RESOLVED references: the type's UniqueId and name, the level's
            // UniqueId and its elevation as measured now. A level that moved 50mm is a
            // different plan even though its name still matches.
            //
            // Elements created do not exist at plan time, so what is fingerprinted is what
            // the caller actually approved: the recipe plus the resolved ingredients.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (Plan planned in plans)
            {
                var row = new PlannedElement
                {
                    UniqueId = "create:" + planned.Index,
                    Category = planned.Kind,
                    TypeName = SafePlanName(planned.Type),
                    Action = PlannedAction.Create,
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "type_uid", SafePlanUid(planned.Type) },
                        { "system_type", SafePlanName(planned.SystemType) },
                        { "system_type_uid", SafePlanUid(planned.SystemType) },
                        { "level", SafePlanName(planned.Level) },
                        { "level_uid", SafePlanUid(planned.Level) },
                        // The level's measured elevation, to the tenth of a millimetre.
                        // "Create on N.E 10" approved a HEIGHT, not a name: a level that
                        // moved is a different creation wearing the same words.
                        { "level_elev_mm", SafePlanElevation(planned.Level) }
                    }
                };
                if (planned.ExtraPlanFacts != null)
                    foreach (KeyValuePair<string, string> fact in planned.ExtraPlanFacts)
                        row.BeforeValues[fact.Key] = fact.Value;
                resolvedPlan.Elements.Add(row);
            }

            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["tabular"] = tabularBlock,
                    ["transaction_status"] = "not_started", ["requested"] = input.Count,
                    ["valid"] = plans.Count, ["invalid"] = errors.Count, ["errors"] = errors,
                    ["plan"] = new JArray(plans.Select(p => p.Summary)),
                    ["note"] = "Nothing was created and no transaction was opened. Correct every invalid row before apply."
                };
                if (errors.Count == 0) DocumentGate.RecordResolvedPlan(resolvedPlan);
                // THE REHEARSAL CARRIES THE VERDICT TOO. dry_run defaults to true, so
                // this is the first thing a caller sends; without the block here they
                // got success=true, invalid=1 and no way to tell a capability gap from
                // a typo except by sending an apply they had no reason to send.
                // Invalid entries make this a partial rehearsal, not a clean one: the token
                // below is already withheld for them, and a plan must read the same fact.
                ApplicationOutcome.StampRehearsal(result, input.Count, errors.Count, 0, 0);
                // Stamp before constructing CommandResult. The previous order happened to
                // work only because Ok retained the mutable JObject reference.
                CommandResult rehearsal = FallbackDecision.Attach(
                    CommandResult.Ok(result),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0
                        ? "the token binds this ordered heterogeneous batch, its units, AND what its names resolved " +
                          "to right now - the types, system types and levels, including each level's measured " +
                          "elevation. A type renamed, a name re-pointed or a level moved before you apply refuses " +
                          "as a stale plan instead of creating something else under the approved words."
                        : "no usable confirmation is issued while any row is invalid");
                return rehearsal;
            }
            if (errors.Count > 0)
            {
                string why = errors.Count + " element plan(s) are invalid. Nothing was created: " +
                             errors.ToString(Formatting.None);
                // Nothing has been written - no transaction is open at this point - so the
                // decision is entirely about WHAT failed, and it is made centrally.
                return FallbackDecision.Refuse(why, FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            // Recomputed above by this call's own PlanItem resolution. The rehearsed plan
            // does not travel in the token, only its fingerprint, so a stale refusal names
            // the drift generically - still refused, nothing created.
            CommandResult confirmation = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                          resolvedPlan, null);
            if (confirmation != null) return confirmation;
            CommandResult moved = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (moved != null) return moved;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: create elements";
            var created = new List<Created>();
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    foreach (Plan plan in plans)
                    {
                        Element element = Create(doc, plan, created);
                        if (element == null) throw new InvalidOperationException("item " + plan.Index + " returned no element");

                        // THE BORE, IN THE SAME TRANSACTION. Revit creates a run
                        // at its type's size and nothing in the geometry says
                        // otherwise, so a declared diameter is set here and
                        // re-read after the commit like everything else. A run
                        // that silently stayed at the default is a 15 mm main
                        // that looks perfectly correct in plan.
                        if (plan.Diameter.HasValue)
                        {
                            Parameter bore = DiameterParameterOf(element);
                            if (bore == null || bore.IsReadOnly)
                                throw new InvalidOperationException(
                                    "item " + plan.Index + ": this " + plan.Kind + " carries no diameter that can " +
                                    "be set - a rectangular run has a width and a height, and setting one of them " +
                                    "for a declared diameter would be a different run. Nothing was created.");
                            bore.Set(plan.Diameter.Value);
                        }
                        created.Add(new Created
                        {
                            Index = plan.Index, Kind = plan.Kind, Id = element.Id,
                            ExpectedTypeId = plan.Type?.Id,
                            ExpectedStructuralType = plan.Kind == "family_instance" || plan.Kind == "structural_framing" || plan.Kind == "structural_column"
                                ? (StructuralType?)plan.StructuralType : null,
                            ExpectedConnected = plan.FittingMembers,
                            ExpectedInlineConnections = plan.Kind == "accessory_inline",
                            ExpectedHostId = plan.OpeningHost?.Id ?? plan.SlabHost?.Id ?? plan.InstanceHost?.Id,
                            ExpectedArc = plan.ArcThird != null,
                            ExpectedArcCentre = plan.ArcCentre,
                            ExpectedArcRadius = plan.ArcRadius,
                            ExpectedStructural = plan.Structural,
                            ExpectedName = plan.WantName,
                            ExpectedNumber = plan.WantNumber,
                            AlsoCreated = plan.AlsoCreated,
                            ExpectedDiameter = plan.Diameter,
                            ExpectedSystemName = plan.SystemName,
                            ExpectedSystemTypeId = plan.Kind == "mep_system" ? plan.SystemType?.Id : null,
                            ExpectedMembers = plan.SystemMembers?.Select(m => m.Id).ToList()
                        });
                    }
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    bool attempted = false; string rb = PlanFailure.NotAttempted;
                    if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                    return CommandResult.Fail("Atomic creation failed: " + ex.Message + ". " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing in this batch was kept"));
                }
            }

            var rows = new JArray();
            int verified = 0;
            foreach (Created made in created)
            {
                Element element = doc.GetElement(made.Id);
                bool kindMatches = element != null && KindMatches(element, made.Kind);
                bool typeMatches = made.ExpectedTypeId == null || (element != null && element.GetTypeId() == made.ExpectedTypeId);
                bool structuralTypeMatches = made.ExpectedStructuralType == null ||
                    (element is FamilyInstance instance && instance.StructuralType == made.ExpectedStructuralType.Value);
                // A fitting's whole point is the joints it closed: each approved
                // connector must re-read as CONNECTED after the commit.
                bool connectorsMatch = true;
                if (made.ExpectedConnected != null)
                    foreach (FittingMember member in made.ExpectedConnected)
                    {
                        bool nowConnected = false;
                        ConnectorManager manager = MepFacts.ManagerOf(doc.GetElement(member.Owner.Id));
                        if (manager != null)
                            foreach (Connector candidate in MepFacts.Ordered(manager))
                                if (candidate.Id == member.ConnectorId) { nowConnected = candidate.IsConnected; break; }
                        if (!nowConnected) { connectorsMatch = false; break; }
                    }
                // An inline accessory is not verified merely because its family
                // exposes a ConnectorManager.  Re-read both physical piping
                // connectors after the commit and prove that each one reaches a
                // DIFFERENT Pipe.  This caught a Revit 2023 failure where ConnectTo
                // looked successful inside the transaction but the committed model
                // carried a valve with two open connectors.
                bool inlineConnectionsMatch = true;
                JObject inlineConnectionsRow = null;
                if (made.ExpectedInlineConnections)
                {
                    var connectedPipeIds = new HashSet<long>();
                    int pipingConnectors = 0, connectedPipingConnectors = 0;
                    ConnectorManager manager = MepFacts.ManagerOf(element);
                    if (manager != null)
                        foreach (Connector connector in MepFacts.Ordered(manager))
                        {
                            if (connector.Domain != Domain.DomainPiping) continue;
                            pipingConnectors++;
                            bool reachesPipe = false;
                            try
                            {
                                foreach (Connector other in connector.AllRefs)
                                {
                                    if (!(other?.Owner is Pipe pipe)) continue;
                                    connectedPipeIds.Add(Rid.Value(pipe.Id));
                                    reachesPipe = true;
                                }
                            }
                            catch { }
                            if (connector.IsConnected && reachesPipe) connectedPipingConnectors++;
                        }
                    inlineConnectionsMatch = pipingConnectors == 2 &&
                                             connectedPipingConnectors == 2 &&
                                             connectedPipeIds.Count == 2;
                    inlineConnectionsRow = new JObject
                    {
                        ["piping_connectors"] = pipingConnectors,
                        ["connected_to_pipe"] = connectedPipingConnectors,
                        ["distinct_pipes"] = connectedPipeIds.Count,
                        ["pipe_ids"] = new JArray(connectedPipeIds.Cast<object>().ToArray()),
                        ["verified"] = inlineConnectionsMatch
                    };
                }
                // LOAD-BEARING, RE-READ. Wall.Create takes the flag and a Floor
                // is told afterwards, so neither is proof; the parameter Revit
                // actually holds is. A wall that reports itself structural and is
                // not appears in no analytical model and no structural schedule,
                // and nothing about it looks wrong in plan.
                bool structuralMatches = true;
                JObject structuralRow = null;
                if (made.ExpectedStructural.HasValue)
                {
                    bool? readBack = StructuralOf(element);
                    structuralMatches = readBack.HasValue && readBack.Value == made.ExpectedStructural.Value;
                    structuralRow = new JObject
                    {
                        ["requested"] = made.ExpectedStructural.Value,
                        ["read"] = readBack.HasValue ? (JToken)new JValue(readBack.Value) : JValue.CreateNull(),
                        ["verified"] = structuralMatches
                    };
                }

                // THE BORE, RE-READ. A drawn line carries no width, so the size
                // comes from the rule - and a run built at the type's default
                // instead is a 15 mm main that looks perfectly correct in plan
                // and fails every flow calculation downstream.
                bool diameterMatches = true;
                JObject diameterRow = null;
                if (made.ExpectedDiameter.HasValue)
                {
                    double? readBack = DiameterOf(element);
                    // A tenth of a millimetre, in feet: Revit stores sizes as
                    // doubles and a nominal bore rounds.
                    diameterMatches = readBack.HasValue &&
                                      Math.Abs(readBack.Value - made.ExpectedDiameter.Value) <= 0.1 / 304.8;
                    diameterRow = new JObject
                    {
                        ["requested_mm"] = Math.Round(made.ExpectedDiameter.Value * 304.8, 3),
                        ["read_mm"] = readBack.HasValue
                            ? (JToken)new JValue(Math.Round(readBack.Value * 304.8, 3)) : JValue.CreateNull(),
                        ["verified"] = diameterMatches
                    };
                }

                // THE NAME, RE-READ. Setting a property is not evidence that it
                // took: Revit renames on collision in some paths and refuses in
                // others, and a room's number is assigned by Revit the instant it
                // is placed. A command that reported a name it never confirmed
                // would put the wrong grid reference on every dimension drawn
                // from it.
                bool identityMatches = true;
                JObject identityRow = null;
                if (made.ExpectedName != null || made.ExpectedNumber != null)
                {
                    identityRow = new JObject();
                    if (made.ExpectedName != null)
                    {
                        string readName = IdentityOf(element, made.Kind, false);
                        bool ok = string.Equals(readName, made.ExpectedName, StringComparison.Ordinal);
                        identityMatches &= ok;
                        identityRow["name_requested"] = made.ExpectedName;
                        identityRow["name_read"] = readName;
                        identityRow["name_verified"] = ok;
                    }
                    if (made.ExpectedNumber != null)
                    {
                        string readNumber = IdentityOf(element, made.Kind, true);
                        bool ok = string.Equals(readNumber, made.ExpectedNumber, StringComparison.Ordinal);
                        identityMatches &= ok;
                        identityRow["number_requested"] = made.ExpectedNumber;
                        identityRow["number_read"] = readNumber;
                        identityRow["number_verified"] = ok;
                    }
                }

                bool hostMatches = made.ExpectedHostId == null ||
                    (element is Opening opening && opening.Host != null && opening.Host.Id == made.ExpectedHostId) ||
                    (element is FamilyInstance hosted && hosted.Host != null && hosted.Host.Id == made.ExpectedHostId);
                // The system's own facts, re-read: what it is CALLED, what type it was
                // made from, and WHICH elements it carries - not the count of Add calls
                // that did not throw.
                bool systemMatches = true;
                JObject systemRow = null;
                if (made.ExpectedSystemName != null)
                {
                    string nameAfter = Safe(() => (element as MEPSystem)?.Name);
                    ElementId typeAfter = null;
                    try { typeAfter = (element as MEPSystem)?.GetTypeId(); } catch { }
                    var membersAfter = new List<long>();
                    try
                    {
                        if (element is MEPSystem readSystem)
                            foreach (Element memberAfter in readSystem.Elements)
                                membersAfter.Add(Rid.Value(memberAfter.Id));
                    }
                    catch { }
                    var expected = (made.ExpectedMembers ?? new List<ElementId>()).Select(Rid.Value).ToList();
                    var missing = expected.Where(id => !membersAfter.Contains(id)).ToList();
                    bool nameOk = string.Equals(nameAfter, made.ExpectedSystemName, StringComparison.Ordinal);
                    bool typeOk = made.ExpectedSystemTypeId == null ||
                                  (typeAfter != null && typeAfter == made.ExpectedSystemTypeId);
                    systemMatches = nameOk && typeOk && missing.Count == 0;
                    systemRow = new JObject
                    {
                        ["name_requested"] = made.ExpectedSystemName,
                        ["name_read"] = nameAfter,
                        ["name_verified"] = nameOk,
                        ["system_type_verified"] = typeOk,
                        ["members_requested"] = expected.Count,
                        ["members_read"] = membersAfter.Count,
                        ["members_missing"] = new JArray(missing.Cast<object>().ToArray()),
                        ["members_verified"] = missing.Count == 0,
                        ["members_read_ids"] = new JArray(membersAfter.Cast<object>().ToArray())
                    };
                }
                // THE CURVE, when one was declared. "e is Wall" proves nothing
                // about curvature: Revit accepts an axis and can produce something
                // else when the type or a join forces it, and a command that
                // reported an arc it never built would be the exact false success
                // this bridge exists to prevent.
                JObject curveRow = VerifyCurve(element, made);
                bool curveMatches = curveRow == null || (bool)curveRow["verified"];

                bool rowVerified = kindMatches && typeMatches && structuralTypeMatches && connectorsMatch &&
                                   inlineConnectionsMatch &&
                                   hostMatches && systemMatches && curveMatches && structuralMatches &&
                                   diameterMatches && identityMatches;
                if (rowVerified) verified++;
                var verifyRow = new JObject
                {
                    ["index"] = made.Index, ["kind"] = made.Kind, ["element_id"] = Rid.Value(made.Id),
                    ["present_after_commit"] = element != null, ["kind_verified"] = kindMatches,
                    ["type_verified"] = typeMatches, ["structural_type_verified"] = structuralTypeMatches,
                    ["verified"] = rowVerified,
                    ["actual_class"] = element?.GetType().Name, ["actual_category"] = Safe(() => element?.Category?.Name)
                };
                // EVERY ELEMENT THIS ROW MADE. One call can produce a chain, and a
                // row that names only the first leaves the rest anonymous - no
                // provenance, so the audit calls them bim_without_source and no
                // incremental update ever touches them again.
                if (made.AlsoCreated != null && made.AlsoCreated.Count > 0)
                {
                    var everyId = new JArray { Rid.Value(made.Id) };
                    foreach (ElementId extra in made.AlsoCreated) everyId.Add(Rid.Value(extra));
                    verifyRow["element_ids"] = everyId;
                    verifyRow["elements_created"] = everyId.Count;
                    verifyRow["elements_created_means"] =
                        "this row asked for one thing and Revit made " + everyId.Count + " elements from it - " +
                        "a chain of curves is one separator and several model curves. element_id names the " +
                        "first; element_ids names all of them, and every one is stamped with this row's " +
                        "origin so none of them is anonymous.";
                }
                if (curveRow != null) verifyRow["curve_verified"] = curveRow;
                if (structuralRow != null) verifyRow["structural_verified"] = structuralRow;
                if (identityRow != null) verifyRow["identity_verified"] = identityRow;
                if (diameterRow != null) verifyRow["diameter_verified"] = diameterRow;
                if (systemRow != null) verifyRow["mep_system"] = systemRow;
                if (made.ExpectedConnected != null) verifyRow["connectors_verified"] = connectorsMatch;
                if (inlineConnectionsRow != null) verifyRow["inline_connections"] = inlineConnectionsRow;
                if (made.ExpectedHostId != null) verifyRow["host_verified"] = hostMatches;
                rows.Add(verifyRow);
            }
            if (verified != created.Count)
                return CommandResult.Fail("The transaction committed, but only " + verified + " of " + created.Count +
                    " created ids were re-read as the requested kinds. Inspect the model; success is not claimed. Verification: " +
                    rows.ToString(Formatting.None));

            var ceResult = new JObject
            {
                ["dry_run"] = false, ["tabular"] = tabularBlock, ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["requested"] = input.Count, ["created_verified"] = verified,
                ["verification"] = new JObject { ["intended"] = plans.Count, ["actual"] = verified, ["verified"] = verified == plans.Count },
                ["rows"] = rows
            };
            // Entries that never became a plan are unresolved: they were asked for and no
            // element was created for them, which is not the same as a creation that failed.
            ApplicationOutcome.StampApplied(ceResult, ApplicationOutcome.Committed, input.Count, verified,
                                            verified, input.Count - plans.Count, 0, 0);
            return CommandResult.Ok(ceResult);
        }

        // ---- CSV rows into family_instance entries. -----------------------------
        private static string ExpandTabularPlacements(Document doc, JObject source, double scale,
                                                      out JArray elements, out JObject provenance)
        {
            elements = null; provenance = null;
            string path = source.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
                return "tabular_source.path is required and must be absolute.";
            if (!System.IO.File.Exists(path)) return "tabular_source file '" + path + "' does not exist.";
            string decimalSeparator = source.Value<string>("decimal_separator") ?? ".";
            if (decimalSeparator != "." && decimalSeparator != ",")
                return "decimal_separator must be '.' or ',' - it is DECLARED, never guessed from the file.";
            string coordinates = (source.Value<string>("coordinates") ?? "internal").ToLowerInvariant();
            if (coordinates != "internal" && coordinates != "shared")
                return "coordinates must be internal or shared.";
            long typeId = source.Value<long?>("type_id") ?? -1;
            if (!Rid.CanRepresent(typeId) || !(doc.GetElement(Rid.Make(typeId)) is FamilySymbol))
                return "tabular_source.type_id must identify the FamilySymbol every row places.";
            long levelId = source.Value<long?>("level_id") ?? -1;
            if (!Rid.CanRepresent(levelId) || !(doc.GetElement(Rid.Make(levelId)) is Level))
                return "tabular_source.level_id must identify the Level every row lands on.";

            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(path); }
            catch (Exception ex) { return "tabular_source file could not be read: " + ex.Message; }
            string sha;
            using (var hasher = System.Security.Cryptography.SHA256.Create())
                sha = BitConverter.ToString(hasher.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            // BOM tolerated, like the parameter writer's reader: PowerShell's
            // Set-Content utf8 prepends one, and a header whose first cell reads
            // \uFEFFx would silently match no column.
            string text = new System.Text.UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF');
            List<string[]> rows = TabularRules.ParseCsv(text);
            if (rows.Count < 2) return "the file has no data rows under its header.";

            string[] header = rows[0];
            int xColumn = Array.FindIndex(header, h => string.Equals(h, source.Value<string>("x_column") ?? "x", StringComparison.OrdinalIgnoreCase));
            int yColumn = Array.FindIndex(header, h => string.Equals(h, source.Value<string>("y_column") ?? "y", StringComparison.OrdinalIgnoreCase));
            int zColumn = Array.FindIndex(header, h => string.Equals(h, source.Value<string>("z_column") ?? "z", StringComparison.OrdinalIgnoreCase));
            int rotationColumn = Array.FindIndex(header, h => string.Equals(h, source.Value<string>("rotation_column") ?? "rotation", StringComparison.OrdinalIgnoreCase));
            if (xColumn < 0 || yColumn < 0)
                return "the header names no x/y columns (looked for '" + (source.Value<string>("x_column") ?? "x") +
                       "' and '" + (source.Value<string>("y_column") ?? "y") + "'; header: " + string.Join(", ", header) + ").";

            // shared -> internal: undo the active project position (rotate back, subtract offsets).
            double angle = 0, eastWest = 0, northSouth = 0, elevation = 0;
            if (coordinates == "shared")
            {
                ProjectPosition position;
                try { position = doc.ActiveProjectLocation?.GetProjectPosition(XYZ.Zero); }
                catch { position = null; }
                if (position == null) return "coordinates=shared, but the active project position could not be read.";
                angle = position.Angle; eastWest = position.EastWest; northSouth = position.NorthSouth;
                elevation = position.Elevation;
            }

            var culture = decimalSeparator == "."
                ? System.Globalization.CultureInfo.InvariantCulture
                : System.Globalization.CultureInfo.GetCultureInfo("es-ES");
            elements = new JArray();
            for (int r = 1; r < rows.Count; r++)
            {
                string[] row = rows[r];
                double x, y, z = 0, rotation = 0;
                if (row.Length <= Math.Max(xColumn, yColumn) ||
                    !double.TryParse(row[xColumn], System.Globalization.NumberStyles.Float, culture, out x) ||
                    !double.TryParse(row[yColumn], System.Globalization.NumberStyles.Float, culture, out y))
                    return "row " + (r + 1) + " (cells: " + string.Join(" | ", row) + ") has no numeric x/y under " +
                           "the declared separator '" + decimalSeparator + "'.";
                if (zColumn >= 0 && row.Length > zColumn &&
                    !double.TryParse(row[zColumn], System.Globalization.NumberStyles.Float, culture, out z))
                    return "row " + (r + 1) + " (cells: " + string.Join(" | ", row) + "): the z value is not " +
                           "numeric under the declared separator '" + decimalSeparator + "'.";
                if (rotationColumn >= 0 && row.Length > rotationColumn && row[rotationColumn].Trim().Length > 0 &&
                    !double.TryParse(row[rotationColumn], System.Globalization.NumberStyles.Float, culture, out rotation))
                    return "row " + (r + 1) + ": the rotation value is not numeric under the declared separator.";
                if (coordinates == "shared")
                {
                    // The file speaks survey (E, N, Elev) in the REQUEST's units; the
                    // API's offsets are feet. ProjectPosition: shared = R(angle) *
                    // internal + T, so internal = R(-angle) * (shared - T) - computed
                    // entirely in the request's units (offsets divided by scale).
                    double cos = Math.Cos(-angle), sin = Math.Sin(-angle);
                    double relativeEast = x - eastWest / scale;
                    double relativeNorth = y - northSouth / scale;
                    x = relativeEast * cos - relativeNorth * sin;
                    y = relativeEast * sin + relativeNorth * cos;
                    z = z - elevation / scale;
                }
                var entry = new JObject
                {
                    ["kind"] = "family_instance",
                    ["type_id"] = typeId,
                    ["level_id"] = levelId,
                    ["point"] = new JArray(x, y, z),
                    ["source_row"] = r + 1
                };
                if (Math.Abs(rotation) > 1e-9) entry["rotation_degrees"] = rotation;
                elements.Add(entry);
            }
            provenance = new JObject
            {
                ["path"] = path, ["sha256"] = sha, ["data_rows"] = rows.Count - 1,
                ["coordinates"] = coordinates, ["decimal_separator"] = decimalSeparator,
                ["note"] = "each element row carries source_row; the file was expanded on THIS call, so a file " +
                           "edited between rehearsal and apply resolves a different plan and the token refuses stale."
            };
            return null;
        }

        private static Plan PlanItem(Document doc, int index, JObject item, double scale, out string error,
                                     out string unsupportedReason)
        {
            error = null; unsupportedReason = null;
            string kind = (item.Value<string>("kind") ?? "").ToLowerInvariant();
            var p = new Plan { Index = index, Kind = kind, Input = item, Scale = scale };
            try
            {
                switch (kind)
                {
                    case "level":
                        if (item["elevation"] == null) throw new ArgumentException("elevation is required");
                        p.Elevation = Finite(item.Value<double>("elevation"), "elevation") * scale;
                        // DECLARED, so it is re-read after the commit like every
                        // other identity. This used to set the name straight off the
                        // raw request inside Create and record nothing, so the row
                        // came back verified with no identity block at all - and an
                        // untrimmed name meant a later rule naming that storey
                        // failed with level_not_found against a level the caller
                        // believed it had just made.
                        p.WantName = Trimmed(item, "name");
                        break;
                    case "grid":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true);
                        NonZero(p.Start, p.End);
                        p.WantName = Trimmed(item, "name");
                        break;
                    case "wall":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        ReadArc(item["arc"], p, scale);
                        p.Structural = item.Value<bool?>("structural");
                        p.Level = Need<Level>(doc, item, "level_id");
                        p.Type = Optional<WallType>(doc, item, "type_id") ??
                            new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                                .FirstOrDefault(w => w.Kind == WallKind.Basic);
                        if (p.Type == null) throw new ArgumentException("type_id was omitted and the document has no Basic WallType default");
                        if (item["height"] == null) throw new ArgumentException("height is required for wall");
                        p.Height = Finite(item.Value<double>("height"), "height") * scale;
                        if (p.Height <= 0) throw new ArgumentException("height must be positive");
                        p.Offset = Finite(item.Value<double?>("offset") ?? 0, "offset") * scale;
                        break;
                    case "floor":
                        p.Structural = item.Value<bool?>("structural");
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Optional<FloorType>(doc, item, "type_id");
                        if (p.Type == null) p.Type = doc.GetElement(Floor.GetDefaultFloorType(doc, false)) as FloorType;
                        if (p.Type == null) throw new ArgumentException("type_id was omitted and Revit reports no default architectural FloorType");
                        p.Loops = Loops(item["profile"] as JArray, scale);
                        RequireHorizontal(p.Loops, "floor");
                        break;
                    case "ceiling":
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Optional<CeilingType>(doc, item, "type_id");
                        if (p.Type == null) p.Type = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<CeilingType>().FirstOrDefault();
                        if (p.Type == null) throw new ArgumentException("type_id was omitted and the document has no CeilingType");
                        p.Loops = Loops(item["profile"] as JArray, scale);
                        RequireHorizontal(p.Loops, "ceiling");
                        break;
                    case "roof":
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Optional<RoofType>(doc, item, "type_id");
                        if (p.Type == null) p.Type = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault();
                        if (p.Type == null) throw new ArgumentException("type_id was omitted and the document has no RoofType");
                        p.Loops = Loops(item["profile"] as JArray, scale);
                        RequireHorizontal(p.Loops, "roof");
                        if (p.Loops.Count != 1) throw new ArgumentException("roof currently requires exactly one closed footprint loop");
                        p.SlopeRadians = Finite(item.Value<double?>("slope_degrees") ?? 0, "slope_degrees") * Math.PI / 180.0;
                        if (p.SlopeRadians < 0 || p.SlopeRadians >= Math.PI / 2)
                            throw new ArgumentException("slope_degrees must be at least 0 and less than 90");
                        break;
                    case "room":
                        p.Level = Need<Level>(doc, item, "level_id"); p.Start = Point(item["point"], scale, false);
                        // A DRAWING CANNOT SUPPLY THESE. Text is unreachable from
                        // imported DWG geometry, so a room's name and number come
                        // from whoever wrote the requirement set, or from nowhere.
                        p.WantName = Trimmed(item, "name");
                        p.WantNumber = Trimmed(item, "number");
                        break;
                    case "family_instance":
                        p.Type = Need<FamilySymbol>(doc, item, "type_id"); p.Start = Point(item["point"], scale, true);
                        p.Level = Optional<Level>(doc, item, "level_id");
                        // Hosted placement: the host is resolved NOW and re-read from the
                        // created instance after commit (host_verified). A symbol whose
                        // family cannot live on this host fails Revit's own placement,
                        // atomically, with the batch rolled back.
                        p.InstanceHost = Optional<Element>(doc, item, "host_id");
                        p.RotationRadians = (item.Value<double?>("rotation_degrees") ?? 0) * System.Math.PI / 180.0;
                        StructuralType parsed;
                        if (!Enum.TryParse(item.Value<string>("structural_type") ?? "NonStructural", true, out parsed) ||
                            !Enum.IsDefined(typeof(StructuralType), parsed))
                            throw new ArgumentException("structural_type is invalid");
                        p.StructuralType = parsed;
                        break;
                    case "duct":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Diameter = ReadDiameter(item, scale);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Need<DuctType>(doc, item, "type_id");
                        p.SystemType = Need<MechanicalSystemType>(doc, item, "system_type_id");
                        break;
                    case "pipe":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Diameter = ReadDiameter(item, scale);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Need<PipeType>(doc, item, "type_id");
                        p.SystemType = Need<PipingSystemType>(doc, item, "system_type_id");
                        break;
                    case "conduit":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Diameter = ReadDiameter(item, scale);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Need<ConduitType>(doc, item, "type_id");
                        break;
                    case "cable_tray":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Diameter = ReadDiameter(item, scale);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Optional<CableTrayType>(doc, item, "type_id");
                        if (p.Type == null) p.Type = new FilteredElementCollector(doc).OfClass(typeof(CableTrayType)).Cast<CableTrayType>().FirstOrDefault();
                        if (p.Type == null) throw new ArgumentException("type_id was omitted and the document has no CableTrayType");
                        break;
                    case "structural_framing":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Need<FamilySymbol>(doc, item, "type_id");
                        if (!InCategory(p.Type, BuiltInCategory.OST_StructuralFraming))
                            throw new ArgumentException("structural_framing type_id must identify a FamilySymbol in OST_StructuralFraming");
                        if (!Enum.TryParse(item.Value<string>("structural_type") ?? "Beam", true, out StructuralType framingType) ||
                            (framingType != StructuralType.Beam && framingType != StructuralType.Brace))
                            throw new ArgumentException("structural_framing structural_type must be Beam or Brace");
                        p.StructuralType = framingType;
                        break;
                    case "structural_column":
                        p.Start = Point(item["point"], scale, true); p.Level = Need<Level>(doc, item, "level_id");
                        p.Type = Need<FamilySymbol>(doc, item, "type_id"); p.StructuralType = StructuralType.Column;
                        if (!InCategory(p.Type, BuiltInCategory.OST_StructuralColumns))
                            throw new ArgumentException("structural_column type_id must identify a FamilySymbol in OST_StructuralColumns");
                        break;
                    case "accessory_inline":
                    {
                        // A valve/accessory INSIDE a run: the host pipe is broken at the
                        // point, the symbol placed there, and BOTH freshly opened ends
                        // connected to the accessory's two ends - all in one transaction,
                        // verified inside it, rolled back whole if any connection fails.
                        Element inlineHost = Optional<Element>(doc, item, "pipe_id");
                        if (!(inlineHost is MEPCurve inlineCurve) || !(inlineHost is Autodesk.Revit.DB.Plumbing.Pipe))
                            throw new ArgumentException("pipe_id must identify a Pipe (the run the accessory sits in); " +
                                "duct accessories are a later subset - the break API differs");
                        p.InlineHost = inlineCurve;
                        p.Type = Need<FamilySymbol>(doc, item, "type_id");
                        p.InlinePoint = Point(item["point"], scale, true);
                        Curve inlineLocation = (inlineCurve.Location as LocationCurve)?.Curve;
                        if (inlineLocation == null)
                            throw new ArgumentException("the pipe has no location curve");
                        double distanceToRun = inlineLocation.Distance(p.InlinePoint);
                        if (distanceToRun > MepRules.CoincidenceToleranceFeet * 10)
                            throw new ArgumentException("the point is " + MepRules.Mm(distanceToRun) +
                                " off the pipe's axis; an inline accessory sits ON the run.");
                        double fromStart = inlineLocation.Project(p.InlinePoint).Parameter;
                        double endDistance = Math.Min(
                            p.InlinePoint.DistanceTo(inlineLocation.GetEndPoint(0)),
                            p.InlinePoint.DistanceTo(inlineLocation.GetEndPoint(1)));
                        if (endDistance < 300 / 304.8)
                            throw new ArgumentException("the point is " + MepRules.Mm(endDistance) + " from a pipe " +
                                "end; the break needs at least 300 mm of run on each side.");
                        p.ExtraPlanFacts = new Dictionary<string, string>
                        {
                            { "inline.pipe", SafePlanUid(inlineHost) },
                            { "inline.point", Canon01(p.InlinePoint) },
                            { "inline.type", SafePlanUid(p.Type) }
                        };
                        break;
                    }
                    case "mep_system":
                    {
                        // A NAMED MEP system of a given system type, and the elements
                        // that belong to it. Revit builds systems from connectivity;
                        // this is the explicit route - PipingSystem/MechanicalSystem
                        // Create, then Add the members' connectors - for the case where
                        // a deliverable needs a system that exists and is named BEFORE
                        // anything is routed into it.
                        p.SystemType = Need<Element>(doc, item, "system_type_id");
                        if (!(p.SystemType is Autodesk.Revit.DB.Plumbing.PipingSystemType) &&
                            !(p.SystemType is Autodesk.Revit.DB.Mechanical.MechanicalSystemType))
                            throw new ArgumentException("system_type_id must identify a PipingSystemType or a " +
                                "MechanicalSystemType; those are the two domains Revit can create a system in.");
                        p.SystemName = item.Value<string>("name");
                        if (string.IsNullOrWhiteSpace(p.SystemName))
                            throw new ArgumentException("name is required for mep_system: an unnamed system is " +
                                "indistinguishable from the ones Revit invents from connectivity.");
                        if (p.SystemName.Length > 200) throw new ArgumentException("name exceeds 200 characters");
                        p.SystemMembers = new List<Element>();
                        p.SystemMemberConnectors = new List<int>();
                        var memberToken = item["member_element_ids"] as JArray;
                        bool wantsPipe = p.SystemType is Autodesk.Revit.DB.Plumbing.PipingSystemType;
                        // What the system type itself says it carries. Members must agree.
                        string systemClassification = null;
                        try { systemClassification = (p.SystemType as MEPSystemType)?.SystemClassification.ToString(); }
                        catch { }
                        if (memberToken != null)
                        {
                            if (memberToken.Count > 500)
                                throw new ArgumentException("member_element_ids exceeds 500 entries");
                            var seen = new HashSet<long>();
                            foreach (JToken tok in memberToken)
                            {
                                if (tok.Type != JTokenType.Integer)
                                    throw new ArgumentException("member_element_ids entries must be element ids");
                                long raw = tok.Value<long>();
                                if (!seen.Add(raw))
                                    throw new ArgumentException("member_element_ids repeats id " + raw +
                                        "; a member belongs to the system once.");
                                if (!Rid.CanRepresentElementId(raw)) throw new ArgumentException(Rid.ElementIdRangeError(raw));
                                Element member = doc.GetElement(Rid.ToElementId(raw));
                                if (member == null) throw new ArgumentException("member element " + raw + " does not exist");
                                ConnectorManager memberManager = MepFacts.ManagerOf(member);
                                if (memberManager == null)
                                    throw new ArgumentException("member element " + raw + " (" +
                                        (member.Category?.Name ?? member.GetType().Name) + ") exposes no connectors; " +
                                        "membership of an MEP system is carried by a connector, so this element " +
                                        "cannot be a member of one.");
                                // The domain has to agree, or Revit accepts the Add and the
                                // system is quietly wrong - the failure this refuses to make.
                                // And the connector must be FREE: MEASURED on run 22, Revit
                                // answers 'Some connectors to be added into the system have
                                // been used' MID-TRANSACTION when a connector already belongs
                                // to a system - and a curve created with a system type is
                                // already in the one Revit made for it. Connector.MEPSystem
                                // is readable NOW, so that refusal happens NOW, by name.
                                bool domainOk = false;
                                Connector freeConnector = null;
                                string occupiedBy = null;
                                string unclassified = null;
                                string mismatched = null;
                                foreach (Connector candidate in MepFacts.Ordered(memberManager))
                                {
                                    bool isPipeDomain = candidate.Domain == Domain.DomainPiping;
                                    bool isHvacDomain = candidate.Domain == Domain.DomainHvac;
                                    if (!((wantsPipe && isPipeDomain) || (!wantsPipe && isHvacDomain))) continue;
                                    domainOk = true;
                                    MEPSystem owning = null;
                                    try { owning = candidate.MEPSystem; } catch { }
                                    if (owning != null)
                                    {
                                        if (occupiedBy == null)
                                        {
                                            string owningName = null;
                                            try { owningName = owning.Name; } catch { }
                                            occupiedBy = "'" + (owningName ?? "(unnamed)") + "' (id " + Rid.Value(owning.Id) + ")";
                                        }
                                        continue;
                                    }
                                    // MEASURED on run 23: a connector whose system
                                    // classification is Fitting/Undefined is accepted by
                                    // MEPSystem.Add WITHOUT THROWING and associates
                                    // NOTHING - the system commits carrying nobody. The
                                    // classification is readable now, so this refuses now.
                                    string classification = SafeConnectorClassification(candidate, wantsPipe);
                                    if (IsUnusableClassification(classification))
                                    {
                                        if (unclassified == null) unclassified = classification ?? "(unreadable)";
                                        continue;
                                    }
                                    // And it has to be the SAME classification the system
                                    // type declares. MEASURED on run 24: Revit answers
                                    // "Some connectors can't match system with domain,
                                    // system type or direction" mid-transaction otherwise.
                                    // Both enums spell these identically, so the comparison
                                    // is exact rather than a mapping table nobody maintains.
                                    if (systemClassification != null &&
                                        !string.Equals(classification, systemClassification, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (mismatched == null) mismatched = classification;
                                        continue;
                                    }
                                    freeConnector = candidate;
                                    break;
                                }
                                if (!domainOk)
                                    throw new ArgumentException("member element " + raw + " has no " +
                                        (wantsPipe ? "piping" : "HVAC") + " connector, and the system type you named is " +
                                        (wantsPipe ? "a PipingSystemType" : "a MechanicalSystemType") +
                                        ". A system whose members are in another domain is not a system.");
                                if (freeConnector == null && occupiedBy != null)
                                    throw new ArgumentException("member_connector_already_in_a_system: every " +
                                        (wantsPipe ? "piping" : "HVAC") + " connector of element " + raw +
                                        " already belongs to system " + occupiedBy + ". Revit keeps a connector in " +
                                        "ONE system, so this element cannot join a second one - and a curve created " +
                                        "with a system type is already in the system Revit made for it. Name members " +
                                        "whose connectors are free, or build the system from equipment connectors.");
                                if (freeConnector == null && mismatched != null)
                                    throw new ArgumentException("member_classification_does_not_match_system: element " +
                                        raw + "'s free connector carries system type '" + mismatched + "' while the " +
                                        "system type you named carries '" + systemClassification + "'. Revit refuses the " +
                                        "mismatch mid-transaction (\"Some connectors can't match system with domain, " +
                                        "system type or direction\"); this reads both classifications first. Name a " +
                                        "system type of the same classification, or members that declare it.");
                                if (freeConnector == null)
                                    throw new ArgumentException("member_connector_has_no_system_classification: the free " +
                                        (wantsPipe ? "piping" : "HVAC") + " connector(s) of element " + raw +
                                        " carry system type '" + (unclassified ?? "(unreadable)") + "'. MEASURED: Revit " +
                                        "accepts such a connector into a system WITHOUT THROWING and associates NOTHING - " +
                                        "the system would commit carrying nobody. A member's connector must declare the " +
                                        "classification it belongs to (Domestic Cold Water, Supply Air, and so on), which " +
                                        "is set on the connector in the family.");
                                p.SystemMembers.Add(member);
                                p.SystemMemberConnectors.Add(freeConnector.Id);
                            }
                        }
                        p.ExtraPlanFacts = new Dictionary<string, string>
                        {
                            { "system.type", SafePlanUid(p.SystemType) },
                            { "system.name", p.SystemName },
                            { "system.members", string.Join(",", p.SystemMembers.Select(m => SafePlanUid(m))) }
                        };
                        break;
                    }
                    case "beam_system":
                    {
                        // A closed rectangular-or-polygonal loop of 3..12 points on one
                        // level, filled with beams along an explicit direction. The
                        // members are REAL framing Revit lays out; the verify re-reads
                        // their count from the committed system.
                        p.Level = Need<Level>(doc, item, "level_id");
                        var loopToken = item["profile"] as JArray;
                        if (loopToken == null || loopToken.Count < 3 || loopToken.Count > 12)
                            throw new ArgumentException("profile must carry 3..12 [x,y] points; they close automatically");
                        p.ProfilePoints = new List<XYZ>();
                        double z = p.Level.Elevation;
                        foreach (JToken pointToken in loopToken)
                        {
                            var xy = pointToken as JArray;
                            if (xy == null || xy.Count < 2) throw new ArgumentException("every profile point is [x, y]");
                            p.ProfilePoints.Add(new XYZ((double)xy[0] * scale, (double)xy[1] * scale, z));
                        }
                        for (int v = 0; v < p.ProfilePoints.Count; v++)
                        {
                            XYZ a = p.ProfilePoints[v], b = p.ProfilePoints[(v + 1) % p.ProfilePoints.Count];
                            if (a.DistanceTo(b) < 0.5)
                                throw new ArgumentException("profile edge " + v + " is under 150 mm; that is not a bay");
                        }
                        var directionToken = item["direction"] as JArray;
                        if (directionToken == null || directionToken.Count < 2)
                            throw new ArgumentException("direction is required: [x, y], the axis the beams run along");
                        p.BeamDirection = new XYZ((double)directionToken[0], (double)directionToken[1], 0);
                        if (p.BeamDirection.GetLength() < 1e-9) throw new ArgumentException("direction must not be zero");
                        p.BeamDirection = p.BeamDirection.Normalize();
                        p.BeamSpacing = (item.Value<double?>("spacing") ?? 0) * scale;
                        if (item["beam_type_id"] != null)
                        {
                            p.BeamType = Optional<Element>(doc, item, "beam_type_id") as FamilySymbol;
                            if (p.BeamType == null || p.BeamType.Category == null || Rid.Value(p.BeamType.Category.Id) != (long)BuiltInCategory.OST_StructuralFraming)
                                throw new ArgumentException("beam_type_id must identify a structural-framing FamilySymbol");
                        }
                        break;
                    }
                    case "wall_foundation":
                    {
                        p.FoundationWall = Need<Wall>(doc, item, "wall_id");
                        Element foundationType = Need<Element>(doc, item, "type_id");
                        if (!(foundationType is WallFoundationType))
                            throw new ArgumentException("type_id must identify a WallFoundationType (a bearing-footing type)");
                        p.Type = (ElementType)foundationType;
                        break;
                    }
                    case "shaft":
                    {
                        // A SHAFT IS NOT A HOLE IN A SLAB.
                        //
                        // MEASURED across 2023-2027: Revit has a dedicated route,
                        // NewOpening(bottomLevel, topLevel, profile), and it cuts
                        // EVERY floor, roof and ceiling the extent passes through -
                        // which is the whole difference. Building one slab opening
                        // per floor would leave a shaft that stops existing the day
                        // somebody adds a storey, and would be a different element
                        // in every schedule.
                        p.BaseLevel = Need<Level>(doc, item, "base_level_id");
                        p.TopLevel = Need<Level>(doc, item, "top_level_id");
                        if (p.BaseLevel.Id == p.TopLevel.Id)
                            throw new ArgumentException(
                                "shaft_zero_extent: base_level_id and top_level_id are the same level, so the " +
                                "shaft would have no height and cut nothing.");
                        if (p.TopLevel.Elevation <= p.BaseLevel.Elevation)
                            throw new ArgumentException(
                                "shaft_inverted: top level '" + Safe(() => p.TopLevel.Name) + "' sits at or below base " +
                                "level '" + Safe(() => p.BaseLevel.Name) + "'. A shaft runs upward.");
                        p.Loops = Loops(item["profile"] as JArray, scale);
                        if (p.Loops.Count != 1)
                            throw new ArgumentException(
                                "shaft_profile_invalid: a shaft takes exactly one closed loop and " +
                                p.Loops.Count + " were given. A shaft with a hole in it is two shafts.");
                        RequireHorizontal(p.Loops, "shaft");

                        // AND WHICH LOAD-BEARING FLOORS IT WOULD GO THROUGH.
                        //
                        // A shaft cuts EVERY slab between its two storeys, which is
                        // more structural floor than any single opening reaches -
                        // and it was the one kind of hole that cut them all with no
                        // opt-in at all, while an `opening` aimed at any ONE of
                        // those same slabs was refused without one. The permission
                        // this design calls "what a person accepts" was never asked
                        // for on the widest cut there is.
                        string shaftRefusal = StructuralSlabsInTheWay(
                            doc, p.Loops[0], p.BaseLevel, p.TopLevel,
                            item.Value<bool?>("allow_structural") == true);
                        if (shaftRefusal != null) throw new ArgumentException(shaftRefusal);
                        break;
                    }
                    case "room_separator":
                    {
                        // Room separation lines are MODEL curves that bound a room,
                        // not detail lines that look like they do. Revit takes them
                        // through a sketch plane and a VIEW - the view is not
                        // decoration, it is what the lines belong to.
                        p.Level = Need<Level>(doc, item, "level_id");
                        p.SeparatorView = Optional<View>(doc, item, "view_id");

                        // THE VIEW IS CHECKED HERE, IN THE REHEARSAL, and not where
                        // it is used. Revit does not check it at all: MEASURED,
                        // NewRoomBoundaryLines with a view whose storey is not the
                        // sketch plane's took the process down mid-transaction - no
                        // exception, no message, a closed pipe. A rehearsal that
                        // answered "valid" and then took Revit down is worse than
                        // one that refuses, so the refusal happens before anything
                        // is called valid.
                        var separatorView = p.SeparatorView as ViewPlan;
                        if (separatorView == null)
                            throw new ArgumentException(
                                "separator_view_invalid: a room separator is drawn through a PLAN VIEW of the " +
                                "storey it sits on, and view_id " +
                                (p.SeparatorView == null ? "was not given" : "names " + Safe(() => p.SeparatorView.Name)) +
                                ". Revit does not refuse the wrong view here - it stops.");
                        if (separatorView.IsTemplate)
                            throw new ArgumentException(
                                "separator_view_invalid: view_id names a view TEMPLATE, which nothing can be " +
                                "drawn in.");
                        Level separatorViewLevel = null;
                        try { separatorViewLevel = separatorView.GenLevel; } catch { }
                        if (separatorViewLevel == null || separatorViewLevel.Id != p.Level.Id)
                            throw new ArgumentException(
                                "separator_view_wrong_storey: the separator is drawn on '" +
                                Safe(() => p.Level.Name) + "' and view_id is a plan of '" +
                                (separatorViewLevel == null ? "(no storey)" : Safe(() => separatorViewLevel.Name)) +
                                "'. Revit does not refuse this - it stops.");

                        // A CHAIN, NOT A RING. This read the profile through
                        // Loops(), which CLOSES what it is given and demands three
                        // points - so the most ordinary separator there is, one
                        // line across a room, was refused as "every profile loop
                        // needs at least three points". The plan emitted exactly
                        // that shape, so a set producing separators could not be
                        // applied at all; and a three-point chain that DID get
                        // through was silently closed into a triangle nobody drew.
                        p.Chains = Chains(item["profile"] as JArray, scale);
                        if (p.Chains.Count < 1)
                            throw new ArgumentException("room_separator needs at least one chain of curves");
                        RequireHorizontalChains(p.Chains, "room_separator");
                        break;
                    }
                    case "slab_opening":
                    {
                        // A vertical opening cut through ONE floor, roof or ceiling:
                        // rectangular or circular, centred on an XY point. The same
                        // structural opt-in as wall_opening - a structural slab is
                        // somebody's engineering decision.
                        Element slabHost = Optional<Element>(doc, item, "host_id");
                        if (!(slabHost is Floor) && !(slabHost is RoofBase) && !(slabHost is Ceiling))
                            throw new ArgumentException("host_id must identify a Floor, Roof or Ceiling for a slab_opening");
                        p.SlabHost = slabHost;
                        p.SlabShape = (item.Value<string>("shape") ?? "rectangular").ToLowerInvariant();
                        if (p.SlabShape != "rectangular" && p.SlabShape != "circular")
                            throw new ArgumentException("shape must be rectangular or circular");
                        p.SlabCenter = Point(item["center"], scale, false);
                        if (p.SlabShape == "circular")
                        {
                            double diameter = (item.Value<double?>("diameter") ?? 0) * scale;
                            p.SlabWidth = diameter; p.SlabHeight = diameter;
                        }
                        else
                        {
                            p.SlabWidth = (item.Value<double?>("width") ?? 0) * scale;
                            p.SlabHeight = (item.Value<double?>("height") ?? 0) * scale;
                        }
                        string sizeReason;
                        if (!PenetrationRules.ValidateOpeningSize(p.SlabWidth, p.SlabHeight, out sizeReason))
                            throw new ArgumentException(sizeReason);
                        bool slabStructural = false;
                        try { slabStructural = slabHost.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1; }
                        catch { }
                        string slabCode, slabReason;
                        if (!PenetrationRules.HostPermitted(true, slabStructural,
                                item.Value<bool?>("allow_structural") == true, out slabCode, out slabReason))
                            throw new ArgumentException(slabCode + ": " + slabReason);
                        p.ExtraPlanFacts = new Dictionary<string, string>
                        {
                            { "opening.host_uid", SafePlanUid(slabHost) },
                            { "opening.shape", p.SlabShape },
                            { "opening.center", Canon01(p.SlabCenter) },
                            { "opening.size", System.Math.Round(p.SlabWidth * 304.8, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" +
                                              System.Math.Round(p.SlabHeight * 304.8, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                            { "opening.host_structural", slabStructural ? "true" : "false" }
                        };
                        break;
                    }
                    case "wall_opening":
                    {
                        // A rectangular opening cut into ONE wall between two diagonal
                        // corners. The structural gate is an opt-in per row: cutting a
                        // bearing wall is an engineering decision, and the argument is
                        // the record that a person made it.
                        Wall openingHost = Need<Wall>(doc, item, "host_id");
                        p.Type = null;
                        p.Start = Point(item["corner_1"], scale, true);
                        p.End = Point(item["corner_2"], scale, true);
                        NonZero(p.Start, p.End);
                        string hostCode, hostReason;
                        bool hostIsStructural = false;
                        try { hostIsStructural = openingHost.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1; }
                        catch { }
                        if (!PenetrationRules.HostPermitted(true, hostIsStructural,
                                item.Value<bool?>("allow_structural") == true, out hostCode, out hostReason))
                            throw new ArgumentException(hostCode + ": " + hostReason);
                        // AND IT HAS TO BE ON THAT WALL.
                        //
                        // NewOpening PROJECTS the two corners onto the host, so a
                        // rectangle drawn past the end of a wall does not fail - it
                        // slides to the end and cuts a hole somewhere nobody drew.
                        // A conversion resolves its own host from a drawing, so this
                        // is the ordinary mistake, not an exotic one: a ring over the
                        // gap between two walls resolves to the nearer of them and
                        // then gets cut into it.
                        string overshootNotChecked;
                        string overshoot = OffTheWall(openingHost, p.Start, p.End, out overshootNotChecked);
                        if (overshoot != null) throw new ArgumentException(overshoot);

                        // AND IT HAS TO HAVE A WIDTH ALONG THAT WALL.
                        //
                        // Two corners that project to the same point on the host cut
                        // nothing, and everything downstream agrees they did: the
                        // element exists, its category is right, its host is right,
                        // and the wall's volume never moves. This is reachable from a
                        // direct call as well as from a conversion, which is why the
                        // check lives here and not only in the plan.
                        string noWidth = NoWidthAlongHost(openingHost, p.Start, p.End);
                        if (noWidth != null) throw new ArgumentException(noWidth);

                        p.OpeningHost = openingHost;
                        p.ExtraPlanFacts = new Dictionary<string, string>
                        {
                            { "opening.host_uid", SafePlanUid(openingHost) },
                            { "opening.corners", Canon01(p.Start) + ";" + Canon01(p.End) },
                            { "opening.host_structural", hostIsStructural ? "true" : "false" }
                        };
                        if (overshootNotChecked != null)
                            p.ExtraPlanFacts["opening.overshoot_not_checked"] = overshootNotChecked;
                        break;
                    }
                    case "fitting":
                    {
                        // A fitting joins OPEN connectors that MEET. The choice of pair is
                        // deterministic or it is a refusal (MepRules); the resolved facts
                        // are frozen into the plan so pipes moved after the rehearsal
                        // refuse as stale instead of putting the fitting somewhere else.
                        string subtype = (item.Value<string>("fitting") ?? "").ToLowerInvariant();
                        if (subtype != "elbow" && subtype != "union" && subtype != "transition" && subtype != "tee" &&
                            subtype != "takeoff")
                            throw new ArgumentException("fitting must name one of: elbow, union, transition, tee, takeoff");
                        p.FittingSubtype = subtype;
                        int need = subtype == "tee" ? 3 : 2;
                        var membersToken = item["elements"] as JArray;
                        if (membersToken == null || membersToken.Count != need)
                            throw new ArgumentException("elements must contain exactly " + need + " entries for a " + subtype +
                                (subtype == "tee" ? " (the two through-run elements, then the branch)" :
                                 subtype == "takeoff" ? " (the branch whose open connector taps in, then the main curve)" : ""));
                        var members = new List<FittingMember>();
                        var sides = new List<List<ConnectorFact>>();
                        var named = new List<int?>();
                        bool anyBatchRef = false;
                        for (int m = 0; m < membersToken.Count; m++)
                        {
                            if (!(membersToken[m] is JObject member))
                                throw new ArgumentException("elements[" + m + "] is not an object");
                            int? batchIndex = member.Value<int?>("batch_index");
                            bool isTakeoffMain = subtype == "takeoff" && m == 1;
                            if (batchIndex != null)
                            {
                                // A member created EARLIER IN THIS BATCH. It has no geometry
                                // yet, so the pair selection is DEFERRED to create time -
                                // inside the atomic transaction, where a refusal still rolls
                                // the whole batch back and nothing partial survives.
                                if (member["element_id"] != null)
                                    throw new ArgumentException("elements[" + m + "]: give element_id OR batch_index, not both");
                                if (batchIndex.Value < 0 || batchIndex.Value >= index)
                                    throw new ArgumentException("elements[" + m + "].batch_index must reference an EARLIER entry of this batch (0.." + (index - 1) + ")");
                                anyBatchRef = true;
                                if (isTakeoffMain) { p.TakeoffMainBatchIndex = batchIndex; }
                                else
                                {
                                    members.Add(new FittingMember { BatchIndex = batchIndex, NamedConnector = member.Value<int?>("connector") });
                                    sides.Add(null); named.Add(member.Value<int?>("connector"));
                                }
                                continue;
                            }
                            long rawId = member.Value<long?>("element_id") ?? -1;
                            Element owner = Rid.CanRepresent(rawId) ? doc.GetElement(Rid.Make(rawId)) : null;
                            if (owner == null)
                                throw new ArgumentException("elements[" + m + "].element_id does not identify an element");
                            if (isTakeoffMain)
                            {
                                if (!(owner is MEPCurve))
                                    throw new ArgumentException("elements[1] of a takeoff must be an MEP curve (the main the branch taps into)");
                                p.TakeoffMain = owner;
                                continue;
                            }
                            ConnectorManager manager = MepFacts.ManagerOf(owner);
                            if (manager == null)
                                throw new ArgumentException("elements[" + m + "] (" + rawId + ") has no connectors; a fitting joins MEP curves or connectable family instances");
                            var facts = new List<ConnectorFact>();
                            foreach (Connector connector in MepFacts.Ordered(manager)) facts.Add(MepFacts.FactOf(connector));
                            sides.Add(facts);
                            named.Add(member.Value<int?>("connector"));
                            members.Add(new FittingMember { Owner = owner, NamedConnector = member.Value<int?>("connector") });
                        }
                        if (subtype == "takeoff" && p.TakeoffMain == null && p.TakeoffMainBatchIndex == null)
                            throw new ArgumentException("a takeoff needs its main curve as elements[1]");
                        if (subtype == "takeoff" && p.TakeoffMain is MEPCurve planMain && members.Count == 1 &&
                            members[0].Owner != null)
                        {
                            // Both sides exist NOW, so the distance is measurable NOW:
                            // a branch that does not touch the main must refuse in the
                            // rehearsal, not discover it inside the transaction.
                            Curve mainCurve = (planMain.Location as LocationCurve)?.Curve;
                            ConnectorManager branchManager = MepFacts.ManagerOf(members[0].Owner);
                            double best = double.MaxValue; int bestId = -1;
                            if (mainCurve != null && branchManager != null)
                                foreach (Connector candidate in MepFacts.Ordered(branchManager))
                                {
                                    if (candidate.IsConnected) continue;
                                    if (members[0].NamedConnector != null && candidate.Id != members[0].NamedConnector.Value) continue;
                                    double distance = mainCurve.Distance(candidate.Origin);
                                    if (distance < best) { best = distance; bestId = candidate.Id; }
                                }
                            if (bestId < 0)
                                throw new ArgumentException("the takeoff branch has no eligible OPEN connector");
                            if (best > MepRules.CoincidenceToleranceFeet * 10)
                                throw new ArgumentException("the branch's open connector is " + MepRules.Mm(best) +
                                    " from the main curve; a takeoff taps a connector that TOUCHES the main (within " +
                                    MepRules.Mm(MepRules.CoincidenceToleranceFeet * 10) + ").");
                            members[0].ConnectorId = bestId;
                            // MEASURED on run 11: NewTakeoffFitting throws 'No routing
                            // preference for takeoff set for this PipeType' MID-TRANSACTION.
                            // The preference is readable NOW, so the refusal happens NOW.
                            // Run 12 measured the finer truth: junction RULES can exist
                            // while the junction PREFERENCE is Tee - and NewTakeoffFitting
                            // still throws. The preference itself is the readable fact.
                            // The preference lives on MEPCurveType, so a DUCT main is read
                            // the same way a pipe one is - the refusal was pipe-only while
                            // the API never was.
                            MEPCurveType mainCurveType = (planMain as MEPCurve)?.GetTypeId() is ElementId mainTypeId
                                ? doc.GetElement(mainTypeId) as MEPCurveType : null;
                            if (mainCurveType?.RoutingPreferenceManager is RoutingPreferenceManager routing &&
                                (routing.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions) == 0 ||
                                 routing.PreferredJunctionType != PreferredJunctionType.Tap))
                                throw new ArgumentException("curve_type_has_no_takeoff_preference: " +
                                    mainCurveType.GetType().Name + " '" + mainCurveType.Name + "' prefers junction type '" +
                                    (routing.PreferredJunctionType.ToString() ?? "(unreadable)") +
                                    "' and NewTakeoffFitting needs Tap with a configured rule. Set the type's " +
                                    "junction preference to Tap (with its fitting) or use a type that has it. Nothing ran.");
                        }
                        if (!anyBatchRef && subtype != "takeoff")
                        {
                            ConnectorFact chosenA, chosenB; string pairCode, pairReason;
                            if (!MepRules.SelectPair(sides[0], sides[1], named[0], named[1],
                                                     out chosenA, out chosenB, out pairCode, out pairReason))
                                throw new ArgumentException(pairCode + ": " + pairReason);
                            members[0].ConnectorId = chosenA.Id; members[0].Fact = chosenA;
                            members[1].ConnectorId = chosenB.Id; members[1].Fact = chosenB;
                            double turn = MepRules.AngleDegrees(chosenA, chosenB);
                            if (subtype == "elbow" && turn <= 1.0)
                                throw new ArgumentException("the chosen connectors are collinear (turn " +
                                    turn.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                                    " degrees); an elbow needs a corner - use fitting 'union' (same size) or 'transition'.");
                            if ((subtype == "union" || subtype == "transition") && turn > 1.0)
                                throw new ArgumentException("the chosen connectors turn " +
                                    turn.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                                    " degrees; a " + subtype + " joins collinear runs - use fitting 'elbow'.");
                            if (subtype == "tee")
                            {
                                ConnectorFact branch, unusedCorner; string branchCode, branchReason;
                                if (!MepRules.SelectPair(sides[2], new List<ConnectorFact> { chosenA }, named[2], null,
                                                         out branch, out unusedCorner, out branchCode, out branchReason))
                                    throw new ArgumentException(branchCode + " (branch): " + branchReason);
                                members[2].ConnectorId = branch.Id; members[2].Fact = branch;
                            }
                        }
                        p.FittingMembers = members;
                        p.ExtraPlanFacts = new Dictionary<string, string> { { "fitting.subtype", subtype } };
                        for (int m = 0; m < members.Count; m++)
                        {
                            FittingMember memberPlan = members[m];
                            if (memberPlan.BatchIndex != null)
                            {
                                // The batch entry IS the fact: a different earlier entry is a
                                // different plan, and the entry's own fields are already hashed.
                                p.ExtraPlanFacts["fitting." + m] = "batch:" +
                                    memberPlan.BatchIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                continue;
                            }
                            ConnectorFact fact = memberPlan.Fact;
                            if (fact == null)
                            {
                                p.ExtraPlanFacts["fitting." + m] = SafePlanUid(memberPlan.Owner) + "|deferred";
                                continue;
                            }
                            p.ExtraPlanFacts["fitting." + m] = SafePlanUid(memberPlan.Owner) + "|" +
                                fact.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                                System.Math.Round(fact.X * 304.8, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                                System.Math.Round(fact.Y * 304.8, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                                System.Math.Round(fact.Z * 304.8, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }
                        if (p.TakeoffMain != null)
                            p.ExtraPlanFacts["fitting.main"] = SafePlanUid(p.TakeoffMain);
                        else if (p.TakeoffMainBatchIndex != null)
                            p.ExtraPlanFacts["fitting.main"] = "batch:" +
                                p.TakeoffMainBatchIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    }
                    default: throw new UnsupportedCapability(
                        "unsupported kind '" + kind + "' - horizun_create_elements implements a fixed set of " +
                        "element kinds and this is not one of them. Nothing was created.",
                        FallbackSignal.ReasonUnsupportedKind);
                }
                p.Summary = new JObject { ["index"] = index, ["kind"] = kind, ["references_resolved"] = true };
                if (p.FittingMembers != null)
                {
                    // Deferred members (batch_index refs, and a takeoff's branch) have no
                    // owner or chosen connector YET - their selection happens inside the
                    // transaction. The summary says "deferred", never dereferences a null.
                    var chosen = new JArray();
                    foreach (FittingMember member in p.FittingMembers)
                        chosen.Add(member.Owner == null || member.Fact == null
                            ? new JObject
                            {
                                ["element_id"] = member.Owner == null ? null : (JToken)Rid.Value(member.Owner.Id),
                                ["batch_index"] = member.BatchIndex,
                                ["selection"] = "deferred_to_transaction"
                            }
                            : new JObject
                            {
                                ["element_id"] = Rid.Value(member.Owner.Id),
                                ["connector"] = member.ConnectorId,
                                ["domain"] = member.Fact.Domain
                            });
                    p.Summary["fitting"] = p.FittingSubtype;
                    p.Summary["chosen_connectors"] = chosen;
                }
                return p;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return null;
            }
        }

        private static Element Create(Document doc, Plan p, List<Created> createdSoFar)
        {
            switch (p.Kind)
            {
                case "level":
                    Level level = Level.Create(doc, p.Elevation);
                    SetIdentity(level, BuiltInParameter.DATUM_TEXT, p.WantName, "name");
                    return level;
                case "grid":
                    Grid grid = Grid.Create(doc, Line.CreateBound(p.Start, p.End));
                    if (!string.IsNullOrWhiteSpace(p.WantName))
                    {
                        // A DUPLICATE GRID NAME THROWS, and that is the right
                        // answer: two grids called 'A' is a drawing nobody can
                        // dimension from. The batch rolls back and says which
                        // name it was.
                        try { grid.Name = p.WantName; }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                "the grid was created and Revit refused the name '" + p.WantName + "': " +
                                ex.Message + ". A grid name must be unique in the document, so this is " +
                                "usually a name the model already has. Nothing was kept.");
                        }
                    }
                    return grid;
                case "wall":
                    // ONE curved wall, not one straight wall per chord. Arc.Create
                    // takes three points on the curve, which is why the plan derived
                    // a third from the declared centre, radius and winding rather
                    // than trusting a chord midpoint - a chord's midpoint is INSIDE
                    // the arc, and a wall through it is a different wall.
                    Curve wallAxis = p.ArcThird == null
                        ? (Curve)Line.CreateBound(p.Start, p.End)
                        : Arc.Create(p.Start, p.End, p.ArcThird);
                    return Wall.Create(doc, wallAxis, p.Type.Id, p.Level.Id, p.Height,
                        p.Offset,
                        p.Input.Value<bool?>("flip") == true, p.Structural == true);
                case "floor":
                    Floor madeFloor = Floor.Create(doc, p.Loops, p.Type.Id, p.Level.Id);
                    // THE PARAMETER, not an overload. Floor.Create's structural
                    // argument is not present in every Revit this bridge builds
                    // against; FLOOR_PARAM_IS_STRUCTURAL is, and it is the thing
                    // the verification re-reads either way.
                    if (p.Structural.HasValue)
                    {
                        Parameter structuralParam = madeFloor?.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
                        if (structuralParam == null || structuralParam.IsReadOnly)
                            throw new InvalidOperationException(
                                "this floor type does not carry a structural parameter that can be set; the row " +
                                "asked for structural and nothing would have been changed");
                        structuralParam.Set(p.Structural.Value ? 1 : 0);
                    }
                    return madeFloor;
                case "ceiling":
                    return Ceiling.Create(doc, p.Loops, p.Type.Id, p.Level.Id);
                case "roof":
                    var footprint = new CurveArray();
                    foreach (Curve curve in p.Loops[0]) footprint.Append(curve);
                    ModelCurveArray boundaries;
                    FootPrintRoof roof = doc.Create.NewFootPrintRoof(footprint, p.Level, (RoofType)p.Type, out boundaries);
                    foreach (ModelCurve edge in boundaries)
                    {
                        bool definesSlope = p.SlopeRadians > 0;
                        roof.set_DefinesSlope(edge, definesSlope);
                        if (definesSlope) roof.set_SlopeAngle(edge, p.SlopeRadians);
                    }
                    return roof;
                case "room":
                {
                    Room room = doc.Create.NewRoom(p.Level, new UV(p.Start.X, p.Start.Y));
                    if (room == null)
                        throw new InvalidOperationException(
                            "Revit placed no room at that point. A room needs a CLOSED boundary around it, and " +
                            "a point inside one - an unenclosed point produces nothing rather than an error.");

                    // NAME AND NUMBER IN THE SAME TRANSACTION as the room itself.
                    // A room created here and named by a later call is a room that
                    // exists unnamed if the later call fails, and Revit hands out
                    // its own number the moment it is placed - so "Room 7" would
                    // be a number nobody chose.
                    SetIdentity(room, BuiltInParameter.ROOM_NAME, p.WantName, "name");
                    SetIdentity(room, BuiltInParameter.ROOM_NUMBER, p.WantNumber, "number");
                    return room;
                }
                case "family_instance":
                {
                    FamilySymbol symbol = (FamilySymbol)p.Type;
                    if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                    Element placedInstance;
                    if (p.InstanceHost != null)
                        placedInstance = p.Level == null
                            ? doc.Create.NewFamilyInstance(p.Start, symbol, p.InstanceHost, p.StructuralType)
                            : doc.Create.NewFamilyInstance(p.Start, symbol, p.InstanceHost, p.Level, p.StructuralType);
                    else
                        placedInstance = p.Level == null
                            ? doc.Create.NewFamilyInstance(p.Start, symbol, p.StructuralType)
                            : doc.Create.NewFamilyInstance(p.Start, symbol, p.Level, p.StructuralType);
                    if (System.Math.Abs(p.RotationRadians) > 1e-9)
                    {
                        Line axis = Line.CreateBound(p.Start, new XYZ(p.Start.X, p.Start.Y, p.Start.Z + 1));
                        ElementTransformUtils.RotateElement(doc, placedInstance.Id, axis, p.RotationRadians);
                    }
                    return placedInstance;
                }
                case "duct": return Duct.Create(doc, p.SystemType.Id, p.Type.Id, p.Level.Id, p.Start, p.End);
                case "pipe": return Pipe.Create(doc, p.SystemType.Id, p.Type.Id, p.Level.Id, p.Start, p.End);
                case "conduit": return Conduit.Create(doc, p.Type.Id, p.Start, p.End, p.Level.Id);
                case "cable_tray": return CableTray.Create(doc, p.Type.Id, p.Start, p.End, p.Level.Id);
                case "structural_framing":
                    FamilySymbol framing = (FamilySymbol)p.Type;
                    if (!framing.IsActive) { framing.Activate(); doc.Regenerate(); }
                    return doc.Create.NewFamilyInstance(Line.CreateBound(p.Start, p.End), framing, p.Level, p.StructuralType);
                case "structural_column":
                    FamilySymbol column = (FamilySymbol)p.Type;
                    if (!column.IsActive) { column.Activate(); doc.Regenerate(); }
                    return doc.Create.NewFamilyInstance(p.Start, column, p.Level, StructuralType.Column);
                case "wall_opening":
                    return doc.Create.NewOpening(p.OpeningHost, p.Start, p.End);
                case "accessory_inline":
                {
                    FamilySymbol accessorySymbol = (FamilySymbol)p.Type;
                    if (!accessorySymbol.IsActive) { accessorySymbol.Activate(); doc.Regenerate(); }
                    Curve runCurve = (p.InlineHost.Location as LocationCurve)?.Curve;
                    Line runLine = runCurve as Line;
                    if (runLine == null)
                        throw new InvalidOperationException("accessory_inline currently requires a straight Pipe; " +
                            "the connector gap on an arc cannot be cut safely by the straight-run algorithm");
                    XYZ runDirection = runLine.Direction;
                    XYZ originalStart = runLine.GetEndPoint(0);
                    XYZ originalEnd = runLine.GetEndPoint(1);
                    Element placed = doc.Create.NewFamilyInstance(p.InlinePoint, accessorySymbol,
                        p.InlineHost.ReferenceLevel, StructuralType.NonStructural);
                    // Rotate the accessory's local X onto the run before connecting.
                    double rotate = Math.Atan2(runDirection.Y, runDirection.X);
                    if (Math.Abs(rotate) > 1e-9)
                        ElementTransformUtils.RotateElement(doc, placed.Id,
                            Line.CreateBound(p.InlinePoint, p.InlinePoint + XYZ.BasisZ), rotate);
                    doc.Regenerate();
                    ConnectorManager accessoryManager = MepFacts.ManagerOf(placed);
                    if (accessoryManager == null)
                        throw new InvalidOperationException("the accessory type exposes no connectors; an inline " +
                            "valve needs two pipe connectors");

                    // A valve occupies a LENGTH, not a mathematical point.  Breaking
                    // once at the insertion point leaves both pipe ends at the centre
                    // while the family connectors sit apart; Revit 2023 can accept
                    // ConnectTo transiently and then commit them open.  Project the two
                    // real connector origins onto the run, cut BOTH ends of that gap,
                    // delete the captured middle, and join the surviving outer halves.
                    var accessoryEnds = new List<Connector>();
                    foreach (Connector connector in MepFacts.Ordered(accessoryManager))
                        if (connector.Domain == Domain.DomainPiping) accessoryEnds.Add(connector);
                    if (accessoryEnds.Count != 2)
                        throw new InvalidOperationException("the accessory exposes " + accessoryEnds.Count +
                            " piping connectors; an inline valve needs exactly two");

                    // Family origins are not guaranteed to sit on the connector
                    // axis.  The authored live fixture is deliberately asymmetric
                    // in Z and measured this: its two connectors were both 30 mm
                    // above the insertion point. Seat their REAL midpoint on the
                    // requested pipe point, then reacquire the connector objects
                    // after regeneration before computing the cut gap.
                    XYZ connectorMidpoint = (accessoryEnds[0].Origin + accessoryEnds[1].Origin).Multiply(0.5);
                    XYZ seatingMove = p.InlinePoint - connectorMidpoint;
                    if (seatingMove.GetLength() > MepRules.CoincidenceToleranceFeet)
                    {
                        ElementTransformUtils.MoveElement(doc, placed.Id, seatingMove);
                        doc.Regenerate();
                        accessoryManager = MepFacts.ManagerOf(placed);
                        accessoryEnds.Clear();
                        if (accessoryManager != null)
                            foreach (Connector connector in MepFacts.Ordered(accessoryManager))
                                if (connector.Domain == Domain.DomainPiping) accessoryEnds.Add(connector);
                        if (accessoryEnds.Count != 2)
                            throw new InvalidOperationException("the accessory no longer exposes exactly two piping " +
                                "connectors after seating it on the run");
                    }

                    double runLength = originalStart.DistanceTo(originalEnd);
                    var orderedEnds = accessoryEnds
                        .Select(c => new { Connector = c, Along = (c.Origin - originalStart).DotProduct(runDirection),
                                           OffAxis = runLine.Distance(c.Origin) })
                        .OrderBy(x => x.Along).ToList();
                    if (orderedEnds.Any(x => x.OffAxis > MepRules.CoincidenceToleranceFeet * 10))
                        throw new InvalidOperationException("the accessory's piping connectors do not lie on the pipe axis after placement; " +
                            "nothing was cut");
                    if (orderedEnds[1].Along - orderedEnds[0].Along <= MepRules.CoincidenceToleranceFeet)
                        throw new InvalidOperationException("the accessory's two piping connectors collapse to one point on the run; " +
                            "nothing was cut");
                    if (orderedEnds[0].Along < 300 / 304.8 || runLength - orderedEnds[1].Along < 300 / 304.8)
                        throw new InvalidOperationException("the accessory connector gap leaves less than 300 mm of pipe on one side; " +
                            "nothing was cut");

                    XYZ lowPoint = originalStart + runDirection.Multiply(orderedEnds[0].Along);
                    XYZ highPoint = originalStart + runDirection.Multiply(orderedEnds[1].Along);
                    ElementId afterLowId = Autodesk.Revit.DB.Plumbing.PlumbingUtils.BreakCurve(
                        doc, p.InlineHost.Id, lowPoint);
                    doc.Regenerate();
                    Pipe afterLow = doc.GetElement(afterLowId) as Pipe;
                    var firstBreakPieces = new[] { p.InlineHost as Pipe, afterLow }.Where(x => x != null).ToList();
                    Pipe highCarrier = firstBreakPieces.FirstOrDefault(x =>
                    {
                        Curve curve = (x.Location as LocationCurve)?.Curve;
                        return curve != null && curve.Distance(highPoint) <= MepRules.CoincidenceToleranceFeet * 10;
                    });
                    if (afterLow == null || highCarrier == null)
                        throw new InvalidOperationException("Revit did not leave a pipe segment carrying the second inline break point");
                    ElementId afterHighId = Autodesk.Revit.DB.Plumbing.PlumbingUtils.BreakCurve(
                        doc, highCarrier.Id, highPoint);
                    doc.Regenerate();

                    var pieces = new List<Pipe>
                    {
                        doc.GetElement(p.InlineHost.Id) as Pipe,
                        doc.GetElement(afterLowId) as Pipe,
                        doc.GetElement(afterHighId) as Pipe
                    }.Where(x => x != null).GroupBy(x => Rid.Value(x.Id)).Select(g => g.First()).ToList();
                    Func<Pipe, XYZ, bool> touches = (pipe, point) =>
                    {
                        Curve curve = (pipe.Location as LocationCurve)?.Curve;
                        return curve != null && (curve.GetEndPoint(0).DistanceTo(point) <= MepRules.CoincidenceToleranceFeet * 10 ||
                                                 curve.GetEndPoint(1).DistanceTo(point) <= MepRules.CoincidenceToleranceFeet * 10);
                    };
                    Pipe left = pieces.FirstOrDefault(x => touches(x, originalStart));
                    Pipe right = pieces.FirstOrDefault(x => touches(x, originalEnd));
                    Pipe middle = pieces.FirstOrDefault(x => x != left && x != right);
                    if (pieces.Count != 3 || left == null || right == null || middle == null || left.Id == right.Id)
                        throw new InvalidOperationException("the two inline breaks did not produce two outer pipe halves and one removable middle");
                    doc.Delete(middle.Id);
                    doc.Regenerate();

                    var halves = new[] { left, right };
                    for (int halfIndex = 0; halfIndex < halves.Length; halfIndex++)
                    {
                        Element half = halves[halfIndex];
                        ConnectorManager halfManager = MepFacts.ManagerOf(half);
                        Connector halfEnd = null; double bestHalf = double.MaxValue;
                        foreach (Connector candidate in MepFacts.Ordered(halfManager))
                        {
                            if (candidate.IsConnected) continue;
                            double distance = candidate.Origin.DistanceTo(p.InlinePoint);
                            if (distance < bestHalf) { bestHalf = distance; halfEnd = candidate; }
                        }
                        if (halfEnd == null)
                            throw new InvalidOperationException("half-run " + Rid.Value(half.Id) +
                                " has no open end at the break point");
                        Connector accessoryEnd = orderedEnds[halfIndex].Connector;
                        if (accessoryEnd == null)
                            throw new InvalidOperationException("the accessory ran out of open connectors before " +
                                "both halves were connected");
                        halfEnd.ConnectTo(accessoryEnd);
                    }
                    doc.Regenerate();
                    for (int i = 0; i < orderedEnds.Count; i++)
                    {
                        Connector connector = orderedEnds[i].Connector;
                        long expectedPipeId = Rid.Value(halves[i].Id);
                        bool reachesExpectedPipe = false;
                        try
                        {
                            foreach (Connector other in connector.AllRefs)
                                if (other?.Owner is Pipe pipe && Rid.Value(pipe.Id) == expectedPipeId)
                                    reachesExpectedPipe = true;
                        }
                        catch { }
                        if (!connector.IsConnected || !reachesExpectedPipe)
                            throw new InvalidOperationException("the accessory still has an open piping connector " +
                                "after both halves were joined; the batch rolls back rather than keep a half-" +
                                "connected valve");
                    }
                    if (right.Id != p.InlineHost.Id) p.AlsoCreated.Add(right.Id);
                    else if (left.Id != p.InlineHost.Id) p.AlsoCreated.Add(left.Id);
                    return placed;
                }
                case "beam_system":
                {
                    var profile = new List<Curve>();
                    for (int v = 0; v < p.ProfilePoints.Count; v++)
                        profile.Add(Line.CreateBound(p.ProfilePoints[v],
                                                     p.ProfilePoints[(v + 1) % p.ProfilePoints.Count]));
                    var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, p.ProfilePoints[0]);
                    SketchPlane sketch = SketchPlane.Create(doc, plane);
                    BeamSystem system = BeamSystem.Create(doc, profile, sketch, p.BeamDirection, false);
                    if (p.BeamType != null)
                    {
                        if (!p.BeamType.IsActive) { p.BeamType.Activate(); doc.Regenerate(); }
                        system.BeamType = p.BeamType;
                    }
                    if (p.BeamSpacing > 1e-9)
                    {
                        var layout = new LayoutRuleFixedDistance(p.BeamSpacing, BeamSystemJustifyType.Beginning);
                        system.LayoutRule = layout;
                    }
                    return system;
                }
                case "mep_system":
                {
                    MEPSystem newSystem = p.SystemType is Autodesk.Revit.DB.Plumbing.PipingSystemType
                        ? (MEPSystem)Autodesk.Revit.DB.Plumbing.PipingSystem.Create(doc, p.SystemType.Id, p.SystemName)
                        : Autodesk.Revit.DB.Mechanical.MechanicalSystem.Create(doc, p.SystemType.Id, p.SystemName);
                    if (p.SystemMembers.Count > 0)
                    {
                        // Add takes CONNECTORS, and the plan already chose and validated
                        // exactly which one per member - free, and in the right domain.
                        // Choosing again here would let the commit act on a connector the
                        // rehearsal never checked.
                        var set = new ConnectorSet();
                        for (int m = 0; m < p.SystemMembers.Count; m++)
                        {
                            Element member = p.SystemMembers[m];
                            int wantedConnectorId = p.SystemMemberConnectors[m];
                            ConnectorManager manager = MepFacts.ManagerOf(member);
                            Connector chosen = null;
                            if (manager != null)
                                foreach (Connector candidate in MepFacts.Ordered(manager))
                                    if (candidate.Id == wantedConnectorId) { chosen = candidate; break; }
                            if (chosen == null)
                                throw new InvalidOperationException("member " + Rid.Value(member.Id) +
                                    " lost connector " + wantedConnectorId + " between the rehearsal and the commit");
                            set.Insert(chosen);
                        }
                        newSystem.Add(set);
                        doc.Regenerate();
                    }
                    return newSystem;
                }
                case "wall_foundation":
                    return WallFoundation.Create(doc, p.Type.Id, p.FoundationWall.Id);
                case "slab_opening":
                {
                    var profile = new CurveArray();
                    double halfW = p.SlabWidth / 2, halfH = p.SlabHeight / 2;
                    XYZ c = p.SlabCenter;
                    if (p.SlabShape == "circular")
                    {
                        // A circle as two half-arcs: the profile Revit's opening sketch takes.
                        profile.Append(Arc.Create(new XYZ(c.X - halfW, c.Y, c.Z), new XYZ(c.X + halfW, c.Y, c.Z),
                                                  new XYZ(c.X, c.Y + halfW, c.Z)));
                        profile.Append(Arc.Create(new XYZ(c.X + halfW, c.Y, c.Z), new XYZ(c.X - halfW, c.Y, c.Z),
                                                  new XYZ(c.X, c.Y - halfW, c.Z)));
                    }
                    else
                    {
                        XYZ p1 = new XYZ(c.X - halfW, c.Y - halfH, c.Z), p2 = new XYZ(c.X + halfW, c.Y - halfH, c.Z);
                        XYZ p3 = new XYZ(c.X + halfW, c.Y + halfH, c.Z), p4 = new XYZ(c.X - halfW, c.Y + halfH, c.Z);
                        profile.Append(Line.CreateBound(p1, p2)); profile.Append(Line.CreateBound(p2, p3));
                        profile.Append(Line.CreateBound(p3, p4)); profile.Append(Line.CreateBound(p4, p1));
                    }
                    return doc.Create.NewOpening(p.SlabHost, profile, true);
                }
                case "shaft":
                {
                    var shaftProfile = new CurveArray();
                    foreach (Curve curve in p.Loops[0]) shaftProfile.Append(curve);
                    Opening shaft = doc.Create.NewOpening(p.BaseLevel, p.TopLevel, shaftProfile);
                    if (shaft == null)
                        throw new InvalidOperationException(
                            "Revit created no shaft from that profile and level pair, and gave no reason. " +
                            "Nothing was kept.");
                    return shaft;
                }
                case "room_separator":
                {
                    // THE SKETCH PLANE IS THE LEVEL'S, not the view's own. A
                    // separator drawn on a view plane sits wherever that view is
                    // cut; on the level's plane it sits where the room is.
                    // Checked in the rehearsal, where a refusal costs nothing.
                    var view = (ViewPlan)p.SeparatorView;

                    Plane plane = Plane.CreateByNormalAndOrigin(
                        XYZ.BasisZ, new XYZ(0, 0, p.Level.Elevation));
                    SketchPlane sketch = SketchPlane.Create(doc, plane);

                    var curves = new CurveArray();
                    int appended = 0;
                    foreach (List<Curve> chain in p.Chains)
                        foreach (Curve curve in chain) { curves.Append(curve); appended++; }
                    if (appended == 0)
                        throw new ArgumentException("room_separator was given no curves to create");

                    ModelCurveArray made = doc.Create.NewRoomBoundaryLines(sketch, curves, view);
                    if (made == null || made.Size == 0)
                        throw new InvalidOperationException(
                            "Revit created no room boundary lines from those curves. Nothing was kept.");

                    // THE BATCH TRACKS ONE ELEMENT PER ROW AND THIS CALL MADE
                    // SEVERAL, so the others are carried explicitly rather than
                    // dropped. They used to be: the row reported created_verified 1
                    // and one element_id for a two-curve chain, and the siblings
                    // were permanently anonymous - no provenance, so the audit
                    // called them bim_without_source and no incremental update
                    // would ever move or delete them when the drawing changed.
                    // The count was written into SeparatorSegments and read by
                    // nothing, which is a comment promising what no code kept.
                    p.SeparatorSegments = made.Size;
                    ModelCurve first = null;
                    foreach (ModelCurve mc in made)
                    {
                        if (first == null) { first = mc; continue; }
                        p.AlsoCreated.Add(mc.Id);
                    }
                    return first;
                }
                case "fitting":
                {
                    // Resolve each member to a live element: by id for pre-existing
                    // ones, from THIS batch's created list for batch_index refs. The
                    // deferred pair selection (batch refs had no geometry at plan time)
                    // runs HERE, inside the atomic transaction - a refusal rolls the
                    // whole batch back, nothing partial survives.
                    var owners = new List<Element>();
                    foreach (FittingMember member in p.FittingMembers)
                    {
                        if (member.BatchIndex != null)
                        {
                            Created earlier = createdSoFar.FirstOrDefault(c => c.Index == member.BatchIndex.Value);
                            Element made = earlier == null ? null : doc.GetElement(earlier.Id);
                            if (made == null)
                                throw new InvalidOperationException("fitting batch_index " + member.BatchIndex.Value +
                                    " references an entry that produced no element");
                            member.Owner = made;
                        }
                        owners.Add(member.Owner);
                    }
                    bool deferred = p.FittingMembers.Any(member => member.Fact == null);
                    if (deferred && p.FittingSubtype != "takeoff")
                    {
                        var factSides = new List<List<ConnectorFact>>();
                        foreach (Element owner in owners)
                        {
                            var facts = new List<ConnectorFact>();
                            ConnectorManager ownerManager = MepFacts.ManagerOf(owner);
                            if (ownerManager != null)
                                foreach (Connector connector in MepFacts.Ordered(ownerManager))
                                    facts.Add(MepFacts.FactOf(connector));
                            factSides.Add(facts);
                        }
                        ConnectorFact chosenA, chosenB; string pairCode, pairReason;
                        if (!MepRules.SelectPair(factSides[0], factSides[1],
                                                 p.FittingMembers[0].NamedConnector, p.FittingMembers[1].NamedConnector,
                                                 out chosenA, out chosenB, out pairCode, out pairReason))
                            throw new InvalidOperationException(pairCode + ": " + pairReason +
                                " (deferred batch selection; the whole batch rolls back)");
                        p.FittingMembers[0].ConnectorId = chosenA.Id; p.FittingMembers[0].Fact = chosenA;
                        p.FittingMembers[1].ConnectorId = chosenB.Id; p.FittingMembers[1].Fact = chosenB;
                        if (p.FittingMembers.Count > 2)
                        {
                            ConnectorFact branchFact, unusedCorner; string branchCode, branchReason;
                            if (!MepRules.SelectPair(factSides[2], new List<ConnectorFact> { chosenA },
                                                     p.FittingMembers[2].NamedConnector, null,
                                                     out branchFact, out unusedCorner, out branchCode, out branchReason))
                                throw new InvalidOperationException(branchCode + " (branch): " + branchReason);
                            p.FittingMembers[2].ConnectorId = branchFact.Id; p.FittingMembers[2].Fact = branchFact;
                        }
                    }
                    if (p.FittingSubtype == "takeoff")
                    {
                        Element main = p.TakeoffMain;
                        if (main == null && p.TakeoffMainBatchIndex != null)
                        {
                            Created earlierMain = createdSoFar.FirstOrDefault(c => c.Index == p.TakeoffMainBatchIndex.Value);
                            main = earlierMain == null ? null : doc.GetElement(earlierMain.Id);
                        }
                        if (!(main is MEPCurve mainCurve))
                            throw new InvalidOperationException("the takeoff main did not resolve to an MEP curve");
                        // The branch: its one open connector nearest the main's curve.
                        Connector branchConnector = null; double bestDistance = double.MaxValue;
                        ConnectorManager branchManager = MepFacts.ManagerOf(owners[0]);
                        Curve mainGeometry = (mainCurve.Location as LocationCurve)?.Curve;
                        if (branchManager != null && mainGeometry != null)
                            foreach (Connector candidate in MepFacts.Ordered(branchManager))
                            {
                                if (candidate.IsConnected) continue;
                                if (p.FittingMembers[0].NamedConnector != null &&
                                    candidate.Id != p.FittingMembers[0].NamedConnector.Value) continue;
                                double distance = mainGeometry.Distance(candidate.Origin);
                                if (distance < bestDistance) { bestDistance = distance; branchConnector = candidate; }
                            }
                        if (branchConnector == null)
                            throw new InvalidOperationException("the takeoff branch has no eligible OPEN connector");
                        if (bestDistance > MepRules.CoincidenceToleranceFeet * 10)
                            throw new InvalidOperationException("the branch's open connector is " +
                                MepRules.Mm(bestDistance) + " from the main curve; a takeoff taps a connector " +
                                "that TOUCHES the main (within " + MepRules.Mm(MepRules.CoincidenceToleranceFeet * 10) + ").");
                        p.FittingMembers[0].ConnectorId = branchConnector.Id;
                        return doc.Create.NewTakeoffFitting(branchConnector, mainCurve);
                    }
                    var live = new List<Connector>();
                    foreach (FittingMember member in p.FittingMembers)
                    {
                        Connector found = null;
                        ConnectorManager manager = MepFacts.ManagerOf(doc.GetElement(member.Owner.Id));
                        if (manager != null)
                            foreach (Connector candidate in MepFacts.Ordered(manager))
                                if (candidate.Id == member.ConnectorId) { found = candidate; break; }
                        if (found == null)
                            throw new InvalidOperationException("connector " + member.ConnectorId + " of element " +
                                Rid.Value(member.Owner.Id) + " is no longer present");
                        if (found.IsConnected)
                            throw new InvalidOperationException("connector " + member.ConnectorId + " of element " +
                                Rid.Value(member.Owner.Id) + " is already connected");
                        live.Add(found);
                    }
                    switch (p.FittingSubtype)
                    {
                        case "elbow": return doc.Create.NewElbowFitting(live[0], live[1]);
                        case "union": return doc.Create.NewUnionFitting(live[0], live[1]);
                        case "transition": return doc.Create.NewTransitionFitting(live[0], live[1]);
                        case "tee": return doc.Create.NewTeeFitting(live[0], live[1], live[2]);
                        default: throw new InvalidOperationException("unsupported fitting '" + p.FittingSubtype + "'");
                    }
                }
                default: throw new InvalidOperationException("unsupported kind '" + p.Kind + "'");
            }
        }

        /// <summary>
        /// The connector's own system classification, as a string, in its domain.
        /// Null when Revit will not give one - which is itself a refusable fact.
        /// </summary>
        private static string SafeConnectorClassification(Connector connector, bool piping)
        {
            try { return piping ? connector.PipeSystemType.ToString() : connector.DuctSystemType.ToString(); }
            catch { return null; }
        }

        /// <summary>
        /// Classifications a system cannot be built from. Both enums spell the two
        /// useless cases the same way: the connector belongs to a fitting (it inherits
        /// whatever it is joined to) or declares nothing at all.
        /// </summary>
        private static bool IsUnusableClassification(string classification) =>
            classification == null ||
            classification.IndexOf("Undefined", StringComparison.OrdinalIgnoreCase) >= 0 ||
            string.Equals(classification, "Fitting", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(classification, "Global", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether these two corners actually land on this wall, or null when they
        /// do.
        ///
        /// THE PROJECTION HAS TO BE ONTO THE INFINITE LINE, and that is the whole
        /// difficulty. Curve.Project CLAMPS to the curve's own bounds - a corner a
        /// metre past the end of a wall projects to the end and reports a
        /// normalised parameter of exactly 1 - so the first version of this check
        /// could never fire, and a live fixture drawn 330 mm past a wall end
        /// rehearsed clean. Which is precisely the behaviour the check exists to
        /// catch, because NewOpening does the same thing and cuts the hole at the
        /// end, in a place nobody drew.
        ///
        /// So the parameter is computed rather than asked for. On a straight wall
        /// that is a dot product and it is exact. On a CURVED one it is not
        /// available this way, and this says so instead of reporting a pass it did
        /// not measure.
        /// </summary>
        private static string OffTheWall(Wall wall, XYZ a, XYZ b, out string notChecked)
        {
            notChecked = null;
            Curve curve = null;
            try { curve = (wall.Location as LocationCurve)?.Curve; } catch { }
            if (curve == null) return null;   // no curve to measure against; not a refusal we can justify

            var line = curve as Line;
            if (line == null)
            {
                // A CURVED WALL, and this check cannot be made on one: the bounded
                // projection clamps and the unbounded one is not straight-line
                // arithmetic. Returning null here is the same value as "measured
                // and fine", so the fact that nothing was measured is RECORDED on
                // the row rather than left to look like a pass.
                notChecked = "the host wall is curved, and whether these corners fall past its ends was NOT " +
                             "measured: the projection Revit does on an arc clamps to the ends, so a corner " +
                             "beyond one is indistinguishable from a corner on it. The opening may be cut at " +
                             "the end of the wall rather than where it was drawn.";
                return null;
            }

            XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);
            XYZ along = p1 - p0;
            double lengthFeet = along.GetLength();
            if (lengthFeet <= 1e-9) return null;
            double lengthMm = CadUnits.FeetToMm(lengthFeet);

            double worstPastMm = 0;
            foreach (XYZ corner in new[] { a, b })
            {
                if (corner == null) continue;
                double t = (corner - p0).DotProduct(along) / (lengthFeet * lengthFeet);
                double past = t < 0 ? -t : (t > 1 ? t - 1 : 0);
                double pastMm = past * lengthMm;
                if (pastMm > worstPastMm) worstPastMm = pastMm;
            }

            // A millimetre of slack, because a ring drawn exactly to a wall end is
            // a drawing that meant the end, not one that overshot it.
            if (worstPastMm <= 1.0) return null;
            return "opening_off_the_wall: this opening reaches " +
                   worstPastMm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                   " mm past the end of wall " + Rid.Value(wall.Id) + ", which is " +
                   lengthMm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                   " mm long. Nothing was created. Revit does not refuse this - it PROJECTS the corners onto " +
                   "the host and cuts the hole at the end instead, in a place nobody drew. Either the drawing " +
                   "puts this opening past the wall it belongs to, or the wall it belongs to has not been " +
                   "converted and the nearest one was found instead.";
        }

        /// <summary>
        /// Whether these two corners span anything ALONG the wall, or null when they
        /// do.
        ///
        /// A hole is cut where its two corners project to on the host, so a pair
        /// that projects to one point removes nothing - and every check downstream
        /// agrees that it did: present_after_commit, the category, the host. Only
        /// the wall's own volume would show it, and nothing re-reads that.
        /// </summary>
        private static string NoWidthAlongHost(Wall wall, XYZ a, XYZ b)
        {
            var line = (wall.Location as LocationCurve)?.Curve as Line;
            if (line == null || a == null || b == null) return null;

            XYZ along = line.GetEndPoint(1) - line.GetEndPoint(0);
            double lengthFeet = along.GetLength();
            if (lengthFeet <= 1e-9) return null;

            double spanMm = Math.Abs(CadUnits.FeetToMm((b - a).DotProduct(along) / lengthFeet));
            if (spanMm > 1.0) return null;
            return "opening_no_width_along_host: these two corners project to the same point on wall " +
                   Rid.Value(wall.Id) + " - " +
                   spanMm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                   " mm apart along it - so the opening would cut nothing. Nothing was created. An opening " +
                   "that cuts nothing is created, verified and audited clean: only the wall's own volume " +
                   "would ever show it, and nothing re-reads that.";
        }

        /// <summary>
        /// The load-bearing floors a shaft would cut, or null when there are none
        /// or a person has said yes.
        ///
        /// The extent is the two storeys the shaft runs between and the footprint
        /// is its own profile, so what is measured is exactly what Revit will cut.
        /// A slab that cannot be read is NOT counted as safe - it is named, because
        /// "could not look" and "nothing there" are different answers and only one
        /// of them is a reason to proceed.
        /// </summary>
        private static string StructuralSlabsInTheWay(Document doc, CurveLoop profile, Level bottom,
                                                      Level top, bool allowed)
        {
            if (allowed || doc == null || profile == null || bottom == null || top == null) return null;

            XYZ inside;
            try { inside = Centroid(profile); } catch { return null; }
            if (inside == null) return null;

            var structural = new List<string>();
            var unreadable = new List<string>();
            try
            {
                foreach (Element slab in CadHostResolver.Slabs(doc))
                {
                    Level on = null;
                    try { on = doc.GetElement(slab.LevelId) as Level; } catch { }
                    if (on == null) continue;
                    if (on.Elevation < bottom.Elevation - 1e-6 || on.Elevation > top.Elevation + 1e-6) continue;
                    if (!CadHostResolver.Covers(slab, inside)) continue;

                    Parameter flag = null;
                    try { flag = slab.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL); } catch { }
                    if (flag == null) { unreadable.Add(Rid.Value(slab.Id) + " on " + Safe(() => on.Name)); continue; }
                    if (flag.AsInteger() == 1) structural.Add(Rid.Value(slab.Id) + " on " + Safe(() => on.Name));
                }
            }
            catch { return null; }

            if (structural.Count == 0 && unreadable.Count == 0) return null;
            return "structural_host_requires_opt_in: this shaft runs from " + Safe(() => bottom.Name) + " to " +
                   Safe(() => top.Name) + " and would cut " +
                   (structural.Count > 0
                       ? structural.Count + " LOAD-BEARING slab(s) - " + string.Join(", ", structural.Take(6))
                       : "slab(s) whose load-bearing flag could not be read - " + string.Join(", ", unreadable.Take(6))) +
                   (unreadable.Count > 0 && structural.Count > 0
                       ? ", and " + unreadable.Count + " more whose flag could not be read"
                       : "") +
                   ". Nothing was created. A shaft cuts EVERY slab between its two storeys, which is more " +
                   "structural floor than any single opening reaches, so the same opt-in applies and more: " +
                   "declare allow_structural on the rule, or allow_structural=true on the row, to record that " +
                   "a person approved it. A slab whose flag could not be read is named rather than assumed " +
                   "safe.";
        }

        /// <summary>A point inside a closed loop: the average of its vertices, which for the
        /// convex rings a shaft profile has to be is inside it.</summary>
        private static XYZ Centroid(CurveLoop loop)
        {
            double x = 0, y = 0, z = 0;
            int n = 0;
            foreach (Curve c in loop)
            {
                XYZ a = c.GetEndPoint(0);
                x += a.X; y += a.Y; z += a.Z; n++;
            }
            return n == 0 ? null : new XYZ(x / n, y / n, z / n);
        }

        private static bool KindMatches(Element e, string kind)
        {
            switch (kind)
            {
                case "level": return e is Level; case "grid": return e is Grid; case "wall": return e is Wall;
                case "floor": return e is Floor; case "ceiling": return e is Ceiling; case "roof": return e is FootPrintRoof;
                case "room": return e is Autodesk.Revit.DB.Architecture.Room;
                case "family_instance": return e is FamilyInstance; case "duct": return e is Duct;
                case "pipe": return e is Pipe; case "conduit": return e is Conduit; case "cable_tray": return e is CableTray;
                case "structural_framing": return e is FamilyInstance && InCategory(e, BuiltInCategory.OST_StructuralFraming);
                case "structural_column": return e is FamilyInstance && InCategory(e, BuiltInCategory.OST_StructuralColumns);
                // THREE KINDS OF HOLE, THREE CATEGORIES. "is it an Opening" is
                // true of all of them, so a verification that stopped there would
                // pass a shaft for a wall opening and a wall opening for a hole in
                // a floor - which is precisely what the separate kinds exist to
                // keep apart.
                case "wall_opening":
                    return e is Opening && InCategory(e, BuiltInCategory.OST_SWallRectOpening);
                case "slab_opening": return e is Opening;
                // A SHAFT IS NOT A HOLE IN A SLAB, and the verification has to
                // know it: "is it an Opening" would pass a shaft built as one
                // opening per floor, which is the exact mistake the separate kind
                // exists to prevent. The CATEGORY is what tells them apart.
                case "shaft": return e is Opening && InCategory(e, BuiltInCategory.OST_ShaftOpening);
                // And a separator is a MODEL curve that bounds a room, not a
                // detail line that looks like one in the view it was drawn in.
                case "room_separator":
                    return e is CurveElement && InCategory(e, BuiltInCategory.OST_RoomSeparationLines);
                case "beam_system":
                    // An empty system is a sketch, not structure - and the row should say
                    // WHY it failed: the member count is measured into the verification
                    // detail by the caller reading actual_class + this refusal.
                    return e is BeamSystem beamSystem && beamSystem.GetBeamIds().Count > 0;
                case "wall_foundation": return e is WallFoundation;
                case "mep_system":
                    // An MEP system that carries no members is a name in a browser tree,
                    // not a system - but an EMPTY one is exactly what was asked for when
                    // no members were named, so the member count is checked separately
                    // (ExpectedMembers) rather than folded into "is it the right class".
                    return e is MEPSystem;
                case "accessory_inline":
                    return e is FamilyInstance inlineInstance && MepFacts.ManagerOf(inlineInstance) != null;
                case "fitting": return e is FamilyInstance && (
                    InCategory(e, BuiltInCategory.OST_PipeFitting) || InCategory(e, BuiltInCategory.OST_DuctFitting) ||
                    InCategory(e, BuiltInCategory.OST_ConduitFitting) || InCategory(e, BuiltInCategory.OST_CableTrayFitting));
                default: return false;
            }
        }

        private static T Need<T>(Document doc, JObject o, string field) where T : Element
        {
            T value = Optional<T>(doc, o, field);
            if (value == null) throw new ArgumentException(field + " is required and must identify a " + typeof(T).Name);
            return value;
        }
        private static T Optional<T>(Document doc, JObject o, string field) where T : Element
        {
            if (o[field] == null) return null;
            long raw = o.Value<long>(field);
            if (!Rid.CanRepresent(raw)) throw new ArgumentException(field + " is outside ElementId range");
            T value = doc.GetElement(Rid.Make(raw)) as T;
            if (value == null) throw new ArgumentException(field + "=" + raw + " does not identify a " + typeof(T).Name);
            return value;
        }
        private static XYZ Point(JToken token, double scale, bool requireZ)
        {
            JArray a = token as JArray;
            int minimum = requireZ ? 3 : 2;
            if (a == null || a.Count < minimum || a.Count > 3) throw new ArgumentException("point/start/end must contain " + minimum + " XYZ coordinates");
            return new XYZ(Finite(a[0].Value<double>(), "X") * scale, Finite(a[1].Value<double>(), "Y") * scale,
                Finite(a.Count > 2 ? a[2].Value<double>() : 0, "Z") * scale);
        }
        /// <summary>
        /// OPEN CHAINS of curves, for the things Revit takes as a run rather than
        /// a boundary. Two points is a line and is the ordinary case; nothing is
        /// closed back to the start, because a separator that closed itself would
        /// bound a room the drawing does not show.
        /// </summary>
        private static IList<List<Curve>> Chains(JArray profile, double scale)
        {
            if (profile == null || profile.Count == 0) throw new ArgumentException("profile requires at least one chain");
            var result = new List<List<Curve>>();
            foreach (JArray chainToken in profile.OfType<JArray>())
            {
                if (chainToken.Count < 2) throw new ArgumentException("every profile chain needs at least two points");
                List<XYZ> points = chainToken.Select(t => Point(t, scale, true)).ToList();
                var chain = new List<Curve>();
                for (int i = 0; i + 1 < points.Count; i++) chain.Add(Line.CreateBound(points[i], points[i + 1]));
                result.Add(chain);
            }
            if (result.Count != profile.Count) throw new ArgumentException("every profile entry must be an array of XYZ points");
            return result;
        }

        private static IList<CurveLoop> Loops(JArray profile, double scale)
        {
            if (profile == null || profile.Count == 0) throw new ArgumentException("profile requires at least one loop");
            var result = new List<CurveLoop>();
            foreach (JArray loopToken in profile.OfType<JArray>())
            {
                if (loopToken.Count < 3) throw new ArgumentException("every profile loop needs at least three points");
                List<XYZ> points = loopToken.Select(t => Point(t, scale, true)).ToList();
                var loop = new CurveLoop();
                for (int i = 0; i < points.Count; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
                result.Add(loop);
            }
            if (result.Count != profile.Count) throw new ArgumentException("every profile entry must be an array of XYZ points");
            return result;
        }
        /// <summary>
        /// A CURVED ELEMENT, DECLARED AND CHECKED AGAINST ITSELF.
        ///
        /// start, end, centre and radius over-determine an arc, and that is
        /// deliberate: four numbers that must agree are four chances to catch a
        /// mistake before anything is written. A centre that is not equidistant
        /// from both ends describes no arc at all, and Revit would still build
        /// something from it.
        ///
        /// The third point Arc.Create needs is DERIVED here rather than asked for,
        /// because the obvious thing to pass - the midpoint between the ends - is
        /// inside the arc and produces a different curve. Which way round the arc
        /// goes is a declaration (clockwise), not a guess.
        /// </summary>
        private static void ReadArc(JToken token, Plan p, double scale)
        {
            var arc = token as JObject;
            if (arc == null) return;

            XYZ centre = Point(arc["centre"] ?? arc["center"], scale, true);
            double radius = arc.Value<double?>("radius").GetValueOrDefault() * scale;
            if (radius <= 0 || double.IsNaN(radius) || double.IsInfinity(radius))
                throw new ArgumentException("arc.radius must be a positive finite number");

            double toStart = centre.DistanceTo(p.Start);
            double toEnd = centre.DistanceTo(p.End);
            const double tolerance = 1.0 / 304.8;   // 1 mm, in feet
            if (Math.Abs(toStart - radius) > tolerance || Math.Abs(toEnd - radius) > tolerance)
                throw new ArgumentException(
                    "arc_does_not_close: the declared centre is " +
                    (toStart * 304.8).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " mm from start and " +
                    (toEnd * 304.8).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " mm from end, but the " +
                    "declared radius is " + (radius * 304.8).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                    " mm. Those describe no arc. Nothing was created.");

            // The midpoint of the SWEEP, on the circle - the point Arc.Create wants.
            double a0 = Math.Atan2(p.Start.Y - centre.Y, p.Start.X - centre.X);
            double a1 = Math.Atan2(p.End.Y - centre.Y, p.End.X - centre.X);
            bool clockwise = arc.Value<bool?>("clockwise") == true;
            double sweep = a1 - a0;
            while (sweep <= 0) sweep += 2 * Math.PI;
            while (sweep > 2 * Math.PI) sweep -= 2 * Math.PI;
            if (clockwise) sweep -= 2 * Math.PI;
            double mid = a0 + sweep / 2.0;

            double z = (p.Start.Z + p.End.Z) / 2.0;
            p.ArcThird = new XYZ(centre.X + radius * Math.Cos(mid), centre.Y + radius * Math.Sin(mid), z);
            p.ArcCentre = centre;
            p.ArcRadius = radius;
        }

        private static void NonZero(XYZ a, XYZ b) { if (a.DistanceTo(b) < 1e-9) throw new ArgumentException("start and end must differ"); }
        /// <summary>
        /// The same rule for open chains. A separator drawn across two storeys is
        /// not a separator anybody meant, and Revit takes the sketch plane from a
        /// level - so a chain that is not flat would be silently flattened onto it.
        /// </summary>
        private static void RequireHorizontalChains(IEnumerable<List<Curve>> chains, string kind)
        {
            double? commonZ = null;
            foreach (List<Curve> chain in chains)
            {
                double z = chain[0].GetEndPoint(0).Z;
                if (chain.Any(c => Math.Abs(c.GetEndPoint(0).Z - z) > 1e-7 || Math.Abs(c.GetEndPoint(1).Z - z) > 1e-7))
                    throw new ArgumentException(kind + " profile chains must be horizontal and coplanar");
                if (commonZ != null && Math.Abs(z - commonZ.Value) > 1e-7)
                    throw new ArgumentException(kind + " profile chains must share one horizontal plane");
                commonZ = z;
            }
        }

        private static void RequireHorizontal(IEnumerable<CurveLoop> loops, string kind)
        {
            double? commonZ = null;
            foreach (CurveLoop loop in loops)
            {
                List<Curve> curves = loop.ToList();
                double z = curves[0].GetEndPoint(0).Z;
                if (curves.Any(c => Math.Abs(c.GetEndPoint(0).Z - z) > 1e-7 || Math.Abs(c.GetEndPoint(1).Z - z) > 1e-7))
                    throw new ArgumentException(kind + " profile loops must be horizontal and coplanar");
                if (commonZ != null && Math.Abs(z - commonZ.Value) > 1e-7)
                    throw new ArgumentException(kind + " profile loops must share one horizontal plane");
                commonZ = z;
            }
        }
        /// <summary>
        /// Whether Revit holds this element as load-bearing. Null when the
        /// element has no such parameter at all, which is a different answer from
        /// "no" and must not be flattened into it.
        /// </summary>
        /// <summary>
        /// The bore a row asked for, in feet. Only round runs have one: a
        /// rectangular duct has a width and a height, and answering a request
        /// for a diameter by setting one of them would be a different duct.
        /// </summary>
        private static double? ReadDiameter(JObject item, double scale)
        {
            double? mm = item.Value<double?>("diameter");
            if (mm == null) return null;
            double value = Finite(mm.Value, "diameter") * scale;
            if (value <= 0) throw new ArgumentException("diameter must be a positive finite number");
            return value;
        }

        /// <summary>Which parameter holds this run's bore, or null when it has none.</summary>
        private static Parameter DiameterParameterOf(Element element)
        {
            if (element == null) return null;
            BuiltInParameter[] candidates;
            if (element is Autodesk.Revit.DB.Plumbing.Pipe)
                candidates = new[] { BuiltInParameter.RBS_PIPE_DIAMETER_PARAM };
            else if (element is Autodesk.Revit.DB.Electrical.Conduit)
                candidates = new[] { BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM };
            else if (element is Autodesk.Revit.DB.Mechanical.Duct)
                candidates = new[] { BuiltInParameter.RBS_CURVE_DIAMETER_PARAM };
            else return null;

            foreach (BuiltInParameter which in candidates)
            {
                Parameter p = null;
                try { p = element.get_Parameter(which); } catch { }
                if (p != null && p.StorageType == StorageType.Double) return p;
            }
            return null;
        }

        private static double? DiameterOf(Element element)
        {
            Parameter p = DiameterParameterOf(element);
            if (p == null) return null;
            try { return p.AsDouble(); } catch { return null; }
        }

        /// <summary>A trimmed string a caller actually sent, or null when they sent nothing.</summary>
        private static string Trimmed(JObject item, string field)
        {
            string value = item.Value<string>(field);
            if (value == null) return null;
            value = value.Trim();
            return value.Length == 0 ? null : value;
        }

        /// <summary>
        /// Set a name or number that a caller asked for, and REFUSE rather than
        /// leave it half-done. A parameter that is read-only on this element, or
        /// that Revit rejects, must fail the batch: a room silently keeping the
        /// number Revit invented is worse than no room, because it schedules.
        /// </summary>
        private static void SetIdentity(Element element, BuiltInParameter which, string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Parameter p = element.get_Parameter(which);
            if (p == null)
                throw new InvalidOperationException(
                    "this element carries no " + label + " parameter, so the " + label + " '" + value +
                    "' could not be set. Nothing was kept.");
            if (p.IsReadOnly)
                throw new InvalidOperationException(
                    "the " + label + " parameter is read-only on this element, so '" + value +
                    "' could not be set. Nothing was kept.");
            if (!p.Set(value))
                throw new InvalidOperationException(
                    "Revit refused the " + label + " '" + value + "'. A room number must be unique on its " +
                    "level, so this is usually one the model already has. Nothing was kept.");
        }

        /// <summary>
        /// What the element is CALLED now, read back from Revit rather than from
        /// the request. Rooms answer through their own parameters; everything
        /// else answers through Element.Name.
        /// </summary>
        private static string IdentityOf(Element element, string kind, bool wantNumber)
        {
            if (element == null) return null;
            try
            {
                if (kind == "room")
                {
                    Parameter p = element.get_Parameter(wantNumber
                        ? BuiltInParameter.ROOM_NUMBER : BuiltInParameter.ROOM_NAME);
                    return p?.AsString();
                }
                return wantNumber ? null : element.Name;
            }
            catch { return null; }
        }

        private static bool? StructuralOf(Element element)
        {
            if (element == null) return null;
            BuiltInParameter which = element is Wall
                ? BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT
                : BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL;
            try
            {
                Parameter p = element.get_Parameter(which);
                if (p == null || p.StorageType != StorageType.Integer) return null;
                return p.AsInteger() == 1;
            }
            catch { return null; }
        }

        private static bool InCategory(Element element, BuiltInCategory category)
        { return element?.Category != null && Rid.Value(element.Category.Id) == (long)category; }
        private static bool Scale(string units, out double scale)
        { if (units == "feet") { scale = 1; return true; } if (units == "m") { scale = 1 / 0.3048; return true; } if (units == "mm") { scale = 1 / 304.8; return true; } scale = 0; return false; }
        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
        private static double Finite(double value, string field)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException(field + " must be finite"); return value; }

        /// <summary>A point frozen for the plan, to a tenth of a millimetre.</summary>
        private static string Canon01(XYZ point) =>
            System.Math.Round(point.X * 304.8, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            System.Math.Round(point.Y * 304.8, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            System.Math.Round(point.Z * 304.8, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Guarded reads for the plan: measuring must never be what fails.</summary>
        private static string SafePlanName(Element e)
        {
            try { return e == null ? "" : (e.Name ?? ""); } catch { return "<unreadable>"; }
        }

        private static string SafePlanUid(Element e)
        {
            try { return e == null ? "" : (e.UniqueId ?? ""); } catch { return "<unreadable>"; }
        }

        /// <summary>
        /// The level's elevation in tenths of a millimetre. Rounded because Revit's own
        /// regeneration jitters the last digits, and a fingerprint that changes on its own
        /// would refuse every apply - the same lesson the transform wiring paid for.
        /// </summary>
        private static string SafePlanElevation(Level level)
        {
            try
            {
                if (level == null) return "";
                return System.Math.Round(level.Elevation * 304.8, 1)
                             .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return "<unreadable>"; }
        }

        /// <summary>
        /// DID REVIT BUILD THE CURVE THAT WAS ASKED FOR?
        ///
        /// "e is Wall" proves nothing about curvature. Revit accepts an axis and,
        /// when the wall type or a join forces it, can produce something else - so
        /// a command that reported an arc it never built would be exactly the kind
        /// of false success this bridge exists to prevent.
        ///
        /// Returns null when the caller declared no arc, so a straight wall carries
        /// no curve claim at all rather than an empty one.
        /// </summary>
        private static JObject VerifyCurve(Element e, Created made)
        {
            if (!made.ExpectedArc) return null;
            var o = new JObject { ["requested"] = "arc" };
            try
            {
                var located = e.Location as LocationCurve;
                Curve built = located?.Curve;
                var arc = built as Arc;
                o["built"] = built == null ? "(no location curve)" : built.GetType().Name;
                if (arc == null)
                {
                    o["verified"] = false;
                    o["means"] = "an ARC was asked for and the element's location curve is not one. The wall " +
                                 "exists; its shape is not what was declared.";
                    return o;
                }
                double centreOff = arc.Center.DistanceTo(made.ExpectedArcCentre) * 304.8;
                double radiusOff = Math.Abs(arc.Radius - made.ExpectedArcRadius) * 304.8;
                o["centre_off_mm"] = Math.Round(centreOff, 4);
                o["radius_off_mm"] = Math.Round(radiusOff, 4);
                o["radius_mm"] = Math.Round(arc.Radius * 304.8, 4);
                // 1 mm on each, the same bar the rest of this command uses for
                // "the model came back where we put it".
                o["verified"] = centreOff <= 1.0 && radiusOff <= 1.0;
                if (!(bool)o["verified"])
                    o["means"] = "the element IS an arc and not the arc that was declared: its centre is " +
                                 Math.Round(centreOff, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                 " mm away and its radius differs by " +
                                 Math.Round(radiusOff, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                 " mm.";
            }
            catch (Exception ex)
            {
                o["verified"] = false;
                o["means"] = "the built curve could not be re-read: " + ex.Message;
            }
            return o;
        }

        private sealed class Plan
        {
            public int Index; public string Kind; public JObject Input; public double Scale, Elevation, Height, Offset, SlopeRadians;
            public XYZ Start, End; public Level Level; public Element Type, SystemType; public IList<CurveLoop> Loops;
            /// <summary>Open runs of curves, for the kinds Revit does not take as a boundary.</summary>
            public IList<List<Curve>> Chains;
            /// <summary>Every element this row made BESIDE the one it reports. Carried, never dropped.</summary>
            public List<ElementId> AlsoCreated = new List<ElementId>();
            /// <summary>A point ON the arc between Start and End; null for a straight element.</summary>
            public XYZ ArcThird;
            /// <summary>What the caller declared, kept so post-commit verification can check the built curve against it.</summary>
            public XYZ ArcCentre;
            /// <summary>Whether the row asked for a load-bearing element. Null: leave the document's default.</summary>
            public bool? Structural;

            /// <summary>
            /// A NAME OR NUMBER THE CALLER ASKED FOR, and null when they did not.
            ///
            /// Null and empty are different requests: null means "leave whatever
            /// Revit chose", and a room whose number Revit set to 7 is not the
            /// same finding as a room somebody asked to call 7.
            /// </summary>
            public string WantName;
            public string WantNumber;

            /// <summary>shaft: the two levels it runs BETWEEN, which is what makes it a shaft.</summary>
            public Level BaseLevel;
            public Level TopLevel;
            /// <summary>room_separator: the view whose sketch plane the lines live on.</summary>
            public View SeparatorView;
            /// <summary>room_separator: how many curves Revit actually made, re-read after the call.</summary>
            public int SeparatorSegments;
            /// <summary>The bore the row declared, in FEET. Null: the type decides.</summary>
            public double? Diameter; public double ArcRadius;
            public StructuralType StructuralType; public JObject Summary;
            public string FittingSubtype; public List<FittingMember> FittingMembers;
            public Element TakeoffMain; public int? TakeoffMainBatchIndex;
            public Wall OpeningHost; public Element InstanceHost;
            public Element SlabHost; public string SlabShape; public XYZ SlabCenter;
            public double SlabWidth, SlabHeight, RotationRadians;
            public List<XYZ> ProfilePoints; public XYZ BeamDirection;
            public double BeamSpacing; public FamilySymbol BeamType;
            public Wall FoundationWall;
            public MEPCurve InlineHost; public XYZ InlinePoint;
            public string SystemName; public List<Element> SystemMembers;
            /// <summary>The exact connector id validated per member, so create time uses
            /// what the plan checked rather than choosing again.</summary>
            public List<int> SystemMemberConnectors;
            // Extra resolved facts frozen into the token's plan, keyed per row.
            public Dictionary<string, string> ExtraPlanFacts;
        }
        private sealed class FittingMember
        {
            public Element Owner; public int ConnectorId; public ConnectorFact Fact;
            /// <summary>The member is an EARLIER ENTRY of this same batch; resolved at create time.</summary>
            public int? BatchIndex;
            /// <summary>Named connector id, when the caller chose one for a deferred member.</summary>
            public int? NamedConnector;
        }
        private sealed class Created
        {
            public int Index; public string Kind; public ElementId Id, ExpectedTypeId;
            public StructuralType? ExpectedStructuralType;
            // The joints the fitting must have closed: re-read after the commit.
            public List<FittingMember> ExpectedConnected;
            public bool ExpectedInlineConnections;
            public ElementId ExpectedHostId;
            // An MEP system is verified by what it IS after the commit: its name, the
            // type it was created from, and the members it actually carries.
            public string ExpectedSystemName; public ElementId ExpectedSystemTypeId;
            public List<ElementId> ExpectedMembers;
            /// <summary>An arc the caller DECLARED, null for a straight element. Checked after the commit.</summary>
            public XYZ ExpectedArcCentre;
            public bool? ExpectedStructural;
            public string ExpectedName;
            public string ExpectedNumber;
            /// <summary>Siblings one call made beside the row's own element. Never dropped.</summary>
            public List<ElementId> AlsoCreated = new List<ElementId>();
            public double? ExpectedDiameter; public double ExpectedArcRadius; public bool ExpectedArc;
        }
    }
}
