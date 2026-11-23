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
using System.Globalization;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand : ICommand
    {
        public string Name => "horizun_create_elements";
        public string Description => "Create architectural, structural and MEP BIM elements atomically, then re-read every created id.";

        private static Plan PlanItem(Document doc, int index, JObject item, string kind, double scale, out string error,
                                     out string unsupportedReason)
        {
            error = null; unsupportedReason = null;
            var p = new Plan { Index = index, Kind = kind, Input = item, Scale = scale };
            try
            {
                switch (kind)
                {
                    case "stairs": PlanStairs(doc,p); break;
                    case "wall_profile": PlanProfileWall(doc,p); break;
                    case "displacement": PlanDisplacement(doc,p); break;
                    case "toposolid": PlanToposolid(doc, item, p, scale); break;
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
                        p.GridExtent3D = GridExtentRules.Parse(item.Value<string>("extent_3d"), out string gridExtentError);
                        if (gridExtentError != null) throw new ArgumentException(gridExtentError);
                        // A grid is a vertical datum plane: Revit keeps only its plan line, so a
                        // Z on start/end could never read back ("asked 20.5, model has 0"). It is
                        // flattened to the plan and the plan row says so.
                        if (GridExtentRules.NeedsFlatten(p.Start.Z, p.End.Z))
                        {
                            p.GridFlattenNote = GridExtentRules.FlattenNote(p.Start.Z / scale, p.End.Z / scale, (item.Parent?.Parent as JObject)?.Value<string>("units") ?? "mm");
                            p.Start = new XYZ(p.Start.X, p.Start.Y, 0); p.End = new XYZ(p.End.X, p.End.Y, 0);
                        }
                        NonZero(p.Start, p.End);
                        p.WantName = Trimmed(item, "name");
                        break;
                    case "wall":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        ReadArc(item["arc"], p, scale);
                        p.Structural = item.Value<bool?>("structural");
                        p.Level = Need<Level>(doc, item, "level_id");
                        p.Type = PickArchitectureType(doc, item,
                            new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>(), p.Assumptions);
                        if (item["height"] != null) p.Height = Finite(item.Value<double>("height"), "height") * scale;
                        else InferWallHeight(doc, p);
                        if (p.Height <= 0) throw new ArgumentException("height must be positive");
                        p.Offset = Finite(item.Value<double?>("offset") ?? 0, "offset") * scale;
                        break;
                    case "floor":
                        p.Structural = item.Value<bool?>("structural");
                        p.Level = Need<Level>(doc, item, "level_id");
                        p.Type = PickArchitectureType(doc, item, new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>(), p.Assumptions);
                        p.Loops = Loops(item["profile"], scale, doc.Application.ShortCurveTolerance);
                        RequireHorizontal(p.Loops, "floor");
                        ReadCeilingSlope(p);   // a sloped floor takes the same arrow + slope as a sloped ceiling
                        break;
                    case "ceiling":
                        p.Level = Need<Level>(doc, item, "level_id");
                        p.Type = PickArchitectureType(doc, item, new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<CeilingType>(), p.Assumptions);
                        p.Loops = Loops(item["profile"], scale, doc.Application.ShortCurveTolerance);
                        RequireHorizontal(p.Loops, "ceiling");
                        ReadCeilingSlope(p);
                        break;
                    case "roof":
                        p.Level = Need<Level>(doc, item, "level_id");
                        p.Type = PickArchitectureType(doc, item, new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>(), p.Assumptions);
                        p.Loops = Loops(item["profile"], scale, doc.Application.ShortCurveTolerance);
                        RequireHorizontal(p.Loops, "roof");
                        ReadRoofSlopes(p);
                        break;
                    case "reference_plane":
                    {
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true);
                        p.WantName = Trimmed(item, "name");
                        View refView = Optional<View>(doc, item, "view_id");
                        if (refView == null || refView.IsTemplate || !(refView is ViewPlan || refView is ViewSection))
                            throw new ArgumentException("view_id must identify a plan, elevation or section view (not a template or a 3D view): the reference plane is drawn in it.");
                        XYZ viewDirection = refView.ViewDirection;
                        double[] cut = ReferencePlaneRules.CutVector(new[] { viewDirection.X, viewDirection.Y, viewDirection.Z });
                        string refError = ReferencePlaneRules.Validate(p.WantName, new[] { p.Start.X, p.Start.Y, p.Start.Z }, new[] { p.End.X, p.End.Y, p.End.Z },
                            refView.ViewType.ToString());
                        if (refError == null && cut == null) refError = "the view's direction could not be read.";
                        if (refError == null && ReferencePlaneRules.LineAlongCutVector(new[] { p.Start.X, p.Start.Y, p.Start.Z }, new[] { p.End.X, p.End.Y, p.End.Z }, cut))
                            refError = "start-end runs along the view's direction, so it would be a point in that view, not a line.";
                        if (refError != null) throw new ArgumentException(refError);
                        if (new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().Any(r => string.Equals(r.Name, p.WantName, StringComparison.Ordinal)))
                            throw new ArgumentException("a reference plane named '" + p.WantName + "' already exists; the name identifies the plane, so pick another.");
                        p.SeparatorView = refView;
                        p.RefCut = new XYZ(cut[0], cut[1], cut[2]);
                        break;
                    }
                    case "room":
                        p.Level = Need<Level>(doc, item, "level_id"); p.Start = Point(item["point"], scale, false);
                        // A DRAWING CANNOT SUPPLY THESE. Text is unreachable from
                        // imported DWG geometry, so a room's name and number come
                        // from whoever wrote the requirement set, or from nowhere.
                        p.WantName = Trimmed(item, "name");
                        p.WantNumber = Trimmed(item, "number");
                        if (item["enclosed_from"] != null) PlanEnclosed(doc, item, p);
                        break;
                    // Space.Create(level, uv): a 2D point on a level, exactly like a room.
                    // Verified: level_id (generic), the placement point (generic, 2D like
                    // room), IsPointInSpace (below), and area>0-or-unbounded (reported, not
                    // asserted - see ReadCreated).
                    case "space":
                        p.Level = Need<Level>(doc, item, "level_id"); p.Start = Point(item["point"], scale, false);
                        if (item["enclosed_from"] != null) PlanEnclosed(doc, item, p);
                        break;
                    // Area.Create(areaView, uv): a point in an AREA PLAN view, not a level -
                    // Revit finds the enclosing AreaBoundaryLine loop through the view.
                    case "area":
                    {
                        var areaView = Optional<View>(doc, item, "view_id") as ViewPlan;
                        if (areaView == null || areaView.ViewType != ViewType.AreaPlan)
                            throw new ArgumentException(
                                "area needs view_id naming an AREA PLAN view (ViewType.AreaPlan) - the loop of " +
                                "area_boundary lines the point falls inside is read through that view, and " +
                                "Revit does not check a wrong view, it stops.");
                        p.SeparatorView = areaView;
                        p.Start = Point(item["point"], scale, false);
                        break;
                    }
                    // AreaBoundaryLine, one per curve of the profile chain - same shape as
                    // room_separator, but for an AREA PLAN view and area_boundary's own
                    // single-curve API (there is no plural NewAreaBoundaryLines).
                    case "area_boundary":
                    {
                        var boundaryView = Optional<View>(doc, item, "view_id") as ViewPlan;
                        if (boundaryView == null || boundaryView.ViewType != ViewType.AreaPlan)
                            throw new ArgumentException(
                                "area_boundary needs view_id naming an AREA PLAN view (ViewType.AreaPlan). Revit " +
                                "does not check a wrong view, it stops.");
                        Level boundaryLevel = null;
                        try { boundaryLevel = boundaryView.GenLevel; } catch { }
                        if (boundaryLevel == null)
                            throw new ArgumentException("area_boundary: the named view has no readable storey (GenLevel).");
                        p.SeparatorView = boundaryView; p.Level = boundaryLevel;
                        p.Chains = Chains(item["profile"] as JArray, scale);
                        if (p.Chains.Count < 1) throw new ArgumentException("area_boundary needs at least one chain of curves");
                        RequireHorizontalChains(p.Chains, "area_boundary");
                        break;
                    }
                    // A sprinkler is a family_instance restricted to OST_Sprinklers: same
                    // routing (level/host/face), same placement, same postconditions - the
                    // category check is the only thing this kind adds over the generic route.
                    case "sprinkler":
                        if (Need<FamilySymbol>(doc, item, "type_id") is FamilySymbol sprinklerSymbol &&
                            !InCategory(sprinklerSymbol, BuiltInCategory.OST_Sprinklers))
                            throw new ArgumentException("sprinkler type_id must identify a FamilySymbol in OST_Sprinklers");
                        goto case "family_instance";
                    case "family_instance":
                        p.Type = Need<FamilySymbol>(doc, item, "type_id"); p.Start = Point(item["point"], scale, true);
                        p.Level = Optional<Level>(doc, item, "level_id");
                        // Hosted placement: the host is resolved NOW and re-read from the
                        // created instance after commit (host_verified). A symbol whose
                        // family cannot live on this host fails Revit's own placement,
                        // atomically, with the batch rolled back.
                        p.InstanceHost = Optional<Element>(doc, item, "host_id");
                        // find_host: the agent names the KIND of host and a radius; the nearest one is taken
                        // (walls: by distance to the location line, and the point is moved ONTO that line).
                        if (p.InstanceHost == null && item["find_host"] is JObject findHost)
                        {
                            p.InstanceHost = FindHost(doc, findHost, p.Start, p.Level, scale, out XYZ snapped, out JObject how);
                            if (snapped != null) p.Start = snapped;
                            item["host_found"] = how;
                        }
                        // The family states its host; a mismatch is refused HERE, before Revit
                        // improvises (a ceiling-hosted luminaire was accepted on a wall).
                        string requiredHost = Horizun.Revit.Core.FamilyPlacementInfo.RequiredHost((FamilySymbol)p.Type);
                        if (requiredHost != null && requiredHost != "face")
                        {
                            if (p.InstanceHost == null)
                                throw new ArgumentException("type_id is a " + requiredHost + "-hosted family: give host_id of a " + requiredHost +
                                    " (horizun_query_model returns each type's placement.host)");
                            if (!Horizun.Revit.Core.FamilyPlacementInfo.HostMatches(requiredHost, p.InstanceHost))
                                throw new ArgumentException("type_id is a " + requiredHost + "-hosted family but host_id " + Rid.Value(p.InstanceHost.Id) +
                                    " is a " + p.InstanceHost.GetType().Name + "; give a " + requiredHost);
                        }
                        string hostFace = item.Value<string>("host_face");
                        if (hostFace != null && hostFace != "side" && hostFace != "end")
                            throw new ArgumentException("host_face must be 'side' or 'end'");
                        if (hostFace == "end" && p.InstanceHost == null)
                            throw new ArgumentException("host_face 'end' needs host_id: the wall whose end the device is on");
                        p.RotationRadians = (item.Value<double?>("rotation_degrees") ?? 0) * System.Math.PI / 180.0;
                        StructuralType parsed;
                        string defaultStructuralType = InCategory((FamilySymbol)p.Type, BuiltInCategory.OST_StructuralFoundation)
                            ? "Footing" : "NonStructural";
                        if (!Enum.TryParse(item.Value<string>("structural_type") ?? defaultStructuralType, true, out parsed) ||
                            !Enum.IsDefined(typeof(StructuralType), parsed))
                            throw new ArgumentException("structural_type is invalid");
                        p.StructuralType = parsed;
                        break;
                    case "duct":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Diameter = ReadDiameter(item, scale);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Need<DuctType>(doc, item, "type_id");
                        p.SystemType = Need<MechanicalSystemType>(doc, item, "system_type_id");
                        ReadSection(p, item, scale);
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
                    // FlexPipe.Create/FlexDuct.Create take the PATH directly - no start/end pair, a
                    // list of points including both ends. Start/End are kept as the first/last point
                    // so every generic check that reads them (join rules, endpoint postconditions
                    // this kind opts out of below) still has something to read.
                    case "flex_pipe":
                        p.FlexPoints = ReadPoints(item["points"], scale);
                        p.Start = p.FlexPoints[0]; p.End = p.FlexPoints[p.FlexPoints.Count - 1];
                        p.Diameter = ReadDiameter(item, scale);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Need<FlexPipeType>(doc, item, "type_id");
                        p.SystemType = Need<PipingSystemType>(doc, item, "system_type_id");
                        break;
                    case "flex_duct":
                    {
                        p.FlexPoints = ReadPoints(item["points"], scale);
                        p.Start = p.FlexPoints[0]; p.End = p.FlexPoints[p.FlexPoints.Count - 1];
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Need<FlexDuctType>(doc, item, "type_id");
                        p.SystemType = Need<MechanicalSystemType>(doc, item, "system_type_id");
                        // FlexDuctType exposes no public Shape, unlike DuctType - the shape is
                        // whatever the created instance's connectors turn out to be, read AFTER
                        // commit. So diameter and width/height are only checked for being
                        // mutually exclusive and positive here; the wrong one for the type's real
                        // shape fails at commit with Revit's own "no settable parameter" refusal.
                        double? flexDia = item.Value<double?>("diameter");
                        JToken flexW = item["width"], flexH = item["height"];
                        if (flexDia != null && (flexW != null || flexH != null))
                            throw new ArgumentException("diameter AND width/height were both given: two sections for one run. Send one.");
                        if ((flexW == null) != (flexH == null))
                            throw new ArgumentException("width and height come together: a section with one side is not a section");
                        if (flexDia != null) p.Diameter = ReadDiameter(item, scale);
                        else if (flexW != null)
                        {
                            double w = Finite(flexW.Value<double>(), "width") * scale, h = Finite(flexH.Value<double>(), "height") * scale;
                            if (w <= 0 || h <= 0) throw new ArgumentException("width and height must be positive");
                            p.SectionWidth = w; p.SectionHeight = h;
                        }
                        break;
                    }
                    // Ramps have no creation surface in the public Revit API in ANY of 2023-2027:
                    // no Ramp class, no Ramp.Create, nothing beyond StairRampFailures (an enum
                    // SHARED with stairs failures) in RevitAPI.xml. horizun_execute_python cannot
                    // build one either - it is the same public API, not a bigger one - so this is
                    // refused as an ordinary argument error rather than a capability gap: granting
                    // the Python fallback here would send a caller to try something that fails the
                    // same way for the same reason. Model it with Revit's own Ramp tool (Architecture
                    // tab > Ramp) or, for coordination geometry only, a sloped floor/generic model.
                    case "ramp":
                        throw new ArgumentException(
                            "kind='ramp' is refused: Revit's public API has no Ramp class or Ramp.Create method " +
                            "in any supported year (2023-2027), so neither this command nor horizun_execute_python " +
                            "can create one. Use Revit's own Ramp tool (Architecture tab > Ramp) and, if the model " +
                            "needs it back for coordination, read it afterwards with horizun_query_model; or, for " +
                            "geometry-only coordination, model its shape as a sloped floor or generic model.");
                    case "cable_tray":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Diameter = ReadDiameter(item, scale);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Optional<CableTrayType>(doc, item, "type_id");
                        if (p.Type == null) p.Type = new FilteredElementCollector(doc).OfClass(typeof(CableTrayType)).Cast<CableTrayType>().FirstOrDefault();
                        if (p.Type == null) throw new ArgumentException("type_id was omitted and the document has no CableTrayType");
                        break;
                    case "structural_framing":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        if (item["join_rule"] != null &&
                            item.Value<string>("join_rule") != "none" && item.Value<string>("join_rule") != "auto")
                            throw new ArgumentException("structural_framing join_rule must be none or auto.");
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
                        double z = p.Level.ProjectElevation;
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
                        if (item["top_elevation"] != null)
                            p.FoundationTop = Finite(item.Value<double>("top_elevation"), "top_elevation") * scale;
                        if (item["width"] != null)
                        {
                            p.FoundationWidth = Finite(item.Value<double>("width"), "width") * scale;
                            if (p.FoundationWidth <= 0) throw new ArgumentException("wall_foundation width must be positive");
                            if (!((p.FoundationWall.Location as LocationCurve)?.Curve is Line))
                                throw new ArgumentException("wall_foundation width requires a straight host wall to measure across its axis");
                        }
                        if (item["thickness"] != null)
                        {
                            p.FoundationThickness = Finite(item.Value<double>("thickness"), "thickness") * scale;
                            if (p.FoundationThickness <= 0) throw new ArgumentException("wall_foundation thickness must be positive");
                        }
                        p.ExtraPlanFacts = new Dictionary<string, string>();
                        foreach (Parameter parameter in p.Type.Parameters)
                            if (parameter.StorageType == StorageType.Double)
                                p.ExtraPlanFacts["type_double:" + Rid.Value(parameter.Id)] =
                                    Math.Round(parameter.AsDouble(), 8).ToString(CultureInfo.InvariantCulture);
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
                        // closed=true now explicitly requests a closing edge.
                        p.Chains = Chains(item["profile"] as JArray, scale, item.Value<bool?>("closed") == true);
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
                        if ((item["start"] != null || item["end"] != null) && (item["corner_1"] != null || item["corner_2"] != null))
                            throw new ArgumentException("Use start/end or corner_1/corner_2, not both.");
                        p.Start = Point(item["corner_1"] ?? item["start"], scale, true);
                        p.End = Point(item["corner_2"] ?? item["end"], scale, true);
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

                        p.OpeningHost = openingHost; p.Host = openingHost;
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
                        if (subtype == "cap") { PlanCap(doc, p, item); break; }
                        // takeoff_or_tee (2026-10-18): a takeoff whose main's type has no Tap
                        // preference falls back to a TEE - the main is split at the branch point
                        // and the type's own Tee junction rule is used. Asked for explicitly,
                        // reported as fallback=tee, the new main piece listed as also created.
                        if (subtype == "takeoff_or_tee") { subtype = "takeoff"; p.TakeoffTeeFallbackAllowed = true; }
                        if (subtype != "elbow" && subtype != "union" && subtype != "transition" && subtype != "tee" &&
                            subtype != "takeoff" && subtype != "cross")
                            throw new ArgumentException("fitting must name one of: elbow, union, transition, tee, takeoff, takeoff_or_tee, cross");
                        p.FittingSubtype = subtype;
                        // A CROSS TAKES FOUR. Revit has had NewCrossFitting in every
                        // year this bridge supports, and offering only up to a tee meant
                        // a four-way junction had to be refused or built as two tees -
                        // which is a different piece of pipework, with an extra fitting
                        // and a length of main that does not exist.
                        int need = subtype == "cross" ? 4 : subtype == "tee" ? 3 : 2;
                        var membersToken = item["elements"] as JArray;
                        if (membersToken == null || membersToken.Count != need)
                            throw new ArgumentException("elements must contain exactly " + need + " entries for a " + subtype +
                                (subtype == "tee" ? " (the two through-run elements, then the branch)" :
                                 subtype == "cross" ? " (the first through pair, then the second through pair - Revit's own argument order)" :
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
                            bool noTap = mainCurveType?.RoutingPreferenceManager is RoutingPreferenceManager r0 &&
                                (r0.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions) == 0 ||
                                 r0.PreferredJunctionType != PreferredJunctionType.Tap);
                            if (noTap && p.TakeoffTeeFallbackAllowed &&
                                mainCurveType.RoutingPreferenceManager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions) > 0 &&
                                (planMain is Pipe || planMain is Autodesk.Revit.DB.Mechanical.Duct))
                            {
                                p.TakeoffAsTee = true;
                            }
                            else if (mainCurveType?.RoutingPreferenceManager is RoutingPreferenceManager routing &&
                                (routing.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions) == 0 ||
                                 routing.PreferredJunctionType != PreferredJunctionType.Tap))
                                throw new ArgumentException("curve_type_has_no_takeoff_preference: " +
                                    mainCurveType.GetType().Name + " '" + mainCurveType.Name + "' prefers junction type '" +
                                    (routing.PreferredJunctionType.ToString() ?? "(unreadable)") +
                                    "' and NewTakeoffFitting needs Tap with a configured rule. Nothing ran. Two ways on " +
                                    "(derivación sin regla Tap): (1) in Revit's Routing Preferences of that type set " +
                                    "Junction = Tap with a tap fitting; or (2) build a TEE instead: split the main at the " +
                                    "branch point into two runs that meet there, then call fitting 'tee' with the two run " +
                                    "elements first and the branch last - it uses the type's existing Tee junction rule.");
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
                            // Collinear ends take a union, a corner takes an elbow, and an elbow whose two curves leave the
                            // joint on the same side is a 180 degree return no family has (Core/MepGraphRules).
                            string jointProblem = MepGraphRules.ElbowProblem(subtype, turn);
                            if (jointProblem != null)
                                throw new ArgumentException(MepGraphRules.CodeJointTurn + ": the chosen connectors: " + jointProblem);
                            // The same generalisation as at create time: a tee has one
                            // member past the through pair and a cross has two. Handling
                            // only the tee left a cross's members unresolved at plan
                            // time, so the REHEARSAL could not refuse a cross whose
                            // fourth connector was already taken - it was discovered
                            // inside the transaction instead.
                            for (int extra = 2; extra < members.Count; extra++)
                            {
                                ConnectorFact extraFact, unusedCorner; string extraCode, extraReason;
                                if (!MepRules.SelectPair(sides[extra], new List<ConnectorFact> { chosenA },
                                                         named[extra], null,
                                                         out extraFact, out unusedCorner, out extraCode, out extraReason))
                                    throw new ArgumentException(extraCode + " (member " + extra + "): " + extraReason);
                                members[extra].ConnectorId = extraFact.Id; members[extra].Fact = extraFact;
                            }
                            // A tee's third port must not lie along the pair that passes straight through, and that pair
                            // must not return on itself: three ports on one axis have no tee family.
                            if (subtype == "tee" && members.Count == 3)
                            {
                                string teeProblem = MepGraphRules.ExistingJointProblem("tee",
                                    new[] { chosenA.DirX, chosenA.DirY, chosenA.DirZ }, new[] { chosenB.DirX, chosenB.DirY, chosenB.DirZ },
                                    new[] { members[2].Fact.DirX, members[2].Fact.DirY, members[2].Fact.DirZ });
                                if (teeProblem != null)
                                    throw new ArgumentException(MepGraphRules.CodeTeeCollinear + ": " + teeProblem);
                            }
                        }
                        p.FittingMembers = members;
                        p.ExtraPlanFacts = new Dictionary<string, string> { { "fitting.subtype", subtype } };
                        if (p.TakeoffAsTee) p.ExtraPlanFacts["fitting.fallback"] = "tee (no Tap preference on the main's type)";
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
                NormalizePlan(doc, p);
                p.Summary = new JObject { ["index"] = index, ["kind"] = kind, ["references_resolved"] = true };
                if (kind == "wall") p.Summary["height"] = p.Height / scale;
                if (p.TopoSource != null) p.Summary["landxml"] = p.TopoSource;
                // A row an expansion produced or moved (all_enclosed, an architectural recipe) names the caller's row too.
                int? callerIndex = Horizun.Revit.Core.CreationBatch<Plan>.SourceIndex(item);
                if (callerIndex != null) p.Summary["elements_index"] = callerIndex.Value;
                if (kind == "grid")
                {
                    p.Summary["extent_3d"] = p.GridExtent3D;
                    if (p.GridFlattenNote != null) p.Summary["note"] = p.GridFlattenNote;
                }
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
                case "wall_profile": return CreateProfileWall(doc,p);
                case "displacement": return DisplacementElement.Create(doc,p.DisplacedIds,p.Displacement,p.OwnerView,null);
                case "level":
                    Level level = Level.Create(doc, p.Elevation);
                    SetIdentity(level, BuiltInParameter.DATUM_TEXT, p.WantName, "name");
                    return level;
                case "grid":
                    Grid grid = Grid.Create(doc, Line.CreateBound(p.Start, p.End));
                    // Without this the grid shows only in the plans whose cut plane falls inside
                    // its own vertical extent; the rest of the building never sees it.
                    if (p.GridExtent3D == GridExtentRules.Maximize &&
                        GridExtentRules.VerticalRange(new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Elevation),
                                                      out double gridBottom, out double gridTop))
                        grid.SetVerticalExtents(gridBottom, gridTop);   // not Maximize3DExtents: it also moves the ends in plan
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
                    Wall wall = Wall.Create(doc, wallAxis, p.Type.Id, p.Level.Id, p.Height,
                        p.Offset,
                        p.Input.Value<bool?>("flip") == true, p.Structural == true);
                    Parameter wallKey = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                    if (wallKey == null || wallKey.IsReadOnly || !wallKey.Set((int)WallLocationLine.WallCenterline))
                        throw new InvalidOperationException("Wall centerline could not be set.");
                    if (p.TopLevel != null)
                    {
                        if (!wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(p.TopLevel.Id))
                            throw new InvalidOperationException("top_level_id was not accepted");
                        SetDouble(wall, BuiltInParameter.WALL_TOP_OFFSET, p.TopOffset);
                    }

                    // THE JOIN RULE, APPLIED. It has been in the requirement-set
                    // schema since the schema existed, described as "passed
                    // through, applied by the writer", and applied by nobody. A
                    // set saying join_rule: none got joins anyway - which trims a
                    // wall's ends back to whatever it meets, moves the centreline
                    // the drawing gave, and then fails the postcondition that
                    // checks it. MEASURED: 1.6 mm, item 14 of 16, batch rolled back.
                    if (string.Equals(p.Input.Value<string>("join_rule"), "none", StringComparison.Ordinal))
                    {
                        WallUtils.DisallowWallJoinAtEnd(wall, 0);
                        WallUtils.DisallowWallJoinAtEnd(wall, 1);
                        doc.Regenerate();
                    }
                    return wall;
                case "toposolid": return CreateToposolid(doc, p);
                case "reference_plane":
                    ReferencePlane refPlane = doc.Create.NewReferencePlane2(p.Start, p.End, p.RefCut, p.SeparatorView);
                    refPlane.Name = p.WantName;
                    return refPlane;
                case "floor":
                    // Floor.Create(..., isStructural, slopeArrow, slope) is the same overload in Revit 2023-2027.
                    Floor madeFloor = p.CeilingArrow == null
                        ? Floor.Create(doc, p.Loops, p.Type.Id, p.Level.Id)
                        : Floor.Create(doc, p.Loops, p.Type.Id, p.Level.Id, p.Structural == true, p.CeilingArrow, Math.Atan(p.CeilingSlopeRatio));
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
                    SetDouble(madeFloor, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, p.Offset);
                    return madeFloor;
                case "ceiling":
                    Ceiling ceiling = p.CeilingArrow == null
                        ? Ceiling.Create(doc, p.Loops, p.Type.Id, p.Level.Id)
                        : Ceiling.Create(doc, p.Loops, p.Type.Id, p.Level.Id, p.CeilingArrow,
                            Math.Atan(p.CeilingSlopeRatio));
                    SetDouble(ceiling, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM, p.Offset);
                    return ceiling;
                case "roof":
                    var footprint = new CurveArray();
                    foreach (CurveLoop footprintLoop in p.Loops) foreach (Curve curve in footprintLoop) footprint.Append(curve);
                    ModelCurveArray boundaries = new ModelCurveArray();
                    FootPrintRoof roof = doc.Create.NewFootPrintRoof(footprint, p.Level, (RoofType)p.Type, out boundaries);
                    SetDouble(roof, BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM, p.Offset);
                    p.RoofEdges = boundaries.Cast<ModelCurve>().ToList();
                    foreach (ModelCurve edge in p.RoofEdges)
                    {
                        int index = MatchEdge(p, edge.GeometryCurve);
                        roof.set_DefinesSlope(edge, p.DefinesSlope[index]);
                        if (p.DefinesSlope[index]) roof.set_SlopeAngle(edge, p.Slopes[index]);
                        if (p.Overhangs != null && p.Overhangs[index].HasValue) roof.set_Overhang(edge, p.Overhangs[index].Value);
                        if (p.EdgeOffsets != null && p.EdgeOffsets[index].HasValue) roof.set_Offset(edge, p.EdgeOffsets[index].Value);
                    }
                    return roof;
                case "room":
                {
                    if (p.Enclosed) return CreateEnclosed(doc, p);
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
                case "space":
                {
                    if (p.Enclosed) return CreateEnclosed(doc, p);
                    Space space = doc.Create.NewSpace(p.Level, new UV(p.Start.X, p.Start.Y));
                    if (space == null)
                        throw new InvalidOperationException(
                            "Revit placed no space at that point. Nothing was kept - MEASURED: unlike a room, a " +
                            "space CAN be placed unbounded (area 0), which is reported rather than refused; a " +
                            "null here means Revit itself declined, not merely that no boundary was found.");
                    return space;
                }
                case "area":
                {
                    var areaView = (ViewPlan)p.SeparatorView;
                    Area area = doc.Create.NewArea(areaView, new UV(p.Start.X, p.Start.Y));
                    if (area == null)
                        throw new InvalidOperationException(
                            "Revit placed no area at that point. Nothing was kept.");
                    return area;
                }
                case "area_boundary":
                {
                    var boundaryView = (ViewPlan)p.SeparatorView;
                    Plane boundaryPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, p.Level.Elevation));
                    SketchPlane boundarySketch = SketchPlane.Create(doc, boundaryPlane);
                    ModelCurve firstBoundary = null;
                    int boundaryCount = 0;
                    foreach (List<Curve> chain in p.Chains)
                        foreach (Curve curve in chain)
                        {
                            ModelCurve line = doc.Create.NewAreaBoundaryLine(boundarySketch, curve, boundaryView);
                            if (line == null)
                                throw new InvalidOperationException(
                                    "Revit created no area boundary line for one of the curves. Nothing was kept.");
                            if (firstBoundary == null) firstBoundary = line; else p.AlsoCreated.Add(line.Id);
                            boundaryCount++;
                        }
                    if (boundaryCount == 0) throw new ArgumentException("area_boundary was given no curves to create");
                    p.SeparatorSegments = boundaryCount;
                    return firstBoundary;
                }
                case "sprinkler":
                case "family_instance":
                {
                    FamilySymbol symbol = (FamilySymbol)p.Type;
                    if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }

                    // WHICH ROUTE THIS FAMILY NEEDS, read from the family itself.
                    //
                    // MEASURED: every device family in Revit's own electrical
                    // template is WorkPlaneBased, and the wall-host overload
                    // ACCEPTS one and returns an instance with Host == null,
                    // raising nothing. A route chosen by category, or by which
                    // overload happens to compile, builds that silently.
                    string placementType;
                    CadPlacementRoute route = CreateElementsPlacement.RouteFor(symbol, out placementType);
                    FamilyInstance placed;

                    if (route == CadPlacementRoute.Unsupported)
                        throw new InvalidOperationException(
                            "this family is " + placementType + ", which this command does not place. The " +
                            "routes it has are a level, a host element and a face. Nothing was built for this " +
                            "row rather than placing it by a route that fits a different family.");

                    if (route == CadPlacementRoute.Face)
                    {
                        if (p.Host == null)
                            throw new InvalidOperationException(
                                "this family is work-plane based: Revit places it on a FACE, and no host was " +
                                "named for this row. Name the host (hosted_on in a requirement set, host_id " +
                                "here) or use a family that stands on a level.");

                        CadFaceChoice face = CreateElementsPlacement.ChooseFace(
                            doc, p.Host, p.Start, p.Input.Value<double?>("rotation_degrees") * Math.PI / 180.0,
                            p.Input.Value<double?>("face_allowance_mm") ?? 300.0,
                            p.Input.Value<double?>("facing_degrees") * Math.PI / 180.0,
                            p.Input.Value<double?>("side_dead_band_mm"),
                            p.Input.Value<string>("host_face"));
                        if (face.Refusal != null) throw new InvalidOperationException(face.Refusal);

                        placed = doc.Create.NewFamilyInstance(face.Face, face.Point, face.ReferenceDirection, symbol);
                        p.FacePlacement = face;

                        // THE POINT MOVED, ON PURPOSE, AND BY A MEASURED AMOUNT.
                        //
                        // A symbol is drawn beside the wall it belongs to, not on
                        // its face - 134 mm out, on the drawing this was built
                        // from. The device is placed ON the face and the
                        // postcondition checks THAT point, because it is what was
                        // asked of Revit; the row reports the drawn point, the
                        // placed point and the distance between them, so nobody
                        // has to take the projection on trust.
                        face.Evidence["drawn_at_mm"] = new JArray(Math.Round(p.Start.X * 304.8, 1),
                                                                  Math.Round(p.Start.Y * 304.8, 1),
                                                                  Math.Round(p.Start.Z * 304.8, 1));
                        p.Start = face.Point;

                        // THE LEVEL FOLLOWS THE HOST, and the row is told so.
                        //
                        // A face-hosted instance has no level of its own: Revit
                        // answers -1 and no elevation. Checking the row's level_id
                        // against that refuses a placement that is correct, and
                        // dropping it silently would be a key accepted and ignored.
                        // So it is dropped LOUDLY: named here, with the level its
                        // host stands on.
                        if (p.Level != null)
                        {
                            face.Evidence["level_asked_for"] = SafePlanName(p.Level);
                            face.Evidence["level_means"] =
                                "a work-plane based family is hosted by a FACE and carries no level of its own. " +
                                "This row's level was used to resolve the plan and is NOT a property of the " +
                                "created instance; the level it stands on is its host's.";
                            p.Level = null;
                        }
                        doc.Regenerate();
                    }
                    else if (route == CadPlacementRoute.HostedOnElement)
                    {
                        if (p.Host == null)
                            throw new InvalidOperationException(
                                "this family is hosted: Revit will not place it without a host element, and no " +
                                "host was named for this row.");
                        if (string.Equals(p.Input.Value<string>("host_face"), "end", StringComparison.Ordinal))
                            throw new InvalidOperationException(
                                "end_face_needs_a_face_based_family: this family is wall based - Revit places it on " +
                                "the wall's LINE, between its ends, and it cannot stand on a terminal face. Use a " +
                                "work-plane (face) based family for a device on the end of a wall.");
                        // A WALL-BASED FAMILY STANDS ON ITS WALL'S LINE. MEASURED (a test
                        // family from Revit's "Electrical Fixture wall based" template):
                        // asked for the drawn point 98.8 mm off the wall, Revit put the
                        // instance on the wall's location line, and the postcondition
                        // refused the whole atomic stage. So the drawn point is projected
                        // onto the host's line HERE, the side it was drawn on decides the
                        // facing, and both are reported - as the face route does.
                        var hostWall = p.Host as Wall;
                        var hostLine = (hostWall?.Location as LocationCurve)?.Curve as Line;
                        if (hostWall != null && hostLine != null)
                        {
                            XYZ drawn = p.Start;
                            XYZ a = hostLine.GetEndPoint(0), b = hostLine.GetEndPoint(1);
                            XYZ dir = new XYZ(b.X - a.X, b.Y - a.Y, 0).Normalize();
                            double t = (drawn.X - a.X) * dir.X + (drawn.Y - a.Y) * dir.Y;
                            double length = new XYZ(b.X - a.X, b.Y - a.Y, 0).GetLength();
                            if (t < -1e-6 || t > length + 1e-6)
                                throw new InvalidOperationException(
                                    "the drawn point projects beyond the ends of its host wall (" +
                                    Math.Round(t * 304.8, 1).ToString(CultureInfo.InvariantCulture) + " mm along a " + Math.Round(length * 304.8, 1).ToString(CultureInfo.InvariantCulture) +
                                    " mm wall). A wall-based family is placed on the wall's line; nothing was built.");
                            var onLine = new XYZ(a.X + dir.X * t, a.Y + dir.Y * t, drawn.Z);
                            XYZ exterior = hostWall.Orientation;
                            double side = (drawn.X - onLine.X) * exterior.X + (drawn.Y - onLine.Y) * exterior.Y;
                            double? deadBand = p.Input.Value<double?>("side_dead_band_mm");
                            double? facingDeg = p.Input.Value<double?>("facing_degrees");
                            string sideFrom = "the side of the wall line the symbol is drawn on";
                            if (facingDeg.HasValue)
                            {
                                double f = facingDeg.Value * Math.PI / 180.0;
                                double dot = Math.Cos(f) * exterior.X + Math.Sin(f) * exterior.Y;
                                if (Math.Abs(dot) > 0.5) { side = dot; sideFrom = "the facing declared for this block"; }
                            }
                            else if (deadBand.HasValue && Math.Abs(side) * 304.8 < deadBand.Value)
                                throw new InvalidOperationException(
                                    "side_of_the_wall_not_stated: the symbol is drawn " +
                                    Math.Round(Math.Abs(side) * 304.8, 1).ToString(CultureInfo.InvariantCulture) + " mm from its wall's line, inside the " +
                                    deadBand.Value + " mm dead band, and no facing is declared. Nothing was built.");
                            p.HostedEvidence = new JObject
                            {
                                ["route"] = "wall_based_on_the_wall_line",
                                ["drawn_at_mm"] = new JArray(Math.Round(drawn.X * 304.8, 1), Math.Round(drawn.Y * 304.8, 1),
                                                             Math.Round(drawn.Z * 304.8, 1)),
                                ["placed_at_mm"] = new JArray(Math.Round(onLine.X * 304.8, 1), Math.Round(onLine.Y * 304.8, 1),
                                                              Math.Round(onLine.Z * 304.8, 1)),
                                ["offset_from_wall_line_mm"] = Math.Round(side * 304.8, 1),
                                ["side"] = side >= 0 ? "exterior" : "interior",
                                ["side_from"] = sideFrom,
                                ["means"] = "a wall-based family's origin is on its wall's location line; the device " +
                                            "stands on the face its FACING points to, chosen from the side the symbol " +
                                            "is drawn on."
                            };
                            p.Start = onLine;
                            placed = p.Level == null
                                ? doc.Create.NewFamilyInstance(p.Start, symbol, p.Host, p.StructuralType)
                                : doc.Create.NewFamilyInstance(p.Start, symbol, p.Host, p.Level, p.StructuralType);
                            doc.Regenerate();
                            XYZ want = side >= 0 ? exterior : exterior.Negate();
                            XYZ facing = placed.FacingOrientation;
                            if (facing.X * want.X + facing.Y * want.Y < 0)
                            {
                                if (placed.CanFlipFacing)
                                {
                                    placed.flipFacing();
                                    doc.Regenerate();
                                    p.HostedEvidence["facing_by"] = "flip_facing";
                                }
                                else if (ElementTransformUtils.CanMirrorElement(doc, placed.Id))
                                {
                                    // MEASURED: a wall-based family without flip controls answers
                                    // CanFlipFacing false. Its reflection across the wall's own
                                    // plane is the same device on the other face of the same wall;
                                    // the copy is kept and the original removed in this transaction.
                                    ICollection<ElementId> copies = ElementTransformUtils.MirrorElements(
                                        doc, new List<ElementId> { placed.Id },
                                        Plane.CreateByNormalAndOrigin(exterior, onLine), true);
                                    doc.Regenerate();
                                    var reflected = doc.GetElement(copies?.FirstOrDefault() ?? ElementId.InvalidElementId)
                                        as FamilyInstance;
                                    if (reflected == null)
                                        throw new InvalidOperationException(
                                            "the reflection to the drawn face produced no instance. Nothing was built " +
                                            "for this row.");
                                    // MEASURED 2026-10-16: Revit re-hosts a reflected copy on the first wall it finds at
                                    // that place, and in a tower that is another storey's wall stacked on the same line.
                                    if (reflected.Host == null || reflected.Host.Id != hostWall.Id)
                                        throw new InvalidOperationException(
                                            "this family has no flip controls, so the only way to put it on the drawn face is to reflect it, and Revit " +
                                            "re-hosted the reflected copy on another element (" + (reflected.Host == null ? "none" : Rid.Value(reflected.Host.Id).ToString(CultureInfo.InvariantCulture)) +
                                            ") instead of wall " + Rid.Value(hostWall.Id).ToString(CultureInfo.InvariantCulture) + " - typically a wall of another storey on the same plan line. " +
                                            "Give the family flip controls (horizun_create_family (Pro edition) flip_controls=['facing']) and place it again. Nothing was built for this row.");
                                    doc.Delete(placed.Id);
                                    doc.Regenerate();
                                    placed = reflected;
                                    p.HostedEvidence["facing_by"] = "reflected_across_the_wall_line";
                                    if (p.Input.Value<bool?>("flip") == true)
                                    {
                                        // the drawn reflection is this one: nothing more to reflect
                                        p.MirrorMethod = "reflected_copy";
                                        p.FlipDone = true;
                                    }
                                    else
                                    {
                                        // a second reflection, across the plane of the hand, turns the
                                        // copy back into an unreflected device on the same face
                                        XYZ o = ((LocationPoint)placed.Location).Point;
                                        ICollection<ElementId> back = ElementTransformUtils.MirrorElements(
                                            doc, new List<ElementId> { placed.Id },
                                            Plane.CreateByNormalAndOrigin(placed.HandOrientation, o), true);
                                        doc.Regenerate();
                                        var turned = doc.GetElement(back?.FirstOrDefault() ?? ElementId.InvalidElementId)
                                            as FamilyInstance;
                                        if (turned == null)
                                            throw new InvalidOperationException(
                                                "the second reflection produced no instance. Nothing was built for this row.");
                                        doc.Delete(placed.Id);
                                        doc.Regenerate();
                                        placed = turned;
                                        p.HostedEvidence["facing_by"] = "reflected_twice (a half turn on the same wall)";
                                    }
                                }
                                else
                                    throw new InvalidOperationException(
                                        "the device is drawn on the " + p.HostedEvidence.Value<string>("side") +
                                        " side of its wall and this wall-based family can neither flip its facing " +
                                        "nor be reflected. Nothing was built for this row.");
                            }
                            p.HostedFacing = want;
                            ElementTransformUtils.MoveElement(doc, placed.Id,
                                new XYZ(p.Start.X - ((LocationPoint)placed.Location).Point.X,
                                        p.Start.Y - ((LocationPoint)placed.Location).Point.Y, 0));
                            // THE HEIGHT ON THE WALL is the instance's elevation from its level.
                            Parameter elevation = placed.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                            if (p.Level != null && elevation != null && !elevation.IsReadOnly)
                            {
                                double wantZ = p.Start.Z - p.Level.ProjectElevation;
                                if (Math.Abs(elevation.AsDouble() - wantZ) > 1e-6 && !elevation.Set(wantZ))
                                    throw new InvalidOperationException(
                                        "the elevation of this wall-based device could not be set. Nothing was built for this row.");
                                doc.Regenerate();
                                p.HostedEvidence["elevation_from_level_mm"] = Math.Round(wantZ * 304.8, 1);
                            }
                        }
                        else
                        {
                            placed = p.Level == null
                                ? doc.Create.NewFamilyInstance(p.Start, symbol, p.Host, p.StructuralType)
                                : doc.Create.NewFamilyInstance(p.Start, symbol, p.Host, p.Level, p.StructuralType);
                            PositionInstance(doc, p, placed);
                        }
                    }
                    else
                    {
                        if (p.Host != null)
                            throw new InvalidOperationException(
                                "this family stands on a LEVEL (" + placementType + ") and a host was named for " +
                                "it. Revit would ignore the host and the row would report a hosting that does " +
                                "not exist.");
                        placed = p.Level == null
                            ? doc.Create.NewFamilyInstance(p.Start, symbol, p.StructuralType)
                            : doc.Create.NewFamilyInstance(p.Start, symbol, p.Level, p.StructuralType);
                        PositionAndTop(doc, p, placed);
                    }

                    // THE MIRROR, BY WHICHEVER OPERATION THIS FAMILY SUPPORTS.
                    //
                    // A symbol the drawing shows reflected is not the same symbol
                    // turned: no angle reproduces a reflection, and on a handed
                    // device that is exactly what the symbol was drawn to say.
                    //
                    // MEASURED on the family that first refused: CanFlipHand and
                    // CanFlipFacing are both false, and CanMirrorElement is TRUE -
                    // the reflected copy keeps its host AND its host face, reverses
                    // the hand, and stays where it was to the millimetre. So
                    // "cannot be mirrored" was one API answering a narrower
                    // question, and the row it refused was buildable all along.
                    if (p.Input.Value<bool?>("flip") == true && !p.FlipDone)
                    {
                        p.FacingBeforeReflection = placed.FacingOrientation;
                        if (placed.CanFlipHand)
                        {
                            placed.flipHand();
                            doc.Regenerate();
                            p.MirrorMethod = "flip_hand";
                        }
                        else if (ElementTransformUtils.CanMirrorElement(doc, placed.Id))
                        {
                            // A true reflection about the plane through the
                            // instance, perpendicular to its hand. MirrorElements
                            // COPIES: the reflected copy is what was asked for and
                            // the original is removed in this same transaction, so
                            // the row reports one element and the model holds one.
                            XYZ hand = placed.HandOrientation;
                            XYZ origin = (placed.Location as LocationPoint)?.Point ?? p.Start;
                            var one = new List<ElementId> { placed.Id };
                            ICollection<ElementId> copies = ElementTransformUtils.MirrorElements(
                                doc, one, Plane.CreateByNormalAndOrigin(hand, origin), true);
                            doc.Regenerate();

                            ElementId copyId = copies?.FirstOrDefault() ?? ElementId.InvalidElementId;
                            var reflected = doc.GetElement(copyId) as FamilyInstance;
                            if (reflected == null)
                                throw new InvalidOperationException(
                                    "the reflection produced no instance to keep. Nothing was built for this row.");

                            doc.Delete(placed.Id);
                            doc.Regenerate();
                            placed = reflected;
                            p.MirrorMethod = "reflected_copy";
                        }
                        else
                            throw new InvalidOperationException(
                                "the drawing shows this symbol mirrored and this family supports no reflection: " +
                                "CanFlipHand, CanFlipFacing and CanMirrorElement are all false. Nothing was " +
                                "built for this row rather than building it the wrong way round. A requirement " +
                                "set can say what the mirror means with mirror: symmetric (checked against the " +
                                "block's own geometry) or mirror: variant.");
                    }
                    return placed;
                }
                case "duct": return Duct.Create(doc, p.SystemType.Id, p.Type.Id, p.Level.Id, p.Start, p.End);
                case "pipe": return Pipe.Create(doc, p.SystemType.Id, p.Type.Id, p.Level.Id, p.Start, p.End);
                case "conduit": return Conduit.Create(doc, p.Type.Id, p.Start, p.End, p.Level.Id);
                case "flex_pipe": return FlexPipe.Create(doc, p.SystemType.Id, p.Type.Id, p.Level.Id, p.FlexPoints);
                case "flex_duct": return FlexDuct.Create(doc, p.SystemType.Id, p.Type.Id, p.Level.Id, p.FlexPoints);
                case "cable_tray": return CableTray.Create(doc, p.Type.Id, p.Start, p.End, p.Level.Id);
                case "structural_framing":
                    FamilySymbol framing = (FamilySymbol)p.Type;
                    if (!framing.IsActive) { framing.Activate(); doc.Regenerate(); }
                    FamilyInstance placedFraming = doc.Create.NewFamilyInstance(Line.CreateBound(p.Start, p.End), framing, p.Level, p.StructuralType);
                    if (string.Equals(p.Input.Value<string>("join_rule"), "none", StringComparison.Ordinal))
                    {
                        StructuralFramingUtils.DisallowJoinAtEnd(placedFraming, 0);
                        StructuralFramingUtils.DisallowJoinAtEnd(placedFraming, 1);
                        doc.Regenerate();
                    }
                    // Revit gives a beam the nearest level BELOW its curve as its Reference Level,
                    // whatever level was passed (MEASURED 2026-09-27 in Revit 2026: asked a level at
                    // 93,000 mm for a beam at 96,000 mm, got the one at 95,000 mm). The caller's level
                    // is set back explicitly; the re-read then judges both the level and the ends.
                    Parameter referenceLevel = placedFraming?.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                    if (referenceLevel != null && !referenceLevel.IsReadOnly && referenceLevel.AsElementId() != p.Level.Id)
                    {
                        referenceLevel.Set(p.Level.Id);
                        doc.Regenerate();
                    }
                    return placedFraming;
                case "structural_column":
                    FamilySymbol column = (FamilySymbol)p.Type;
                    if (!column.IsActive) { column.Activate(); doc.Regenerate(); }
                    FamilyInstance placedColumn = doc.Create.NewFamilyInstance(p.Start, column, p.Level, StructuralType.Column);
                    PositionAndTop(doc, p, placedColumn);
                    return placedColumn;
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
                    // BeamSystem.Create REFUSES a level with no plan view, with a message
                    // that names a parameter rather than the fixable fact; and MEASURED on
                    // an Autodesk MEP sample in Revit 2023, asking for that model's lowest
                    // level silently returns a system bound to the NEXT level up, at a
                    // different elevation, while its other three levels bind correctly.
                    // A system on a level nobody asked for is not the requested structure,
                    // so both cases are named here instead of being discovered downstream.
                    if (!new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                            .Any(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == p.Level.Id))
                        throw new InvalidOperationException(
                            "A beam system needs a plan view on its level, and level '" + p.Level.Name +
                            "' has none. Create a plan view on that level, or use a level that has one.");
                    BeamSystem system = BeamSystem.Create(doc, profile, p.Level, p.BeamDirection, false);
                    if (system.Level != null && system.Level.Id != p.Level.Id)
                        throw new InvalidOperationException(
                            "Revit bound this beam system to level '" + system.Level.Name + "' (" + Rid.Value(system.Level.Id) +
                            ") instead of the requested '" + p.Level.Name + "' (" + Rid.Value(p.Level.Id) +
                            "), which places it at a different elevation. Nothing was kept. Use a level Revit accepts for a beam system in this model.");
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
                    if (p.FittingSubtype == "cap") return CreateCap(doc, p);
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
                        // EVERY MEMBER PAST THE FIRST PAIR, not only the third. A tee
                        // has one; a cross has two, and the version that handled exactly
                        // one left the cross's fourth member with no connector chosen -
                        // which surfaces as a null-reference inside Revit's factory
                        // rather than as a refusal anybody can read.
                        for (int extra = 2; extra < p.FittingMembers.Count; extra++)
                        {
                            ConnectorFact extraFact, unusedCorner; string extraCode, extraReason;
                            if (!MepRules.SelectPair(factSides[extra], new List<ConnectorFact> { chosenA },
                                                     p.FittingMembers[extra].NamedConnector, null,
                                                     out extraFact, out unusedCorner, out extraCode, out extraReason))
                                throw new InvalidOperationException(extraCode + " (member " + extra + "): " + extraReason);
                            p.FittingMembers[extra].ConnectorId = extraFact.Id;
                            p.FittingMembers[extra].Fact = extraFact;
                        }
                    }
                    if (p.FittingSubtype == "takeoff")
                    {
                        Element main = p.TakeoffMain;
                        Plan mainPlan = null;
                        if (main == null && p.TakeoffMainBatchIndex != null)
                        {
                            Created earlierMain = createdSoFar.FirstOrDefault(c => c.Index == p.TakeoffMainBatchIndex.Value);
                            main = earlierMain == null ? null : doc.GetElement(earlierMain.Id);
                            mainPlan = earlierMain?.Plan;
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
                        // A main created EARLIER IN THIS BATCH did not exist at plan time, so the Tap
                        // preference is read here too (measured live 2026-10-18, Revit 2024: without
                        // this, NewTakeoffFitting threw "No routing preference for takeoff" mid-batch).
                        if (!p.TakeoffAsTee && !HasTapPreference(doc, mainCurve))
                        {
                            if (p.TakeoffTeeFallbackAllowed && (mainCurve is Pipe || mainCurve is Autodesk.Revit.DB.Mechanical.Duct))
                                p.TakeoffAsTee = true;
                            else
                                throw new InvalidOperationException("curve_type_has_no_takeoff_preference: the main's type '" +
                                    (doc.GetElement(mainCurve.GetTypeId())?.Name ?? "?") + "' has no Tap junction preference, which " +
                                    "NewTakeoffFitting needs. Use fitting='takeoff_or_tee' to place a tee instead, or set Junction = Tap " +
                                    "in that type's Routing Preferences.");
                        }
                        if (p.TakeoffAsTee)
                        {
                            FamilyInstance tee = TeeInsteadOfTakeoff(doc, p, mainCurve, mainGeometry, branchConnector, mainPlan);
                            // A branch created in this batch now ends at the tee's connector, not on the
                            // main's axis: its end is verified at the tee's junction point.
                            Plan branchPlan = createdSoFar.FirstOrDefault(c => c.Id == owners[0].Id)?.Plan;
                            if (branchPlan != null && tee != null) branchPlan.TeeJunction = tee.Id;
                            return tee;
                        }
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
                        case "elbow":
                        case "union":
                        case "transition": return CreateDiameterCheckedFitting(doc, p, live);
                        case "tee": return CreatePipeTeeAtDrawnAngle(doc, live, p);
                        case "cross": return doc.Create.NewCrossFitting(live[0], live[1], live[2], live[3]);
                        default: throw new InvalidOperationException("unsupported fitting '" + p.FittingSubtype + "'");
                    }
                }
                default: throw new InvalidOperationException("unsupported kind '" + p.Kind + "'");
            }
        }

        /// <summary>
        /// takeoff_or_tee on a main whose type has no Tap preference: split the main where the
        /// branch connector projects onto it, then join the two pieces and the branch with
        /// NewTeeFitting (the type's Tee junction rule). Both pieces must keep at least 150 mm
        /// on each side; the new piece is recorded in AlsoCreated so it is verified and undone
        /// with the fitting. Runs inside the caller's transaction: any failure rolls it back.
        /// </summary>
        private static bool HasTapPreference(Document doc, MEPCurve main)
        {
            try
            {
                var type = doc.GetElement(main.GetTypeId()) as MEPCurveType;
                RoutingPreferenceManager r = type?.RoutingPreferenceManager;
                if (r == null) return true; // unreadable: let Revit decide, as before
                return r.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions) > 0 &&
                       r.PreferredJunctionType == PreferredJunctionType.Tap;
            }
            catch { return true; }
        }

        private static FamilyInstance TeeInsteadOfTakeoff(Document doc, Plan p, MEPCurve main, Curve mainGeometry, Connector branch,
                                                          Plan mainPlan = null)
        {
            IntersectionResult hit = mainGeometry.Project(branch.Origin);
            if (hit == null) throw new InvalidOperationException("tee fallback: the branch point does not project onto the main");
            XYZ at = hit.XYZPoint;
            double minPiece = 150 / 304.8;
            if (at.DistanceTo(mainGeometry.GetEndPoint(0)) < minPiece || at.DistanceTo(mainGeometry.GetEndPoint(1)) < minPiece)
                throw new InvalidOperationException("tee fallback: the branch meets the main less than 150 mm from one of its ends; " +
                    "use an elbow/tee at the end instead. Nothing was created.");
            ElementId newPiece = main is Pipe
                ? Autodesk.Revit.DB.Plumbing.PlumbingUtils.BreakCurve(doc, main.Id, at)
                : Autodesk.Revit.DB.Mechanical.MechanicalUtils.BreakCurve(doc, main.Id, at);
            doc.Regenerate();
            if (newPiece == null || newPiece == ElementId.InvalidElementId)
                throw new InvalidOperationException("tee fallback: Revit did not split the main");
            p.AlsoCreated.Add(newPiece);
            // A main created earlier in this batch is verified against the line it was asked
            // for; split, that line is the UNION of its two pieces (measured live 2026-10-18).
            if (mainPlan != null) mainPlan.SplitPiece = newPiece;
            var ends = new List<Connector>();
            foreach (ElementId id in new[] { main.Id, newPiece })
            {
                ConnectorManager m = MepFacts.ManagerOf(doc.GetElement(id));
                if (m == null) continue;
                foreach (Connector c in MepFacts.Ordered(m))
                    if (!c.IsConnected && c.ConnectorType == ConnectorType.End && c.Origin.DistanceTo(at) <= MepRules.CoincidenceToleranceFeet * 10)
                    { ends.Add(c); break; }
            }
            if (ends.Count != 2)
                throw new InvalidOperationException("tee fallback: the split main does not expose two open ends at the branch point");
            return doc.Create.NewTeeFitting(ends[0], ends[1], branch);
        }

        /// <summary>
        /// Text of the chain "port -> transition -> ... -> pipe" when the pipe is reached through
        /// transitions only, else null.
        /// </summary>
        private static string ChainToPipe(Connector from, ElementId pipeId, int depth)
        {
            foreach (Connector other in from.AllRefs.Cast<Connector>())
            {
                if (other.Owner == null) continue;
                if (other.Owner.Id == pipeId) return "-> pipe " + pipeId;
                if (depth > 0 && other.Owner is FamilyInstance fi && fi.MEPModel != null)
                {
                    string part = "";
                    try { part = ((PartType)(fi.Symbol.Family.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)?.AsInteger() ?? -1)).ToString(); } catch { }
                    if (part != "Transition") continue;
                    foreach (Connector inner in fi.MEPModel.ConnectorManager.Connectors.Cast<Connector>())
                    {
                        if (inner.Id == other.Id && inner.Owner.Id == other.Owner.Id) continue;
                        string rest = ChainToPipe(inner, pipeId, depth - 1);
                        if (rest != null)
                            return "-> reducer " + fi.Id + " (" + MepRules.Mm(other.Radius * 2) + " to " + MepRules.Mm(inner.Radius * 2) + ") " + rest;
                    }
                }
            }
            return null;
        }

        private static double[] Arr(XYZ p) => new[] { p.X, p.Y, p.Z };

        private static Connector PortOf(FamilyInstance fitting, int id) =>
            fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>().FirstOrDefault(c => c.Id == id);

        /// <summary>The fitting port that is connected to the given pipe, or null.</summary>
        private static Connector PortJoinedTo(FamilyInstance fitting, ElementId pipeId) =>
            fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                .FirstOrDefault(c => c.AllRefs.Cast<Connector>().Any(o => o.Owner != null && o.Owner.Id == pipeId));

        /// <summary>
        /// Closing check of the oblique wye: both run pipes are joined to a (different) wye port,
        /// each port and each pipe end is coaxial with the run as drawn, and the pipe end sits on
        /// its port. Returns the first problem, or null.
        /// </summary>
        private static string CheckWyeRunPorts(Document doc, ElementId fittingId, ElementId pipeAId, ElementId pipeBId,
            double[] runPoint, double[] run)
        {
            FamilyInstance fitting = doc.GetElement(fittingId) as FamilyInstance;
            if (fitting?.MEPModel == null) return "the wye is gone at the final check of the run";
            double tol = MepRules.CoincidenceToleranceFeet * 2;
            var seen = new HashSet<int>();
            foreach (ElementId pipeId in new[] { pipeAId, pipeBId })
            {
                Pipe pipe = doc.GetElement(pipeId) as Pipe;
                Connector port = PortJoinedTo(fitting, pipeId);
                if (pipe == null || port == null) return "run pipe " + pipeId + " is not connected to the wye";
                if (!seen.Add(port.Id)) return "both run pipes are joined to the same wye port (" + port.Id + ")";
                Connector end = port.AllRefs.Cast<Connector>().FirstOrDefault(o => o.Owner != null && o.Owner.Id == pipeId);
                double off = ObliquePipeTeeRules.ProjectOntoLine(Arr(port.Origin), runPoint, run) is double[] foot
                    ? new XYZ(port.Origin.X - foot[0], port.Origin.Y - foot[1], port.Origin.Z - foot[2]).GetLength() : double.MaxValue;
                if (!ObliquePipeTeeRules.IsCoaxialWithRun(run, runPoint, Arr(port.Origin), Arr(port.CoordinateSystem.BasisZ), tol))
                    return "the wye port " + port.Id + " of run pipe " + pipeId + " is not coaxial with the run (" + MepRules.Mm(off) + " off the run axis)";
                if (end == null || !ObliquePipeTeeRules.IsCoaxialWithRun(run, runPoint, Arr(end.Origin), Arr(end.CoordinateSystem.BasisZ), tol))
                    return "the end of run pipe " + pipeId + " is not coaxial with the run";
                if (end.Origin.DistanceTo(port.Origin) > tol)
                    return "run pipe " + pipeId + " does not meet its wye port (gap " + MepRules.Mm(end.Origin.DistanceTo(port.Origin)) + ")";
            }
            return null;
        }

        private static void TrimPipeEndTo(Pipe pipe, Connector pipeEnd, XYZ target)
        {
            var location = pipe.Location as LocationCurve;
            var line = location?.Curve as Line;
            if (line == null) throw new InvalidOperationException("run pipe " + pipe.Id + " is not a straight pipe");
            XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);
            bool moveFirst = p0.DistanceTo(pipeEnd.Origin) < p1.DistanceTo(pipeEnd.Origin);
            XYZ oldEnd = moveFirst ? p0 : p1, other = moveFirst ? p1 : p0;
            if ((target - other).GetLength() < 1.0 / 304.8 || (target - other).DotProduct(oldEnd - other) <= 0)
                throw new InvalidOperationException("run pipe " + pipe.Id + " is too short to reach the reversed wye's port");
            location.Curve = moveFirst ? Line.CreateBound(target, other) : Line.CreateBound(other, target);
        }

        private static Dictionary<ElementId, double> PipeBoresAround(FamilyInstance fitting)
        {
            var bores = new Dictionary<ElementId, double>();
            foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>())
                foreach (Connector o in c.AllRefs.Cast<Connector>())
                    if (o.Owner is Pipe p && !bores.ContainsKey(p.Id))
                        bores[p.Id] = p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;
            return bores;
        }

        /// <summary>
        /// Sets a fitting port's bore. A fitting's port size is not settable on the connector: it follows the
        /// family parameter the family associates with that connector's diameter (or radius), read here, never
        /// by name. Null when it was set and regenerated, else why not.
        /// </summary>
        private static string SetPortBore(Document doc, FamilyInstance fitting, Connector port, double boreFeet)
        {
            try
            {
                var info = port.GetMEPConnectorInfo() as MEPFamilyConnectorInfo;
                ElementId diameterId = info?.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_DIAMETER)) ?? ElementId.InvalidElementId;
                ElementId radiusId = info?.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_RADIUS)) ?? ElementId.InvalidElementId;
                bool byRadius = diameterId == ElementId.InvalidElementId;
                ElementId sizeId = byRadius ? radiusId : diameterId;
                Parameter size = sizeId == ElementId.InvalidElementId ? null
                    : fitting.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Id == sizeId);
                if (size == null) return "its port size is not tied to an instance parameter of the fitting";
                if (size.IsReadOnly) return "its port size parameter '" + size.Definition?.Name + "' is read-only (driven by a formula)";
                // Before: every other port of the fitting and the bore of every pipe joined to the fitting. A parameter
                // shared by several ports (one diameter driving the whole fitting) would resize those too, so any
                // change there refuses instead of leaving a mismatch nobody re-reads.
                int portId = port.Id;
                var others = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>().Where(c => c.Id != portId)
                    .ToDictionary(c => c.Id, c => c.Radius);
                var pipes = PipeBoresAround(fitting);
                if (!size.Set(byRadius ? boreFeet / 2 : boreFeet)) return "Revit refused the value for '" + size.Definition?.Name + "'";
                doc.Regenerate();
                foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>().Where(c => c.Id != portId))
                    if (!others.TryGetValue(c.Id, out double was) || Math.Abs(was - c.Radius) > 0.25 / 304.8)
                        return "its size parameter '" + size.Definition?.Name + "' also resizes port " + c.Id + " (to " +
                            MepRules.Mm(c.Radius * 2) + "): the fitting's port sizes are not independent";
                foreach (var kv in PipeBoresAround(fitting))
                    if (!pipes.TryGetValue(kv.Key, out double was) || Math.Abs(was - kv.Value) > 0.25 / 304.8)
                        return "setting '" + size.Definition?.Name + "' resized the joined pipe " + Rid.Value(kv.Key) + " (to " + MepRules.Mm(kv.Value) + ")";
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// Reverses a wye along its run: disconnect it from both run pipes, half turn about the
        /// axis through the run line perpendicular to the run in the run-branch plane
        /// (<see cref="ObliquePipeTeeRules.FlipAxis"/>), then reconnect pipe A to the port now on its
        /// side and pipe B likewise (directions opposed, bores equal), trimming the pipe ends to the
        /// ports. A reducing wye (run ports of different bores) has each run port resized to the pipe it
        /// will meet (<see cref="SetPortBore"/>). Refuses, throwing, when the run ports are off the run line,
        /// such a port cannot be resized on its own, the turned branch does not lean as expected, or no port matches a pipe.
        /// The caller's transaction rolls everything back.
        /// </summary>
        private static FamilyInstance FlipWyeAlongRun(Document doc, ElementId fittingId, Connector runA, Connector runB, int branchPortId)
        {
            FamilyInstance fitting = doc.GetElement(fittingId) as FamilyInstance;
            if (fitting?.MEPModel == null) throw new InvalidOperationException("the wye is gone before it could be reversed");
            double tolLen = MepRules.CoincidenceToleranceFeet * 2, tolBore = 0.5 / 304.8;
            ElementId pipeAId = runA.Owner.Id, pipeBId = runB.Owner.Id;
            int connA = runA.Id, connB = runB.Id;
            Connector portA = PortJoinedTo(fitting, pipeAId), portB = PortJoinedTo(fitting, pipeBId), branchPort = PortOf(fitting, branchPortId);
            if (portA == null || portB == null || branchPort == null || portA.Id == portB.Id)
                throw new InvalidOperationException("the wye is not joined to both run pipes, so it cannot be reversed along the run");
            double[] run = Arr(runA.CoordinateSystem.BasisZ), runPoint = Arr(runA.Origin);
            foreach (Connector p in new[] { portA, portB })
                if (!ObliquePipeTeeRules.IsCoaxialWithRun(run, runPoint, Arr(p.Origin), Arr(p.CoordinateSystem.BasisZ), tolLen))
                    throw new InvalidOperationException("the wye's run port " + p.Id + " is not on the run axis, so it cannot be reversed along the run");
            // A reducing run: the reversed wye's ports meet the other pipe each, so each is resized to the pipe
            // it will meet through the family parameter tied to it (below); a wye that cannot be resized is refused.
            string bore = ObliquePipeTeeRules.FlipBoreProblem(portA.Radius, portB.Radius, runA.Radius, runB.Radius, tolBore);
            double[] before = Arr(branchPort.CoordinateSystem.BasisZ);
            double[] axis = ObliquePipeTeeRules.FlipAxis(run, before);
            if (axis == null) throw new InvalidOperationException("the wye's branch runs along the run: nothing to reverse");
            XYZ fo = (fitting.Location as LocationPoint)?.Point ?? branchPort.Origin;
            double[] axisPoint = ObliquePipeTeeRules.ProjectOntoLine(Arr(fo), runPoint, run);
            double[] expected = ObliquePipeTeeRules.FlipDirection(before, axis);
            XYZ ap = new XYZ(axisPoint[0], axisPoint[1], axisPoint[2]);

            portA.DisconnectFrom(runA);
            portB.DisconnectFrom(runB);
            ElementTransformUtils.RotateElement(doc, fittingId, Line.CreateBound(ap, ap + new XYZ(axis[0], axis[1], axis[2])), Math.PI);
            doc.Regenerate();
            fitting = doc.GetElement(fittingId) as FamilyInstance;
            if (fitting?.MEPModel == null) throw new InvalidOperationException("Revit removed the wye when it was disconnected from the run to be reversed");
            branchPort = PortOf(fitting, branchPortId);
            if (branchPort == null || !ObliquePipeTeeRules.FlipLeanMatches(expected, Arr(branchPort.CoordinateSystem.BasisZ)))
                throw new InvalidOperationException("after the half turn the wye's branch does not lean where expected");

            foreach (var pair in new[] { new KeyValuePair<ElementId, int>(pipeAId, connA), new KeyValuePair<ElementId, int>(pipeBId, connB) })
            {
                Pipe pipe = doc.GetElement(pair.Key) as Pipe;
                Connector end = pipe?.ConnectorManager.Connectors.Cast<Connector>().FirstOrDefault(c => c.Id == pair.Value);
                if (end == null) throw new InvalidOperationException("run pipe " + pair.Key + " could not be re-read while reversing the wye");
                Connector port = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                    .Where(c => c.Id != branchPortId && !c.IsConnected &&
                        ObliquePipeTeeRules.PortsOppose(Arr(end.CoordinateSystem.BasisZ), Arr(c.CoordinateSystem.BasisZ)))
                    .OrderBy(c => c.Origin.DistanceTo(end.Origin)).FirstOrDefault();
                if (port == null) throw new InvalidOperationException("no port of the reversed wye faces run pipe " + pair.Key);
                if (bore != null && Math.Abs(port.Radius - end.Radius) > tolBore)
                {
                    int portId = port.Id;
                    string sized = SetPortBore(doc, fitting, port, end.Radius * 2);
                    fitting = doc.GetElement(fittingId) as FamilyInstance;
                    port = fitting?.MEPModel?.ConnectorManager.Connectors.Cast<Connector>().FirstOrDefault(c => c.Id == portId);
                    if (sized != null || port == null)
                        throw new InvalidOperationException(bore + " (resizing its port " + portId + ": " + (sized ?? "the port disappeared") + ")");
                }
                if (Math.Abs(port.Radius - end.Radius) > tolBore)
                    throw new InvalidOperationException("the reversed wye's port facing run pipe " + pair.Key + " has a different bore (" +
                        MepRules.Mm(port.Radius * 2) + " against " + MepRules.Mm(end.Radius * 2) + ")");
                if (!ObliquePipeTeeRules.IsCoaxialWithRun(run, runPoint, Arr(port.Origin), Arr(port.CoordinateSystem.BasisZ), tolLen))
                    throw new InvalidOperationException("the reversed wye's port facing run pipe " + pair.Key + " is not on the run axis");
                try { port.ConnectTo(end); }
                catch (Exception ex) { throw new InvalidOperationException("the reversed wye could not connect to run pipe " + pair.Key + ": " + ex.Message, ex); }
                if (end.Origin.DistanceTo(port.Origin) > tolLen) TrimPipeEndTo(pipe, end, port.Origin);
                doc.Regenerate();
                fitting = doc.GetElement(fittingId) as FamilyInstance;
                if (fitting?.MEPModel == null) throw new InvalidOperationException("the wye disappeared while reconnecting the run");
            }
            return fitting;
        }

        /// <summary>
        /// NewTeeFitting accepts a perpendicular branch but Revit rejects many 45-degree
        /// branches even when the pipe type's first junction rule is a wye. Build against
        /// a temporary perpendicular pipe, switch the tee to the wye, reverse the wye along the
        /// run when its branch leans against the drawn one (half turn, run pipes reconnected),
        /// then roll and slide it until its branch port lies on the drawn branch axis and attach
        /// the original branch. This runs inside the caller's atomic transaction; any failed
        /// connection or read-back rolls the whole fitting back. A last check re-reads that both
        /// run ports are connected to the run pipes and coaxial with the run.
        /// </summary>
        private static FamilyInstance CreatePipeTeeAtDrawnAngle(Document doc, List<Connector> live, Plan plan)
        {
            if (live.Count != 3 || !(live[0].Owner is Pipe) || !(live[1].Owner is Pipe) ||
                !(live[2].Owner is Pipe branchPipe))
                return doc.Create.NewTeeFitting(live[0], live[1], live[2]);

            var run = live[0].CoordinateSystem.BasisZ;
            var branch = live[2].CoordinateSystem.BasisZ;
            double[] r = { run.X, run.Y, run.Z }, b = { branch.X, branch.Y, branch.Z };
            double angle = ObliquePipeTeeRules.AcuteBranchAngleDegrees(r, b);
            if (!ObliquePipeTeeRules.NeedsObliqueWorkaround(angle))
                return doc.Create.NewTeeFitting(live[0], live[1], live[2]);
            // The run as drawn: a point on its line and the two pipes, for the closing check.
            double[] runPoint = { live[0].Origin.X, live[0].Origin.Y, live[0].Origin.Z };
            ElementId[] runPipeIds = { live[0].Owner.Id, live[1].Owner.Id };
            var startBores = new Dictionary<ElementId, double>();
            foreach (Connector liveEnd in live)
                if (liveEnd.Owner is Pipe livePipe) startBores[livePipe.Id] = livePipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;

            var mainRouting = (doc.GetElement(live[0].Owner.GetTypeId()) as PipeType)?.RoutingPreferenceManager;
            if (mainRouting == null || mainRouting.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions) == 0)
                throw new InvalidOperationException("oblique pipe junction needs a configured wye junction rule");
            // Classify every Junctions rule by the family's Part Type, never by language-dependent names.
            var ruleInfos = new List<JunctionRuleInfo>();
            var ruleSymbols = new Dictionary<int, FamilySymbol>();
            int ruleCount = mainRouting.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions);
            for (int i = 0; i < ruleCount; i++)
            {
                FamilySymbol sym = doc.GetElement(mainRouting.GetRule(
                    RoutingPreferenceRuleGroupType.Junctions, i).MEPPartId) as FamilySymbol;
                string partType = "";
                try
                {
                    Parameter pt = sym?.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                    if (pt != null) partType = ((PartType)pt.AsInteger()).ToString();
                }
                catch { }
                ruleInfos.Add(new JunctionRuleInfo(i, sym == null ? "(not a loaded fitting type)" :
                    (sym.Family?.Name ?? "") + ": " + sym.Name, partType));
                if (sym != null) ruleSymbols[i] = sym;
            }
            ObliqueJunctionChoice choice = JunctionRuleClassifier.Choose(ruleInfos);
            if (choice.Problem != null) throw new InvalidOperationException(choice.Problem);
            FamilySymbol wye = ruleSymbols[choice.WyeIndex];
            // NewTeeFitting needs a perpendicular TEE for the temporary branch. If the
            // wye is the first routing rule, Revit tries that wye for the perpendicular
            // branch and returns "failed to insert tee" before we can rotate it.
            // Temporarily lead with an existing tee rule, then restore the exact
            // original order before changing the placed symbol to the selected wye.
            FamilySymbol perpendicularTee = ruleSymbols[choice.TeeIndex];

            string stage = "reading the original branch";
            try
            {
            var systemId = branchPipe.MEPSystem?.GetTypeId() ?? branchPipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
            var levelId = branchPipe.ReferenceLevel?.Id;
            ElementId branchPipeId = branchPipe.Id;
            int originalConnectorId = live[2].Id;
            if (systemId == null || systemId == ElementId.InvalidElementId || levelId == null)
                throw new InvalidOperationException("the oblique branch has no readable piping system or level");

            double[] v = ObliquePipeTeeRules.PerpendicularPipeExtensionFromConnector(r, b);
            XYZ start = live[2].Origin;
            XYZ end = start + new XYZ(v[0], v[1], v[2]) * (1000.0 / 304.8);
            stage = "creating the perpendicular branch";
            Pipe temporary = Pipe.Create(doc, systemId, branchPipe.GetTypeId(), levelId, start, end);
            Parameter bore = branchPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            Parameter temporaryBore = temporary.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            // A reducing junction (branch narrower than the run): the perpendicular tee would take the
            // narrow branch through a reducer. The temporary branch takes the run's bore instead, and the
            // wye's branch port is set to the drawn bore once the wye is in place (below).
            // The larger of the two run pipes, so the result does not depend on which side comes first.
            double runBore = Math.Max((live[0].Owner as Pipe)?.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0,
                (live[1].Owner as Pipe)?.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0);
            bool reducing = bore != null && runBore > bore.AsDouble() + 0.5 / 304.8;
            if (bore == null || temporaryBore == null || !temporaryBore.Set(reducing ? runBore : bore.AsDouble()))
                throw new InvalidOperationException("the temporary branch could not retain the existing pipe diameter");
            stage = "regenerating the sized perpendicular branch";
            doc.Regenerate();
            Connector temporaryEnd = temporary.ConnectorManager.Connectors.Cast<Connector>()
                .OrderBy(c => c.Origin.DistanceTo(start)).First();
            stage = "creating the perpendicular tee";
            var temporaryTeeRule = new RoutingPreferenceRule(perpendicularTee.Id,
                "Horizun temporary perpendicular tee");
            temporaryTeeRule.AddCriterion(PrimarySizeCriterion.All());
            FamilyInstance fitting;
            bool temporaryRuleInserted = false;
            try
            {
                mainRouting.AddRule(RoutingPreferenceRuleGroupType.Junctions, temporaryTeeRule, 0);
                temporaryRuleInserted = true;
                fitting = doc.Create.NewTeeFitting(live[0], live[1], temporaryEnd);
            }
            finally
            {
                if (temporaryRuleInserted)
                    mainRouting.RemoveRule(RoutingPreferenceRuleGroupType.Junctions, 0);
            }
            ElementId fittingId = fitting.Id;
            stage = "regenerating the perpendicular tee";
            doc.Regenerate();
            stage = "finding the tee branch connector";
            Connector fittingBranch = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                .FirstOrDefault(c => c.AllRefs.Cast<Connector>().Any(other => other.Owner.Id == temporary.Id));
            if (fittingBranch == null)
            {
                // NewTeeFitting did not link the temporary pipe directly. Follow each port through
                // transitions (at most 3 deep) to see whether Revit put a reducer between them.
                string chain = null;
                foreach (Connector tp in fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>())
                {
                    chain = ChainToPipe(tp, temporary.Id, 3);
                    if (chain != null) { chain = "tee port id " + tp.Id + " (" + MepRules.Mm(tp.Radius * 2) + ") " + chain; break; }
                }
                if (chain != null)
                    throw new InvalidOperationException("this is a reducing junction: Revit joined the " + MepRules.Mm(bore.AsDouble()) +
                        " branch to the perpendicular tee " + (perpendicularTee.Family?.Name ?? "") + ": " + perpendicularTee.Name +
                        " through a reducer (" + chain + "). The junction needs a wye with a reducing branch (or reducers around a wye of the larger size), " +
                        "so nothing was built. Give the pipe type a wye sized for this branch, " +
                        "or move the size change away from the junction.");
                var ports = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>().ToList();
                Connector nearestFree = ports.Where(c => !c.IsConnected)
                    .OrderBy(c => c.Origin.DistanceTo(temporaryEnd.Origin)).FirstOrDefault();
                // Only a port of the same bore may take the temporary pipe: a larger or smaller port
                // would be joined with a size mismatch that the rest of the build would not notice.
                if (nearestFree != null && nearestFree.Origin.DistanceTo(temporaryEnd.Origin) <= 5.0 / 304.8 &&
                    Math.Abs(nearestFree.Radius - temporaryEnd.Radius) <= 0.5 / 304.8)
                {
                    try { nearestFree.ConnectTo(temporaryEnd); fittingBranch = nearestFree; } catch { fittingBranch = null; }
                }
                if (fittingBranch == null)
                    throw new InvalidOperationException((nearestFree != null && nearestFree.Origin.DistanceTo(temporaryEnd.Origin) <= 5.0 / 304.8 &&
                        Math.Abs(nearestFree.Radius - temporaryEnd.Radius) > 0.5 / 304.8
                            ? "bore mismatch: the free tee port nearest the temporary branch (id " + nearestFree.Id + ", " + MepRules.Mm(nearestFree.Radius * 2) +
                              ") does not match the branch bore (" + MepRules.Mm(temporaryEnd.Radius * 2) + "), so it was not joined. "
                            : "") + "the temporary branch did not connect to the tee. Tee " +
                        (perpendicularTee.Family?.Name ?? "") + ": " + perpendicularTee.Name + " ports: " + string.Join("; ",
                        ports.Select(c => "id " + c.Id + " radius " + MepRules.Mm(c.Radius) + ", " + (c.IsConnected ? "connected" : "free") +
                            ", " + MepRules.Mm(c.Origin.DistanceTo(temporaryEnd.Origin)) + " from the temporary pipe end")) +
                        ". Run bores " + MepRules.Mm((live[0].Owner as Pipe)?.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0) + " / " +
                        MepRules.Mm((live[1].Owner as Pipe)?.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0) +
                        ", branch " + MepRules.Mm(bore.AsDouble()) + ".");
            }
            Connector joinedTemp = temporary.ConnectorManager.Connectors.Cast<Connector>()
                .FirstOrDefault(c => c.IsConnectedTo(fittingBranch));
            if (joinedTemp == null) throw new InvalidOperationException("the temporary branch connection disappeared");
            stage = "disconnecting the perpendicular branch";
            fittingBranch.DisconnectFrom(joinedTemp);
            // Keep the temporary pipe alive until the original branch has taken
            // its place. Revit may delete an unhosted fitting when its last
            // branch pipe is deleted during regeneration.
            stage = "regenerating the disconnected tee";
            doc.Regenerate();
            if (doc.GetElement(fitting.Id) == null)
                throw new InvalidOperationException("the tee disappeared after disconnecting the temporary branch");

            stage = "switching the tee type to wye";
            if (fitting.GetTypeId() != wye.Id)
            {
                // Revit refuses to swap a tee for some manufacturer wyes ("Can't make type"): their
                // connectors differ from the tee's. Try every wye-like rule of the pipe type, best first,
                // each inside a SubTransaction so a refusal leaves the tee exactly as it was; only when
                // all are refused say why (JunctionRuleClassifier.SwitchRefusal).
                var order = new List<FamilySymbol> { wye };
                foreach (int candidateIndex in choice.WyeCandidates)
                    if (ruleSymbols.TryGetValue(candidateIndex, out FamilySymbol alternative) && order.All(o => o.Id != alternative.Id))
                        order.Add(alternative);
                var refusals = new List<KeyValuePair<string, string>>();
                bool switched = false;
                FamilySymbol preferredWye = wye;
                foreach (FamilySymbol candidate in order)
                {
                    using (var attempt = new SubTransaction(doc))
                    {
                        // A sub-transaction that does not open is a fault of the document state, not a
                        // refusal of the wye's connectors: it is said as such and stops the build.
                        TransactionStatus opened;
                        try { opened = attempt.Start(); }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException("could not open a sub-transaction to try wye " + (candidate.Family?.Name ?? "") + ": " + candidate.Name +
                                " (" + ex.Message + "). This is the document's transaction state, not the wye's connectors; nothing was tried on this wye.", ex);
                        }
                        if (opened != TransactionStatus.Started)
                            throw new InvalidOperationException("could not open a sub-transaction to try wye " + (candidate.Family?.Name ?? "") + ": " + candidate.Name +
                                " (Revit returned " + opened + "). This is the document's transaction state, not the wye's connectors; nothing was tried on this wye.");
                        try
                        {
                            fitting.Symbol = candidate;
                            doc.Regenerate();
                            Guard.Commit(attempt, "switch the temporary tee to wye " + candidate.Name);
                            wye = candidate;
                            switched = true;
                        }
                        catch (Exception ex)
                        {
                            try { if (attempt.GetStatus() == TransactionStatus.Started) Guard.RollBack(attempt); } catch { }
                            refusals.Add(new KeyValuePair<string, string>((candidate.Family?.Name ?? "") + ": " + candidate.Name, ex.Message));
                        }
                    }
                    if (switched) break;
                }
                if (!switched) throw new InvalidOperationException(JunctionRuleClassifier.SwitchRefusal(refusals));
                // Another wye than the preferred one was used: said as a SUPUESTO with each refusal in Revit's words.
                Newtonsoft.Json.Linq.JObject substitution = JunctionRuleClassifier.WyeSubstitution(plan?.Index ?? -1,
                    (preferredWye.Family?.Name ?? "") + ": " + preferredWye.Name, (wye.Family?.Name ?? "") + ": " + wye.Name, refusals);
                if (substitution != null && plan != null) plan.Assumptions.Add(substitution);
            }
            stage = "finding the wye branch connector";
            fittingBranch = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                .OrderBy(c => c.Origin.DistanceTo(start)).FirstOrDefault(c => !c.IsConnected);
            if (fittingBranch == null)
                throw new InvalidOperationException("the wye exposes no free branch connector");
            if (reducing)
            {
                stage = "sizing the wye branch to the drawn bore";
                string sized = SetPortBore(doc, fitting, fittingBranch, bore.AsDouble());
                int sizedId = fittingBranch.Id;
                fittingBranch = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>().FirstOrDefault(c => c.Id == sizedId);
                if (sized != null || fittingBranch == null || Math.Abs(fittingBranch.Radius * 2 - bore.AsDouble()) > 0.5 / 304.8)
                    throw new InvalidOperationException("this is a reducing junction: the wye " + (wye.Family?.Name ?? "") + ": " + wye.Name +
                        " did not take the " + MepRules.Mm(bore.AsDouble()) + " branch on its branch port (" +
                        (sized ?? (fittingBranch == null ? "the port disappeared" : "it stayed at " + MepRules.Mm(fittingBranch.Radius * 2))) +
                        "), so nothing was built. Give the pipe type a wye whose branch size is an instance parameter independent of the run, " +
                        "or move the size change away from the junction.");
            }
            int wyeBranchId = fittingBranch.Id;
            // The branch port by its id, not "the free one": turning it can land it on the
            // temporary pipe's end and Revit then connects them, leaving no free port at all
            // (seen live). Only that accidental link is undone.
            Connector WyeBranchPort()
            {
                Connector port = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                    .FirstOrDefault(c => c.Id == wyeBranchId);
                if (port == null || !port.IsConnected) return port;
                foreach (Connector other in port.AllRefs.Cast<Connector>()
                    .Where(o => o.Owner != null && o.Owner.Id == temporary.Id).ToList())
                    port.DisconnectFrom(other);
                return port.IsConnected ? null : port;
            }
            stage = "orienting the wye branch towards the drawn pipe";
            var branchAxis = (branchPipe.Location as LocationCurve)?.Curve as Line;
            if (branchAxis == null)
                throw new InvalidOperationException("the original branch is not a straight pipe");
            XYZ farEnd = branchAxis.GetEndPoint(0).DistanceTo(start) > branchAxis.GetEndPoint(1).DistanceTo(start)
                ? branchAxis.GetEndPoint(0) : branchAxis.GetEndPoint(1);
            var triedAngles = new List<string>();
            Connector alignedBranch = null;
            XYZ towardsFar = (farEnd - start).Normalize();
            double[] FarArr() => new[] { towardsFar.X, towardsFar.Y, towardsFar.Z };
            {
                Connector leanPort = WyeBranchPort();
                XYZ ru = live[0].CoordinateSystem.BasisZ;
                if (leanPort != null && ObliquePipeTeeRules.LeanIsOpposite(new[] { ru.X, ru.Y, ru.Z },
                    new[] { leanPort.CoordinateSystem.BasisZ.X, leanPort.CoordinateSystem.BasisZ.Y, leanPort.CoordinateSystem.BasisZ.Z }, FarArr()))
                {
                    // The branch leans the other way along the run: reverse the wye along the run.
                    // Any refusal inside rolls the whole fitting back with the caller's transaction.
                    stage = "reversing the wye along the run";
                    fitting = FlipWyeAlongRun(doc, fittingId, live[0], live[1], wyeBranchId);
                    leanPort = WyeBranchPort();
                    XYZ ru2 = live[0].CoordinateSystem.BasisZ;
                    if (leanPort == null || ObliquePipeTeeRules.LeanIsOpposite(new[] { ru2.X, ru2.Y, ru2.Z },
                        new[] { leanPort.CoordinateSystem.BasisZ.X, leanPort.CoordinateSystem.BasisZ.Y, leanPort.CoordinateSystem.BasisZ.Z }, FarArr()))
                        throw new InvalidOperationException("the wye's branch still leans against the drawn branch after reversing it along the run");
                    stage = "orienting the wye branch towards the drawn pipe";
                }
            }
            // A port counts as aligned only if it also points along the drawn branch: a 90-degree
            // port can sit on a 45-degree branch's axis at one point and still be the wrong fitting.
            bool Faces(Connector c) => ObliquePipeTeeRules.PortFacesBranch(
                new[] { c.CoordinateSystem.BasisZ.X, c.CoordinateSystem.BasisZ.Y, c.CoordinateSystem.BasisZ.Z }, FarArr());
            foreach (double candidateDegrees in new[] { angle, 180.0 - angle, 180.0 + angle, 360.0 - angle })
            {
                Connector free = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                    .Where(c => !c.IsConnected).OrderBy(c => c.Origin.DistanceTo(start)).FirstOrDefault();
                if (free == null) { triedAngles.Add(candidateDegrees + ": no free port"); continue; }
                try { free.Angle = candidateDegrees * Math.PI / 180.0; doc.Regenerate(); }
                catch (Exception ex) { triedAngles.Add(candidateDegrees + ": " + ex.Message); continue; }
                free = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                    .Where(c => !c.IsConnected).OrderBy(c => c.Origin.DistanceTo(start)).FirstOrDefault();
                if (free == null) { triedAngles.Add(candidateDegrees + ": no free port after regeneration"); continue; }
                double offAxis = branchAxis.Distance(free.Origin);
                double along = free.Origin.DistanceTo(farEnd);
                triedAngles.Add(candidateDegrees + ": off_axis_mm=" + MepRules.Mm(offAxis) +
                    ", from_far_mm=" + MepRules.Mm(along));
                if (offAxis <= MepRules.CoincidenceToleranceFeet &&
                    along > MepRules.CoincidenceToleranceFeet && along < farEnd.DistanceTo(start))
                {
                    if (!Faces(free)) { triedAngles.Add(candidateDegrees + ": on the axis but not pointing along the drawn branch"); continue; }
                    alignedBranch = free; break;
                }
            }
            if (alignedBranch == null)
            {
                // A wye's branch meets its run where the family says, not where the temporary
                // tee stood, so the right angle can leave the port BESIDE the drawn branch.
                // Try each angle again and slide the fitting along the run onto the drawn axis;
                // the run pipes stay connected and follow. Any failure keeps the refusal.
                XYZ runDir = live[0].CoordinateSystem.BasisZ;
                XYZ branchDir = branchAxis.Direction;
                foreach (double candidateDegrees in new[] { angle, 180.0 - angle, 180.0 + angle, 360.0 - angle })
                {
                    Connector free = WyeBranchPort();
                    if (free == null) { triedAngles.Add(candidateDegrees + " + slide: branch port held by another element"); continue; }
                    try { free.Angle = candidateDegrees * Math.PI / 180.0; doc.Regenerate(); }
                    catch (Exception ex) { triedAngles.Add(candidateDegrees + " + slide: " + ex.Message); continue; }
                    free = WyeBranchPort();
                    if (free == null) { triedAngles.Add(candidateDegrees + " + slide: branch port held after regeneration"); continue; }
                    double? slide = ObliquePipeTeeRules.SlideAlongRunToMeetBranch(
                        new[] { runDir.X, runDir.Y, runDir.Z },
                        new[] { free.Origin.X, free.Origin.Y, free.Origin.Z },
                        new[] { farEnd.X, farEnd.Y, farEnd.Z },
                        new[] { branchDir.X, branchDir.Y, branchDir.Z });
                    if (slide == null || Math.Abs(slide.Value) > 1000.0 / 304.8)
                    {
                        triedAngles.Add(candidateDegrees + " + slide: " + (slide == null
                            ? "run and branch parallel" : "slide " + MepRules.Mm(slide.Value) + " over 1000 mm"));
                        continue;
                    }
                    stage = "sliding the wye along the run onto the drawn branch";
                    ElementTransformUtils.MoveElement(doc, fitting.Id, runDir.Normalize() * slide.Value);
                    doc.Regenerate();
                    fitting = doc.GetElement(fittingId) as FamilyInstance;
                    if (fitting == null) throw new InvalidOperationException("the wye disappeared while sliding along the run");
                    free = WyeBranchPort();
                    double offAxis = free == null ? double.MaxValue : branchAxis.Distance(free.Origin);
                    double along = free == null ? 0.0 : free.Origin.DistanceTo(farEnd);
                    triedAngles.Add(candidateDegrees + " + slide " + MepRules.Mm(slide.Value) + ": " + (free == null
                        ? "branch port held after sliding"
                        : "off_axis_mm=" + MepRules.Mm(offAxis) + ", from_far_mm=" + MepRules.Mm(along)));
                    if (free != null && offAxis <= MepRules.CoincidenceToleranceFeet && along > MepRules.CoincidenceToleranceFeet && Faces(free))
                    { alignedBranch = free; break; }
                    ElementTransformUtils.MoveElement(doc, fitting.Id, runDir.Normalize() * -slide.Value);
                    doc.Regenerate();
                    fitting = doc.GetElement(fittingId) as FamilyInstance;
                    if (fitting == null) throw new InvalidOperationException("the wye disappeared while sliding back");
                }
            }
            if (alignedBranch == null)
            {
                // Connector.Angle did not move the port (measured live: the same 80 mm off the axis
                // at every angle, so the slide computed from it was ~0). Roll the whole fitting about
                // the run axis by the solved angle, then slide it along the run; both are re-read.
                stage = "rolling the wye about the run onto the drawn branch";
                Connector port = WyeBranchPort();
                if (port == null) triedAngles.Add("roll: branch port held by another element");
                else
                {
                    XYZ runAxis = live[0].CoordinateSystem.BasisZ.Normalize();
                    XYZ fo = (fitting.Location as LocationPoint)?.Point ?? port.Origin;
                    var sol = ObliquePipeTeeRules.SolveRollAndSlide(
                        new[] { runAxis.X, runAxis.Y, runAxis.Z }, new[] { fo.X, fo.Y, fo.Z },
                        new[] { port.Origin.X, port.Origin.Y, port.Origin.Z },
                        new[] { port.CoordinateSystem.BasisZ.X, port.CoordinateSystem.BasisZ.Y, port.CoordinateSystem.BasisZ.Z },
                        new[] { start.X, start.Y, start.Z }, FarArr());
                    if (sol.Problem != null) triedAngles.Add("roll: " + sol.Problem);
                    else if (sol.ResidualFeet > MepRules.CoincidenceToleranceFeet * 4)
                        triedAngles.Add("roll " + (sol.RollRadians * 180.0 / Math.PI).ToString("0.##", CultureInfo.InvariantCulture) + " deg + slide " + MepRules.Mm(sol.SlideFeet) +
                            " would leave the port " + MepRules.Mm(sol.ResidualFeet) + " off the drawn axis");
                    else
                    {
                        ElementTransformUtils.RotateElement(doc, fitting.Id, Line.CreateBound(fo, fo + runAxis), sol.RollRadians);
                        doc.Regenerate();
                        fitting = doc.GetElement(fittingId) as FamilyInstance;
                        if (fitting == null) throw new InvalidOperationException("the wye disappeared while rolling");
                        ElementTransformUtils.MoveElement(doc, fitting.Id, runAxis * sol.SlideFeet);
                        doc.Regenerate();
                        fitting = doc.GetElement(fittingId) as FamilyInstance;
                        if (fitting == null) throw new InvalidOperationException("the wye disappeared while sliding along the run");
                        port = WyeBranchPort();
                        double offAxis = port == null ? double.MaxValue : branchAxis.Distance(port.Origin);
                        double along = port == null ? 0.0 : port.Origin.DistanceTo(farEnd);
                        triedAngles.Add("roll " + (sol.RollRadians * 180.0 / Math.PI).ToString("0.##", CultureInfo.InvariantCulture) + " deg + slide " + MepRules.Mm(sol.SlideFeet) + ": " +
                            (port == null ? "branch port held after rolling" :
                             "off_axis_mm=" + MepRules.Mm(offAxis) + ", from_far_mm=" + MepRules.Mm(along) + ", faces_branch=" + Faces(port)));
                        if (port != null && offAxis <= MepRules.CoincidenceToleranceFeet && along > MepRules.CoincidenceToleranceFeet && Faces(port))
                            alignedBranch = port;
                    }
                }
            }
            if (alignedBranch == null)
                throw new InvalidOperationException("no wye angle places its branch port on the drawn pipe: " +
                    string.Join("; ", triedAngles));
            fittingBranch = alignedBranch;
            stage = "finding the original branch connector";
            Connector original = branchPipe.ConnectorManager.Connectors.Cast<Connector>()
                .FirstOrDefault(c => c.Id == originalConnectorId);
            if (original == null || original.IsConnected)
                throw new InvalidOperationException("the original oblique branch connector changed before attachment");
            stage = "connecting the original branch";
            try { fittingBranch.ConnectTo(original); }
            catch (Exception ex) { throw new InvalidOperationException("the wye branch could not connect to the drawn pipe: " + ex.Message, ex); }
            doc.Regenerate();
            stage = "verifying the original branch";
            fitting = doc.GetElement(fittingId) as FamilyInstance;
            if (fitting == null)
                throw new InvalidOperationException("Revit removed the wye when the original branch connected");
            branchPipe = doc.GetElement(branchPipeId) as Pipe;
            if (branchPipe == null)
                throw new InvalidOperationException("Revit removed the original branch when it connected");
            fittingBranch = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                .OrderBy(c => c.Origin.DistanceTo(start)).FirstOrDefault(c =>
                    c.AllRefs.Cast<Connector>().Any(other => other.Owner.Id == branchPipeId));
            original = branchPipe.ConnectorManager.Connectors.Cast<Connector>()
                .FirstOrDefault(c => c.Id == originalConnectorId);
            if (fittingBranch == null || original == null)
                throw new InvalidOperationException("the attached branch connectors could not be re-read");
            bool joined = fittingBranch.IsConnectedTo(original);
            double gap = fittingBranch.Origin.DistanceTo(original.Origin);
            if (joined && gap > MepRules.CoincidenceToleranceFeet)
            {
                stage = "trimming the drawn branch to the wye port";
                var location = branchPipe.Location as LocationCurve;
                var line = location?.Curve as Line;
                if (line == null) throw new InvalidOperationException("the connected branch is no longer straight");
                XYZ a = line.GetEndPoint(0), bEnd = line.GetEndPoint(1);
                XYZ port = fittingBranch.Origin;
                location.Curve = a.DistanceTo(original.Origin) < bEnd.DistanceTo(original.Origin)
                    ? Line.CreateBound(port, bEnd) : Line.CreateBound(a, port);
                doc.Regenerate();
                fitting = doc.GetElement(fittingId) as FamilyInstance;
                branchPipe = doc.GetElement(branchPipeId) as Pipe;
                if (fitting == null || branchPipe == null)
                    throw new InvalidOperationException("the wye or branch disappeared after trimming");
                fittingBranch = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                    .FirstOrDefault(c => c.AllRefs.Cast<Connector>().Any(other => other.Owner.Id == branchPipeId));
                original = branchPipe.ConnectorManager.Connectors.Cast<Connector>()
                    .FirstOrDefault(c => c.Id == originalConnectorId);
                joined = fittingBranch != null && original != null && fittingBranch.IsConnectedTo(original);
                gap = fittingBranch == null || original == null ? double.MaxValue :
                    fittingBranch.Origin.DistanceTo(original.Origin);
            }
            if (!joined || gap > MepRules.CoincidenceToleranceFeet)
                throw new InvalidOperationException("the oblique wye did not meet the branch at its drawn endpoint" +
                    " (connected=" + joined + ", gap_mm=" + MepRules.Mm(gap) +
                    ", wye_port=" + (fittingBranch == null ? "missing" : fittingBranch.Origin.ToString()) +
                    ", pipe_port=" + (original == null ? "missing" : original.Origin.ToString()) + ")");
            stage = "deleting the temporary branch";
            doc.Delete(temporary.Id);
            doc.Regenerate();
            fitting = doc.GetElement(fittingId) as FamilyInstance;
            branchPipe = doc.GetElement(branchPipeId) as Pipe;
            if (fitting == null || branchPipe == null)
                throw new InvalidOperationException("the connected wye did not survive deletion of the temporary pipe");
            fittingBranch = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                .FirstOrDefault(c => c.AllRefs.Cast<Connector>().Any(other => other.Owner.Id == branchPipeId));
            original = branchPipe.ConnectorManager.Connectors.Cast<Connector>()
                .FirstOrDefault(c => c.Id == originalConnectorId);
            if (fittingBranch == null || original == null || !fittingBranch.IsConnectedTo(original) ||
                fittingBranch.Origin.DistanceTo(original.Origin) > MepRules.CoincidenceToleranceFeet)
                throw new InvalidOperationException("the connected wye did not retain a coincident branch after deleting the temporary pipe");
            stage = "checking that both run ports are connected and coaxial with the run";
            string runProblem = CheckWyeRunPorts(doc, fittingId, runPipeIds[0], runPipeIds[1], runPoint, r);
            if (runProblem != null) throw new InvalidOperationException(runProblem);
            stage = "checking every wye port's bore against its pipe";
            fitting = doc.GetElement(fittingId) as FamilyInstance;
            foreach (Connector wyePort in fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>())
                foreach (Connector joinedEnd in wyePort.AllRefs.Cast<Connector>().Where(o => o.Owner is Pipe))
                    if (Math.Abs(wyePort.Radius - joinedEnd.Radius) > 0.5 / 304.8)
                        throw new InvalidOperationException("the wye's port " + wyePort.Id + " (" + MepRules.Mm(wyePort.Radius * 2) + ") is joined to pipe " +
                            Rid.Value(joinedEnd.Owner.Id) + " of " + MepRules.Mm(joinedEnd.Radius * 2) + ": the bores do not match");
            foreach (var started in startBores)
            {
                double boreNow = (doc.GetElement(started.Key) as Pipe)?.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? -1;
                if (Math.Abs(boreNow - started.Value) > 0.25 / 304.8)
                    throw new InvalidOperationException("pipe " + Rid.Value(started.Key) + " changed size while the wye was built (" + MepRules.Mm(started.Value) + " -> " + MepRules.Mm(boreNow) + ")");
            }
            plan.BuiltWyeTypeUniqueId = wye.UniqueId;
            return fitting;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("oblique wye failed while " + stage + ": " + ex.Message, ex);
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
                case "wall_profile": return e is Wall;
                case "displacement": return e is DisplacementElement;
                case "level": return e is Level; case "grid": return e is Grid; case "wall": return e is Wall;
                case "reference_plane": return e is ReferencePlane;
                case "floor": return e is Floor; case "ceiling": return e is Ceiling; case "roof": return e is FootPrintRoof;
                case "room": return e is Autodesk.Revit.DB.Architecture.Room;
                case "space": return e is Autodesk.Revit.DB.Mechanical.Space;
                case "area": return e is Autodesk.Revit.DB.Area; case "toposolid": return IsToposolid(e);
                case "area_boundary":
                    return e is CurveElement && InCategory(e, BuiltInCategory.OST_AreaSchemeLines);
                case "sprinkler": return e is FamilyInstance && InCategory(e, BuiltInCategory.OST_Sprinklers);
                case "family_instance": return e is FamilyInstance; case "duct": return e is Duct;
                case "pipe": return e is Pipe; case "conduit": return e is Conduit; case "cable_tray": return e is CableTray;
                case "flex_pipe": return e is FlexPipe; case "flex_duct": return e is FlexDuct;
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

        private static Element FindHost(Document doc, JObject spec, XYZ point, Level level, double scale, out XYZ snapped, out JObject how)
        {
            snapped = null;
            string kind = ((string)spec["category"] ?? "").ToLowerInvariant();
            double max = ((double?)spec["max_distance_mm"] ?? 600) * scale;
            if (point == null) throw new ArgumentException("find_host needs the instance point");
            if (kind == "wall")
            {
                Element best = null; double bestD = double.MaxValue; XYZ bestP = null; bool bestSameLevel = false;
                foreach (Wall w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
                {
                    if (!(w.Location is LocationCurve lc) || lc.Curve == null) continue;
                    Curve curve = lc.Curve;
                    double z0 = curve.GetEndPoint(0).Z;
                    XYZ flat = new XYZ(point.X, point.Y, z0);
                    IntersectionResult pr = curve.Project(flat);
                    if (pr == null) continue;
                    XYZ q = pr.XYZPoint;
                    double d = new XYZ(q.X - point.X, q.Y - point.Y, 0).GetLength();
                    if (d > max) continue;
                    bool same = level != null && w.LevelId == level.Id;
                    if (best == null || (same && !bestSameLevel) || (same == bestSameLevel && d < bestD))
                    { best = w; bestD = d; bestP = new XYZ(q.X, q.Y, point.Z); bestSameLevel = same; }
                }
                if (best == null) throw new ArgumentException("find_host: no wall within " + Math.Round(max / scale).ToString(System.Globalization.CultureInfo.InvariantCulture) + " of the point");
                // NOT snapped by default: the wall-based route projects the point onto the wall line itself AND
                // reads which side of the line it was drawn on to choose the face. Snapping first erased that
                // side and put a lavatory on the outside face of its wall (measured).
                snapped = spec["snap"] != null && (bool)spec["snap"] ? bestP : null;
                how = new JObject { ["host_id"] = Rid.Value(best.Id), ["category"] = "wall", ["distance"] = Math.Round(bestD / scale, 1),
                    ["same_level"] = bestSameLevel, ["snapped_to_wall_line"] = snapped != null };
                return best;
            }
            Type cls = kind == "ceiling" ? typeof(Ceiling) : kind == "floor" ? typeof(Floor) : kind == "roof" ? typeof(RoofBase) : null;
            if (cls == null) throw new ArgumentException("find_host.category must be wall, ceiling, floor or roof");
            Element hit = null; bool hitSame = false;
            foreach (Element e in new FilteredElementCollector(doc).OfClass(cls).WhereElementIsNotElementType())
            {
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb == null || point.X < bb.Min.X || point.X > bb.Max.X || point.Y < bb.Min.Y || point.Y > bb.Max.Y) continue;
                bool same = level != null && e.LevelId == level.Id;
                if (hit == null || (same && !hitSame)) { hit = e; hitSame = same; }
            }
            if (hit == null) throw new ArgumentException("find_host: no " + kind + " contains the point in plan");
            how = new JObject { ["host_id"] = Rid.Value(hit.Id), ["category"] = kind, ["same_level"] = hitSame };
            return hit;
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
            if (!Rid.CanRepresent(raw)) throw new ArgumentException(field + " is outside ElementId range" + DocumentContext.Hint<T>(doc, raw.ToString()));
            T value = doc.GetElement(Rid.Make(raw)) as T;
            if (value == null) throw new ArgumentException(field + "=" + raw + " does not identify a " + typeof(T).Name + DocumentContext.Hint<T>(doc, raw.ToString()));
            return value;
        }
        private static XYZ Point(JToken token, double scale, bool requireZ)
        {
            JArray a = token as JArray;
            int minimum = requireZ ? 3 : 2;
            if (a == null || a.Count < minimum || a.Count > 3) throw new ArgumentException("point/start/end must contain " + minimum + " XYZ coordinates");
            return new XYZ(GeometryInput.Number(a[0], "X") * scale, GeometryInput.Number(a[1], "Y") * scale,
                (a.Count > 2 ? GeometryInput.Number(a[2], "Z") : 0) * scale);
        }
        /// <summary>flex_pipe/flex_duct: the path FlexPipe.Create/FlexDuct.Create take directly, in order.</summary>
        private static List<XYZ> ReadPoints(JToken token, double scale)
        {
            JArray a = token as JArray;
            if (a == null || a.Count < 2) throw new ArgumentException("points must contain at least 2 XYZ coordinates");
            var list = new List<XYZ>(a.Count);
            foreach (JToken t in a) list.Add(Point(t, scale, true));
            return list;
        }
        /// <summary>
        /// OPEN CHAINS of curves, for the things Revit takes as a run rather than
        /// a boundary. Two points is a line and is the ordinary case; nothing is
        /// closed back to the start, because a separator that closed itself would
        /// bound a room the drawing does not show.
        /// </summary>
        private static IList<List<Curve>> Chains(JArray profile, double scale, bool closed = false)
        {
            if (profile == null || profile.Count == 0) throw new ArgumentException("profile requires at least one chain");
            var result = new List<List<Curve>>();
            foreach (JArray chainToken in profile.OfType<JArray>())
            {
                if (chainToken.Count < 2) throw new ArgumentException("every profile chain needs at least two points");
                IList<double[]> declared = chainToken.Select(t => { XYZ q = Point(t, scale, true); return new[] { q.X, q.Y, q.Z }; }).ToList();
                List<XYZ> points = EnclosedRules.SeparatorChain(declared, closed).Select(q => new XYZ(q[0], q[1], q[2])).ToList();
                var chain = new List<Curve>();
                for (int i = 0; i + 1 < points.Count; i++) chain.Add(Line.CreateBound(points[i], points[i + 1]));
                result.Add(chain);
            }
            if (result.Count != profile.Count) throw new ArgumentException("every profile entry must be an array of XYZ points");
            return result;
        }

        private static IList<CurveLoop> Loops(JToken profile, double scale, double shortCurveTolerance = 1e-6)
        {
            var result = new List<CurveLoop>();
            foreach (var contour in GeometryInput.HorizontalProfile(profile, scale, shortCurveTolerance))
            {
                var loop = new CurveLoop();
                var points = contour.Select(v => new XYZ(v[0], v[1], v[2])).ToList();
                for (int i = 0; i < points.Count; i++) loop.Append(Line.CreateBound(points[i], points[(i+1)%points.Count]));
                result.Add(loop);
            }
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
        /// <summary>
        /// A RECTANGULAR SECTION: width and height together, in the call's units, on a type
        /// whose shape is rectangular. Every way this can be two statements at once is a
        /// refusal before anything is written: a diameter AND a section, only one side, a
        /// non-positive side, or a section asked of a round or oval type - a round duct of
        /// equal area is a different duct and is never substituted.
        /// Width is the HORIZONTAL side of the section of a horizontal run, which is Revit's
        /// own convention for a rectangular duct and is re-read from the connectors.
        /// </summary>
        private static void ReadSection(Plan p, JObject item, double scale)
        {
            JToken w = item["width"], h = item["height"];
            if (w == null && h == null)
            {
                if (p.Diameter.HasValue && p.Type is DuctType round && round.Shape == ConnectorProfileType.Rectangular)
                    throw new ArgumentException("diameter was given for a RECTANGULAR duct type ('" + p.Type.Name +
                        "'): a rectangular run has a width and a height. Send width and height, or a round type.");
                return;
            }
            if (w == null || h == null)
                throw new ArgumentException("width and height come together: a section with one side is not a section");
            if (p.Diameter.HasValue)
                throw new ArgumentException("diameter AND width/height were both given: two sections for one run. Send one.");
            double width = Finite(w.Value<double>(), "width") * scale, height = Finite(h.Value<double>(), "height") * scale;
            if (width <= 0 || height <= 0) throw new ArgumentException("width and height must be positive");
            var type = p.Type as DuctType;
            if (type == null || type.Shape != ConnectorProfileType.Rectangular)
                throw new ArgumentException("width/height were given but duct type '" + (p.Type?.Name ?? "?") + "' is " +
                    (type == null ? "not a duct type" : type.Shape.ToString()).ToLowerInvariant() +
                    ", not rectangular. A round duct of equal area is a different duct; choose a rectangular type.");
            p.SectionWidth = width;
            p.SectionHeight = height;
        }

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
            if (element is Autodesk.Revit.DB.Plumbing.Pipe || element is Autodesk.Revit.DB.Plumbing.FlexPipe)
                candidates = new[] { BuiltInParameter.RBS_PIPE_DIAMETER_PARAM };
            else if (element is Autodesk.Revit.DB.Electrical.Conduit)
                candidates = new[] { BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM };
            else if (element is Autodesk.Revit.DB.Mechanical.Duct || element is Autodesk.Revit.DB.Mechanical.FlexDuct)
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
        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
        private static double Finite(double value, string field)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException(field + " must be finite"); return value; }

        /// <summary>A point frozen for the plan, to a tenth of a millimetre.</summary>
        private static string Canon01(XYZ point) =>
            System.Math.Round(point.X * 304.8, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            System.Math.Round(point.Y * 304.8, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            System.Math.Round(point.Z * 304.8, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

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
                // In PLAN: Revit keeps a wall's location curve at its level elevation and carries the base
                // offset as a parameter (verified separately as "offset"), so a centre declared at the wall's
                // base read "off" by exactly that offset (measured: 141.8 mm on a wall 0.465 ft above its level).
                double centreOff = Math.Sqrt((arc.Center.X - made.ExpectedArcCentre.X) * (arc.Center.X - made.ExpectedArcCentre.X) +
                                             (arc.Center.Y - made.ExpectedArcCentre.Y) * (arc.Center.Y - made.ExpectedArcCentre.Y)) * 304.8;
                double radiusOff = Math.Abs(arc.Radius - made.ExpectedArcRadius) * 304.8;
                o["centre_off_mm"] = Math.Round(centreOff, 4);
                o["radius_off_mm"] = Math.Round(radiusOff, 4);
                o["radius_mm"] = Math.Round(arc.Radius * 304.8, 4);
                // THE SHAPE, not the centre: the largest distance from the built curve to the declared circle, sampled
                // along it, and the declared third point must lie ON the built curve (the complementary arc lies on
                // the same circle and would otherwise pass). Centre and radius stay in the reply as information: on a
                // shallow arc they move by millimetres while the wall does not (ArcTolerance.ShapeOffMm).
                var samples = new List<double[]>();
                for (int k = 0; k <= 32; k++)
                {
                    XYZ q = arc.Evaluate(k / 32.0, true);
                    samples.Add(new[] { q.X * 304.8, q.Y * 304.8 });
                }
                double shapeOff = ArcTolerance.ShapeOffMm(made.ExpectedArcCentre.X * 304.8, made.ExpectedArcCentre.Y * 304.8,
                    made.ExpectedArcRadius * 304.8, samples);
                double thirdOff = double.NaN;
                if (made.ExpectedArcThird != null)
                {
                    IntersectionResult hit = arc.Project(new XYZ(made.ExpectedArcThird.X, made.ExpectedArcThird.Y, arc.Center.Z));
                    if (hit != null) thirdOff = hit.XYZPoint.DistanceTo(new XYZ(made.ExpectedArcThird.X, made.ExpectedArcThird.Y, arc.Center.Z)) * 304.8;
                }
                o["shape_off_mm"] = double.IsNaN(shapeOff) ? JValue.CreateNull() : (JToken)Math.Round(shapeOff, 4);
                o["third_off_mm"] = double.IsNaN(thirdOff) ? JValue.CreateNull() : (JToken)Math.Round(thirdOff, 4);
                // 1 mm, growing with the radius (0.01 %) up to 5 mm. The tolerance used is in the reply.
                double tolerance = ArcTolerance.Mm(made.ExpectedArcRadius * 304.8);
                o["tolerance_mm"] = Math.Round(tolerance, 4);
                o["tolerance_rule"] = ArcTolerance.Rule;
                o["check"] = "shape: built curve within tolerance of the declared circle, declared third point on the built curve";
                o["verified"] = !double.IsNaN(shapeOff) && !double.IsNaN(thirdOff) && shapeOff <= tolerance && thirdOff <= tolerance;
                if (!(bool)o["verified"])
                    o["means"] = "the element IS an arc and not the arc that was declared: it lies up to " +
                                 (double.IsNaN(shapeOff) ? "(unmeasured)" : Math.Round(shapeOff, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)) +
                                 " mm from the declared circle and " +
                                 (double.IsNaN(thirdOff) ? "(unmeasured)" : Math.Round(thirdOff, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)) +
                                 " mm from the declared middle point (tolerance " +
                                 Math.Round(tolerance, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + " mm).";
            }
            catch (Exception ex)
            {
                o["verified"] = false;
                o["measured"] = false;
                o["means"] = "the built curve could not be re-read: " + ex.Message;
            }
            return o;
        }

        private sealed class Plan
        {
            // Assumptions resolved in planning and those measured during Revit construction share one list.
            public JArray Assumptions = new JArray();
            public int Index; public string Kind; public JObject Input; public double Scale, Elevation, Height, Offset;
            /// <summary>A declared level-span exception: the reason, where it came from and the levels it crosses.</summary>
            public string LevelSpanException, LevelSpanSource; public List<string> LevelsCrossed;
            public XYZ Start, End; public Level Level; public Element Type, SystemType; public IList<CurveLoop> Loops;
            /// <summary>grid: "maximize" (vertical extent spans every level) or "none"; GridFlattenNote says a Z was dropped.</summary>
            public string GridExtent3D = GridExtentRules.Maximize, GridFlattenNote;
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
            /// <summary>placement='all_enclosed': the phase the circuit was found in, and that the row came from one.</summary>
            public Phase Phase; public bool Enclosed;
            /// <summary>kind=toposolid: the top-surface points (internal feet) and the indices re-read after the commit.</summary>
            public List<XYZ> TopoPoints; public List<int> TopoSamples;
            /// <summary>kind=toposolid from landxml_path: what was read (file, surface, hash, counts, position), echoed in the plan.</summary>
            public JObject TopoSource;

            /// <summary>shaft: the two levels it runs BETWEEN, which is what makes it a shaft.</summary>
            public Level BaseLevel;
            public Level TopLevel;
            /// <summary>room_separator: the view whose sketch plane the lines live on.</summary>
            public View SeparatorView;
            /// <summary>room_separator: how many curves Revit actually made, re-read after the call.</summary>
            public int SeparatorSegments;
            /// <summary>The bore the row declared, in FEET. Null: the type decides.</summary>
            public double? Diameter; public double ArcRadius;
            /// <summary>A rectangular run's declared section, in FEET. Null: no section was declared.</summary>
            public double? SectionWidth, SectionHeight;
            public List<StairRunSpec> StairRuns;
            public List<StairLandingSpec> StairLandings;
            public int DesiredRisers;
            public double TreadDepth;
            public List<Curve> WallProfile;
            public XYZ ProfileNormal, ProfileOrigin, Displacement;
            public List<ElementId> DisplacedIds;
            public View3D OwnerView;
            public JObject DisplacedState;
            public List<TypeParameterWrites.Write> ParameterWrites;
            public double Rotation, TopOffset; public Element Host; public double[] Slopes; public bool[] DefinesSlope; public List<ModelCurve> RoofEdges;
            public Line CeilingArrow; public double CeilingSlopeRatio; public double[] CeilingExpectedElevations;
            /// <summary>roof: per edge (all contours, perimeter first) the overhang / offset asked for, in feet; null = not asked.</summary>
            public double?[] Overhangs, EdgeOffsets;
            /// <summary>reference_plane: the cut vector (the view's own direction).</summary>
            public XYZ RefCut;
            public StructuralType StructuralType; public JObject Summary;
            public string FittingSubtype; public List<FittingMember> FittingMembers;
            public SizedFitting DiameterFitting;
            public ReducerPorts BuiltReducerPorts;
            public bool KeepNativeConduitDiameter;
            public string BuiltWyeTypeUniqueId;
            public Element TakeoffMain; public int? TakeoffMainBatchIndex;
            public bool TakeoffTeeFallbackAllowed, TakeoffAsTee;
            /// <summary>Set when a later takeoff_or_tee split this curve: its other piece.</summary>
            public ElementId SplitPiece;
            /// <summary>Set when takeoff_or_tee joined this branch with a tee: the tee.</summary>
            public ElementId TeeJunction;
            public Wall OpeningHost; public Element InstanceHost;

            /// <summary>
            /// The face this row was placed on, when the family is work-plane
            /// based. Kept so the postcondition can re-read the SAME reference
            /// rather than asking whether some host exists.
            /// </summary>
            public CadFaceChoice FacePlacement;

            /// <summary>A wall-based family placed on its wall's line: where, why, and the facing it must keep.</summary>
            public JObject HostedEvidence;
            public XYZ HostedFacing;
            /// <summary>The requested reflection was already made while choosing the face.</summary>
            public bool FlipDone;
            /// <summary>Facing before a reflected copy replaced the instance: a reflection keeps it.</summary>
            public XYZ FacingBeforeReflection;

            /// <summary>How a requested reflection was achieved: flip_hand or reflected_copy.</summary>
            public string MirrorMethod;
            public Element SlabHost; public string SlabShape; public XYZ SlabCenter;
            public double SlabWidth, SlabHeight, RotationRadians;
            public List<XYZ> ProfilePoints; public XYZ BeamDirection;
            public double BeamSpacing; public FamilySymbol BeamType;
            public Wall FoundationWall;
            /// <summary>Requested physical dimensions in internal feet; nullable means the selected type/host decides.</summary>
            public double? FoundationTop, FoundationWidth, FoundationThickness;
            public MEPCurve InlineHost; public XYZ InlinePoint;
            /// <summary>flex_pipe/flex_duct: the full path, including its two ends (Start/End mirror the first/last point).</summary>
            public List<XYZ> FlexPoints;
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
            public XYZ ExpectedArcCentre, ExpectedArcThird;
            public bool? ExpectedStructural;
            public string ExpectedName;
            public string ExpectedNumber;
            /// <summary>Siblings one call made beside the row's own element. Never dropped.</summary>
            public List<ElementId> AlsoCreated = new List<ElementId>();
            public double? ExpectedDiameter; public double ExpectedArcRadius; public bool ExpectedArc;
            public double? ExpectedWidth, ExpectedHeight;
            public Plan Plan;
            public List<Created> Batch;
        }
    }
}
