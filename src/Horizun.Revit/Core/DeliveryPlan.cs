using System;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Horizun.Revit.Core
{
    // A staged client workflow, not an executor and never a pre-approval. Each
    // writer rehearses against the state produced by its preceding stage.
    public static class DeliveryPlan
    {
        public static JObject Build(JObject profile,string document)
        {
            if(profile==null) throw new ArgumentException("delivery_profile is required.");
            foreach(var p in profile.Properties())
                if(!new[]{"id","version","units","views","packing","publication","requirement_set"}.Contains(p.Name))
                    throw new ArgumentException("Unknown delivery profile field: "+p.Name);
            foreach(string k in new[]{"id","version"})
                if(profile[k]?.Type!=JTokenType.String || string.IsNullOrWhiteSpace((string)profile[k])) throw new ArgumentException("Delivery profile requires "+k);
            string units=profile.Value<string>("units");
            if(units!="mm" && units!="m" && units!="feet") throw new ArgumentException("Explicit delivery units must be mm, m or feet.");
            var views=profile["views"] as JArray;
            if(views==null || views.Count<1 || views.Count>50 || views.Any(v=>!(v is JObject) || v["view_id"]?.Type!=JTokenType.Integer || (long)v["view_id"]<=0))
                throw new ArgumentException("views requires 1..50 explicit existing view IDs with annotation plans.");
            if(views.Select(v=>(long)v["view_id"]).Distinct().Count()!=views.Count) throw new ArgumentException("Duplicate delivery views.");
            var packing=profile["packing"] as JObject ?? throw new ArgumentException("packing is required.");
            var publication=profile["publication"] as JObject ?? throw new ArgumentException("publication is required.");
            var requirements=profile["requirement_set"] as JObject ?? throw new ArgumentException("requirement_set is required.");
            PlanimetryRequirementSet.Load(requirements);
            var layouts=packing["sheets"] as JArray ?? new JArray(packing.DeepClone());
            if ((packing["sheet_id"] == null) == (packing["sheets"] == null) ||
                layouts.Count > 30 || layouts.Any(l => !(l is JObject) || l["sheet_id"]?.Type != JTokenType.Integer || (long)l["sheet_id"] <= 0))
                throw new ArgumentException("Packing requires one sheet_id or 1..30 explicit sheet candidates.");
            if (layouts.Select(l => (long)l["sheet_id"]).Distinct().Count() != layouts.Count)
                throw new ArgumentException("Duplicate packing sheet IDs.");
            if(layouts.Count<1 || layouts.Any(l=>!(l is JObject) || (l["usable_rect"]==null && !(l["reserved_zones"] is JArray zones && zones.Count>0))))
                throw new ArgumentException("Every delivery sheet needs explicit usable_rect or reserved_zones for its titleblock/graphics.");
            if(publication["view_ids"] is JArray published && published.Select(v=>v.ToString()).Distinct().Count()!=published.Count)
                throw new ArgumentException("Duplicate publication sheet IDs.");
            if(publication.Value<string>("format")!="pdf" || !(publication["view_ids"] is JArray sheetIds) || sheetIds.Count<1)
                throw new ArgumentException("publication requires PDF and explicit sheet view_ids.");
            if (sheetIds.Any(id => id.Type != JTokenType.Integer || (long)id <= 0))
                throw new ArgumentException("Publication sheet IDs must be positive integers.");
            if (layouts.Any(l => !sheetIds.Values<long>().Contains((long)l["sheet_id"])))
                throw new ArgumentException("Every candidate packing sheet must be included in publication; do not silently omit a destination.");
            if (!(packing["items"] is JArray contents) || contents.Count < 1 || contents.Count > 100)
                throw new ArgumentException("Packing requires 1..100 explicit content items.");
            var stages=new JArray();
            foreach(JObject view in views)
            {
                if(view.Properties().Any(p=>p.Name!="view_id" && p.Name!="dimension_sets" && p.Name!="tags"))
                    throw new ArgumentException("Delivery views accept view_id, dimension_sets and tags.");
                long id=(long)view["view_id"];
                stages.Add(Stage("view_"+id,"horizun_navigate",new JObject { ["operation"]="open_view",["view_id"]=id },"verify active graphical view"));
                if(view["dimension_sets"]!=null)
                {
                    if (!(view["dimension_sets"] is JArray sets) || sets.Count < 1 || sets.Count > 30 || sets.Any(s => !(s is JObject)))
                        throw new ArgumentException("dimension_sets requires 1..30 dimension set objects.");
                    stages.Add(Stage("dimensions_"+id,"horizun_plan_annotations",new JObject { ["operation"]="dimension_set",
                        ["view_id"]=id,["units"]=units,["distance_space"]="paper",["sets"]=view["dimension_sets"].DeepClone() },
                        "complete coverage; execute next_arguments through its fresh dry run and confirmation; preserve native value verification"));
                }
                if(view["tags"] is JObject tags)
                {
                    var a=(JObject)tags.DeepClone(); a["operation"]="auto_tags";a["view_id"]=id;a["units"]=units;
                    if(a["distance_space"]==null) a["distance_space"]="paper";
                    stages.Add(Stage("tags_"+id,"horizun_plan_annotations",a,
                        "replan after dimensions; execute next_arguments through its fresh dry run and confirmation; all measured tags must verify"));
                }
                else if(view["tags"]!=null) throw new ArgumentException("tags must be an object.");
                if(view["dimension_sets"]==null && view["tags"]==null) throw new ArgumentException("Each delivery view requires dimension_sets or tags.");
                stages.Add(Stage("capture_view_"+id,"horizun_capture_view",new JObject { ["view_id"]=id },
                    "inspect glyph spacing, leader crossings and crop; visual findings require review, never infer approval"));
            }
            var pack=(JObject)packing.DeepClone();pack["target_document"]=document;pack["units"]=units;pack["dry_run"]=true;
            stages.Add(Stage("pack","horizun_pack_sheets",pack,"rehearse after annotation stages; confirm complete placement only"));
            stages.Add(Stage("audit","horizun_audit_planimetry",new JObject { ["scope"]="sheets",["sheet_ids"]=sheetIds.DeepClone(),
                ["requirement_set"]=requirements.DeepClone(),["units"]=units },"complete coverage and no blocking findings; re-audit any corrections"));
            foreach(JToken id in sheetIds)
            {
                if(id.Type!=JTokenType.Integer || (long)id<=0) throw new ArgumentException("Publication sheet IDs must be positive integers.");
                stages.Add(Stage("capture_sheet_"+id,"horizun_capture_view",new JObject { ["view_id"]=id.DeepClone() },
                    "explicit visual approval of the final sheet; stop if missing or rejected"));
            }
            var export=(JObject)publication.DeepClone(); export["target_document"]=document;export["dry_run"]=true;export["emit_manifest"]=true;
            stages.Add(Stage("publish","horizun_export",export,"only after audit and visual approval; fresh export confirmation; inspect exact file/page counts and manifest"));
            return new JObject { ["schema"]="horizun.delivery-plan/1",["profile_id"]=profile["id"].DeepClone(),
                ["profile_version"]=profile["version"].DeepClone(),["profile_sha256"]=RequestFingerprint.Sha256Hex(RequestFingerprint.Canonical(profile)),
                ["target_document"]=document,["stages"]=stages,["applied"]=false,["publication_approved"]=false,
                ["execution_policy"]="client executes stages in order, stops on refusal or unknown coverage, records stage receipts; do not replay completed writes without checking their receipts",
                ["transaction_policy"]="atomic per typed write, not across the entire deliverable; navigation and external files cannot share a Revit transaction",
                ["new_views"]="Create rooms/views with document_rooms first, then resolve their actual IDs and request this plan." };
        }
        static JObject Stage(string key,string tool,JObject arguments,string acceptance)=>new JObject {
            ["key"]=key,["tool"]=tool,["arguments"]=arguments,["acceptance"]=acceptance,["status"]="pending" };
    }
}
