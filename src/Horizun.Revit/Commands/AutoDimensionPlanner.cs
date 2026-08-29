// -----------------------------------------------------------------------------
// Horizun Revit MCP - the Revit half of auto_dimension_*. Original Horizun code.
//
// FINDING the geometry a chain is built from - grids visible in a view, level
// datums in a section, curtain grid lines, the centre reference of an opening -
// and turning each into a candidate the Revit-free rules can order. The
// DECISIONS (which chain, which axis, which order, what is a duplicate, what was
// left out) live in Core/AutoDimensionRules.cs and are proved without a model.
//
// This file writes NOTHING. Its whole output is a horizun_annotate dry-run
// request, because that command remains the single rehearsed, confirmed and
// host-verified write path. A planner that wrote would be a second one.
//
// The one rule that shapes everything here: A SOURCE THAT CANNOT ANSWER IS
// NAMED. A grid that is hidden in the view, an opening whose family exposes no
// centre reference, a curtain wall with no grid lines - each becomes an omission
// row with a structured code. A plan that silently covered nine of twelve grids
// is worse than one that refused, because the drawing looks finished.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>
    /// Where a set of candidates came from: the host document, or one link instance.
    /// Carried so a candidate can be turned into a HOST-document stable reference and
    /// its provenance reported, exactly as the discovery surface does it.
    /// </summary>
    internal sealed class AutoDimensionSource
    {
        public Document GeometryDoc;

        // The link half of this class was REMOVED after measurement (2026-08-26):
        // Revit's NewDimension rejects datum references lifted through a link, so
        // the planner is host-only and PlanAnnotationsCommand refuses
        // link_instance_id with the measured reason. The identity transform and the
        // constant stay so the collection paths read the same either way - and so a
        // future measured re-enable changes one class, not four collectors.
        public Transform ToHost = Transform.Identity;
        public long LinkInstanceId = 0;
        public bool IsLinked => false;

        public Reference Lift(Reference reference) => reference;
    }

    internal static class AutoDimensionPlanner
    {
        /// <summary>
        /// Collect the candidates one operation is about, from one source. Omissions
        /// are appended rather than thrown: a source that produced nothing useful is a
        /// finding about the model, not a failure of the call.
        /// </summary>
        public static List<AutoDimensionCandidate> Collect(string operation, Document hostDoc, View view,
                                                            AutoDimensionSource source,
                                                            IList<long> explicitIds,
                                                            List<AutoDimensionOmission> omissions,
                                                            out string error)
        {
            error = null;
            switch (operation)
            {
                case AutoDimensionRules.OpGrids:
                    return CollectDatums<Grid>(hostDoc, view, source, explicitIds, omissions, "grid");
                case AutoDimensionRules.OpLevels:
                    return CollectDatums<Level>(hostDoc, view, source, explicitIds, omissions, "level");
                case AutoDimensionRules.OpCurtainWalls:
                    return CollectCurtainGrids(hostDoc, view, source, explicitIds, omissions);
                case AutoDimensionRules.OpOpenings:
                    return CollectOpenings(hostDoc, view, source, explicitIds, omissions);
                default:
                    error = AutoDimensionRules.OperationError(operation);
                    return null;
            }
        }

        // =====================================================================
        // Grids and levels - the datum classes
        // =====================================================================

        /// <summary>
        /// Every datum of one class that is VISIBLE in the view, or the explicit subset
        /// the caller named. Visibility is what a plan needs: a grid hidden by the view
        /// template cannot carry a dimension in that view, and offering it would
        /// produce a plan whose rehearsal fails for a reason the caller cannot see.
        ///
        /// A LEVEL contributes its horizontal plane and no direction, which is exactly
        /// right: levels are dimensioned as a vertical stack in one chain, never
        /// grouped by direction. A GRID contributes its curve's direction and is
        /// grouped by it, which is what keeps the two families of a building apart.
        /// </summary>
        private static List<AutoDimensionCandidate> CollectDatums<T>(Document hostDoc, View view,
                                                                      AutoDimensionSource source,
                                                                      IList<long> explicitIds,
                                                                      List<AutoDimensionOmission> omissions,
                                                                      string sourceName) where T : Element
        {
            var result = new List<AutoDimensionCandidate>();
            IEnumerable<T> found = Candidates<T>(source.GeometryDoc, view, source.IsLinked, explicitIds);

            XYZ right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize(), origin = view.Origin;
            foreach (T datum in found.OrderBy(e => Rid.Value(e.Id)))
            {
                long id = Rid.Value(datum.Id);
                var placeholder = new AutoDimensionCandidate
                {
                    SubjectId = id, Source = sourceName, Label = SafeName(datum),
                    LinkInstanceId = source.IsLinked ? (long?)source.LinkInstanceId : null
                };
                try
                {
                    Reference lifted = source.Lift(new Reference(datum));
                    string stable = lifted == null ? null : Stable(lifted, hostDoc);
                    if (stable == null)
                    {
                        omissions.Add(new AutoDimensionOmission(placeholder, AutoDimensionRules.CodeNoReference,
                            "no host-document reference could be made for this " + sourceName + "."));
                        continue;
                    }

                    XYZ point; XYZ direction = null;
                    var grid = datum as Grid;
                    var level = datum as Level;
                    if (grid != null)
                    {
                        Curve curve = grid.Curve;
                        if (curve == null || !curve.IsBound)
                        {
                            omissions.Add(new AutoDimensionOmission(placeholder, AutoDimensionRules.CodeUnreadable,
                                "the grid has no bounded curve to measure from."));
                            continue;
                        }
                        Curve inHost = source.IsLinked ? curve.CreateTransformed(source.ToHost) : curve;
                        point = inHost.Evaluate(0.5, true);
                        direction = inHost.GetEndPoint(1).Subtract(inHost.GetEndPoint(0));
                    }
                    else if (level != null)
                    {
                        point = source.ToHost.OfPoint(new XYZ(0, 0, level.ProjectElevation));
                    }
                    else
                    {
                        omissions.Add(new AutoDimensionOmission(placeholder, AutoDimensionRules.CodeUnreadable,
                            "unsupported datum class " + datum.GetType().Name + "."));
                        continue;
                    }

                    placeholder.StableRepresentation = stable;
                    // The DEDUP identity collapses to the datum's own id (measured:
                    // Revit respells datum references once they live on a dimension;
                    // see CanonicalReference). The stable representation the caller
                    // dimensions WITH stays untouched.
                    placeholder.DedupIdentity = "datum:" + (Safe(() => datum.UniqueId) ?? stable);
                    Project(placeholder, point, direction, right, up, origin);
                    result.Add(placeholder);
                }
                catch (Exception ex)
                {
                    omissions.Add(new AutoDimensionOmission(placeholder, AutoDimensionRules.CodeUnreadable,
                        sourceName + " could not be read: " + ex.Message));
                }
            }
            return result;
        }

        // =====================================================================
        // Curtain walls
        // =====================================================================

        /// <summary>
        /// The U and V grid lines of every curtain wall named (or every one visible in
        /// the view). The two directions are NOT merged here - GroupByDirection sorts
        /// them out - so a curtain wall whose mullions run both ways produces two
        /// chains rather than one nonsensical one.
        /// </summary>
        private static List<AutoDimensionCandidate> CollectCurtainGrids(Document hostDoc, View view,
                                                                         AutoDimensionSource source,
                                                                         IList<long> explicitIds,
                                                                         List<AutoDimensionOmission> omissions)
        {
            var result = new List<AutoDimensionCandidate>();
            XYZ right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize(), origin = view.Origin;

            foreach (Wall wall in Candidates<Wall>(source.GeometryDoc, view, source.IsLinked, explicitIds)
                                  .OrderBy(e => Rid.Value(e.Id)))
            {
                long wallId = Rid.Value(wall.Id);
                var wallRow = new AutoDimensionCandidate
                {
                    SubjectId = wallId, Source = "curtain_grid", Label = SafeName(wall),
                    LinkInstanceId = source.IsLinked ? (long?)source.LinkInstanceId : null
                };
                CurtainGrid cg = null;
                try { cg = wall.CurtainGrid; } catch { cg = null; }
                if (cg == null)
                {
                    // Only an omission when the caller NAMED this wall: sweeping a view
                    // and reporting every ordinary wall as an omission would bury the
                    // real findings under noise.
                    if (explicitIds != null && explicitIds.Count > 0)
                        omissions.Add(new AutoDimensionOmission(wallRow, AutoDimensionRules.CodeNoReference,
                            "wall " + wallId + " is not a curtain wall - it has no curtain grid."));
                    continue;
                }

                var lineIds = new List<KeyValuePair<string, ElementId>>();
                try
                {
                    foreach (ElementId id in cg.GetUGridLineIds() ?? new List<ElementId>())
                        lineIds.Add(new KeyValuePair<string, ElementId>("curtain_grid_u", id));
                    foreach (ElementId id in cg.GetVGridLineIds() ?? new List<ElementId>())
                        lineIds.Add(new KeyValuePair<string, ElementId>("curtain_grid_v", id));
                }
                catch (Exception ex)
                {
                    omissions.Add(new AutoDimensionOmission(wallRow, AutoDimensionRules.CodeUnreadable,
                        "the curtain grid of wall " + wallId + " could not be read: " + ex.Message));
                    continue;
                }
                if (lineIds.Count == 0)
                {
                    omissions.Add(new AutoDimensionOmission(wallRow, AutoDimensionRules.CodeNoReference,
                        "curtain wall " + wallId + " has no grid lines; there is nothing between which to " +
                        "measure. A single-panel curtain wall is dimensioned by its own faces instead."));
                    continue;
                }

                foreach (KeyValuePair<string, ElementId> entry in
                         lineIds.OrderBy(e => e.Key, StringComparer.Ordinal).ThenBy(e => Rid.Value(e.Value)))
                {
                    long lineId = Rid.Value(entry.Value);
                    var row = new AutoDimensionCandidate
                    {
                        SubjectId = lineId, Source = entry.Key,
                        Label = SafeName(wall) + " " + entry.Key + " " + lineId,
                        LinkInstanceId = source.IsLinked ? (long?)source.LinkInstanceId : null
                    };
                    try
                    {
                        var line = source.GeometryDoc.GetElement(entry.Value) as CurtainGridLine;
                        if (line == null)
                        {
                            omissions.Add(new AutoDimensionOmission(row, AutoDimensionRules.CodeUnreadable,
                                "grid line " + lineId + " did not resolve."));
                            continue;
                        }
                        Curve curve = line.FullCurve;
                        if (curve == null || !curve.IsBound)
                        {
                            omissions.Add(new AutoDimensionOmission(row, AutoDimensionRules.CodeUnreadable,
                                "grid line " + lineId + " has no bounded curve."));
                            continue;
                        }
                        Reference lifted = source.Lift(new Reference(line));
                        string stable = lifted == null ? null : Stable(lifted, hostDoc);
                        if (stable == null)
                        {
                            omissions.Add(new AutoDimensionOmission(row, AutoDimensionRules.CodeNoReference,
                                "grid line " + lineId + " has no usable host-document reference."));
                            continue;
                        }
                        Curve inHost = source.IsLinked ? curve.CreateTransformed(source.ToHost) : curve;
                        row.StableRepresentation = stable;
                        Project(row, inHost.Evaluate(0.5, true),
                                inHost.GetEndPoint(1).Subtract(inHost.GetEndPoint(0)), right, up, origin);
                        result.Add(row);
                    }
                    catch (Exception ex)
                    {
                        omissions.Add(new AutoDimensionOmission(row, AutoDimensionRules.CodeUnreadable,
                            "grid line " + lineId + " could not be read: " + ex.Message));
                    }
                }
            }
            return result;
        }

        // =====================================================================
        // Openings
        // =====================================================================

        /// <summary>
        /// The CENTRE reference of each opening - what Revit itself calls
        /// CenterLeftRight, the reference plane a door or window family is built around.
        /// Not its bounding box and not its location point: a location point has no
        /// reference a dimension can attach to, and a bounding box is a measurement of
        /// the geometry rather than of the family's own datum.
        ///
        /// A family that exposes no such reference is an omission with a code, because
        /// it is a real and common condition (a face-based or badly authored family)
        /// and substituting some other reference would dimension to the wrong thing.
        /// </summary>
        private static List<AutoDimensionCandidate> CollectOpenings(Document hostDoc, View view,
                                                                     AutoDimensionSource source,
                                                                     IList<long> explicitIds,
                                                                     List<AutoDimensionOmission> omissions)
        {
            var result = new List<AutoDimensionCandidate>();
            XYZ right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize(), origin = view.Origin;

            IEnumerable<FamilyInstance> found = Candidates<FamilyInstance>(source.GeometryDoc, view,
                                                                            source.IsLinked, explicitIds)
                .Where(fi => explicitIds != null && explicitIds.Count > 0 || IsOpeningCategory(fi));

            foreach (FamilyInstance opening in found.OrderBy(e => Rid.Value(e.Id)))
            {
                long id = Rid.Value(opening.Id);
                var row = new AutoDimensionCandidate
                {
                    SubjectId = id, Source = "opening_center", Label = SafeName(opening),
                    LinkInstanceId = source.IsLinked ? (long?)source.LinkInstanceId : null
                };
                try
                {
                    IList<Reference> centres = null;
                    try { centres = opening.GetReferences(FamilyInstanceReferenceType.CenterLeftRight); }
                    catch { centres = null; }
                    if (centres == null || centres.Count == 0)
                    {
                        omissions.Add(new AutoDimensionOmission(row, AutoDimensionRules.CodeNoReference,
                            "family instance " + id + " (" + SafeName(opening) + ") exposes no CenterLeftRight " +
                            "reference. Its family has no left/right centre plane marked as a reference, so " +
                            "there is nothing at its centre a dimension can attach to. Dimension to its host " +
                            "wall's faces instead, or mark the plane in the family."));
                        continue;
                    }
                    if (centres.Count > 1)
                    {
                        omissions.Add(new AutoDimensionOmission(row, AutoDimensionRules.CodeNoReference,
                            "family instance " + id + " exposes " + centres.Count + " CenterLeftRight references; " +
                            "which one is 'the centre' is not something this planner can decide. Name the " +
                            "reference explicitly with intent_dimension."));
                        continue;
                    }

                    Reference lifted = source.Lift(centres[0]);
                    string stable = lifted == null ? null : Stable(lifted, hostDoc);
                    if (stable == null)
                    {
                        omissions.Add(new AutoDimensionOmission(row, AutoDimensionRules.CodeNoReference,
                            "the centre reference of " + id + " has no usable host-document representation."));
                        continue;
                    }

                    XYZ point = null; XYZ direction = null;
                    var location = opening.Location as LocationPoint;
                    if (location != null) point = source.ToHost.OfPoint(location.Point);
                    if (point == null)
                    {
                        BoundingBoxXYZ box = null;
                        try { box = opening.get_BoundingBox(null); } catch { box = null; }
                        if (box != null)
                            point = source.ToHost.OfPoint((box.Min + box.Max) * 0.5);
                    }
                    if (point == null)
                    {
                        omissions.Add(new AutoDimensionOmission(row, AutoDimensionRules.CodeUnreadable,
                            "opening " + id + " has neither a location point nor a readable bounding box, so " +
                            "where it sits along the chain could not be measured."));
                        continue;
                    }

                    // The centre plane faces along the family's own X; a chain of
                    // openings is grouped by that so two walls at right angles do not
                    // share one chain.
                    try
                    {
                        Transform t = opening.GetTotalTransform();
                        if (t != null) direction = source.ToHost.OfVector(t.BasisY);
                    }
                    catch { direction = null; }

                    row.StableRepresentation = stable;
                    Project(row, point, direction, right, up, origin);
                    result.Add(row);
                }
                catch (Exception ex)
                {
                    omissions.Add(new AutoDimensionOmission(row, AutoDimensionRules.CodeUnreadable,
                        "opening " + id + " could not be read: " + ex.Message));
                }
            }
            return result;
        }

        private static bool IsOpeningCategory(FamilyInstance fi)
        {
            try
            {
                if (fi.Category == null) return false;
                long id = Rid.Value(fi.Category.Id);
                return id == (long)BuiltInCategory.OST_Doors || id == (long)BuiltInCategory.OST_Windows ||
                       id == (long)BuiltInCategory.OST_GenericModel && fi.Host != null;
            }
            catch { return false; }
        }

        // =====================================================================
        // Shared helpers
        // =====================================================================

        /// <summary>
        /// The elements one collection is about. Explicit ids win; otherwise it is
        /// everything of that class VISIBLE in the view - and for a LINKED source the
        /// view filter cannot apply, because FilteredElementCollector's view overload
        /// takes a view in the same document. The linked sweep is the whole linked
        /// document, which the caller is told about in the response.
        /// </summary>
        private static IEnumerable<T> Candidates<T>(Document doc, View view, bool linked, IList<long> explicitIds)
            where T : Element
        {
            if (explicitIds != null && explicitIds.Count > 0)
            {
                foreach (long id in explicitIds)
                {
                    if (!Rid.CanRepresent(id)) continue;
                    Element e = null;
                    try { e = doc.GetElement(Rid.Make(id)); } catch { e = null; }
                    var typed = e as T;
                    if (typed != null) yield return typed;
                }
                yield break;
            }

            FilteredElementCollector collector = linked
                ? new FilteredElementCollector(doc)
                : new FilteredElementCollector(doc, view.Id);
            foreach (Element e in collector.OfClass(typeof(T)).WhereElementIsNotElementType())
            {
                var typed = e as T;
                if (typed != null) yield return typed;
            }
        }

        /// <summary>
        /// Project one candidate into the view plane. Both the point and (where there
        /// is one) the direction are expressed in the view's right/up frame, which is
        /// the frame every rule in Core reasons about - one conversion here, none
        /// scattered through the decisions.
        /// </summary>
        private static void Project(AutoDimensionCandidate candidate, XYZ point, XYZ direction,
                                     XYZ right, XYZ up, XYZ origin)
        {
            XYZ delta = point.Subtract(origin);
            candidate.X = delta.DotProduct(right);
            candidate.Y = delta.DotProduct(up);
            if (direction == null) return;
            double dx = direction.DotProduct(right), dy = direction.DotProduct(up);
            double length = Math.Sqrt(dx * dx + dy * dy);
            // A direction perpendicular to the view plane projects to nothing. Leaving
            // it null is right: the rules then report the candidate as ungroupable
            // rather than grouping it by a direction that is numerical noise.
            if (double.IsNaN(length) || length < 1e-9) return;
            candidate.DirectionX = dx / length;
            candidate.DirectionY = dy / length;
        }

        private static string Stable(Reference reference, Document hostDoc)
        {
            try { return reference.ConvertToStableRepresentation(hostDoc); }
            catch { return null; }
        }

        private static string SafeName(Element element)
        { try { return element.Name; } catch { return null; } }

        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }

        // =====================================================================
        // Existing chains, for duplicate detection
        // =====================================================================

        /// <summary>
        /// The unordered identity of every dimension already living in this view. A
        /// re-run of the same plan must not double every dimension on the sheet, and
        /// "already there" is decided by the reference SET rather than by position,
        /// because a dimension somebody nudged is still that dimension.
        /// </summary>
        public static HashSet<string> ExistingChainIdentities(Document hostDoc, View view)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dimension d in new FilteredElementCollector(hostDoc, view.Id)
                                    .OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                try
                {
                    ReferenceArray refs = d.References;
                    if (refs == null || refs.Size == 0) continue;
                    var reps = new List<string>();
                    bool complete = true;
                    foreach (Reference r in refs)
                    {
                        string rep = CanonicalReference(hostDoc, r);
                        if (rep == null) { complete = false; break; }
                        reps.Add(rep);
                    }
                    // A dimension whose references could not all be serialised is not
                    // added: a PARTIAL identity would match nothing and could match the
                    // wrong thing, and neither is worth having.
                    if (complete && reps.Count > 0)
                        result.Add(AutoDimensionRules.ChainIdentityUnordered(reps));
                }
                catch { /* one unreadable dimension must not blind the whole check */ }
            }
            return result;
        }

        /// <summary>
        /// The identity of one reference FOR DUPLICATE DETECTION - and the reason it is
        /// not simply the stable representation is MEASURED, not stylistic. On live
        /// Revit 2026 (2026-08-26), `new Reference(grid)` serialises as the bare unique
        /// id while the SAME reference read back off a committed dimension serialises
        /// as `<uid>:0:SURFACE`, and parse-and-reserialize does not unify them. A datum
        /// has exactly one dimensionable plane, so every HOST reference whose owner is
        /// a DatumPlane collapses to that owner's identity, matching the planner's
        /// DedupIdentity - and a chain committed a minute ago is recognised however
        /// Revit respelt its references. Non-datum references keep their stable
        /// representation: two faces of one wall must stay distinct.
        /// </summary>
        internal static string CanonicalReference(Document hostDoc, Reference r)
        {
            if (r == null) return null;
            try
            {
                bool linked = false;
                try { linked = r.LinkedElementId != null && r.LinkedElementId != ElementId.InvalidElementId; }
                catch { linked = false; }
                if (!linked)
                {
                    Element owner = null;
                    try { owner = hostDoc.GetElement(r); } catch { owner = null; }
                    if (owner is DatumPlane)
                    {
                        string uid = null;
                        try { uid = owner.UniqueId; } catch { uid = null; }
                        if (uid != null) return "datum:" + uid;
                    }
                }
                return Stable(r, hostDoc);
            }
            catch { return null; }
        }

        // =====================================================================
        // Serialisation
        // =====================================================================

        public static JObject CandidateJson(AutoDimensionCandidate c, double fromFeet)
        {
            return new JObject
            {
                ["subject_id"] = c.SubjectId,
                ["link_instance_id"] = c.LinkInstanceId.HasValue
                    ? (JToken)new JValue(c.LinkInstanceId.Value) : JValue.CreateNull(),
                ["linked"] = c.LinkInstanceId.HasValue,
                ["source"] = c.Source,
                ["label"] = c.Label,
                ["stable_representation"] = c.StableRepresentation,
                ["view_position"] = new JArray(c.X * fromFeet, c.Y * fromFeet),
                ["view_direction"] = c.DirectionX.HasValue
                    ? (JToken)new JArray(c.DirectionX.Value, c.DirectionY.Value) : JValue.CreateNull()
            };
        }

        public static JObject OmissionJson(AutoDimensionOmission o, double fromFeet)
        {
            JObject row = CandidateJson(o.Candidate, fromFeet);
            row["code"] = o.Code;
            row["reason"] = o.Reason;
            return row;
        }
    }
}
