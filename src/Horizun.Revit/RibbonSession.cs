// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The SESSION half of the Horizun Hub tab: packs, security, planimetry review
// and jobs. The first three buttons (status, Python, hub) answer "is this
// working"; these answer "what is this session ABOUT, and what has it done".
//
// The same iron rule as Ribbon.cs: nothing here may ever be the reason Revit
// fails to start or a command takes the process down. Every entry point is
// wrapped; a failure is a dialog or a log line, never an unhandled exception.
//
// And one rule of its own: THE UI NEVER APPLIES MODEL CORRECTIONS. The
// planimetry review shows findings and names the typed operation that would
// correct each family of them; applying goes through the MCP tools with their
// rehearsal and confirmation, because a click must not be a write path that
// bypasses the contract everything else honours.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using BridgeSettings = Horizun.Revit.Core.Settings;

namespace Horizun.Revit
{
    // =====================================================================
    // Tool packs
    // =====================================================================

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ToolPacksCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool spanish = PythonPermissionCommand.IsSpanishLanguage(data.Application.Application.Language);
                ToolPacks.Resolution current = BridgeSettings.ActivePackResolution();

                var td = new TaskDialog("Horizun RVT MCP")
                {
                    MainInstruction = spanish ? "Packs de herramientas" : "Tool packs",
                    MainContent = Describe(current, spanish),
                    ExpandedContent = Roster(spanish),
                    CommonButtons = TaskDialogCommonButtons.Close,
                    FooterText = spanish
                        ? "La selección se guarda por usuario en settings.json. Los clientes compatibles se " +
                          "actualizan solos (tools/list_changed); los demás necesitan un reinicio."
                        : "The selection persists per user in settings.json. Compatible clients refresh " +
                          "themselves (tools/list_changed); others need one restart."
                };

