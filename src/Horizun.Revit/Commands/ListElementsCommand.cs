// -----------------------------------------------------------------------------
// Horizun Revit MCP — bounded element inventory across host and loaded RVT links.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ListElementsCommand : ICommand
    {
        public string Name => "horizun_list_elements";
        public string Description =>
            "List elements of one category in the active model and loaded RVT links. Totals are exact and independent " +
            "of max_rows; every row names its source model and link instance. Unloaded links and read failures are " +
            "reported, so an empty result is never presented as complete when part of the federated model was unavailable.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            CommandResult wrongDocument = DocumentGate.ReadGuard(doc, request, Name);
            if (wrongDocument != null) return wrongDocument;

            string categoryText = request.Value<string>("category");
            if (string.IsNullOrWhiteSpace(categoryText)) return CommandResult.Fail("category is required.");
            Category hostCategory = ResolveCategory(doc, categoryText);
            if (hostCategory == null)
                return CommandResult.Fail("Category '" + categoryText + "' was not found in the active document.");

            // return_parameters: the same projection horizun_query_model gives (instance first, then
            // the type; each cell says whether the parameter exists and in which scope). '*' is not
            // offered here: the columns of a wildcard are only known once each element is read.
            var returnParameters = new List<string>();
            if (request["return_parameters"] != null)
            {
                JArray listed = request["return_parameters"] as JArray;
                if (listed == null)
                    return CommandResult.Fail("return_parameters must be an array of parameter names.");
                foreach (JToken item in listed)
                {
                    string name = item.Type == JTokenType.String ? item.Value<string>() : null;
                    if (string.IsNullOrWhiteSpace(name))
                        return CommandResult.Fail("return_parameters must hold non-empty parameter names.");
                    if (name.Trim() == QueryPagingRules.AllParameters)
                        return CommandResult.Fail("return_parameters '*' is not available in horizun_list_elements: name the parameters, or use horizun_query_model.");
                    if (!returnParameters.Contains(name, StringComparer.OrdinalIgnoreCase)) returnParameters.Add(name);
                }
                if (returnParameters.Count > 30)
                    return CommandResult.Fail("return_parameters accepts at most 30 names; got " + returnParameters.Count + ".");
            }

            bool includeLinks = request["include_links"] == null || request.Value<bool>("include_links");
            int maxRows = Math.Max(1, Math.Min(1000, request.Value<int?>("max_rows") ?? 200));
            int offset = Math.Max(0, request.Value<int?>("offset") ?? 0);

            // OPT-IN, and absent means exactly what it always meant. See
            // Core/CooperativeReadOptions.cs: a published reader that quietly started
            // stopping early would turn every existing integration's complete answer into a
            // partial one that still looks complete.
            string coopFingerprint = CooperativeOptions.FingerprintOf(request, Name, doc);
            CooperativeOptions cooperative = CooperativeOptions.Read(request, coopFingerprint);
            if (cooperative.Refusal != null)
                return CommandResult.Fail("cooperative: " + cooperative.Refusal);
            CooperativeRead.Scope coopScope = cooperative.Begin("list_elements " + categoryText, 0);

            var all = new List<Row>();
            var unavailable = new JArray();
            Collect(doc, hostCategory.Id, "host", doc.Title, null, all, unavailable, coopScope, cooperative);

            int loadedLinks = 0;
            int unloadedLinks = 0;
            if (includeLinks)
            {
                foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    Document linked = null;
                    try { linked = link.GetLinkDocument(); }
                    catch (Exception ex)
                    {
                        unavailable.Add(new JObject { ["link_instance_id"] = Rid.Value(link.Id), ["name"] = link.Name, ["reason"] = ex.Message });
                        unloadedLinks++;
                        continue;
                    }
                    if (linked == null)
                    {
                        unloadedLinks++;
                        unavailable.Add(new JObject { ["link_instance_id"] = Rid.Value(link.Id), ["name"] = link.Name, ["reason"] = "link is unloaded or its document is unavailable" });
                        continue;
                    }

                    loadedLinks++;
                    Category linkedCategory = ResolveCategory(linked, categoryText);
                    if (linkedCategory == null)
                    {
                        unavailable.Add(new JObject { ["link_instance_id"] = Rid.Value(link.Id), ["name"] = link.Name, ["reason"] = "category not present in linked document" });
                        continue;
                    }
                    if (coopScope != null && !coopScope.Complete) break;
                    Collect(linked, linkedCategory.Id, "link", linked.Title, Rid.Value(link.Id), all,
                            unavailable, coopScope, cooperative);
                }
            }

            all = all.OrderBy(r => r.SourceKind, StringComparer.Ordinal)
                     .ThenBy(r => r.SourceModel, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.Id).ToList();
            List<Row> page = all.Skip(offset).Take(maxRows).ToList();
            if (returnParameters.Count > 0)
            {
                // Only the requested page is projected, never every matched element. The first
                // 50 read problems are listed one by one; the rest are counted, so a parameter
                // that is ambiguous on every row cannot bury the reply.
                int projectionProblems = 0;
                const int listedProblems = 50;
                foreach (Row row in page)
                {
                    if (row.Element == null) continue;
                    var problems = new List<string>();
                    try
                    {
                        row.Parameters = QueryModelCommand.ProjectParameters(row.Document, row.Element, row.TypeElement,
                            returnParameters, out problems, null, null);
                    }
                    catch (Exception ex) { problems.Add("parameters could not be read: " + ex.Message); }
                    foreach (string problem in problems)
                    {
                        projectionProblems++;
                        if (projectionProblems <= listedProblems)
                            unavailable.Add(new JObject { ["source_model"] = row.SourceModel, ["element_id"] = row.Id, ["reason"] = problem });
                    }
                }
                if (projectionProblems > listedProblems)
                    unavailable.Add(new JObject { ["reason"] = (projectionProblems - listedProblems) + " more return_parameters read problems are not listed" });
            }
            JObject federatedCoverage = FederatedVisibility.Measure(doc, includeLinks);
            var result = new JObject
            {
                ["category"] = hostCategory.Name,
                ["include_links"] = includeLinks,
                ["total"] = all.Count,
                ["host_total"] = all.Count(r => r.SourceKind == "host"),
                ["linked_total"] = all.Count(r => r.SourceKind == "link"),
                ["loaded_link_instances"] = loadedLinks,
                ["unloaded_link_instances"] = unloadedLinks,
                ["offset"] = offset,
                ["returned"] = page.Count,
                ["truncated"] = offset + page.Count < all.Count,
                ["coverage_complete"] = unavailable.Count == 0 && federatedCoverage.Value<bool>("coverage_complete"),
                ["federated_coverage"] = federatedCoverage,
                ["unavailable"] = unavailable,
                ["rows"] = new JArray(page.Select(ToJson)),
                // Present ONLY when the caller asked for it. A reply that grew a new block
                // for everybody would be the behaviour change this design exists to avoid.
                ["cooperative"] = cooperative.Report(
                    coopScope, offset + page.Count, coopFingerprint,
                    offset + page.Count < all.Count || (coopScope != null && !coopScope.Complete),
                    ownCursorField: "offset")
            };
            QueryResponseOptions.ApplyCollectionCoverage(result, coopScope?.Complete);
            return CommandResult.Ok(result);
        }

        private static void Collect(Document source, ElementId categoryId, string sourceKind, string sourceName,
                                    long? linkInstanceId, List<Row> rows, JArray unavailable,
                                    CooperativeRead.Scope scope = null, CooperativeOptions options = null)
        {
            try
            {
                foreach (Element element in new FilteredElementCollector(source).OfCategoryId(categoryId).WhereElementIsNotElementType())
                {
                    // BETWEEN elements, never inside one: stopping mid-element would leave a
                    // half-read row in the results, which is a different and worse failure
                    // than reading fewer of them.
                    if (scope != null && !scope.ContinueWithin(options?.MaxUnits ?? 0)) break;
                    try
                    {
                        Element type = source.GetElement(element.GetTypeId());
                        rows.Add(new Row
                        {
                            Id = Rid.Value(element.Id),
                            UniqueId = element.UniqueId,
                            Name = SafeName(element),
                            Family = type is ElementType et ? et.FamilyName : null,
                            Type = type == null ? null : SafeName(type),
                            Level = LevelName(source, element),
                            Document = source,
                            Element = element,
                            TypeElement = type,
                            SourceKind = sourceKind,
                            SourceModel = sourceName,
                            LinkInstanceId = linkInstanceId
                        });
                    }
                    catch (Exception ex)
                    {
                        rows.Add(new Row
                        {
                            Id = Rid.Value(element.Id),
                            SourceKind = sourceKind,
                            SourceModel = sourceName,
                            LinkInstanceId = linkInstanceId,
                            ReadError = ex.Message
                        });
                        unavailable.Add(new JObject { ["source_model"] = sourceName, ["element_id"] = Rid.Value(element.Id), ["reason"] = ex.Message });
                    }
                }
            }
            catch (Exception ex)
            {
                unavailable.Add(new JObject { ["source_model"] = sourceName, ["reason"] = "category collector failed: " + ex.Message });
            }
        }

        private static string LevelName(Document doc, Element element)
        {
            Parameter p = element.get_Parameter(BuiltInParameter.LEVEL_PARAM) ??
                          element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ??
                          element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            if (p == null) return null;
            if (p.StorageType == StorageType.ElementId)
            {
                Element level = doc.GetElement(p.AsElementId());
                if (level != null) return SafeName(level);
            }
            return p.AsValueString();
        }

        private static Category ResolveCategory(Document doc, string text)
        {
            BuiltInCategory bic;
            if (Enum.TryParse(text, true, out bic))
            {
                try { return Category.GetCategory(doc, bic); } catch { }
            }
            foreach (Category category in doc.Settings.Categories)
                if (string.Equals(category.Name, text, StringComparison.OrdinalIgnoreCase)) return category;
            return null;
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; } catch { return null; }
        }

        private static JObject ToJson(Row row)
        {
            JObject json = ToBaseJson(row);
            if (row.Parameters != null) json["parameters"] = row.Parameters;
            return json;
        }

        private static JObject ToBaseJson(Row row) => new JObject
        {
            ["element_id"] = row.Id,
            ["unique_id"] = row.UniqueId,
            ["name"] = row.Name,
            ["family"] = row.Family,
            ["type"] = row.Type,
            ["level"] = row.Level,
            ["source_kind"] = row.SourceKind,
            ["source_model"] = row.SourceModel,
            ["link_instance_id"] = row.LinkInstanceId,
            ["read_error"] = row.ReadError
        };

        private sealed class Row
        {
            public long Id;
            public string UniqueId;
            public string Name;
            public string Family;
            public string Type;
            public string Level;
            public string SourceKind;
            public string SourceModel;
            public long? LinkInstanceId;
            public string ReadError;
            // Held only so the requested page can be projected after paging.
            public Document Document;
            public Element Element;
            public Element TypeElement;
            public JObject Parameters;
        }
    }
}
