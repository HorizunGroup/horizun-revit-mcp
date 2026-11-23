using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private static CommandResult CreationResponse(CommandResult response, IEnumerable<Plan> plans, JArray expansion,
            string failurePrefix = "SUPUESTO: creation used assumed values. ") =>
            ArchitectureCreationRules.CreationResponse(response, plans.Select(p => (p.Index, p.Assumptions)), expansion, failurePrefix);

        private static JObject ResolveHostedRecipe(Document doc, JObject row, string kind, JArray assumptions)
        {
            Wall host = Need<Wall>(doc, row, "host_id");
            BuiltInCategory category = kind == "door" ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows;
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => InCategory(s, category) && s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted &&
                    s.Family.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR)?.AsInteger() == 1);
            FamilySymbol type = PickArchitectureType(doc, row, symbols, assumptions);
            row.Remove("type_name"); row["kind"] = "family_instance"; row["type_id"] = Rid.Value(type.Id);
            if (row["level_id"] == null && !(doc.GetElement(host.LevelId) is Level))
                throw new ArgumentException("host has no readable base level; supply level_id");
            JObject hosted = ArchitectureCreationRules.ResolveHostedDefaults(row, Rid.Value(host.LevelId));
            if (hosted["assumptions"] is JArray notes)
                foreach (JObject note in notes.OfType<JObject>()) assumptions.Add(note.DeepClone());
            return (JObject)hosted["element"];
        }

        private static T PickArchitectureType<T>(Document doc, JObject item, IEnumerable<T> available, JArray assumptions) where T : ElementType
        {
            var candidates = available.ToList();
            long? id = item.Value<long?>("type_id");
            if (id.HasValue && !Rid.CanRepresent(id.Value)) throw new ArgumentException("type_id is outside ElementId range");
            if (id.HasValue && doc.GetElement(Rid.Make(id.Value)) != null && !candidates.Any(t => Rid.Value(t.Id) == id.Value))
                throw new ArgumentException("type_id identifies an incompatible architectural type");
            ElementId defaultId = ElementId.InvalidElementId;
            if (typeof(T) == typeof(WallType)) defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.WallType);
            else if (typeof(T) == typeof(FloorType)) defaultId = Floor.GetDefaultFloorType(doc, false);
            else if (typeof(T) == typeof(CeilingType)) defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.CeilingType);
            else if (typeof(T) == typeof(RoofType)) defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.RoofType);
            else if (string.Equals(item.Value<string>("kind"), "door", StringComparison.OrdinalIgnoreCase)) defaultId = doc.GetDefaultFamilyTypeId(Rid.Make((long)BuiltInCategory.OST_Doors));
            else if (string.Equals(item.Value<string>("kind"), "window", StringComparison.OrdinalIgnoreCase)) defaultId = doc.GetDefaultFamilyTypeId(Rid.Make((long)BuiltInCategory.OST_Windows));
            JObject choice = ArchitectureCreationRules.ResolveType(candidates.Select(t => new ArchitectureCreationRules.TypeCandidate
            {
                Id = Rid.Value(t.Id), Name = t.Name, Family = t.FamilyName,
                Preferred = (!(t is WallType wall) || wall.Kind == WallKind.Basic) && (!(t is FloorType floor) || !floor.IsFoundationSlab)
            }), id, item.Value<string>("type_name"), Rid.Value(defaultId));
            foreach (JObject note in (choice["assumptions"] as JArray ?? new JArray()).OfType<JObject>()) assumptions.Add(note.DeepClone());
            long selected = choice.Value<long>("type_id");
            return candidates.First(t => Rid.Value(t.Id) == selected);
        }

        private static void InferWallHeight(Document doc, Plan p)
        {
            if (p.Input["height"] != null) return;
            Level explicitTop = Optional<Level>(doc, p.Input, "top_level_id");
            Level top = explicitTop ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Where(l => l.ProjectElevation > p.Start.Z + GeometryInput.Tolerance && l.ProjectElevation > p.Level.ProjectElevation + GeometryInput.Tolerance)
                .OrderBy(l => l.ProjectElevation).ThenBy(l => Rid.Value(l.Id)).FirstOrDefault();
            double? topZ = top == null ? (double?)null : top.ProjectElevation + (p.Input.Value<double?>("top_offset") ?? 0) * p.Scale;
            JObject height = ArchitectureCreationRules.ResolveWallHeight(p.Start.Z, topZ, explicitTop != null, p.Scale, top?.Name);
            p.TopLevel = height.Value<bool>("bind_top") ? top : null;
            p.Height = height.Value<double>("height_feet");
            foreach (JObject note in (height["assumptions"] as JArray ?? new JArray()).OfType<JObject>()) p.Assumptions.Add(note.DeepClone());
        }
    }
}