                if (current.Source == ToolPacks.SelectionSource.Environment)
                {
                    // The administrator's word wins and this dialog must not pretend
                    // otherwise: offering an Apply that the environment then overrides
                    // would be a control that looks connected and is not.
                    td.MainContent += spanish
                        ? "\n\nLa variable de entorno " + ToolPacks.EnvironmentOverride + " está fijada por un " +
                          "administrador y manda sobre cualquier selección de este diálogo."
                        : "\n\nThe environment variable " + ToolPacks.EnvironmentOverride + " is set by an " +
                          "administrator and overrides anything this dialog could select.";
                    td.Show();
                    return Result.Succeeded;
                }

                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, spanish
                    ? "Elegir packs para esta sesión…"
                    : "Choose packs for this session…");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, spanish
                    ? "Restaurar todos los packs (por defecto)"
                    : "Restore every pack (the default)");

                TaskDialogResult r = td.Show();
                if (r == TaskDialogResult.CommandLink2)
                {
                    string error;
                    if (!BridgeSettings.TrySetToolPacks(null, out error))
                        TaskDialog.Show("Horizun RVT MCP", (spanish ? "No se pudo guardar: " : "Could not save: ") + error);
                    else
                        TaskDialog.Show("Horizun RVT MCP", spanish
                            ? "Restaurado: todos los packs. Los clientes compatibles se actualizan solos."
                            : "Restored: every pack. Compatible clients refresh themselves.");
                }
                else if (r == TaskDialogResult.CommandLink1)
                {
                    ChoosePacks(current, spanish);
                }
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }

        /// <summary>
        /// Pack selection through a chain of yes/no dialogs - deliberately native
        /// TaskDialogs rather than a custom window: they inherit Revit's DPI scaling,
        /// theme and keyboard handling, and cannot deadlock the UI thread.
        /// </summary>
        private static void ChoosePacks(ToolPacks.Resolution current, bool spanish)
        {
            var chosen = new List<string>();
            List<string> preselected = current.ChosenPacks ?? new List<string>();
            foreach (string pack in ToolPacks.KnownPacks
                     .Where(p => p != "core")
                     .OrderBy(p => p, StringComparer.Ordinal))
            {
                var ask = new TaskDialog("Horizun RVT MCP")
                {
                    MainInstruction = (spanish ? "¿Incluir el pack '" : "Include pack '") + pack + "'?",
                    MainContent = PackBlurb(pack, spanish) + "\n\n" +
                                  (spanish ? "Herramientas: " : "Tools: ") + ToolPacks.MembersOf(pack).Count +
                                  DependencyNote(pack, spanish) +
                                  (preselected.Contains(pack)
                                      ? (spanish ? "\nActualmente: INCLUIDO" : "\nCurrently: INCLUDED") : ""),
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No |
                                    TaskDialogCommonButtons.Cancel,
                    DefaultButton = preselected.Contains(pack)
                        ? TaskDialogResult.Yes : TaskDialogResult.No
                };
                TaskDialogResult answer = ask.Show();
                if (answer == TaskDialogResult.Cancel)
                {
                    TaskDialog.Show("Horizun RVT MCP",
                        spanish ? "Cancelado; nada cambió." : "Cancelled; nothing changed.");
                    return;
                }
                if (answer == TaskDialogResult.Yes) chosen.Add(pack);
            }

            string error;
            bool saved = chosen.Count == 0
                ? BridgeSettings.TrySetToolPacks(null, out error)
                : BridgeSettings.TrySetToolPacks(chosen, out error);
            if (!saved)
            {
                TaskDialog.Show("Horizun RVT MCP", (spanish ? "No se pudo guardar: " : "Could not save: ") + error);
                return;
            }
            ToolPacks.Resolution applied = BridgeSettings.ActivePackResolution();
            TaskDialog.Show("Horizun RVT MCP", chosen.Count == 0
                ? (spanish
                    ? "Sin selección: se restauraron todos los packs."
                    : "No selection: every pack was restored.")
                : Describe(applied, spanish));
        }

        internal static string Describe(ToolPacks.Resolution r, bool spanish)
        {
            var sb = new StringBuilder();
            if (r.Problem != null)
            {
                sb.Append(spanish ? "CONFIGURACIÓN INVÁLIDA: " : "MALFORMED CONFIGURATION: ").Append(r.Problem);
                return sb.ToString();
            }
            if (!r.Restricting)
            {
                sb.Append(spanish
                    ? "Activos: TODOS los packs (por defecto)."
                    : "Active: EVERY pack (the default).");
            }
            else
            {
                sb.Append(spanish ? "Elegidos: " : "Chosen: ")
                  .Append(string.Join(", ", r.ChosenPacks));
                if (r.AddedByDependency != null && r.AddedByDependency.Count > 0)
                    sb.Append(spanish ? "\nPor dependencia: " : "\nBy dependency: ")
                      .Append(string.Join(", ", r.AddedByDependency));
            }
            sb.Append(spanish ? "\nOrigen: " : "\nSource: ").Append(r.Source.ToString().ToLowerInvariant());
            int visible = 0;
            var tools = r.Tools();
            foreach (Horizun.Contracts.CommandContract c in Horizun.Contracts.Contract.All)
                if (!r.Restricting || tools.Contains(c.Name)) visible++;
            sb.Append(spanish ? "\nHerramientas del contrato visibles por packs: " : "\nContract tools visible by packs: ")
              .Append(visible).Append(" / ").Append(Horizun.Contracts.Contract.All.Count)
              .Append(spanish
                  ? "  (el perfil de permisos puede ocultar más)"
                  : "  (the permission profile may hide more)");
            return sb.ToString();
        }

        private static string Roster(bool spanish)
        {
            var sb = new StringBuilder();
            foreach (string pack in ToolPacks.KnownPacks.OrderBy(p => p, StringComparer.Ordinal))
                sb.Append(pack).Append(" (").Append(ToolPacks.MembersOf(pack).Count)
                  .Append(spanish ? " herr.)" : " tools)")
                  .Append(": ").Append(PackBlurb(pack, spanish)).Append('\n');
            return sb.ToString();
        }

        private static string DependencyNote(string pack, bool spanish)
        {
            IReadOnlyList<string> deps = ToolPacks.DependenciesOf(pack);
            if (deps.Count == 0) return "";
            return (spanish ? "\nTrae consigo: " : "\nBrings along: ") + string.Join(", ", deps);
        }

        private static string PackBlurb(string pack, bool spanish)
        {
            switch (pack)
            {
                case "core": return spanish ? "salud, destino, trabajos - siempre presente" : "health, target, jobs - always present";
                case "read": return spanish ? "consultas y descubrimiento, nada cambia" : "queries and discovery, nothing changes";
                case "model": return spanish ? "escrituras tipadas genéricas del modelo" : "generic typed model writes";
                case "architecture": return spanish ? "recetas de muros, suelos y toposólidos" : "wall, floor and toposolid recipes";
                case "structure": return spanish ? "losas: división y elevaciones" : "slabs: splitting and elevations";
                case "mep": return spanish ? "tipos de sistema MEP" : "MEP system types";
                case "documentation": return spanish ? "vistas, planos, cotas, anotación, revisiones" : "views, sheets, dimensions, annotation, revisions";
                case "planimetry": return spanish ? "consulta/auditoría/corrección de planimetría y producción" : "planimetry query/audit/fix plus production planners";
                case "audit": return spanish ? "auditorías, interferencias, evidencia" : "audits, clash, evidence capture";
                case "coordination": return spanish ? "interferencias, ACC, cantidades" : "clash, ACC, quantities";
                case "schedules": return spanish ? "tablas de planificación: crear, editar, leer" : "schedules: create, edit definitions, read";
                case "family": return spanish ? "familias: crear, homologar, catálogos" : "families: author, homologate, catalogs";
                case "interoperability": return spanish ? "exportaciones, Excel, catálogos" : "exports, Excel, catalogs";
                case "powerbi": return spanish ? "el empuje a Power BI y su mitad Excel" : "the Power BI push and its Excel half";
                case "administration": return spanish ? "sesión de documentos: abrir, guardar, liberar" : "document sessions: open, save, relinquish";
                case "unsafe_code": return spanish ? "la superficie Python - la visibilidad; el permiso del dueño sigue mandando" : "the Python surface - visibility only; the owner grant still gates it";
                default: return "";
            }
        }
    }

    // =====================================================================
    // Security
    // =====================================================================

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class SecurityStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool spanish = PythonPermissionCommand.IsSpanishLanguage(data.Application.Application.Language);
                string profile = BridgeSettings.PermissionProfile;

                var sb = new StringBuilder();
                sb.Append(spanish ? "Perfil de escritura: " : "Write profile: ").Append(profile).Append('\n');
                sb.Append(ProfileMeaning(profile, spanish)).Append("\n\n");
                sb.Append(BridgeStatusCommand.PythonStatusLine(spanish)).Append('\n');
                sb.Append(PythonOrigin(spanish)).Append("\n\n");
                sb.Append(spanish
                    ? "Python NUNCA puede activarse desde un cliente MCP: solo este botón de la cinta o el " +
                      "script del administrador conceden el permiso, y ambos exigen a la persona dueña de la " +
                      "máquina delante de Revit."
                    : "Python can NEVER be enabled from an MCP client: only the ribbon button or the " +
                      "administrator script grant it, and both require the machine's owner at the keyboard.");

                var td = new TaskDialog("Horizun RVT MCP")
                {
                    MainInstruction = spanish ? "Seguridad del puente" : "Bridge security",
                    MainContent = sb.ToString(),
                    ExpandedContent = ChangeHistory(spanish),
                    CommonButtons = TaskDialogCommonButtons.Close,
                    FooterText = spanish
                        ? "El perfil se cambia editando permission_profile en " + BridgeSettings.Path()
                        : "The profile changes by editing permission_profile in " + BridgeSettings.Path()
                };
                td.Show();
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }

        private static string ProfileMeaning(string profile, bool spanish)
        {
            switch (profile)
            {
                case "read_only":
                    return spanish
                        ? "Solo lectura: nada cambia - ni el modelo, ni la sesión de documentos, ni nada fuera."
                        : "Read only: nothing changes - not the model, not the document session, nothing outside.";
                case "full_write":
                    return spanish
                        ? "Escritura completa: además de escrituras tipadas, sesiones de documentos y archivos externos."
                        : "Full write: typed writes plus document sessions and external files.";
                case "unsafe_code":
                    return spanish
                        ? "Código arbitrario ELEGIBLE: el nivel que permite Python cuando además está activado."
                        : "Arbitrary code ELIGIBLE: the rung that permits Python when it is also switched on.";
                default:
                    return spanish
                        ? "Escritura segura (por defecto): escrituras tipadas DENTRO del documento, nada externo."
                        : "Safe write (the default): typed writes INSIDE the document, nothing external.";
            }
        }

        private static string PythonOrigin(bool spanish)
        {
            try
            {
                bool allowed = BridgeSettings.IsToolAllowed(
                    Horizun.Contracts.Contract.Find("horizun_execute_python"), out _);
                if (!allowed)
                    return spanish ? "Origen: apagado (el valor por defecto)." : "Origin: off (the default).";
                if (BridgeSettings.ExecutePythonTemporaryGrantUntilUtc != null)
                    return spanish
                        ? "Origen: un permiso temporal heredado de una versión anterior."
                        : "Origin: a temporary grant inherited from an earlier version.";
                string raw = BridgeSettings.RawValue("execute_python_ui_granted_at_utc");
                if (!string.IsNullOrWhiteSpace(raw))
                    return (spanish ? "Origen: el botón Python ON/OFF, el " : "Origin: the Python ON/OFF button, on ")
                           + raw;
                return spanish
                    ? "Origen: enable_execute_python en settings.json (el script del administrador)."
                    : "Origin: enable_execute_python in settings.json (the administrator script).";
            }
            catch (Exception ex)
            {
                return (spanish ? "Origen: ilegible (" : "Origin: unreadable (") + ex.Message + ")";
            }
        }

        /// <summary>
        /// The recent settings changes made through the ribbon, read from the backup
        /// files the guarded settings writer leaves behind. Not an audit log - the
        /// backups rotate at three - but enough to answer "when did this change last".
        /// </summary>
        private static string ChangeHistory(bool spanish)
        {
            try
            {
                string directory = Path.GetDirectoryName(BridgeSettings.Path());
                if (directory == null || !Directory.Exists(directory))
                    return spanish ? "Sin historial." : "No history.";
                var backups = new DirectoryInfo(directory)
                    .GetFiles(Path.GetFileName(BridgeSettings.Path()) + ".horizun-ui-bak-*")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(3)
                    .ToList();
                if (backups.Count == 0)
                    return spanish
                        ? "Sin cambios recientes desde la cinta."
                        : "No recent changes made from the ribbon.";
                var sb = new StringBuilder(spanish
                    ? "Últimos cambios desde la cinta:\n" : "Recent ribbon changes:\n");
                foreach (FileInfo f in backups)
                    sb.Append("  ").Append(f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss",
                                                                    CultureInfo.InvariantCulture)).Append('\n');
                return sb.ToString();
            }
            catch { return spanish ? "Historial ilegible." : "History unreadable."; }
        }
    }

    // =====================================================================
    // Planimetry review
    // =====================================================================

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class PlanimetryReviewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool spanish = PythonPermissionCommand.IsSpanishLanguage(data.Application.Application.Language);
                if (data.Application.ActiveUIDocument?.Document == null)
                {
                    TaskDialog.Show("Horizun RVT MCP", spanish
                        ? "No hay documento activo que auditar."
                        : "There is no active document to audit.");
                    return Result.Succeeded;
                }

                // The SAME read-only audit an MCP client runs, with the universal rules
                // and no requirement set - not a UI re-implementation of it. What this
                // dialog shows is what horizun_audit_planimetry answered.
                CommandResult audit = new AuditPlanimetryCommand().Execute(data.Application, "{}");
                if (!audit.Success)
                {
                    TaskDialog.Show("Horizun RVT MCP", (spanish
                        ? "La auditoría no pudo ejecutarse: " : "The audit could not run: ") + audit.Error);
                    return Result.Succeeded;
                }
                JObject result = audit.Data as JObject ?? JObject.FromObject(audit.Data);
                JArray findings = result["findings"] as JArray ?? new JArray();

                var bySeverity = findings.OfType<JObject>()
                    .GroupBy(f => (f.Value<string>("severity") ?? "unknown").ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.Count());
                var byRule = findings.OfType<JObject>()
                    .GroupBy(f => f.Value<string>("rule_id") ?? "(no rule)")
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .ToList();

                var sb = new StringBuilder();
                sb.Append(spanish ? "Hallazgos: " : "Findings: ").Append(findings.Count).Append('\n');
                foreach (string severity in new[] { "blocking", "advisory", "unknown" })
                    sb.Append("  ").Append(severity).Append(": ")
                      .Append(bySeverity.TryGetValue(severity, out int n) ? n : 0).Append('\n');
                string fingerprint = result.Value<string>("finding_set_fingerprint");
                if (fingerprint != null)
                    sb.Append(spanish ? "\nHuella del conjunto: " : "\nFinding-set fingerprint: ")
                      .Append(fingerprint.Length > 16 ? fingerprint.Substring(0, 16) + "…" : fingerprint)
                      .Append('\n');

                var expanded = new StringBuilder();
                if (byRule.Count > 0)
                {
                    expanded.Append(spanish ? "Reglas con más hallazgos:\n" : "Rules with the most findings:\n");
                    foreach (var rule in byRule)
                        expanded.Append("  ").Append(rule.Key).Append(": ").Append(rule.Count()).Append('\n');
                }
                expanded.Append(spanish
                    ? "\nCorregir pasa SIEMPRE por las herramientas tipadas: horizun_fix_planimetry aplica " +
                      "correcciones ligadas a hallazgos, con ensayo y confirmación; esta ventana solo muestra. " +
                      "La evidencia visual sale de horizun_capture_view, nunca de un PDF."
                    : "\nCorrecting ALWAYS goes through the typed tools: horizun_fix_planimetry applies " +
                      "finding-bound corrections with rehearsal and confirmation; this window only shows. " +
                      "Visual evidence comes from horizun_capture_view, never a PDF.");

                var td = new TaskDialog("Horizun RVT MCP")
                {
                    MainInstruction = findings.Count == 0
                        ? (spanish ? "Sin hallazgos con las reglas universales." : "No findings under the universal rules.")
                        : (spanish ? "Revisión de planimetría" : "Planimetry review"),
                    MainContent = sb.ToString(),
                    ExpandedContent = expanded.ToString(),
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, spanish
                    ? "Guardar el informe completo (JSON)…" : "Save the full report (JSON)…");
                if (td.Show() == TaskDialogResult.CommandLink1) SaveReport(result, spanish);
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }

        private static void SaveReport(JObject result, bool spanish)
        {
            try
            {
                // The OWNER at the keyboard chooses where; this is a human export, not
                // an MCP write, and the dialog makes the destination their decision.
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "JSON|*.json",
                    FileName = "planimetry-audit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"
                };
                if (dialog.ShowDialog() != true) return;
                File.WriteAllText(dialog.FileName, result.ToString(Newtonsoft.Json.Formatting.Indented),
                                  new UTF8Encoding(false));
                TaskDialog.Show("Horizun RVT MCP", (spanish ? "Guardado: " : "Saved: ") + dialog.FileName);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Horizun RVT MCP", (spanish ? "No se pudo guardar: " : "Could not save: ") + ex.Message);
            }
        }
    }

    // =====================================================================
    // Jobs
    // =====================================================================

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class JobsStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool spanish = PythonPermissionCommand.IsSpanishLanguage(data.Application.Application.Language);
                string jobsDir = HorizunPaths.JobsDir();
                var rows = new List<JobRow>();
                if (Directory.Exists(jobsDir))
                    foreach (string file in Directory.GetFiles(jobsDir, "*.jsonl")
                             .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).Take(50))
                        rows.Add(ReadRow(file));

                int running = rows.Count(r => r.State == "running_or_died");
                int queued = rows.Count(r => r.State == "queued");
                int finished = rows.Count(r => r.State == "finished");
                int failed = rows.Count(r => r.FinishStatus != null &&
                                             r.FinishStatus != "ok" && r.FinishStatus != "succeeded");
                // (the same Failed rule JobRecordSummary proves; restated over rows
                // because the id travels separately here)

                var sb = new StringBuilder();
                sb.Append(spanish ? "Registros recientes: " : "Recent records: ").Append(rows.Count).Append('\n');
                sb.Append(spanish ? "  en cola: " : "  queued: ").Append(queued).Append('\n');
                sb.Append(spanish ? "  corriendo (o sin cerrar): " : "  running (or unclosed): ").Append(running).Append('\n');
                sb.Append(spanish ? "  terminados: " : "  finished: ").Append(finished)
                  .Append(spanish ? " (fallidos: " : " (failed: ").Append(failed).Append(")\n\n");
                sb.Append(spanish
                    ? "Un registro 'corriendo' puede ser un trabajo vivo o un proceso que murió con él: " +
                      "horizun_job_status distingue los dos comprobando el proceso. La cancelación solo elimina " +
                      "trabajo EN COLA, y se pide por MCP, no desde aquí."
                    : "A 'running' record may be live work or a process that died with it: horizun_job_status " +
                      "tells the two apart by checking the process. Cancellation removes QUEUED work only, and " +
                      "is requested over MCP, not from here.");

                var expanded = new StringBuilder();
                foreach (JobRow row in rows.Take(15))
                    expanded.Append(row.Id).Append("  ").Append(row.State)
                            .Append(row.FinishStatus == null ? "" : " (" + row.FinishStatus + ")").Append('\n');

                var td = new TaskDialog("Horizun RVT MCP")
                {
                    MainInstruction = spanish ? "Trabajos asíncronos" : "Asynchronous jobs",
                    MainContent = sb.ToString(),
                    ExpandedContent = expanded.Length == 0
                        ? (spanish ? "Sin registros." : "No records.") : expanded.ToString(),
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, spanish
                    ? "Abrir la carpeta de registros" : "Open the records folder");
                if (td.Show() == TaskDialogResult.CommandLink1) BridgeStatusCommand.OpenPath(jobsDir);
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }

        private sealed class JobRow { public string Id; public string State; public string FinishStatus; }

        /// <summary>The OS check the Core fold takes as a delegate: alive means the
        /// pid exists and has not exited. A pid Windows recycled onto another process
        /// can in principle read alive; the panel wears that the same way the server
        /// does.</summary>
        private static bool ProcessAlive(int pid)
        {
            try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
            catch { return false; }
        }

        /// <summary>
        /// One record's state, decided by the Core summary rules so the folding - a
        /// half-written last line reads as a state, not an exception - is proved in
        /// tests rather than hoped for here.
        /// </summary>
        private static JobRow ReadRow(string file)
        {
            var row = new JobRow { Id = Path.GetFileNameWithoutExtension(file), State = "queued" };
            try
            {
                JobRecordSummary summary = JobRecordSummary.FromLines(File.ReadLines(file), ProcessAlive);
                row.State = summary.State;
                row.FinishStatus = summary.FinishStatus;
            }
            catch { row.State = "unreadable"; }
            return row;
        }
    }
}
