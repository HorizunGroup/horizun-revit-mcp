// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_query_classification - READ ONLY. The keynote table and the assembly code
// table as the model holds them (where they were loaded from, whether that file is
// still there, every entry with its parent), how many types and placed instances
// carry each code, what nothing uses, what is missing, and the lookup (size) tables
// of the loaded families.
//
// Family size tables are read through FamilySizeTableManager.GetFamilySizeTableManager
// (project document, family element id): the family is NOT opened, edited or
// reloaded. Measured by reflection over RevitAPI 2023-2027 - the call exists and takes
// the project document in every year.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class QueryClassificationCommand : ICommand
    {
        public string Name => "horizun_query_classification";
        public string Description => "Read-only: keynote and assembly-code tables, their use by code, unused and missing codes, and family lookup tables.";

        private static readonly string[] Ops = { "keynote_table", "assembly_code", "family_lookup_tables", "unused_codes", "missing_codes" };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
            if (wrong != null) return wrong;

            string op = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (!Ops.Contains(op)) return CommandResult.Fail("operation must be one of " + string.Join(", ", Ops) + ".");
            int max = request.Value<int?>("max_rows") ?? 500;
            if (max < 1 || max > 5000) return CommandResult.Fail("max_rows must be between 1 and 5000.");

            try
            {
                JObject result;
                if (op == "family_lookup_tables") result = LookupTables(doc, request, max);
                else
                {
                    bool keynote = op == "keynote_table" || (op != "assembly_code" &&
                                   !string.Equals(request.Value<string>("table"), "assembly_code", StringComparison.OrdinalIgnoreCase));
                    TableRead table = ReadTable(doc, keynote);
                    List<ParameterClassificationRules.TypeCode> types = TypeCodes(doc, keynote);
                    if (op == "unused_codes") result = ParameterClassificationRules.Unused(table.Keys, types, max);
                    else if (op == "missing_codes") result = ParameterClassificationRules.Missing(table.Keys, types, max);
                    else result = TableJson(table, types, max);
                    result["table"] = keynote ? "keynote" : "assembly_code";
                    result["source"] = table.Source;
                    result["table_entries"] = table.Keys.Count;
                    // An empty or unloaded table makes every code "unknown": say so beside the verdict.
                    if (table.Keys.Count == 0)
                        result["warning"] = "The table has no entries in this model (not loaded, or its file is missing), so every code reads as absent from it.";
                }
                result["operation"] = op;
                result["document"] = doc.Title;
                result["read_only"] = true;
                return CommandResult.Ok(result);
            }
            catch (Exception ex) { return CommandResult.Fail(op + " could not be read: " + ex.Message); }
        }

        private sealed class TableRead
        {
            public JObject Source = new JObject();
            public readonly List<string> Keys = new List<string>();
            public readonly List<JObject> Entries = new List<JObject>();
        }

        private static TableRead ReadTable(Document doc, bool keynote)
        {
            var r = new TableRead();
            KeyBasedTreeEntryTable table = keynote ? (KeyBasedTreeEntryTable)KeynoteTable.GetKeynoteTable(doc)
                                                   : AssemblyCodeTable.GetAssemblyCodeTable(doc);
            if (table == null) { r.Source["present"] = false; return r; }
            r.Source["present"] = true;
            r.Source["element_id"] = Rid.Value(table.Id);
            try
            {
                ExternalResourceType kind = keynote ? ExternalResourceTypes.BuiltInExternalResourceTypes.KeynoteTable
                                                    : ExternalResourceTypes.BuiltInExternalResourceTypes.AssemblyCodeTable;
                ExternalResourceReference res = table.GetExternalResourceReference(kind);
                if (res != null)
                {
                    string path = res.InSessionPath;
                    r.Source["path"] = path;
                    r.Source["server_id"] = res.ServerId.ToString();
                    r.Source["version_status"] = res.GetResourceVersionStatus().ToString();
                    if (!string.IsNullOrEmpty(path) && Path.IsPathRooted(path)) r.Source["file_exists"] = File.Exists(path);
                }
            }
            catch (Exception ex) { r.Source["path_error"] = ex.Message; }
            try
            {
                if (table.IsExternalFileReference())
                    r.Source["link_status"] = table.GetExternalFileReference().GetLinkedFileStatus().ToString();
            }
            catch (Exception ex) { r.Source["link_status_error"] = ex.Message; }

            // MEASURED 2026-09-24 (Revit 2024/2025 fixtures): the read failed with a
            // NullReferenceException. A table with no loaded file can hand back no entry
            // collection at all, and an entry can be null; both read as "no entries".
            KeyBasedTreeEntries entries = null;
            try { entries = table.GetKeyBasedTreeEntries(); } catch (Exception ex) { r.Source["entries_error"] = ex.Message; }
            if (entries == null) { r.Source["entries_available"] = false; return r; }
            foreach (KeyBasedTreeEntry e in entries)
            {
                if (e == null) continue;
                string key = e.Key;
                if (string.IsNullOrWhiteSpace(key)) continue;
                r.Keys.Add(key.Trim());
                var o = new JObject { ["code"] = key, ["parent"] = string.IsNullOrEmpty(e.ParentKey) ? null : e.ParentKey };
                if (e is KeynoteEntry kn) o["text"] = kn.KeynoteText;
                else if (e is ClassificationEntry ce) { o["text"] = ce.Description; o["level"] = ce.Level; }
                r.Entries.Add(o);
            }
            return r;
        }

        /// <summary>Every element type (and material, for keynotes) with its code and placed-instance count.</summary>
        private static List<ParameterClassificationRules.TypeCode> TypeCodes(Document doc, bool keynote)
        {
            // Revit 2026 renamed UNIFORMAT_CODE to ASSEMBLY_CODE (measured by reflection, 2023-2027).
#if REVIT2022 || REVIT2023 || REVIT2024 || REVIT2025
            BuiltInParameter assembly = BuiltInParameter.UNIFORMAT_CODE;
#else
            BuiltInParameter assembly = BuiltInParameter.ASSEMBLY_CODE;
#endif
            BuiltInParameter bip = keynote ? BuiltInParameter.KEYNOTE_PARAM : assembly;
            var placed = new Dictionary<long, int>();
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                ElementId tid = e.GetTypeId();
                if (tid == null || tid == ElementId.InvalidElementId || e.Category == null) continue;
                long k = Rid.Value(tid);
                placed[k] = placed.TryGetValue(k, out int n) ? n + 1 : 1;
            }
            var rows = new List<ParameterClassificationRules.TypeCode>();
            IEnumerable<Element> carriers = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<Element>();
            if (keynote) carriers = carriers.Concat(new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Element>());
            foreach (Element t in carriers)
            {
                Parameter p = t.get_Parameter(bip);
                if (p == null || t.Category == null && !(t is Material)) continue;
                long id = Rid.Value(t.Id);
                rows.Add(new ParameterClassificationRules.TypeCode
                {
                    Id = id, Name = t.Name, Category = t.Category?.Name ?? "Materials",
                    Code = p.AsString(), Instances = placed.TryGetValue(id, out int n) ? n : 0
                });
            }
            return rows;
        }

        private static JObject TableJson(TableRead table, List<ParameterClassificationRules.TypeCode> types, int max)
        {
            Dictionary<string, int[]> usage = ParameterClassificationRules.UsageByCode(types);
            var entries = new JArray();
            foreach (JObject e in table.Entries.Take(max))
            {
                usage.TryGetValue(((string)e["code"]).Trim(), out int[] c);
                e["types"] = c?[0] ?? 0; e["instances"] = c?[1] ?? 0;
                entries.Add(e);
            }
            return new JObject
            {
                ["entries"] = entries,
                ["truncated"] = table.Entries.Count > max,
                ["codes_in_use"] = usage.Count
            };
        }

        private static JObject LookupTables(Document doc, JObject request, int max)
        {
            var families = new List<Family>();
            long? only = request.Value<long?>("family_id");
            if (doc.IsFamilyDocument) families.Add(doc.OwnerFamily);
            else if (only.HasValue)
            {
                Family f = Rid.CanRepresent(only.Value) ? doc.GetElement(Rid.Make(only.Value)) as Family : null;
                if (f == null) throw new ArgumentException("family_id " + only.Value + " is not a loaded family");
                families.Add(f);
            }
            else families.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>());

            var rows = new JArray(); int scanned = 0, unreadable = 0;
            foreach (Family f in families)
            {
                scanned++;
                FamilySizeTableManager m;
                try { m = FamilySizeTableManager.GetFamilySizeTableManager(doc, f.Id); }
                catch (Exception ex) { unreadable++; rows.Add(new JObject { ["family_id"] = Rid.Value(f.Id), ["family"] = f.Name, ["error"] = ex.Message }); continue; }
                if (m == null) { if (only.HasValue) rows.Add(new JObject { ["family_id"] = Rid.Value(f.Id), ["family"] = f.Name, ["tables"] = new JArray() }); continue; }
                using (m)
                {
                    var tables = new JArray();
                    foreach (string name in m.GetAllSizeTableNames())
                        using (FamilySizeTable t = m.GetSizeTable(name))
                        {
                            var cols = new JArray();
                            for (int i = 0; i < t.NumberOfColumns; i++)
                                using (FamilySizeTableColumn c = t.GetColumnHeader(i))
                                    cols.Add(new JObject { ["name"] = c.Name, ["spec"] = SafeTypeId(() => c.GetSpecTypeId()) });
                            tables.Add(new JObject { ["name"] = name, ["columns"] = cols, ["rows"] = t.NumberOfRows });
                        }
                    if (tables.Count > 0 || only.HasValue)
                        rows.Add(new JObject { ["family_id"] = Rid.Value(f.Id), ["family"] = f.Name, ["tables"] = tables });
                }
                if (rows.Count >= max) break;
            }
            return new JObject
            {
                ["families_scanned"] = scanned, ["families_unreadable"] = unreadable,
                ["families"] = rows, ["truncated"] = rows.Count >= max && scanned < families.Count,
                ["read_from"] = "project document; no family was opened or edited"
            };
        }

        private static string SafeTypeId(Func<ForgeTypeId> f)
        {
            try { return f()?.TypeId; } catch { return null; }
        }
    }
}
