using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private static void RecordCreated(Document doc, Plan plan, Element element, List<Created> created)
        {
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
                            Index = plan.Index, Kind = plan.Kind, Id = element.Id, Plan = plan,
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
        private static JObject VerifyProductionProperties(Document doc, Created made)
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
                return verifyRow;
        }
    }
}
