using System;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ProductionOptimizationTests
    {
        [Fact] public void Cache_is_copy_isolated_expires_and_rejects_stale_epoch()
        {
            var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
            var cache = new BoundedReadCache(2, 1000, TimeSpan.FromSeconds(5), () => now);
            long epoch = cache.Epoch;
            var data = JObject.Parse("{'value':1}");
            Assert.True(cache.Store("a", epoch, data)); data["value"] = 2;
            Assert.True(cache.TryGet("a", epoch, out var read)); Assert.Equal(1, (int)read["value"]);
            read["value"] = 3;
            Assert.True(cache.TryGet("a", epoch, out read)); Assert.Equal(1, (int)read["value"]);
            now = now.AddSeconds(5); Assert.False(cache.TryGet("a", epoch, out read));
            cache.Invalidate(); Assert.False(cache.Store("b", epoch, data));
            Assert.False(cache.TryGet("b", epoch, out read));
        }
        [Fact] public void Cache_enforces_entry_byte_budgets_and_lru()
        {
            var c = new BoundedReadCache(2, 1000);
            c.Store("a", c.Epoch, new JObject()); c.Store("b", c.Epoch, new JObject());
            c.TryGet("a", c.Epoch, out _); c.Store("c", c.Epoch, new JObject());
            Assert.False(c.TryGet("b", c.Epoch, out _));
            Assert.True(c.TryGet("a", c.Epoch, out _));
            Assert.False(c.Store("large", c.Epoch, new JObject { ["text"] = new string('x', 1000) }));
            Assert.Equal(2, c.Count);
            c.Invalidate(); Assert.Equal(0, c.Count);
        }
        [Fact] public void Streaming_summary_keeps_full_mode_casing_and_blank_semantics()
        {
            var s = new QuerySummaryAccumulator();
            s.Add("walls", "", "host", "Model", null, 20);
            s.Add("Walls", " ", "host", "Model", null, 10);
            s.Add(null, null, "link", "Link", 50, 1);
            var expected = new JObject {
                ["by_category"] = JsonObjectKey.SummaryCounts(new[] { "Walls", "walls", "(no category)" }),
                ["by_level"] = JsonObjectKey.SummaryCounts(new[] { " ", "", "(no level)" }),
                ["by_source"] = JsonObjectKey.SummaryCounts(new[] { "host:Model", "host:Model", "link:Link" }) };
            Assert.Equal(3, s.Count); Assert.True(JToken.DeepEquals(expected, s.ToJson()));
        }
        [Fact] public void Progressive_scan_keeps_atomic_geometry_coverage_and_hidden_page_recoverable()
        {
            var root = JObject.Parse("{'complete':false,'sections':{'health':{'total':10,'returned':5,'truncated':true,'next_cursor':'skip-five','items':[{'polygon':[1,2,3,4,5]},2,3,4,5]},'geometry':[1,2,3,4]}}");
            var result = ProgressiveResponse.Scan(root, JObject.Parse("{'response_mode':'summary'}"));
            Assert.False((bool)result["complete"]);
            Assert.Equal(3, (int)result["sections"]["health"]["returned"]);
            Assert.Null(result["sections"]["health"]["next_cursor"]);
            Assert.Equal(5, ((JArray)result["sections"]["health"]["items"][0]["polygon"]).Count);
            Assert.Equal(4, ((JArray)result["sections"]["geometry"]).Count);
            Assert.Equal(5, ((JArray)root["sections"]["health"]["items"]).Count);
            Assert.Equal("full", (string)result["expand"]["arguments"]["response_mode"]);
            Assert.Single((JArray)result["response_omissions"]);
        }
        static JObject Recipe(string name = "rectangular_prism") => JObject.Parse(
            "{'recipe':{'name':'" + name + "','width':200,'depth':300,'height_parameter':'Height','types':[{'name':'Short','height':500},{'name':'Tall','height':1000}]}}");
        [Fact] public void Progressive_scan_does_not_claim_complete_detail_for_an_already_truncated_small_page()
        {
            var root = JObject.Parse("{'complete':true,'sections':{'x':{'items':[1],'total':10,'returned':1,'truncated':true}}}");
            var result = ProgressiveResponse.Scan(root, JObject.Parse("{'response_mode':'summary'}"));
            Assert.False((bool)result["response_detail_complete"]);
            Assert.Empty((JArray)result["response_omissions"]);
            Assert.True((bool)result["complete"]);
        }
        [Theory][InlineData("rectangular_prism",1)][InlineData("rectangular_tube",2)]
        public void Recipes_compile_to_associated_extrusions_and_force_acceptance_artifacts(string name, int loops)
        {
            var r = Recipe(name); if (loops == 2) r["recipe"]["wall"] = 20;
            var built = FamilyRecipe.Expand(r);
            Assert.Null(built["recipe"]); Assert.NotNull(r["recipe"]);
            Assert.True((bool)built["flex"]); Assert.True((bool)built["emit_thumbnail"]);
            Assert.Equal("Height", (string)built["forms"][0]["end_parameter"]);
            Assert.Equal(loops, ((JArray)built["forms"][0]["profile"]).Count);
            Assert.Equal(1000, (double)built["types"][1]["values"]["Height"]);
        }
        [Theory][InlineData("forms")][InlineData("parameters")][InlineData("connectors")]
        public void Recipe_refuses_mixed_raw_spec(string key)
        { var r = Recipe(); r[key] = new JArray(); Assert.Throws<ArgumentException>(() => FamilyRecipe.Expand(r)); }
        [Fact] public void Recipe_refuses_no_flex_and_closed_tube()
        {
            var r = Recipe(); r["recipe"]["types"][1]["height"] = 500;
            Assert.Throws<ArgumentException>(() => FamilyRecipe.Expand(r));
            r = Recipe("rectangular_tube"); r["recipe"]["wall"] = 100;
            Assert.Throws<ArgumentException>(() => FamilyRecipe.Expand(r));
            r = Recipe(); r["flex"] = false; Assert.Throws<ArgumentException>(() => FamilyRecipe.Expand(r));
        }
        static JObject Rooms() => JObject.Parse(@"{
          'workflow':{'name':'document_rooms','room_plan':{'room_ids':[1],'plan_view_id':10,'template_view_id':20,
          'kinds':['plan'],'units':'mm','scale':50,'margin':500,'name_pattern':'{room_number}-{kind}','orient_to_walls':true},
          'room_sheets':[{'room_id':1,'number':'A01','name':'Room','title_block_type_id':30,
          'placements':[{'kind':'plan','index':1,'point':[200,150]}]}]}}");
        static JObject Planned() => JObject.Parse(@"{
          'rooms_excluded':0,'rooms_planned':1,'safe_to_execute':true,
          'rooms':[{'room_id':1,'views':[{'kind':'plan','index':1,'view_key':'room-1-plan-1'}]}],
          'next_arguments':{'actions':[{'operation':'duplicate_view','key':'room-1-plan-1','source_view_id':10}]}}");
        [Fact] public void Room_workflow_maps_planned_view_alias_to_explicit_sheet_and_scale()
        {
            var r = Rooms();
            var built = WorkflowPlan.Expand(r, args => { Assert.Equal("room_views",(string)args["operation"]); return Planned(); });
            var actions = (JArray)built["actions"][0]["arguments"]["actions"];
            Assert.Equal(3, actions.Count); Assert.Equal(50,(int)actions[0]["view_scale"]);
            Assert.Equal(actions[0]["key"], actions[2]["view_key"]);
            Assert.Equal(actions[1]["key"], actions[2]["sheet_key"]);
            Assert.Null(r["actions"]);
        }
        [Fact] public void Room_workflow_refuses_partial_plans_and_ambiguous_targets()
        {
            Assert.Throws<ArgumentException>(() => WorkflowPlan.Expand(Rooms()));
            Assert.Throws<ArgumentException>(() => WorkflowPlan.Expand(Rooms(), _ => { var p = Planned(); p["rooms_excluded"] = 1; return p; }));
            var r = Rooms(); r["workflow"]["room_plan"]["room_ids"] = new JArray(1,1);
            Assert.Throws<ArgumentException>(() => WorkflowPlan.Expand(r, _ => Planned()));
            r = Rooms(); r["workflow"]["room_sheets"][0]["placements"][0]["point"] = new JArray(1,2,3);
            Assert.Throws<ArgumentException>(() => WorkflowPlan.Expand(r, _ => Planned()));
        }
        [Fact] public void Room_workflow_attaches_per_kind_templates_without_implicit_cross_kind_choice()
        {
            var r = Rooms();
            ((JObject)r["workflow"]["room_plan"]).Remove("template_view_id");
            r["workflow"]["view_templates"] = JObject.Parse("{'plan':40}");
            var result = WorkflowPlan.Expand(r, _ => Planned());
            var a = (JArray)result["actions"][0]["arguments"]["actions"];
            Assert.Equal("apply_template", (string)a[1]["operation"]);
            Assert.Equal(40, (long)a[1]["template_view_id"]);
            Assert.Equal(a[0]["key"], a[1]["view_key"]);
            Assert.Equal(50, (int)a[1]["view_scale"]);
            r["workflow"]["room_plan"]["kinds"] = new JArray("plan", "sections");
            Assert.Throws<ArgumentException>(() => WorkflowPlan.Expand(r, _ => Planned()));
        }
    }
}
