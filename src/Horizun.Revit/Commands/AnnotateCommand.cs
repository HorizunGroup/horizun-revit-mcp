// -----------------------------------------------------------------------------
// Horizun Revit MCP - text, tags and dimensions with explicit references.
//
// THE DIMENSION OPERATIONS ARE PRODUCTION WRITES, and this file holds them to
// the same bar as transform and write_params:
//
//   * a dry run does not merely validate arguments - it CREATES every annotation
//     provisionally in one transaction, regenerates, measures the result, and
//     rolls back, reporting the rollback status Revit actually returned. What a
//     caller approves is a batch Revit has already demonstrated it can build,
//     with the measured values in hand;
//   * the materialised plan binds the view, the EFFECTIVE dimension type
//     (explicit or the document default resolved now - a default swapped between
//     rehearsal and apply is a stale plan), every reference reserialized with
//     its owner, its subgeometry kind and a 0.1 mm geometry fingerprint, the
//     requested line, and the value the rehearsal measured;
//   * the apply runs inside a TransactionGroup: create, regenerate, verify every
//     postcondition while the transaction is still reversible, commit only when
//     all of them hold, re-read everything again after the commit, and only then
//     assimilate. Any failure rolls the WHOLE batch back and the response
//     carries the real TransactionStatus of every rollback - uncertain when
//     Revit did not confirm one, never a claimed-clean model;
//   * verification is per field: requested / read / match, with the comparison
//     tolerance named. There is no bare verified=true for a dimension.
//
// Operations whose API does not exist in this Revit (radial/diameter/arc length
// before 2025, spot slope everywhere) refuse with an ORDINARY planning error on
// purpose, never UnsupportedCapability: that type grants the Python fallback,
// and Python cannot call a class that is absent from RevitAPI.dll either.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
using VerifyStage = Horizun.Revit.Core.DimensionPlanRules.VerifyStage;

namespace Horizun.Revit.Commands
{
    public sealed partial class AnnotateCommand : ICommand
    {
        public string Name => "horizun_annotate";
        public string Description => "Create text, tags and dimensions atomically and verify the committed annotations.";

#if REVIT2023
        private const string HostRevitYear = "2023";
#elif REVIT2024
        private const string HostRevitYear = "2024";
#elif REVIT2025
        private const string HostRevitYear = "2025";
#elif REVIT2026
        private const string HostRevitYear = "2026";
#elif REVIT2027
        private const string HostRevitYear = "2027";
#else
        private const string HostRevitYear = "unknown";
#endif

        // ---------------------------------------------------------------------
        // Planning.
        // ---------------------------------------------------------------------
        private static Plan PlanAction(Document doc, int index, JObject a, double scale, long? activeGraphicalViewId, out string error,
                                       out string unsupportedReason, out string errorCode)
        {
            error = null; unsupportedReason = null; errorCode = null;
            if (a == null) { error = "entry is not an object"; return null; }
            try
            {
                string op = (a.Value<string>("operation") ?? "").ToLowerInvariant();
                // The closed enum first: an operation OUTSIDE it is the structural gap
                // that may grant the Python fallback. An operation INSIDE it that this
                // Revit's API cannot build must NOT - see the year guards below.
                if (!DimensionPlanRules.IsKnownOperation(op))
                    throw new UnsupportedCapability(
                        "unsupported operation '" + op + "' - horizun_annotate creates text, tags and dimensions " +
                        "(linear, angular, radial, diameter, arc length, spot elevation, spot coordinate) only. " +
                        "Nothing was written.", FallbackSignal.ReasonUnsupportedOperation);

                // No Revit API can create these here, and Python cannot call an absent
                // class either - so these are ORDINARY planning errors on purpose, never
                // UnsupportedCapability: that type is the grant of the Python fallback,
                // and granting it would send a client to script against nothing.
#if REVIT2023 || REVIT2024
                if (op == DimensionPlanRules.OpRadial || op == DimensionPlanRules.OpDiameter)
                    throw new ArgumentException(DimensionPlanRules.NoApiThisYear(op, "RadialDimension.Create", 2025, HostRevitYear));   // not supported before 2025
                if (op == DimensionPlanRules.OpArcLength)
                    throw new ArgumentException(DimensionPlanRules.NoApiThisYear(op, "ArcLengthDimension.Create", 2025, HostRevitYear));   // not supported before 2025
#endif

                View view = Need<View>(doc, a, "view_id");
                if (view.IsTemplate) throw new ArgumentException("view_id is a template");
                var p = new Plan { Index = index, Operation = op, View = view, Input = a, Scale = scale };
                string textOnly = TextNoteLayoutRules.ForeignFields(op, f => a[f] != null);
                if (textOnly != null) throw new ArgumentException(textOnly);
                foreach (string key in new[] { "layout_clearance", "layout_max_displacement" })
                    if (a[key] != null && (op != "tag" || (a[key].Type != JTokenType.Integer && a[key].Type != JTokenType.Float) ||
                        !DeliveryLayoutRules.Finite(a.Value<double>(key)) || a.Value<double>(key) < 0))
                        throw new ArgumentException(key + " requires a finite nonnegative tag layout distance.");
                foreach (string key in new[] { "avoid_collisions", "require_tag_text" })
                    if (a[key] != null && (op != "tag" || a[key].Type != JTokenType.Boolean))
                        throw new ArgumentException(key + " is a boolean tag option only.");
                if (a["layout_accept_unmeasurable"] != null)
                {
                    if (op != "tag")
                        throw new ArgumentException("layout_accept_unmeasurable is a tag option only.");
                    p.LayoutAccepted = AnnotationVisibility.ReadAccepted(a["layout_accept_unmeasurable"]);
                }
                if (op == "text" || op == "tag")
                {
                    // The schema carries the dimension fields on EVERY action (one
                    // properties table, conditional requireds), so a text action offering
                    // expected_value would otherwise succeed with the option silently
                    // dropped - a request the caller believes was honoured. Same rule the
                    // dimension path applies through UnavailableOptions, extended to the
                    // dimension-shape fields these operations cannot mean.
                    var foreign = new List<string>();
                    foreach (string f in DimensionPlanRules.OptionFields)
                        if (a[f] != null && !(op == "text" && f == "allow_outside_crop")) foreign.Add(f);   // a text note may be placed outside the crop on purpose
                    foreach (string f in new[] { "line_start", "line_end", "references",
                                                 "arc_center", "arc_radius", "arc_reference", "reference" })
                        if (a[f] != null) foreign.Add(f);
                    if (foreign.Count > 0)
                        throw new ArgumentException(op + " does not carry " + string.Join(", ", foreign) +
                            " - those are dimension fields, and accepting them here would drop them silently. " +
                            "Remove them, or use a dimension operation.");
                }
                if (op == "text")
                {
                    if (a["tag_type_id"] != null || a["element_id"] != null || a["tag_mode"] != null ||
                        a["orientation"] != null || a["add_leader"] != null)
                        throw new ArgumentException("text does not accept tag_type_id, element_id, tag_mode, " +
                            "orientation or add_leader; accepting them would silently drop tag intent.");
                    p.Point = Point(a["point"], scale, false); p.Text = a.Value<string>("text");
                    if (string.IsNullOrEmpty(p.Text)) throw new ArgumentException("text is required");
                    p.Type = Need<TextNoteType>(doc, a, "text_type_id");
                    if (a["expected_height_mm"] != null)
                    {
                        double requested = a.Value<double>("expected_height_mm");
                        if (requested <= 0) throw new ArgumentException("expected_height_mm must be positive.");
                        Parameter size = p.Type.get_Parameter(BuiltInParameter.TEXT_SIZE);
                        if (size == null || !size.HasValue)
                            throw new ArgumentException("text_type_id has no readable TEXT_SIZE; height cannot be verified.");
                        double actual = size.AsDouble() * 304.8;
                        if (Math.Abs(actual - requested) > 0.1)
                            throw new ArgumentException("text_type_id has height " + actual.ToString("0.###", CultureInfo.InvariantCulture) +
                                " mm, but expected_height_mm is " + requested.ToString("0.###", CultureInfo.InvariantCulture) +
                                ". Choose a TextNoteType of the requested height; no type was silently resized.");
                    }
                    if (a["horizontal_alignment"] != null) ParseHorizontal(a.Value<string>("horizontal_alignment"));
                    if (a["vertical_alignment"] != null) ParseVertical(a.Value<string>("vertical_alignment"));
                    if (a["rotation_degrees"] != null)
                    {
                        double angle = a.Value<double>("rotation_degrees");
                        if (double.IsNaN(angle) || double.IsInfinity(angle))
                            throw new ArgumentException("rotation_degrees must be finite.");
                    }
                    p.TextPlan = PlanText(doc, p, a, scale);
                }
                else if (op == "tag")
                {
                    if (a["text"] != null || a["text_type_id"] != null || HasTextOptions(a))
                        throw new ArgumentException("tag does not accept text or text-note options; accepting them would silently drop text-note intent.");
                    p.Point = Point(a["point"], scale, false);
                    string linkProblem = LinkedTagRules.Check(a["element_id"] != null, a[LinkedTagRules.LinkInstanceField] != null,
                        a[LinkedTagRules.LinkedElementField] != null, out bool linkedTarget);
                    if (linkProblem != null) throw new ArgumentException(linkProblem);
                    if (linkedTarget)
                    {
                        // An element of a Revit link: the pair (link instance in THIS document,
                        // element id inside the linked model). The tag Revit creates is bound to
                        // the pair, so a reload of the link keeps it attached.
                        p.LinkInstance = Need<RevitLinkInstance>(doc, a, LinkedTagRules.LinkInstanceField);
                        Document linkDoc = p.LinkInstance.GetLinkDocument();
                        if (linkDoc == null)
                            throw new ArgumentException("link_instance_id " + Rid.Value(p.LinkInstance.Id) + " is not loaded, so its elements cannot be tagged. Reload the link first.");
                        long linkedId = a.Value<long?>(LinkedTagRules.LinkedElementField) ?? -1;
                        p.LinkedElement = Rid.CanRepresent(linkedId) ? linkDoc.GetElement(Rid.Make(linkedId)) : null;
                        if (p.LinkedElement == null)
                            throw new ArgumentException("linked_element_id " + linkedId + " does not exist in the linked document '" + linkDoc.Title + "'.");
                        // A Room of a link IS taggable: NewRoomTag takes LinkElementId(link instance, room)
                        // in 2023-2027. An Area, a Space or a nested link instance is not (TagRules).
                        string linkedRefusal = TagRules.LinkedTargetRefusal(LinkedKind(p.LinkedElement));
                        if (linkedRefusal != null) throw new ArgumentException(linkedRefusal);
                        p.Target = p.LinkInstance;
                    }
                    else p.Target = Need<Element>(doc, a, "element_id");
                    if (a["tag_type_id"] != null)
                        p.Type = Need<ElementType>(doc, a, "tag_type_id");
                    PlanTagOptions(doc, p, a, scale);
                    if (TaggedRoom(p) != null)
                    {
                        if (a.Value<bool?>("avoid_collisions") == true)
                            throw new ArgumentException("RoomTag collision placement is not measured by this operation; omit avoid_collisions.");
                        if (a["tag_mode"] != null && TagRules.NormalizeMode(a.Value<string>("tag_mode")) != "by_category")
                            throw new ArgumentException("RoomTag only accepts tag_mode=by_category.");
                        if (p.Type != null && p.Type.Category?.Id != Category.GetCategory(doc, BuiltInCategory.OST_RoomTags)?.Id)
                            throw new ArgumentException("tag_type_id must be a RoomTag type for a Room target.");
                        if (a["tag_type_name"] != null && a["tag_family_path"] == null)
                            throw new ArgumentException("tag_type_name requires tag_family_path; otherwise use tag_type_id.");
                        bool? roomContainsPoint = RoomContainsHostPoint(TaggedRoom(p), p.LinkInstance, p.Point);
                        string outsideRoom = RoomTagRules.OutsideRoomRefusal(roomContainsPoint, a.Value<bool?>("add_leader") == true, Rid.Value(TaggedRoom(p).Id));
                        if (outsideRoom != null) throw new ArgumentException(outsideRoom);
                        p.RoomTagFamilyPath = ResolveRoomTagFamily(doc, a.Value<string>("tag_family_path"));
                        if (p.RoomTagFamilyPath != null)
                        {
                            string newer = FamilyVersionGate.NewerError(doc.Application, p.RoomTagFamilyPath, out JObject newerDetail);
                            if (newer != null) throw new ArgumentException(newer);
                        }
                        if (p.RoomTagFamilyPath != null)
                        {
                            string familyName = Path.GetFileNameWithoutExtension(p.RoomTagFamilyPath);
                            if (new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                                .Any(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase)))
                                throw new ArgumentException("RoomTag family '" + familyName + "' is already loaded; use its tag_type_id rather than tag_family_path, since the loaded content cannot be proven equal to the file.");
                            p.RoomTagFamilyHash = FileHash(p.RoomTagFamilyPath);
                        }
                    }
                    else if (SpatialKind(p.Target) != null) PlanSpatialTag(doc, p, a);
                    else if (a["tag_family_path"] != null || a["tag_type_name"] != null)
                        throw new ArgumentException("tag_family_path and tag_type_name are only valid when element_id identifies a Room.");
                    else PlanIndependentTag(doc, p, a);
                    p.ExistingTagCount = p.LinkedElement != null
                        ? ExistingLinkedTagCount(doc, p.View.Id, p.LinkInstance.Id, p.LinkedElement.Id)
                        : ExistingTagCount(doc, p.View.Id, p.Target.Id);
                }
                else
                {
                    p.TargetViewIsActive = activeGraphicalViewId == Rid.Value(view.Id);
                    PlanDimension(doc, p, a, scale);
                }
                return p;
            }
            catch (LinkedDatumDimensionException ex)
            {
                error = ex.Message;
                errorCode = DimensionReferenceRules.CodeLinkedDatumRejected;
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return null;
            }
        }

