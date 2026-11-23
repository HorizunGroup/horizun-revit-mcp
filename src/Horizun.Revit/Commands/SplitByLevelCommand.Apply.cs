using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class SplitByLevelCommand
    {
        private static void SplitWall(Document doc, Wall source, List<Interval> intervals,
            List<ElementId> created, List<ElementId> inserts)
        {
            Curve axis = ((LocationCurve)source.Location).Curve;
            var oldInserts = source.FindInserts(true, true, true, true)
                .Select(id => (FamilyInstance)doc.GetElement(id)).ToList();
            var insertTypes = oldInserts.Select(fi => fi.Symbol.Id).ToList();
            var pieces = new List<Wall>();
            foreach (Interval part in intervals)
            {
                Wall piece = Wall.Create(doc, axis, source.GetTypeId(), part.Level.Id,
                    part.Top - part.Bottom, part.Bottom - part.Level.ProjectElevation,
                    source.Flipped, source.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1);
                if (piece == null) throw new InvalidOperationException("Revit returned no wall for one interval.");
                Level topLevel = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => Math.Abs(l.ProjectElevation - part.Top) < 1e-4);
                if (topLevel != null) Set(piece, BuiltInParameter.WALL_HEIGHT_TYPE, topLevel.Id);
                CopyIdentityParameters(source, piece);
                pieces.Add(piece); created.Add(piece.Id);
            }
            doc.Regenerate();
            foreach (FamilyInstance old in oldInserts)
            {
                XYZ point = ((LocationPoint)old.Location).Point;
                int index = intervals.FindIndex(p => point.Z >= p.Bottom - 0.05 && point.Z <= p.Top + 0.05);
                if (index < 0) throw new InvalidOperationException("A door/window lies outside all wall intervals.");
                FamilyInstance next = doc.Create.NewFamilyInstance(point, old.Symbol, pieces[index],
                    intervals[index].Level, StructuralType.NonStructural);
                if (next == null) throw new InvalidOperationException("A hosted door/window was not recreated.");
                if (next.HandFlipped != old.HandFlipped && !next.flipHand())
                    throw new InvalidOperationException("A door/window hand could not be preserved.");
                if (next.FacingFlipped != old.FacingFlipped && !next.flipFacing())
                    throw new InvalidOperationException("A door/window facing could not be preserved.");
                CopyIdentityParameters(old, next);
                inserts.Add(next.Id);
            }
            doc.Delete(source.Id);
            doc.Regenerate();
            for (int i = 0; i < inserts.Count; i++)
            {
                var next = doc.GetElement(inserts[i]) as FamilyInstance;
                if (next == null || !pieces.Any(w => w.Id == next.Host?.Id) ||
                    next.Symbol.Id != insertTypes[i])
                    throw new InvalidOperationException("A recreated door/window lost its host or type.");
            }
        }

        private static void CopyIdentityParameters(Element before, Element after)
        {
            foreach (Parameter source in before.Parameters)
            {
                string name = source.Definition?.Name;
                // Comments by its BuiltInParameter: its visible name follows Revit's language.
                bool comments = (source.Definition as InternalDefinition)?.BuiltInParameter == BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS;
                if (name == null || !(name.StartsWith("HZ_", StringComparison.Ordinal) || comments)) continue;
                Parameter target = comments ? after.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) : after.LookupParameter(name);
                if (target == null || target.IsReadOnly || source.StorageType != target.StorageType || !source.HasValue) continue;
                switch (source.StorageType)
                {
                    case StorageType.String: target.Set(source.AsString() ?? ""); break;
                    case StorageType.Double: target.Set(source.AsDouble()); break;
                    case StorageType.Integer: target.Set(source.AsInteger()); break;
                    case StorageType.ElementId: target.Set(source.AsElementId()); break;
                }
                bool equal = source.StorageType == StorageType.String ? target.AsString() == source.AsString() :
                    source.StorageType == StorageType.Double ? Math.Abs(target.AsDouble() - source.AsDouble()) < 1e-6 :
                    source.StorageType == StorageType.Integer ? target.AsInteger() == source.AsInteger() :
                    source.StorageType == StorageType.ElementId && target.AsElementId() == source.AsElementId();
                if (!equal) throw new InvalidOperationException(name + " was not copied and re-read.");
            }
        }

        private static void SplitColumn(Document doc, FamilyInstance source, List<Interval> intervals,
            List<ElementId> created)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation).ToList();
            foreach (Interval part in intervals)
            {
                ElementId id = ElementTransformUtils.CopyElement(doc, source.Id, XYZ.Zero).Single();
                var piece = doc.GetElement(id) as FamilyInstance;
                if (piece == null) throw new InvalidOperationException("Column copy did not produce a family instance.");
                Level topLevel = levels.FirstOrDefault(l => l.ProjectElevation >= part.Top - 0.05);
                if (topLevel == null || topLevel.Id == part.Level.Id)
                    topLevel = levels.FirstOrDefault(l => l.ProjectElevation > part.Level.ProjectElevation + 0.05);
                if (topLevel == null) throw new InvalidOperationException("No upper level can constrain the column piece.");
                Set(piece, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, topLevel.Id);
                Set(piece, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, part.Level.Id);
                Set(piece, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM,
                    part.Bottom - part.Level.ProjectElevation);
                Set(piece, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM,
                    part.Top - topLevel.ProjectElevation);
                created.Add(id);
            }
            doc.Delete(source.Id);
        }

        private static void Set(Element e, BuiltInParameter p, ElementId value)
        {
            Parameter parameter = e.get_Parameter(p);
            if (parameter == null || parameter.IsReadOnly || !parameter.Set(value) || parameter.AsElementId() != value)
                throw new InvalidOperationException(p + " was not set and re-read.");
        }
        private static void Set(Element e, BuiltInParameter p, double value)
        {
            Parameter parameter = e.get_Parameter(p);
            if (parameter == null || parameter.IsReadOnly || !parameter.Set(value) ||
                Math.Abs(parameter.AsDouble() - value) > 1e-5)
                throw new InvalidOperationException(p + " was not set and re-read.");
        }

        private static void SplitMep(Document doc, MEPCurve source, List<Interval> intervals,
            List<ElementId> created)
        {
            Line line = (Line)((LocationCurve)source.Location).Curve;
            XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
            XYZ bottomPoint = a.Z <= b.Z ? a : b;
            XYZ topPoint = a.Z > b.Z ? a : b;
            var oldEnds = source.ConnectorManager.Connectors.Cast<Connector>()
                .OrderBy(c => c.Origin.Z).ToList();
            var externals = oldEnds.Select(c => c.AllRefs.Cast<Connector>()
                .Where(r => r.Owner.Id != source.Id).ToList()).ToList();
            var pieces = new List<MEPCurve>();
            foreach (Interval part in intervals)
            {
                XYZ start = new XYZ(bottomPoint.X, bottomPoint.Y, part.Bottom);
                XYZ end = new XYZ(topPoint.X, topPoint.Y, part.Top);
                MEPCurve piece;
                if (source is Pipe pipe)
                {
                    ElementId system = pipe.MEPSystem?.GetTypeId() ??
                        pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
                    if (system == null || system == ElementId.InvalidElementId)
                        throw new InvalidOperationException("Pipe system type is unreadable.");
                    piece = Pipe.Create(doc, system, pipe.GetTypeId(), part.Level.Id, start, end);
                }
                else if (source is Duct duct)
                {
                    ElementId system = duct.MEPSystem?.GetTypeId() ??
                        duct.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM)?.AsElementId();
                    if (system == null || system == ElementId.InvalidElementId)
                        throw new InvalidOperationException("Duct system type is unreadable.");
                    piece = Duct.Create(doc, system, duct.GetTypeId(), part.Level.Id, start, end);
                }
                else if (source is Conduit conduit)
                    piece = Conduit.Create(doc, conduit.GetTypeId(), start, end, part.Level.Id);
                else if (source is CableTray tray)
                    piece = CableTray.Create(doc, tray.GetTypeId(), start, end, part.Level.Id);
                else throw new InvalidOperationException("Unsupported MEP run.");
                foreach (BuiltInParameter p in SectionParameters(source))
                {
                    Parameter from = source.get_Parameter(p), to = piece.get_Parameter(p);
                    if (from == null || to == null || to.IsReadOnly || !from.HasValue) continue;
                    to.Set(from.AsDouble());
                    if (Math.Abs(to.AsDouble() - from.AsDouble()) > 1e-5)
                        throw new InvalidOperationException("MEP section was not preserved.");
                }
                CopyIdentityParameters(source, piece);
                pieces.Add(piece); created.Add(piece.Id);
            }
            doc.Delete(source.Id);
            doc.Regenerate();
            for (int i = 0; i < pieces.Count - 1; i++)
            {
                Connector upper = End(pieces[i], true), lower = End(pieces[i + 1], false);
                upper.ConnectTo(lower);
                if (!upper.IsConnectedTo(lower)) throw new InvalidOperationException("Adjacent MEP pieces are not connected.");
            }
            for (int side = 0; side < 2; side++)
            {
                Connector end = End(side == 0 ? pieces[0] : pieces[pieces.Count - 1], side == 1);
                foreach (Connector external in externals[side])
                {
                    end.ConnectTo(external);
                    if (!end.IsConnectedTo(external))
                        throw new InvalidOperationException("An existing external connector was not restored.");
                }
            }
        }

        private static Connector End(MEPCurve run, bool upper) => run.ConnectorManager.Connectors
            .Cast<Connector>().OrderBy(c => c.Origin.Z).ElementAt(upper ? 1 : 0);

        private static IEnumerable<BuiltInParameter> SectionParameters(MEPCurve run)
        {
            if (run is Pipe) return new[] { BuiltInParameter.RBS_PIPE_DIAMETER_PARAM };
            if (run is Conduit) return new[] { BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM };
            if (run is CableTray) return new[] { BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM };
            return new[] { BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, BuiltInParameter.RBS_CURVE_WIDTH_PARAM,
                BuiltInParameter.RBS_CURVE_HEIGHT_PARAM };
        }
    }
}
