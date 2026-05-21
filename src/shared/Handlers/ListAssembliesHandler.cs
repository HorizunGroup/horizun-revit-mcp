using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class ListAssembliesHandler : IRevitCommand
    {
        public string Name => "list_assemblies";
        public string Description => "List Revit assembly instances in the active document.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""includeMembers"":{""type"":""boolean"",""default"":false,""description"":""Include member element IDs for each assembly.""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson)
                    ? new JObject()
                    : JObject.Parse(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            var includeMembers = request.Value<bool?>("includeMembers") ?? false;
            var assemblies = new List<ListAssemblyInfo>();

            foreach (AssemblyInstance assembly in new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)))
            {
                assemblies.Add(BuildAssemblyInfo(doc, assembly, includeMembers));
            }

            var orderedAssemblies = assemblies
                .OrderBy(a => a.Name)
                .ThenBy(a => a.AssemblyId)
                .ToArray();

            return CommandResult.Ok(new
            {
                count = orderedAssemblies.Length,
                includeMembers,
                assemblies = orderedAssemblies
            });
        }

        private static ListAssemblyInfo BuildAssemblyInfo(Document doc, AssemblyInstance assembly, bool includeMembers)
        {
            var typeId = ToValidId(assembly.GetTypeId());
            var typeElement = typeId.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(typeId.Value))
                : null;
            var namingCategoryId = ToValidId(assembly.NamingCategoryId);
            var namingCategory = namingCategoryId.HasValue
                ? Category.GetCategory(doc, RevitCompat.ToElementId(namingCategoryId.Value))
                : null;
            var ownerViewId = ToValidId(assembly.OwnerViewId);
            var ownerView = ownerViewId.HasValue
                ? doc.GetElement(assembly.OwnerViewId)
                : null;
            var memberIds = assembly.GetMemberIds();

            var info = new ListAssemblyInfo
            {
                AssemblyId = RevitCompat.GetId(assembly.Id),
                Name = assembly.Name,
                TypeId = typeId,
                TypeName = string.IsNullOrEmpty(assembly.AssemblyTypeName)
                    ? typeElement?.Name
                    : assembly.AssemblyTypeName,
                Category = assembly.Category?.Name,
                CategoryId = RevitCompat.GetIdOrNull(assembly.Category?.Id),
                NamingCategoryId = namingCategoryId,
                NamingCategory = namingCategory?.Name,
                MemberCount = memberIds.Count,
                OwnerViewId = ownerViewId,
                OwnerViewName = ownerView?.Name
            };

            if (includeMembers)
                info.MemberIds = memberIds.Select(RevitCompat.GetId).ToArray();

            return info;
        }

        private static long? ToValidId(ElementId id)
        {
            var value = RevitCompat.GetIdOrNull(id);
            return value.HasValue && value.Value > 0
                ? value
                : null;
        }

        private class ListAssemblyInfo
        {
            [JsonProperty("assemblyId")]
            public long AssemblyId { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("typeId")]
            public long? TypeId { get; set; }

            [JsonProperty("typeName")]
            public string TypeName { get; set; }

            [JsonProperty("category")]
            public string Category { get; set; }

            [JsonProperty("categoryId")]
            public long? CategoryId { get; set; }

            [JsonProperty("namingCategoryId")]
            public long? NamingCategoryId { get; set; }

            [JsonProperty("namingCategory")]
            public string NamingCategory { get; set; }

            [JsonProperty("memberCount")]
            public int MemberCount { get; set; }

            [JsonProperty("ownerViewId")]
            public long? OwnerViewId { get; set; }

            [JsonProperty("ownerViewName")]
            public string OwnerViewName { get; set; }

            [JsonProperty("memberIds", NullValueHandling = NullValueHandling.Ignore)]
            public long[] MemberIds { get; set; }
        }
    }
}
