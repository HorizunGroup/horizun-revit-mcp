// horizun_query_model include_compound_structure: a host type's layers, read as Revit stores them.
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class QueryModelCommand
    {
        /// <summary>
        /// The compound structure of a wall, floor, roof or ceiling TYPE (or of an instance's type),
        /// in the very shape horizun_manage_system_types (Pro edition) takes as compound_structure: read it,
        /// change a width, duplicate with it. Widths in millimetres. A type with no compound
        /// structure (curtain wall, stacked wall, in-place) says so instead of returning nothing.
        /// </summary>
        internal static JObject CompoundJson(Element element)
        {
            HostObjAttributes host = element as HostObjAttributes;
            if (host == null)
            {
                ElementId typeId = null;
                try { typeId = element.GetTypeId(); } catch { }
                if (typeId != null && typeId != ElementId.InvalidElementId)
                    host = element.Document.GetElement(typeId) as HostObjAttributes;
            }
            if (host == null) return null;
            CompoundStructure cs = null;
            try { cs = host.GetCompoundStructure(); }
            catch (Exception ex) { return new JObject { ["readable"] = false, ["reason"] = ex.Message }; }
            if (cs == null) return new JObject { ["readable"] = false, ["reason"] = "this type has no compound structure (curtain, stacked or in-place)" };

            var layers = cs.GetLayers();
            double total = 0;
            var rows = new JArray();
            for (int i = 0; i < layers.Count; i++)
            {
                CompoundStructureLayer l = layers[i];
                double mm = Math.Round(l.Width * 304.8, 3);
                total += mm;
                string material = null;
                try { material = l.MaterialId == ElementId.InvalidElementId ? null : element.Document.GetElement(l.MaterialId)?.Name; } catch { }
                // wrapping only means something with shell layers, and manage_system_types refuses wraps=true without them:
                // report it as the duplicate would have to send it, so a read can be sent back unchanged
                bool wraps = false;
                try { wraps = cs.ParticipatesInWrapping(i) && (cs.GetNumberOfShellLayers(ShellLayerType.Exterior) + cs.GetNumberOfShellLayers(ShellLayerType.Interior)) > 0; } catch { }
                rows.Add(new JObject
                {
                    ["function"] = l.Function.ToString(), ["width"] = mm,
                    ["material_id"] = Rid.Value(l.MaterialId), ["material_name"] = material, ["wraps"] = wraps,
                    ["is_core"] = i >= cs.GetFirstCoreLayerIndex() && i <= cs.GetLastCoreLayerIndex()
                });
            }
            var o = new JObject
            {
                ["readable"] = true, ["type_id"] = Rid.Value(host.Id), ["units"] = "mm", ["total_width"] = Math.Round(total, 3),
                ["layers"] = rows,
                ["exterior_shell_layers"] = cs.GetNumberOfShellLayers(ShellLayerType.Exterior),
                ["interior_shell_layers"] = cs.GetNumberOfShellLayers(ShellLayerType.Interior),
                ["structural_layer_index"] = cs.StructuralMaterialIndex,
                ["variable_layer_index"] = cs.VariableLayerIndex,
                ["note"] = "Layers exterior to interior. To make another thickness: horizun_manage_system_types (Pro edition) with source_type_id and compound_structure = these layers (drop is_core and material_name) with the core width changed."
            };
            try { o["end_cap"] = cs.EndCap.ToString(); } catch { }
            try { o["opening_wrapping"] = cs.OpeningWrapping.ToString(); } catch { }
            return o;
        }
    }
}
