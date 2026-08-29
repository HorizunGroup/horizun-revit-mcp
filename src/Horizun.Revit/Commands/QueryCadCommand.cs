// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_query_cad — what CAD this document carries, and what it actually says.
//
// Read-only. Four modes, each answering a question somebody has to answer before
// a single element is built from a drawing:
//
//   instances   WHICH drawings are here: linked or imported, from what path, in
//               what state, at what transform, in what declared units, and the
//               SHA-256 of the file when this machine can read it.
//   layers      WHAT the drawing is organised into, with a primitive census per
//               layer. A layer map is the first thing a reviewer checks, because
//               a rule that matches nothing is nearly always a misspelt pattern.
//   geometry    THE CURVES, in millimetres, with their layer and a stable
//               surrogate identity - bounded, paginated, and never a million
//               vertices in one reply.
//   coverage    WHAT THIS BRIDGE CANNOT READ. Published as a first-class answer
//               rather than left to be discovered: text is unreachable, hatches
//               arrive as zero-volume residue, no entity carries a handle.
//
// THE HONESTY THAT COSTS SOMETHING. Two facts were MEASURED on Revit 2026 and
// they shape every reply:
//
//   Text is not readable. Not "hard" - not readable. Zero strings are reachable
//   from imported geometry at any depth. Anything that hoped to read a room name
//   off a label has to be told, not left to find out.
//
//   GeometryObject.Id collides: 35 objects came back with 24 distinct ids. So
//   entity identity is a computed surrogate, and this command publishes both the
//   surrogate and the fact that it is derived.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class QueryCadCommand : ICommand
    {
        public string Name => "horizun_query_cad";

        public string Description =>
            "Read the CAD (DWG) surface of the active document: instances with link/import state, resolved path, " +
            "load status, transform, declared units and file SHA-256; layers with a primitive census; geometry as " +
            "millimetre curves with stable surrogate ids; and an explicit coverage block naming what Revit does " +
            "NOT expose. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            string mode = (request.Value<string>("mode") ?? "instances").ToLowerInvariant();
            if (mode != "instances" && mode != "layers" && mode != "geometry" && mode != "coverage" &&
                mode != "profile")
                return CommandResult.Fail("mode must be instances, layers, geometry, coverage or profile.");

            // A count of CAD over a half-loaded model is the canonical example of
            // a true-but-misleading answer, so coverage rides on every reply.
            DocumentVisibilityCoverage visibility = DocumentVisibility.Measure(doc);

            List<JObject> unreadable;
            List<CadInstanceFacts> instances = CadFacts.Collect(doc, out unreadable);

            if (mode == "instances")
                return CommandResult.Ok(new JObject
                {
                    ["mode"] = "instances",
                    ["document"] = SafeTitle(doc),
                    ["instances"] = new JArray(instances.Select(f =>
                    {
                        JObject o = f.ToJson();
                        o["source_fingerprint"] = CadFacts.SourceFingerprint(f);
                        return (JToken)o;
                    })),
                    ["count"] = instances.Count,
                    ["unreadable"] = new JArray(unreadable),
                    ["unreadable_means"] = "a CAD instance Revit would not identify. It is NOT absent, and it is " +
                                           "NOT counted in 'count' - a census that hides what it could not read is worse than none.",
                    ["visibility_coverage"] = visibility.ToJson(),
                    ["provenance"] = ProvenanceBlock()
                });

            if (mode == "coverage")
                return CommandResult.Ok(new JObject
                {
                    ["mode"] = "coverage",
                    ["document"] = SafeTitle(doc),
                    ["instances_found"] = instances.Count,
                    ["provenance"] = ProvenanceBlock(),
                    ["visibility_coverage"] = visibility.ToJson()
                });

            // layers and geometry both need ONE instance named.
            long instanceId = request.Value<long?>("instance_id") ?? -1;
            if (instanceId < 0)
                return CommandResult.Fail(
                    "instance_id is required for mode '" + mode + "'. List the candidates with mode='instances' " +
                    "first; there is no default CAD instance, and picking one for you is the guess this refuses.");
            if (!Rid.CanRepresent(instanceId))
                return CommandResult.Fail(Rid.RangeError(instanceId));

            Element element = doc.GetElement(Rid.Make(instanceId));
            if (element == null)
                return CommandResult.Fail("No element with id " + instanceId + " in '" + SafeTitle(doc) + "'.");
            if (!(element is ImportInstance))
                return CommandResult.Fail(
                    "Element " + instanceId + " is a " + element.GetType().Name + ", not an ImportInstance. " +
                    "mode='" + mode + "' reads CAD geometry, and reading a Revit element through it would " +
                    "report the wrong thing convincingly.");

            CadInstanceFacts facts = instances.FirstOrDefault(f => f.ElementId == instanceId);
            double sagitta = request.Value<double?>("arc_sagitta_mm") ?? 5.0;
            if (sagitta <= 0) return CommandResult.Fail("arc_sagitta_mm must be positive; it is how far a chord may depart from its arc.");
            int maxPrimitives = Math.Max(1, Math.Min(500000, request.Value<int?>("max_primitives") ?? 200000));

            CadHarvest harvest = CadGeometryHarvest.Harvest(doc, element, sagitta, maxPrimitives);

            if (mode == "layers")
            {
                var rows = harvest.LayerCounts
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new JObject
                    {
                        ["layer"] = kv.Key,
                        ["primitive_count"] = kv.Value,
                        ["segment_count"] = harvest.Segments.Count(s => string.Equals(s.Layer, kv.Key, StringComparison.OrdinalIgnoreCase))
                    });
                return CommandResult.Ok(new JObject
                {
                    ["mode"] = "layers",
                    ["document"] = SafeTitle(doc),
                    ["instance_id"] = instanceId,
                    ["instance_name"] = facts?.Name,
                    ["declared_units"] = facts?.DeclaredUnits,
                    ["layers"] = new JArray(rows),
                    ["layer_count"] = harvest.LayerCounts.Count,
                    ["primitives_by_class"] = new JObject(harvest.PrimitiveCounts
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new JProperty(kv.Key, kv.Value))),
                    ["primitives_without_a_layer"] = harvest.PrimitivesVisited - harvest.LayerCounts.Values.Sum(),
                    ["nested_instances"] = harvest.InstancePaths.Count,
                    ["harvest_coverage"] = harvest.CoverageJson(sagitta),
                    ["visibility_coverage"] = visibility.ToJson(),
                    ["provenance"] = ProvenanceBlock()
                });
            }

            if (mode == "profile")
            {
                int maxLayers = Math.Max(1, Math.Min(200, request.Value<int?>("max_layers") ?? 40));
                JObject profile = CadLayerProfiler.Profile(harvest.Segments, facts?.DeclaredUnits, maxLayers);
                profile["mode"] = "profile";
                profile["document"] = SafeTitle(doc);
                profile["instance_id"] = instanceId;
                profile["instance_name"] = facts?.Name;
                profile["declared_units"] = facts?.DeclaredUnits;
                profile["harvest_coverage"] = harvest.CoverageJson(sagitta);
                profile["visibility_coverage"] = visibility.ToJson();
                profile["read_only"] = true;
                profile["provenance"] = ProvenanceBlock();
                return CommandResult.Ok(profile);
            }

            // geometry: bounded and paginated, because a site plan is millions of vertices.
            string layerFilter = request.Value<string>("layer");
            int maxRows = Math.Max(1, Math.Min(5000, request.Value<int?>("max_rows") ?? 500));
            int offset = Math.Max(0, request.Value<int?>("offset") ?? 0);
            string sourceHash = facts?.FileSha256 ?? CadFacts.SourceFingerprint(facts) ?? "(no-source-identity)";

            List<CadSegment> matching = harvest.Segments
                .Where(s => layerFilter == null || CadGlob.IsMatch(s.Layer ?? "", layerFilter, false))
                .ToList();
            List<CadSegment> page = matching.Skip(offset).Take(maxRows).ToList();

            var segmentRows = page.Select(s => new JObject
            {
                ["surrogate_id"] = CadIdentity.SurrogateUndirected(sourceHash, s.Layer, "root", s.SourceKind,
                    new List<CadPoint> { s.A, s.B }, 1.0),
                ["layer"] = s.Layer,
                ["source_kind"] = s.SourceKind.ToString().ToLowerInvariant(),
                ["approximate"] = s.SourceKind == CadCurveKind.Arc || s.SourceKind == CadCurveKind.Spline,
                ["start_mm"] = new JArray(Round(s.A.X), Round(s.A.Y), Round(s.A.Z)),
                ["end_mm"] = new JArray(Round(s.B.X), Round(s.B.Y), Round(s.B.Z)),
                ["length_mm"] = Round(s.PlanLength)
            });

            // THE ARCS, AS ARCS.
            //
            // Every arc also appears above as chords, and the chords are what a
            // reader would otherwise have to work from - which is exactly what a
            // curved wall cannot be built or audited from. Two arcs through one
            // chord differ only in centre and radius, so those are published, and
            // published for the SAME page of layers the segments answer for.
            List<CadArcFact> matchingArcs = harvest.Arcs
                .Where(a => a != null && (layerFilter == null || CadGlob.IsMatch(a.Layer ?? "", layerFilter, false)))
                .ToList();
            List<CadArcFact> arcPage = matchingArcs.Skip(offset).Take(maxRows).ToList();

            Tuple<CadPoint, CadPoint> box = CadTopologyRules.BoundingBox(
                matching.SelectMany(s => new[] { s.A, s.B }));

            return CommandResult.Ok(new JObject
            {
                ["mode"] = "geometry",
                ["document"] = SafeTitle(doc),
                ["instance_id"] = instanceId,
                ["instance_name"] = facts?.Name,
                ["declared_units"] = facts?.DeclaredUnits,
                ["units_of_this_reply"] = "mm",
                ["source_hash"] = sourceHash,
                ["source_fingerprint"] = CadFacts.SourceFingerprint(facts),
                ["layer_filter"] = layerFilter,
                ["segments_matching"] = matching.Count,
                ["segments_returned"] = page.Count,
                ["offset"] = offset,
                ["truncated"] = offset + page.Count < matching.Count,
                ["bounding_box_mm"] = box == null ? (JToken)JValue.CreateNull() : new JObject
                {
                    ["min"] = new JArray(Round(box.Item1.X), Round(box.Item1.Y), Round(box.Item1.Z)),
                    ["max"] = new JArray(Round(box.Item2.X), Round(box.Item2.Y), Round(box.Item2.Z))
                },
                ["set_fingerprint"] = CadIdentity.SetFingerprint(matching.Select(s =>
                    CadIdentity.SurrogateUndirected(sourceHash, s.Layer, "root", s.SourceKind,
                        new List<CadPoint> { s.A, s.B }, 1.0))),
                ["segments"] = new JArray(segmentRows),
                ["arcs_matching"] = matchingArcs.Count,
                ["arcs_returned"] = arcPage.Count,
                ["arcs"] = new JArray(arcPage.Select(a => a.ToJson())),
                ["harvest_coverage"] = harvest.CoverageJson(sagitta),
                ["visibility_coverage"] = visibility.ToJson(),
                ["provenance"] = ProvenanceBlock()
            });
        }

        /// <summary>
        /// What a reader is trusting, per fact. Published on every reply because
        /// the difference between "Revit said so" and "we computed it" is the
        /// difference between evidence and inference.
        /// </summary>
        private static JObject ProvenanceBlock() => new JObject
        {
            ["layer_names"] = CadProvenanceKind.Native,
            ["layer_names_route"] = "GeometryObject.GraphicsStyleId -> GraphicsStyle.GraphicsStyleCategory.Name",
            ["transform"] = CadProvenanceKind.Native,
            ["declared_units"] = CadProvenanceKind.Native,
            ["declared_units_route"] = "the CADLinkType's 'Import Units' parameter - MEASURED: the instance's " +
                                       "IMPORT_DISPLAY_UNITS reads null",
            ["external_path"] = CadProvenanceKind.Native,
            ["file_sha256"] = CadProvenanceKind.Native,
            ["lines_and_polylines"] = CadProvenanceKind.Native,
            ["arcs_and_splines"] = CadProvenanceKind.Approximate,
            ["arcs_and_splines_means"] = "chorded to the declared sagitta; the segments are not what was drawn",
            ["entity_identity"] = CadProvenanceKind.Derived,
            ["entity_identity_means"] = "there is NO DWG handle anywhere in the Revit API, and GeometryObject.Id " +
                                        "COLLIDES (measured: 35 objects, 24 distinct ids). Identity is a hash of " +
                                        "source, layer, nesting and quantized geometry.",
            ["text"] = CadProvenanceKind.Unavailable,
            ["text_means"] = "MEASURED: zero strings are reachable from imported DWG geometry at any depth. Text " +
                             "arrives as curves on its own layer - the layer survives, the words do not.",
            ["block_names_and_attributes"] = CadProvenanceKind.Unavailable,
            ["block_names_means"] = "a block arrives as a nested GeometryInstance with a transform; its NAME and " +
                                    "its attributes are not exposed on this path.",
            ["hatches"] = CadProvenanceKind.Unavailable,
            ["hatches_means"] = "a hatch leaves a zero-volume Solid with no layer; it is reported in " +
                                "harvest_coverage.not_harvested, never counted as buildable geometry.",
            ["cad_dimensions"] = CadProvenanceKind.Unavailable
        };

        private static double Round(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
    }
}