        /// <summary>
        /// One dimension action, fully resolved: view compatibility, the conditional
        /// requirements table, option availability and eligibility, references parsed
        /// AND reserialized AND fingerprinted, the effective type materialised. Throws
        /// with a message the caller can act on; writes nothing.
        /// </summary>
        private static void PlanDimension(Document doc, Plan p, JObject a, double scale)
        {
            string op = p.Operation;

            List<string> missing = DimensionPlanRules.MissingFields(op, f => a[f] != null);
            if (missing != null && missing.Count > 0)
                throw new ArgumentException(op + " is missing required field(s): " + string.Join(", ", missing));

            var offered = DimensionPlanRules.OptionFields.Where(f => a[f] != null).ToList();
            List<string> unavailable = DimensionPlanRules.UnavailableOptions(op, offered);
            if (unavailable.Count > 0) throw new ArgumentException(string.Join(" ", unavailable));
            p.RequirePrintable = a.Value<bool?>("require_printable") ?? false;

            // Where a dimension may live. Schedules and sheets have no model space to
            // measure in; a 3D view is only stable once it is locked - Revit itself
            // refuses annotation in an unlocked one.
            if (p.View is ViewSchedule) throw new ArgumentException("view_id is a schedule; a dimension needs a model view.");
            if (p.View is ViewSheet) throw new ArgumentException("view_id is a sheet; a dimension needs a model view.");
            var v3 = p.View as View3D;
            if (v3 != null && !v3.IsLocked)
                throw new ArgumentException("view_id is an UNLOCKED 3D view. Revit only accepts dimensions in a " +
                                            "locked 3D view (View3D.IsLocked); lock it or pick a 2D view.");

            // ---- references ----
            if (op == DimensionPlanRules.OpDimension || op == DimensionPlanRules.OpAngular ||
                op == DimensionPlanRules.OpArcLength)
            {
                JArray refsToken = a["references"] as JArray;
                List<string> stable = refsToken == null
                    ? null
                    : refsToken.Select(t => t != null && t.Type == JTokenType.String ? t.Value<string>() : null).ToList();
                string listError = DimensionPlanRules.ReferenceListError(op, stable);
                if (listError != null) throw new ArgumentException(listError);
                for (int i = 0; i < stable.Count; i++) stable[i] = ResolveLevelToken(doc, stable[i], "references[" + i + "]");
                ResolvePatternRequests(doc, p, a, stable, scale);
                for (int i = 0; i < stable.Count; i++) AddReference(doc, p, stable[i], "references[" + i + "]");
            }
            else
            {
                string single = a.Value<string>("reference");
                if (string.IsNullOrWhiteSpace(single))
                    throw new ArgumentException(op + " needs 'reference': one stable reference string.");
                if (a["pattern_points"] != null)
                    throw new ArgumentException("pattern_points is only for operation=dimension.");
                AddReference(doc, p, ResolveLevelToken(doc, single, "reference"), "reference");
            }
            if (op == DimensionPlanRules.OpArcLength)
            {
                string arcRef = a.Value<string>("arc_reference");
                if (string.IsNullOrWhiteSpace(arcRef))
                    throw new ArgumentException(op + " needs 'arc_reference': the curved edge being measured.");
                p.ArcReference = AddReference(doc, p, arcRef, "arc_reference");
            }
            if (op == DimensionPlanRules.OpRadial || op == DimensionPlanRules.OpDiameter)
                p.ArcReference = p.RefList[0];

            // ---- per-operation geometry ----
            if (op == DimensionPlanRules.OpDimension)
            {
                XYZ x = Point(a["line_start"], scale, true), y = Point(a["line_end"], scale, true);
                if (x.DistanceTo(y) < 1e-9) throw new ArgumentException("dimension line endpoints must differ");
                p.Line = Line.CreateBound(x, y);
                p.References = new ReferenceArray();
                foreach (Reference r in p.RefList) p.References.Append(r);

                List<string> ineligible = DimensionPlanRules.IneligibleOptions(p.RefList.Count, offered);
                if (ineligible.Count > 0) throw new ArgumentException(string.Join(" ", ineligible));
                if (a["prefix"] != null) { p.HasPrefix = true; p.Prefix = a.Value<string>("prefix") ?? ""; }
                if (a["suffix"] != null) { p.HasSuffix = true; p.Suffix = a.Value<string>("suffix") ?? ""; }
                if (a["above"] != null) { p.HasAbove = true; p.Above = a.Value<string>("above") ?? ""; }
                if (a["below"] != null) { p.HasBelow = true; p.Below = a.Value<string>("below") ?? ""; }
                if (a["value_override"] != null) { p.HasValueOverride = true; p.ValueOverride = a.Value<string>("value_override") ?? ""; }
                // Item 2.3: a dimension whose text is not its measurement is refused unless
                // the caller says so explicitly, and one drawn outside the active crop would
                // be clipped - both refused HERE, before anything is rehearsed.
                p.AllowTextOverride = a.Value<bool?>("allow_text_override") ?? false;
                p.AllowOutsideCrop = a.Value<bool?>("allow_outside_crop") ?? false;
                string overrideError = DimensionGeometryRules.ValueOverrideError(p.HasValueOverride, p.AllowTextOverride);
                if (overrideError != null) throw new ArgumentException(overrideError);
                if (!p.AllowOutsideCrop)
                {
                    JObject cropDetail;
                    if (DimensionGeometryProbe.InsideCrop(p.View, new[] { x, y }, out cropDetail) == false)
                        throw new ArgumentException(DimensionGeometryRules.OutsideCropError(p.Index));
                }
                if (a["eq"] != null) p.Eq = a.Value<bool>("eq");
                if (a["lock"] != null) p.Lock = a.Value<bool>("lock");
            }
            else if (op == DimensionPlanRules.OpAngular || op == DimensionPlanRules.OpArcLength)
            {
                XYZ center = Point(a["arc_center"], scale, true);
                double radius = (a.Value<double?>("arc_radius") ?? 0) * scale;
                if (radius <= 0) throw new ArgumentException("arc_radius must be greater than zero.");
                p.ArcCenter = center; p.ArcRadius = radius;
                XYZ xAxis = p.View.RightDirection, yAxis = p.View.UpDirection;
                if (op == DimensionPlanRules.OpAngular)
                {
                    // The dimension arc sweeps from the view's X axis through the angle
                    // BETWEEN the two references, measured from their own directions
                    // projected into the view plane. Two parallel references have no
                    // angle to dimension and refuse here rather than in the transaction.
                    XYZ d1 = DirectionOf(doc, p.RefList[0], p.View, 0);
                    XYZ d2 = DirectionOf(doc, p.RefList[1], p.View, 1);
                    double angle = d1.AngleTo(d2);
                    if (angle < 1e-6 || Math.Abs(Math.PI - angle) < 1e-6)
                        throw new ArgumentException("the two references are parallel in this view; an angular " +
                                                    "dimension needs two non-parallel directions.");
                    p.Arc = Arc.Create(center, radius, 0, angle, xAxis, yAxis);
                }
                else
                {
                    // The endpoints of an arc carry no direction to measure an angle
                    // from, so the dimension arc is drawn as a half sweep in the view
                    // plane at the caller's centre and radius; the rehearsal is what
                    // proves Revit accepts it against the referenced arc.
                    p.Arc = Arc.Create(center, radius, 0, Math.PI, xAxis, yAxis);
                }
            }
            else if (op == DimensionPlanRules.OpSpotElevation || op == DimensionPlanRules.OpSpotCoordinate)
            {
                p.SpotOrigin = Point(a["point"], scale, true);
                XYZ right = p.View.RightDirection, up = p.View.UpDirection;
                p.SpotBend = a["bend"] != null
                    ? Point(a["bend"], scale, true)
                    : p.SpotOrigin.Add(right.Multiply(2.0)).Add(up.Multiply(2.0));
                p.SpotEnd = a["end"] != null
                    ? Point(a["end"], scale, true)
                    : p.SpotBend.Add(right.Multiply(2.0));
                p.SpotLeader = a.Value<bool?>("leader") ?? false;
                if (op == DimensionPlanRules.OpSpotElevation) SpotElevationPrecheck(doc, p);
            }

            // ---- expected_value: the caller's postcondition on the measured value ----
            if (a["expected_tolerance"] != null && a["expected_value"] == null)
                throw new ArgumentException("expected_tolerance without expected_value tolerates nothing; " +
                                            "pass both or neither.");
            if (a["expected_value"] != null)
            {
                double raw = a.Value<double>("expected_value");
                if (op == DimensionPlanRules.OpAngular)
                {
                    // Millimetres mean nothing to an angle: angular expectations arrive
                    // in DEGREES and compare in radians.
                    p.ExpectedValueFeet = DimensionPlanRules.DegreesToRadians(raw);
                    double? tol = a.Value<double?>("expected_tolerance");
                    p.ExpectedToleranceFeet = tol.HasValue
                        ? DimensionPlanRules.DegreesToRadians(tol.Value)
                        : DimensionPlanRules.DefaultAngularToleranceRadians;
                }
                else
                {
                    p.ExpectedValueFeet = raw * scale;
                    p.ExpectedToleranceFeet = DimensionPlanRules.ExpectedToleranceFeet(
                        a.Value<double?>("expected_tolerance"), scale);
                }
            }

            // ---- the EFFECTIVE dimension type, explicit or the default materialised ----
            p.EffectiveType = EffectiveDimensionType(doc, a, op, out p.TypeFromDefault);
            p.Type = p.EffectiveType;
        }

        /// <summary>
        /// Parse one stable reference, resolve where it lives, reserialize it, and
        /// fingerprint the geometry behind it. Returns the parsed reference and records
        /// every fact on the plan, in order.
        ///
        /// A reference INTO an RVT link is accepted here, and the extra facts it brings
        /// are not decoration: the link instance, the linked document, the linked
        /// element and the instance transform all go into the plan, because a dimension
        /// to a linked face measures from wherever the link happens to stand. Without
        /// those facts a token minted against a link at one position would still be
        /// spent after somebody moved it, and the dimension would be right about the
        /// face and wrong about the distance.
        /// </summary>
        private static Reference AddReference(Document doc, Plan p, string stable, string field)
        {
            Reference r;
            try { r = Reference.ParseFromStableRepresentation(doc, stable); }
            catch (Exception ex)
            {
                throw new ArgumentException(field + " does not parse as a stable reference in this document: " +
                                            ex.Message);
            }
            if (r == null) throw new ArgumentException(field + " did not resolve to a reference.");

            LinkBinding binding = null;
            Element owner = null;
            Transform toHost = Transform.Identity;
            ElementId linkedId = null;
            try { linkedId = r.LinkedElementId; } catch { linkedId = null; }
            bool isLinked = linkedId != null && linkedId != ElementId.InvalidElementId;

            RevitLinkInstance linkOwner = null;
            try { linkOwner = doc.GetElement(r.ElementId) as RevitLinkInstance; } catch { }
            string linkRefusal = LinkedDatumRules.ReferenceRefusal(field, linkOwner != null, isLinked, isLinked);
            if (linkRefusal != null) throw new ArgumentException(linkRefusal);
            if (isLinked)
            {
                binding = ResolveLinkBinding(doc, r, linkedId, field, out owner, out toHost);
                if (owner is DatumPlane || owner is ReferencePlane)
                {
                    throw new LinkedDatumDimensionException(field, binding);
                }
#if REVIT2023
                IncompatibilityReason revit2023Limit = DimensionReferenceRules.LinkedGeometryRejectedByRevit2023();
                throw new ArgumentException(field + ": " + revit2023Limit.Code + ": " + revit2023Limit.Message);
#endif
            }
            else
            {
                try { owner = doc.GetElement(r); } catch { owner = null; }
                if (owner == null)
                    throw new ArgumentException(field + " parses but its element no longer exists in this document.");
            }

            string reserialized;
            try { reserialized = r.ConvertToStableRepresentation(doc); }
            catch (Exception ex)
            {
                throw new ArgumentException(field + " could not be reserialized (" + ex.Message + "), so the plan " +
                                            "cannot bind it. Nothing was written.");
            }
            string refType;
            try { refType = r.ElementReferenceType.ToString(); } catch { refType = null; }
            if (string.IsNullOrEmpty(refType))
                throw new ArgumentException(field + ": reference type could not be read. Nothing was written.");

            int order = p.RefList.Count;
            string fingerprint = ReferenceGeometryFingerprint(doc, p.View, r, owner, toHost, order, refType, field);

            p.RefList.Add(r);
            p.RefRequested.Add(stable);
            p.RefReserialized.Add(reserialized);
            p.RefOwnerUids.Add(SafeUid(owner));
            p.RefTypes.Add(refType);
            p.RefFingerprints.Add(fingerprint);
            p.RefLinks.Add(binding);
            return r;
        }

        private sealed class LinkedDatumDimensionException : ArgumentException
        {
            public LinkedDatumDimensionException(string field, LinkBinding binding)
                : base(field + " (link instance " + binding.LinkInstanceId + ", linked element " +
                       binding.LinkedElementId + "): " + DimensionReferenceRules.LinkedDatumRejected().Message) { }
        }

        /// <summary>
        /// Everything a linked reference is, resolved from the host document. Each
        /// failure below refuses the whole batch by name rather than degrading the
        /// reference to a host one - a linked reference the bridge cannot fully
        /// describe is a reference it cannot promise stale detection for.
        /// </summary>
        private static LinkBinding ResolveLinkBinding(Document doc, Reference r, ElementId linkedId, string field,
                                                      out Element linkedElement, out Transform toHost)
        {
            linkedElement = null;
            toHost = Transform.Identity;

            long instanceId = 0;
            RevitLinkInstance link = null;
            try
            {
                instanceId = Rid.Value(r.ElementId);
                link = doc.GetElement(r.ElementId) as RevitLinkInstance;
            }
            catch { link = null; }
            if (link == null)
                throw new ArgumentException(field + " is a LINKED reference whose host-side owner (" + instanceId +
                                            ") is not a RevitLinkInstance in this document. " +
                                            LinkedReferenceRules.NotALinkInstance(instanceId) +
                                            " Nothing was written.");

            string linkName = null; try { linkName = link.Name; } catch { }
            string status = "Unknown";
            try
            {
                var type = doc.GetElement(link.GetTypeId()) as RevitLinkType;
                if (type != null) status = type.GetLinkedFileStatus().ToString();
            }
            catch { status = "Unknown"; }
            if (!string.Equals(status, "Loaded", StringComparison.Ordinal))
                throw new ArgumentException(field + ": " +
                                            LinkedReferenceRules.LinkUnloaded(instanceId, linkName, status) +
                                            " Nothing was written.");

            Document linkedDoc = null;
            try { linkedDoc = link.GetLinkDocument(); } catch { linkedDoc = null; }
            if (linkedDoc == null)
                throw new ArgumentException(field + ": " +
                                            LinkedReferenceRules.LinkDocumentUnavailable(instanceId, linkName) +
                                            " Nothing was written.");

            long linkedElementId = Rid.Value(linkedId);
            try { linkedElement = linkedDoc.GetElement(linkedId); } catch { linkedElement = null; }
            if (linkedElement == null)
                throw new ArgumentException(field + ": " +
                                            LinkedReferenceRules.LinkedElementMissing(instanceId, linkedElementId,
                                                                                      linkedDoc.Title) +
                                            " Nothing was written.");
            LinkTransformFacts facts;
            string fingerprint;
            try
            {
                toHost = link.GetTotalTransform();
                if (toHost == null) throw new InvalidOperationException("GetTotalTransform returned null");
                facts = new LinkTransformFacts
                {
                    Origin = new[] { toHost.Origin.X, toHost.Origin.Y, toHost.Origin.Z },
                    BasisX = new[] { toHost.BasisX.X, toHost.BasisX.Y, toHost.BasisX.Z },
                    BasisY = new[] { toHost.BasisY.X, toHost.BasisY.Y, toHost.BasisY.Z },
                    BasisZ = new[] { toHost.BasisZ.X, toHost.BasisZ.Y, toHost.BasisZ.Z },
                    Determinant = toHost.Determinant,
                    IsIdentity = toHost.IsIdentity,
                    HasRotation = !toHost.BasisX.IsAlmostEqualTo(XYZ.BasisX) ||
                                  !toHost.BasisY.IsAlmostEqualTo(XYZ.BasisY) ||
                                  !toHost.BasisZ.IsAlmostEqualTo(XYZ.BasisZ)
                };
                fingerprint = LinkedReferenceRules.TransformFingerprint(facts);
            }
            catch (Exception ex)
            {
                throw new ArgumentException(field + ": link instance " + instanceId + " did not report a readable " +
                                            "total transform (" + ex.Message + "), so a dimension to it could not " +
                                            "be bound to WHERE the link stands. Nothing was written.");
            }

            string path = null; try { path = linkedDoc.PathName; } catch { }
            return new LinkBinding
            {
                LinkInstanceId = instanceId,
                InstanceUniqueId = SafeUid(link),
                LinkTypeId = SafeId(link),
                LinkName = linkName,
                DocumentTitle = linkedDoc.Title,
                DocumentIdentity = LinkedReferenceRules.DocumentIdentity(linkedDoc.Title, path),
                LinkedElementId = linkedElementId,
                LinkedElementUniqueId = SafeUid(linkedElement),
                LinkedElementCategory = SafeCategoryName(linkedElement),
                LinkedElementClass = linkedElement.GetType().Name,
                TransformFingerprint = fingerprint,
                Transform = facts
            };
        }

