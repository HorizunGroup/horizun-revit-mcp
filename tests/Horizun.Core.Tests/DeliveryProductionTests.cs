using System;
using System.IO;
using System.Linq;
using System.Text;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
namespace Horizun.Core.Tests
{
    public class DeliveryProductionTests
    {
        [Fact] public void Manifest_roundtrip_preserves_iso_dates_strings_and_unicode()
        {
            string path=Path.Combine(Path.GetTempPath(),"hz-manifest-"+Guid.NewGuid().ToString("N")+".json");
            try
            {
                var manifest=new JObject { ["utc"]="2026-09-08T02:50:45.3380000Z",["document"]="Planta – revisión",["files"]=new JArray() };
                DeliveryPdf.WriteManifestVerified(path,manifest,false);
                Assert.Equal(manifest.ToString(Newtonsoft.Json.Formatting.Indented),File.ReadAllText(path));
            }
            finally { if(File.Exists(path)) File.Delete(path); }
        }
        [Fact] public void Manifest_never_overwrites_without_explicit_permission()
        {
            string path=Path.Combine(Path.GetTempPath(),"hz-manifest-"+Guid.NewGuid().ToString("N")+".json");
            try
            {
                var first=new JObject { ["id"]="first" };var second=new JObject { ["id"]="second" };
                DeliveryPdf.WriteManifestVerified(path,first,false);
                Assert.Throws<IOException>(()=>DeliveryPdf.WriteManifestVerified(path,second,false));
                Assert.Equal(first.ToString(Newtonsoft.Json.Formatting.Indented),File.ReadAllText(path));
                DeliveryPdf.WriteManifestVerified(path,second,true);
                Assert.Equal(second.ToString(Newtonsoft.Json.Formatting.Indented),File.ReadAllText(path));
            }
            finally { if(File.Exists(path)) File.Delete(path); }
        }
        [Theory][InlineData(50)][InlineData(100)][InlineData(200)]
        public void Paper_spacing_remains_constant_across_view_scales(int scale)
        {
            double feet=DeliveryLayoutRules.Distance(10,1.0/304.8,scale,"paper");
            Assert.Equal(10,feet*304.8/scale,8);
            Assert.Equal(10.0/304.8,DeliveryLayoutRules.Distance(10,1.0/304.8,scale,"model"),8);
        }
        [Fact] public void Invalid_distances_and_rectangles_refuse()
        {
            Assert.Throws<ArgumentException>(()=>DeliveryLayoutRules.Distance(double.NaN,1,100,"paper"));
            Assert.Throws<ArgumentException>(()=>DeliveryLayoutRules.Distance(10,1,100,"pixels"));
            Assert.Throws<ArgumentException>(()=>DeliveryLayoutRules.Distance(double.MaxValue,1,100,"paper"));
            Assert.Throws<ArgumentException>(()=>DeliveryLayoutRules.ReadBox(new JArray(2,0,1,3),1));
            Assert.Throws<ArgumentException>(()=>DeliveryLayoutRules.ReadBox(new JArray(0,0,0,0),1));
            Assert.Throws<ArgumentException>(()=>DeliveryLayoutRules.ReadBox(new JArray(0,0,10,10),-1));
        }
        [Fact] public void Packing_distinguishes_capacity_from_unreadable_geometry()
        {
            var paper=DeliveryLayoutRules.ReadBox(new JArray(0,0,100,100),1);
            var item=new PackingItem { Key="view",Width=200,Height=10 };
            var noFit=PlanimetryPackingRules.Pack(paper,new PlanBox[0],new[]{item},0,0,.01);
            Assert.False(noFit.Ok);Assert.True(noFit.NoFit);
            item.Width=10;
            var unknown=PlanimetryPackingRules.Pack(paper,new[]{PlanBox.Unreadable},new[]{item},0,0,.01);
            Assert.False(unknown.Ok);Assert.False(unknown.NoFit);
            Assert.True(PlanimetryPackingRules.Pack(paper,new PlanBox[0],new[]{item},0,0,.01).Ok);
        }
        [Fact] public void Real_extent_collisions_include_reserved_zones_and_unknown_boxes()
        {
            var candidate=DeliveryLayoutRules.ReadBox(new JArray(0,0,10,10),1);
            var occupied=DeliveryLayoutRules.ReadBox(new JArray(8,8,20,20),1);
            Assert.False(DeliveryLayoutRules.Clear(candidate,new[]{occupied},0));
            Assert.False(DeliveryLayoutRules.Clear(candidate,new[]{PlanBox.Unreadable},0));
            Assert.True(DeliveryLayoutRules.Clear(DeliveryLayoutRules.Shift(candidate,-20,0),new[]{occupied},2));
            Assert.False(DeliveryLayoutRules.Clear(candidate,new PlanBox[0],0,DeliveryLayoutRules.ReadBox(new JArray(0,0,5,5),1)));
        }
        [Fact] public void Separate_pdf_names_are_deterministic_and_duplicates_refuse()
        {
            string path=Path.Combine(Path.GetTempPath(),"package.pdf");
            string[] split=DeliveryPdf.Paths(path,false,new long[]{8,4});
            Assert.EndsWith("package-001-8.pdf",split[0]);Assert.EndsWith("package-002-4.pdf",split[1]);
            Assert.Equal(path,DeliveryPdf.Paths(path,true,new long[]{8,4})[0]);
            Assert.Throws<ArgumentException>(()=>DeliveryPdf.Paths(path,false,new long[]{8,8}));
        }
        [Fact] public void Pdf_evidence_reopens_real_pages_and_does_not_infer_visual_approval()
        {
            string path=Path.Combine(Path.GetTempPath(),"hz-pdf-"+Guid.NewGuid().ToString("N")+".pdf");
            try
            {
                File.WriteAllBytes(path,MinimalPdf());
                var ok=DeliveryPdf.Inspect(path,2);
                Assert.True((bool)ok["page_count_verified"]);Assert.Equal(2,(int)ok["pages"]);
                Assert.Equal(64,((string)ok["sha256"]).Length);Assert.Equal("not_performed",(string)ok["visual_approval"]);
                Assert.False((bool)DeliveryPdf.Inspect(path,3)["page_count_verified"]);
                File.WriteAllText(path,"not a PDF");
                Assert.ThrowsAny<Exception>(()=>DeliveryPdf.Inspect(path,1));
            }
            finally { if(File.Exists(path)) File.Delete(path); }
        }
        static byte[] MinimalPdf()
        {
            var b=new StringBuilder("%PDF-1.4\n");var offsets=new int[5];
            string[] objects={ "", "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> >>" };
            for(int i=1;i<=4;i++){ offsets[i]=b.Length;b.Append(i+" 0 obj\n"+objects[i]+"\nendobj\n"); }
            int xref=b.Length;b.Append("xref\n0 5\n0000000000 65535 f \n");
            for(int i=1;i<=4;i++)b.Append(offsets[i].ToString("D10")+" 00000 n \n");
            b.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n"+xref+"\n%%EOF\n");
            return Encoding.ASCII.GetBytes(b.ToString());
        }
        static JObject Profile()=>JObject.Parse(@"{
          'id':'fixture','version':'1','units':'mm',
          'views':[{'view_id':1,'dimension_sets':[{'role':'general','operation':'intent_dimension','offset':10,'side':'positive','dimension_type_id':10,'element_ids':[20,21]}],
                    'tags':{'element_ids':[20],'tag_type_id':30,'clearance':3,'max_displacement':20}}],
          'packing':{'sheet_id':2,'usable_rect':[0,0,800,500],'items':[{'key':'v','view_id':1}]},
          'publication':{'format':'pdf','view_ids':[2],'output_path':'C:/fixture.pdf'},
          'requirement_set':{'requirement_set':{'id':'fixture','version':'1'},'rules':[{
             'id':'number','entity':'sheet','selector':{'applies_to_all':true},
             'assertion':{'field':'sheet_number','operator':'matches','value':'^A'}}]}}");
        [Fact] public void Delivery_orders_remeasurement_review_and_publication_without_granting_approval()
        {
            JObject p=Profile();var plan=DeliveryPlan.Build(p,"Fixture");
            var stages=(JArray)plan["stages"];string[] keys=stages.Select(s=>(string)s["key"]).ToArray();
            Assert.True(Array.IndexOf(keys,"dimensions_1")<Array.IndexOf(keys,"tags_1"));
            Assert.True(Array.IndexOf(keys,"tags_1")<Array.IndexOf(keys,"pack"));
            Assert.True(Array.IndexOf(keys,"audit")<Array.IndexOf(keys,"publish"));
            Assert.True(Array.IndexOf(keys,"capture_sheet_2")<Array.IndexOf(keys,"publish"));
            Assert.False((bool)plan["publication_approved"]);
            Assert.True((bool)stages.Last["arguments"]["dry_run"]);
            Assert.True((bool)stages.Last["arguments"]["emit_manifest"]);
            Assert.Null(p["publication"]["dry_run"]);
        }
        [Fact] public void Delivery_refuses_unknown_standards_or_unreserved_titleblock_space()
        {
            var p=Profile();((JObject)p["packing"]).Remove("usable_rect");
            Assert.Throws<ArgumentException>(()=>DeliveryPlan.Build(p,"Fixture"));
            p=Profile();p["requirement_set"]=new JObject();
            Assert.ThrowsAny<Exception>(()=>DeliveryPlan.Build(p,"Fixture"));
        }
        [Fact] public void Delivery_refuses_unpublished_destinations_and_empty_dimension_sets()
        {
            var p=Profile();p["packing"]["sheet_id"]=3;
            Assert.Throws<ArgumentException>(()=>DeliveryPlan.Build(p,"Fixture"));
            p=Profile();p["views"][0]["dimension_sets"]=new JArray();
            Assert.Throws<ArgumentException>(()=>DeliveryPlan.Build(p,"Fixture"));
        }
        [Fact] public void Delivery_stage_arguments_use_published_tool_fields()
        {
            var stages=(JArray)DeliveryPlan.Build(Profile(),"Fixture")["stages"];
            foreach(JObject stage in stages)
            {
                var contract=Horizun.Contracts.Contract.All.Single(c=>c.Name==(string)stage["tool"]);
                var args=(JObject)stage["arguments"];
                var properties=(JObject)contract.InputSchema["properties"];
                foreach(var p in args.Properties()) Assert.NotNull(properties[p.Name]);
                foreach(var required in (JArray)contract.InputSchema["required"] ?? new JArray())
                    Assert.NotNull(args[(string)required]);
            }
        }
    }
}
