// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_audit_model - template_comparison: the model measured against what
// it was meant to stand on. Field session 2026-09-25 found 495 project
// parameters the template never declared, three "Material" parameters whose
// guid did not match the shared parameter file's, 465 view filters and 631
// line patterns the template never drew - all of it by hand, one
// FilteredElementCollector at a time, through horizun_execute_python.
//
// OPT-IN and the only background-opening finding in this command: the
// template is opened DETACHED (never as the caller's own central) and closed
// WITHOUT saving, exactly as horizun_model_diff snapshot opens a comparison
// file - see ModelDiffCommand.ReadFile, which this mirrors. The shared
// parameter file is never opened as the application's own: it is read
// straight off disk with Core/ParameterClassificationRules.ReadSpf, so this
// finding cannot disturb whatever SPF the caller's Revit session is
// configured with.
//
// Pure comparison lives in Core/TemplateComparisonRules.cs; this file only
// gathers the facts on both sides and shapes the reply.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public partial class AuditModelCommand
    {
        private const int TemplateFactCap = 20000;

        private static JObject TemplateComparison(UIApplication app, Document doc, int top,
                                                   string templatePath, string spfPath)
        {
            var session = new JObject();
            Dictionary<string, List<TemplateFact>> modelFacts = ExtractComparableFacts(doc);

            Dictionary<string, List<TemplateFact>> templateFacts = new Dictionary<string, List<TemplateFact>>();
            if (!string.IsNullOrWhiteSpace(templatePath))
            {
                string openError = OpenTemplateAndExtract(app, templatePath, session, templateFacts);
                if (openError != null)
                    return Finding(AuditCheckNames.TemplateComparison, true, 0,
                        "Could not compare against the template: " + openError, new JArray(), 0);
            }

            List<TemplateFact> spfFacts = null;
            if (!string.IsNullOrWhiteSpace(spfPath))
            {
                if (!File.Exists(spfPath))
                    return Finding(AuditCheckNames.TemplateComparison, true, 0,
                        "spf_path '" + spfPath + "' does not exist. Nothing was opened.", new JArray(), 0);
                List<ParameterClassificationRules.SpfEntry> entries;
                try { entries = ParameterClassificationRules.ReadSpf(File.ReadAllText(spfPath)); }
                catch (Exception ex)
                {
                    return Finding(AuditCheckNames.TemplateComparison, true, 0,
                        "Could not read spf_path '" + spfPath + "': " + ex.Message, new JArray(), 0);
                }
                spfFacts = entries.Select(e => new TemplateFact
                {
                    Category = "shared_parameter_file",
                    Key = e.Guid.ToString("D"),
                    KeyIsGuid = true,
                    Name = e.Name,
                    Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["data_type"] = e.DataType ?? "",
                        ["group"] = e.Group ?? ""
                    }
                }).ToList();
                session["spf_entries_read"] = spfFacts.Count;
            }

            var categories = new List<string> { "project_parameter", "view_filter", "line_pattern", "fill_pattern", "object_style", "view_type" };
            var perCategory = new JArray();
            int totalExtra = 0, totalMissing = 0, totalConflicts = 0;

            foreach (string cat in categories)
            {
                if (!string.IsNullOrWhiteSpace(templatePath))
                {
                    modelFacts.TryGetValue(cat, out List<TemplateFact> mf);
                    templateFacts.TryGetValue(cat, out List<TemplateFact> tf);
                    TemplateCategoryResult r = TemplateComparisonRules.Compare(cat, mf, tf);
                    perCategory.Add(CategoryJson(r, top));
                    totalExtra += r.Extra.Count; totalMissing += r.Missing.Count; totalConflicts += r.Conflicts.Count;
                }
            }

            JObject spfRow = null;
            if (spfFacts != null)
            {
                modelFacts.TryGetValue("project_parameter", out List<TemplateFact> modelParams);
                List<TemplateFact> modelShared = (modelParams ?? new List<TemplateFact>()).Where(f => f.KeyIsGuid).ToList();
                TemplateCategoryResult r = TemplateComparisonRules.Compare("shared_parameter_file", modelShared, spfFacts);
                spfRow = CategoryJson(r, top);

                // THE NAME COLLISION, named explicitly: an extra (model) and a missing (SPF)
                // sharing a NAME but not a guid are not one parameter drifting - they are two
                // parameters wearing one label, the exact shape of the field's three "Material"
                // parameters. TemplateComparisonRules keys by guid on purpose (see its own
                // header), so this pairing is done here, once, over the two lists it already
                // produced rather than as a second, looser comparison.
                var collisions = new JArray();
                foreach (TemplateFact m in r.Extra)
                    foreach (TemplateFact t in r.Missing.Where(x => string.Equals(x.Name, m.Name, StringComparison.Ordinal)))
                        collisions.Add(new JObject
                        {
                            ["name"] = m.Name,
                            ["model_guid"] = m.Key,
                            ["spf_guid"] = t.Key
                        });
                spfRow["name_collisions"] = collisions;
                spfRow["name_collisions_note"] = collisions.Count == 0 ? null :
                    collisions.Count + " parameter name(s) exist in both the model and the SPF under DIFFERENT " +
                    "guids: these are different parameters that happen to share a label, not one parameter " +
                    "that drifted. " + TemplateComparisonRules.IdentityMeans;
                perCategory.Add(spfRow);
                totalExtra += r.Extra.Count; totalMissing += r.Missing.Count; totalConflicts += r.Conflicts.Count;
            }

            bool isIssue = totalExtra > 0 || totalMissing > 0 || totalConflicts > 0;
            string summary = (string.IsNullOrWhiteSpace(templatePath) ? "" : "against template '" + templatePath + "': ") +
                              (string.IsNullOrWhiteSpace(spfPath) ? "" : "against SPF '" + spfPath + "': ") +
                              totalExtra + " extra (in the model, not the reference), " +
                              totalMissing + " missing (in the reference, not the model), " +
                              totalConflicts + " conflict(s) across " + perCategory.Count + " compared categories. " +
                              TemplateComparisonRules.IdentityMeans + " " + session.ToString(Newtonsoft.Json.Formatting.None);

            JObject finding = Finding(AuditCheckNames.TemplateComparison, isIssue, totalExtra + totalMissing + totalConflicts,
                                      summary, perCategory, perCategory.Count);
            finding["session"] = session;
            return finding;
        }

        private static JObject CategoryJson(TemplateCategoryResult r, int top)
        {
            Func<IEnumerable<TemplateFact>, JArray> take = list => new JArray(list.Take(top).Select(f => (JToken)new JObject
            {
                ["key"] = f.Key,
                ["key_is_guid"] = f.KeyIsGuid,
                ["name"] = f.Name
            }));
            return new JObject
            {
                ["category"] = r.Category,
                ["model_count"] = r.ModelCount,
                ["reference_count"] = r.TemplateCount,
                ["extra_total"] = r.Extra.Count,
                ["extra_shown"] = Math.Min(r.Extra.Count, top),
                ["extra"] = take(r.Extra),
                ["missing_total"] = r.Missing.Count,
                ["missing_shown"] = Math.Min(r.Missing.Count, top),
                ["missing"] = take(r.Missing),
                ["conflicts_total"] = r.Conflicts.Count,
                ["conflicts"] = new JArray(r.Conflicts.Take(top).Select(c => (JToken)new JObject
                {
                    ["key"] = c.Key,
                    ["name"] = c.Name,
                    ["differing_attributes"] = new JArray(c.DifferingAttributes),
                    ["model_values"] = JObject.FromObject(c.ModelValues),
                    ["reference_values"] = JObject.FromObject(c.TemplateValues)
                }))
            };
        }

        /// <summary>Opens templatePath DETACHED in the background and closes it WITHOUT saving,
        /// exactly as ModelDiffCommand.ReadFile does for a snapshot comparison file. Extracts
        /// its comparable facts into templateFacts before closing.</summary>
        private static string OpenTemplateAndExtract(UIApplication app, string templatePath, JObject session,
                                                      Dictionary<string, List<TemplateFact>> templateFacts)
        {
            if (!File.Exists(templatePath)) return "template_path '" + templatePath + "' does not exist.";

            var open = new OpenRequest
            {
                CommandName = "horizun_audit_model", Path = templatePath, AllowUpgrade = false, Detach = true
            };
            OpenPlan plan = OpenGuard.Check(app, open);
            if (!plan.Ok) return "the template could not be opened: " + (plan.Refusal?.Error ?? "refused");
            if (plan.IsCloud) return "template_path must be a local file; a cloud model is not supported here.";
            if (plan.FileIsWorkshared != true) open.Detach = false;

            Document bg = null;
            try
            {
                using (Interference.WithDialogAnswer(DialogAnswer.Cancel))
                    bg = app.Application.OpenDocumentFile(plan.ModelPath, plan.Options());
                if (bg == null) return "Revit returned no document for '" + templatePath + "'.";
                Dictionary<string, List<TemplateFact>> read = ExtractComparableFacts(bg);
                foreach (KeyValuePair<string, List<TemplateFact>> kv in read) templateFacts[kv.Key] = kv.Value;
                session["template_opened"] = true;
                session["template_detached"] = open.Detach;
                return null;
            }
            catch (Exception ex)
            {
                return "could not open '" + templatePath + "' in the background: " + ex.Message;
            }
            finally
            {
                if (bg != null)
                {
                    bool closed = false;
                    try { closed = bg.Close(false); } catch { }
                    session["template_closed_without_saving"] = closed;
                }
            }
        }

        /// <summary>Everything this finding compares, read from one Document - the active model
        /// or the background-opened template. Symmetric on purpose: whatever this reads from the
        /// model, it reads the same way from the template, so a difference is a real difference
        /// and not an artefact of two collectors that disagree.</summary>
        private static Dictionary<string, List<TemplateFact>> ExtractComparableFacts(Document doc)
        {
            var byCategory = new Dictionary<string, List<TemplateFact>>(StringComparer.Ordinal);
            byCategory["project_parameter"] = ProjectParameterFacts(doc);
            byCategory["view_filter"] = NamedFacts(doc, typeof(ParameterFilterElement), "view_filter");
            byCategory["line_pattern"] = NamedFacts(doc, typeof(LinePatternElement), "line_pattern");
            byCategory["fill_pattern"] = FillPatternFacts(doc);
            byCategory["object_style"] = ObjectStyleFacts(doc);
            byCategory["view_type"] = NamedFacts(doc, typeof(ViewFamilyType), "view_type");
            return byCategory;
        }

        private static List<TemplateFact> ProjectParameterFacts(Document doc)
        {
            var facts = new List<TemplateFact>();
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            it.Reset();
            int n = 0;
            while (it.MoveNext() && n++ < TemplateFactCap)
            {
                var def = it.Key as InternalDefinition;
                if (def == null) continue;
                var binding = it.Current as ElementBinding;
                Guid? guid = null;
                try { if (doc.GetElement(def.Id) is SharedParameterElement sp) guid = sp.GuidValue; } catch { }
                string dataType = null;
                try { dataType = def.GetDataType()?.TypeId; } catch { }
                string cats = null;
                try { cats = string.Join(",", (binding?.Categories?.Cast<Category>() ?? Enumerable.Empty<Category>()).Select(c => c.Name).OrderBy(x => x, StringComparer.Ordinal)); } catch { }
                facts.Add(new TemplateFact
                {
                    Category = "project_parameter",
                    Key = guid.HasValue ? guid.Value.ToString("D") : def.Name,
                    KeyIsGuid = guid.HasValue,
                    Name = def.Name,
                    Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["data_type"] = dataType ?? "",
                        ["binding"] = binding is TypeBinding ? "type" : "instance",
                        ["categories"] = cats ?? ""
                    }
                });
            }
            return facts;
        }

        private static List<TemplateFact> FillPatternFacts(Document doc)
        {
            var facts = new List<TemplateFact>();
            foreach (FillPatternElement e in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                string target = "unknown";
                try { target = e.GetFillPattern()?.Target.ToString() ?? "unknown"; } catch { }
                facts.Add(new TemplateFact
                {
                    Category = "fill_pattern",
                    Key = e.Name,
                    KeyIsGuid = false,
                    Name = e.Name,
                    Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = target }
                });
            }
            return facts;
        }

        /// <summary>Every subcategory of every category the model/template declares - the
        /// "object styles" the Object Styles dialog shows, keyed by "Category|Subcategory".</summary>
        private static List<TemplateFact> ObjectStyleFacts(Document doc)
        {
            var facts = new List<TemplateFact>();
            Categories cats;
            try { cats = doc.Settings.Categories; } catch { return facts; }
            foreach (Category top in cats.Cast<Category>())
            {
                CategoryNameMap subs;
                try { subs = top.SubCategories; } catch { continue; }
                if (subs == null) continue;
                foreach (Category sub in subs.Cast<Category>())
                {
                    string weight = "", color = "";
                    try { weight = sub.GetLineWeight(GraphicsStyleType.Projection)?.ToString() ?? ""; }
                    catch { }
                    try { color = sub.LineColor != null && sub.LineColor.IsValid ? sub.LineColor.Red + "," + sub.LineColor.Green + "," + sub.LineColor.Blue : ""; }
                    catch { }
                    facts.Add(new TemplateFact
                    {
                        Category = "object_style",
                        Key = top.Name + "|" + sub.Name,
                        KeyIsGuid = false,
                        Name = top.Name + " / " + sub.Name,
                        Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["line_weight"] = weight,
                            ["line_color"] = color
                        }
                    });
                }
            }
            return facts;
        }

        private static List<TemplateFact> NamedFacts(Document doc, Type elementType, string category)
        {
            var facts = new List<TemplateFact>();
            foreach (Element e in new FilteredElementCollector(doc).OfClass(elementType))
            {
                string name;
                try { name = e.Name; } catch { continue; }
                if (string.IsNullOrEmpty(name)) continue;
                facts.Add(new TemplateFact { Category = category, Key = name, KeyIsGuid = false, Name = name });
            }
            return facts;
        }
    }
}
