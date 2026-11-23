using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class WallFinishCommand
    {
        private sealed class PaintFacePlan
        {
            public long ElementId;
            public string Reference;
            public string Action;
            public long? Material;
            public bool BeforePainted;
            public long? BeforeMaterial;
            public string Geometry;
        }

        private static Face ResolvePaintFace(Document document, long elementId, string stable, out Element owner)
        {
            Reference reference = Reference.ParseFromStableRepresentation(document, stable);
            if (reference == null || reference.ElementId == ElementId.InvalidElementId ||
                (reference.LinkedElementId != null && reference.LinkedElementId != ElementId.InvalidElementId))
                throw new ArgumentException("face_reference must resolve to a face in the host document; linked faces cannot be painted.");
            if (Rid.Value(reference.ElementId) != elementId) throw new ArgumentException("face_reference does not belong to element_id " + elementId + ".");
            owner = document.GetElement(reference.ElementId);
            if (owner == null || owner is ElementType) throw new ArgumentException("face owner is not an instance in this document.");
            Face face = owner.GetGeometryObjectFromReference(reference) as Face;
            if (face == null) throw new ArgumentException("face_reference is not a readable face.");
            return face;
        }

        private static string PaintFaceGeometry(Face face)
        {
            BoundingBoxUV box = face.GetBoundingBox();
            UV uv = new UV((box.Min.U + box.Max.U) / 2, (box.Min.V + box.Max.V) / 2);
            XYZ point = face.Evaluate(uv), normal = face.ComputeNormal(uv);
            return string.Join(",", new[] { face.Area, point.X, point.Y, point.Z, normal.X, normal.Y, normal.Z }
                .Select(v => Math.Round(v, 9).ToString("R", CultureInfo.InvariantCulture)));
        }

        private static ArchModelEdit PlanPaintFaces(Document doc, JObject request)
        {
            var plans = new List<PaintFacePlan>();
            var edit = new ArchModelEdit();
            var normalized = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject row in ((JArray)request["pairs"]).OfType<JObject>())
            {
                long id = (long)row["element_id"];
                if (!Rid.CanRepresent(id)) throw new ArgumentException("element_id is outside this Revit version's range.");
                Face face = ResolvePaintFace(doc, id, (string)row["face_reference"], out Element owner);
                string stable = face.Reference?.ConvertToStableRepresentation(doc) ?? (string)row["face_reference"];
                if (!normalized.Add(stable)) throw new ArgumentException("pairs resolves the same face more than once.");
                var plan = new PaintFacePlan
                {
                    ElementId = id, Reference = stable, Action = (string)row["action"], Material = (long?)row["material_id"],
                    BeforePainted = doc.IsPainted(owner.Id, face), Geometry = PaintFaceGeometry(face)
                };
                if (plan.BeforePainted) plan.BeforeMaterial = Rid.Value(doc.GetPaintedMaterial(owner.Id, face));
                Material material = plan.Material.HasValue && Rid.CanRepresent(plan.Material.Value)
                    ? doc.GetElement(Rid.Make(plan.Material.Value)) as Material : null;
                if (plan.Action == "paint" && material == null) throw new ArgumentException("material_id is not a material of this document.");
                PlannedElement planned = ModelEditRunner.Planned(owner, PlannedAction.Modify, request);
                planned.GeometryFingerprint += ";face=" + stable + ";geometry=" + plan.Geometry;
                planned.BeforeValues["painted"] = plan.BeforePainted.ToString();
                planned.BeforeValues["painted_material"] = plan.BeforeMaterial?.ToString(CultureInfo.InvariantCulture) ?? "none";
                if (material != null) planned.BeforeValues["target_material_unique_id"] = material.UniqueId;
                edit.Planned.Add(planned);
                plans.Add(plan);
            }
            edit.Summary["operation"] = "paint_faces";
            edit.Summary["faces"] = new JArray(plans.Select(p => new JObject { ["element_id"] = p.ElementId, ["face_reference"] = p.Reference,
                ["action"] = p.Action, ["material_id"] = p.Material, ["before_painted"] = p.BeforePainted, ["before_material_id"] = p.BeforeMaterial }));
            edit.Apply = document =>
            {
                foreach (PaintFacePlan p in plans)
                {
                    Face face = ResolvePaintFace(document, p.ElementId, p.Reference, out Element owner);
                    if (p.Action == "paint") document.Paint(owner.Id, face, Rid.Make(p.Material.Value));
                    else if (document.IsPainted(owner.Id, face)) document.RemovePaint(owner.Id, face);
                }
            };
            edit.Verify = document =>
            {
                var required = plans.SelectMany((p, i) => new[] { "face_" + i + "_paint", "face_" + i + "_geometry" }).ToArray();
                var check = new PostconditionCheck(required);
                var evidence = new JArray();
                for (int i = 0; i < plans.Count; i++)
                {
                    PaintFacePlan p = plans[i];
                    try
                    {
                        Face face = ResolvePaintFace(document, p.ElementId, p.Reference, out Element owner);
                        bool painted = document.IsPainted(owner.Id, face);
                        long? material = painted ? Rid.Value(document.GetPaintedMaterial(owner.Id, face)) : (long?)null;
                        bool matches = PaintFacesRules.Matches(p.Action, p.Material, painted, material);
                        check.Compare("face_" + i + "_paint", true, matches);
                        check.Compare("face_" + i + "_geometry", p.Geometry, PaintFaceGeometry(face));
                        evidence.Add(new JObject { ["element_id"] = p.ElementId, ["face_reference"] = p.Reference, ["painted"] = painted,
                            ["painted_material_id"] = material, ["matches"] = matches });
                    }
                    catch (Exception ex)
                    {
                        check.Unreadable("face_" + i + "_paint", p.Action, ex.Message);
                        check.Unreadable("face_" + i + "_geometry", p.Geometry, ex.Message);
                        evidence.Add(new JObject { ["element_id"] = p.ElementId, ["face_reference"] = p.Reference, ["unreadable"] = ex.Message });
                    }
                }
                edit.Evidence["faces"] = evidence;
                return check;
            };
            return edit;
        }
    }
}
