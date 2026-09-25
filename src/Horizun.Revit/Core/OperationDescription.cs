// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// What an operations-pane row SAYS. Field feedback, 2026-09-25: the pane showed
// "failed" on every row and "horizun_execute_python" as the only description, so a
// person could not tell what had been done to their model or whether it worked. Two
// defects - the pane read a field the ledger never wrote, and it never said what a
// call did - and one design gap: hundreds of reads buried the few calls that changed
// something.
//
// This is the pure half: from a receipt (Core/ReceiptLedger.cs) to a kind (changed,
// rehearsal, read, failed, no change) and one sentence in Revit's language. The pane
// shows the sentence; the raw receipt stays one click away.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class OperationDescription
    {
        public const string Changed = "changed", Rehearsal = "rehearsal", Read = "read", Failed = "failed", NoChange = "no_change";

        /// <summary>The outcome the ledger wrote. Older lines used `success`; both are honoured.</summary>
        public static bool Succeeded(JObject receipt)
        {
            string outcome = receipt?.Value<string>("outcome");
            if (outcome != null) return string.Equals(outcome, "ok", StringComparison.OrdinalIgnoreCase);
            return receipt?.Value<bool?>("success") ?? false;
        }

        public static string Kind(JObject r)
        {
            if (!Succeeded(r)) return Failed;
            if (r.Value<bool?>("dry_run") == true) return Rehearsal;
            JObject changes = r["model_changes"] as JObject;
            if (changes != null && (Int(changes, "added") + Int(changes, "modified") + Int(changes, "deleted")) > 0) return Changed;
            string tx = r.Value<string>("transaction_status");
            if (string.Equals(tx, "Committed", StringComparison.OrdinalIgnoreCase)) return Changed;
            return ReadOnlyTools.Contains(r.Value<string>("tool") ?? "") ? Read : NoChange;
        }

        /// <summary>The words in the State column.</summary>
        public static string KindLabel(string kind, bool es)
        {
            switch (kind)
            {
                case Changed: return es ? "Cambió el modelo" : "Changed model";
                case Rehearsal: return es ? "Ensayo" : "Rehearsal";
                case Failed: return es ? "Falló" : "Failed";
                case NoChange: return es ? "Sin cambios" : "No change";
                default: return es ? "Consulta" : "Read";
            }
        }

        /// <summary>One sentence: what was done, and what came of it.</summary>
        public static string Sentence(JObject r, bool es)
        {
            string tool = r.Value<string>("tool") ?? "";
            string what = r.Value<string>("purpose");
            if (string.IsNullOrWhiteSpace(what)) what = ToolPhrase(tool, es);
            string op = r.Value<string>("request_operation");
            if (!string.IsNullOrWhiteSpace(op) && string.IsNullOrWhiteSpace(r.Value<string>("purpose")))
                what += " (" + op.Replace('_', ' ') + ")";

            string kind = Kind(r);
            string outcome;
            switch (kind)
            {
                case Failed:
                    string err = (r.Value<string>("error") ?? "").Replace('\n', ' ').Trim();
                    if (err.Length > 140) err = err.Substring(0, 140) + "…";
                    outcome = (es ? "no se hizo: " : "not done: ") + (err.Length == 0 ? (es ? "sin detalle" : "no detail") : err);
                    break;
                case Rehearsal:
                    outcome = es ? "ensayo: se revisó sin escribir nada" : "rehearsal: checked, nothing written";
                    break;
                case Changed:
                    JObject c = r["model_changes"] as JObject;
                    outcome = c == null
                        ? (es ? "aplicado y comprobado" : "applied and checked")
                        : (es ? string.Format(CultureInfo.InvariantCulture, "{0} añadidos, {1} modificados, {2} borrados", Int(c, "added"), Int(c, "modified"), Int(c, "deleted"))
                              : string.Format(CultureInfo.InvariantCulture, "{0} added, {1} modified, {2} deleted", Int(c, "added"), Int(c, "modified"), Int(c, "deleted")));
                    break;
                case NoChange:
                    outcome = es ? "terminó sin cambiar el modelo" : "finished without changing the model";
                    break;
                default:
                    outcome = es ? "solo lectura" : "read only";
                    break;
            }
            string sentence = what + " — " + outcome;
            JObject spatial = r["spatial_check"] as JObject;
            int errors = spatial == null ? 0 : Int(spatial, "errors"), warnings = spatial == null ? 0 : Int(spatial, "warnings");
            if (errors + warnings > 0)
                sentence += es ? string.Format(CultureInfo.InvariantCulture, " · ⚠ revisión espacial: {0} errores, {1} avisos", errors, warnings)
                               : string.Format(CultureInfo.InvariantCulture, " · ⚠ spatial check: {0} errors, {1} warnings", errors, warnings);
            return sentence;
        }

        /// <summary>Show it by default? Reads and rehearsals are there on request, not in the way.</summary>
        public static bool ShownByDefault(JObject r)
        {
            string k = Kind(r);
            return k == Changed || k == Failed;
        }

        public static string ToolPhrase(string tool, bool es)
        {
            if (Phrases.TryGetValue(tool ?? "", out string[] p)) return es ? p[0] : p[1];
            string s = (tool ?? "").StartsWith("horizun_", StringComparison.Ordinal) ? tool.Substring(8) : (tool ?? "");
            s = s.Replace('_', ' ');
            return s.Length == 0 ? (es ? "operación" : "operation") : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static int Int(JObject o, string key)
        {
            JToken t = o?[key];
            if (t == null) return 0;
            if (t.Type == JTokenType.Integer) return t.Value<int>();
            if (t.Type == JTokenType.Array) return ((JArray)t).Count;
            return 0;
        }

        private static readonly HashSet<string> ReadOnlyTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "horizun_health", "horizun_query_model", "horizun_list_elements", "horizun_model_scan", "horizun_quantities",
            "horizun_query_structure", "horizun_query_planimetry", "horizun_query_cad", "horizun_query_dimensions",
            "horizun_query_detail_2d", "horizun_file_info", "horizun_clash", "horizun_audit_model", "horizun_audit_planimetry",
            "horizun_audit_reinforcement", "horizun_audit_cad_model", "horizun_audit_access", "horizun_get_schedule_data",
            "horizun_list_schedules", "horizun_get_dimension_references", "horizun_query_classification",
            "horizun_verify_changes", "horizun_capture_view", "horizun_job_status", "get_document_info"
        };

        // es, en - what a person would say the tool does.
        private static readonly Dictionary<string, string[]> Phrases = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["horizun_execute_python"] = new[] { "Script de Python", "Python script" },
            ["horizun_create_elements"] = new[] { "Crear elementos", "Create elements" },
            ["horizun_transform_elements"] = new[] { "Mover, copiar o girar elementos", "Move, copy or rotate elements" },
            ["horizun_delete_verified"] = new[] { "Borrar elementos", "Delete elements" },
            ["horizun_write_params_verified"] = new[] { "Escribir parámetros", "Write parameters" },
            ["horizun_set_keynote"] = new[] { "Asignar nota clave", "Set keynote" },
            ["horizun_bind_shared_param"] = new[] { "Vincular parámetro compartido", "Bind shared parameter" },
            ["horizun_manage_parameters"] = new[] { "Gestionar parámetros", "Manage parameters" },
            ["horizun_manage_views"] = new[] { "Gestionar vistas", "Manage views" },
            ["horizun_create_schedule"] = new[] { "Crear tabla de planificación", "Create schedule" },
            ["horizun_manage_schedules"] = new[] { "Gestionar tablas", "Manage schedules" },
            ["horizun_manage_worksets"] = new[] { "Gestionar subproyectos", "Manage worksets" },
            ["horizun_manage_groups"] = new[] { "Gestionar grupos", "Manage groups" },
            ["horizun_manage_curtain"] = new[] { "Editar muro cortina", "Edit curtain wall" },
            ["horizun_create_railing"] = new[] { "Crear barandilla", "Create railing" },
            ["horizun_slab_shape"] = new[] { "Editar forma de losa", "Edit slab shape" },
            ["horizun_connect_mep"] = new[] { "Conectar MEP", "Connect MEP" },
            ["horizun_mep_routing"] = new[] { "Trazar MEP", "Route MEP" },
            ["horizun_resolve_clash"] = new[] { "Resolver interferencia", "Resolve clash" },
            ["horizun_undo"] = new[] { "Deshacer cambios de Horizun", "Undo Horizun changes" },
            ["horizun_annotate"] = new[] { "Anotar vistas", "Annotate views" },
            ["horizun_edit_dimensions"] = new[] { "Editar cotas", "Edit dimensions" },
            ["horizun_detail_2d"] = new[] { "Detalle 2D", "2D detailing" },
            ["horizun_pack_sheets"] = new[] { "Organizar planos", "Pack sheets" },
            ["horizun_export"] = new[] { "Exportar", "Export" },
            ["horizun_deliver_ifc"] = new[] { "Entregar IFC", "Deliver IFC" },
            ["horizun_save_document"] = new[] { "Guardar documento", "Save document" },
            ["horizun_open_document"] = new[] { "Abrir documento", "Open document" },
            ["horizun_apply_cad_plan"] = new[] { "Modelar desde CAD", "Model from CAD" },
            ["horizun_apply_ifc_plan"] = new[] { "Modelar desde IFC", "Model from IFC" },
            ["horizun_apply_reinforcement"] = new[] { "Colocar armadura", "Place reinforcement" },
            ["horizun_family_apply"] = new[] { "Homologar familia", "Standardise family" },
            ["horizun_create_family"] = new[] { "Crear familia", "Create family" },
            ["horizun_fix_planimetry"] = new[] { "Corregir planimetría", "Fix drawings" },
            ["horizun_apply_corrections"] = new[] { "Aplicar correcciones", "Apply corrections" },
            ["horizun_query_model"] = new[] { "Consultar el modelo", "Query the model" },
            ["horizun_list_elements"] = new[] { "Listar elementos", "List elements" },
            ["horizun_health"] = new[] { "Estado del puente", "Bridge status" },
            ["horizun_model_scan"] = new[] { "Diagnóstico del modelo", "Model scan" },
            ["horizun_clash"] = new[] { "Detectar interferencias", "Clash detection" },
            ["horizun_verify_changes"] = new[] { "Revisar lo modelado", "Review what was modelled" },
            ["horizun_capture_view"] = new[] { "Capturar vista", "Capture view" },
            ["horizun_audit_model"] = new[] { "Auditar el modelo", "Audit the model" }
        };
    }
}
