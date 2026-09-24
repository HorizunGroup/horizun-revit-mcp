// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_federation_check. Original Horizun code. READ-ONLY.
//
// Reads the host and every LOADED link: element counts per model category (with
// sample ids), each link instance's title, workset, phase and whether its shared
// coordinates agree with the host's. The judging is Core/FederationCheckRules.cs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class FederationCheckCommand : ICommand
    {
        public string Name => "horizun_federation_check";

        public string Description =>
            "Federation QA against declared rules: out-of-place categories, expected/missing/duplicate links, same-site " +
            "coordinates, link worksets and phases. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
            if (wrong != null) return wrong;

            JObject rules = request["rules"] as JObject;
            string invalid = FederationCheckRules.Validate(rules);
            if (invalid != null) return CommandResult.Fail("The federation rules were refused: " + invalid);
            double tolerance = request.Value<double?>("tolerance_mm") ?? 10.0;
            if (tolerance < 0) return CommandResult.Fail("tolerance_mm must be >= 0.");
            int maxItems = Math.Max(1, request.Value<int?>("max_items") ?? 50);

            bool needCategories = rules["models"] != null;
            var models = new List<FederationModelFact>();
            if (needCategories) models.Add(ModelFact(doc, doc.Title, true, maxItems));

            var links = new List<FederationLinkFact>();
            var seenDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var notLoaded = new JArray();
            foreach (RevitLinkInstance inst in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document linkDoc = null;
                try { linkDoc = inst.GetLinkDocument(); } catch { }
                var type = doc.GetElement(inst.GetTypeId()) as RevitLinkType;
                string title = linkDoc?.Title ?? Path.GetFileNameWithoutExtension(CodeCheckCommand.SafeName(type) ?? "");
                var f = new FederationLinkFact
                {
                    InstanceId = Rid.Value(inst.Id), Title = title, Loaded = linkDoc != null,
                    Workset = WorksetName(doc, inst),
                    Phase = PhaseName(inst)
                };
                if (linkDoc == null)
                {
                    f.SiteWhyNot = "the link is not loaded: there is no link document to read its shared coordinates from.";
                    notLoaded.Add(f.InstanceId);
                }
                else
                {
                    SameSite(doc, inst, linkDoc, f);
                    if (needCategories && seenDocs.Add(linkDoc.PathName ?? title))
                        models.Add(ModelFact(linkDoc, title, false, maxItems));
                }
                links.Add(f);
            }

            JObject result = FederationCheckRules.Evaluate(rules, models, links, tolerance, maxItems);
            result["document"] = doc.Title;
            result["links_not_loaded"] = notLoaded;
            result["not_loaded_means"] = "an unloaded link's content and coordinates cannot be read: its models row is absent and its site is not_decidable.";
            return CommandResult.Ok(result);
        }

        private static FederationModelFact ModelFact(Document d, string title, bool isHost, int maxItems)
        {
            var f = new FederationModelFact { Title = title, IsHost = isHost };
            foreach (Element e in new FilteredElementCollector(d).WhereElementIsNotElementType())
            {
                Category c;
                try { c = e.Category; } catch { continue; }
                if (c == null || c.CategoryType != CategoryType.Model || e is RevitLinkInstance) continue;
                string token = CodeCheckCommand.CategoryToken(c) ?? CodeCheckCommand.SafeCatName(c);
                if (token == null) continue;
                f.CategoryCounts[token] = (f.CategoryCounts.TryGetValue(token, out int n) ? n : 0) + 1;
                if (!f.CategoryNames.ContainsKey(token)) f.CategoryNames[token] = CodeCheckCommand.SafeCatName(c);
                if (!f.SampleIds.TryGetValue(token, out List<long> ids)) f.SampleIds[token] = ids = new List<long>();
                if (ids.Count < maxItems) ids.Add(Rid.Value(e.Id));
            }
            return f;
        }

        /// <summary>Three link points to shared coordinates two ways; the largest disagreement, in mm.</summary>
        private static void SameSite(Document host, RevitLinkInstance inst, Document linkDoc, FederationLinkFact f)
        {
            try
            {
                ProjectLocation hostLoc = host.ActiveProjectLocation, linkLoc = linkDoc.ActiveProjectLocation;
                f.LinkSiteName = linkLoc?.Name;
                if (hostLoc == null || linkLoc == null) { f.SiteWhyNot = "a project location could not be read."; return; }
                Transform t = inst.GetTotalTransform();
                double worst = 0;
                foreach (XYZ q in new[] { XYZ.Zero, new XYZ(100, 0, 0), new XYZ(0, 100, 0) })
                {
                    ProjectPosition viaLink = linkLoc.GetProjectPosition(q);
                    ProjectPosition viaHost = hostLoc.GetProjectPosition(t.OfPoint(q));
                    double dx = viaLink.EastWest - viaHost.EastWest, dy = viaLink.NorthSouth - viaHost.NorthSouth,
                           dz = viaLink.Elevation - viaHost.Elevation;
                    worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy + dz * dz) * 304.8);
                }
                f.SiteDeltaMm = worst;
            }
            catch (Exception ex) { f.SiteWhyNot = "shared coordinates could not be compared: " + ex.Message; }
        }

        private static string WorksetName(Document doc, Element e)
        {
            try { return doc.IsWorkshared ? doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name : null; }
            catch { return null; }
        }

        private static string PhaseName(Element e)
        {
            try
            {
                Parameter p = e.get_Parameter(BuiltInParameter.PHASE_CREATED);
                return p == null || !p.HasValue ? null : p.AsValueString();
            }
            catch { return null; }
        }
    }
}
