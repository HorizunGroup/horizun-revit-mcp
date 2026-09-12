using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Deterministic workflow compilation; actual resolution, confirmation,
    // transactions and verification remain in ExecutePlan and its typed children.
    public static class WorkflowPlan
    {
        public static JObject Expand(JObject request, Func<JObject, JObject> planRooms = null)
        {
            if (request["workflow"] == null) return request;
            if (request["actions"] != null) throw new ArgumentException("Supply actions OR workflow, never both.");
            var w = request["workflow"] as JObject ?? throw new ArgumentException("workflow must be an object.");
            string name = w.Value<string>("name");
            var children = new JArray();
            if (name == "document_rooms")
            {
                Fields(w, "name", "room_plan", "view_types", "view_templates", "room_sheets");
                var plan = w["room_plan"] as JObject ?? throw new ArgumentException("room_plan is required.");
                Fields(plan, "room_ids", "plan_view_id", "kinds", "units", "scale", "margin", "name_pattern", "orient_to_walls", "elevation_count", "template_view_id");
                var roomIds = Ids(plan["room_ids"]);
                if (roomIds.Count > 200) throw new ArgumentException("document_rooms accepts at most 200 rooms.");
                Id(plan["plan_view_id"]);
                if (plan["scale"]?.Type != JTokenType.Integer || (int)plan["scale"] < 1 || (int)plan["scale"] > 24000)
                    throw new ArgumentException("room_plan.scale must be an explicit integer in 1..24000.");
                if (plan["margin"] == null || (plan["margin"].Type != JTokenType.Integer && plan["margin"].Type != JTokenType.Float) ||
                    !Finite(plan["margin"].Value<double>()) || plan["margin"].Value<double>() < 0)
                    throw new ArgumentException("room_plan.margin must be a finite nonnegative number.");
                string units = Text(plan, "units");
                if (units != "mm" && units != "m" && units != "feet") throw new ArgumentException("room_plan.units must be mm, m or feet.");
                Text(plan, "name_pattern");
                if (plan["orient_to_walls"]?.Type != JTokenType.Boolean) throw new ArgumentException("Specify room_plan.orient_to_walls.");
                var kinds = plan["kinds"] as JArray;
                if (kinds == null || kinds.Count == 0 || kinds.Any(k => k.Type != JTokenType.String) || kinds.Values<string>().Distinct().Count() != kinds.Count)
                    throw new ArgumentException("Specify unique room_plan.kinds.");
                var viewTypes = w["view_types"] as JObject ?? new JObject();
                if (w["view_types"] != null && !(w["view_types"] is JObject)) throw new ArgumentException("view_types must be an object.");
                Fields(viewTypes, "section", "elevation");
                var templates = w["view_templates"] as JObject ?? new JObject();
                if (w["view_templates"] != null && !(w["view_templates"] is JObject)) throw new ArgumentException("view_templates must be an object.");
                Fields(templates, "plan", "section", "elevation");
                if (plan["template_view_id"] != null)
                {
                    Id(plan["template_view_id"]);
                    if (kinds.Count != 1 || w["view_templates"] != null) throw new ArgumentException("A single room_plan template requires one kind only; otherwise specify view_templates per kind.");
                }
                else foreach (string kind in kinds.Values<string>())
                    Id(templates[kind == "sections" ? "section" : kind == "elevations" ? "elevation" : kind]);
                foreach (string kind in kinds.Values<string>())
                {
                    if (kind == "sections") Id(viewTypes["section"]);
                    else if (kind == "elevations")
                    {
                        Id(viewTypes["elevation"]);
                        if (plan["elevation_count"]?.Type != JTokenType.Integer || (int)plan["elevation_count"] < 1 || (int)plan["elevation_count"] > 4)
                            throw new ArgumentException("Specify elevation_count in 1..4.");
                    }
                    else if (kind != "plan") throw new ArgumentException("room_plan.kinds must be plan, sections or elevations.");
                }
                if (planRooms == null) throw new ArgumentException("document_rooms requires the model's room planner.");
                var args = (JObject)plan.DeepClone(); args["operation"] = "room_views";
                JObject answer = planRooms(args);
                if (answer.Value<int?>("rooms_excluded") != 0 || answer.Value<int?>("rooms_planned") != roomIds.Count || answer.Value<bool?>("safe_to_execute") != true)
                    throw new ArgumentException("Room planning was incomplete. Inspect horizun_plan_views exclusions before applying; nothing was written.");
                var next = answer["next_arguments"] as JObject ?? throw new ArgumentException("Room planner returned no typed action graph.");
                var actions = (JArray)next["actions"].DeepClone();
                var enhanced = new JArray();
                foreach (JObject action in actions)
                {
                    string op = action.Value<string>("operation");
                    if (op == "create_section") action["view_family_type_id"] = Id(viewTypes["section"]);
                    if (op == "create_elevation") action["view_family_type_id"] = Id(viewTypes["elevation"]);
                    if (op == "create_section" || op == "create_elevation" || op == "duplicate_view" || op == "apply_template")
                        action["view_scale"] = plan["scale"].DeepClone();
                    enhanced.Add(action);
                    string templateKind = op == "create_section" ? "section" : op == "create_elevation" ? "elevation" : op == "duplicate_view" ? "plan" : null;
                    if (templateKind != null && plan["template_view_id"] == null)
                        enhanced.Add(new JObject { ["operation"] = "apply_template", ["view_key"] = action["key"].DeepClone(),
                            ["template_view_id"] = Id(templates[templateKind]), ["view_scale"] = plan["scale"].DeepClone() });
                }
                actions = enhanced;
                AddRoomSheets(w["room_sheets"], answer, actions);
                if (actions.Count > 500) throw new ArgumentException("Room documentation exceeds 500 actions; reduce the explicitly selected rooms.");
                children.Add(Action("room_documentation", "horizun_manage_views", new JObject { ["units"] = units, ["actions"] = actions }));
            }
            else if (name == "pin_elements")
            {
                Fields(w,"name","element_ids");
                var ids = Ids(w["element_ids"]);
                children.Add(Action("pin", "horizun_transform_elements", new JObject {
                    ["operations"]=new JArray(new JObject { ["operation"]="pin", ["element_ids"]=ids }) }));
            }
            else if (name == "apply_view_template")
            {
                Fields(w,"name","view_ids","template_view_id");
                long template = Id(w["template_view_id"]);
                var actions = new JArray(Ids(w["view_ids"]).Select(id => new JObject {
                    ["operation"]="apply_template", ["view_id"]=id.DeepClone(), ["template_view_id"]=template }));
                children.Add(Action("templates","horizun_manage_views",new JObject { ["actions"]=actions }));
            }
            else if (name == "prepare_sheet_set")
            {
                Fields(w,"name","sheets","units");
                string units=w.Value<string>("units")??"mm";
                if (units!="mm" && units!="m" && units!="feet") throw new ArgumentException("workflow.units must be mm, m or feet.");
                var sheets=w["sheets"] as JArray;
                if(sheets==null || sheets.Count<1 || sheets.Count>100) throw new ArgumentException("workflow.sheets requires 1..100 entries.");
                var actions=new JArray(); var views=new HashSet<long>(); var numbers=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for(int i=0;i<sheets.Count;i++)
                {
                    var sheet=sheets[i] as JObject??throw new ArgumentException("Each sheet must be an object.");
                    Fields(sheet,"view_id","template_view_id","number","name","title_block_type_id","point");
                    long view=Id(sheet["view_id"]); string number=Text(sheet,"number"); string title=Text(sheet,"name");
                    if(!views.Add(view) || !numbers.Add(number)) throw new ArgumentException("Each workflow view and sheet number must be unique.");
                    var point=sheet["point"] as JArray;
                    if(point==null || point.Count<2 || point.Count>3 || point.Any(p=>p.Type!=JTokenType.Float && p.Type!=JTokenType.Integer))
                        throw new ArgumentException("Each sheet requires an explicit numeric placement point of length 2 or 3.");
                    if(point.Count==3 && point[2].Value<double>()!=0)
                        throw new ArgumentException("Sheet placement uses X/Y coordinates; an optional Z must be zero.");
                    if(sheet["template_view_id"]!=null) actions.Add(new JObject { ["operation"]="apply_template",["view_id"]=view,["template_view_id"]=Id(sheet["template_view_id"]) });
                    string key="sheet_"+i;
                    actions.Add(new JObject { ["operation"]="create_sheet",["key"]=key,["number"]=number,["name"]=title,["title_block_type_id"]=Id(sheet["title_block_type_id"]) });
                    actions.Add(new JObject { ["operation"]="place_view",["sheet_key"]=key,["view_id"]=view,["point"]=point.DeepClone() });
                }
                children.Add(Action("sheet_set","horizun_manage_views",new JObject { ["units"]=units,["actions"]=actions }));
            }
            else throw new ArgumentException("Unknown workflow. Choose pin_elements, apply_view_template, prepare_sheet_set or document_rooms.");
            var expanded=(JObject)request.DeepClone(); expanded["actions"]=children;
            return expanded;
        }
        static JObject Action(string key,string tool,JObject args)=>new JObject { ["key"]=key,["tool"]=tool,["arguments"]=args };
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static void AddRoomSheets(JToken token, JObject plan, JArray actions)
        {
            if (token == null) return;
            var sheets = token as JArray ?? throw new ArgumentException("room_sheets must be an array.");
            if (sheets.Count < 1 || sheets.Count > 100) throw new ArgumentException("room_sheets requires 1..100 entries.");
            var usedViews = new HashSet<string>(StringComparer.Ordinal);
            var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JObject sheet in sheets.OfType<JObject>())
            {
                Fields(sheet, "room_id", "number", "name", "title_block_type_id", "placements");
                long roomId = Id(sheet["room_id"]);
                JObject room = ((JArray)plan["rooms"]).OfType<JObject>().SingleOrDefault(r => r.Value<long>("room_id") == roomId)
                    ?? throw new ArgumentException("room_sheets names a room outside the plan.");
                string number = Text(sheet, "number");
                if (!numbers.Add(number)) throw new ArgumentException("Duplicate room sheet number.");
                string key = "room_sheet_" + numbers.Count;
                actions.Add(new JObject { ["operation"] = "create_sheet", ["key"] = key, ["number"] = number,
                    ["name"] = Text(sheet, "name"), ["title_block_type_id"] = Id(sheet["title_block_type_id"]) });
                var placements = sheet["placements"] as JArray ?? throw new ArgumentException("Room sheets require placements.");
                if (placements.Count == 0 || placements.Any(p => !(p is JObject))) throw new ArgumentException("Room sheets require object placements.");
                foreach (JObject placement in placements)
                {
                    Fields(placement, "kind", "index", "point");
                    string kind = Text(placement, "kind"); long index = Id(placement["index"]);
                    JObject view = ((JArray)room["views"]).OfType<JObject>().SingleOrDefault(v => v.Value<string>("kind") == kind && v.Value<long>("index") == index)
                        ?? throw new ArgumentException("Placement does not identify a planned room view.");
                    string viewKey = Text(view, "view_key");
                    if (!usedViews.Add(viewKey)) throw new ArgumentException("A room view cannot be placed twice.");
                    var point = placement["point"] as JArray;
                    if (point == null || point.Count != 2 || point.Any(p => (p.Type != JTokenType.Integer && p.Type != JTokenType.Float) || !Finite(p.Value<double>())))
                        throw new ArgumentException("Room placement point requires two finite coordinates.");
                    actions.Add(new JObject { ["operation"] = "place_view", ["sheet_key"] = key, ["view_key"] = viewKey, ["point"] = point.DeepClone() });
                }
            }
            if (sheets.OfType<JObject>().Count() != sheets.Count) throw new ArgumentException("Every room_sheets entry must be an object.");
        }
        static long Id(JToken token)
        {
            if(token?.Type!=JTokenType.Integer || !long.TryParse(token.ToString(),out long id) || id<=0)
                throw new ArgumentException("Workflow element references must be explicit positive integer IDs.");
            return id;
        }
        static JArray Ids(JToken token)
        {
            var list=token as JArray;
            if(list==null || list.Count<1 || list.Count>500) throw new ArgumentException("Workflow target IDs require 1..500 entries.");
            var ids=list.Select(Id).ToList();
            if(ids.Distinct().Count()!=ids.Count) throw new ArgumentException("Duplicate workflow targets are not allowed.");
            return new JArray(ids);
        }
        static string Text(JObject value,string key)
        {
            if(value[key]?.Type!=JTokenType.String || string.IsNullOrWhiteSpace((string)value[key])) throw new ArgumentException("Each sheet requires '"+key+"'.");
            return (string)value[key];
        }
        static void Fields(JObject value,params string[] allowed)
        { foreach(var p in value.Properties()) if(!allowed.Contains(p.Name)) throw new ArgumentException("Unknown workflow field: "+p.Name); }
    }
}
