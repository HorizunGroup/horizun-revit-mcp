using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Project-supplied references, never a compiled-in drawing standard.</summary>
    public static class SourceTrace
    {
        public static void Validate(JObject trace)
        {
            if (trace == null) throw new ArgumentException("source_reference must be an object.");
            var allowed = new HashSet<string>(new[] { "document_id", "document_sha256", "revision", "page", "region", "method", "assumption", "confidence", "measurements" });
            foreach (var field in trace.Properties()) if (!allowed.Contains(field.Name)) throw new ArgumentException("Unknown source_reference field: " + field.Name);
            if (string.IsNullOrWhiteSpace(trace.Value<string>("document_id"))) throw new ArgumentException("source_reference.document_id is required.");
            if (!Regex.IsMatch(trace.Value<string>("document_sha256") ?? "", "^[a-fA-F0-9]{64}$")) throw new ArgumentException("source_reference.document_sha256 must have 64 hex characters.");
            if (trace["page"]?.Type != JTokenType.Integer || trace.Value<int>("page") < 1) throw new ArgumentException("source_reference.page is one-based.");
            string method = trace.Value<string>("method");
            if (method != "dimension" && method != "scaled_measurement" && method != "assumption") throw new ArgumentException("source_reference.method must be dimension, scaled_measurement or assumption.");
            if (method != "dimension" && string.IsNullOrWhiteSpace(trace.Value<string>("assumption"))) throw new ArgumentException("An inferred measurement needs its assumption.");
            if (trace["confidence"] != null) { double c = GeometryInput.Number(trace["confidence"], "confidence"); if (c < 0 || c > 1) throw new ArgumentException("confidence must be in [0,1]."); }
            if (trace["region"] != null)
            {
                if (!(trace["region"] is JArray region) || region.Count != 4) throw new ArgumentException("region is [x,y,width,height] in PDF points, top-left origin.");
                for (int i = 0; i < 4; i++) if (GeometryInput.Number(region[i], "region") < 0 || (i >= 2 && region[i].Value<double>() == 0)) throw new ArgumentException("Invalid PDF region dimensions.");
            }
            if (!(trace["measurements"] is JArray measures) || measures.Count < 1 || measures.Count > 100) throw new ArgumentException("source_reference.measurements needs 1..100 entries.");
            var seen = new HashSet<string>();
            foreach (var value in measures)
            {
                if (!(value is JObject measure)) throw new ArgumentException("Each source measurement must be an object.");
                foreach (var field in measure.Properties()) if (!new[] { "property", "value", "unit", "tolerance", "reference" }.Contains(field.Name)) throw new ArgumentException("Unknown measurement field: " + field.Name);
                if (string.IsNullOrWhiteSpace(measure.Value<string>("property")) || !seen.Add(measure.Value<string>("property"))) throw new ArgumentException("Source measurement properties must be unique and named.");
                GeometryInput.Number(measure["value"], "value");
                if (GeometryInput.Number(measure["tolerance"], "tolerance") < 0) throw new ArgumentException("Measurement tolerance cannot be negative.");
                if (!new[] { "mm", "m", "feet", "ratio", "degrees", "radians" }.Contains(measure.Value<string>("unit"))) throw new ArgumentException("Unsupported source measurement unit.");
                if (string.IsNullOrWhiteSpace(measure.Value<string>("reference"))) throw new ArgumentException("Each measurement must name the dimension/reference used.");
            }
        }
        public static JObject Compare(JObject trace, JObject postconditions)
        {
            Validate(trace); var rows = new JArray(); bool matches = true;
            foreach (JObject desired in (JArray)trace["measurements"])
            {
                string property = desired.Value<string>("property"), unit = desired.Value<string>("unit");
                var actual = ((JArray)postconditions["properties"]).OfType<JObject>().SingleOrDefault(x => x.Value<string>("property") == property);
                string actualUnit = actual?.Value<string>("unit");
                bool compatible = (actualUnit == "feet" && new[] { "mm", "m", "feet" }.Contains(unit)) ||
                    (actualUnit == "rise/run" && unit == "ratio") || (actualUnit == "radians" && (unit == "degrees" || unit == "radians"));
                double scale = unit == "mm" ? 1 / 304.8 : unit == "m" ? 1 / 0.3048 : unit == "degrees" ? Math.PI / 180 : 1;
                bool measured = compatible && actual?.Value<bool?>("measured") == true &&
                    (actual["found_in_committed_model"].Type == JTokenType.Float || actual["found_in_committed_model"].Type == JTokenType.Integer);
                double wanted = desired.Value<double>("value") * scale, tolerance = desired.Value<double>("tolerance") * scale;
                double? found = measured ? (double?)actual["found_in_committed_model"].Value<double>() : null;
                bool match = found.HasValue && Math.Abs(found.Value - wanted) <= tolerance;
                matches &= match;
                rows.Add(new JObject
                {
                    ["property"] = property,
                    ["reference"] = desired["reference"],
                    ["requested"] = wanted,
                    ["measured"] = found,
                    ["unit"] = actualUnit,
                    ["tolerance"] = tolerance,
                    ["matches"] = match,
                    ["status"] = measured ? (match ? "match" : "discrepancy") : "unmeasured_or_incompatible"
                });
            }
            return new JObject { ["matches"] = matches, ["page"] = trace["page"], ["method"] = trace["method"], ["measurements"] = rows };
        }
    }
}
