using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PlanPreview
    {
        public const int MaxRows=50;
        public const int MaxBytes=32*1024;
        public static JObject Describe(ResolvedPlan plan)
        {
            var rows=new JArray(); int bytes=0;
            foreach(var e in plan.Elements.OrderBy(x=>x.UniqueId,StringComparer.Ordinal).ThenBy(x=>x.Action))
            {
                if(rows.Count>=MaxRows) break;
                bool shortened=false;
                var row=new JObject {
                    ["unique_id"]=Clip(e.UniqueId,ref shortened),
                    ["element_id"]=e.ElementId.HasValue?(JToken)e.ElementId.Value:JValue.CreateNull(),
                    ["action"]=e.Action.ToString().ToLowerInvariant(),
                    ["category"]=Clip(e.Category,ref shortened), ["type"]=Clip(e.TypeName,ref shortened),
                    ["captured_state"]=Values(e.BeforeValues,ref shortened),
                    ["proposed_values"]=Values(e.ProposedValues,ref shortened)
                };
                row["values_truncated"]=shortened;
                int size=Encoding.UTF8.GetByteCount(row.ToString(Formatting.None));
                if(bytes+size>MaxBytes-4096) break;
                rows.Add(row); bytes+=size;
            }
            return new JObject {
                ["fingerprint"]=plan.Fingerprint(), ["total"]=plan.Elements.Count, ["shown"]=rows.Count,
                ["truncated"]=rows.Count<plan.Elements.Count, ["expected_cascade"]=plan.ExpectedCascadeCount,
                ["rows"]=rows,
                ["note"]="Captured state contains facts read by the command; proposed_values are populated where supported. Null proposed_values means no standardized value preview is available. This bounded sample does not replace each command's full plan/errors. Confirmation covers the entire resolved plan, including omitted rows and full values."
            };
        }
        static string Clip(string value,ref bool shortened)
        { if(value==null || value.Length<=256)return value; shortened=true; return value.Substring(0,256)+"…"; }
        static JToken Values(IDictionary<string,string> values,ref bool shortened)
        {
            if(values==null)return JValue.CreateNull();
            var result=new JObject(); int n=0;
            foreach(var p in values.OrderBy(x=>x.Key,StringComparer.Ordinal))
            {
                if(n++>=25){shortened=true;break;}
                // Truncating dictionary keys can merge distinct parameters. Omit an
                // oversized key explicitly rather than show one under another's name.
                if(p.Key.Length>256){shortened=true;continue;}
                result[p.Key]=Clip(p.Value,ref shortened);
            }
            return result;
        }
    }
}
