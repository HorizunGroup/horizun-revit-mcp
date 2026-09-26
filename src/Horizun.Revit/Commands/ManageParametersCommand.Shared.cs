// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
// horizun_manage_parameters: create_shared - write the definition to an SPF, bind it,
// and re-read both the binding and the file on disk.
//
// The rehearsal never touches the caller's SPF: it runs against a temporary COPY, so
// the dry run exercises the same reuse/create decision without writing the real file.
// On apply the definition is written BEFORE the transaction; if the binding then fails
// the definition stays in the file (a file is not transactional) and the reply says so
// through spf_definition_created.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageParametersCommand
    {
        /// <summary>A SpecTypeId from an id ("autodesk.spec.aec:length-2.0.0"), a path ("String.Text") or a legacy ParameterType name.</summary>
        internal static ForgeTypeId ResolveSpec(string text, out string why)
        {
            why = null;
            if (string.IsNullOrWhiteSpace(text)) { why = "data_type is required."; return null; }
            text = text.Trim();
            if (text.IndexOf(':') >= 0)
            {
                string want = ParameterClassificationRules.Unversioned(text);
                ForgeTypeId hit = SpecUtils.GetAllSpecs().FirstOrDefault(s => ParameterClassificationRules.Unversioned(s.TypeId) == want);
                if (hit == null) why = "data_type '" + text + "' is not a spec this Revit version knows.";
                return hit;
            }
            string path = ParameterClassificationRules.LegacySpecPath(text) ?? text;
            string[] parts = path.Split('.');
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
            Type owner = typeof(SpecTypeId);
            if (parts.Length == 2) owner = typeof(SpecTypeId).GetNestedTypes().FirstOrDefault(t => string.Equals(t.Name, parts[0], StringComparison.OrdinalIgnoreCase));
            PropertyInfo prop = owner == null || parts.Length > 2 ? null : owner.GetProperty(parts[parts.Length - 1], F);
            ForgeTypeId spec = prop?.GetValue(null) as ForgeTypeId;
            if (spec == null) why = "data_type '" + text + "' does not resolve: pass a SpecTypeId id, a path such as String.Text or Length, or a legacy name such as Text, YesNo, Integer.";
            return spec;
        }

        private static List<ParameterClassificationRules.SpfEntry> ReadSpfFile(string path)
            => File.Exists(path) ? ParameterClassificationRules.ReadSpf(File.ReadAllText(path)) : new List<ParameterClassificationRules.SpfEntry>();

        /// <summary>
        /// Open <paramref name="path"/> as the SPF and reuse or create the definition. The
        /// SPF stays the application's until <see cref="Run"/> puts the user's back in its
        /// finally: MEASURED 2026-09-25 on Revit 2026, restoring it here - before the binding
        /// was inserted - left the ExternalDefinition "not valid" and the rehearsal failed.
        /// </summary>
        private static ExternalDefinition SpfDefinition(UIApplication uiapp, string path, string groupName, string name, ForgeTypeId spec, Scratch s, out bool created)
        {
            created = false;
            var app = uiapp.Application;
            string previous = app.SharedParametersFilename;
            bool ok = false;
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, "");
                app.SharedParametersFilename = path;
                DefinitionFile file = app.OpenSharedParameterFile() ?? throw new InvalidOperationException("Revit could not open '" + path + "' as a shared parameter file");
                foreach (DefinitionGroup g in file.Groups)
                    foreach (Definition d in g.Definitions)
                        if (d.Name == name)
                        {
                            if (g.Name != groupName) throw new InvalidOperationException("'" + name + "' already exists in SPF group '" + g.Name + "', not '" + groupName + "'");
                            if (d.GetDataType().TypeId != spec.TypeId) throw new InvalidOperationException("'" + name + "' already exists in the SPF with data type " + d.GetDataType().TypeId);
                            ok = true;
                            return (ExternalDefinition)d;
                        }
                DefinitionGroup group = file.Groups.get_Item(groupName) ?? file.Groups.Create(groupName);
                var def = group.Definitions.Create(new ExternalDefinitionCreationOptions(name, spec) { Visible = true }) as ExternalDefinition;
                created = def != null;
                if (def == null) throw new InvalidOperationException("Revit did not create the definition");
                ok = true;
                return def;
            }
            finally
            {
                if (ok) { s.SpfSwitched = true; s.PreviousSpf = previous ?? ""; }
                else try { app.SharedParametersFilename = previous ?? ""; } catch { }
            }
        }

        private static Plan BuildCreateShared(UIApplication uiapp, Document doc, JObject r, out string error)
        {
            error = null;
            string name = (r.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) { error = "name is required."; return null; }
            string spf = r.Value<string>("spf_path");
            if (string.IsNullOrWhiteSpace(spf) || !Path.IsPathRooted(spf)) { error = "spf_path must be an absolute path."; return null; }
            if (!File.Exists(spf) && !Directory.Exists(Path.GetDirectoryName(spf))) { error = "The folder of spf_path does not exist."; return null; }
            string groupName = string.IsNullOrWhiteSpace(r.Value<string>("spf_group")) ? "Horizun" : r.Value<string>("spf_group").Trim();
            ForgeTypeId spec = ResolveSpec(r.Value<string>("data_type"), out error);
            if (spec == null) return null;
            if (!(r["categories"] is JArray ct)) { error = "categories is required."; return null; }
            List<Category> cats = ResolveCats(doc, ct, out error);
            if (cats == null) return null;
            bool type = string.Equals(r.Value<string>("binding_kind"), "Type", StringComparison.OrdinalIgnoreCase);
            ForgeTypeId group = BindSharedParamCommand.ResolveGroup(string.IsNullOrWhiteSpace(r.Value<string>("group")) ? "PG_DATA" : r.Value<string>("group"), out string why);
            if (group == null) { error = why; return null; }
            bool vary = !type && (r["allow_vary_between_groups"] == null || r.Value<bool>("allow_vary_between_groups"));

            List<ParameterClassificationRules.SpfEntry> onDisk = ReadSpfFile(spf).Where(e => e.Name == name).ToList();
            if (onDisk.Count > 1) { error = "'" + name + "' appears " + onDisk.Count + " times in the SPF; which one is meant is not guessable."; return null; }
            if (AllBindings(doc).Any(b => b.Def.Name == name || (onDisk.Count == 1 && b.Guid == onDisk[0].Guid)))
            { error = "A parameter named '" + name + "' (or with its GUID) is already bound in this document; use rebind."; return null; }

            var required = new List<string> { "bound", "binding_kind", "categories", "group", "data_type", "spf_on_disk" };
            if (vary) required.Add("varies_across_groups");
            var p = new Plan
            {
                Op = "create_shared", Subject = name, Category = "ParameterBinding", Action = PlannedAction.Create,
                Definition = (app, rehearse, s) =>
                {
                    s.SpfPath = spf;
                    string path = spf;
                    if (rehearse)
                    {
                        s.TempSpf = Path.Combine(Path.GetTempPath(), "hz-spf-" + Guid.NewGuid().ToString("N") + ".txt");
                        if (File.Exists(spf)) File.Copy(spf, s.TempSpf); else File.WriteAllText(s.TempSpf, "");
                        path = s.TempSpf;
                    }
                    ExternalDefinition def = SpfDefinition(app, path, groupName, name, spec, s, out bool created);
                    s.SpfDefinitionCreated = created;
                    // The GUID, read NOW. MEASURED 2026-09-25 on Revit 2023: after the commit the
                    // ExternalDefinition object was no longer valid, and the post-commit re-read
                    // threw although the binding was there. Everything after Apply uses this copy.
                    s.DefinitionGuid = def.GUID;
                    return def;
                },
                Apply = (app, d, def, s) =>
                {
                    if (!d.ParameterBindings.Insert(def, NewBinding(app, cats, type), group)) throw new InvalidOperationException("Revit refused the Insert");
                    if (vary)
                    {
                        SharedParameterElement sp = SharedParameterElement.Lookup(d, def.GUID) ?? throw new InvalidOperationException("the SharedParameterElement was not created");
                        sp.GetDefinition().SetAllowVaryBetweenGroups(d, true);
                    }
                },
                Verify = (d, def, s) =>
                {
                    var c = new PostconditionCheck(required.ToArray());
                    Bound now = AllBindings(d).FirstOrDefault(b => b.Guid == s.DefinitionGuid);
                    CheckBinding(c, now, type, cats, group);
                    if (now == null) c.Unreadable("data_type", spec.TypeId, "the binding is absent");
                    else c.Compare("data_type", spec.TypeId, SafeId(() => now.Def.GetDataType()));
                    if (vary)
                    {
                        if (now == null) c.Unreadable("varies_across_groups", true, "the binding is absent");
                        else c.Compare("varies_across_groups", true, now.Def.VariesAcrossGroups);
                    }
                    // Re-read the FILE, not the object that wrote it.
                    var entry = ReadSpfFile(s.TempSpf ?? s.SpfPath).FirstOrDefault(e => e.Guid == s.DefinitionGuid);
                    c.Compare("spf_on_disk", name + "|" + groupName, entry == null ? null : entry.Name + "|" + entry.Group);
                    return c;
                },
                Report = (d, def, s) =>
                {
                    Bound now = AllBindings(d).FirstOrDefault(b => b.Guid == s.DefinitionGuid);
                    JObject o = now == null ? new JObject() : BindingJson(now);
                    o["spf_path"] = s.SpfPath; o["spf_definition_reused"] = !s.SpfDefinitionCreated;
                    return o;
                }
            };
            p.Before["request"] = name + "|" + spec.TypeId + "|" + groupName + "|" + (type ? "Type" : "Instance") + "|" + CatKey(cats) + "|" + group.TypeId;
            p.Before["spf_has_definition"] = (onDisk.Count == 1).ToString();
            p.Preview = new JObject
            {
                ["name"] = name, ["data_type"] = spec.TypeId, ["spf_path"] = spf, ["spf_group"] = groupName,
                ["spf_definition"] = onDisk.Count == 1 ? "reuse " + onDisk[0].Guid.ToString("d") : "create",
                ["binding_kind"] = type ? "Type" : "Instance", ["categories"] = new JArray(cats.Select(c => c.Name)),
                ["group"] = group.TypeId, ["allow_vary_between_groups"] = vary
            };
            return p;
        }
    }
}
