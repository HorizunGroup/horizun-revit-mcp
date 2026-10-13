using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    /// <summary>
    /// ACERO 3.0, stage 1c: what Revit holds each handle of a bar with, READ ONLY (horizun_query_structure
    /// mode=rebar include_constraints). Per handle: the constraint Revit applies now and the one preferred,
    /// its type, the element it targets, the face kind and plane, and the distance it holds. Every accessor
    /// is wrapped: a constraint kind that does not own a distance throws, and a throw is reported as null.
    /// </summary>
    internal static class RebarConstraintRead
    {
        private const double FtToMm = 304.8;

        internal static JObject Read(Document doc, Rebar bar)
        {
            var o = new JObject();
            long hostId = -1;
            try { hostId = Rid.Value(bar.GetHostId()); } catch { }
            RebarConstraintsManager cm;
            IList<RebarConstrainedHandle> handles;
            try { cm = bar.GetRebarConstraintsManager(); handles = cm.GetAllHandles(); }
            catch (Exception ex)
            {
                o["readable"] = false;
                o["why"] = "Revit would not give this bar's constraints manager: " + ex.Message;
                return o;
            }
            var rows = new JArray();
            var typed = new List<RebarConstraintRules.Handle>();
            foreach (RebarConstrainedHandle h in handles)
            {
                string name = null;
                try { name = h.GetHandleName(); } catch { }
                var row = new JObject { ["name"] = name };
                try { row["handle_type"] = h.GetHandleType().ToString(); } catch { row["handle_type"] = null; }
                try { row["edge_number"] = h.GetHandleType() == RebarHandleType.Edge ? (JToken)h.GetEdgeNumber() : JValue.CreateNull(); }
                catch { row["edge_number"] = null; }
                RebarConstraint current = null, preferred = null;
                try { current = cm.GetCurrentConstraintOnHandle(h); } catch { }
                try { preferred = cm.GetPreferredConstraintOnHandle(h); } catch { }
                RebarConstraintRules.Handle t = Typed(name, current);
                typed.Add(t);
                row["current"] = Describe(current, t, hostId);
                row["preferred"] = preferred == null ? JValue.CreateNull() : Describe(preferred, Typed(name, preferred), hostId);
                rows.Add(row);
            }
            RebarConstraintRules.Summary s = RebarConstraintRules.Summarise(hostId, typed);
            o["readable"] = true;
            o["host_id"] = hostId;
            o["handles"] = rows;
            o["summary"] = new JObject
            {
                ["handles"] = s.Handles,
                ["constrained"] = s.Constrained,
                ["to_cover"] = s.ToCover,
                ["unconstrained"] = new JArray(s.Unconstrained),
                ["targets_other_than_host"] = new JArray(s.OtherTargets),
                ["handles_on_other_targets"] = new JArray(s.HandlesOnOtherTargets)
            };
            o["means"] = "the constraint Revit applies to each handle NOW (current) and the one set as preferred, read " +
                         "from the bar's RebarConstraintsManager. A handle on the cover of another element than the host " +
                         "is how Revit moves a bar off its plan to that element's cover; hold_ends re-pins such handles.";
            return o;
        }

        /// <summary>
        /// The handles' summary alone, for the apply's geometry check (stage 1c): how many handles Revit holds to a
        /// cover, and which to another element than the host - the evidence that names hold_ends. Null when Revit
        /// would not give the constraints manager; the verdict does not depend on it.
        /// </summary>
        internal static RebarConstraintRules.Summary Summary(Rebar bar)
        {
            try
            {
                long hostId = -1;
                try { hostId = Rid.Value(bar.GetHostId()); } catch { }
                RebarConstraintsManager cm = bar.GetRebarConstraintsManager();
                var typed = new List<RebarConstraintRules.Handle>();
                foreach (RebarConstrainedHandle h in cm.GetAllHandles())
                {
                    string name = null;
                    try { name = h.GetHandleName(); } catch { }
                    RebarConstraint current = null;
                    try { current = cm.GetCurrentConstraintOnHandle(h); } catch { }
                    typed.Add(Typed(name, current));
                }
                return RebarConstraintRules.Summarise(hostId, typed);
            }
            catch { return null; }
        }

        private static RebarConstraintRules.Handle Typed(string name, RebarConstraint c)
        {
            var t = new RebarConstraintRules.Handle { Name = name };
            if (c == null) return t;
            try { t.ConstraintType = c.GetConstraintType().ToString(); } catch { }
            try { Element e = c.GetTargetElement(); if (e != null) t.TargetElementId = Rid.Value(e.Id); } catch { }
            try { t.ToCover = c.IsToCover(); } catch { }
            try { t.DistanceToHostFaceMm = RebarConstraintRules.FeetToMm(c.GetDistanceToTargetHostFace()); } catch { }
            try { t.DistanceToCoverMm = RebarConstraintRules.FeetToMm(c.GetDistanceToTargetCover()); } catch { }
            return t;
        }

        private static JToken Describe(RebarConstraint c, RebarConstraintRules.Handle t, long hostId)
        {
            if (c == null) return JValue.CreateNull();
            var o = new JObject
            {
                ["type"] = t.ConstraintType,
                ["target_element_id"] = t.TargetElementId.HasValue ? (JToken)t.TargetElementId.Value : JValue.CreateNull(),
                ["target_is_host"] = t.TargetElementId.HasValue ? (JToken)(t.TargetElementId.Value == hostId) : JValue.CreateNull(),
                ["to_cover"] = t.ToCover,
                ["distance_mm"] = RebarConstraintRules.Distance(t)
            };
            try { o["number_of_targets"] = c.NumberOfTargets; } catch { o["number_of_targets"] = null; }
            try { o["target_face_kind"] = c.GetRebarConstraintTargetHostFaceType().ToString(); } catch { o["target_face_kind"] = null; }
            o["target_face"] = Face(c);
            return o;
        }

        // The target face's plane in model coordinates, when it is planar: which cover plane the handle sits on.
        private static JToken Face(RebarConstraint c)
        {
            try
            {
                var tr = Transform.Identity;
                Face f = c.GetTargetHostFaceAndTransform(0, tr);
                var pf = f as PlanarFace;
                if (pf == null) return f == null ? JValue.CreateNull() : new JObject { ["planar"] = false };
                return new JObject
                {
                    ["planar"] = true,
                    ["normal"] = RebarFacts.Xyz(tr.OfVector(pf.FaceNormal).Normalize(), 6),
                    ["point_mm"] = RebarFacts.Xyz(tr.OfPoint(pf.Origin), 3, FtToMm)
                };
            }
            catch { return JValue.CreateNull(); }
        }
    }
}
