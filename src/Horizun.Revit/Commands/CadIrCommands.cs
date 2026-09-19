// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// THREE READ-ONLY TOOLS OVER ONE READING.
//
//   horizun_cad_extract        what this reading of the drawing contains, and —
//                              the half that did not exist before — what the
//                              reader could NOT reach, axis by axis, with the
//                              evidence for each verdict.
//
//   horizun_cad_networks       the same drawing read as MEP: straight runs,
//                              the junctions between them, the fittings those
//                              junctions imply, and every crossing and gap that
//                              was deliberately NOT joined.
//
//   horizun_cad_unit_instances repeated layouts: which regions of the drawing
//                              are the same unit under a rigid transform, and
//                              which only look like it.
//
// None of the three writes anything. None opens a transaction. Between them they
// answer the question somebody has to answer before converting a permit set:
// what is in here, what connects to what, and what repeats.
//
// WHY EXTRACT EXISTS AT ALL, given that horizun_query_cad already reports layers
// and geometry. Because "the drawing has no text" and "this reader cannot see
// text" were, until the intermediate representation, the same reply — and they
// lead to opposite decisions. One means the information is not there. The other
// means go and get a reader that can see it. Every count this tool publishes
// travels with the sentence that says which kind of zero it is.
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
    /// <summary>One resolved reading of one CAD instance, or the refusal that replaced it.</summary>
    internal sealed class CadReading
    {
        public Document Document;
        public Element Instance;
        public CadInstanceFacts Facts;
        public CadHarvest Harvest;
        public CadIr Ir;
        public JObject Request;
        public CommandResult Refusal;

        public bool Ok => Refusal == null;
    }

    internal static class CadReadingHelper
    {
        /// <summary>
        /// Resolve the request into one reading, or into the refusal that says
        /// why there is none. Shared by all three tools so that "which drawing"
        /// is answered once and identically.
        /// </summary>
        public static CadReading Resolve(UIApplication app, string paramsJson)
        {
            var r = new CadReading();

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex)
            {
                r.Refusal = CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
                return r;
            }
            r.Request = request;

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) { r.Refusal = CommandResult.Fail("No document is open."); return r; }
            r.Document = doc;

            long instanceId = request.Value<long?>("instance_id") ?? -1;
            if (instanceId < 0)
            {
                r.Refusal = CommandResult.Fail(
                    "instance_id is required. List the candidates with horizun_query_cad mode='instances'; " +
                    "there is no default CAD instance, and picking one is the guess this refuses.");
                return r;
            }
            if (!Rid.CanRepresent(instanceId))
            {
                r.Refusal = CommandResult.Fail(Rid.RangeError(instanceId));
                return r;
            }

            Element element = doc.GetElement(Rid.Make(instanceId));
            if (element == null)
            {
                r.Refusal = CommandResult.Fail("No element with id " + instanceId + " in '" + Title(doc) + "'.");
                return r;
            }
            if (!(element is ImportInstance))
            {
                r.Refusal = CommandResult.Fail(
                    "Element " + instanceId + " is a " + element.GetType().Name + ", not an ImportInstance. " +
                    "These tools read CAD geometry, and reading a Revit element through them would report " +
                    "the wrong thing convincingly.");
                return r;
            }
            r.Instance = element;

            List<JObject> unreadable;
            List<CadInstanceFacts> instances = CadFacts.Collect(doc, out unreadable);
            r.Facts = instances.FirstOrDefault(f => f.ElementId == instanceId);

            double sagitta = request.Value<double?>("arc_sagitta_mm") ?? 5.0;
            if (sagitta <= 0)
            {
                r.Refusal = CommandResult.Fail(
                    "arc_sagitta_mm must be positive; it is how far a chord may depart from its arc.");
                return r;
            }
            int maxPrimitives = Math.Max(1, Math.Min(500000, request.Value<int?>("max_primitives") ?? 200000));

            // A VIEW, WHEN THE CALLER HAS ONE. A CAD placed "current view only"
            // returns no geometry at all to a view-less read, and the resulting
            // empty reading is the commonest false report in this whole area.
            View view = null;
            long viewId = request.Value<long?>("view_id") ?? -1;
            if (viewId >= 0 && Rid.CanRepresent(viewId)) view = doc.GetElement(Rid.Make(viewId)) as View;

            r.Harvest = CadGeometryHarvest.Harvest(doc, element, sagitta, maxPrimitives, view);

            string version = null;
            try { version = app?.Application?.VersionNumber + "." + app?.Application?.VersionBuild; }
            catch { }

            r.Ir = CadIrFromRevit.Adapt(doc, element, r.Harvest,
                                        r.Facts != null ? r.Facts.Name : null,
                                        r.Facts != null ? r.Facts.FileSha256 : null,
                                        version);

            // THE DRAWING'S DECLARED UNIT, when the LINK carries one. It is not
            // the file's own header - Revit keeps what the import was told - but
            // it is the only unit statement available from inside, and saying
            // where it came from is the difference between a fact and a guess.
            if (r.Facts != null && !string.IsNullOrWhiteSpace(r.Facts.DeclaredUnits))
            {
                r.Ir.DeclaredUnits = r.Facts.DeclaredUnits;
                r.Ir.UnitScaleSource =
                    "the CAD LINK declares '" + r.Facts.DeclaredUnits + "'. That is what the import was told " +
                    "the drawing is in, not what the DWG header says - a link created with the wrong unit " +
                    "declares the wrong unit here too, consistently and undetectably from inside Revit.";
            }
            return r;
        }

        public static string Title(Document d) { try { return d.Title; } catch { return null; } }

        /// <summary>The block every one of these replies carries, so a count is never read bare.</summary>
        public static JObject ReadingBlock(CadReading r, double sagittaMm) => new JObject
        {
            ["document"] = Title(r.Document),
            ["instance_id"] = r.Facts != null ? (JToken)r.Facts.ElementId : Rid.Value(r.Instance.Id),
            ["instance_name"] = r.Facts != null ? r.Facts.Name : null,
            ["source_sha256"] = r.Facts != null ? r.Facts.FileSha256 : null,
            ["ir_fingerprint"] = r.Ir.Fingerprint(),
            ["ir_schema_version"] = r.Ir.SchemaVersion,
            ["reader"] = r.Ir.Reader.ToJson(),
            ["counts_mean"] = r.Ir.CountsCaveat(),
            ["harvest_coverage"] = r.Harvest.CoverageJson(sagittaMm),
            ["read_only"] = true
        };

        public static double Sagitta(JObject request) => request.Value<double?>("arc_sagitta_mm") ?? 5.0;

        /// <summary>
        /// The segments of a SECOND CAD instance in the same document, read with
        /// the same reader and the SAME chord tolerance.
        ///
        /// The tolerance matters more than it looks: two drawings chorded
        /// differently produce different line work for the same symbol, and the
        /// cross-reference then matches nothing for a reason that has nothing to do
        /// with either drawing.
        ///
        /// Returns null and sets <paramref name="refusal"/> when the instance is
        /// not one, so the caller refuses rather than silently comparing against
        /// an empty legend - which would report every symbol as undefined.
        /// </summary>
        public static List<CadSegment> SecondInstance(UIApplication app, CadReading r, long instanceId,
                                                      IList<string> layerGlobs, out CommandResult refusal)
        {
            refusal = null;
            if (!Rid.CanRepresent(instanceId)) { refusal = CommandResult.Fail(Rid.RangeError(instanceId)); return null; }

            Element element = r.Document.GetElement(Rid.Make(instanceId));
            if (element == null)
            {
                refusal = CommandResult.Fail("No element with id " + instanceId + " in '" + Title(r.Document) + "'.");
                return null;
            }
            if (!(element is ImportInstance))
            {
                refusal = CommandResult.Fail(
                    "Element " + instanceId + " is a " + element.GetType().Name + ", not an ImportInstance.");
                return null;
            }

            double sagitta = Sagitta(r.Request);
            int maxPrimitives = Math.Max(1, Math.Min(500000, r.Request.Value<int?>("max_primitives") ?? 200000));
            CadHarvest harvest = CadGeometryHarvest.Harvest(r.Document, element, sagitta, maxPrimitives);

            if (harvest.GeometryUnreadable)
            {
                refusal = CommandResult.Fail(
                    "geometry_unreadable: Revit returned no geometry for CAD instance " + instanceId +
                    ". Comparing against a drawing nothing could read would report every symbol as one the " +
                    "legend does not define, which is a false finding rather than an empty one.");
                return null;
            }

            string version = null;
            try { version = app?.Application?.VersionNumber + "." + app?.Application?.VersionBuild; }
            catch { }
            CadIr ir = CadIrFromRevit.Adapt(r.Document, element, harvest, null, null, version);

            List<CadSegment> all = ir.ToSegments();
            if (layerGlobs == null || layerGlobs.Count == 0) return all;
            return all.Where(s => layerGlobs.Any(p => CadGlob.IsMatch(s.Layer ?? "", p, false))).ToList();
        }

        /// <summary>
        /// The arcs of the layers this reading covers, kept AS arcs.
        ///
        /// Filtered by the same layers as the segments: an arc on a layer the
        /// request excluded would otherwise become a run on a layer nobody asked
        /// about.
        /// </summary>
        public static List<CadArcFact> ArcsOn(CadReading r, IList<string> layers)
        {
            List<CadArcFact> all = r.Ir.ToArcs();
            if (layers == null || layers.Count == 0) return all;
            var wanted = new HashSet<string>(layers, StringComparer.OrdinalIgnoreCase);
            return all.Where(a => a != null && wanted.Contains(a.Layer ?? "")).ToList();
        }

        /// <summary>The segments a request asked for: everything, or only the named layers.</summary>
        public static List<CadSegment> Selected(CadReading r, out List<string> layersUsed)
        {
            var patterns = new List<string>();
            var array = r.Request["layers"] as JArray;
            if (array != null)
                foreach (JToken t in array)
                {
                    string s = t?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) patterns.Add(s);
                }
            string single = r.Request.Value<string>("layer");
            if (!string.IsNullOrWhiteSpace(single)) patterns.Add(single);

            List<CadSegment> all = r.Ir.ToSegments();
            if (patterns.Count == 0)
            {
                layersUsed = all.Select(s => s.Layer ?? "")
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                return all;
            }

            var picked = all.Where(s => patterns.Any(p => CadGlob.IsMatch(s.Layer ?? "", p, false))).ToList();
            layersUsed = picked.Select(s => s.Layer ?? "")
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return picked;
        }
    }

    /// <summary>horizun_cad_extract — the reading, and what the reader could not reach.</summary>
    public sealed class CadExtractCommand : ICommand
    {
        public string Name => "horizun_cad_extract";

        public string Description =>
            "Read one linked or imported DWG into a VERSIONED, reader-agnostic intermediate representation and " +
            "return it with an explicit CAPABILITY DECLARATION: for each of twelve axes - geometry, layers, " +
            "entity handles, text, block names, block attributes, external references, units, layouts, " +
            "elevation, appearance and extended data - whether this reader supplied it, whether the drawing " +
            "lacks it, or whether the reader is blind to it, each with the evidence for the verdict. That " +
            "distinction is the point: an empty text result from a reader that cannot see text is not a " +
            "finding about the drawing. The IR carries a canonical fingerprint so two readings of one file are " +
            "comparable, and layers are read TWICE - from the geometry and from the import's own subcategories " +
            "- so a layer whose content this reading lost is named rather than silently absent. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            CadReading r = CadReadingHelper.Resolve(app, paramsJson);
            if (!r.Ok) return r.Refusal;

            bool includeEntities = r.Request.Value<bool?>("include_entities") ?? false;
            int maxEntities = Math.Max(1, Math.Min(20000, r.Request.Value<int?>("max_entities") ?? 2000));
            int offset = Math.Max(0, r.Request.Value<int?>("offset") ?? 0);

            JObject reply = r.Ir.SummaryJson();
            foreach (var p in CadReadingHelper.ReadingBlock(r, CadReadingHelper.Sagitta(r.Request)))
                if (reply[p.Key] == null) reply[p.Key] = p.Value;

            if (includeEntities)
            {
                List<CadIrEntity> page = r.Ir.Entities.Skip(offset).Take(maxEntities).ToList();
                reply["entities"] = new JArray(page.Select(e => (JToken)e.ToJson()));
                reply["entities_page"] = new JObject
                {
                    ["offset"] = offset,
                    ["returned"] = page.Count,
                    ["total"] = r.Ir.Entities.Count,
                    ["complete"] = offset + page.Count >= r.Ir.Entities.Count,
                    ["means"] = offset + page.Count >= r.Ir.Entities.Count
                        ? "every entity this reading produced is in this reply"
                        : "THIS IS A PAGE. Send offset=" +
                          (offset + page.Count).ToString(CultureInfo.InvariantCulture) +
                          " for the next one; the summary above counts the whole reading, not this page."
                };
            }
            else
            {
                reply["entities_omitted"] =
                    "the entities themselves are not in this reply. Send include_entities=true to page " +
                    "through them; the summary and the capability above describe the whole reading either way.";
            }
            return CommandResult.Ok(reply);
        }
    }

    /// <summary>horizun_cad_networks — the drawing read as connected MEP.</summary>
    public sealed class CadNetworksCommand : ICommand
    {
        public string Name => "horizun_cad_networks";

        public string Description =>
            "Read a linked or imported DWG as MEP NETWORKS: straight runs (one per MEP curve Revit would " +
            "create), the junctions between them classified as terminal, elbow, collinear pair, tee, cross, " +
            "irregular, unsupported degree or elevation change, and the connection intents those junctions " +
            "imply. It reports what it deliberately did NOT join: every crossing with no shared endpoint, " +
            "because in a plan that is two services at different heights and joining them routes waste " +
            "through a water main; every gap wider than the connect tolerance, with the distance measured; " +
            "and every junction whose arrangement no fitting covers. Connected components are reported with " +
            "their open ends and any system conflict. Per-layer system, bore and elevation are taken from the " +
            "caller's declarations and NEVER defaulted - a run with no declared elevation is a run whose " +
            "height nobody stated, which is not the same as one at zero. Read-only: nothing is created, " +
            "nothing is connected.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            CadReading r = CadReadingHelper.Resolve(app, paramsJson);
            if (!r.Ok) return r.Refusal;

            var options = new CadNetworkOptions
            {
                ConnectToleranceMm = r.Request.Value<double?>("connect_tolerance_mm") ?? 1.0,
                GapReviewDistanceMm = r.Request.Value<double?>("gap_review_distance_mm") ?? 50.0,
                CollinearToleranceDegrees = r.Request.Value<double?>("collinear_tolerance_degrees") ?? 2.0,
                ThroughToleranceDegrees = r.Request.Value<double?>("through_tolerance_degrees") ?? 15.0,
                CrossingCheckLimit = Math.Max(0, r.Request.Value<int?>("crossing_check_limit") ?? 4000)
            };
            if (options.ConnectToleranceMm <= 0)
                return CommandResult.Fail(
                    "connect_tolerance_mm must be positive: it is the declared meaning of 'these two ends are " +
                    "joined', and a zero tolerance joins nothing in a real drawing.");

            // THE PER-LAYER DECLARATIONS. Nothing is inferred from a layer name
            // here or anywhere else in this bridge: what a layer MEANS is the
            // caller's artefact.
            //
            // AND THERE IS ONLY ONE OF THEM. The requirement set the conversion
            // will use already says the system, the bore and the offset, so when
            // one is given it is the source - and a run cannot be BUILT at one
            // height and CONNECTED at another with both replies looking correct.
            Dictionary<string, CadNetworkRules.CadRunDeclaration> byLayer;
            JObject bad = ReadDeclarations(r.Request, out byLayer);
            if (bad != null) return CommandResult.FailWithDetail(bad.Value<string>("error"), bad);

            CadRequirementSet set = null;
            JObject setJson = r.Request["requirement_set"] as JObject;
            if (setJson != null)
            {
                try { set = CadRequirementSet.Load(setJson); }
                catch (Exception ex)
                {
                    return CommandResult.Fail(
                        "the requirement set was refused WHOLE: " + ex.Message +
                        " A document with a typo in one rule is a document whose author believed something " +
                        "this bridge cannot confirm, and reading the other nine would produce a network " +
                        "nobody asked for.");
                }
            }
            var precedenceTies = new List<JObject>();
            Func<string, CadNetworkRules.CadRunDeclaration> fromSet =
                CadNetworkRules.DeclarationsFrom(set, precedenceTies);

            // THE IDENTITY TOLERANCE FOLLOWS THE SET, because that is the only way
            // a run's name is the same name the conversion will give the element it
            // builds from it. The interpreter merges collinear segments with the
            // set's point and angle tolerances and names the result with the set's
            // point tolerance; reading the network at different numbers produces
            // different runs with different names, and the mapping to the model is
            // gone. A caller may still override each one - and the reply then says
            // the identities no longer match.
            if (set != null)
            {
                if (r.Request["connect_tolerance_mm"] == null)
                    options.ConnectToleranceMm = set.PointToleranceMm > 0
                        ? set.PointToleranceMm : options.ConnectToleranceMm;
                if (r.Request["collinear_tolerance_degrees"] == null)
                    options.CollinearToleranceDegrees = set.AngleToleranceDegrees > 0
                        ? set.AngleToleranceDegrees : options.CollinearToleranceDegrees;
                if (r.Request["identity_tolerance_mm"] == null)
                    options.IdentityToleranceMm = set.PointToleranceMm;
            }
            if (r.Request["identity_tolerance_mm"] != null)
                options.IdentityToleranceMm = r.Request.Value<double?>("identity_tolerance_mm") ?? 0;

            List<string> layersUsed;
            List<CadSegment> segments = CadReadingHelper.Selected(r, out layersUsed);

            // WHERE THE TWO DISAGREE, NOTHING IS READ. Choosing one of them would
            // make the reading depend on which source this file happens to prefer,
            // which is not a decision about the building.
            var disagreements = new JArray();
            if (set != null && byLayer.Count > 0)
                foreach (string layer in layersUsed)
                {
                    CadNetworkRules.CadRunDeclaration mine = Explicit(byLayer, layer);
                    CadNetworkRules.CadRunDeclaration theirs = fromSet(layer);
                    if (mine == null || theirs == null) continue;
                    if (mine.SameAs(theirs)) continue;
                    disagreements.Add(new JObject
                    {
                        ["layer"] = layer,
                        ["layer_declarations_say"] = mine.ToJson(),
                        ["requirement_set_says"] = theirs.ToJson()
                    });
                }

            if (disagreements.Count > 0)
                return CommandResult.FailWithDetail(
                    "two_sources_of_truth: the requirement set and layer_declarations disagree about " +
                    disagreements.Count + " layer(s).",
                    new JObject
                    {
                        ["refused"] = "two_sources_of_truth",
                        ["layers"] = disagreements,
                        ["means"] = "the requirement set is what the conversion will BUILD from and " +
                                    "layer_declarations is what this reading would CONNECT from. Where they " +
                                    "differ, a run is built at one height and joined at another, and both " +
                                    "replies look correct because each agrees with its own input. Nothing " +
                                    "was read. Send one of them, or make them agree."
                    });

            // THE SAME PIECES THE CONVERSION BUILDS. A rule that cuts a run where its section changes
            // makes pieces the drawing does not contain as entities; read here from the raw segments,
            // the network would name the whole run and connect would find no element built from it.
            // So the conversion's own reading - same hook, same labels - replaces those segments.
            JObject piecesReport = null;
            var drawnTransitions = new JArray();
            if (set != null && set.Rules.Any(x => x.Section != null && x.Section.SplitAtSectionChanges))
            {
                piecesReport = new JObject();
                string sourceHash = r.Facts?.FileSha256 ?? CadFacts.SourceFingerprint(r.Facts) ?? "(no-source-identity)";
                CadInterpretation reading = CadInterpretationRules.Interpret(r.Harvest.Segments, set, sourceHash, r.Harvest.Arcs);
                var wholeLines = reading.Candidates.ToDictionary(c => c.SemanticId, c => c, StringComparer.Ordinal);
                string hookFailure;
                JObject sectionsRead = CadSectionsHook.Apply(r.Instance, r.Facts, set, r.Harvest, r.Request, reading,
                                                             new JArray(), true, out hookFailure);
                if (hookFailure != null) return CommandResult.Fail(hookFailure);
                double tol = Math.Max(set.PointToleranceMm, 1.0);
                int replaced = 0, added = 0;
                var notCoordinated = new JArray();
                foreach (var group in reading.Candidates.Where(c => c.PieceOf != null).GroupBy(c => c.PieceOf))
                {
                    CadCandidate parent;
                    if (!wholeLines.TryGetValue(group.Key, out parent)) continue;
                    CadRule rule = set.Rules.FirstOrDefault(x => x.Id == parent.RuleId);
                    if (rule?.Geometry == null || rule.Geometry.MergeCollinear)
                    {
                        notCoordinated.Add(new JObject { ["run"] = parent.SemanticId, ["why"] =
                            "the rule merges collinear segments, so the network would merge the pieces back; declare " +
                            "geometry.merge_collinear false for a rule that cuts runs" });
                        continue;
                    }
                    CadPoint a0 = parent.Geometry[0], a1 = parent.Geometry[parent.Geometry.Count - 1];
                    int before = segments.Count;
                    segments.RemoveAll(sg => string.Equals(sg.Layer, parent.Layer, StringComparison.Ordinal) &&
                        ((sg.A.PlanDistanceTo(a0) <= tol && sg.B.PlanDistanceTo(a1) <= tol) ||
                         (sg.A.PlanDistanceTo(a1) <= tol && sg.B.PlanDistanceTo(a0) <= tol)));
                    replaced += before - segments.Count;
                    foreach (CadCandidate piece in group)
                    {
                        segments.Add(new CadSegment(piece.Geometry[0], piece.Geometry[piece.Geometry.Count - 1], piece.Layer, piece.Kind));
                        added++;
                    }
                }
                // A DRAWN TRANSITION WITH A SIZE AT BOTH ENDS is not a duct: it is the fitting between
                // its two neighbours. Its segment leaves the network and the connection is proposed
                // instead, carrying where the drawing put it so connect can check the fit.
                var bySemantic = reading.Candidates.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().SemanticId, StringComparer.Ordinal);
                foreach (JProperty ruleRows in (sectionsRead?["by_rule"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                    foreach (JObject row in (ruleRows.Value["rows"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var ends = row["transition_ends"] as JObject;
                        if (row.Value<string>("state") != "transition" || ends == null) continue;
                        string ra = ends["a"]?.Value<string>("run"), rb = ends["b"]?.Value<string>("run");
                        string sa, sb;
                        if (ra == null || rb == null || !bySemantic.TryGetValue(ra, out sa) || !bySemantic.TryGetValue(rb, out sb)) continue;
                        var f = row["from_mm"] as JArray; var t = row["to_mm"] as JArray;
                        var pf = new CadPoint(f[0].Value<double>(), f[1].Value<double>());
                        var pt = new CadPoint(t[0].Value<double>(), t[1].Value<double>());
                        int n0 = segments.Count;
                        segments.RemoveAll(sg => (sg.A.PlanDistanceTo(pf) <= tol && sg.B.PlanDistanceTo(pt) <= tol) ||
                                                 (sg.A.PlanDistanceTo(pt) <= tol && sg.B.PlanDistanceTo(pf) <= tol));
                        if (segments.Count == n0) continue;
                        double z = 0;
                        drawnTransitions.Add(new JObject
                        {
                            ["id"] = "drawn-transition:" + row.Value<string>("semantic_id"),
                            ["junction_node"] = "drawn-transition:" + row.Value<string>("semantic_id"),
                            ["fitting"] = "transition",
                            ["automatic"] = true,
                            ["says"] = "a transition the drawing draws between " + ends["a"].Value<double>("width_mm") + "x" +
                                       ends["a"].Value<double>("height_mm") + " and " + ends["b"].Value<double>("width_mm") + "x" +
                                       ends["b"].Value<double>("height_mm") + " mm",
                            ["at"] = new JArray(Math.Round((pf.X + pt.X) / 2, 4), Math.Round((pf.Y + pt.Y) / 2, 4), z),
                            ["elements"] = new JArray(new JObject { ["semantic_id"] = sa }, new JObject { ["semantic_id"] = sb }),
                            ["drawn"] = new JObject
                            {
                                ["from_mm"] = new JArray(pf.X, pf.Y), ["to_mm"] = new JArray(pt.X, pt.Y),
                                ["length_mm"] = row["transition_ends"]["drawn_length_mm"],
                                ["piece_semantic_id"] = row["semantic_id"],
                                ["provenance"] = "drawn: a short piece between two sized runs"
                            },
                            ["sendable"] = true
                        });
                    }
                piecesReport["drawn_runs_replaced_by_pieces"] = replaced;
                piecesReport["pieces"] = added;
                piecesReport["not_coordinated"] = notCoordinated;
                piecesReport["means"] = "runs the conversion cuts where their section changes are read here as the same " +
                                        "pieces, so every network run names an element the conversion builds (or a piece " +
                                        "it keeps pending).";
                if (sectionsRead?["by_rule"] != null) piecesReport["sections"] = sectionsRead["by_rule"];
            }

            // THE ARCS THE READER KEPT AS ARCS. Without them a curve chorded to the
            // declared sagitta comes back as a chain of straight runs with an elbow
            // at every chord - within tolerance geometrically, and a piece of
            // pipework nobody would fabricate.
            CadNetwork network = CadNetworkRules.Build(segments, options, layer =>
            {
                if (layer == null) return null;
                CadNetworkRules.CadRunDeclaration mine = Explicit(byLayer, layer);
                if (mine != null) return mine;
                return fromSet == null ? null : fromSet(layer);
            }, CadReadingHelper.ArcsOn(r, layersUsed));

            JObject reply = network.ToJson();
            if (piecesReport != null) reply["section_pieces"] = piecesReport;
            if (drawnTransitions.Count > 0)
            {
                var conns = reply["connections"] as JArray;
                if (conns != null) foreach (JToken dt in drawnTransitions) conns.Add(dt);
                reply["drawn_transitions"] = drawnTransitions.Count;
                reply["drawn_transitions_mean"] = "each drawn transition piece with a settled size at both ends left the " +
                    "network as a run and was added to connections as a 'transition' between its two neighbours; the " +
                    "components above therefore count those neighbours apart until connect places the fitting.";
            }
            foreach (var p in CadReadingHelper.ReadingBlock(r, CadReadingHelper.Sagitta(r.Request)))
                reply[p.Key] = p.Value;

            reply["layers_read"] = new JArray(layersUsed.Select(s => (JToken)s));
            reply["segments_considered"] = segments.Count;
            reply["declarations_given"] = byLayer.Count;

            bool identityMatchesConversion = set != null
                && Math.Abs(options.IdentityTolerance - set.PointToleranceMm) <= 1e-9
                && Math.Abs(options.ConnectToleranceMm - set.PointToleranceMm) <= 1e-9
                && Math.Abs(options.CollinearToleranceDegrees - set.AngleToleranceDegrees) <= 1e-9;

            reply["run_identity"] = new JObject
            {
                ["matches_the_conversion"] = identityMatchesConversion,
                ["identity_tolerance_mm"] = options.IdentityTolerance,
                ["set_point_tolerance_mm"] = set == null
                    ? (JToken)JValue.CreateNull() : set.PointToleranceMm,
                ["set_angle_tolerance_degrees"] = set == null
                    ? (JToken)JValue.CreateNull() : set.AngleToleranceDegrees,
                ["means"] = identityMatchesConversion
                    ? "every run's semantic_id is the SAME id the conversion stamps on the element it builds " +
                      "from that run, so a junction can name its members by semantic_id and " +
                      "horizun_cad_connect resolves them against the model's own provenance. No manual " +
                      "mapping from runs to element ids is needed."
                    : set == null
                        ? "no requirement set was given, so these ids were computed at this reading's own " +
                          "tolerance. They are stable and comparable between two readings of this drawing, " +
                          "and they are NOT guaranteed to equal the ids the conversion stamps - send the " +
                          "requirement set to make them agree."
                        : "the tolerances used here differ from the requirement set's, so a run's " +
                          "semantic_id is NOT the id the conversion stamps. Matching runs to elements by " +
                          "id would silently match nothing, or worse, match the wrong ones. Drop the " +
                          "tolerance overrides to make them agree."
            };
            reply["declaration_source"] = set != null && byLayer.Count > 0
                ? "layer_declarations, falling back to the requirement set for layers it does not name - and " +
                  "they were checked against each other for every layer read"
                : set != null
                    ? "the requirement set's own MEP rules, by its own precedence, so this reading and the " +
                      "conversion cannot disagree about a layer"
                    : byLayer.Count > 0
                        ? "layer_declarations only. No requirement set was given, so nothing here is checked " +
                          "against what the conversion will actually build."
                        : "nothing was declared";
            reply["undeclared_layers"] = new JArray(layersUsed
                .Where(l => Explicit(byLayer, l) == null && (fromSet == null || fromSet(l) == null))
                .Select(s => (JToken)s));
            if (precedenceTies.Count > 0)
            {
                reply["rule_precedence_ties"] = new JArray(precedenceTies);
                reply["rule_precedence_ties_mean"] =
                    "on these layers two of the requirement set's MEP rules claim the layer at the same " +
                    "precedence. They were left UNDECLARED rather than resolved by sort order. The same tie " +
                    "will reach the conversion, so it is worth settling in the set rather than here.";
            }
            reply["undeclared_means"] =
                "runs on these layers have no system, no bore and no elevation. They are still read as " +
                "geometry and still form junctions, and NOTHING can be built from them: Revit will not create " +
                "a pipe without a system type, and a drawn line carries no width.";

            // The text axis governs whether sizes and levels could ever have come
            // from the drawing itself, so the answer travels with the reply
            // rather than being something a caller has to think to ask.
            // THE FALL, when the caller names an outfall.
            //
            // A drain's slope is declared per layer; its DIRECTION is not declarable
            // per layer, because it is a fact about the network. Water runs downhill
            // to one place, the caller names that place, and every invert follows.
            var outfall = r.Request["outfall"] as JArray;
            if (outfall != null)
            {
                if (outfall.Count < 2)
                    return CommandResult.Fail(
                        "outfall must be [x, y] in millimetres, in the drawing's own coordinates - the point " +
                        "the network drains to.");

                double outfallTolerance = r.Request.Value<double?>("outfall_tolerance_mm") ?? 50.0;
                var at = new CadPoint(outfall[0].Value<double>(), outfall[1].Value<double>(),
                                      outfall.Count > 2 ? outfall[2].Value<double>() : 0);
                string node = CadFallRules.NodeNear(network, at, outfallTolerance);

                if (node == null)
                    return CommandResult.FailWithDetail(
                        "outfall_matches_no_node: no junction of this network is within " +
                        outfallTolerance.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                        " mm of that point.",
                        new JObject
                        {
                            ["refused"] = "outfall_matches_no_node",
                            ["outfall"] = outfall,
                            ["tolerance_mm"] = outfallTolerance,
                            ["nodes"] = network.Junctions.Count,
                            ["means"] = "an outfall placed on the wrong node inverts an entire drainage " +
                                        "layout while looking plausible, so the nearest node is NOT taken " +
                                        "when it is out of tolerance. Nothing was computed."
                        });

                double invert = r.Request.Value<double?>("outfall_invert_mm") ?? 0.0;
                CadFall computed = CadFallRules.Compute(network, node, invert, run => run.SlopePercent);
                reply["fall"] = computed.ToJson();
                reply["fall_means"] =
                    "the invert of every node, measured along the network from the outfall. The upstream " +
                    "end of each run is decided by the NETWORK and not by which point the drawing listed " +
                    "first - a drawn line has a first point and a second point, and that is drawing order, " +
                    "not hydraulics. THIS IS A READING AND BUILDS NOTHING: to convert to these heights, " +
                    "send the same outfall to horizun_plan_from_cad, which walks the fall again from its " +
                    "own reading and puts the heights on the runs it plans. It computes its own rather " +
                    "than taking these, so that the heights built are the ones derived from the geometry " +
                    "the plan is actually built from - and the two agree exactly when the tolerances do, " +
                    "which is what run_identity above reports.";
            }
            else
            {
                reply["fall"] = JValue.CreateNull();
                reply["fall_means"] =
                    "no outfall was named, so no fall was computed. A slope declared per layer says HOW " +
                    "STEEP and not WHICH WAY; the direction is a fact about the network, and naming the " +
                    "point it drains to is the only thing that settles it.";
            }

            JObject textRefusal = r.Ir.RefuseIfBlind(CadAxes.Text, "reading pipe sizes and elevations off the drawing");
            if (textRefusal != null) reply["why_sizes_must_be_declared"] = textRefusal;

            // THE LISTING IS PAGED; THE ANALYSIS IS NOT. See Core/CadNetworkPaging.cs.
            var only = (r.Request["lists"] as JArray)?.Select(t => (string)t).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            int pageOffset = Math.Max(0, r.Request.Value<int?>("page_offset") ?? 0);
            int pageLimit = r.Request.Value<int?>("page_limit") ?? CadNetworkPaging.DefaultLimit;
            CadNetworkPaging.Page(reply, only, pageOffset, pageLimit);
            string expected = r.Request.Value<string>("expect_analysis_fingerprint");
            if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(expected, reply.Value<string>("analysis_fingerprint"), StringComparison.Ordinal))
                return CommandResult.FailWithDetail(
                    "source_changed_between_pages: this page was read from an analysis whose fingerprint is " +
                    reply.Value<string>("analysis_fingerprint") + ", not " + expected + ". The pages already read belong to " +
                    "another reading; start again from offset 0.",
                    new JObject { ["refused"] = "source_changed_between_pages", ["expected"] = expected,
                                  ["now"] = reply["analysis_fingerprint"] });
            if (r.Request["layers"] == null && r.Request["layer"] == null && set == null)
                reply["scope_proposal"] = CadNetworkPaging.ScopeProposal(segments);

            return CommandResult.Ok(reply);
        }

        /// <summary>
        /// The explicit declaration for a layer: an exact name first, then a glob.
        ///
        /// Exact before glob, because a caller who names one layer specifically has
        /// said something more definite than a pattern that also covers it.
        /// </summary>
        private static CadNetworkRules.CadRunDeclaration Explicit(
            Dictionary<string, CadNetworkRules.CadRunDeclaration> map, string layer)
        {
            if (map == null || layer == null) return null;
            CadNetworkRules.CadRunDeclaration exact;
            if (map.TryGetValue(layer, out exact)) return exact;
            foreach (var kv in map)
                if (CadGlob.IsMatch(layer, kv.Key, false)) return kv.Value;
            return null;
        }

        private static JObject ReadDeclarations(JObject request,
            out Dictionary<string, CadNetworkRules.CadRunDeclaration> map)
        {
            map = new Dictionary<string, CadNetworkRules.CadRunDeclaration>(StringComparer.OrdinalIgnoreCase);
            var declarations = request["layer_declarations"] as JArray;
            if (declarations == null) return null;

            for (int i = 0; i < declarations.Count; i++)
            {
                var row = declarations[i] as JObject;
                if (row == null)
                    return new JObject
                    {
                        ["error"] = "layer_declarations[" + i + "] is not an object.",
                        ["index"] = i
                    };
                string layer = row.Value<string>("layer");
                if (string.IsNullOrWhiteSpace(layer))
                    return new JObject
                    {
                        ["error"] = "layer_declarations[" + i + "] has no 'layer'.",
                        ["index"] = i,
                        ["means"] = "a declaration that does not say which layer it is about would be applied " +
                                    "to whichever layer happened to be read first"
                    };
                double? diameter = row.Value<double?>("diameter_mm");
                if (diameter.HasValue && diameter.Value <= 0)
                    return new JObject
                    {
                        ["error"] = "layer_declarations[" + i + "] declares diameter_mm " + diameter.Value +
                                    ", which is not a bore.",
                        ["index"] = i
                    };
                // THE SLOPE, WHICH SameAs COMPARES.
                //
                // Adding the slope to the declaration's equality without adding it
                // to the thing a caller can declare made every layer whose RULE
                // declares a fall disagree with an explicit declaration that could
                // not mention one - and a disagreement refuses the whole call. A
                // caller working without a requirement set needs it anyway, or the
                // fall cannot be computed from declarations at all.
                map[layer] = new CadNetworkRules.CadRunDeclaration
                {
                    SystemType = row.Value<string>("system_type"),
                    DiameterMm = diameter,
                    ElevationMm = row.Value<double?>("elevation_mm"),
                    SlopePercent = row.Value<double?>("slope_percent"),
                    Source = "layer_declarations"
                };
            }
            return null;
        }
    }

    /// <summary>horizun_cad_symbols — which marks on the drawing are the same symbol.</summary>
    public sealed class CadSymbolsCommand : ICommand
    {
        public string Name => "horizun_cad_symbols";

        public string Description =>
            "Group the marks on a drawing into SYMBOL TYPES. An electrical unit plan is mostly symbols - " +
            "outlets, switches, fixtures, data points - each a handful of arcs and lines, and the existing " +
            "point-cluster reading places something in the right spot while saying nothing about what it is, " +
            "so a layer holding switches and receptacles converts to a wall of receptacles. This groups line " +
            "work by CONNECTIVITY (what a draughtsman drew together, not what happens to be near) bounded by " +
            "a declared footprint, then decides which groups are the same marks under a rigid transform " +
            "using the same two-stage matching that decides whether two apartments are the same layout - " +
            "nothing in that reasoning depends on scale. Types are NOT named: what a symbol means lives in " +
            "the drawing's legend, which is text. Naming a type is one sentence from a person and then " +
            "applies to every occurrence of it, which is the point of grouping them. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            CadReading r = CadReadingHelper.Resolve(app, paramsJson);
            if (!r.Ok) return r.Refusal;

            double footprint = r.Request.Value<double?>("max_footprint_mm") ?? 0;
            if (footprint <= 0)
                return CommandResult.Fail(
                    "max_footprint_mm is required and must be positive: it is how large a piece of connected " +
                    "line work may be and still be a symbol. There is no default, because a receptacle is " +
                    "forty millimetres across on one drawing and four hundred on another - and without a " +
                    "bound the first connected component is the whole circuit, symbols and home runs together.");

            double snap = r.Request.Value<double?>("snap_tolerance_mm") ?? 0.5;
            if (snap <= 0)
                return CommandResult.Fail(
                    "snap_tolerance_mm must be positive: it is how close two ends must be to count as " +
                    "touching, and at zero nothing a real drawing contains touches anything.");

            double match = r.Request.Value<double?>("match_tolerance_mm") ?? 0.5;
            if (match <= 0)
                return CommandResult.Fail("match_tolerance_mm must be positive.");

            int minSegments = Math.Max(1, r.Request.Value<int?>("min_segments") ?? 2);

            List<string> layersUsed;
            List<CadSegment> segments = CadReadingHelper.Selected(r, out layersUsed);

            // THE LEGEND, when there is one. A symbol's NAME lives on the legend
            // sheet, and reading both drawings together is what turns "name four
            // hundred marks" into "read six legend entries".
            List<CadSegment> legend = null;
            long legendId = r.Request.Value<long?>("legend_instance_id") ?? -1;
            if (legendId >= 0)
            {
                var legendGlobs = new List<string>();
                var declared = r.Request["legend_layers"] as JArray;
                if (declared != null)
                    foreach (JToken t in declared)
                    {
                        string g = t?.ToString();
                        if (!string.IsNullOrWhiteSpace(g)) legendGlobs.Add(g);
                    }

                CommandResult refusal;
                legend = CadReadingHelper.SecondInstance(app, r, legendId, legendGlobs, out refusal);
                if (refusal != null) return refusal;
            }

            CadSymbolReading reading = legend == null
                ? CadSymbolRules.Read(segments, snap, footprint, match, minSegments)
                : CadSymbolRules.ReadAgainstLegend(segments, legend, snap, footprint, match, minSegments);
            JObject reply = reading.ToJson();
            foreach (var p in CadReadingHelper.ReadingBlock(r, CadReadingHelper.Sagitta(r.Request)))
                reply[p.Key] = p.Value;

            reply["layers_read"] = new JArray(layersUsed.Select(x => (JToken)x));
            reply["segments_considered"] = segments.Count;
            reply["bounds"] = new JObject
            {
                ["snap_tolerance_mm"] = snap,
                ["max_footprint_mm"] = footprint,
                ["match_tolerance_mm"] = match,
                ["min_segments"] = minSegments
            };
            if (legend != null)
            {
                reply["legend_instance_id"] = legendId;
                reply["legend_segments_considered"] = legend.Count;
            }

            JObject textRefusal = r.Ir.RefuseIfBlind(CadAxes.Text, "reading the legend that names these symbols");
            if (textRefusal != null) reply["why_types_are_unnamed"] = textRefusal;

            JObject blockRefusal = r.Ir.RefuseIfBlind(CadAxes.BlockNames,
                                                      "recognising a symbol by its block definition name");
            if (blockRefusal != null) reply["why_grouping_is_by_geometry"] = blockRefusal;

            return CommandResult.Ok(reply);
        }
    }

    /// <summary>horizun_cad_unit_instances — which regions of the drawing are the same layout.</summary>
    public sealed class CadUnitInstancesCommand : ICommand
    {
        public string Name => "horizun_cad_unit_instances";

        public string Description =>
            "Find REPEATED LAYOUTS in a linked or imported DWG. Given regions - closed boundaries the caller " +
            "supplies - it groups them into unit TYPES and OCCURRENCES, each occurrence carrying the exact " +
            "rigid transform (quarter turns, mirror, offset) that maps the type onto it. Matching is two " +
            "stage and neither stage is a similarity score: a rotation-invariant signature decides who is " +
            "compared, and then every one of the eight rigid maps between the bounding boxes is applied and " +
            "a match declared only when every segment of the type lands on a segment of the region, one for " +
            "one, within tolerance. Regions that share a signature and fit no transform are reported as NEAR " +
            "MISSES rather than as weak matches, because two apartments that differ by a wall are two " +
            "apartments. Types are NOT named: a unit's name lives in the drawing's text, and whether that is " +
            "readable is a property of the reader - see horizun_cad_extract. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            CadReading r = CadReadingHelper.Resolve(app, paramsJson);
            if (!r.Ok) return r.Refusal;

            double tolerance = r.Request.Value<double?>("match_tolerance_mm") ?? 2.0;
            if (tolerance <= 0)
                return CommandResult.Fail(
                    "match_tolerance_mm must be positive: it is how far two drawn segments may differ and " +
                    "still be the same segment, and at zero no real drawing matches itself.");
            bool singletons = r.Request.Value<bool?>("singletons_become_types") ?? false;

            var regionsJson = r.Request["regions"] as JArray;
            if (regionsJson == null || regionsJson.Count == 0)
                return CommandResult.Fail(
                    "regions is required and must hold at least one boundary. This tool does not invent the " +
                    "outline of an apartment: deciding where one unit stops and the corridor begins is a " +
                    "reading of the building, and a rectangle guessed here would put the party wall in " +
                    "whichever unit was processed first.");

            List<string> layersUsed;
            List<CadSegment> segments = CadReadingHelper.Selected(r, out layersUsed);

            var regions = new List<CadUnitRegion>();
            for (int i = 0; i < regionsJson.Count; i++)
            {
                var row = regionsJson[i] as JObject;
                if (row == null) return CommandResult.Fail("regions[" + i + "] is not an object.");
                string id = row.Value<string>("id");
                if (string.IsNullOrWhiteSpace(id))
                    return CommandResult.Fail(
                        "regions[" + i + "] has no 'id'. Every region needs a name the reply can refer to, " +
                        "and numbering them here would make two runs of this tool disagree about which is which.");

                var boundary = row["boundary"] as JArray;
                if (boundary == null || boundary.Count < 3)
                    return CommandResult.Fail(
                        "regions[" + i + "] ('" + id + "') needs a boundary of at least three points.");

                var ring = new List<CadPoint>();
                foreach (JToken t in boundary)
                {
                    var pt = t as JArray;
                    if (pt == null || pt.Count < 2)
                        return CommandResult.Fail(
                            "regions[" + i + "] ('" + id + "') has a boundary point that is not [x, y].");
                    ring.Add(new CadPoint(pt[0].Value<double>(), pt[1].Value<double>(),
                                          pt.Count > 2 ? pt[2].Value<double>() : 0));
                }

                regions.Add(new CadUnitRegion
                {
                    Id = id,
                    Boundary = ring,
                    Level = row.Value<string>("level"),
                    Contents = CadUnitRules.Inside(ring, segments)
                });
            }

            // AN EMPTY REGION MUST NOT BE A TYPE.
            //
            // Two boundaries that enclose nothing have identical signatures - zero
            // segments, zero length, a zero-by-zero box - so they match each other
            // EXACTLY and the reading reports a unit type with two occurrences and
            // no geometry. It is the most confident wrong answer this file can
            // produce, and it comes from boundaries in the wrong place or a layer
            // filter that excluded everything inside them.
            var empty = regions.Where(x => x.Contents.Count == 0).Select(x => x.Id).ToList();
            List<CadUnitRegion> populated = regions.Where(x => x.Contents.Count > 0).ToList();

            if (populated.Count == 0)
                return CommandResult.Fail(
                    "every region given encloses nothing this reading produced, so there is nothing to " +
                    "compare. Either the boundaries are in the wrong place, or the layer filter excluded " +
                    "what is inside them, or their content is made of things this reader drops - text, " +
                    "hatches, dimensions. Comparing them would report one unit type with " +
                    regions.Count.ToString(CultureInfo.InvariantCulture) + " identical occurrences and no " +
                    "geometry, which is the most confident wrong answer available here.");

            CadUnitReading reading = CadUnitRules.Recognise(populated, tolerance, singletons);

            JObject reply = reading.ToJson();
            foreach (var p in CadReadingHelper.ReadingBlock(r, CadReadingHelper.Sagitta(r.Request)))
                reply[p.Key] = p.Value;

            reply["layers_read"] = new JArray(layersUsed.Select(s => (JToken)s));
            reply["segments_considered"] = segments.Count;
            reply["regions_given"] = regions.Count;
            reply["regions_compared"] = populated.Count;
            reply["regions_with_no_content"] = new JArray(empty.Select(s => (JToken)s));
            if (empty.Count > 0)
                reply["regions_with_no_content_means"] =
                    "these boundaries enclose nothing this reading produced and were EXCLUDED from the " +
                    "comparison. Either they are in the wrong place, or their content is on a layer the " +
                    "request filtered out, or it is made of things this reader drops - text, hatches, " +
                    "dimensions. They are excluded because two empty regions have identical signatures and " +
                    "would match each other exactly, producing a unit type with no geometry.";

            reply["stacking"] =
                "an occurrence marked exact can be repeated on other storeys with its PLAN transform " +
                "unchanged and its level and HEIGHT replaced. WHICH storeys repeat is not decided here: a " +
                "podium level with a different core is not a typical floor, and only somebody who knows the " +
                "building can say where the typical range starts. Supply each level's elevation with the " +
                "levels: a level nobody gives a height to stacks at ZERO, and a stack of storeys at zero " +
                "occupies one storey's worth of space while reading, in plan, exactly like a correct stack. " +
                "Each stacked occurrence reports whether its height was declared or defaulted - a declared " +
                "zero is a ground floor and an undeclared one is nobody having said.";

            JObject textRefusal = r.Ir.RefuseIfBlind(CadAxes.Text, "reading the unit type names off the drawing");
            if (textRefusal != null) reply["why_types_are_unnamed"] = textRefusal;

            return CommandResult.Ok(reply);
        }
    }
}