        private static long SafeId(Element element)
        { try { return Rid.Value(element.GetTypeId()); } catch { return 0; } }

        /// <summary>
        /// The provenance of one reference, or null when it is host geometry. Every id
        /// is labelled by the document it belongs to: the link instance and the link
        /// type are HOST ids, the linked element id is not, and they are all integers.
        /// </summary>
        private static JToken LinkJson(LinkBinding b)
        {
            if (b == null) return JValue.CreateNull();
            var o = new JObject
            {
                ["link_instance_id"] = b.LinkInstanceId,
                ["link_instance_unique_id"] = b.InstanceUniqueId,
                ["link_type_id"] = b.LinkTypeId,
                ["link_name"] = b.LinkName,
                ["linked_document_title"] = b.DocumentTitle,
                ["linked_document_identity"] = b.DocumentIdentity,
                ["linked_element_id"] = b.LinkedElementId,
                ["linked_element_unique_id"] = b.LinkedElementUniqueId,
                ["linked_element_category"] = b.LinkedElementCategory,
                ["linked_element_class"] = b.LinkedElementClass,
                ["transform_fingerprint"] = b.TransformFingerprint
            };
            if (b.Transform != null)
                o["transform"] = new JObject
                {
                    ["origin_feet"] = new JArray(b.Transform.Origin[0], b.Transform.Origin[1],
                                                 b.Transform.Origin[2]),
                    ["basis_x"] = new JArray(b.Transform.BasisX[0], b.Transform.BasisX[1], b.Transform.BasisX[2]),
                    ["basis_y"] = new JArray(b.Transform.BasisY[0], b.Transform.BasisY[1], b.Transform.BasisY[2]),
                    ["basis_z"] = new JArray(b.Transform.BasisZ[0], b.Transform.BasisZ[1], b.Transform.BasisZ[2]),
                    ["determinant"] = b.Transform.Determinant,
                    ["handedness"] = b.Transform.Handedness,
                    ["identity"] = b.Transform.IsIdentity,
                    ["has_rotation"] = b.Transform.HasRotation,
                    ["has_reflection"] = b.Transform.HasReflection
                };
            return o;
        }

        private static string SafeCategoryName(Element element)
        { try { return element.Category == null ? null : element.Category.Name; } catch { return null; } }

        /// <summary>
        /// The geometric FACTS behind a reference, fingerprinted at 0.1 mm: a planar
        /// face by its origin and normal, a curve by its endpoints (a full circle by
        /// centre and radius), an endpoint by its coordinate, a datum by its curve in
        /// this view. A reference whose geometry cannot be read refuses: a plan that
        /// cannot bind the geometry cannot promise stale detection, and promising it
        /// anyway is the lie this whole file exists to prevent.
        /// </summary>
        private static string ReferenceGeometryFingerprint(Document doc, View view, Reference r, Element owner,
                                                           Transform toHost, int order, string refType, string field)
        {
            var facts = new List<double>();
            string kind = null;
            if (toHost == null) toHost = Transform.Identity;

            // A linked reference resolves its geometry through the element in the
            // LINKED document; the host-side owner of such a reference is the link
            // instance, whose own geometry is not what anybody is dimensioning to. The
            // inner form is tried first and the outer one second, because different
            // reference kinds accept different forms and neither is universal.
            GeometryObject g = null;
            try { g = owner.GetGeometryObjectFromReference(r); } catch { g = null; }
            if (g == null)
            {
                Reference inner = null;
                try { inner = r.CreateReferenceInLink(); } catch { inner = null; }
                if (inner != null)
                    try { g = owner.GetGeometryObjectFromReference(inner); } catch { g = null; }
            }

            // EVERY fact below travels through toHost before it is hashed. For a host
            // reference that is the identity and nothing changes; for a linked one it
            // is what turns the linked model's own coordinates into the coordinates
            // the dimension will be measured in. Hashing the raw linked numbers would
            // produce a fingerprint that survives moving the link, which is precisely
            // the drift the plan exists to catch.
            if (g is PlanarFace pf)
            {
                kind = "face";
                PushPoint(facts, toHost, pf.Origin); PushVector(facts, toHost, pf.FaceNormal);
            }
            else if (g is Face face)
            {
                // A non-planar face still has a measurable anchor: its midpoint and the
                // normal there.
                try
                {
                    BoundingBoxUV bb = face.GetBoundingBox();
                    var mid = new UV((bb.Min.U + bb.Max.U) / 2, (bb.Min.V + bb.Max.V) / 2);
                    kind = "face";
                    PushPoint(facts, toHost, face.Evaluate(mid));
                    PushVector(facts, toHost, face.ComputeNormal(mid));
                }
                catch { kind = null; }
            }
            else if (g is Edge edge)
            {
                Curve c = null; try { c = edge.AsCurve(); } catch { c = null; }
                kind = CurveFacts(c, toHost, facts) ? "curve" : null;
            }
            else if (g is Curve curve)
            {
                kind = CurveFacts(curve, toHost, facts) ? "curve" : null;
            }
            else if (g is Autodesk.Revit.DB.Point point)
            {
                kind = "point"; PushPoint(facts, toHost, point.Coord);
            }
            else if (g == null && owner is DatumPlane datum)
            {
                try
                {
                    // GetCurvesInView needs a view in the DATUM'S OWN document. A linked
                    // grid has none here, so it falls through to the datum's own curve
                    // below rather than being read against a host view it does not
                    // belong to.
                    if (toHost.IsIdentity)
                    {
                        IList<Curve> curves = datum.GetCurvesInView(DatumExtentType.Model, view);
                        if (curves != null && curves.Count > 0 && CurveFacts(curves[0], toHost, facts))
                            kind = "datum";
                    }
                }
                catch { kind = null; }
                if (kind == null && owner is Grid linkedGrid)
                {
                    try { kind = CurveFacts(linkedGrid.Curve, toHost, facts) ? "datum" : null; }
                    catch { kind = null; facts.Clear(); }
                }
                if (kind == null && owner is Level linkedLevel)
                {
                    try
                    {
                        kind = "datum";
                        PushPoint(facts, toHost, new XYZ(0, 0, linkedLevel.ProjectElevation));
                        PushVector(facts, toHost, XYZ.BasisZ);
                    }
                    catch { kind = null; facts.Clear(); }
                }
                if (kind == null && owner is ReferencePlane rp)
                {
                    try
                    {
                        kind = "reference_plane";
                        PushPoint(facts, toHost, rp.BubbleEnd);
                        PushPoint(facts, toHost, rp.FreeEnd);
                        PushVector(facts, toHost, rp.Normal);
                    }
                    catch { kind = null; facts.Clear(); }
                }
            }
            else if (g == null)
            {
                LocationCurve lc = null; LocationPoint lp = null;
                try { lc = owner.Location as LocationCurve; lp = owner.Location as LocationPoint; } catch { }
                if (lc != null && CurveFacts(lc.Curve, toHost, facts)) kind = "curve";
                else if (lp != null)
                { kind = "point"; try { PushPoint(facts, toHost, lp.Point); } catch { kind = null; } }
            }

            if (kind == null || facts.Count == 0)
                throw new ArgumentException(field + ": the geometry behind this reference could not be read " +
                                            "(element " + Rid.Value(owner.Id) + ", " + owner.GetType().Name + ", " +
                                            refType + "), so the plan cannot fingerprint it and stale detection " +
                                            "could not be honest. Nothing was written.");
            return DimensionPlanRules.GeometryFingerprint(order, refType, kind, facts);
        }

        /// <summary>
        /// Endpoints for a bound curve; centre+radius for a full arc/circle. The radius
        /// is NOT transformed: a Revit link transform is rigid, so it moves and turns a
        /// circle without resizing it, and putting a transformed length in beside an
        /// untransformed one would only make the hash harder to reason about.
        /// </summary>
        private static bool CurveFacts(Curve c, Transform toHost, List<double> facts)
        {
            if (c == null) return false;
            try
            {
                if (c.IsBound)
                {
                    PushPoint(facts, toHost, c.GetEndPoint(0));
                    PushPoint(facts, toHost, c.GetEndPoint(1));
                    return true;
                }
                var arc = c as Arc;
                if (arc != null) { PushPoint(facts, toHost, arc.Center); facts.Add(arc.Radius); return true; }
            }
            catch { return false; }
            return false;
        }

        private static void Push(List<double> facts, XYZ v) { facts.Add(v.X); facts.Add(v.Y); facts.Add(v.Z); }

        /// <summary>A POINT, in host coordinates: the placement moves it.</summary>
        private static void PushPoint(List<double> facts, Transform toHost, XYZ v)
            => Push(facts, toHost == null ? v : toHost.OfPoint(v));

        /// <summary>A DIRECTION, in host coordinates: the placement turns it but never translates it.</summary>
        private static void PushVector(List<double> facts, Transform toHost, XYZ v)
            => Push(facts, toHost == null ? v : toHost.OfVector(v));

        /// <summary>
        /// The direction a reference contributes to an angular dimension, projected
        /// into the view plane. Grids, reference planes and line-based elements carry
        /// one; anything else refuses with the reason.
        /// </summary>
        private static XYZ DirectionOf(Document doc, Reference r, View view, int index)
        {
            Element el = null; try { el = doc.GetElement(r); } catch { el = null; }
            XYZ d = null;
            var grid = el as Grid;
            if (grid != null) { var gl = grid.Curve as Line; if (gl != null) d = gl.Direction; }
            if (d == null) { var rp = el as ReferencePlane; if (rp != null) d = rp.Direction; }
            if (d == null && el != null)
            {
                GeometryObject g = null;
                try { g = el.GetGeometryObjectFromReference(r); } catch { g = null; }
                var edge = g as Edge;
                if (edge != null) { var l = edge.AsCurve() as Line; if (l != null) d = l.Direction; }
                if (d == null) { var l2 = g as Line; if (l2 != null) d = l2.Direction; }
                if (d == null)
                {
                    var lc = el.Location as LocationCurve;
                    if (lc != null) { var l3 = lc.Curve as Line; if (l3 != null) d = l3.Direction; }
                }
            }
            if (d == null)
                throw new ArgumentException("references[" + index + "] carries no readable straight direction " +
                                            "(element " + (el == null ? "?" : Rid.Value(el.Id).ToString(CultureInfo.InvariantCulture)) +
                                            ", " + (el == null ? "?" : el.GetType().Name) + "). An angular dimension " +
                                            "needs direction-bearing references: walls, grids, reference planes or " +
                                            "line-based elements.");
            XYZ n = view.ViewDirection;
            XYZ projected = d.Subtract(n.Multiply(d.DotProduct(n)));
            if (projected.GetLength() < 1e-9)
                throw new ArgumentException("references[" + index + "] is perpendicular to this view's plane; its " +
                                            "direction has no angular meaning here.");
            return projected.Normalize();
        }

        /// <summary>
        /// The type the created dimension will actually carry: the explicit
        /// dimension_type_id where the operation takes one, otherwise the document's
        /// default for the operation's ElementTypeGroup, resolved and validated NOW so
        /// the plan can bind it - a default swapped between rehearsal and apply must
        /// read as a different plan. An irresoluble default is a refusal, not a shrug.
        /// </summary>
        private static DimensionType EffectiveDimensionType(Document doc, JObject a, string op, out bool fromDefault)
        {
            fromDefault = false;
            DimensionStyleType expected = ExpectedStyle(op);
            if (a["dimension_type_id"] != null && DimensionPlanRules.AllowsOption(op, "dimension_type_id"))
            {
                DimensionType explicitType = Need<DimensionType>(doc, a, "dimension_type_id");
                CheckStyle(explicitType, op, expected, "dimension_type_id");
                return explicitType;
            }
            fromDefault = true;
            ElementTypeGroup group = GroupOf(op);
            ElementId id = null;
            try { id = doc.GetDefaultElementTypeId(group); } catch { id = null; }
            DimensionType byDefault = (id == null || id == ElementId.InvalidElementId)
                ? null : doc.GetElement(id) as DimensionType;
            // The document's default TABLE can be wrong, and a real fixture proved it:
            // GetDefaultElementTypeId(ArcLengthDimensionType) answered a LINEAR type
            // ('Linear - 3/32" Arial') on a stock-derived model. A wrong default is not
            // the absence of capability - when any type of the RIGHT style exists in the
            // document, the lowest-id one is used deterministically, and the plan binds
            // THAT identity so a swap before the apply still reads as stale. Only a
            // document with no type of the style at all is a refusal.
            bool defaultUsable = byDefault != null && StyleMatches(byDefault, expected);
            if (!defaultUsable)
            {
                DimensionType fallback = null;
                try
                {
                    fallback = new FilteredElementCollector(doc)
                        .OfClass(typeof(DimensionType)).Cast<DimensionType>()
                        .Where(t => StyleMatches(t, expected))
                        .OrderBy(t => Rid.Value(t.Id))
                        .FirstOrDefault();
                }
                catch { fallback = null; }
                if (fallback != null) return fallback;
                throw new ArgumentException("this document has no DimensionType of style " + expected + " at all (" +
                                            "the default " + group + " resolved to " +
                                            (byDefault == null ? "nothing usable" : "'" + SafeName(byDefault) + "' of the wrong style") +
                                            ", and no type of the right style exists to fall back on), so '" + op +
                                            "' has nothing to create with. " +
                                            (DimensionPlanRules.AllowsOption(op, "dimension_type_id")
                                                ? "Pass dimension_type_id explicitly, or create a type of that style in Revit."
                                                : "Create a type of that style in Revit first."));
            }
            return byDefault;
        }

        private static bool StyleMatches(DimensionType t, DimensionStyleType expected)
        {
            try { return t.StyleType == expected; } catch { return false; }
        }

