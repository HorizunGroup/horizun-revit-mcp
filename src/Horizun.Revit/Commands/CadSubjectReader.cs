// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ONE PLACE THAT TURNS AN ELEMENT INTO A SUBJECT.
//
// The audit and the incremental update both ask the same question of the model -
// "what is this element, and where" - and both used to answer it with their own
// copy of the reading. The copies diverged, silently and in the direction that
// matters: the update's copy never read the element's TYPE, so a classification
// that compares the drawing's requested type against the element's own could
// never fire through the command that needs it most. It fired in tests, because
// the tests build subjects by hand.
//
// So the reading lives here, once. A fact the audit can see is a fact the update
// can see, and a fact added for one is available to the other by construction.
//
// The DECISIONS stay where they were - in Core, Revit-free, provable at a desk.
// This file is only the measurement, and it is the half that needs a Document.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class CadSubjectReader
    {
        /// <summary>
        /// Everything a CAD decision can ask about an element, read once and
        /// guarded individually: a category that will not answer must not cost
        /// the geometry, and an element that answers nothing is still a subject -
        /// its existence is the fact the audit is about.
        /// </summary>
        public static CadAuditSubject Measure(Element e) => Measure(e, null);

        /// <summary>
        /// <paramref name="wantedParameters"/> is the set of parameter names some
        /// rule actually asked about. Only those are read: sweeping every
        /// parameter of every element would cost more than the audit and answer
        /// a question nobody asked.
        /// </summary>
        public static CadAuditSubject Measure(Element e, IEnumerable<string> wantedParameters)
        {
            var s = new CadAuditSubject { ElementId = Rid.Value(e.Id) };
            try { s.Category = e.Category?.Name; } catch { }
            try { s.TypeName = (e.Document.GetElement(e.GetTypeId()) as ElementType)?.Name; } catch { }
            try { s.LevelName = (e.Document.GetElement(e.LevelId) as Level)?.Name; } catch { }

            try
            {
                var curve = e.Location as LocationCurve;
                if (curve?.Curve != null)
                {
                    s.Geometry.Add(Mm(curve.Curve.GetEndPoint(0)));
                    s.Geometry.Add(Mm(curve.Curve.GetEndPoint(1)));

                    // ITS CURVATURE, when it has any. Two endpoints do not identify
                    // an arc: a minor and a major arc of one chord share both.
                    var arc = curve.Curve as Arc;
                    if (arc != null)
                    {
                        s.ArcCentre = Mm(arc.Center);
                        s.ArcRadiusMm = CadUnits.FeetToMm(arc.Radius);
                    }
                }
                else
                {
                    var point = e.Location as LocationPoint;
                    if (point?.Point != null) s.Geometry.Add(Mm(point.Point));
                }
            }
            catch { }

            s.WidthMm = WidthOf(e);

            // WHAT IT IS CALLED. Only the kinds that carry an identity of their
            // own answer: a wall's name is its TYPE's name and is not a
            // per-instance thing, so reading it here would invite a finding about
            // a name nobody set.
            try
            {
                if (e is Room room)
                {
                    s.ElementName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                    s.ElementNumber = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();
                }
                else if (e is Grid || e is Level)
                {
                    s.ElementName = e.Name;
                }
            }
            catch { }

            ReadWantedParameters(e, wantedParameters, s);
            try
            {
                Element host = (e as FamilyInstance)?.Host;
                if (host != null) s.HostElementId = Rid.Value(host.Id);
            }
            catch { }
            return s;
        }

        /// <summary>
        /// Read back the parameters a rule named, and NOTHING else.
        ///
        /// A parameter the element does not carry is absent from the map, which
        /// the audit reports as missing. One that exists and will not answer goes
        /// in ParametersUnreadable - unreadable is its own finding and is never
        /// allowed to read as agreement.
        /// </summary>
        private static void ReadWantedParameters(Element e, IEnumerable<string> wanted, CadAuditSubject s)
        {
            if (e == null || wanted == null) return;
            foreach (string name in wanted)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                try
                {
                    Parameter p = e.LookupParameter(name);
                    if (p == null)
                    {
                        // Not on the instance: a rule may be writing the TYPE, and
                        // an audit that only looked at the instance would report
                        // every type parameter as missing.
                        Element type = null;
                        try { type = e.Document?.GetElement(e.GetTypeId()); } catch { }
                        if (type != null) p = type.LookupParameter(name);
                    }
                    if (p == null) continue;

                    string value = p.StorageType == StorageType.String
                        ? p.AsString()
                        : p.AsValueString() ?? p.AsString();
                    if (value == null) { s.ParametersUnreadable.Add(name); continue; }
                    s.ParameterValues[name] = value;
                }
                catch { s.ParametersUnreadable.Add(name); }
            }
        }

        /// <summary>
        /// HOW WIDE THE THING IS, in millimetres, or null when the question does
        /// not apply to it.
        ///
        /// Null is a real answer and must not collapse into zero: a drawing that
        /// asks for a 200 mm wall and an element nobody can measure is not the
        /// same finding as a drawing that asks for 200 and gets 150. The first is
        /// "not comparable", the second is a wall of the wrong thickness.
        ///
        /// Each family is asked in its own terms - a wall has a Width, a round
        /// duct has a Diameter and a rectangular one has a Width - because the
        /// generic parameter lookup that would cover them all returns whichever
        /// of those happens to exist and cannot say which it found.
        /// </summary>
        private static double? WidthOf(Element e)
        {
            try
            {
                var wall = e as Wall;
                if (wall != null) return CadUnits.FeetToMm(wall.Width);

                var pipe = e as Pipe;
                if (pipe != null) return DiameterMm(pipe);

                var duct = e as Duct;
                if (duct != null)
                {
                    double? round = DiameterMm(duct);
                    if (round.HasValue) return round;
                    Parameter w = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                    if (w != null && w.StorageType == StorageType.Double) return CadUnits.FeetToMm(w.AsDouble());
                    return null;
                }
            }
            catch { }
            return null;
        }

        private static double? DiameterMm(Element e)
        {
            try
            {
                Parameter d = e.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                if (d == null || d.StorageType != StorageType.Double) return null;
                double feet = d.AsDouble();
                return feet > 0 ? (double?)CadUnits.FeetToMm(feet) : null;
            }
            catch { return null; }
        }

        private static CadPoint Mm(XYZ p) =>
            new CadPoint(CadUnits.FeetToMm(p.X), CadUnits.FeetToMm(p.Y), CadUnits.FeetToMm(p.Z));
    }
}
