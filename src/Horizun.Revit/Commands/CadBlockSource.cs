// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// ONE READER, TWO COMMANDS.
//
// The plan reads a drawing's named symbols through the file itself, because
// Revit's import cannot see a block name. The AUDIT has to read it exactly the
// same way or it reports a model that disagrees with a drawing it agrees with -
// MEASURED: eight devices built from this route, eight findings of
// "built_not_in_drawing", because the audit re-read the drawing with the
// geometry reader alone and found no symbols at all.
//
// So the reading lives here, once, and both commands call it. A second copy of
// this logic would drift within a week and the drift would look like a change in
// the building.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>The drawing's named symbols, read from the file, for any command that needs them.</summary>
    public static class CadBlockSource
    {
        /// <summary>How many unclaimed block names the report lists; the totals count all of them.</summary>
        private const int UnclaimedListed = 50;

        /// <summary>
        /// Read the DRAWING for its named symbols and add them to this reading.
        ///
        /// Everything here is about the two coordinate frames. The file reader
        /// speaks the drawing's coordinates in millimetres; Revit's harvest
        /// speaks the model's, already transformed. The import instance's own
        /// transform is what relates them - and rather than trusting it, this
        /// applies it and then CHECKS, by comparing the drawing's extents with
        /// the geometry Revit actually handed over. A disagreement stops the
        /// symbols: a receptacle forty metres from where it was drawn passes
        /// every count and is wrong in the only way that matters.
        /// </summary>
        public static JObject Read(Element element, CadInstanceFacts facts, CadRequirementSet set,
                                          CadHarvest harvest, string sourceHash,
                                          CadInterpretation interpretation, string callerPath,
                                          int readTimeoutSeconds, List<CadInventoryRow> inventory = null)
        {
            var report = new JObject
            {
                ["why"] = "this requirement set has at least one rule with geometry.from = blocks, and a " +
                          "block NAME is not reachable through Revit's import - that axis is declared " +
                          "Unavailable by the import reader, with its reason. So the DWG itself is read."
            };

            CadDwgReading reading = ReadDrawing(facts, set, callerPath, readTimeoutSeconds, report);
            if (reading == null) return report;

            CadPlacementReading placement = CadBlockPlacement.Place(reading.Entities);
            report["placement"] = placement.SummaryJson();

            // MODEL SPACE ONLY. A sheet carries the legend, the title block and the
            // detail bubbles, drawn as the same blocks on the same layers - and
            // Revit's import shows model space, so a paper-space symbol has no
            // counterpart in the model to check it against and no business being
            // built. MEASURED on this drawing: the two spaces together covered
            // 168 x 117 m against the 64 x 72 m the drawing actually occupies.
            var inModel = new List<CadPlacedBlock>();
            int onSheets = 0, spaceUnknown = 0;
            var rowOf = new Dictionary<CadPlacedBlock, CadInventoryRow>();
            foreach (CadPlacedBlock p in placement.Placed)
            {
                if (p.Space == "model") inModel.Add(p);
                else if (p.Space == "paper") onSheets++;
                else spaceUnknown++;
                if (inventory != null)
                {
                    var row = new CadInventoryRow
                    {
                        Key = p.Key,
                        BlockName = p.BlockName,
                        EffectiveName = p.Source == null ? null : p.Source.EffectiveName,
                        EffectiveNameSource = p.Source == null ? null : p.Source.EffectiveNameSource,
                        DynamicProperties = p.Source == null ? null : p.Source.DynamicProperties,
                        DefinitionSignature = p.Source == null ? null : p.Source.DefinitionSignature,
                        Layer = p.Layer,
                        Space = p.Space ?? "unknown",
                        Path = new List<string>(p.Path),
                        HandleChain = new List<string>(p.HandleChain),
                        DrawingAt = p.At,
                        RotationRadians = p.RotationRadians,
                        Mirrored = p.Mirrored,
                        Attributes = p.Source == null ? null : p.Source.Attributes,
                        Outcome = p.Space == "model" ? CadInventoryOutcome.NotClassified
                                : p.Space == "paper" ? CadInventoryOutcome.PaperSpace
                                : CadInventoryOutcome.SpaceUnknown
                    };
                    rowOf[p] = row;
                    inventory.Add(row);
                }
            }
            report["on_sheets_not_converted"] = onSheets;
            report["space_unknown"] = spaceUnknown;
            report["space_means"] =
                "a symbol drawn in PAPER space is part of the drawing, not of the building - a legend, a " +
                "title block, a detail. It is read, counted and not converted. space_unknown is a placement " +
                "whose root space this reading could not name; it is not converted either.";

            List<JObject> duplicates;
            var droppedFor = new Dictionary<CadPlacedBlock, CadPlacedBlock>();
            List<CadPlacedBlock> placed = CadBlockPlacement.Distinct(inModel,
                                                                    set.PointToleranceMm, out duplicates, droppedFor);
            report["coincident_collapsed"] = duplicates.Count;

            // ---- the two frames, related and then checked ---------------------
            Transform t = Transform.Identity;
            var import = element as ImportInstance;
            if (import != null) { try { t = import.GetTransform(); } catch { t = Transform.Identity; } }

            var entities = new List<CadIrEntity>(placed.Count);
            var rowOfEntity = new Dictionary<CadIrEntity, CadInventoryRow>();
            Func<CadPoint, CadPoint> toModel = at =>
            {
                XYZ ft = t.OfPoint(new XYZ(at.X / 304.8, at.Y / 304.8, at.Z / 304.8));
                return new CadPoint(ft.X * 304.8, ft.Y * 304.8, ft.Z * 304.8);
            };
            foreach (CadPlacedBlock p in placed)
            {
                CadIrEntity e = p.AsEntity();
                e.Points[0] = toModel(e.Points[0]);
                if (e.RotationRadians.HasValue)
                    e.RotationRadians = e.RotationRadians.Value + Math.Atan2(t.BasisX.Y, t.BasisX.X);
                entities.Add(e);
                CadInventoryRow row;
                if (inventory != null && rowOf.TryGetValue(p, out row))
                {
                    row.ModelAt = e.Points[0];
                    rowOfEntity[e] = row;
                }
            }
            if (inventory != null)
                foreach (var kv in droppedFor)
                {
                    CadInventoryRow row, keptRow;
                    if (!rowOf.TryGetValue(kv.Key, out row)) continue;
                    row.ModelAt = toModel(kv.Key.At);
                    row.Outcome = CadInventoryOutcome.Duplicate;
                    if (rowOf.TryGetValue(kv.Value, out keptRow)) row.DuplicateOf = keptRow.Key;
                }

            JObject frame = FrameAgreement(entities, harvest);
            report["frame_check"] = frame;
            if ((bool?)frame["agrees"] != true)
            {
                report["refused"] = "frame_unconfirmed";
                report["means"] = "the symbols read from the file do not land where Revit says this drawing " +
                                  "is. Nothing was planned from them rather than placing them somewhere " +
                                  "plausible and wrong. The commonest causes are a link placed by shared " +
                                  "coordinates and a drawing whose model space sits far from its paper space.";
                return report;
            }

            // ---- the zone, applied to symbols exactly as to geometry -----------
            int outside = 0;
            var heldOnBoundary = new JArray();
            if (set.ExtentMm != null)
            {
                var kept = new List<CadIrEntity>();
                foreach (CadIrEntity e in entities)
                {
                    if (set.ExtentMm.ContainsSymbol(e.Points[0])) kept.Add(e);
                    else
                    {
                        // A SYMBOL ON THE BOUNDARY BELONGS TO NEITHER UNIT until a person says
                        // which: named here, counted outside, never claimed by two zones.
                        if (set.ExtentMm.HoldsOnBoundary(e.Points[0]))
                            heldOnBoundary.Add(new JObject
                            {
                                ["block"] = e.BlockName,
                                ["handle"] = e.Handle,
                                ["at_mm"] = new JArray(Math.Round(e.Points[0].X, 1), Math.Round(e.Points[0].Y, 1)),
                                ["distance_to_boundary_mm"] = Math.Round(set.ExtentMm.DistanceToBoundary(e.Points[0]), 1)
                            });
                        outside++;
                        CadInventoryRow row;
                        if (rowOfEntity.TryGetValue(e, out row)) row.Outcome = CadInventoryOutcome.OutsideExtent;
                    }
                }
                entities = kept;
                report["outside_extent"] = outside;
                if (set.ExtentMm.SymbolsOnBoundaryMm > 0)
                {
                    report["held_on_boundary"] = heldOnBoundary;
                    report["held_on_boundary_means"] =
                        "symbols no further than " + set.ExtentMm.SymbolsOnBoundaryMm + " mm from the zone's boundary: " +
                        "left out of this zone (counted as outside_extent) so that no symbol is claimed by two " +
                        "neighbouring units. A person assigns each one.";
                }
            }

            // ---- what a mirror means, measured before it is decided -------------
            //
            // Nine of twenty-one symbols in one apartment are inserted reflected.
            // Whether that is a fact about the device or about the draughtsman is
            // settled by the block's OWN geometry: reflect the definition and see
            // whether anything moved.
            Dictionary<string, CadSymmetryReading> symmetry =
                CadBlockSymmetry.MeasureAll(reading.Entities, set.SymmetryToleranceMm);
            report["symmetry_measured_for_blocks"] = symmetry.Count;

            CadBlockReading blocks = CadBlockRules.Interpret(entities, set, sourceHash);
            foreach (var kv in blocks.Outcomes)
            {
                CadInventoryRow row;
                if (!rowOfEntity.TryGetValue(kv.Key, out row)) continue;
                row.Outcome = kv.Value.Outcome;
                row.RuleId = kv.Value.RuleId;
                row.CandidateId = kv.Value.Candidate == null ? null : kv.Value.Candidate.Id;
                row.TieRules = kv.Value.TieRules;
            }

            // ---- the policy, applied per candidate ------------------------------
            var mirrored = new JArray();
            foreach (CadCandidate c in blocks.Candidates)
            {
                if (!c.Mirrored) { c.MirrorResolution = "not_mirrored"; continue; }

                string bare = CadBlockRules.BareName(c.SourceBlockName) ?? c.SourceBlockName ?? "";
                CadSymmetryReading measured = null;
                if (c.SourceBlockName != null) symmetry.TryGetValue(c.SourceBlockName, out measured);
                if (measured == null && bare.Length > 0) symmetry.TryGetValue(bare, out measured);

                string policy = c.MirrorPolicy ?? "preserve";
                var evidence = new JObject
                {
                    ["candidate_id"] = c.Id,
                    ["block"] = c.SourceBlockName,
                    ["effective_block"] = c.SourceEffectiveName,
                    ["policy"] = policy,
                    ["symmetry"] = measured?.ToJson()
                };

                switch (policy)
                {
                    case "symmetric":
                        // ACCREDITED, NOT ASSERTED. A set that claims symmetry for a
                        // block whose reflection moves its geometry is refused here,
                        // with the residual that refused it.
                        if (measured?.Symmetric == true)
                        {
                            c.MirrorResolution = "symmetric_by_measurement";
                            evidence["means"] = "the set says this symbol's mirror is a drafting convenience " +
                                                "and the block's own geometry agrees, so it is built unmirrored.";
                        }
                        else
                        {
                            c.MirrorResolution = "pending_symmetry_not_accredited";
                            evidence["means"] = measured == null
                                ? "the set claims symmetry and this reading could not see the block's " +
                                  "definition, so the claim is unchecked. NOT built."
                                : "the set claims symmetry and the block's own geometry does not support it. " +
                                  "NOT built.";
                        }
                        break;

                    case "variant":
                        c.MirrorResolution = "variant_type";
                        c.FamilyType = c.MirrorEvidenceVariant ?? c.FamilyType;
                        evidence["means"] = "the mirrored symbol means a different product, and the set names it.";
                        break;

                    case "pending":
                        c.MirrorResolution = "pending_by_policy";
                        evidence["means"] = "the set has not decided what this reflection means, so the symbol " +
                                            "is listed and not built.";
                        break;

                    default:   // preserve
                        if (measured?.Symmetric == true)
                        {
                            // Preserving a reflection of a symbol that IS its own
                            // reflection is preserving nothing. Say so rather than
                            // asking the writer for an operation with no effect.
                            c.MirrorResolution = "mirror_has_no_effect";
                            evidence["means"] = "the reflection is preserved trivially: this block is its own " +
                                                "mirror image, measured, so the built device is the same either way.";
                        }
                        else
                        {
                            c.MirrorResolution = "preserve_requested";
                            evidence["means"] = "the reflection is part of what this symbol says, so the row asks " +
                                                "for it. A family that cannot be reflected refuses the row rather " +
                                                "than building it the wrong way round.";
                        }
                        break;
                }

                c.MirrorEvidence = evidence;
                mirrored.Add(evidence);
            }
            if (mirrored.Count > 0)
                report["mirrored_symbols"] = new JObject
                {
                    ["count"] = mirrored.Count,
                    ["resolutions"] = mirrored,
                    ["means"] = "every reflected insertion this reading found, what the set says it means, and " +
                                "what the block's own geometry says about that claim. A resolution beginning " +
                                "'pending' was NOT planned."
                };
            report["instances_considered"] = blocks.InstancesConsidered;
            report["candidates"] = blocks.Candidates.Count;
            // THE TOTALS ARE COUNTED FROM EVERYTHING, THE LIST IS BOUNDED. The
            // classification used to add up the listed names, and the list stops
            // at fifty: on a second apartment with 50+ symbol names, 19 instances
            // fell out of the accounting as "unaccounted".
            report["unclaimed_instances"] = blocks.Unclaimed.Sum(u => u.Count);
            report["unclaimed_block_names"] = blocks.Unclaimed.Count;
            report["unclaimed_blocks"] = new JArray(blocks.Unclaimed
                .OrderByDescending(u => u.Count).Take(UnclaimedListed).Select(u => u.ToJson()));
            if (blocks.Unclaimed.Count > UnclaimedListed)
                report["unclaimed_blocks_truncated"] =
                    "the " + UnclaimedListed + " most frequent of " + blocks.Unclaimed.Count + " names are listed; " +
                    "unclaimed_instances counts all of them, and horizun_query_cad mode='blocks' with this same " +
                    "requirement_set pages through every placement";
            report["inventory"] = "every placement, paged and reconciled: horizun_query_cad mode='blocks' with this " +
                                  "requirement_set";
            report["ties"] = new JArray(blocks.Ties.Select(x => x.ToJson()));

            // The candidates join the reading. Everything downstream - eligibility,
            // the plan, the fingerprint, the apply's provenance - treats them as
            // what they are: candidates with a rule, a layer and a source.
            interpretation.Candidates.AddRange(blocks.Candidates);
            return report;
        }

        /// <summary>A drawing name without its extension, for comparing a file with a link.</summary>
        private static string BareDrawingName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string bare = name.Trim();
            if (bare.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) ||
                bare.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                bare = bare.Substring(0, bare.Length - 4);
            return bare;
        }

        /// <summary>
        /// Do the symbols land where Revit says this drawing is?
        ///
        /// The question is whether the two readings share a FRAME - the same
        /// scale and the same origin - and the honest test is how many symbols
        /// fall on the geometry Revit handed over. A frame that is wrong by the
        /// usual amounts puts almost nothing there: a unit read as millimetres
        /// instead of inches is out by 25.4, an unapplied transform by the whole
        /// width of the site.
        ///
        /// It is NOT containment, and that was the first version's mistake.
        /// MEASURED on this drawing: model-space symbols reach 86 m to the left
        /// of everything Revit drew, because the file resolves external
        /// references this import does not show. Requiring every symbol to sit
        /// inside the geometry box refused a reading whose frame was provably
        /// right - the same layer's lowest symbol matched the same layer's lowest
        /// line work to four figures.
        ///
        /// So: the fraction is measured, the threshold is stated, and the
        /// outliers are counted rather than hidden.
        /// </summary>
        /// <summary>
        /// The DWG itself, resolved from the link (or the caller when the link cannot
        /// say), read, and checked to be the drawing the link shows. Null with
        /// report["refused"] set when any of that fails.
        /// </summary>
        private static CadDwgReading ReadDrawing(CadInstanceFacts facts, CadRequirementSet set,
                                                 string callerPath, int readTimeoutSeconds, JObject report)
        {
            // THE LINK FIRST, THE CALLER ONLY IF THE LINK CANNOT SAY.
            string path = facts != null ? facts.ExternalPath : null;
            string pathSource = "link";
            if (string.IsNullOrWhiteSpace(path))
            {
                path = callerPath;
                pathSource = "caller";
            }
            else if (!string.IsNullOrWhiteSpace(callerPath) &&
                     !string.Equals(System.IO.Path.GetFullPath(callerPath),
                                    System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                report["refused"] = "dwg_path_disagrees_with_the_link";
                report["link_resolves"] = path;
                report["caller_named"] = callerPath;
                report["means"] = "this link resolves its own file and the caller named a different one. " +
                                  "Nothing was read: a plan built from one drawing and applied against " +
                                  "another is the failure this whole binding exists to prevent.";
                return null;
            }
            report["path_source"] = pathSource;

            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                report["refused"] = "no_readable_dwg_file";
                report["path"] = path ?? "(none)";
                report["means"] = "the blocks branch has to read the DWG, and no readable path names it. " +
                                  "MEASURED in Revit 2026: a CAD link created through the API is NOT an " +
                                  "ExternalFileReference, so the file cannot be recovered from the link - " +
                                  "pass dwg_path. The blocks rules planned NOTHING; every other rule was " +
                                  "unaffected.";
                return null;
            }

            CadDwgRunResult run;
            try { run = CadDwgReader.Read(path, readTimeoutSeconds, set.SourceUnitsToMm); }
            catch (Exception ex)
            {
                report["refused"] = "dwg_reader_threw";
                report["detail"] = ex.Message;
                return null;
            }
            report["engine"] = run.EnginePath;
            report["engine_version"] = run.EngineVersion;
            report["seconds"] = Math.Round(run.Seconds, 1);
            // WHETHER THE EXTRACTION WAS REUSED, and why not: a changed reference is a miss by name.
            if (run.CacheDetail != null) report["cache"] = run.CacheDetail;
            if (!run.Ok)
            {
                report["refused"] = run.Refusal;
                if (run.RefusalDetail != null) report["detail"] = run.RefusalDetail;
                return null;
            }

            CadDwgReading reading = run.Reading;
            report["drawing"] = reading.DrawingName;

            // A PATH THE CALLER NAMED IS CHECKED AGAINST THE LINK, by the only
            // thing both of them carry: the drawing's name. Revit calls the
            // import symbol after the file, and the file says its own name in the
            // reading's header. They must be the same drawing.
            if (pathSource == "caller")
            {
                string linkName = facts != null ? facts.Name : null;
                string bare = BareDrawingName(reading.DrawingName);
                string linkBare = BareDrawingName(linkName);
                if (linkBare != null && bare != null &&
                    !string.Equals(bare, linkBare, StringComparison.OrdinalIgnoreCase))
                {
                    report["refused"] = "dwg_path_names_a_different_drawing";
                    report["link_is_called"] = linkName;
                    report["file_says_it_is"] = reading.DrawingName;
                    report["means"] = "the file named by dwg_path is not the drawing this link is showing. " +
                                      "Nothing was planned from it.";
                    return null;
                }
                report["name_check"] = "the file calls itself '" + (bare ?? "(nothing)") +
                                       "' and the link is called '" + (linkBare ?? "(nothing)") + "'";
            }
            report["declared_mm_per_unit"] = reading.MmPerUnit.HasValue
                ? (JToken)reading.MmPerUnit.Value : JValue.CreateNull();
            report["entities_read"] = reading.Entities.Count;
            report["repeated_top_level_rows"] = reading.RepeatedTopLevelRows;
            return reading;
        }

        /// <summary>
        /// The drawing's WALL HATCHES, in model millimetres, for a set whose wall
        /// rules declare solid_hatch_layers. The same file, the same frame and the
        /// same checks as the symbols; null with report["refused"] set when the
        /// drawing cannot be read or does not land where Revit shows it. Every
        /// command that interprets such a set calls this, so the plan, the audit
        /// and the update agree on which pairs of faces enclose material.
        /// </summary>
        /// <summary>
        /// Why a read was refused, as text. MEASURED (campaign 4): the detail of a reader refusal is an
        /// OBJECT (it names the missing reference), and casting it to a string threw instead of refusing.
        /// </summary>
        public static string Explain(JObject report)
        {
            if (report == null) return "";
            JToken t = report["means"] ?? report["detail"];
            if (t == null || t.Type == JTokenType.Null) return "";
            return t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static CadSolidHatch ReadSolid(Element element, CadInstanceFacts facts, CadRequirementSet set,
                                              CadHarvest harvest, string callerPath, int readTimeoutSeconds,
                                              JObject report)
        {
            var layers = set.Rules.Where(r => r.Geometry != null)
                                  .SelectMany(r => r.Geometry.SolidHatchLayers)
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            report["why"] = "a wall rule declares solid_hatch_layers, and a hatch boundary is not reachable " +
                            "through Revit's import. So the DWG itself is read for its wall hatches.";
            report["layers"] = new JArray(layers.Cast<object>().ToArray());
            CadDwgReading reading = ReadDrawing(facts, set, callerPath, readTimeoutSeconds, report);
            if (reading == null) return null;
            CadPlacementReading placement = CadBlockPlacement.Place(reading.Entities);

            Transform t = Transform.Identity;
            var import = element as ImportInstance;
            if (import != null) { try { t = import.GetTransform(); } catch { t = Transform.Identity; } }
            Func<CadPoint, CadPoint> toModel = at =>
            {
                XYZ ft = t.OfPoint(new XYZ(at.X / 304.8, at.Y / 304.8, at.Z / 304.8));
                return new CadPoint(ft.X * 304.8, ft.Y * 304.8, ft.Z * 304.8);
            };
            CadSolidHatch solid = CadSolidHatch.Build(reading.Entities, placement, layers, toModel,
                                                      set.CaseSensitiveLayers);
            report["hatches"] = solid.SummaryJson();
            if (solid.PlacedHatches == 0)
            {
                report["refused"] = "no_hatch_on_the_declared_layers";
                report["means"] = "the set says walls are hatched on these layers and the drawing has no such " +
                                  "hatch in model space. Nothing was planned from the wall rules rather than " +
                                  "treating every pair of faces as empty.";
                return null;
            }

            // THE SAME FRAME CHECK AS THE SYMBOLS, on one corner of every placed ring.
            var anchors = solid.Anchors().Select(p => new CadIrEntity { Points = { p } }).ToList();
            JObject frame = FrameAgreement(anchors, harvest);
            report["frame_check"] = frame;
            if ((bool?)frame["agrees"] != true)
            {
                report["refused"] = "frame_unconfirmed";
                report["means"] = "the hatches read from the file do not land where Revit says this drawing is. " +
                                  "Nothing was planned from the wall rules.";
                return null;
            }
            return solid;
        }

        /// <summary>
        /// The drawing's TEXTS and LEADERS on the given layers, in model millimetres - model
        /// space and every placement of a block or external reference that carries them (a
        /// corridor's duct sizes live in an xref). Same frame check as the symbols: labels that
        /// do not land where Revit says the drawing is are refused, not used.
        /// </summary>
        public static bool ReadLabels(Element element, CadInstanceFacts facts, CadRequirementSet set, CadHarvest harvest,
                                      string callerPath, int readTimeoutSeconds, IList<string> layerPatterns,
                                      List<CadLabel> labels, List<CadLeaderLine> leaders, JObject report)
        {
            report["why"] = "a duct rule reads each run's section from the drawing's labels, and text is not " +
                            "reachable through Revit's import. So the DWG itself is read for its texts and leaders.";
            report["layers"] = new JArray(layerPatterns.Cast<object>().ToArray());
            CadDwgReading reading = ReadDrawing(facts, set, callerPath, readTimeoutSeconds, report);
            if (reading == null) return false;
            CadPlacementReading placement = CadBlockPlacement.Place(reading.Entities);
            Transform t = Transform.Identity;
            var import = element as ImportInstance;
            if (import != null) { try { t = import.GetTransform(); } catch { t = Transform.Identity; } }
            Func<CadPoint, CadPoint> toModel = at =>
            {
                XYZ ft = t.OfPoint(new XYZ(at.X / 304.8, at.Y / 304.8, at.Z / 304.8));
                return new CadPoint(ft.X * 304.8, ft.Y * 304.8, ft.Z * 304.8);
            };
            var byBlock = new Dictionary<string, List<CadPlacedBlock>>(StringComparer.OrdinalIgnoreCase);
            foreach (CadPlacedBlock p in placement.Placed)
            {
                if (p.Space != null && p.Space != "model") continue;
                List<CadPlacedBlock> list;
                if (!byBlock.TryGetValue(p.BlockName ?? "", out list)) byBlock[p.BlockName ?? ""] = list = new List<CadPlacedBlock>();
                list.Add(p);
            }
            bool cs = set.CaseSensitiveLayers;
            int texts = 0, leaderCount = 0, skippedPaper = 0;
            foreach (CadIrEntity e in reading.Entities)
            {
                bool isText = e.Kind == CadEntityKind.Text && e.Text != null && e.Points.Count > 0;
                bool isLeader = e.Kind == CadEntityKind.Leader && e.Points.Count >= 2;
                if (!isText && !isLeader) continue;
                // The layer as Revit names it inside an xref ("XREF|LAYER") and bare both match.
                string layer = e.Layer ?? "";
                string bare = layer.Contains("|") ? layer.Substring(layer.LastIndexOf('|') + 1) : layer;
                if (!layerPatterns.Any(p => CadGlob.IsMatch(layer, p, cs) || CadGlob.IsMatch(bare, p, cs))) continue;
                var frames = new List<Tuple<string, Func<CadPoint, CadPoint>>>();
                if (e.BlockPath.Count == 0)
                {
                    if (e.Space != "model") { skippedPaper++; continue; }
                    frames.Add(Tuple.Create(e.Handle ?? e.Id, toModel));
                }
                else
                {
                    List<CadPlacedBlock> hosts;
                    if (!byBlock.TryGetValue(e.BlockPath[e.BlockPath.Count - 1], out hosts)) continue;
                    foreach (CadPlacedBlock p in hosts)
                    {
                        CadPlacedBlock frame = p;
                        frames.Add(Tuple.Create(frame.Key + "/" + (e.Handle ?? e.Id), (Func<CadPoint, CadPoint>)(q => toModel(frame.Place(q)))));
                    }
                }
                foreach (var f in frames)
                {
                    if (isText)
                    {
                        CadPoint a = e.Points[0];
                        double rot = e.RotationRadians ?? 0;
                        CadPoint a2 = new CadPoint(a.X + 1000 * Math.Cos(rot), a.Y + 1000 * Math.Sin(rot), a.Z);
                        CadPoint m1 = f.Item2(a), m2 = f.Item2(a2);
                        labels.Add(new CadLabel
                        {
                            Id = "text:" + f.Item1, Text = e.Text, Layer = layer, At = m1,
                            RotationRadians = e.RotationRadians.HasValue ? Math.Atan2(m2.Y - m1.Y, m2.X - m1.X) : (double?)null
                        });
                        texts++;
                    }
                    else
                    {
                        var ld = new CadLeaderLine { Id = "leader:" + f.Item1, Layer = layer };
                        foreach (CadPoint q in e.Points) ld.Points.Add(f.Item2(q));
                        leaders.Add(ld);
                        leaderCount++;
                    }
                }
            }
            report["texts"] = texts;
            report["leaders"] = leaderCount;
            report["on_sheets_not_read"] = skippedPaper;
            var anchors = labels.Select(l => new CadIrEntity { Points = { l.At } }).ToList();
            JObject frameCheck = FrameAgreement(anchors, harvest);
            report["frame_check"] = frameCheck;
            if ((bool?)frameCheck["agrees"] != true)
            {
                report["refused"] = "frame_unconfirmed";
                report["means"] = "the labels read from the file do not land where Revit says this drawing is. No " +
                                  "section was read from them.";
                labels.Clear(); leaders.Clear();
                return false;
            }
            return true;
        }

        private static JObject FrameAgreement(IList<CadIrEntity> symbols, CadHarvest harvest)
        {
            var result = new JObject();
            if (symbols == null || symbols.Count == 0)
            {
                result["agrees"] = true;
                result["why"] = "no symbols to place, so there is nothing to disagree about";
                return result;
            }
            if (harvest == null || harvest.Segments == null || harvest.Segments.Count == 0)
            {
                result["agrees"] = false;
                result["why"] = "Revit handed over no geometry for this instance, so there is nothing to " +
                                "check the symbols against";
                return result;
            }

            double gx0 = double.MaxValue, gy0 = double.MaxValue, gx1 = double.MinValue, gy1 = double.MinValue;
            foreach (CadSegment seg in harvest.Segments)
            {
                gx0 = Math.Min(gx0, Math.Min(seg.A.X, seg.B.X)); gx1 = Math.Max(gx1, Math.Max(seg.A.X, seg.B.X));
                gy0 = Math.Min(gy0, Math.Min(seg.A.Y, seg.B.Y)); gy1 = Math.Max(gy1, Math.Max(seg.A.Y, seg.B.Y));
            }

            // A symbol may sit on the very edge of the line work, and a drawing is
            // tens of metres across, so the slack is a fraction of the drawing
            // rather than a fixed number of millimetres.
            double slack = 0.05 * Math.Max(gx1 - gx0, gy1 - gy0);
            int inside = 0;
            double sx0 = double.MaxValue, sy0 = double.MaxValue, sx1 = double.MinValue, sy1 = double.MinValue;
            foreach (CadIrEntity e in symbols)
            {
                CadPoint p = e.Points[0];
                sx0 = Math.Min(sx0, p.X); sx1 = Math.Max(sx1, p.X);
                sy0 = Math.Min(sy0, p.Y); sy1 = Math.Max(sy1, p.Y);
                if (p.X >= gx0 - slack && p.X <= gx1 + slack && p.Y >= gy0 - slack && p.Y <= gy1 + slack)
                    inside++;
            }

            double fraction = (double)inside / symbols.Count;
            const double Threshold = 0.5;
            result["agrees"] = fraction >= Threshold;
            result["symbols"] = symbols.Count;
            result["symbols_on_the_drawn_geometry"] = inside;
            result["fraction"] = Math.Round(fraction, 3);
            result["threshold"] = Threshold;
            result["symbols_box_mm"] = new JArray(Math.Round(sx0), Math.Round(sy0), Math.Round(sx1), Math.Round(sy1));
            result["geometry_box_mm"] = new JArray(Math.Round(gx0), Math.Round(gy0), Math.Round(gx1), Math.Round(gy1));
            result["slack_mm"] = Math.Round(slack);
            result["why"] = fraction >= Threshold
                ? "most symbols read from the file fall on the geometry Revit handed over, so the two readers " +
                  "share a frame. The ones outside it are symbols whose line work this import does not show - " +
                  "commonly an external reference the file resolves and the link does not."
                : "the symbols read from the file do not fall on the geometry Revit handed over, so the two " +
                  "readers do not share a frame. Nothing is planned from them.";
            return result;
        }

    }
}
