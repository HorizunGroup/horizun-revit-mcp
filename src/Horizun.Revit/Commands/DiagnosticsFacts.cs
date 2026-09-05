// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE READING HALF of the diagnostics P0 slice. Everything here touches Revit
// and decides nothing: it fills the plain fact objects in Core/CoordinateRules,
// Core/DatumRules and Core/ReadinessRules, which are Revit-free precisely so the
// judgements can be proved at a desk.
//
// The split is not decoration. "Are these two levels the same level" is a
// question about two numbers and a tolerance, and a version of it that needs a
// Document to answer can only ever be tested by opening Revit.
//
// EVERY READ IS INDIVIDUALLY GUARDED. A model that will not report its survey
// point must still report its levels; one throw taking out the section would
// turn a partial answer into no answer, and a section that returns nothing is
// indistinguishable from a model with nothing in it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class DiagnosticsFacts
    {
        internal const double FtToMm = 304.8;

        // ------------------------------------------------------- coordinates

        internal static CoordinateFacts ReadCoordinates(Document doc, double farRadiusMm)
        {
            var f = new CoordinateFacts();

            // THE INTERNAL ORIGIN IS ALWAYS (0,0,0) BY DEFINITION. It is reported
            // anyway, because a reader comparing three points needs all three, and
            // leaving it out invites the assumption that the base point is the
            // origin - which is the confusion this whole area exists to remove.
            f.InternalOrigin = new PointFact
            {
                Name = "internal_origin", Readable = true, XMm = 0, YMm = 0, ZMm = 0,
                Why = "Revit's own origin. It is (0,0,0) by definition and cannot be moved."
            };

            f.ProjectBasePoint = ReadBasePoint(doc, BuiltInCategory.OST_ProjectBasePoint, "project_base_point");
            f.SurveyPoint = ReadBasePoint(doc, BuiltInCategory.OST_SharedBasePoint, "survey_point");

            try
            {
                ProjectLocation active = doc.ActiveProjectLocation;
                if (active == null)
                {
                    f.LocationReadable = false;
                    f.LocationWhy = "the document reports no active project location.";
                }
                else
                {
                    f.LocationReadable = true;
                    f.ActiveLocationName = active.Name;
                    ProjectPosition p = active.GetProjectPosition(XYZ.Zero);
                    if (p != null)
                    {
                        f.TrueNorthReadable = true;
                        f.TrueNorthDegrees = Math.Round(p.Angle * 180.0 / Math.PI, 6);
                    }
                    else f.TrueNorthReadable = false;
                }
            }
            catch (Exception ex)
            {
                f.LocationReadable = false;
                f.LocationWhy = ex.Message;
            }

            try
            {
                int n = 0;
                foreach (ProjectLocation unused in doc.ProjectLocations) n++;
                f.NamedLocationCount = n;
            }
            catch { f.NamedLocationCount = null; }

            // EACH FIELD GUARDED ON ITS OWN. A document that will not report a
            // time zone still knows its latitude, and one throw taking out the
            // whole site would report an unreadable planet.
            try
            {
                SiteLocation site = doc.SiteLocation;
                if (site == null)
                {
                    f.SiteReadable = false;
                    f.SiteWhy = "the document reports no site location.";
                }
                else
                {
                    f.SiteReadable = true;
                    // RADIANS TO DEGREES. The API answers in radians; reporting that
                    // number as degrees puts every project near the equator and the
                    // result still looks like a coordinate, so the bug survives review.
                    try { f.LatitudeDegrees = Math.Round(site.Latitude * 180.0 / Math.PI, 8); }
                    catch { f.LatitudeDegrees = null; }
                    try { f.LongitudeDegrees = Math.Round(site.Longitude * 180.0 / Math.PI, 8); }
                    catch { f.LongitudeDegrees = null; }
                    try { f.PlaceName = site.PlaceName; }
                    catch { f.PlaceName = null; }
                    try { f.TimeZoneHours = site.TimeZone; }
                    catch { f.TimeZoneHours = null; }
                }
            }
            catch (Exception ex)
            {
                f.SiteReadable = false;
                f.SiteWhy = ex.Message;
            }

            try
            {
                Units units = doc.GetUnits();
                FormatOptions fo = units.GetFormatOptions(SpecTypeId.Length);
                f.LengthUnitName = LabelUtils.GetLabelForUnit(fo.GetUnitTypeId());
                f.UnitsReadable = true;
            }
            catch (Exception ex)
            {
                f.UnitsReadable = false;
                f.LengthUnitName = null;
                if (string.IsNullOrEmpty(f.LocationWhy)) f.LocationWhy = ex.Message;
            }

            ReadOutliers(doc, f, farRadiusMm);
            ReadLinkPlacements(doc, f);
            return f;
        }

        private static PointFact ReadBasePoint(Document doc, BuiltInCategory category, string name)
        {
            var p = new PointFact { Name = name };
            try
            {
                BasePoint found = null;
                var collector = new FilteredElementCollector(doc)
                    .OfCategory(category).WhereElementIsNotElementType();
                foreach (Element e in collector) { found = e as BasePoint; if (found != null) break; }

                if (found == null)
                {
                    p.Readable = false;
                    p.Why = "the document reports no " + name.Replace('_', ' ') + ".";
                    return p;
                }

                // Position is in Revit internal feet, relative to the internal origin -
                // which is exactly the number this area needs.
                XYZ at = found.Position;
                p.Readable = true;
                p.XMm = at.X * FtToMm;
                p.YMm = at.Y * FtToMm;
                p.ZMm = at.Z * FtToMm;
                // THE CLIPPED STATE IS NOT READ, and null says so.
                //
                // There is no BuiltInParameter for it that compiles across
                // 2023-2027 - BASEPOINT_CLIPPED_PARAM does not exist in the 2026
                // API - and guessing a name would either fail to compile for one
                // year or silently read a different parameter. A clipped survey
                // point behaves differently under shared coordinates, so this is a
                // real gap; it is left as an admitted null rather than a fabricated
                // false, and it is in the backlog rather than in this comment only.
                p.Clipped = null;
            }
            catch (Exception ex)
            {
                p.Readable = false;
                p.Why = ex.Message;
            }
            return p;
        }

        /// <summary>
        /// How far the GEOMETRY sits from the internal origin. Deliberately walks
        /// element bounding boxes and never a control point - see CoordinateRules
        /// for why that distinction is the whole point of this check.
        /// </summary>
        private static void ReadOutliers(Document doc, CoordinateFacts f, double farRadiusMm)
        {
            var worst = new List<OutlierFact>();
            try
            {
                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WhereElementIsViewIndependent();
                foreach (Element e in collector)
                {
                    BoundingBoxXYZ box;
                    try { box = e.get_BoundingBox(null); }
                    catch { f.ElementsUnreadable++; continue; }
                    if (box == null) continue;      // no geometry is not an unreadable element

                    double d;
                    try
                    {
                        XYZ c = (box.Min + box.Max) / 2.0;
                        d = Math.Sqrt(c.X * c.X + c.Y * c.Y + c.Z * c.Z) * FtToMm;
                    }
                    catch { f.ElementsUnreadable++; continue; }

                    f.ElementsMeasured++;
                    if (!f.FarthestElementMm.HasValue || d > f.FarthestElementMm.Value) f.FarthestElementMm = d;
                    if (d <= farRadiusMm) continue;

                    worst.Add(new OutlierFact
                    {
                        ElementId = Rid.Value(e.Id),
                        Category = SafeCategory(e),
                        Name = SafeName(e),
                        DistanceMm = Math.Round(d, 1)
                    });
                }
            }
            catch { /* the collector itself failed; what was gathered still stands */ }

            // THE FULL LIST, FARTHEST FIRST. The caller truncates for display and
            // reports `total` from this list's length, which is what keeps the count
            // the model's number rather than the page size.
            worst.Sort((a, b) => b.DistanceMm.CompareTo(a.DistanceMm));
            f.Outliers = worst;
        }

        private static void ReadLinkPlacements(Document doc, CoordinateFacts f)
        {
            try
            {
                var collector = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance));
                foreach (Element e in collector)
                {
                    var link = e as RevitLinkInstance;
                    if (link == null) continue;
                    var fact = new LinkPlacementFact { InstanceId = Rid.Value(e.Id), Name = SafeName(e) };
                    try
                    {
                        Transform t = link.GetTotalTransform();
                        fact.TransformReadable = true;
                        fact.OriginOffsetMm = Math.Round(t.Origin.GetLength() * FtToMm, 3);
                        // A reflection is a negative determinant. It is almost never
                        // intentional and it turns every text in the link backwards.
                        fact.HasReflection = t.Determinant < 0;
                        fact.HasRotation = !t.BasisX.IsAlmostEqualTo(XYZ.BasisX) ||
                                           !t.BasisY.IsAlmostEqualTo(XYZ.BasisY);
                    }
                    catch (Exception ex)
                    {
                        fact.TransformReadable = false;
                        fact.Why = ex.Message;
                    }
                    // Whether it shares the host's position is a question this read
                    // does not answer yet, and null says so rather than false.
                    fact.SharedPositionMatchesHost = null;
                    f.Links.Add(fact);
                }
            }
            catch { }
        }

        // ------------------------------------------- level association

        /// <summary>
        /// Walks the model elements and asks each one which level it is on.
        ///
        /// THE POPULATION IS PUBLISHED, NOT ASSUMED: model-category elements that
        /// are not types and not view-specific. Nothing is quietly dropped for
        /// "not needing a level" - a hidden exclusion list is one organisation's
        /// opinion, and the per-category breakdown shows a reader that Levels
        /// themselves report no level far more clearly than an omission would.
        ///
        /// The answer is Element.LevelId, which is Revit's own consolidated view
        /// of the association. A read that THROWS is counted apart: it is neither
        /// associated nor unassociated, and folding it into either invents a fact.
        /// </summary>
        internal static LevelAssociationFacts ReadLevelAssociation(Document doc)
        {
            var f = new LevelAssociationFacts();
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                string categoryName;
                try
                {
                    Category cat = e.Category;
                    if (cat == null) continue;
                    if (cat.CategoryType != CategoryType.Model) continue;
                    if (e.ViewSpecific) continue;
                    categoryName = cat.Name;
                }
                catch
                {
                    // Could not even decide whether this element belongs to the
                    // population, so it is not in it. Counting it as unreadable
                    // would put it in a denominator it was never admitted to.
                    continue;
                }

                f.Examined++;
                try
                {
                    ElementId lid = e.LevelId;
                    if (lid == null || lid == ElementId.InvalidElementId)
                    {
                        f.WithoutLevel++;
                        long had;
                        f.WithoutByCategory[categoryName] =
                            f.WithoutByCategory.TryGetValue(categoryName, out had) ? had + 1 : 1;
                        f.Unassociated.Add(new UnassociatedElement
                        {
                            ElementId = Rid.Value(e.Id),
                            Category = categoryName,
                            Name = SafeName(e)
                        });
                    }
                    else
                    {
                        f.WithLevel++;
                        long key = Rid.Value(lid), n;
                        f.CountByLevel[key] = f.CountByLevel.TryGetValue(key, out n) ? n + 1 : 1;
                    }
                }
                catch { f.Unreadable++; }
            }
            return f;
        }

        // ------------------------------------------------------------ datums

        internal static List<LevelFact> ReadLevels(Document doc, out long unreadable)
        {
            unreadable = 0;
            var levels = new List<LevelFact>();
            var viewsPerLevel = new Dictionary<long, int>();
            try
            {
                foreach (Element v in new FilteredElementCollector(doc).OfClass(typeof(View)))
                {
                    var view = v as View;
                    if (view == null || view.IsTemplate) continue;
                    try
                    {
                        ElementId lid = view.GenLevel != null ? view.GenLevel.Id : ElementId.InvalidElementId;
                        if (lid == ElementId.InvalidElementId) continue;
                        long key = Rid.Value(lid);
                        viewsPerLevel[key] = viewsPerLevel.ContainsKey(key) ? viewsPerLevel[key] + 1 : 1;
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Level)))
                {
                    var level = e as Level;
                    if (level == null) continue;
                    var fact = new LevelFact { ElementId = Rid.Value(e.Id) };
                    try { fact.Name = level.Name; fact.NameReadable = true; }
                    catch { fact.NameReadable = false; unreadable++; }
                    try { fact.ElevationMm = Math.Round(level.Elevation * FtToMm, 4); }
                    catch { fact.ElevationMm = null; }
                    try { fact.ProjectElevationMm = Math.Round(level.ProjectElevation * FtToMm, 4); }
                    catch { fact.ProjectElevationMm = null; }
                    try
                    {
                        Parameter story = level.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);
                        fact.IsBuildingStory = story == null ? (bool?)null : story.AsInteger() != 0;
                    }
                    catch { fact.IsBuildingStory = null; }
                    int views;
                    fact.ViewCount = viewsPerLevel.TryGetValue(fact.ElementId, out views) ? views : 0;
                    levels.Add(fact);
                }
            }
            catch { }
            return levels;
        }

        internal static List<GridFact> ReadGrids(Document doc, out long unreadable)
        {
            unreadable = 0;
            var grids = new List<GridFact>();
            try
            {
                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Grid)))
                {
                    var grid = e as Grid;
                    if (grid == null) continue;
                    var fact = new GridFact { ElementId = Rid.Value(e.Id) };
                    try { fact.Name = grid.Name; fact.NameReadable = true; }
                    catch { fact.NameReadable = false; }
                    try
                    {
                        Curve c = grid.Curve;
                        if (c == null)
                        {
                            fact.GeometryReadable = false;
                            fact.Why = "the grid reports no curve.";
                            unreadable++;
                        }
                        else if (c is Line)
                        {
                            XYZ a = c.GetEndPoint(0), b = c.GetEndPoint(1);
                            fact.GeometryReadable = true;
                            fact.X1Mm = a.X * FtToMm; fact.Y1Mm = a.Y * FtToMm;
                            fact.X2Mm = b.X * FtToMm; fact.Y2Mm = b.Y * FtToMm;
                        }
                        else
                        {
                            // A CURVED GRID IS NOT COMPARED, and is reported as such
                            // rather than as clear. Two arcs on top of each other are a
                            // real defect this slice does not detect, and saying so is
                            // better than a silent pass.
                            fact.GeometryReadable = true;
                            fact.IsCurved = true;
                            fact.Why = "a curved grid: this check compares straight grids only, so it is not " +
                                       "evaluated rather than found clear.";
                        }
                    }
                    catch (Exception ex)
                    {
                        fact.GeometryReadable = false;
                        fact.Why = ex.Message;
                        unreadable++;
                    }
                    grids.Add(fact);
                }
            }
            catch { }
            return grids;
        }

        /// <summary>How many elements report each level as theirs. Null everywhere when it cannot be walked.</summary>
        internal static void CountElementsPerLevel(Document doc, List<LevelFact> levels, out long noLevel,
                                                   out long unreadable)
        {
            noLevel = 0; unreadable = 0;
            var counts = new Dictionary<long, long>();
            try
            {
                foreach (Element e in new FilteredElementCollector(doc)
                             .WhereElementIsNotElementType().WhereElementIsViewIndependent())
                {
                    try
                    {
                        ElementId lid = e.LevelId;
                        if (lid == null || lid == ElementId.InvalidElementId) { noLevel++; continue; }
                        long key = Rid.Value(lid);
                        counts[key] = counts.ContainsKey(key) ? counts[key] + 1 : 1;
                    }
                    catch { unreadable++; }
                }
            }
            catch
            {
                // NOT ZERO. Leaving ElementCount null is what makes the rules report
                // "not measured" instead of "no elements on this level".
                return;
            }
            foreach (LevelFact l in levels)
            {
                long n;
                l.ElementCount = counts.TryGetValue(l.ElementId, out n) ? n : 0;
            }
        }

        // ----------------------------------------------------------- helpers

        internal static string SafeName(Element e)
        {
            try { return e.Name; } catch { return null; }
        }

        internal static string SafeCategory(Element e)
        {
            try { return e.Category != null ? e.Category.Name : null; } catch { return null; }
        }
    }
}