        private static void CheckStyle(DimensionType t, string op, DimensionStyleType expected, string source)
        {
            DimensionStyleType actual;
            try { actual = t.StyleType; }
            catch (Exception ex)
            {
                throw new ArgumentException(source + " resolved to '" + SafeName(t) + "' but its StyleType could " +
                                            "not be read: " + ex.Message);
            }
            if (actual != expected)
                throw new ArgumentException(source + " resolved to '" + SafeName(t) + "' whose StyleType is " +
                                            actual + ", but '" + op + "' needs " + expected + ". Pick a type of " +
                                            "the matching style.");
        }

        private static ElementTypeGroup GroupOf(string op)
        {
            switch (op)
            {
                case DimensionPlanRules.OpAngular: return ElementTypeGroup.AngularDimensionType;
                case DimensionPlanRules.OpRadial: return ElementTypeGroup.RadialDimensionType;
                case DimensionPlanRules.OpDiameter: return ElementTypeGroup.DiameterDimensionType;
                case DimensionPlanRules.OpArcLength: return ElementTypeGroup.ArcLengthDimensionType;
                case DimensionPlanRules.OpSpotElevation: return ElementTypeGroup.SpotElevationType;
                case DimensionPlanRules.OpSpotCoordinate: return ElementTypeGroup.SpotCoordinateType;
                default: return ElementTypeGroup.LinearDimensionType;
            }
        }

        private static DimensionStyleType ExpectedStyle(string op)
        {
            switch (op)
            {
                case DimensionPlanRules.OpAngular: return DimensionStyleType.Angular;
                case DimensionPlanRules.OpRadial: return DimensionStyleType.Radial;
                case DimensionPlanRules.OpDiameter: return DimensionStyleType.Diameter;
                case DimensionPlanRules.OpArcLength: return DimensionStyleType.ArcLength;
                case DimensionPlanRules.OpSpotElevation: return DimensionStyleType.SpotElevation;
                case DimensionPlanRules.OpSpotCoordinate: return DimensionStyleType.SpotCoordinate;
                default: return DimensionStyleType.Linear;
            }
        }

        // ---------------------------------------------------------------------
        // Creation.
        // ---------------------------------------------------------------------
        private static bool HasTextOptions(JObject a) => a["expected_height_mm"] != null ||
            a["horizontal_alignment"] != null || a["vertical_alignment"] != null || a["rotation_degrees"] != null;

        private static HorizontalTextAlignment ParseHorizontal(string value)
        {
            switch ((value ?? "").ToLowerInvariant())
            {
                case "left": return HorizontalTextAlignment.Left;
                case "center": return HorizontalTextAlignment.Center;
                case "right": return HorizontalTextAlignment.Right;
                default: throw new ArgumentException("horizontal_alignment must be left, center or right.");
            }
        }

        private static VerticalTextAlignment ParseVertical(string value)
        {
            switch ((value ?? "").ToLowerInvariant())
            {
                case "top": return VerticalTextAlignment.Top;
                case "middle": return VerticalTextAlignment.Middle;
                case "bottom": return VerticalTextAlignment.Bottom;
                default: throw new ArgumentException("vertical_alignment must be top, middle or bottom.");
            }
        }

