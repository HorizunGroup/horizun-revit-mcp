using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class TagAllAreasHandler : IRevitCommand
    {
        public string Name => "tag_all_areas";
        public string Description => "Place area tags for untagged areas in an area plan view";
        public string ParametersSchema => @"{
            ""type"": ""object"",
            ""properties"": {
                ""area_plan_view_id"": { ""type"": ""integer"" },
                ""area_plan_view_name"": { ""type"": ""string"" },
                ""skip_existing"": { ""type"": ""boolean"" },
                ""tag_type_id"": { ""type"": ""integer"" }
            }
        }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            long? areaPlanViewId = null;
            string areaPlanViewName = "";
            bool skipExisting = true;
            long? tagTypeId = null;

            try
            {
                if (!string.IsNullOrEmpty(paramsJson))
                {
                    var request = JObject.Parse(paramsJson);
                    if (request.TryGetValue("area_plan_view_id", out var apvIdVal))
                        areaPlanViewId = apvIdVal.Value<long?>();
                    if (request.TryGetValue("area_plan_view_name", out var apvNameVal))
                        areaPlanViewName = apvNameVal.Value<string>() ?? "";
                    if (request.TryGetValue("skip_existing", out var skipVal))
                        skipExisting = skipVal.Value<bool>();
                    if (request.TryGetValue("tag_type_id", out var tagTypeVal))
                        tagTypeId = tagTypeVal.Value<long?>();
                }
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            // Resolve Area Plan
            ViewPlan areaPlan = null;
            if (areaPlanViewId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(areaPlanViewId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(areaPlanViewId.Value));
                areaPlan = doc.GetElement(RevitCompat.ToElementId(areaPlanViewId.Value)) as ViewPlan;
                if (areaPlan == null || areaPlan.ViewType != ViewType.AreaPlan)
                    return CommandResult.Fail($"Specified view ID {areaPlanViewId.Value} is not a valid Area Plan View.");
            }
            else if (!string.IsNullOrEmpty(areaPlanViewName))
            {
                areaPlan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan))
                    .Cast<ViewPlan>()
                    .FirstOrDefault(v => v.ViewType == ViewType.AreaPlan && v.Name.Equals(areaPlanViewName, StringComparison.OrdinalIgnoreCase));
                if (areaPlan == null)
                    return CommandResult.Fail($"Area Plan View named '{areaPlanViewName}' was not found.");
            }
            else
            {
                areaPlan = app.ActiveUIDocument?.ActiveView as ViewPlan;
            }

            if (areaPlan == null || areaPlan.ViewType != ViewType.AreaPlan)
                return CommandResult.Fail("A valid Area Plan view must be specified or active.");

            // Resolve target tag type if supplied
            FamilySymbol targetTagType = null;
            if (tagTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(tagTypeId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(tagTypeId.Value));

                var typeId = RevitCompat.ToElementId(tagTypeId.Value);
                targetTagType = doc.GetElement(typeId) as FamilySymbol;
                if (targetTagType == null)
                    return CommandResult.Fail($"Specified tag type ID {tagTypeId.Value} is not a valid FamilySymbol (tag type).");
            }

            // Collect visible areas in this area plan view
            var areas = new FilteredElementCollector(doc, areaPlan.Id)
                .OfCategory(BuiltInCategory.OST_Areas)
                .Cast<Area>()
                .ToList();

            // Collect existing tags in this view
            var existingTags = new FilteredElementCollector(doc, areaPlan.Id)
                .OfCategory(BuiltInCategory.OST_AreaTags)
                .Cast<AreaTag>()
                .ToList();

            var taggedAreaIds = new HashSet<ElementId>();
            foreach (var t in existingTags)
            {
                if (t.Area != null)
                {
                    taggedAreaIds.Add(t.Area.Id);
                }
            }

            int processedCount = 0;
            int createdCount = 0;
            int skippedCount = 0;
            int failedCount = 0;
            var createdTagIds = new List<long>();
            var itemsList = new List<object>();

            using (var tx = new Transaction(doc, "RvtMcp: Tag All Areas"))
            {
                tx.Start();
                try
                {
                    foreach (var area in areas)
                    {
                        processedCount++;
                        long areaId = RevitCompat.GetId(area.Id);

                        // Check if already tagged
                        if (skipExisting && taggedAreaIds.Contains(area.Id))
                        {
                            skippedCount++;
                            itemsList.Add(new
                            {
                                area_id = areaId,
                                tag_id = (long?)null,
                                status = "skipped_existing",
                                message = "Area already has an existing tag in this view."
                            });
                            continue;
                        }

                        // Determine placement location (LocationPoint or fallback centroid/first segment midpoint)
                        UV tagUv = null;
                        if (area.Location is LocationPoint lp)
                        {
                            tagUv = new UV(lp.Point.X, lp.Point.Y);
                        }

                        if (tagUv == null)
                        {
                            failedCount++;
                            itemsList.Add(new
                            {
                                area_id = areaId,
                                tag_id = (long?)null,
                                status = "failed",
                                message = "Area is not placed or does not have a valid location point."
                            });
                            continue;
                        }

                        try
                        {
                            var areaTag = doc.Create.NewAreaTag(areaPlan, area, tagUv);
                            if (areaTag != null)
                            {
                                if (targetTagType != null)
                                {
                                    areaTag.ChangeTypeId(targetTagType.Id);
                                }

                                createdCount++;
                                long tagId = RevitCompat.GetId(areaTag.Id);
                                createdTagIds.Add(tagId);

                                itemsList.Add(new
                                {
                                    area_id = areaId,
                                    tag_id = tagId,
                                    status = "created",
                                    message = "Area tag successfully created."
                                });
                            }
                            else
                            {
                                failedCount++;
                                itemsList.Add(new
                                {
                                    area_id = areaId,
                                    tag_id = (long?)null,
                                    status = "failed",
                                    message = "doc.Create.NewAreaTag returned null."
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            itemsList.Add(new
                            {
                                area_id = areaId,
                                tag_id = (long?)null,
                                status = "failed",
                                message = $"Revit error: {ex.Message}"
                            });
                        }
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Tag all areas transaction did not commit. Status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to place area tags: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                area_plan = new
                {
                    element_id = RevitCompat.GetId(areaPlan.Id),
                    name = areaPlan.Name,
                    area_scheme_name = areaPlan.AreaScheme?.Name ?? ""
                },
                processed_count = processedCount,
                created_count = createdCount,
                skipped_existing_count = skippedCount,
                failed_count = failedCount,
                created_tag_ids = createdTagIds,
                items = itemsList
            });
        }
    }
}
