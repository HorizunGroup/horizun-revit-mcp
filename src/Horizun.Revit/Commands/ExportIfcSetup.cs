// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_export format=ifc_setup: a NAMED IFC export setup
// saved inside the document, plus the per-type IFC class mapping.
// Original Horizun code.
//
// Revit has no public API for the IFC exporter's saved setups (IFCExportConfiguration
// lives in the exporter UI add-in, not in RevitAPI). What the Export IFC dialog lists
// is Extensible Storage that UI writes: one DataStorage per setup, schema
// "IFCExportConfigurationMap" {C2A3E6FE-...}, string field "MapField" = the setup as
// JSON (Core/IfcSetupRules.cs). This command writes exactly that, so the setup shows
// in the dialog's list, and proves it three ways:
//
//   1. before the commit, a fresh read of the DataStorage (a mismatch rolls back);
//   2. after the commit, the same read again (what the reply calls persisted);
//   3. when the exporter UI assembly is loaded in this Revit, ITS OWN loader
//      (IFCExportConfigurationsMap.AddSavedConfigurations) reads the document and the
//      setup is looked up by name in what it built - exporter_readback. Reflection
//      over a non-public API: when it is absent or its shape differs, the reply says
//      unavailable and why; it never turns into a pass.
//
// The category -> IFC class table has no API before Revit 2025 (IFCCategoryTemplate),
// so the class mapping is written where the exporter reads it first on every year:
// the type parameters IfcExportAs (IFC_EXPORT_ELEMENT_TYPE_AS) and IfcExportType
// (IFC_EXPORT_PREDEFINEDTYPE_TYPE), each re-read after the commit.
//
// output_path receives the re-read setup and mapping as .json - the file to keep
// beside a template, evidence rather than an echo of the request.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ExportCommand
    {
        private sealed class IfcTypePlan
        {
            public IfcTypeMapping Row; public ElementType Type;
            public string BeforeAs, BeforePredefined;
        }

        internal sealed class StoredIfcSetup { public DataStorage Storage; public JObject Config; }

        private CommandResult ExecuteIfcSetup(UIApplication app, GateResult gate, Document doc, JObject request, string output)
        {
            if (doc.IsFamilyDocument) return CommandResult.Fail("An IFC export setup lives in a project or template, not in an RFA.");
            string folder = Path.GetDirectoryName(output);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return CommandResult.Fail("The output directory does not exist: " + folder + ". It is not created implicitly: create that folder first, or point output_path (the full path of the file to write) at a folder that exists. Nothing was written.");
            bool overwrite = request.Value<bool?>("overwrite") == true;
            if (!overwrite && File.Exists(output))
                return CommandResult.Fail("Output already exists and overwrite=false: " + output);
            string versionName = request.Value<string>("ifc_version");
            if (string.IsNullOrWhiteSpace(versionName) || string.Equals(versionName, "Default", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("format ifc_setup needs an explicit ifc_version (e.g. IFC4RV): a saved setup never takes the exporter default.");
            IFCVersion version;
            IfcSetupRequest setup;
            try
            {
                version = ParseEnum(versionName, IFCVersion.Default, "ifc_version");
                setup = IfcSetupRules.Read(request["ifc_setup"] as JObject,
                    request.Value<bool?>("ifc_export_base_quantities"), request.Value<bool?>("ifc_split_walls_and_columns"),
                    request.Value<int?>("ifc_space_boundary_level"));
            }
            catch (ArgumentException ex) { return CommandResult.Fail(ex.Message + " Nothing was changed."); }

            var typePlans = new List<IfcTypePlan>();
            var errors = new List<string>();
            foreach (IfcTypeMapping m in setup.TypeMappings)
            {
                if (!Rid.CanRepresent(m.TypeId) || !(doc.GetElement(Rid.Make(m.TypeId)) is ElementType type))
                { errors.Add("type_id " + m.TypeId + " is not an ElementType"); continue; }
                Parameter asP = type.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS);
                Parameter preP = type.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE_TYPE);
                if (asP == null || asP.IsReadOnly || asP.StorageType != StorageType.String)
                { errors.Add("type " + m.TypeId + " ('" + type.Name + "') has no writable IfcExportAs"); continue; }
                if (m.PredefinedType != null && (preP == null || preP.IsReadOnly || preP.StorageType != StorageType.String))
                { errors.Add("type " + m.TypeId + " ('" + type.Name + "') has no writable IfcExportType"); continue; }
                typePlans.Add(new IfcTypePlan { Row = m, Type = type, BeforeAs = asP.AsString(), BeforePredefined = preP?.AsString() });
            }
            if (errors.Count > 0) return CommandResult.Fail("ifc_setup refused; nothing was changed: " + string.Join("; ", errors) + ".");

            StoredIfcSetup existing;
            List<string> others;
            try { existing = FindIfcSetup(doc, setup.Name, out others); }
            catch (Exception ex) { return CommandResult.Fail("The document's IFC setups could not be read: " + ex.Message + ". Nothing was changed."); }
            JObject requested = IfcSetupRules.BuildConfig(setup, (int)version);
            // An existing setup keeps every key it had (the dialog writes many) and takes the requested ones.
            JObject after = existing?.Config != null ? (JObject)existing.Config.DeepClone() : new JObject();
            foreach (JProperty p in requested.Properties())
            {
                if (p.Value is JObject inner && after[p.Name] is JObject had) { foreach (JProperty q in inner.Properties()) had[q.Name] = q.Value.DeepClone(); }
                else after[p.Name] = p.Value.DeepClone();
            }
            after["Name"] = setup.Name;

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "format", "output_path", "overwrite", "ifc_setup", "ifc_version",
                "ifc_export_base_quantities", "ifc_split_walls_and_columns", "ifc_space_boundary_level");
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber, DocumentFingerprint = gate.Identity?.FingerprintDigest(),
                ContextFingerprint = "existing=" + (File.Exists(output) ? output : "") + ";overwrite=" + (overwrite ? "1" : "0")
            };
            resolvedPlan.Elements.Add(new PlannedElement
            {
                UniqueId = existing != null ? existing.Storage.UniqueId : "ifc_setup:" + setup.Name,
                Category = "ifc_export_setup", TypeName = setup.Name,
                Action = existing == null ? PlannedAction.Create : PlannedAction.Modify,
                BeforeValues = new Dictionary<string, string> { { "config", existing?.Config?.ToString(Formatting.None) ?? "" } }
            });
            foreach (IfcTypePlan t in typePlans)
                resolvedPlan.Elements.Add(new PlannedElement
                {
                    UniqueId = t.Type.UniqueId, Category = t.Type.Category?.Name ?? t.Type.GetType().Name, TypeName = t.Type.Name,
                    Action = PlannedAction.Modify,
                    BeforeValues = new Dictionary<string, string> { { "IfcExportAs", t.BeforeAs ?? "" }, { "IfcExportType", t.BeforePredefined ?? "" } }
                });
            JArray mappingRows = new JArray(typePlans.Select(t => new JObject
            {
                ["type_id"] = t.Row.TypeId, ["type"] = t.Type.Name, ["family"] = t.Type.FamilyName,
                ["export_as_before"] = t.BeforeAs, ["export_as"] = t.Row.ExportAs,
                ["predefined_type_before"] = t.BeforePredefined, ["predefined_type"] = t.Row.PredefinedType
            }));

            if (dryRun)
            {
                var rehearsal = new JObject
                {
                    ["dry_run"] = true, ["format"] = "ifc_setup", ["output_path"] = output, ["setup"] = setup.Name,
                    ["setup_exists"] = existing != null, ["other_setups"] = new JArray(others),
                    ["before"] = existing?.Config, ["after"] = after, ["type_mappings"] = mappingRows,
                    ["exporter_ui_loaded"] = ExporterUiAssembly() != null,
                    ["note"] = "Nothing was written. Apply stores the setup where the Export IFC dialog lists it (the exporter UI's own " +
                               "Extensible Storage - Revit has no public setup API), writes IfcExportAs/IfcExportType per type, re-reads both " +
                               "and asks the installed exporter UI to load the setup back."
                };
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(rehearsal, gate, Name, planHash, true,
                    "the token binds the setup as stored now and each type's IfcExportAs/IfcExportType BEFORE the edit.");
                return CommandResult.Ok(rehearsal);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            string commitStatus;
            using (var tx = new Transaction(doc, "Horizun: IFC export setup"))
            {
                tx.Start();
                try
                {
                    Schema schema = IfcSetupSchema(create: true);
                    DataStorage storage = existing?.Storage ?? DataStorage.Create(doc);
                    var entity = new Entity(schema);
                    entity.Set(IfcSetupRules.FieldName, after.ToString(Formatting.None));
                    storage.SetEntity(entity);
                    foreach (IfcTypePlan t in typePlans)
                    {
                        if (!t.Type.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS).Set(t.Row.ExportAs))
                            throw new InvalidOperationException("Revit refused IfcExportAs on type " + t.Row.TypeId);
                        if (t.Row.PredefinedType != null && !t.Type.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE_TYPE).Set(t.Row.PredefinedType))
                            throw new InvalidOperationException("Revit refused IfcExportType on type " + t.Row.TypeId);
                    }
                    List<string> lost = IfcSetupRules.Mismatches(after, FindIfcSetup(doc, setup.Name, out _)?.Config);
                    lost.AddRange(LostMappings(doc, typePlans));
                    if (lost.Count > 0)
                        throw new InvalidOperationException("the re-read before commit does not hold: " + string.Join(", ", lost));
                    commitStatus = Guard.Commit(tx, "IFC export setup").ToString();
                }
                catch (Exception ex)
                {
                    string rb = PlanFailure.NotAttempted; bool attempted = false;
                    if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                    return CommandResult.FailWithDetail("IFC export setup write failed: " + ex.Message + ". " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing in it was kept"),
                        new JObject { ["setup"] = setup.Name, ["measured"] = "write_not_persisted_or_refused" });
                }
            }

            StoredIfcSetup reread = FindIfcSetup(doc, setup.Name, out _);
            List<string> lostAfter = IfcSetupRules.Mismatches(after, reread?.Config);
            lostAfter.AddRange(LostMappings(doc, typePlans));
            if (lostAfter.Count > 0)
                return CommandResult.FailWithDetail("The transaction committed, but the re-read does not hold: " + string.Join(", ", lostAfter),
                    new JObject { ["setup"] = setup.Name, ["commit_status"] = commitStatus, ["persisted"] = false });
            foreach (JObject row in mappingRows.OfType<JObject>())
            {
                ElementType t = (ElementType)doc.GetElement(Rid.Make(row.Value<long>("type_id")));
                row["export_as_after_commit"] = t.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS)?.AsString();
                row["predefined_type_after_commit"] = t.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE_TYPE)?.AsString();
                row["verified"] = true;
            }
            JObject exporter = ExporterReadback(doc, setup.Name, requested);

            var file = new JObject
            {
                ["schema"] = "horizun.ifc-setup/1", ["document"] = doc.Title, ["setup"] = setup.Name,
                ["ifc_version"] = version.ToString(), ["revit"] = app?.Application?.VersionNumber, ["utc"] = DateTime.UtcNow.ToString("o"),
                ["config"] = reread.Config, ["type_mappings"] = new JArray(mappingRows.OfType<JObject>().Select(r => new JObject
                {
                    ["type_id"] = r["type_id"], ["family"] = r["family"], ["type"] = r["type"],
                    ["IfcExportAs"] = r["export_as_after_commit"], ["IfcExportType"] = r["predefined_type_after_commit"]
                })),
                ["exporter_readback"] = exporter["status"]
            };
            try
            {
                File.WriteAllText(output, file.ToString(Formatting.Indented));
                JObject back = JObject.Parse(File.ReadAllText(output));
                if (!JToken.DeepEquals(back["config"], file["config"]) || !JToken.DeepEquals(back["type_mappings"], file["type_mappings"]))
                    throw new IOException("the file re-read differs from what was written");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return CommandResult.FailWithDetail("The setup is written and verified in the document, but " + output + " could not be written: " + ex.Message,
                    new JObject { ["setup"] = setup.Name, ["commit_status"] = commitStatus, ["persisted"] = true });
            }

            var result = new JObject
            {
                ["format"] = "ifc_setup", ["setup"] = setup.Name, ["setup_storage_id"] = Rid.Value(reread.Storage.Id),
                ["created"] = existing == null, ["ifc_version"] = version.ToString(), ["commit_status"] = commitStatus,
                ["config"] = reread.Config, ["type_mappings"] = mappingRows, ["exporter_readback"] = exporter,
                ["files_verified"] = 1, ["files"] = new JArray(new JObject { ["path"] = output, ["bytes"] = new FileInfo(output).Length }),
                ["means"] = "persisted = the setup JSON and each type parameter re-read after the commit. exporter_readback=verified means the " +
                            "installed IFC exporter UI loaded this setup from the document by its name with the requested values. " +
                            "Save/close/reopen is not measured by this call."
            };
            ApplicationOutcome.Stamp(result, WriteTally.OneObject(commitStatus, true));
            return CommandResult.Ok(result);
        }

        internal static Schema IfcSetupSchema(bool create)
        {
            var guid = new Guid(IfcSetupRules.SchemaGuid);
            Schema schema = Schema.Lookup(guid);
            if (schema != null || !create) return schema;
            // The exporter UI's own definition: public, one string field. Any other shape
            // under this GUID would make the exporter's lookup fail on this document.
            var builder = new SchemaBuilder(guid);
            builder.SetSchemaName(IfcSetupRules.SchemaName);
            builder.AddSimpleField(IfcSetupRules.FieldName, typeof(string));
            return builder.Finish();
        }

        /// <summary>The stored setup with this name (exporter's case-insensitive key), and the names of the others.</summary>
        internal static StoredIfcSetup FindIfcSetup(Document doc, string name, out List<string> others)
        {
            others = new List<string>();
            Schema schema = IfcSetupSchema(create: false);
            if (schema == null) return null;
            StoredIfcSetup found = null;
            foreach (DataStorage ds in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
            {
                Entity e = ds.GetEntity(schema);
                if (e == null || !e.IsValid()) continue;
                JObject config;
                try { config = JsonText.ParseObject(e.Get<string>(IfcSetupRules.FieldName) ?? ""); }
                catch (JsonException) { others.Add("(unreadable setup in DataStorage " + Rid.Value(ds.Id) + ")"); continue; }
                string n = (string)config["Name"];
                if (found == null && IfcSetupRules.SameName(n, name)) found = new StoredIfcSetup { Storage = ds, Config = config };
                else others.Add(n ?? "(unnamed)");
            }
            return found;
        }

        /// <summary>
        /// The exporter schemas, other than the one this tool reads, under which THIS document holds saved setups (INT-6 M-2).
        /// An entity that cannot be looked for counts as present: not being able to look is not "none".
        /// </summary>
        internal static List<string> OtherSetupSchemasInUse(Document doc)
        {
            var inUse = new List<string>();
            foreach (string g in IfcSetupRules.OtherSetupSchemaGuids)
            {
                try
                {
                    int count = new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(new Guid(g))).GetElementCount();
                    if (count > 0) inUse.Add(g + " (" + count + " element(s))");
                }
                catch (Exception ex) { inUse.Add(g + " (could not be looked for: " + ex.GetType().Name + ")"); }
            }
            return inUse;
        }

        /// <summary>Every readable saved setup (name, config); unreadable ones are counted, not skipped silently.</summary>
        internal static List<KeyValuePair<string, JObject>> AllIfcSetups(Document doc, out int unreadable)
        {
            unreadable = 0;
            var all = new List<KeyValuePair<string, JObject>>();
            Schema schema = IfcSetupSchema(create: false);
            if (schema == null) return all;
            foreach (DataStorage ds in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
            {
                Entity e = ds.GetEntity(schema);
                if (e == null || !e.IsValid()) continue;
                try
                {
                    JObject config = JsonText.ParseObject(e.Get<string>(IfcSetupRules.FieldName) ?? "");
                    all.Add(new KeyValuePair<string, JObject>((string)config["Name"] ?? "(unnamed)", config));
                }
                catch (JsonException) { unreadable++; }
            }
            return all;
        }

        private static IEnumerable<string> LostMappings(Document doc, List<IfcTypePlan> plans)
        {
            foreach (IfcTypePlan t in plans)
            {
                ElementType now = doc.GetElement(t.Type.Id) as ElementType;
                if (now == null) { yield return "type " + t.Row.TypeId + " missing"; continue; }
                if (!string.Equals(now.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS)?.AsString(), t.Row.ExportAs, StringComparison.Ordinal))
                    yield return "type " + t.Row.TypeId + " IfcExportAs";
                if (t.Row.PredefinedType != null &&
                    !string.Equals(now.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE_TYPE)?.AsString(), t.Row.PredefinedType, StringComparison.Ordinal))
                    yield return "type " + t.Row.TypeId + " IfcExportType";
            }
        }

        private static Assembly ExporterUiAssembly() =>
            AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                string.Equals(a.GetName().Name, "Autodesk.IFC.Export.UI", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Ask the installed exporter UI to load the document's setups with its own code and
        /// look this one up by name. Non-public API by reflection: any absence or exception is
        /// reported as unavailable with its reason - never as verified.
        /// </summary>
        private static JObject ExporterReadback(Document doc, string name, JObject requested)
        {
            Assembly ui = ExporterUiAssembly();
            if (ui == null) return new JObject { ["status"] = "unavailable", ["reason"] = "the IFC exporter UI add-in is not loaded in this Revit session" };
            PropertyInfo theDoc = null; object previous = null; bool swapped = false;
            try
            {
                Type mapType = ui.GetType("BIM.IFC.Export.UI.IFCExportConfigurationsMap");
                Type appType = ui.GetType("BIM.IFC.Export.UI.IFCCommandOverrideApplication");
                if (mapType == null || appType == null) return new JObject { ["status"] = "unavailable", ["reason"] = "exporter UI types not found" };
                theDoc = appType.GetProperty("TheDocument", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo load = mapType.GetMethod("AddSavedConfigurations", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                MethodInfo has = mapType.GetMethod("HasName", BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo item = mapType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo setter = theDoc?.GetSetMethod(true);
                if (theDoc == null || setter == null || load == null || has == null || item == null)
                    return new JObject { ["status"] = "unavailable", ["reason"] = "exporter UI loader shape differs from the one this build knows" };
                previous = theDoc.GetValue(null);
                setter.Invoke(null, new object[] { doc }); swapped = true;
                object map = Activator.CreateInstance(mapType);
                load.Invoke(map, null);
                if (!(bool)has.Invoke(map, new object[] { name }))
                    return new JObject { ["status"] = "not_found", ["reason"] = "the exporter's loader did not list a setup named '" + name + "'" };
                object config = item.GetValue(map, new object[] { name });
                var seen = new JObject();
                foreach (JProperty p in requested.Properties())
                {
                    if (p.Value is JObject inner)
                    {
                        object sub = config.GetType().GetProperty(p.Name)?.GetValue(config);
                        var subSeen = new JObject();
                        if (sub != null)
                            foreach (JProperty q in inner.Properties()) subSeen[q.Name] = Scalar(ReadMember(sub, q.Name));
                        seen[p.Name] = subSeen;
                    }
                    else seen[p.Name] = Scalar(ReadMember(config, p.Name));
                }
                List<string> bad = IfcSetupRules.Mismatches(requested, seen);
                return new JObject
                {
                    ["status"] = bad.Count == 0 ? "verified" : "mismatch", ["mismatched"] = new JArray(bad), ["loaded"] = seen,
                    ["exporter_version"] = ui.GetName().Version?.ToString()
                };
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                return new JObject { ["status"] = "unavailable", ["reason"] = inner.GetType().Name + ": " + inner.Message };
            }
            finally
            {
                if (swapped) { try { theDoc.GetSetMethod(true).Invoke(null, new[] { previous }); } catch { } }
            }
        }

        private static object ReadMember(object target, string member)
        {
            Type t = target.GetType();
            PropertyInfo p = t.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(target);
            FieldInfo f = t.GetField(member, BindingFlags.Public | BindingFlags.Instance);
            return f?.GetValue(target);
        }

        private static JToken Scalar(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is Enum) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (value is bool || value is string) return new JValue(value);
            if (value is int || value is long || value is short) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (value is double || value is float || value is decimal) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return new JValue(value.ToString());
        }
    }
}