        private static Element Create(Document doc, Plan p)
        {
            if (p.Operation == "text")
            {
                if (p.TextPlan != null && p.TextPlan.Any) return CreateText(doc, p);
                if (!HasTextOptions(p.Input)) return TextNote.Create(doc, p.View.Id, p.Point, p.Text, p.Type.Id);
                var options = new TextNoteOptions(p.Type.Id);
                if (p.Input["horizontal_alignment"] != null)
                    options.HorizontalAlignment = ParseHorizontal(p.Input.Value<string>("horizontal_alignment"));
                if (p.Input["vertical_alignment"] != null)
                    options.VerticalAlignment = ParseVertical(p.Input.Value<string>("vertical_alignment"));
                if (p.Input["rotation_degrees"] != null)
                    options.Rotation = p.Input.Value<double>("rotation_degrees") * Math.PI / 180.0;
                return TextNote.Create(doc, p.View.Id, p.Point, p.Text, options);
            }
            if (p.Operation == "tag")
            {
                Room room = TaggedRoom(p);
                if (room != null)
                {
                    FamilySymbol roomType = p.Type as FamilySymbol;
                    if (p.RoomTagFamilyPath != null)
                    {
                        string familyName = Path.GetFileNameWithoutExtension(p.RoomTagFamilyPath);
                        Family loaded;
                        if (!doc.LoadFamily(p.RoomTagFamilyPath, out loaded) || loaded == null)
                            loaded = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                                .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.Ordinal));
                        if (loaded == null || loaded.FamilyCategory?.Id != Category.GetCategory(doc, BuiltInCategory.OST_RoomTags)?.Id)
                            throw new InvalidOperationException("tag_family_path did not load a RoomTag family: " + p.RoomTagFamilyPath);
                        if (roomType != null && roomType.Family.Id != loaded.Id)
                            throw new InvalidOperationException("tag_type_id does not belong to the RoomTag family at tag_family_path.");
                        var symbols = loaded.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol)
                            .Where(s => s != null).ToList();
                        if (roomType == null)
                        {
                            string wanted = p.Input.Value<string>("tag_type_name");
                            var matches = string.IsNullOrWhiteSpace(wanted) ? symbols : symbols.Where(s => s.Name == wanted).ToList();
                            if (matches.Count != 1)
                                throw new InvalidOperationException("RoomTag RFA must have exactly one selectable type, or provide tag_type_name. Found " +
                                    matches.Count + " matching types in " + familyName + ".");
                            roomType = matches[0];
                        }
                    }
                    if (roomType == null)
                    {
                        // Field (2025): several room tag types loaded refused every room. The document's own
                        // default for OST_RoomTags is the type Revit itself would use, so it is taken; only
                        // when there is none among the loaded types is the call refused, listing them.
                        string ambiguous;
                        roomType = DefaultTagType(doc, BuiltInCategory.OST_RoomTags, "RoomTag", out ambiguous) as FamilySymbol;
                        if (roomType == null)
                            throw new InvalidOperationException(ambiguous ??
                                "No RoomTag type is loaded; load one (annotate load_tag_family) or pass tag_type_id.");
                        p.TypeDefaulted = true;
                    }
                    // A room tag is born INSIDE its room; its head may sit outside only when it has a leader
                    // (measured: asking for an outside head without one is Revit's "Head position outside" failure).
                    // So with add_leader the tag is created at a point inside the room, the leader is switched on,
                    // and only then is the head moved to the requested point.
                    bool roomLeader = p.Input.Value<bool?>("add_leader") == true;
                    UV birth = new UV(p.Point.X, p.Point.Y);
                    // A room of a LINK lives in the link's coordinates: the inside test runs on the
                    // requested point carried into the link, and the fallback birth point (the room's
                    // location) is carried back to the host.
                    Transform toHost = p.LinkInstance != null ? p.LinkInstance.GetTotalTransform() : Transform.Identity;
                    if (roomLeader)
                    {
                        bool inside = RoomContainsHostPoint(room, p.LinkInstance, p.Point) == true;
                        if (!inside && room.Location is LocationPoint roomAt)
                        {
                            XYZ hostAt = toHost.OfPoint(roomAt.Point);
                            birth = new UV(hostAt.X, hostAt.Y);
                        }
                    }
                    LinkElementId roomRef = p.LinkInstance != null ? new LinkElementId(p.LinkInstance.Id, room.Id) : new LinkElementId(room.Id);
                    RoomTag created = doc.Create.NewRoomTag(roomRef, birth, p.View.Id);
                    if (created == null) throw new InvalidOperationException("Revit returned no RoomTag.");
                    if (created.GetTypeId() != roomType.Id) created.ChangeTypeId(roomType.Id);
                    if (roomLeader) created.HasLeader = true;
                    p.TagPoint = new XYZ(p.Point.X, p.Point.Y, created.TagHeadPosition.Z);
                    created.TagHeadPosition = p.TagPoint;
                    ApplySpatialLeader(created, p);
                    p.RoomTagTypeName = roomType.Name;
                    p.RoomTagFamilyName = roomType.Family.Name;
                    return created;
                }
                if (SpatialKind(p.Target) != null) return CreateSpatialTag(doc, p);
                string m = p.Mode ?? TagRules.ByCategory;
                TagMode mode = TagMode.TM_ADDBY_CATEGORY;
                if (m == TagRules.MultiCategory) mode = TagMode.TM_ADDBY_MULTICATEGORY;
                else if (TagRules.IsMaterialMode(m)) mode = TagMode.TM_ADDBY_MATERIAL;
                // Every keynote mode is created straight with its keynote type (the tag-type overload) on the
                // element's reference. A material keynote is NOT a material tag (measured, 2025.4: a keynote type
                // is "not valid for the tag Revit created" by TM_ADDBY_MATERIAL): it is a keynote tag placed on a
                // FACE of the material (Key Source is read-only on the instance), verified against that material's keynote.
                bool keynoteByType = TagRules.IsKeynote(m);
                TagOrientation orientation = (p.Input.Value<string>("orientation") ?? "horizontal").ToLowerInvariant() == "vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal;
                Reference tagReference = p.LinkedElement != null
                    ? new Reference(p.LinkedElement).CreateLinkReference(p.LinkInstance)
                    : (m == TagRules.KeynoteMaterial && p.MaterialFaceRef != null ? p.MaterialFaceRef : new Reference(p.Target));
                IndependentTag tag = null;
                if (p.LinkedElement != null)
                {
                    // A tag on a LINKED element: Revit rejects the legacy TagMode overload with a bare
                    // InternalException (measured live, Revit 2023). The tag type goes in the call, the
                    // reference is the link reference, and the head is a point of the HOST view
                    // (p.Point is host coordinates; the link's own placement is already in the reference).
                    ElementId linkedTypeId = p.Type != null ? p.Type.Id : DefaultLinkedTagTypeId(doc, p.LinkedElement);
                    if (linkedTypeId == null || linkedTypeId == ElementId.InvalidElementId)
                        throw new InvalidOperationException("No tag type is loaded in the host for the category '" +
                            (p.LinkedElement.Category?.Name ?? "?") + "' of linked element " + Rid.Value(p.LinkedElement.Id) +
                            ". Load a tag family for it in the host (load_tag_family) or pass tag_type_id.");
                    tag = IndependentTag.Create(doc, linkedTypeId, p.View.Id, tagReference,
                        p.Input.Value<bool?>("add_leader") == true, orientation, p.Point);
                }
                else if (m == TagRules.KeynoteMaterial && p.MaterialFaces != null && p.MaterialFaces.Count > 0)
                {
                    // Revit tags only some faces of an element in a given view ("The reference can not be tagged" for the
                    // rest), and which ones is not exposed: the candidates are tried in order and the first accepted wins.
                    // A refused Create changes nothing, so trying the next face is safe; the face actually used names the
                    // material and keynote the verification then demands.
                    string lastRefusal = null;
                    foreach (var c in p.MaterialFaces)
                    {
                        p.Budget?.Check();
                        try
                        {
                            tag = IndependentTag.Create(doc, p.Type.Id, p.View.Id, c.Item1,
                                p.Input.Value<bool?>("add_leader") == true, orientation, p.Point);
                        }
                        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ArgumentException || ex is System.ArgumentException) { lastRefusal = ex.Message; tag = null; }
                        if (tag != null)
                        {
                            // The face Revit accepted names the material: its keynote AND that key's table text are what the
                            // verification then demands (not the first candidate's).
                            p.MaterialFaceRef = c.Item1; p.MaterialId = c.Item2; p.MaterialKeynote = c.Item3;
                            string tableText = null;
                            p.KeynoteText = p.MaterialKeynoteTable != null && p.MaterialKeynoteTable.TryGetValue(c.Item3, out tableText) ? tableText : null;
                            break;
                        }
                    }
                    if (tag == null)
                        throw new InvalidOperationException("tag_mode=keynote_material: Revit accepted none of the " + p.MaterialFaces.Count +
                            " faces of element " + Rid.Value(p.Target.Id) + " whose material carries a keynote as a tag reference in view " +
                            Rid.Value(p.View.Id) + " (last: " + (lastRefusal ?? "?").Split('\r', '\n')[0] + "). Tag it in a view where that face is seen, or use keynote_element / keynote_user.");
                }
                else if (keynoteByType)
                    tag = IndependentTag.Create(doc, p.Type.Id, p.View.Id, tagReference,
                        p.Input.Value<bool?>("add_leader") == true, orientation, p.Point);
                else
                    tag = IndependentTag.Create(doc, p.View.Id, tagReference, p.Input.Value<bool?>("add_leader") == true, mode, orientation, p.Point);
                if (p.Type != null && tag.GetTypeId() != p.Type.Id)
                {
                    ICollection<ElementId> valid = tag.GetValidTypes();
                    if (valid == null || !valid.Contains(p.Type.Id))
                        throw new InvalidOperationException("tag_type_id " + Rid.Value(p.Type.Id) +
                            " is not valid for the tag Revit created for element " +
                            (p.LinkedElement != null ? Rid.Value(p.LinkedElement.Id) + " of link instance " + Rid.Value(p.LinkInstance.Id) : Rid.Value(p.Target.Id).ToString(CultureInfo.InvariantCulture)) + "." +
                            " Types valid for it: " + TagRules.CandidatesText(valid == null ? null : valid.Take(12).Select(id =>
                                Rid.Value(id) + " " + ((doc.GetElement(id) as FamilySymbol)?.FamilyName ?? "?") + ": " + (doc.GetElement(id)?.Name ?? "?")).ToList()) + ".");
                    tag.ChangeTypeId(p.Type.Id);
                }
                ApplyTagExtras(doc, p, tag);
                tag.TagHeadPosition = p.Point;
                p.TagPoint = p.Point;
                ApplyTagLeader(p, tag);
                if (p.Input.Value<bool?>("avoid_collisions") == true)
                {
                    p.TagCoverage = new JObject();
                    p.TagPoint = AnnotationLayout.Place(doc,p.View,tag,p.Point,
                        (p.Input.Value<double?>("layout_clearance") ?? 10)*p.Scale,
                        (p.Input.Value<double?>("layout_max_displacement") ?? 1200)*p.Scale,
                        p.LayoutAccepted, p.TagCoverage, p.Budget);
                    p.TagLayoutFallback = p.TagCoverage.Value<bool>("layout_fallback");
                }
                return tag;
            }
            switch (p.Operation)
            {
                case DimensionPlanRules.OpDimension:
                    return doc.Create.NewDimension(p.View, p.Line, p.References, p.EffectiveType);
                case DimensionPlanRules.OpAngular:
                    return AngularDimension.Create(doc, p.View, p.Arc, p.RefList, p.EffectiveType);
#if !REVIT2023 && !REVIT2024
                case DimensionPlanRules.OpRadial:
                    return RadialDimension.Create(doc, p.View, p.ArcReference, false);
                case DimensionPlanRules.OpDiameter:
                    return RadialDimension.Create(doc, p.View, p.ArcReference, true);
                case DimensionPlanRules.OpArcLength:
                    // RefList carries the two endpoint references first and the measured
                    // arc's own reference last (AddReference order); the API wants them
                    // apart.
                    return ArcLengthDimension.Create(doc, p.View, p.Arc, p.ArcReference, p.RefList.GetRange(0, 2));
#endif
                case DimensionPlanRules.OpSpotElevation:
                    return doc.Create.NewSpotElevation(p.View, p.RefList[0], p.SpotOrigin, p.SpotBend, p.SpotEnd,
                                                       p.SpotOrigin, p.SpotLeader);
                case DimensionPlanRules.OpSpotCoordinate:
                    return doc.Create.NewSpotCoordinate(p.View, p.RefList[0], p.SpotOrigin, p.SpotBend, p.SpotEnd,
                                                        p.SpotOrigin, p.SpotLeader);
                default:
                    // Unreachable: PlanAction refused everything else before any
                    // transaction. Kept as a throw so that if it ever stops being
                    // unreachable the failure is loud instead of a null write.
                    throw new InvalidOperationException("operation '" + p.Operation + "' reached Create() without " +
                                                        "a creation path; planning should have refused it.");
            }
        }

        /// <summary>Apply the requested overrides/EQ/lock to a freshly created dimension.</summary>
        private static void Decorate(Dimension d, Plan p)
        {
            if (p.HasPrefix) d.Prefix = p.Prefix;
            if (p.HasSuffix) d.Suffix = p.Suffix;
            if (p.HasAbove) d.Above = p.Above;
            if (p.HasBelow) d.Below = p.Below;
            if (p.HasValueOverride) d.ValueOverride = p.ValueOverride;
            if (p.Eq.HasValue) d.AreSegmentsEqual = p.Eq.Value;
            if (p.Lock.HasValue) d.IsLocked = p.Lock.Value;
        }

        // ---------------------------------------------------------------------
        // Measurement and verification.
        // ---------------------------------------------------------------------

        /// <summary>Every fact the verification compares, read guarded off one dimension.</summary>
        private static Measured Measure(Document doc, Plan p, Element e)
        {
            var m = new Measured();
            if (e == null) { m.Unreadable = "the created element could not be re-read from the document"; return m; }
            m.ClassName = e.GetType().Name;
            // The class check is by HIERARCHY, not by string: Revit 2025 answers a
            // two-grid NewDimension with a LinearDimension, which IS a Dimension -
            // measured live 2026-08-24, where the exact-name comparison refused a
            // perfectly correct dimension for wearing its subclass's name.
            m.ClassMatches = ClassMatches(p.Operation, e);
            m.ElementId = Rid.Value(e.Id);
            try { m.UniqueId = e.UniqueId; } catch (Exception ex) { Note(m, "UniqueId: " + ex.Message); }
            var dim = e as Dimension;
            if (dim == null) { Note(m, "the created element is a " + m.ClassName + ", not a Dimension"); return m; }

            try { m.OwnerViewId = Rid.Value(dim.OwnerViewId); } catch (Exception ex) { Note(m, "OwnerViewId: " + ex.Message); }
            try { m.TypeId = Rid.Value(dim.GetTypeId()); } catch (Exception ex) { Note(m, "GetTypeId: " + ex.Message); }
            try { m.Shape = dim.DimensionShape.ToString(); } catch (Exception ex) { Note(m, "DimensionShape: " + ex.Message); }
            try { m.ReferencesAvailable = dim.AreReferencesAvailable; } catch (Exception ex) { Note(m, "AreReferencesAvailable: " + ex.Message); }
            try
            {
                m.RefReps = new List<string>();
                m.RefTypes = new List<string>();
                ReferenceArray refs = dim.References;
                if (refs == null) Note(m, "References returned null");
                else foreach (Reference r in refs)
                {
                    m.RefReps.Add(r == null ? "<null>" : r.ConvertToStableRepresentation(doc));
                    m.RefTypes.Add(r == null ? "<null>" : r.ElementReferenceType.ToString());
                }
            }
            catch (Exception ex) { Note(m, "References: " + ex.Message); }
            try { m.SegmentCount = dim.NumberOfSegments; } catch (Exception ex) { Note(m, "NumberOfSegments: " + ex.Message); }
            try { m.SingleValue = dim.Value; } catch (Exception ex) { Note(m, "Value: " + ex.Message); }
            try
            {
                m.SegmentValues = new List<double>();
                var presentedSegments = new List<string>();
                foreach (DimensionSegment s in dim.Segments)
                {
                    double? v = s.Value;
                    if (v.HasValue) m.SegmentValues.Add(v.Value);
                    // Not Unreadable: pre-commit Revit legitimately has no segment
                    // values yet. Recorded as a COUNT so the post-commit stage - where
                    // absence really is a defect - can enforce it.
                    else m.SegmentValuesMissing++;
                    try { presentedSegments.Add(s.ValueString ?? ""); } catch { presentedSegments.Add("<unreadable>"); }
                }
                if (presentedSegments.Count > 0) m.Presented = string.Join("; ", presentedSegments);
            }
            catch (Exception ex) { Note(m, "Segments: " + ex.Message); }
            if (m.Presented == null)
            {
                try { m.Presented = dim.ValueString; } catch { m.Presented = null; }
            }

            if (p.Operation == DimensionPlanRules.OpDimension)
            {
                try
                {
                    var line = dim.Curve as Line;
                    if (line == null) Note(m, "Curve is not a Line");
                    else
                    {
                        m.CurveRead = true; m.CurveBound = line.IsBound;
                        if (line.IsBound) { m.CurveStart = Arr(line.GetEndPoint(0)); m.CurveEnd = Arr(line.GetEndPoint(1)); }
                        else { m.CurveOrigin = Arr(line.Origin); m.CurveDirection = Arr(line.Direction); }
                        m.LineDirection = Arr(line.Direction);
                    }
                }
                catch (Exception ex) { Note(m, "Curve: " + ex.Message); }
                ReadGeometryFacts(doc, p, dim, m);
                ReadPatternExtent(doc, dim, m);
            }
            else if (p.Operation == DimensionPlanRules.OpAngular)
            {
                try
                {
                    var arc = dim.Curve as Arc;
                    if (arc == null) Note(m, "Curve is not an Arc");
                    else { m.CurveRead = true; m.ArcCenter = Arr(arc.Center); m.ArcRadius = arc.Radius; }
                }
                catch (Exception ex) { Note(m, "Curve: " + ex.Message); }
            }
            else if (p.Operation == DimensionPlanRules.OpSpotElevation ||
                     p.Operation == DimensionPlanRules.OpSpotCoordinate)
            {
                try { m.SpotOrigin = Arr(dim.Origin); } catch (Exception ex) { Note(m, "Origin: " + ex.Message); }
            }

            if (p.HasPrefix) try { m.Prefix = dim.Prefix; } catch (Exception ex) { Note(m, "Prefix: " + ex.Message); }
            if (p.HasSuffix) try { m.Suffix = dim.Suffix; } catch (Exception ex) { Note(m, "Suffix: " + ex.Message); }
            if (p.HasAbove) try { m.Above = dim.Above; } catch (Exception ex) { Note(m, "Above: " + ex.Message); }
            if (p.HasBelow) try { m.Below = dim.Below; } catch (Exception ex) { Note(m, "Below: " + ex.Message); }
            if (p.HasValueOverride) try { m.ValueOverride = dim.ValueOverride; } catch (Exception ex) { Note(m, "ValueOverride: " + ex.Message); }
            if (p.Eq.HasValue) try { m.Eq = dim.AreSegmentsEqual; } catch (Exception ex) { Note(m, "AreSegmentsEqual: " + ex.Message); }
            if (p.Lock.HasValue) try { m.Locked = dim.IsLocked; } catch (Exception ex) { Note(m, "IsLocked: " + ex.Message); }
            ReadPaperFacts(doc, p, dim, m);
            return m;
        }

        private static void Note(Measured m, string what) { if (m.Unreadable == null) m.Unreadable = what; }

        /// <summary>
        /// Item 2.3 facts, read without poisoning the row: the geometry of every
        /// reference (unmeasurable ones say why), every value override actually on the
        /// dimension and its segments, and whether the dimension sits inside the crop.
        /// A read that fails here becomes "unavailable" in the check, never a pass.
        /// </summary>
        private static void ReadGeometryFacts(Document doc, Plan p, Dimension dim, Measured m)
        {
            try
            {
                var refs = new List<Reference>();
                foreach (Reference r in dim.References) refs.Add(r);
                m.RefGeometries = DimensionGeometryProbe.ReadAll(doc, refs);
            }
            catch (Exception ex) { m.GeometryUnreadable = ex.Message; }
            try
            {
                var found = new List<string>();
                string own = dim.NumberOfSegments > 1 ? null : dim.ValueOverride;
                if (!string.IsNullOrEmpty(own)) found.Add(own);
                foreach (DimensionSegment s in dim.Segments)
                {
                    string so = s.ValueOverride;
                    if (!string.IsNullOrEmpty(so)) found.Add(so);
                }
                m.OverridesFound = found;
            }
            catch (Exception ex) { m.OverridesUnreadable = ex.Message; }
            var points = new List<XYZ>();
            if (p.Line != null) { points.Add(p.Line.GetEndPoint(0)); points.Add(p.Line.GetEndPoint(1)); }
            try
            {
                if (dim.NumberOfSegments > 1) { foreach (DimensionSegment s in dim.Segments) points.Add(s.Origin); }
                else points.Add(dim.Origin);
            }
            catch { /* the requested line endpoints still stand */ }
            JObject detail;
            m.InsideCrop = DimensionGeometryProbe.InsideCrop(p.View, points, out detail);
            m.CropDetail = detail;
        }

        /// <summary>
        /// Every comparison the contract demands, as requested / read / match rows.
        /// `baseline` is what THIS call's rehearsal measured - the deterministic answer
        /// the same model gives twice - so facts Revit normalises on creation (arc
        /// rebasing, reference storage for the API-shaped operations, spot origins) are
        /// held against the rehearsed measurement, while everything the caller stated
        /// (references for linear/angular, the line, overrides, the type, the view) is
        /// held against the REQUEST. In the rehearsal itself baseline == measured, so
        /// the baseline rows pass trivially and the request rows do the work.
        /// </summary>
        /// <summary>
        /// WHEN a fact may be demanded. Measured live on Revit 2025 (2026-08-24): a
        /// dimension inside a still-open transaction reports AreReferencesAvailable
        /// false and Value 0 even after Regenerate - Revit materialises both AT COMMIT.
        /// Demanding them earlier refused every correct dimension; not demanding them
        /// at all would let a broken one through. So the checks are staged: identity
        /// and geometry always; availability, values and the caller's expected_value
        /// only post-commit - where the TransactionGroup is still open, so a failure
        /// still rolls the WHOLE batch back.
        /// </summary>
        private static JObject BuildChecks(Plan p, Measured m, Measured baseline, VerifyStage stage, out bool allOk)
        {
            var checks = new JArray();
            bool ok = true;
            Action<string, JToken, JToken, bool> add = null;
            add = (field, requested, read, match) =>
            {
                checks.Add(new JObject
                {
                    ["field"] = field,
                    ["requested"] = requested ?? JValue.CreateNull(),
                    ["read"] = read ?? JValue.CreateNull(),
                    ["match"] = match
                });
                if (!match) ok = false;
            };
            double tol = DimensionPlanRules.CurveToleranceFeet;
            string op = p.Operation;

            string expectedClass = ExpectedClassName(op);
            add("class", expectedClass + " (or a subclass)", m.ClassName, m.ClassMatches);
            string expectedShape = ExpectedShapeName(op);
            add("shape", expectedShape, m.Shape, string.Equals(m.Shape, expectedShape, StringComparison.Ordinal));

            long viewId = Rid.Value(p.View.Id);
            add("owner_view_id", viewId, m.OwnerViewId.HasValue ? (JToken)m.OwnerViewId.Value : null,
                m.OwnerViewId.HasValue && m.OwnerViewId.Value == viewId);

            long typeId = Rid.Value(p.EffectiveType.Id);
            add("dimension_type_id", typeId, m.TypeId.HasValue ? (JToken)m.TypeId.Value : null,
                m.TypeId.HasValue && m.TypeId.Value == typeId);

            if (stage != VerifyStage.PostCommit)
                // Observed, not demanded: Revit materialises reference availability at
                // commit, so inside an open transaction false is the normal reading of
                // a correct dimension. The value travels so nobody has to trust this
                // sentence; the post-commit stage is where false becomes a failure.
                checks.Add(new JObject
                {
                    ["field"] = "references_available_observed",
                    ["requested"] = "enforced post-commit; Revit materialises this at commit",
                    ["read"] = m.ReferencesAvailable.HasValue ? (JToken)m.ReferencesAvailable.Value : JValue.CreateNull(),
                    ["match"] = true
                });

            // References: the caller-stated operations are held to the caller's list;
            // the API-shaped ones (radial/diameter/arc length/spots, whose stored
            // reference set is the API's business) are held to the rehearsed baseline.
            bool vsRequested = op == DimensionPlanRules.OpDimension || op == DimensionPlanRules.OpAngular;
            List<string> wanted = vsRequested ? p.RefReserialized : baseline?.RefReps;
            int? readCount = m.RefReps == null ? (int?)null : m.RefReps.Count;
            add("references_count",
                wanted == null ? null : (JToken)wanted.Count, readCount.HasValue ? (JToken)readCount.Value : null,
                wanted != null && readCount.HasValue && readCount.Value == wanted.Count);
            // By OWNER, not by exact string, when held against the request: Revit
            // canonicalises a bare element reference on storage - a grid handed in as
            // 'uid' reads back as 'uid:0:SURFACE' (measured live, 2025) - and the same
            // element under two spellings is the same reference. The read-vs-rehearsed
            // ORDER check below stays exact-string: both sides of it went through the
            // same canonicalisation.
            bool setMatch;
            if (vsRequested)
                setMatch = wanted != null && m.RefReps != null &&
                           DimensionPlanRules.ReferenceKeysWithTypes(wanted, p.RefTypes)
                               .SequenceEqual(DimensionPlanRules.ReferenceKeysWithTypes(m.RefReps, m.RefTypes), StringComparer.Ordinal);
            else
                setMatch = wanted != null && m.RefReps != null &&
                           wanted.OrderBy(s => s, StringComparer.Ordinal)
                                 .SequenceEqual(m.RefReps.OrderBy(s => s, StringComparer.Ordinal), StringComparer.Ordinal);
            add(vsRequested ? "references_owners_vs_requested" : "references_set_vs_rehearsed",
                wanted == null ? null : new JArray(wanted), m.RefReps == null ? null : new JArray(m.RefReps), setMatch);
            bool orderMatch = baseline?.RefReps != null && m.RefReps != null &&
                              baseline.RefReps.SequenceEqual(m.RefReps, StringComparer.Ordinal);
            add("references_order_vs_rehearsed",
                baseline?.RefReps == null ? null : new JArray(baseline.RefReps),
                m.RefReps == null ? null : new JArray(m.RefReps), orderMatch);
            // NewDimension ignores some references IN SILENCE (a linked grid, field report
            // on Revit 2024): the dimension exists with fewer references than asked. Named
            // here, by index, so the refusal says which one Revit dropped.
            if (vsRequested && m.RefReps != null)
            {
                List<int> dropped = DimensionLinkRules.Dropped(p.RefReserialized, p.RefTypes, m.RefReps, m.RefTypes);
                m.DroppedMessage = DimensionLinkRules.DroppedMessage(dropped, p.RefRequested);
                if (dropped.Count > 0) add("references_dropped_by_revit", new JArray(), new JArray(dropped), false);
            }

            // Segments: the caller-shaped operations have an arithmetic expectation;
            // the rest are held to the rehearsed count.
            if (op == DimensionPlanRules.OpDimension)
            {
                int expectedSegments = DimensionPlanRules.ExpectedSegmentCount(p.RefList.Count);
                add("segments", expectedSegments, m.SegmentCount.HasValue ? (JToken)m.SegmentCount.Value : null,
                    m.SegmentCount.HasValue && m.SegmentCount.Value == expectedSegments);
            }
            else if (op != DimensionPlanRules.OpSpotElevation && op != DimensionPlanRules.OpSpotCoordinate)
            {
                add("segments_vs_rehearsed",
                    baseline?.SegmentCount == null ? null : (JToken)baseline.SegmentCount.Value,
                    m.SegmentCount.HasValue ? (JToken)m.SegmentCount.Value : null,
                    baseline?.SegmentCount != null && m.SegmentCount.HasValue &&
                    baseline.SegmentCount.Value == m.SegmentCount.Value);
            }

            // The measured value, against what THIS call rehearsed. The same model
            // must measure the same; a difference means it moved mid-call.
            bool isSpot = op == DimensionPlanRules.OpSpotElevation || op == DimensionPlanRules.OpSpotCoordinate;
            if (!isSpot)
            {
                // The rehearsal's value only binds when the rehearsal could MEASURE one:
                // pre-commit Revit reports availability false and value 0 for a correct
                // dimension, and holding the committed value to that 0 would refuse
                // every success. When the baseline never materialised, the enforced
                // facts are the post-commit ones: availability above, segment presence
                // below, and the caller's expected_value when one was stated.
                bool baselineMaterialised = baseline != null && baseline.ReferencesAvailable == true &&
                                            baseline.Total.HasValue;
                double? want = baseline?.Total; double? got = m.Total;
                string valueField = op == DimensionPlanRules.OpAngular ? "value_internal_radians_vs_rehearsed"
                                                                       : "value_internal_feet_vs_rehearsed";
                if (baselineMaterialised)
                    add(valueField,
                        want.HasValue ? (JToken)want.Value : null, got.HasValue ? (JToken)got.Value : null,
                        want.HasValue && got.HasValue && Math.Abs(want.Value - got.Value) <= tol);
                else
                {
                    // Not-yet-materialised is the documented, expected reading before
                    // commit - Revit computes dimension values lazily. POST-commit it
                    // is not: nothing in this operation ever asks to CLEAR a dimension's
                    // value, so a null read at that stage is a real miss, never a match.
                    bool nullReadCountsAsMatch = stage != VerifyStage.PostCommit || got.HasValue;
                    checks.Add(new JObject
                    {
                        ["field"] = "value_first_materialised",
                        ["requested"] = "no rehearsal value to hold this to: Revit materialises values at commit",
                        ["read"] = got.HasValue ? (JToken)got.Value : JValue.CreateNull(),
                        ["match"] = nullReadCountsAsMatch
                    });
                }
                if (baselineMaterialised && baseline.SegmentValues != null && baseline.SegmentValues.Count > 0)
                {
                    bool segMatch = m.SegmentValues != null &&
                                    m.SegmentValues.Count == baseline.SegmentValues.Count;
                    if (segMatch)
                        for (int i = 0; i < m.SegmentValues.Count; i++)
                            if (Math.Abs(m.SegmentValues[i] - baseline.SegmentValues[i]) > tol) { segMatch = false; break; }
                    add("segment_values_vs_rehearsed",
                        new JArray(baseline.SegmentValues),
                        m.SegmentValues == null ? null : new JArray(m.SegmentValues), segMatch);
                }
                // Post-commit, a chain must actually CARRY its values: a committed
                // dimension whose segments still report none is broken, not early.
                if (stage == VerifyStage.PostCommit && m.SegmentCount.HasValue && m.SegmentCount.Value > 0)
                    add("segment_values_present", m.SegmentCount.Value,
                        m.SegmentValues == null ? 0 : m.SegmentValues.Count,
                        m.SegmentValuesMissing == 0 && m.SegmentValues != null &&
                        m.SegmentValues.Count == m.SegmentCount.Value);
            }

            // Geometry.
            if (op == DimensionPlanRules.OpDimension)
            {
                double[] reqStart = Arr(p.Line.GetEndPoint(0)), reqEnd = Arr(p.Line.GetEndPoint(1));
                bool lineOk;
                string readDescription;
                if (!m.CurveRead) { lineOk = false; readDescription = "(curve unreadable)"; }
                else if (m.CurveBound)
                {
                    lineOk = DimensionPlanRules.SameEndpoints(reqStart, reqEnd, m.CurveStart, m.CurveEnd, tol)
                          || (DimensionPlanRules.PointOnLine(m.CurveStart, Delta(m.CurveEnd, m.CurveStart), reqStart, tol) &&
                              DimensionPlanRules.PointOnLine(m.CurveStart, Delta(m.CurveEnd, m.CurveStart), reqEnd, tol));
                    readDescription = "bound " + Show(m.CurveStart) + " -> " + Show(m.CurveEnd);
                }
                else
                {
                    lineOk = DimensionPlanRules.PointOnLine(m.CurveOrigin, m.CurveDirection, reqStart, tol) &&
                             DimensionPlanRules.PointOnLine(m.CurveOrigin, m.CurveDirection, reqEnd, tol);
                    readDescription = "unbound through " + Show(m.CurveOrigin) + " along " + Show(m.CurveDirection);
                }
                add("line_carries_requested_endpoints", Show(reqStart) + " -> " + Show(reqEnd), readDescription, lineOk);
            }
            else if (op == DimensionPlanRules.OpAngular)
            {
                add("arc_center_vs_rehearsed",
                    baseline?.ArcCenter == null ? null : (JToken)Show(baseline.ArcCenter),
                    m.ArcCenter == null ? null : (JToken)Show(m.ArcCenter),
                    DimensionPlanRules.SamePoint(baseline?.ArcCenter, m.ArcCenter, tol));
                add("arc_radius_vs_rehearsed",
                    baseline?.ArcRadius == null ? null : (JToken)baseline.ArcRadius.Value,
                    m.ArcRadius.HasValue ? (JToken)m.ArcRadius.Value : null,
                    baseline?.ArcRadius != null && m.ArcRadius.HasValue &&
                    Math.Abs(baseline.ArcRadius.Value - m.ArcRadius.Value) <= tol);
            }
            else if (isSpot)
            {
                add("spot_origin_vs_rehearsed",
                    baseline?.SpotOrigin == null ? null : (JToken)Show(baseline.SpotOrigin),
                    m.SpotOrigin == null ? null : (JToken)Show(m.SpotOrigin),
                    DimensionPlanRules.SamePoint(baseline?.SpotOrigin, m.SpotOrigin, tol));
            }

            // Overrides, EQ and lock: only re-read where they were requested.
            if (p.HasPrefix) add("prefix", p.Prefix, m.Prefix, string.Equals(p.Prefix, m.Prefix ?? "", StringComparison.Ordinal));
            if (p.HasSuffix) add("suffix", p.Suffix, m.Suffix, string.Equals(p.Suffix, m.Suffix ?? "", StringComparison.Ordinal));
            if (p.HasAbove) add("above", p.Above, m.Above, string.Equals(p.Above, m.Above ?? "", StringComparison.Ordinal));
            if (p.HasBelow) add("below", p.Below, m.Below, string.Equals(p.Below, m.Below ?? "", StringComparison.Ordinal));
            if (p.HasValueOverride) add("value_override", p.ValueOverride, m.ValueOverride,
                                        string.Equals(p.ValueOverride, m.ValueOverride ?? "", StringComparison.Ordinal));
            if (p.Eq.HasValue) add("eq", p.Eq.Value, m.Eq.HasValue ? (JToken)m.Eq.Value : null, m.Eq == p.Eq);
            if (p.Lock.HasValue) add("lock", p.Lock.Value, m.Locked.HasValue ? (JToken)m.Locked.Value : null, m.Locked == p.Lock);

            // Item 2.3, post-commit: the values against the GEOMETRY of the references
            // (1 mm), no value override that was not asked for, and inside the crop.
            if (stage == VerifyStage.PostCommit && op == DimensionPlanRules.OpDimension)
            {
                JObject geometry;
                bool? geometryMatch;
                if (m.RefGeometries == null || m.LineDirection == null)
                {
                    geometry = new JObject { ["status"] = DimensionGeometryRules.StatusUnavailable,
                                             ["reason"] = m.GeometryUnreadable ?? "dimension line or references unreadable" };
                    geometryMatch = null;
                }
                else
                {
                    DimensionGeometryVerdict v = DimensionGeometryRules.VerifyAnyOrder(m.RefGeometries, m.LineDirection,
                        m.SingleValue, m.SegmentValues, DimensionGeometryRules.DefaultToleranceFeet);
                    geometry = DimensionGeometryProbe.ToJson(v, 1.0);
                    geometry["unit"] = "feet";
                    geometryMatch = v.Failed ? false : v.Status == DimensionGeometryRules.StatusVerified ? (bool?)true : null;
                }
                checks.Add(new JObject
                {
                    ["field"] = "geometry",
                    ["requested"] = "every measurable segment within 1 mm of its references' geometry",
                    ["read"] = geometry,
                    ["match"] = geometryMatch.HasValue ? (JToken)geometryMatch.Value : JValue.CreateNull()
                });
                if (geometryMatch == false) ok = false;

                if (!p.HasValueOverride)
                {
                    if (m.OverridesFound != null)
                        add("value_override_empty", "", new JArray(m.OverridesFound), m.OverridesFound.Count == 0);
                    else add("value_override_empty", "", m.OverridesUnreadable, false);
                }
                if (!p.AllowOutsideCrop && m.InsideCrop.HasValue)
                    add("inside_crop", true, m.CropDetail, m.InsideCrop.Value);
            }

            // A pattern joint is an infinite line: a segment end on one must fall inside its face.
            if (stage == VerifyStage.PostCommit && op == DimensionPlanRules.OpDimension && m.PatternExtent != null)
            {
                bool? inside = m.PatternExtent.Status == "verified" ? true
                             : m.PatternExtent.Status == "failed" ? (bool?)false : null;
                checks.Add(new JObject
                {
                    ["field"] = "pattern_lines_inside_element",
                    ["requested"] = "every segment end on a pattern line inside the patterned face",
                    ["read"] = new JObject { ["status"] = m.PatternExtent.Status, ["reason"] = m.PatternExtent.Reason,
                                             ["outside_internal_feet"] = new JArray(m.PatternExtent.Outside) },
                    ["match"] = inside.HasValue ? (JToken)inside.Value : JValue.CreateNull()
                });
                if (inside == false) ok = false;

                bool? jointMatch;
                JObject jointRead = JointPositionCheck(p, m, out jointMatch);
                if (jointRead != null)
                {
                    checks.Add(new JObject
                    {
                        ["field"] = "pattern_joint_is_segment_end",
                        ["requested"] = "every joint chosen for '<face>/?' is where a segment of the dimension ends",
                        ["read"] = jointRead,
                        ["match"] = jointMatch.HasValue ? (JToken)jointMatch.Value : JValue.CreateNull()
                    });
                    if (jointMatch == false) ok = false;
                }
            }

            // Will it reach the sheet? Reported on every row; it decides only when the
            // caller asked with require_printable. The box counts only once committed.
            {
                bool committedStage = stage == VerifyStage.PostCommit;
                DimensionPrintRules.Verdict print = PrintVerdict(m, committedStage);
                bool printOk = !p.RequirePrintable ||
                               DimensionPrintRules.RequirePrintableError(p.Index, print, committedStage) == null;
                checks.Add(new JObject
                {
                    ["field"] = "will_print",
                    ["requested"] = p.RequirePrintable ? (JToken)true : "reported only (require_printable=false)",
                    ["read"] = DimensionPaper.VerdictJson(print),
                    ["match"] = printOk
                });
                if (!printOk) ok = false;
            }

            // The caller's own postcondition. Only POST-COMMIT: that is where Revit has
            // materialised the value, and the TransactionGroup is still open there, so
            // a miss rolls the whole batch back exactly as designed.
            if (stage == VerifyStage.PostCommit && p.ExpectedValueFeet.HasValue)
            {
                double? total = m.Total;
                add("expected_value",
                    // *_internal_feet holds RADIANS for angular - named, never implied.
                    new JObject { ["unit"] = op == DimensionPlanRules.OpAngular ? "radians" : "feet",
                                  ["value_internal_feet"] = p.ExpectedValueFeet.Value,
                                  ["tolerance_internal_feet"] = p.ExpectedToleranceFeet },
                    total.HasValue ? (JToken)total.Value : null,
                    total.HasValue && Math.Abs(total.Value - p.ExpectedValueFeet.Value) <= p.ExpectedToleranceFeet);
            }

            // A fact that could not be read poisons the whole row: "we could not look"
            // must never add up to "it matches".
            if (m.Unreadable != null)
                add("readable", "every field this operation verifies", m.Unreadable, false);

            JObject verification = DimensionPlanRules.FinishDimensionChecks(checks, ok, stage, viewId,
                p.TargetViewIsActive, m.ReferencesAvailable, isSpot, m.Total);
            allOk = verification.Value<bool>("all_match");
            return verification;
        }

        private static void AddTagEvidence(JObject legacy, Plan p, Element e, ref bool ok)
        {
            if (e is IndependentTag tag)
            {
                try
                {
                    legacy["tag_evidence"] = AnnotationLayout.Evidence(tag,p.View);
                    bool held = p.TagRehearsal != null && JToken.DeepEquals(p.TagRehearsal,legacy["tag_evidence"]);
                    legacy["matches_rehearsal"] = held; ok = ok && held; legacy["verified"] = ok;
                    legacy["layout_method"] = "conservative annotation bounding boxes; visual approval remains separate";
                }
                catch(Exception ex) { ok=false; legacy["verified"]=false; legacy["unreadable"]=ex.Message; }
                JObject kind = TagKindEvidence(p, tag);
                if (kind != null) legacy["tag_kind"] = kind;
                string read = AnnotationLayout.SafeTagText(tag);
                MarkEmptyTagText(legacy, p, read, read != null);
            }
            else if (e is SpatialElementTag spatialTag)
            {
                try { legacy["tag_text"] = spatialTag.TagText; } catch { legacy["tag_text"] = null; }
                MarkEmptyTagText(legacy, p, legacy.Value<string>("tag_text"), legacy["tag_text"].Type != JTokenType.Null);
            }
            if (e is RoomTag roomTag && TaggedRoom(p) is Room taggedRoom)
            {
                legacy["room_tag"] = new JObject
                {
                    ["number"] = taggedRoom.Number,
                    ["name"] = taggedRoom.Name,
                    ["area_display"] = taggedRoom.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsValueString(),
                    ["tag_text"] = roomTag.TagText,
                    ["type_name"] = p.RoomTagTypeName,
                    ["family_name"] = p.RoomTagFamilyName,
                    ["all_fields_visible"] = ok
                };
            }
        }

        private static JObject VerifyDimensionRow(Document doc, Plan p, Element e, VerifyStage stage, out bool ok)
        {
            Measured m = Measure(doc, p, e);
            Measured baseline = p.Rehearsed;
            bool allOk;
            JObject verification = BuildChecks(p, m, baseline, stage, out allOk);
            if (baseline == null)
            {
                // Cannot happen while the constructibility gate holds, but if it ever
                // does, an absent baseline must read as failure, not as agreement.
                allOk = false;
                verification["all_match"] = false;
                verification["baseline_missing"] = "this action carries no rehearsal measurement to verify against";
            }
            ok = allOk;
            var row = new JObject
            {
                ["index"] = p.Index, ["operation"] = p.Operation,
                ["element_id"] = p.Created == null ? JValue.CreateNull() : new JValue(Rid.Value(p.Created)),
                ["verified"] = ok,
                ["unique_id"] = m.UniqueId,
                ["class"] = m.ClassName,
                ["shape"] = m.Shape,
                ["values"] = Values(m),
                ["will_print"] = DimensionPaper.VerdictJson(PrintVerdict(m, stage == VerifyStage.PostCommit)),
                ["verification"] = verification
            };
            if (m.DroppedMessage != null) row["reason"] = m.DroppedMessage;
            if (p.PatternResolutions != null) row["pattern_resolutions"] = p.PatternResolutions.DeepClone();
            return row;
        }

        private static JObject Values(Measured m)
        {
            double? total = m.Total;
            return new JObject
            {
                ["internal_feet"] = total.HasValue ? (JToken)total.Value : JValue.CreateNull(),
                ["per_segment_feet"] = m.SegmentValues == null ? (JToken)JValue.CreateNull() : new JArray(m.SegmentValues),
                ["presented"] = m.Presented,
                ["printed"] = m.Printed == null ? JValue.CreateNull() : (JToken)m.Printed.DeepClone(),
                ["internal_unit"] = "feet"
            };
        }

        private static JObject Evidence(Measured m)
        {
            var o = new JObject
            {
                ["class"] = m.ClassName,
                ["shape"] = m.Shape,
                ["values"] = Values(m),
                ["segments"] = m.SegmentCount.HasValue ? (JToken)m.SegmentCount.Value : JValue.CreateNull(),
                ["references_read"] = m.RefReps == null ? (JToken)JValue.CreateNull() : new JArray(m.RefReps),
                ["references_available"] = m.ReferencesAvailable.HasValue ? (JToken)m.ReferencesAvailable.Value : JValue.CreateNull()
            };
            if (m.SpotOrigin != null) o["spot_origin_feet"] = Show(m.SpotOrigin);
            if (m.ArcCenter != null) { o["arc_center_feet"] = Show(m.ArcCenter); o["arc_radius_feet"] = m.ArcRadius; }
            if (m.Unreadable != null) o["unreadable"] = m.Unreadable;
            return o;
        }

        private static string ExpectedClassName(string op)
        {
            switch (op)
            {
                case DimensionPlanRules.OpAngular: return "AngularDimension";
                case DimensionPlanRules.OpRadial:
                case DimensionPlanRules.OpDiameter: return "RadialDimension";
                case DimensionPlanRules.OpArcLength: return "ArcLengthDimension";
                case DimensionPlanRules.OpSpotElevation:
                case DimensionPlanRules.OpSpotCoordinate: return "SpotDimension";
                default: return "Dimension";
            }
        }

        /// <summary>
        /// By HIERARCHY: Revit is free to answer with a subclass (a two-grid
        /// NewDimension on 2025 returns LinearDimension), and a subclass of the right
        /// base is the right answer. The SHAPE check beside this one is what separates
        /// the Dimension-derived forms from each other.
        /// </summary>
        private static bool ClassMatches(string op, Element e)
        {
            switch (op)
            {
                case DimensionPlanRules.OpAngular: return e is AngularDimension;
#if !REVIT2023 && !REVIT2024
                case DimensionPlanRules.OpRadial:
                case DimensionPlanRules.OpDiameter: return e is RadialDimension;
                case DimensionPlanRules.OpArcLength: return e is ArcLengthDimension;
#endif
                case DimensionPlanRules.OpSpotElevation:
                case DimensionPlanRules.OpSpotCoordinate: return e is SpotDimension;
                default: return e is Dimension && !(e is SpotDimension);
            }
        }

        private static string ExpectedShapeName(string op)
        {
            switch (op)
            {
                case DimensionPlanRules.OpAngular: return "Angular";
                case DimensionPlanRules.OpRadial: return "Radial";
                case DimensionPlanRules.OpDiameter: return "Diameter";
                case DimensionPlanRules.OpArcLength: return "ArcLength";
                case DimensionPlanRules.OpSpotElevation:
                case DimensionPlanRules.OpSpotCoordinate: return "Spot";
                default: return "Linear";
            }
        }

        private static bool VerifyOtherAnnotation(Plan p, Element e, out string reason)
        {
            reason = null;
            if (p.Operation == "tag")
            {
                try
                {
                if (SpatialKind(p.Target) != null) return VerifySpatialTag(p, e, out reason);
                Room room = TaggedRoom(p);
                if (room != null)
                {
                    RoomTag native = e as RoomTag;
                    if (native == null || !RoomTagPointsAt(native, p, room) || native.OwnerViewId != p.View.Id || native.IsOrphaned)
                    { reason = "the committed RoomTag is missing, orphaned, or bound to another Room/view"; return false; }
                    if (!VerifySpatialLeader(native, p, out reason)) return false;
                    if (native.TagHeadPosition.DistanceTo(p.TagPoint ?? p.Point) > 1e-6)
                    { reason = "the committed RoomTag head moved from the requested point"; return false; }
                    if (native.HasLeader != (p.Input.Value<bool?>("add_leader") == true))
                    { reason = "the committed RoomTag leader state (" + native.HasLeader + ") differs from add_leader"; return false; }
                    FamilySymbol symbol = native.Document.GetElement(native.GetTypeId()) as FamilySymbol;
                    if (symbol == null || (p.Type != null && symbol.Id != p.Type.Id) ||
                        symbol.Name != p.RoomTagTypeName || symbol.Family.Name != p.RoomTagFamilyName)
                    { reason = "the committed RoomTag type/family differs from the rehearsed choice"; return false; }
                    string area = room.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsValueString();
                    string roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name;
                    string textProblem = RoomTagRules.TextProblem(native.TagText, room.Number, roomName, area, room.Area * 0.09290304);
                    if (textProblem != null)
                    {
                        string shown = native.TagText ?? "";
                        if (shown.Length > 300) shown = shown.Substring(0, 300) + "...";
                        reason = textProblem + "; " +
                            "observed_tag_text=" + JsonConvert.ToString(shown) +
                            ", expected_number=" + JsonConvert.ToString(room.Number) +
                            ", expected_name=" + JsonConvert.ToString(roomName) +
                            ", expected_area=" + JsonConvert.ToString(area);
                        return false;
                    }
                    return true;
                }
                IndependentTag tag = e as IndependentTag;
                if (tag != null && p.LinkedElement != null)
                {
                    var taggedPairs = tag.GetTaggedElementIds().Select(l => new KeyValuePair<long, long>(Rid.Value(l.LinkInstanceId), Rid.Value(l.LinkedElementId)));
                    if (!LinkedTagRules.PointsOnlyAt(taggedPairs, Rid.Value(p.LinkInstance.Id), Rid.Value(p.LinkedElement.Id)))
                    { reason = "the created tag does not point only at element " + Rid.Value(p.LinkedElement.Id) + " of link instance " + Rid.Value(p.LinkInstance.Id); return false; }
                }
                else if (tag == null || !tag.GetTaggedLocalElementIds().Any(id => id == p.Target.Id))
                { reason = "the created element is not a tag on element " + Rid.Value(p.Target.Id); return false; }
                if (!VerifyTagKind(p, tag, out reason)) return false;
                Element expected = p.Type ?? p.EffectiveTagType;
                if (expected != null && tag.GetTypeId() != expected.Id)
                { reason = "the committed tag type is not the bound type " + Rid.Value(expected.Id); return false; }
                if (tag.OwnerViewId != p.View.Id || tag.IsOrphaned || tag.TagHeadPosition.DistanceTo(p.TagPoint ?? p.Point)>1e-6 ||
                    tag.HasLeader != (p.Input.Value<bool?>("add_leader")==true) ||
                    tag.TagOrientation != ((p.Input.Value<string>("orientation") ?? "horizontal") == "vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal))
                { reason = "the committed tag differs from the plan in view, orphan state, head position, leader or orientation"; return false; }
                if (!VerifyTagLeader(p, tag, out reason)) return false;
                if (p.Input.Value<bool?>("require_tag_text")==true && (string.IsNullOrWhiteSpace(AnnotationLayout.SafeTagText(tag)) || AnnotationLayout.SafeTagText(tag).Trim()=="?"))
                { reason = "require_tag_text is on and the tag reads '" + (AnnotationLayout.SafeTagText(tag) ?? "") + "': the tag family " +
                           "produces no readable text for this element"; return false; }
                if (p.Input.Value<bool?>("avoid_collisions")==true)
                {
                    // A coverage refusal is NOT the same finding as an overlap, and it
                    // travels with its ids instead of collapsing into a bare false.
                    AnnotationSurvey survey = AnnotationLayout.Survey(tag.Document, p.View, tag.Id, p.LayoutAccepted, p.Budget);
                    p.TagCoverage = survey.Coverage;
                    if (!survey.Complete) { reason = AnnotationVisibility.RefusalMessage(survey.Coverage); return false; }
                    // A LABEL-ONLY tag family publishes no extent - every stock Autodesk
                    // tag is one - so there is no box to test against the obstacles. The
                    // overlap is then neither asserted nor quietly passed: the coverage
                    // says it was not measured, and the row carries that with it.
                    PlanBox own; bool extentMeasured = true;
                    try { own = AnnotationLayout.OwnBox(tag, p.View); }
                    catch (Exception ex)
                    {
                        extentMeasured = false; own = default(PlanBox);
                        p.TagCoverage["tag_extent_measured"] = false;
                        p.TagCoverage["tag_extent_unavailable_reason"] = ex.Message;
                        p.TagCoverage["layout_claim"] = "avoid_collisions could not measure this tag's own extent, so no " +
                            "clearance is claimed for it; the obstacles around it were measured.";
                    }
                    if (extentMeasured)
                    {
                        p.TagCoverage["tag_extent_measured"] = true;
                        double gap = (p.Input.Value<double?>("layout_clearance") ?? 10) * p.Scale;
                        bool clear = DeliveryLayoutRules.Clear(own, survey.Obstacles, gap, survey.Bounds, p.Budget);
                        p.TagCoverage["clearance_verified"] = clear;
                        if (!clear)
                        {
                            var fallbackIds = new HashSet<long>(p.LayoutBatch
                                .Where(row => row.Created != null && row.TagLayoutFallback)
                                .Select(row => Rid.Value(row.Created)));
                            var required = p.TagLayoutFallback ? Enumerable.Empty<PlanBox>() :
                                survey.Obstacles.Where((box, i) => !fallbackIds.Contains(survey.ObstacleIds[i]));
                            if (!DeliveryLayoutRules.Clear(own, required, gap, survey.Bounds, p.Budget))
                            {
                                reason = "the committed tag overlaps a required measured annotation or leaves the view limit";
                                return false;
                            }
                            p.TagCoverage["layout_fallback"] = p.TagLayoutFallback;
                            p.TagCoverage["layout_claim"] = p.TagLayoutFallback
                                ? "This tag used the least conflicting measured position within the search budget."
                                : "Clearance against a tag placed under the layout assumption was not verified.";
                        }
                    }
                }
                return true;
                }
                catch (AnnotationCoverageException ex)
                {
                    p.TagCoverage = ex.Coverage; reason = ex.Message; return false;
                }
                catch (Exception ex) when (!(ex is AnnotationBudgetExceeded))
                {
                    // Rehearsal remains responsible for a confirmed rollback; the reason
                    // still names what threw instead of disappearing.
                    reason = "tag verification could not complete: " + ex.GetType().Name + ": " + ex.Message;
                    return false;
                }
            }
            bool okDim = e is Dimension dimension && dimension.References != null && dimension.References.Size == p.References.Size;
            if (!okDim) reason = "the created dimension does not read back with the requested reference count";
            return okDim;
        }

        /// <summary>One dry-run plan row: the legacy keys plus everything the plan resolved.</summary>
        private JObject PlanRow(Plan p)
        {
            var row = new JObject
            {
                ["index"] = p.Index,
                ["operation"] = p.Operation,
                ["references"] = DimensionPlanRules.IsDimensionOperation(p.Operation)
                    ? (JToken)p.RefList.Count
                    : p.References == null ? (JToken)JValue.CreateNull() : p.References.Size
            };
            if (!DimensionPlanRules.IsDimensionOperation(p.Operation))
            {
                if (p.Operation == "tag")
                {
                    row["view"] = IdNameJson(p.View);
                    row["target"] = IdNameJson(p.Target);
                    if (p.LinkedElement != null)
                        row["linked_target"] = LinkedTagRules.Describe(Rid.Value(p.LinkInstance.Id), p.LinkInstance.Name,
                            Rid.Value(p.LinkedElement.Id), p.LinkInstance.GetLinkDocument()?.Title, p.LinkedElement.Category?.Name);
                    Element effective = p.Type ?? p.EffectiveTagType;
                    row["tag_type"] = effective == null ? (JToken)JValue.CreateNull() : IdNameJson(effective);
                    if (effective != null) ((JObject)row["tag_type"])["source"] = p.Type == null || p.TypeDefaulted ? "default" : "explicit";
                    row["existing_tags_for_target_in_view"] = p.ExistingTagCount;
                    if (p.Mode != null) row["tag_mode"] = p.Mode;
                    if (p.ExtraTargets != null) row["also_tags"] = new JArray(p.ExtraTargets.Select(IdNameJson));
                    if (p.KeynoteKey != null) row["keynote_key"] = p.KeynoteKey;
                    if (TaggedRoom(p) != null)
                    {
                        row["room_tag_family_path"] = p.RoomTagFamilyPath;
                        row["room_tag_family_sha256"] = p.RoomTagFamilyHash;
                        row["room_tag_family_name"] = p.RoomTagFamilyName;
                        row["room_tag_type_name"] = p.RoomTagTypeName;
                    }
                }
                if (p.TextPlan != null) row["text_plan"] = TextPlanJson(p);
                return row;
            }

            row["view"] = IdNameJson(p.View);
            row["dimension_type"] = IdNameJson(p.EffectiveType);
            ((JObject)row["dimension_type"])["source"] = p.TypeFromDefault ? "default" : "explicit";
            var refRows = new JArray();
            for (int i = 0; i < p.RefList.Count; i++)
                refRows.Add(new JObject
                {
                    ["order"] = i,
                    ["requested"] = p.RefRequested[i],
                    ["reserialized"] = p.RefReserialized[i],
                    ["owner_unique_id"] = p.RefOwnerUids[i],
                    ["reference_type"] = p.RefTypes[i],
                    ["geometry_fingerprint"] = p.RefFingerprints[i],
                    ["linked"] = p.RefLinks[i] != null,
                    ["link"] = LinkJson(p.RefLinks[i])
                });
            row["reference_detail"] = refRows;
            row["has_linked_references"] = p.HasLinkedReferences;
            if (p.Line != null)
                row["line_feet"] = new JObject { ["start"] = Show(Arr(p.Line.GetEndPoint(0))), ["end"] = Show(Arr(p.Line.GetEndPoint(1))) };
            if (p.ArcCenter != null)
                row["arc_feet"] = new JObject { ["center"] = Show(Arr(p.ArcCenter)), ["radius"] = p.ArcRadius };
            if (p.SpotOrigin != null)
                row["spot_feet"] = new JObject
                {
                    ["point"] = Show(Arr(p.SpotOrigin)), ["bend"] = Show(Arr(p.SpotBend)),
                    ["end"] = Show(Arr(p.SpotEnd)), ["leader"] = p.SpotLeader
                };
            if (p.ExpectedValueFeet.HasValue)
                row["expected_value_internal_feet"] = new JObject
                {
                    ["value"] = p.ExpectedValueFeet.Value,
                    ["tolerance"] = p.ExpectedToleranceFeet
                };
            if (p.Rehearsed != null)
                row["rehearsed_values"] = Values(p.Rehearsed);
            return row;
        }

        private static JObject IdNameJson(Element e)
        {
            var o = new JObject();
            if (e == null) return o;
            o["id"] = Rid.Value(e.Id);
            try { o["unique_id"] = e.UniqueId; } catch { o["unique_id"] = "<unreadable>"; }
            try { o["name"] = e.Name; } catch { o["name"] = "<unreadable>"; }
            return o;
        }

        // ---------------------------------------------------------------------
        // Small helpers.
        // ---------------------------------------------------------------------
        private static T Need<T>(Document d, JObject a, string f) where T : Element { long id = a.Value<long?>(f) ?? -1; if (!Rid.CanRepresent(id) || !(d.GetElement(Rid.Make(id)) is T e)) throw new ArgumentException(f + " must identify " + typeof(T).Name + DocumentContext.Hint<T>(d, id.ToString())); return e; }
        private static string FileHash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static string ResolveRoomTagFamily(Document doc, string supplied)
        {
            if (!string.IsNullOrWhiteSpace(supplied))
            {
                if (!Path.IsPathRooted(supplied) || !string.Equals(Path.GetExtension(supplied), ".rfa", StringComparison.OrdinalIgnoreCase) || !File.Exists(supplied))
                    throw new ArgumentException("tag_family_path must be an absolute, existing .rfa file.");
                return Path.GetFullPath(supplied);
            }
            if (new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RoomTags)
                .WhereElementIsElementType().Any()) return null;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "RVT " + HostRevitYear, "Libraries");
            if (!Directory.Exists(root))
                throw new ArgumentException("No RoomTag type is loaded and the Revit family library is absent; pass tag_family_path to an existing RoomTag RFA.");
            string[] matches;
            try { matches = Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories)
                .Where(RoomTagRules.IsDefaultFamilyName).Take(2).ToArray(); }
            catch (Exception ex) { throw new ArgumentException("The Revit family library could not be searched: " + ex.Message + "; pass tag_family_path."); }
            if (matches.Length != 1)
                throw new ArgumentException("No unique default RoomTag RFA was found in " + root +
                    " (found " + matches.Length + "); pass tag_family_path to the approved RoomTag family.");
            return matches[0];
        }
        private static int ExistingTagCount(Document doc, ElementId viewId, ElementId targetId)
        {
            int count = 0;
            // Do not use FilteredElementCollector(doc, viewId) here. Revit 2023 was
            // measured omitting annotations from an unopened view until graphics had
            // regenerated even though OwnerViewId and get_BoundingBox(view) proved they
            // were there. Duplicate prevention is a database fact, so sweep the class
            // document-wide and filter by its authoritative owner instead.
            foreach (IndependentTag tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                try { if (tag.OwnerViewId == viewId && tag.GetTaggedLocalElementIds().Any(id => id == targetId)) count++; }
                catch { }
            }
            // RoomTag is an API subclass, but Revit's native collector rejects
            // OfClass(RoomTag). Filter its native category and narrow in managed code.
            foreach (RoomTag tag in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType().OfType<RoomTag>())
            {
                try { if (tag.OwnerViewId == viewId && tag.TaggedLocalRoomId == targetId) count++; }
                catch { }
            }
            foreach (SpatialElementTag tag in new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(
                new ElementCategoryFilter(BuiltInCategory.OST_AreaTags), new ElementCategoryFilter(BuiltInCategory.OST_MEPSpaceTags)))
                .WhereElementIsNotElementType().OfType<SpatialElementTag>())
            {
                try { if (tag.OwnerViewId == viewId && SpatialTargetId(tag) == targetId) count++; }
                catch { }
            }
            return count;
        }
        /// <summary>The host's default (or first loaded) tag type for the category of a linked element, or Invalid.</summary>
        private static ElementId DefaultLinkedTagTypeId(Document doc, Element linkedElement)
        {
            if (linkedElement.Category == null) return ElementId.InvalidElementId;
            string catName = linkedElement.Category.BuiltInCategory.ToString();
            string tagName = LinkedTagRules.TagCategoryName(catName);
            if (tagName == null || !Enum.IsDefined(typeof(BuiltInCategory), tagName)) return ElementId.InvalidElementId;
            BuiltInCategory tagCat = (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), tagName);
            ElementId catId = new ElementId(tagCat);
            try
            {
                ElementId def = doc.GetDefaultFamilyTypeId(catId);
                if (def != null && def != ElementId.InvalidElementId) return def;
            }
            catch { }
            FamilySymbol first = new FilteredElementCollector(doc).OfCategory(tagCat).WhereElementIsElementType()
                .OfType<FamilySymbol>().OrderBy(x => x.Name, StringComparer.Ordinal).FirstOrDefault();
            return first == null ? ElementId.InvalidElementId : first.Id;
        }
        /// <summary>Tags in a view that already point at one element of a link (the pair, not the host id).</summary>
        private static int ExistingLinkedTagCount(Document doc, ElementId viewId, ElementId linkInstanceId, ElementId linkedElementId)
        {
            int count = 0;
            foreach (IndependentTag tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                try
                {
                    if (tag.OwnerViewId == viewId && tag.GetTaggedElementIds().Any(l => l.LinkInstanceId == linkInstanceId && l.LinkedElementId == linkedElementId)) count++;
                }
                catch { }
            }
            // A room of the link carries a RoomTag bound to the (instance, room) pair.
            foreach (RoomTag tag in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType().OfType<RoomTag>())
            {
                try
                {
                    LinkElementId bound = tag.TaggedRoomId;
                    if (tag.OwnerViewId == viewId && bound != null && bound.LinkInstanceId == linkInstanceId && bound.LinkedElementId == linkedElementId) count++;
                }
                catch { }
            }
            return count;
        }
        private static XYZ Point(JToken t, double s, bool z) { JArray a = t as JArray; if (a == null || a.Count < (z ? 3 : 2) || a.Count > 3) throw new ArgumentException("point/line coordinate has wrong length"); return new XYZ(a[0].Value<double>() * s, a[1].Value<double>() * s, (a.Count > 2 ? a[2].Value<double>() : 0) * s); }
        private static double[] Arr(XYZ v) => new[] { v.X, v.Y, v.Z };
        private static double[] Delta(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static string Canon(XYZ v) => DimensionPlanRules.CanonicalPoint(v.X, v.Y, v.Z);
        private static string Show(double[] v)
            => v == null ? "(null)" : "[" + string.Join(",", v.Select(d => d.ToString("0.######", CultureInfo.InvariantCulture))) + "]";
        private static string SafeName(Element e) { try { return e?.Name ?? ""; } catch { return "<unreadable>"; } }
        private static string SafeUid(Element e) { try { return e?.UniqueId ?? ""; } catch { return "<unreadable>"; } }

        /// <summary>
        /// Identity and name in one guarded read. The name is what the person read when
        /// they approved; the UniqueId is what makes a swap under the same name visible.
        /// A plan must never fail while MEASURING, so unreadable stays a value.
        /// </summary>
        private static string SafePlanIdName(Element e)
        {
            if (e == null) return "";
            string uid; try { uid = e.UniqueId ?? ""; } catch { uid = "<unreadable>"; }
            string name; try { name = e.Name ?? ""; } catch { name = "<unreadable>"; }
            return uid + "|" + name;
        }

        /// <summary>
        /// Deletes WARNINGS during a batch so Revit does not raise a modal nobody is at
        /// the keyboard to answer; errors are still Revit's to resolve or roll back.
        /// </summary>
        private class SilenceWarnings : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (var f in a.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        /// <summary>Everything the verification reads off one created dimension, guarded.</summary>
        private sealed class Measured
        {
            public string ClassName, Shape;
            public bool ClassMatches;
            public int SegmentValuesMissing;
            public long? ElementId;
            public string UniqueId;
            public long? OwnerViewId, TypeId;
            public bool? ReferencesAvailable;
            public int? SegmentCount;
            public List<string> RefReps;
            public List<string> RefTypes;
            public double? SingleValue;
            public List<double> SegmentValues;
            public string Presented;
            public bool CurveRead, CurveBound;
            public double[] CurveStart, CurveEnd, CurveOrigin, CurveDirection;
            public double[] ArcCenter;
            public double? ArcRadius;
            public double[] SpotOrigin;
            public string Prefix, Suffix, Above, Below, ValueOverride;
            public bool? Eq, Locked;
            public string Unreadable;
            public double[] LineDirection;
            public List<RefGeometry> RefGeometries; public string GeometryUnreadable;
            public List<string> OverridesFound; public string OverridesUnreadable;
            public bool? InsideCrop; public JObject CropDetail;
            /// <summary>Paper facts: box in the view, hidden references, the printed text.</summary>
            public List<double> PatternBoundaries;
            public bool? HasBox; public List<string> HiddenRefs = new List<string>(), PrintUnknown = new List<string>();
            public JObject Printed;
            public string DroppedMessage;
            public DimensionPatternRules.ExtentVerdict PatternExtent;
            public double? Total => DimensionPlanRules.TotalOf(SingleValue, SegmentValues);
        }

        private sealed class Plan
        {
            public int Index; public string Operation, Text; public View View; public JObject Input; public double Scale;
            public XYZ Point; public Element Target, Type; public Line Line; public ReferenceArray References;
            /// <summary>Tag of an element in a Revit link: the link instance (also Target) and the element inside the linked model.</summary>
            public RevitLinkInstance LinkInstance; public Element LinkedElement;
            public ElementId Created;
            public int ExistingTagCount;
            public bool Constructible;
            public ElementType EffectiveTagType;
            public string RoomTagFamilyPath, RoomTagFamilyHash, RoomTagFamilyName, RoomTagTypeName;
            /// <summary>Area or MEP space tag: the tag type chosen at planning (SpatialTagRules).</summary>
            public ElementType SpatialType;
            public XYZ TagPoint;
            public JObject TagRehearsal;
            public AnnotationBudget Budget;
            /// <summary>Ids the caller explicitly accepted as unmeasurable; never a blanket switch.</summary>
            public HashSet<long> LayoutAccepted = new HashSet<long>();
            /// <summary>The annotation coverage this tag was judged against.</summary>
            public JObject TagCoverage;
            /// <summary>Resolved tag_mode of an IndependentTag (TagRules.Modes); null for room/area/space tags.</summary>
            public string Mode;
            /// <summary>tag_type_id was not given and the plan chose the single loaded type (keynote, link multi-category).</summary>
            public bool TypeDefaulted;
            /// <summary>Further elements of this document the same tag labels (IndependentTag.AddReferences).</summary>
            public List<Element> ExtraTargets;
            /// <summary>Free leader end and leader elbow, model coordinates in feet; null when not asked.</summary>
            public XYZ LeaderEnd, LeaderElbow;
            public bool TagLayoutFallback;
            public List<Plan> LayoutBatch;
            /// <summary>keynote_user: the keynote-table key written into the tag.</summary>
            public string KeynoteKey;
            /// <summary>keynote_user: the keynote table's text for KeynoteKey, read when planning (null when the entry has none).</summary>
            public string KeynoteText;
            /// <summary>keynote_material: the face the tag is placed on, its material and that material's keynote (the expected key).</summary>
            public Reference MaterialFaceRef;
            public ElementId MaterialId;
            public string MaterialKeynote;
            /// <summary>keynote_material: candidate faces (reference, material, keynote) in the order creation tries them.</summary>
            public List<Tuple<Reference, ElementId, string>> MaterialFaces;
            /// <summary>keynote_material: the keynote table (key to text) read when planning; null when unreadable.</summary>
            public Dictionary<string, string> MaterialKeynoteTable;

            // Dimension production.
            public readonly List<Reference> RefList = new List<Reference>();
            public readonly List<string> RefRequested = new List<string>();
            public readonly List<string> RefReserialized = new List<string>();
            public readonly List<string> RefOwnerUids = new List<string>();
            public readonly List<string> RefTypes = new List<string>();
            public readonly List<string> RefFingerprints = new List<string>();
            /// <summary>Null at a position whose reference is host geometry; the provenance otherwise.</summary>
            public readonly List<LinkBinding> RefLinks = new List<LinkBinding>();
            public bool HasLinkedReferences
            {
                get { foreach (LinkBinding b in RefLinks) if (b != null) return true; return false; }
            }
            public DimensionType EffectiveType; public bool TypeFromDefault, TargetViewIsActive;
            public Reference ArcReference; public Arc Arc; public XYZ ArcCenter; public double ArcRadius;
            public XYZ SpotOrigin, SpotBend, SpotEnd; public bool SpotLeader;
            public bool HasPrefix, HasSuffix, HasAbove, HasBelow, HasValueOverride;
            public string Prefix, Suffix, Above, Below, ValueOverride;
            public bool AllowTextOverride, AllowOutsideCrop;
            public bool? Eq, Lock;
            public double? ExpectedValueFeet; public double ExpectedToleranceFeet;
            public Measured Rehearsed;
            /// <summary>operation=text: the paper facts (width, lines, leader, visibility) - AnnotateCommand.TextNotes.cs.</summary>
            public TextLayout TextPlan;
            /// <summary>require_printable: a dimension that would not print is refused.</summary>
            public bool RequirePrintable;
            /// <summary>'&lt;face&gt;/?' entries resolved to the nearest joint, with the search evidence.</summary>
            public JArray PatternResolutions;
        }
    }
}
