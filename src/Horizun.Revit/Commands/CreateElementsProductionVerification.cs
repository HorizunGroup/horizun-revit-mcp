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
                        // THE SECTION, IN THE SAME TRANSACTION, set and re-read like the bore.
                        if (plan.SectionWidth.HasValue && plan.SectionHeight.HasValue)
                        {
                            Parameter pw = element.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                            Parameter ph = element.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                            if (pw == null || ph == null || pw.IsReadOnly || ph.IsReadOnly)
                                throw new InvalidOperationException(
                                    "item " + plan.Index + ": this duct exposes no settable width and height. Nothing was created.");
                            pw.Set(plan.SectionWidth.Value);
                            ph.Set(plan.SectionHeight.Value);
                        }
                        created.Add(new Created
                        {
                            Index = plan.Index, Kind = plan.Kind, Id = element.Id, Plan = plan, Batch = created,
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
                            ExpectedWidth = plan.SectionWidth,
                            ExpectedHeight = plan.SectionHeight,
                            ExpectedSystemName = plan.SystemName,
                            ExpectedSystemTypeId = plan.Kind == "mep_system" ? plan.SystemType?.Id : null,
                            ExpectedMembers = plan.SystemMembers?.Select(m => m.Id).ToList()
                        });
        }
        /// <summary>
        /// Width, height, shape and orientation of a rectangular run, read from the model:
        /// the width/height parameters (0.1 mm), every end connector's Shape and Width/Height,
        /// and - for a run that is not vertical - that the connector's width axis (BasisX)
        /// lies horizontal, which is what "width WxH" means on a plan.
        /// </summary>
        private static JObject VerifySection(Element element, double width, double height)
        {
            const double tol = 0.1 / 304.8;
            double? w = null, h = null;
            try { w = element.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble(); } catch { }
            try { h = element.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble(); } catch { }
            bool paramsOk = w.HasValue && h.HasValue && Math.Abs(w.Value - width) <= tol && Math.Abs(h.Value - height) <= tol;
            var connectors = new JArray();
            bool shapeOk = true, orientOk = true, connSizeOk = true;
            int ends = 0, unreadableConnectors = 0;
            string orientation = "not_applicable";
            XYZ axis = null;
            try
            {
                if ((element.Location as LocationCurve)?.Curve is Line ln) axis = ln.Direction;
            }
            catch { }
            bool vertical = axis != null && Math.Abs(axis.Z) > 0.999;
            ConnectorManager manager = MepFacts.ManagerOf(element);
            if (manager != null)
                foreach (Connector c in MepFacts.Ordered(manager))
                {
                    if (c.ConnectorType != ConnectorType.End) continue;
                    ends++;
                    string shape = Safe(() => c.Shape.ToString());
                    bool isRect = c.Shape == ConnectorProfileType.Rectangular;
                    // An end connector whose size cannot be read is UNMEASURED - it used to be
                    // published as 0 x 0 mm, a measurement nobody took.
                    double cw = 0, ch = 0; bool sizeRead = true;
                    try { cw = c.Width; ch = c.Height; } catch { sizeRead = false; unreadableConnectors++; }
                    bool sizeOk = sizeRead && isRect && Math.Abs(cw - width) <= tol && Math.Abs(ch - height) <= tol;
                    XYZ bx = null;
                    try { bx = c.CoordinateSystem.BasisX; } catch { }
                    bool horizontalWidth = bx != null && Math.Abs(bx.Z) < 1e-6;
                    shapeOk &= isRect;
                    connSizeOk &= sizeOk;
                    if (!vertical) orientOk &= horizontalWidth;
                    connectors.Add(new JObject
                    {
                        ["connector"] = c.Id, ["shape"] = shape,
                        ["width_mm"] = sizeRead ? (JToken)Math.Round(cw * 304.8, 3) : JValue.CreateNull(),
                        ["height_mm"] = sizeRead ? (JToken)Math.Round(ch * 304.8, 3) : JValue.CreateNull(),
                        ["width_axis"] = bx == null ? null : new JArray(Math.Round(bx.X, 6), Math.Round(bx.Y, 6), Math.Round(bx.Z, 6)),
                        ["connected"] = Safe(() => c.IsConnected.ToString()) == "True"
                    });
                }
            if (ends == 0) { shapeOk = false; connSizeOk = false; }
            if (!vertical) orientation = orientOk ? "width_horizontal" : "width_not_horizontal";
            bool ok = paramsOk && shapeOk && connSizeOk && orientOk;
            return new JObject
            {
                ["requested_width_mm"] = Math.Round(width * 304.8, 3),
                ["requested_height_mm"] = Math.Round(height * 304.8, 3),
                ["read_width_mm"] = w.HasValue ? (JToken)Math.Round(w.Value * 304.8, 3) : JValue.CreateNull(),
                ["read_height_mm"] = h.HasValue ? (JToken)Math.Round(h.Value * 304.8, 3) : JValue.CreateNull(),
                ["shape_verified"] = shapeOk,
                ["connector_size_verified"] = connSizeOk,
                ["orientation"] = orientation,
                ["end_connectors"] = connectors,
                ["unreadable_connectors"] = unreadableConnectors,
                ["verified"] = ok
            };
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
                bool? structuralRead = null;
                if (made.ExpectedStructural.HasValue)
                {
                    bool? readBack = StructuralOf(element);
                    structuralRead = readBack;
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
                double? diameterRead = null;
                if (made.ExpectedDiameter.HasValue)
                {
                    double? readBack = DiameterOf(element);
                    diameterRead = readBack;
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

                // THE SECTION, RE-READ: the two parameters, the shape of the connectors, and
                // which way the width lies. A rectangular run whose width stood vertical is the
                // requested numbers in the wrong building, so orientation is part of the check.
                bool sectionMatches = true;
                JObject sectionRow = null;
                if (made.ExpectedWidth.HasValue && made.ExpectedHeight.HasValue)
                {
                    sectionRow = VerifySection(element, made.ExpectedWidth.Value, made.ExpectedHeight.Value);
                    sectionMatches = (bool)sectionRow["verified"];
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

                // THE SAME COMPARISONS, AS A TYPED CHECKLIST. The booleans above decide as they
                // always did; the checklist adds the two protections they lack by construction:
                // it knows WHICH properties this row asked for (so one silently dropped from the
                // code cannot pass by omission), and a value that could not be re-read is
                // UNMEASURED rather than a comparison against a default. The row now verifies
                // only when both agree.
                PostconditionCheck production = ProductionChecklist(doc, made, element,
                    kindMatches, typeMatches, structuralTypeMatches, connectorsMatch,
                    inlineConnectionsMatch, inlineConnectionsRow, structuralRead, diameterRead,
                    sectionRow, sectionMatches, identityRow, hostMatches, systemRow, systemMatches,
                    curveRow, curveMatches);

                bool rowVerified = kindMatches && typeMatches && structuralTypeMatches && connectorsMatch &&
                                   inlineConnectionsMatch &&
                                   hostMatches && systemMatches && curveMatches && structuralMatches &&
                                   diameterMatches && sectionMatches && identityMatches &&
                                   production.AllVerified;

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
                if (sectionRow != null) verifyRow["section_verified"] = sectionRow;
                if (systemRow != null) verifyRow["mep_system"] = systemRow;
                if (made.ExpectedConnected != null) verifyRow["connectors_verified"] = connectorsMatch;
                if (inlineConnectionsRow != null) verifyRow["inline_connections"] = inlineConnectionsRow;
                if (made.ExpectedHostId != null) verifyRow["host_verified"] = hostMatches;
                verifyRow["production_postconditions"] = production.ToJson();
                return verifyRow;
        }

        /// <summary>
        /// The production properties of one created row as a PostconditionCheck: exactly the
        /// properties this row declared, each recorded with what was requested and what the
        /// committed model returned, or as unmeasured when it could not be read.
        /// </summary>
        private static PostconditionCheck ProductionChecklist(Document doc, Created made, Element element,
            bool kindMatches, bool typeMatches, bool structuralTypeMatches, bool connectorsMatch,
            bool inlineConnectionsMatch, JObject inlineConnectionsRow, bool? structuralRead, double? diameterRead,
            JObject sectionRow, bool sectionMatches, JObject identityRow, bool hostMatches,
            JObject systemRow, bool systemMatches, JObject curveRow, bool curveMatches)
        {
            var required = new List<string> { "present", "kind" };
            if (made.ExpectedTypeId != null) required.Add("type_id");
            if (made.ExpectedStructuralType != null) required.Add("structural_type");
            if (made.ExpectedConnected != null) required.Add("fitting_connections");
            if (made.ExpectedInlineConnections) required.Add("inline_connections");
            if (made.ExpectedStructural.HasValue) required.Add("structural");
            if (made.ExpectedDiameter.HasValue) required.Add("diameter");
            if (made.ExpectedWidth.HasValue && made.ExpectedHeight.HasValue) required.Add("section");
            if (made.ExpectedName != null) required.Add("name");
            if (made.ExpectedNumber != null) required.Add("number");
            if (made.ExpectedHostId != null) required.Add("host_id");
            if (made.ExpectedSystemName != null) required.Add("mep_system");
            if (curveRow != null) required.Add("curve");
            var check = new PostconditionCheck(required.ToArray());

            check.Compare("present", true, element != null);
            if (element == null)
            {
                foreach (string property in required.Skip(1))
                    check.Unreadable(property, JValue.CreateNull(), "the element is not in the committed model");
                return check;
            }

            check.Record("kind", made.Kind, element.GetType().Name, kindMatches);
            if (made.ExpectedTypeId != null)
            {
                long? typeRead = null;
                try { typeRead = Rid.Value(element.GetTypeId()); } catch { }
                if (typeRead.HasValue) check.Record("type_id", Rid.Value(made.ExpectedTypeId), typeRead.Value, typeMatches);
                else check.Unreadable("type_id", Rid.Value(made.ExpectedTypeId), "GetTypeId could not be read");
            }
            if (made.ExpectedStructuralType != null)
            {
                string read = Safe(() => (element as FamilyInstance)?.StructuralType.ToString());
                if (read != null) check.Record("structural_type", made.ExpectedStructuralType.Value.ToString(), read, structuralTypeMatches);
                else check.Unreadable("structural_type", made.ExpectedStructuralType.Value.ToString(), "not a FamilyInstance, or StructuralType could not be read");
            }
            if (made.ExpectedConnected != null)
                check.Record("fitting_connections", made.ExpectedConnected.Count,
                             connectorsMatch ? (JToken)made.ExpectedConnected.Count : "not every approved connector re-reads as connected",
                             connectorsMatch);
            if (made.ExpectedInlineConnections)
                check.Record("inline_connections", 2, inlineConnectionsRow?["distinct_pipes"], inlineConnectionsMatch);
            if (made.ExpectedStructural.HasValue)
            {
                if (structuralRead.HasValue) check.Compare("structural", made.ExpectedStructural.Value, structuralRead.Value);
                else check.Unreadable("structural", made.ExpectedStructural.Value, "the structural flag could not be read");
            }
            if (made.ExpectedDiameter.HasValue)
            {
                if (diameterRead.HasValue)
                    check.Measure("diameter", made.ExpectedDiameter.Value, diameterRead.Value, 0.1 / 304.8, "feet",
                                  "diameter parameter read back after commit");
                else check.Unreadable("diameter", made.ExpectedDiameter.Value, "the diameter parameter could not be read");
            }
            if (made.ExpectedWidth.HasValue && made.ExpectedHeight.HasValue)
            {
                bool readable = sectionRow != null && sectionRow["read_width_mm"]?.Type != JTokenType.Null &&
                                sectionRow["read_height_mm"]?.Type != JTokenType.Null &&
                                sectionRow["unreadable_connectors"]?.Value<int>() == 0;
                var requested = new JObject { ["width_mm"] = Math.Round(made.ExpectedWidth.Value * 304.8, 3), ["height_mm"] = Math.Round(made.ExpectedHeight.Value * 304.8, 3) };
                if (readable) check.Record("section", requested, sectionRow, sectionMatches);
                else check.Unreadable("section", requested, "the section parameters or an end connector's size could not be read");
            }
            if (made.ExpectedName != null)
                check.Record("name", made.ExpectedName, identityRow?["name_read"], identityRow?.Value<bool?>("name_verified") == true);
            if (made.ExpectedNumber != null)
                check.Record("number", made.ExpectedNumber, identityRow?["number_read"], identityRow?.Value<bool?>("number_verified") == true);
            if (made.ExpectedHostId != null)
            {
                long? hostRead = null;
                try
                {
                    Element host = (element as Opening)?.Host ?? (element as FamilyInstance)?.Host;
                    if (host != null) hostRead = Rid.Value(host.Id);
                }
                catch { }
                check.Record("host_id", Rid.Value(made.ExpectedHostId), hostRead.HasValue ? (JToken)hostRead.Value : JValue.CreateNull(), hostMatches);
            }
            if (made.ExpectedSystemName != null)
                check.Record("mep_system", made.ExpectedSystemName, systemRow, systemMatches);
            if (curveRow != null)
            {
                if (curveRow.Value<bool?>("measured") == false)
                    check.Unreadable("curve", curveRow["requested"] ?? JValue.CreateNull(), (string)curveRow["means"]);
                else check.Record("curve", curveRow["requested"] ?? JValue.CreateNull(), curveRow, curveMatches);
            }
            return check;
        }
    }
}
