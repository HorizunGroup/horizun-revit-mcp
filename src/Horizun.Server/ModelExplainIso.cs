// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_model_diff operation=explain: what is missing for ISO 19650, read from
// project-context.json by the SAME evaluation horizun_project_context validate
// runs (schema, coherence, unanswered intake questions). Nothing is inferred to
// fill a gap: an absent path is "not_requested", an unreadable file says so.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class ModelExplainIso
    {
        internal static JObject Evaluate(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new JObject { ["status"] = "not_requested", ["why"] = "pass project_context_path to have the gaps evaluated." };
            if (!File.Exists(path))
                return new JObject { ["status"] = "not_found", ["path"] = path };
            try
            {
                JToken doc;
                string error;
                if (!ProjectContext.TryParse(File.ReadAllBytes(path), out doc, out error))
                    return new JObject { ["status"] = "unreadable", ["path"] = path, ["why"] = error };
                JObject report = ProjectContext.Evaluate(doc);
                return new JObject
                {
                    ["status"] = "evaluated",
                    ["path"] = path,
                    ["state"] = report["state"],
                    ["state_means"] = report["state_means"],
                    ["missing_topics"] = doc is JObject o ? new JArray(ProjectContext.MissingTopics(o).Cast<object>().ToArray()) : new JArray(),
                    ["missing"] = report["missing"],
                    ["errors"] = report["errors"],
                    ["coherence"] = report["coherence"],
                    ["documents_declared_missing"] = report["documents_declared_missing"],
                    ["source"] = "the evaluation horizun_project_context operation=validate runs"
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "unreadable", ["path"] = path, ["why"] = ex.Message };
            }
        }
    }
}
