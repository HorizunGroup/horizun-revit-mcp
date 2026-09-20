// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// horizun_plan_from_ifc — what an IFC contains, and the request that rebuilds the
// part of it this bridge can rebuild EXACTLY.
//
// READ-ONLY. It opens no transaction and writes nothing at all. The apply half is
// horizun_apply_ifc_plan, a separate confirmable command — the same shape
// horizun_plan_from_cad / horizun_apply_cad_plan already uses, for the same
// reason: a conversion somebody has not read is a conversion nobody agreed to.
//
// G19 of the 2026-09-14 inventory asked for "reconstrucción IFC a elementos
// nativos ... declara explícitamente las clases que no se pueden reconstruir".
// BOTH clauses. An inventory alone would be the second clause pretending to be
// the whole job.
//
// WHAT IT PLANS, decided by REPRESENTATION and never by class name alone — the
// resolution lives in Core/IfcSubset.cs and the set is closed:
//
//   WALLS    · an 'Axis' 2-point polyline → a Revit wall on the mapped level and
//              type, at the height the Body extrusion measures (or a declared
//              default), with its base offset computed from the level it lands on.
//   COLUMNS  · a placement, plus a vertical Body extrusion for the height → a
//              structural column at the placement point, ROTATED by the
//              placement's own plan angle. The section is the Revit type's.
//   BEAMS    · an 'Axis' 2-point polyline → structural framing between the points.
//   SLABS    · a vertical Body extrusion of a horizontal polygon → a Revit floor
//              on that polygon, holes included.
//   OPENINGS · an IfcOpeningElement voiding a planned wall, rectangular → a wall
//              opening. UNLESS it is filled by a door or a window, because a
//              hosted family cuts its own hole and doing both cuts it twice.
//   DOORS,
//   WINDOWS  · filling an opening in a planned wall → a hosted family instance at
//              the opening's centre, on the caller's symbol.
//
// WHAT IT REFUSES, every one of them NAMED with the representation that caused
// it: BREPs, tessellations, CSG, mapped representations, revolutions, composite
// and trimmed curves, arcs in a boundary, circular and parameterised profiles,
// multi-solid bodies, grid placements, placement cycles, spaces, stairs,
// railings, curtain walls and proxies. The refusals are the deliverable as much
// as the plan is: an importer whose report lists only what it managed is an
// importer that drops a third of a building without anybody noticing.
//
// WHAT IT STILL DOES NOT DO, said here rather than discovered later: property
// sets and quantities are counted and not transferred; a layered IFC construction
// is not rebuilt as a Revit compound type; materials are reported and, where the
// caller names a text parameter, recorded on the element — Revit carries material
// on the TYPE, and inventing types would be inventing a standard.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class PlanFromIfcCommand : ICommand
    {
        public string Name => "horizun_plan_from_ifc";

        public string Description =>
            "Read an IFC, report everything in it with a per-class verdict, and PLAN the subset this bridge can " +
            "rebuild exactly: walls, columns, beams, slabs, their openings and the doors and windows that fill " +
            "them. Emits a horizun_create_elements request per stage plus the binding horizun_apply_ifc_plan " +
            "needs. Detects what a previous run already imported and leaves it alone. Writes nothing.";

        /// <summary>The most rows one plan may carry before the caller is asked to split it.</summary>
        public const int MaxPlannedRows = 4000;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            CommandResult wrongDocument = DocumentGate.ReadGuard(doc, request, Name);
            if (wrongDocument != null) return wrongDocument;

            string path = request.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return CommandResult.Fail("path is required: the .ifc file to read.");

            string readError;
            IfcStepReader.Document ifc = IfcStepReader.Read(path, out readError);
            if (ifc == null) return CommandResult.Fail(readError);

            long sourceBytes;
            string sourceProblem;
            string sourceSha = IfcSourceIdentity.Sha256(path, out sourceBytes, out sourceProblem);
            if (sourceSha == null)
                return CommandResult.Fail("the file parsed but could not be hashed, and a plan that cannot name " +
                                          "the bytes it was made from cannot be checked at apply time: " + sourceProblem);

            string unitBasis;
            double lengthScale = IfcPlacement.LengthScale(ifc, out unitBasis);

            JArray inventory = Inventory(ifc);

            var context = new PlanContext
            {
                Doc = doc,
                Ifc = ifc,
                Relations = IfcSubset.Relations(ifc),
                LengthScale = lengthScale,
                Request = request,
                Path = path,
                SourceSha = sourceSha,
                AlreadyBuilt = IfcProvenanceStore.Records(doc)
            };

            CommandResult levelProblem = ResolveLevels(context);
            if (levelProblem != null) return levelProblem;

            PlanWalls(context);
            PlanColumns(context);
            PlanBeams(context);
            PlanSlabs(context);
            PlanOpenings(context);
            PlanHosted(context);

            if (context.Rows.Count > MaxPlannedRows)
                return CommandResult.Fail(
                    "this plan holds " + context.Rows.Count + " rows and the bound is " + MaxPlannedRows + ". " +
                    "An import nobody can review is an import nobody agreed to: narrow it with only_classes, " +
                    "only_kinds or level_mapping, or split the file.");

            JArray actions = BuildActions(context);
            // THE SAME FUNCTION THE APPLY USES. Two fingerprints over the same bytes is
            // one too many: the plan would stamp one value, the apply would compute
            // another, and every apply would refuse stale_plan over a file nobody had
            // touched.
            string actionsFingerprint = IfcPlanFingerprint.OfActions(actions);
            string planFingerprint = Fingerprint("ifcplan", new JObject
            {
                ["source_sha256"] = sourceSha,
                ["length_scale_mm"] = lengthScale,
                ["rows"] = context.Rows.Count,
                ["actions"] = actionsFingerprint,
                ["target_document"] = SafeTitle(doc)
            });

            return CommandResult.Ok(new JObject
            {
                ["document"] = SafeTitle(doc),
                ["ifc_path"] = path,
                ["ifc_schema"] = ifc.SchemaIdentifier,
                ["ifc_sha256"] = sourceSha,
                ["ifc_bytes"] = sourceBytes,
                ["entity_instances"] = ifc.ById.Count,
                ["units"] = new JObject
                {
                    ["millimetres_per_ifc_length_unit"] = lengthScale,
                    ["basis"] = unitBasis,
                    ["means"] = "every coordinate below is in MILLIMETRES, and each create_elements request " +
                                "declares units=mm. A file read at the wrong scale produces a building either the " +
                                "size of a city or of a coin, so the basis is printed rather than assumed."
                },
                ["inventory"] = inventory,
                ["summary"] = context.Summary(),
                ["levels"] = context.LevelReport,
                ["already_imported"] = new JObject
                {
                    ["elements_carrying_ifc_provenance"] = context.AlreadyBuilt.Count,
                    ["unchanged"] = context.Unchanged,
                    ["changed"] = context.Changed.Count,
                    ["means"] = context.AlreadyBuilt.Count == 0
                        ? "no element in this document remembers an IFC origin, so this is a first import."
                        : "elements built by a previous run carry the IFC GlobalId they came from AND a hash " +
                          "of the row they were built from. UNCHANGED means this file describes them exactly " +
                          "as before and they were skipped. CHANGED means this file describes them " +
                          "DIFFERENTLY - those are listed under 'changed', with the row this file would " +
                          "build, and they are UPDATED IN PLACE by the update actions at the end of the " +
                          "plan. They are never recreated: creating would duplicate, and delete-and-recreate " +
                          "would lose the edits, tags and dimensions attached to them since."
                },
                ["changed"] = context.Changed,
                ["orphaned"] = context.Orphans(),
                ["orphaned_policy"] =
                    "elements a previous import built whose IFC entity is NOT in this file. NOTHING IS " +
                    "DELETED and nothing will be: 'absent from this file' has at least four causes that need " +
                    "opposite responses - the entity was deleted upstream, this export was filtered to a " +
                    "subset, the exporter re-issued the same things under new GlobalIds, or somebody edited " +
                    "the element here on purpose. This import cannot tell them apart, and deleting is the one " +
                    "action that cannot be undone from a wrong guess. They are listed with their element ids " +
                    "so a person can decide. The absence is measured against the WHOLE FILE, not against what " +
                    "this plan considered - otherwise a run restricted to walls would report every imported " +
                    "door as orphaned.",
                ["updates_planned"] = context.Updates.Count,
                ["updates_mean"] =
                    "each update edits an element that already exists, keeping its id and everything attached " +
                    "to it. The apply half reports every field as updated, unchanged, unsupported or refused - " +
                    "a field this build cannot express in place is NAMED rather than dropped, because a " +
                    "difference that disappears from the report is one every future run will detect again " +
                    "with nobody able to see why.",
                ["plan"] = context.Rows,
                ["skipped"] = context.Skipped,
                ["coverage"] = context.Coverage(),
                ["reconstruction"] = context.Reconstruction(),
                ["execute_plan_request"] = new JObject
                {
                    ["tool"] = "horizun_apply_ifc_plan",
                    ["actions"] = actions
                },
                ["apply_binding"] = new JObject
                {
                    ["plan_fingerprint"] = planFingerprint,
                    ["actions_fingerprint"] = actionsFingerprint,
                    ["source_sha256"] = sourceSha,
                    ["source_path"] = path,
                    ["source_bytes"] = sourceBytes,
                    ["length_scale_mm"] = lengthScale,
                    ["target_document"] = SafeTitle(doc),
                    ["revit_version"] = SafeVersion(app),
                    ["resolved_names"] = context.ResolvedNames
                },
                ["candidate_index"] = context.Candidates,
                ["writes_nothing"] = true,
                ["how_to_apply"] =
                    "Send apply_binding and execute_plan_request.actions VERBATIM to horizun_apply_ifc_plan. It " +
                    "re-hashes the file, re-checks every resolved type and level, and refuses stale_plan naming " +
                    "which one moved. Rows whose host is built in an earlier stage carry host_global_id rather " +
                    "than host_id: the apply resolves it from what it has just created, which is the only moment " +
                    "that id exists.",
                ["what_this_does_not_do"] =
                    "Property sets and quantities are counted and NOT transferred. A layered IFC construction is " +
                    "not rebuilt as a Revit compound type. Materials are reported, and written to a text " +
                    "parameter when material_parameter names one, because Revit carries material on the TYPE and " +
                    "inventing types would be inventing a standard. Every geometry kind outside the supported " +
                    "set is named in 'skipped' with the representation that caused it.",
                ["lineage"] =
                    "Every planned row carries its source GlobalId, and horizun_apply_ifc_plan records it on the " +
                    "created element in Extensible Storage. Revit's UniqueId is not the IFC GlobalId and never " +
                    "will be; the lineage is a recorded fact, not an identity."
            });
        }

        // =====================================================================
        // Inventory
        // =====================================================================

        private static JArray Inventory(IfcStepReader.Document ifc)
        {
            var inventory = new JArray();
            foreach (KeyValuePair<string, List<IfcEntity>> pair in ifc.ByType
                         .OrderByDescending(p => p.Value.Count)
                         .ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!IsElementClass(pair.Key)) continue;
                ClassVerdict verdict;
                if (!Verdicts.TryGetValue(pair.Key, out verdict)) verdict = ClassVerdict.Unknown();
                inventory.Add(new JObject
                {
                    ["ifc_class"] = pair.Key,
                    ["count"] = pair.Value.Count,
                    ["verdict"] = verdict.Kind,
                    ["revit_category"] = verdict.Category,
                    ["reason"] = verdict.Reason
                });
            }
            return inventory;
        }

        // =====================================================================
        // Levels
        // =====================================================================

        /// <summary>
        /// Which Revit level each IFC storey maps to.
        ///
        /// A PROJECT'S LEVEL SCHEME IS A DECISION, NOT DATA. This does not create levels:
        /// it maps the file's storeys onto levels that already exist, by explicit mapping
        /// first and by exact name second, and reports every storey it could not place. An
        /// importer that invents levels leaves a model with two parallel schemes somebody
        /// has to reconcile by hand.
        /// </summary>
        private static CommandResult ResolveLevels(PlanContext c)
        {
            var levels = new FilteredElementCollector(c.Doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            if (levels.Count == 0)
                return CommandResult.Fail("this document has no levels, and every element this plans belongs to " +
                                          "one. Nothing else was read.");

            JObject mapping = c.Request["level_mapping"] as JObject;
            long fallback = c.Request.Value<long?>("level_id") ?? -1;
            Level fallbackLevel = null;
            if (fallback >= 0 && Rid.CanRepresent(fallback))
                fallbackLevel = c.Doc.GetElement(Rid.Make(fallback)) as Level;
            if (fallback >= 0 && fallbackLevel == null)
                return CommandResult.Fail("level_id " + fallback + " is not a Level in this document.");
            c.FallbackLevel = fallbackLevel;

            foreach (IfcEntity storey in c.Ifc.Of("IFCBUILDINGSTOREY"))
            {
                string globalId = IfcStepReader.Text(storey.At(0));
                string name = IfcStepReader.Text(storey.At(2));
                double? elevation = IfcStepReader.Number(storey.At(9));   // Elevation
                Level resolved = null;
                string how = null;

                if (mapping != null)
                {
                    JToken chosen = (globalId == null ? null : mapping[globalId]) ??
                                    (name == null ? null : mapping[name]);
                    if (chosen != null)
                    {
                        if (chosen.Type == JTokenType.Integer)
                        {
                            long id = (long)chosen;
                            if (Rid.CanRepresent(id)) resolved = c.Doc.GetElement(Rid.Make(id)) as Level;
                        }
                        else if (chosen.Type == JTokenType.String)
                        {
                            string wanted = (string)chosen;
                            resolved = levels.FirstOrDefault(l => string.Equals(SafeName(l), wanted,
                                                                                StringComparison.OrdinalIgnoreCase));
                        }
                        if (resolved != null) how = "level_mapping";
                    }
                }
                if (resolved == null && !string.IsNullOrWhiteSpace(name))
                {
                    resolved = levels.FirstOrDefault(l => string.Equals(SafeName(l), name,
                                                                        StringComparison.OrdinalIgnoreCase));
                    if (resolved != null) how = "exact name match";
                }
                if (resolved == null && fallbackLevel != null) { resolved = fallbackLevel; how = "level_id fallback"; }

                if (resolved != null)
                {
                    c.LevelOfStorey[storey.Id] = resolved;
                    c.RememberResolved("level", resolved,
                        "storey '" + (name ?? globalId ?? "unnamed") + "' → level '" + SafeName(resolved) + "'");
                }

                c.LevelReport.Add(new JObject
                {
                    ["ifc_global_id"] = globalId,
                    ["ifc_name"] = name,
                    ["ifc_elevation_mm"] = elevation.HasValue
                        ? (JToken)Math.Round(elevation.Value * c.LengthScale, 3) : JValue.CreateNull(),
                    ["revit_level_id"] = resolved == null ? (JToken)JValue.CreateNull() : Rid.Value(resolved.Id),
                    ["revit_level_name"] = resolved == null ? null : SafeName(resolved),
                    ["matched_by"] = how,
                    ["note"] = resolved == null
                        ? "no Revit level was found for this storey, so its elements are not planned. Name one in " +
                          "level_mapping, or pass level_id as a fallback. Levels are NOT created here: a " +
                          "project's level scheme is a decision, not data."
                        : null
                });
            }

            if (c.LevelOfStorey.Count == 0 && fallbackLevel == null)
                return CommandResult.Fail(
                    "no IFC storey could be matched to a Revit level and no level_id fallback was given, so " +
                    "nothing could be planned. Call this again and read the 'levels' block, which names every " +
                    "storey in the file; then map them with level_mapping. Nothing was written and nothing else " +
                    "was read.");
            return null;
        }

        // =====================================================================
        // Walls
        // =====================================================================

        private static void PlanWalls(PlanContext c)
        {
            if (!c.Wants("wall")) return;

            double? declaredHeight = c.Request.Value<double?>("default_wall_height");

            foreach (IfcEntity wall in c.Entities("IFCWALLSTANDARDCASE", "IFCWALL", "IFCWALLELEMENTEDCASE"))
            {
                Candidate candidate = c.Begin(wall, "wall");
                if (candidate == null) continue;

                Level level = c.LevelFor(wall, candidate);
                if (level == null) continue;

                ElementType type = c.TypeFor(wall, candidate, typeof(WallType), "wall type");
                if (type == null) continue;

                IfcShape shape = IfcSubset.Shape(c.Ifc, wall, c.LengthScale);
                if (shape.Placement == null) { c.Skip(candidate, shape.PlacementRefusal); continue; }
                if (!shape.HasAxis) { c.Skip(candidate, shape.AxisRefusal); continue; }

                double? measured = IfcSubset.VerticalHeight(shape);
                double height;
                string heightSource;
                if (measured.HasValue)
                {
                    height = measured.Value;
                    heightSource = "height measured from the Body extrusion";
                }
                else if (declaredHeight.HasValue && declaredHeight.Value > 0)
                {
                    height = declaredHeight.Value;
                    heightSource = "height from default_wall_height, because " +
                                   (shape.BodyRefusal ?? "the body gave no vertical extrusion");
                }
                else
                {
                    c.Skip(candidate, "this wall's height could not be measured (" +
                                      (shape.BodyRefusal ?? "no vertical body extrusion") +
                                      ") and no default_wall_height was given. A wall built to a guessed height " +
                                      "is a wall somebody has to find and fix.");
                    continue;
                }

                double baseZ = Math.Min(shape.AxisStart[2], shape.AxisEnd[2]);

                var row = new JObject
                {
                    ["kind"] = "wall",
                    ["type_id"] = Rid.Value(type.Id),
                    ["level_id"] = Rid.Value(level.Id),
                    ["start"] = Coordinate(shape.AxisStart, baseZ),
                    ["end"] = Coordinate(shape.AxisEnd, baseZ),
                    ["height"] = Math.Round(height, 3),
                    ["offset"] = Math.Round(baseZ - LevelElevationMm(level), 3)
                };
                c.Parameters(row, wall, candidate);
                c.Emit(candidate, "walls", row, "Axis/Curve2D + " + heightSource);
            }
        }

        // =====================================================================
        // Columns
        // =====================================================================

        private static void PlanColumns(PlanContext c)
        {
            if (!c.Wants("column")) return;

            foreach (IfcEntity column in c.Entities("IFCCOLUMN"))
            {
                Candidate candidate = c.Begin(column, "structural_column");
                if (candidate == null) continue;

                Level level = c.LevelFor(column, candidate);
                if (level == null) continue;

                FamilySymbol symbol = c.SymbolFor(column, candidate, BuiltInCategory.OST_StructuralColumns,
                                                  "structural column family type");
                if (symbol == null) continue;

                IfcShape shape = IfcSubset.Shape(c.Ifc, column, c.LengthScale);
                if (shape.Placement == null) { c.Skip(candidate, shape.PlacementRefusal); continue; }

                double? rotation = shape.Placement.PlanRotationDegrees();
                if (!rotation.HasValue)
                {
                    // A TILTED COLUMN IS NOT A ROTATED ONE. Revit's point placement takes a
                    // rotation about Z; a placement tilted out of plan cannot be expressed
                    // that way, and placing it upright would stand a raking column straight —
                    // which looks correct in plan and is wrong everywhere else.
                    c.Skip(candidate, "this column's placement is tilted out of plan, and a point-placed Revit " +
                                      "column takes a rotation about Z only. Standing it upright would look " +
                                      "correct in plan and be wrong in every section.");
                    continue;
                }

                // THE POINT IS THE PLACEMENT ORIGIN, NOT THE PROFILE CENTRE. An IFC column's
                // profile may sit off-centre inside its own placement, and a Revit column is
                // placed at a point: taking the profile centre would move every column that
                // was modelled eccentric in its family.
                double[] point = shape.Placement.Origin;

                var row = new JObject
                {
                    ["kind"] = "structural_column",
                    ["type_id"] = Rid.Value(symbol.Id),
                    ["level_id"] = Rid.Value(level.Id),
                    ["point"] = Coordinate(point, point[2])
                };
                if (Math.Abs(rotation.Value) > 1e-6) row["rotation_degrees"] = Math.Round(rotation.Value, 6);

                double? height = IfcSubset.VerticalHeight(shape);
                string representation = height.HasValue
                    ? "ObjectPlacement + Body/SweptSolid height " + IfcProfile.Round(height.Value) + " mm"
                    : "ObjectPlacement only";

                if (height.HasValue)
                {
                    // THE HEIGHT IS WRITABLE AND WAS NOT BEING WRITTEN. A column placed at a
                    // point with no top constraint is a column of whatever height its TYPE
                    // defaults to, and for a 3.2 m storey with a 4 m type that is wrong in a
                    // way nobody sees in plan. Base and top offsets are instance parameters,
                    // and create_elements routes its `parameters` map through the one writer
                    // that parses units - so the value travels as a unit-bearing string
                    // rather than as a number in whatever units the reader assumes.
                    double baseOffset = point[2] - LevelElevationMm(level);
                    c.Parameter(candidate, "FAMILY_BASE_LEVEL_OFFSET_PARAM",
                                Math.Round(baseOffset, 3).ToString(CultureInfo.InvariantCulture) + " mm");
                    c.Parameter(candidate, "FAMILY_TOP_LEVEL_PARAM", Rid.Value(level.Id));
                    c.Parameter(candidate, "FAMILY_TOP_LEVEL_OFFSET_PARAM",
                                Math.Round(baseOffset + height.Value, 3)
                                    .ToString(CultureInfo.InvariantCulture) + " mm");
                    c.Note(candidate, "the IFC column is " + IfcProfile.Round(height.Value) + " mm tall, and " +
                                      "the row sets base and top offsets against the SAME level to reach it. " +
                                      "A column left on its type's default constraints is a column of " +
                                      "whatever height the type happens to be.");
                }
                else
                {
                    c.Note(candidate, "no vertical body extrusion, so the column's height is whatever its " +
                                      "Revit type defaults to. That is recorded rather than guessed at.");
                }
                if (shape.HasBody && shape.Body.Profile != null && shape.Body.Profile.IfcType != null)
                    c.Note(candidate, "the IFC section is " + shape.Body.Profile.IfcType +
                                      (shape.Body.Profile.Name == null ? "" : " '" + shape.Body.Profile.Name + "'") +
                                      "; the built column uses the Revit type's section, which may differ.");

                c.Parameters(row, column, candidate);
                c.Emit(candidate, "members", row, representation);
            }
        }

        // =====================================================================
        // Beams
        // =====================================================================

        private static void PlanBeams(PlanContext c)
        {
            if (!c.Wants("beam")) return;

            foreach (IfcEntity beam in c.Entities("IFCBEAM"))
            {
                Candidate candidate = c.Begin(beam, "structural_framing");
                if (candidate == null) continue;

                Level level = c.LevelFor(beam, candidate);
                if (level == null) continue;

                FamilySymbol symbol = c.SymbolFor(beam, candidate, BuiltInCategory.OST_StructuralFraming,
                                                  "structural framing family type");
                if (symbol == null) continue;

                IfcShape shape = IfcSubset.Shape(c.Ifc, beam, c.LengthScale);
                if (shape.Placement == null) { c.Skip(candidate, shape.PlacementRefusal); continue; }
                if (!shape.HasAxis)
                {
                    c.Skip(candidate, shape.AxisRefusal + " A beam without an axis would have to be inferred from " +
                                      "its solid, and the longest edge of a solid is not always the member's axis.");
                    continue;
                }

                var row = new JObject
                {
                    ["kind"] = "structural_framing",
                    ["type_id"] = Rid.Value(symbol.Id),
                    ["level_id"] = Rid.Value(level.Id),
                    ["start"] = Coordinate(shape.AxisStart, shape.AxisStart[2]),
                    ["end"] = Coordinate(shape.AxisEnd, shape.AxisEnd[2]),
                    ["structural_type"] = "Beam"
                };
                if (shape.HasBody && shape.Body.Profile != null && shape.Body.Profile.IfcType != null)
                    c.Note(candidate, "the IFC section is " + shape.Body.Profile.IfcType +
                                      "; the built member uses the Revit type's section, which may differ.");
                c.Parameters(row, beam, candidate);
                c.Emit(candidate, "members", row, "Axis/Curve3D");
            }
        }

        // =====================================================================
        // Slabs
        // =====================================================================

        private static void PlanSlabs(PlanContext c)
        {
            if (!c.Wants("slab")) return;

            foreach (IfcEntity slab in c.Entities("IFCSLAB"))
            {
                Candidate candidate = c.Begin(slab, "floor");
                if (candidate == null) continue;

                // A ROOF SLAB IS NOT A FLOOR. IFC says so in PredefinedType, and building a
                // roof as a floor puts a roof in the floor schedule for the rest of the
                // project's life.
                string predefined = IfcStepReader.Enumeration(slab.At(8));
                if (string.Equals(predefined, "ROOF", StringComparison.OrdinalIgnoreCase))
                {
                    c.Skip(candidate, "this slab declares PredefinedType=ROOF. Building it as a Revit floor would " +
                                      "put a roof in the floor schedule for the rest of the project; roofs are " +
                                      "not in this subset.");
                    continue;
                }

                Level level = c.LevelFor(slab, candidate);
                if (level == null) continue;

                ElementType type = c.TypeFor(slab, candidate, typeof(FloorType), "floor type");
                if (type == null) continue;

                IfcShape shape = IfcSubset.Shape(c.Ifc, slab, c.LengthScale);
                if (shape.Placement == null) { c.Skip(candidate, shape.PlacementRefusal); continue; }

                string why;
                if (!IfcSubset.IsHorizontalPlate(shape, out why)) { c.Skip(candidate, why); continue; }

                List<IfcLoop> loops = IfcProfile.WorldLoops(shape.Body, shape.Placement);
                double[] extrusion = IfcProfile.WorldExtrusion(shape.Body, shape.Placement);
                if (loops == null || loops.Count == 0 || extrusion == null)
                {
                    c.Skip(candidate, "the slab's profile could not be placed in world coordinates.");
                    continue;
                }

                // THE TOP FACE, not the bottom. Revit sketches a floor at its top and grows
                // downwards by the type's thickness; an IFC slab is extruded from its
                // underside upwards. Sketching the underside would sink every floor in the
                // building by its own thickness — an error that reads like a settlement
                // problem in a coordination review.
                double sketchZ = loops[0].Points[0][2] + Math.Max(0, extrusion[2]);

                var profile = new JArray();
                foreach (IfcLoop loop in loops)
                {
                    var contour = new JArray();
                    foreach (double[] p in loop.Points)
                        contour.Add(new JArray(Math.Round(p[0], 3), Math.Round(p[1], 3), Math.Round(sketchZ, 3)));
                    profile.Add(contour);
                }

                var row = new JObject
                {
                    ["kind"] = "floor",
                    ["type_id"] = Rid.Value(type.Id),
                    ["level_id"] = Rid.Value(level.Id),
                    ["profile"] = profile
                };
                c.Note(candidate, "the IFC slab is " + IfcProfile.Round(Math.Abs(extrusion[2])) + " mm thick; the " +
                                  "built floor takes its thickness from the Revit type, which may differ. The " +
                                  "sketch is placed at the slab's TOP face.");
                if (loops.Count > 1)
                    c.Note(candidate, (loops.Count - 1) + " opening(s) carried through as holes in the sketch.");
                c.Parameters(row, slab, candidate);
                c.Emit(candidate, "slabs", row, "Body/SweptSolid: vertical extrusion of a horizontal polygon");
            }
        }

        // =====================================================================
        // Openings
        // =====================================================================

        private static void PlanOpenings(PlanContext c)
        {
            if (!c.Wants("opening")) return;

            foreach (IfcEntity opening in c.Ifc.Of("IFCOPENINGELEMENT"))
            {
                int hostEntityId;
                if (!c.Relations.HostOfOpening.TryGetValue(opening.Id, out hostEntityId)) continue;

                // AN OPENING FILLED BY A DOOR OR A WINDOW IS NOT CUT SEPARATELY. A hosted
                // Revit family cuts its own hole; cutting it here as well cuts the wall
                // twice, and the second cut is invisible until somebody schedules areas.
                if (c.Relations.FilledBy.ContainsKey(opening.Id)) continue;

                string hostGlobalId;
                if (!c.PlannedWallByEntity.TryGetValue(hostEntityId, out hostGlobalId))
                    continue;   // its host is not planned; the host's own row already says why

                Candidate candidate = c.Begin(opening, "wall_opening");
                if (candidate == null) continue;

                IfcShape shape = IfcSubset.Shape(c.Ifc, opening, c.LengthScale);
                if (shape.Placement == null) { c.Skip(candidate, shape.PlacementRefusal); continue; }

                string why;
                double[][] corners = IfcSubset.RectangularOpening(shape, out why);
                if (corners == null) { c.Skip(candidate, why); continue; }

                var row = new JObject
                {
                    ["kind"] = "wall_opening",
                    ["corner_1"] = Coordinate(corners[0], corners[0][2]),
                    ["corner_2"] = Coordinate(corners[1], corners[1][2])
                };
                if (c.Request.Value<bool?>("allow_structural") == true) row["allow_structural"] = true;
                c.Note(candidate, "host_id is resolved at apply time from the wall built for GlobalId " +
                                  hostGlobalId + ": that id does not exist until the wall stage commits.");
                c.Emit(candidate, "openings", row, "Body/SweptSolid: rectangular void in a planned wall",
                       hostGlobalId);
            }
        }

        // =====================================================================
        // Doors and windows
        // =====================================================================

        private static void PlanHosted(PlanContext c)
        {
            if (!c.Wants("hosted")) return;

            foreach (IfcEntity filler in c.Entities("IFCDOOR", "IFCWINDOW"))
            {
                Candidate candidate = c.Begin(filler, "family_instance");
                if (candidate == null) continue;

                int openingEntityId;
                if (!c.Relations.FillsOpening.TryGetValue(filler.Id, out openingEntityId))
                {
                    c.Skip(candidate, "this " + filler.Type + " fills no IfcOpeningElement, so there is no wall to " +
                                      "host it. A door placed without a host is a free-standing family that cuts " +
                                      "nothing and schedules as a door.");
                    continue;
                }

                int hostEntityId;
                string hostGlobalId;
                if (!c.Relations.HostOfOpening.TryGetValue(openingEntityId, out hostEntityId) ||
                    !c.PlannedWallByEntity.TryGetValue(hostEntityId, out hostGlobalId))
                {
                    c.Skip(candidate, "the wall this " + filler.Type + " belongs to is not in this plan, so there " +
                                      "is nothing to host it on. The wall's own entry in 'skipped' says why.");
                    continue;
                }

                FamilySymbol symbol = c.SymbolFor(filler, candidate,
                    IsDoor(filler) ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows,
                    IsDoor(filler) ? "door family type" : "window family type");
                if (symbol == null) continue;

                // THE CENTRE OF THE OPENING, not of the door. Several exporters put a door's
                // own placement on the hinge side, and a Revit family placed there sits half
                // a leaf off centre in the hole it is meant to fill.
                IfcEntity opening;
                double[] point = null;
                if (c.Ifc.ById.TryGetValue(openingEntityId, out opening) && opening != null)
                {
                    IfcShape openingShape = IfcSubset.Shape(c.Ifc, opening, c.LengthScale);
                    if (openingShape.Placement != null) point = IfcSubset.BodyCentre(openingShape);
                }
                if (point == null)
                {
                    IfcShape own = IfcSubset.Shape(c.Ifc, filler, c.LengthScale);
                    if (own.Placement == null) { c.Skip(candidate, own.PlacementRefusal); continue; }
                    point = own.Placement.Origin;
                    c.Note(candidate, "the opening's box could not be measured, so this uses the element's own " +
                                      "placement point. Several exporters put that point on the hinge side rather " +
                                      "than at the centre, so this one is worth checking by eye.");
                }

                var row = new JObject
                {
                    ["kind"] = "family_instance",
                    ["type_id"] = Rid.Value(symbol.Id),
                    ["point"] = Coordinate(point, point[2]),
                    ["coordinate_mode"] = "absolute",
                    ["structural_type"] = "NonStructural"
                };
                c.Note(candidate, "the family cuts its own hole, so the IfcOpeningElement it fills is NOT cut " +
                                  "separately. Cutting both would cut the wall twice.");
                c.Parameters(row, filler, candidate);
                c.Emit(candidate, "hosted", row, "IfcRelFillsElement into a planned wall", hostGlobalId);
            }
        }

        private static bool IsDoor(IfcEntity entity) =>
            string.Equals(entity.Type, "IFCDOOR", StringComparison.OrdinalIgnoreCase);

        // =====================================================================
        // Actions
        // =====================================================================

        /// <summary>
        /// One create_elements action per stage, split into batches nobody has to recover
        /// from by hand. The stage order is a DEPENDENCY order: a hole cannot be cut in a
        /// wall that does not exist yet, and a door cannot be hosted on one.
        /// </summary>
        private static JArray BuildActions(PlanContext c)
        {
            var actions = new JArray();
            int batchSize = Math.Max(1, Math.Min(200, c.Request.Value<int?>("batch_size") ?? 100));

            foreach (string stage in Stages)
            {
                List<PlannedRow> rows = c.RowsOfStage(stage);
                if (rows.Count == 0) continue;

                for (int offset = 0, batch = 1; offset < rows.Count; offset += batchSize, batch++)
                {
                    string key = stage + "-" + batch;
                    List<PlannedRow> slice = rows.Skip(offset).Take(batchSize).ToList();

                    var elements = new JArray();
                    for (int index = 0; index < slice.Count; index++)
                    {
                        elements.Add(slice[index].Row.DeepClone());
                        c.Candidates.Add(new JObject
                        {
                            ["key"] = key,
                            ["stage"] = stage,
                            ["element_index"] = index,
                            ["global_id"] = slice[index].GlobalId,
                            ["ifc_class"] = slice[index].IfcClass,
                            ["name"] = slice[index].Name,
                            ["representation_used"] = slice[index].Representation,
                            ["host_global_id"] = slice[index].HostGlobalId,
                            // Carried so the apply can store it, so the NEXT run can compare.
                            ["row_fingerprint"] = slice[index].RowFingerprint
                        });
                    }

                    actions.Add(new JObject
                    {
                        ["key"] = key,
                        ["stage"] = StageOrder(stage),
                        ["tool"] = "horizun_create_elements",
                        ["arguments"] = new JObject
                        {
                            ["units"] = "mm",
                            ["transaction_name"] = "Horizun: IFC import (" + stage + " " + batch + ")",
                            ["elements"] = elements
                        }
                    });
                }
            }

            // ---- the updates, LAST ---------------------------------------------------
            // After every create stage: an update touches an element that already exists
            // and depends on nothing this run builds, and running it first would let a
            // refused update stop the creates, which are what an import is for.
            for (int offset = 0, batch = 1; offset < c.Updates.Count; offset += batchSize, batch++)
            {
                var slice = new JArray(c.Updates.Skip(offset).Take(batchSize).Select(u => u.DeepClone()));
                if (slice.Count == 0) break;
                actions.Add(new JObject
                {
                    ["key"] = "update-" + batch,
                    ["stage"] = 90,
                    ["tool"] = "ifc_update_in_place",
                    ["arguments"] = new JObject
                    {
                        ["units"] = "mm",
                        ["transaction_name"] = "Horizun: IFC update (batch " + batch + ")",
                        ["elements"] = slice
                    }
                });
            }

            return actions;
        }

        /// <summary>The stages, in dependency order. Shared with the apply half so the two cannot disagree.</summary>
        public static readonly string[] Stages = { "walls", "members", "slabs", "openings", "hosted" };

        public static int StageOrder(string stage)
        {
            int index = Array.IndexOf(Stages, stage);
            return index < 0 ? 99 : index + 1;
        }

        // =====================================================================
        // The planning context
        // =====================================================================

        /// <summary>A create_elements row, plus the IFC identity that travels beside it.</summary>
        private sealed class PlannedRow
        {
            public JObject Row;
            public string GlobalId;
            public string IfcClass;
            public string Name;
            public string Representation;
            public string HostGlobalId;
            public string RowFingerprint;
        }

        private sealed class Candidate
        {
            public IfcEntity Entity;
            public string GlobalId;
            public string Name;
            public string PlannedKind;
            public JArray Notes = new JArray();

            /// <summary>What a previous run recorded about this entity, or null.</summary>
            public IfcProvenance Previous;

            /// <summary>The hash of the row this run would build. Set by Emit.</summary>
            public string RowFingerprint;

            /// <summary>Instance parameters a planner measured, merged into the row by `Parameters`.</summary>
            public JObject Parameters = new JObject();
        }

        private sealed class PlanContext
        {
            public Document Doc;
            public IfcStepReader.Document Ifc;
            public IfcRelations Relations;
            public double LengthScale;
            public JObject Request;
            public string Path;
            public string SourceSha;
            /// <summary>GlobalId → what a previous run recorded about the element it built.</summary>
            public Dictionary<string, IfcProvenance> AlreadyBuilt;

            /// <summary>
            /// Every GlobalId THE FILE carries, read from the file itself.
            ///
            /// NOT from the candidates this planner built. It plans walls, columns, beams,
            /// slabs, openings and hosted elements; an element a previous import created
            /// from an IfcFurnishingElement is not among them, and taking "not planned" for
            /// "not in the file" would report it as orphaned by a run that simply does not
            /// plan furniture. A GlobalId is 22 characters of the IFC base-64 alphabet in
            /// the first attribute, which is what makes this readable without knowing every
            /// class in the schema.
            /// </summary>
            public HashSet<string> GlobalIdsInFile()
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (Ifc == null) return seen;
                foreach (IfcEntity entity in Ifc.ById.Values)
                {
                    string first = IfcStepReader.Text(entity.At(0));
                    if (first != null && first.Length == 22) seen.Add(first);
                }
                return seen;
            }

            /// <summary>
            /// Elements a previous import built whose source entity is NOT in this file.
            ///
            /// REPORTED, NEVER DELETED, and that is not timidity. "Absent from this file"
            /// has at least four causes and they need opposite responses: the entity was
            /// deleted in the authoring model; this export was filtered to a subset; the
            /// exporter changed and re-issued the same thing under new GlobalIds; or the
            /// element was legitimately edited here after import and should survive. An
            /// importer cannot tell them apart, and the one irreversible action is the one
            /// it must not take on a guess.
            /// </summary>
            public JArray Orphans()
            {
                var orphans = new JArray();
                if (AlreadyBuilt == null) return orphans;
                HashSet<string> seen = GlobalIdsInFile();
                foreach (KeyValuePair<string, IfcProvenance> pair in AlreadyBuilt)
                {
                    if (seen.Contains(pair.Key)) continue;
                    orphans.Add(new JObject
                    {
                        ["global_id"] = pair.Key,
                        ["revit_element_id"] = pair.Value.ElementId >= 0
                            ? (JToken)pair.Value.ElementId : JValue.CreateNull(),
                        ["ifc_class"] = pair.Value.IfcClass,
                        ["built_from"] = pair.Value.SourcePath,
                        ["built_utc"] = pair.Value.WrittenUtc
                    });
                }
                return orphans;
            }
            public Level FallbackLevel;

            public readonly Dictionary<int, Level> LevelOfStorey = new Dictionary<int, Level>();
            public readonly JArray LevelReport = new JArray();
            public readonly JArray ResolvedNames = new JArray();
            public readonly JArray Rows = new JArray();
            public readonly JArray Skipped = new JArray();
            public readonly JArray Candidates = new JArray();

            /// <summary>IFC entity id of a PLANNED wall → the GlobalId its row carries.</summary>
            public readonly Dictionary<int, string> PlannedWallByEntity = new Dictionary<int, string>();

            /// <summary>Entities a previous run built and this file has not changed.</summary>
            public int Unchanged;

            /// <summary>Entities a previous run built and this file HAS changed.</summary>
            public readonly JArray Changed = new JArray();

            private readonly Dictionary<string, List<PlannedRow>> _byStage =
                new Dictionary<string, List<PlannedRow>>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _plannedByClass =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _skippedByClass =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<long> _resolvedIds = new HashSet<long>();

            public bool Wants(string family)
            {
                JArray only = Request["only_kinds"] as JArray;
                if (only == null || only.Count == 0) return true;
                return only.Values<string>().Any(v => string.Equals(v, family, StringComparison.OrdinalIgnoreCase));
            }

            public IEnumerable<IfcEntity> Entities(params string[] types)
            {
                JArray onlyClasses = Request["only_classes"] as JArray;
                HashSet<string> allowed = onlyClasses == null || onlyClasses.Count == 0
                    ? null
                    : new HashSet<string>(onlyClasses.Values<string>().Where(v => v != null),
                                          StringComparer.OrdinalIgnoreCase);
                foreach (string type in types)
                {
                    if (allowed != null && !allowed.Contains(type)) continue;
                    foreach (IfcEntity entity in Ifc.Of(type)) yield return entity;
                }
            }

            /// <summary>
            /// Start a candidate.
            ///
            /// An entity a previous run already built is NOT planned again - that would
            /// duplicate it - but whether it is UNCHANGED or CHANGED is decided later, once
            /// the row exists to fingerprint. So the candidate is built either way and
            /// `Emit` makes the call.
            /// </summary>
            public Candidate Begin(IfcEntity entity, string plannedKind)
            {
                string globalId = IfcStepReader.Text(entity.At(0));
                IfcProvenance previous = null;
                if (!string.IsNullOrWhiteSpace(globalId)) AlreadyBuilt.TryGetValue(globalId, out previous);
                return new Candidate
                {
                    Entity = entity,
                    GlobalId = globalId,
                    Name = IfcStepReader.Text(entity.At(2)),
                    PlannedKind = plannedKind,
                    Previous = previous
                };
            }

            public void Note(Candidate candidate, string note)
            {
                if (candidate != null && !string.IsNullOrWhiteSpace(note)) candidate.Notes.Add(note);
            }

            public void Skip(Candidate candidate, string why)
            {
                Count(_skippedByClass, candidate.Entity.Type);
                Skipped.Add(new JObject
                {
                    ["ifc_class"] = candidate.Entity.Type,
                    ["ifc_entity"] = candidate.Entity.Id,
                    ["global_id"] = candidate.GlobalId,
                    ["name"] = candidate.Name,
                    ["would_have_been"] = candidate.PlannedKind,
                    ["reason"] = why ?? "no reason was recorded, which is a defect in this command rather than in " +
                                        "the file."
                });
            }

            /// <summary>
            /// Record a planned row plus the IFC identity behind it.
            ///
            /// THE IDENTITY DOES NOT GO IN THE ROW. horizun_create_elements declares
            /// additionalProperties:false on both the request and each element, so a row
            /// carrying source_global_id is a row that does not match the contract it is
            /// being sent to. It travels in candidate_index instead, keyed by the action
            /// and by the position of the element within it - which is also what lets the
            /// apply half substitute a host id that does not exist until the stage before
            /// it has committed.
            /// </summary>
            /// <summary>
            /// The elements a re-issued file describes differently, with what to write.
            ///
            /// Separate from Rows on purpose: these are not created, and an update mixed
            /// into a create batch would be one an idempotency key could not tell apart.
            /// </summary>
            public readonly JArray Updates = new JArray();

            public void Emit(Candidate candidate, string stage, JObject row, string representation,
                             string hostGlobalId = null)
            {
                // THE ROW IS THE FINGERPRINT. Everything this run would build - geometry,
                // type, level, parameters - hashed once, here, where the row is complete.
                candidate.RowFingerprint = "ifcrow:" + RequestFingerprint.Sha256Hex(
                    RequestFingerprint.Canonical(row)).Substring(0, 24);

                if (candidate.Previous != null)
                {
                    // BUILT BEFORE. Not planned again, whatever else is true: creating it
                    // would put a second copy of the same entity in the model, perfectly
                    // aligned with the first.
                    bool same = string.Equals(candidate.Previous.RowFingerprint,
                                              candidate.RowFingerprint, StringComparison.Ordinal);
                    if (same) { Unchanged++; return; }

                    // CHANGED: UPDATED IN PLACE, not rebuilt. Deleting and recreating
                    // loses every edit anybody made since, every tag pointing at it and
                    // every dimension referencing it - so the element keeps its identity
                    // and the fields that CAN be edited are edited. The apply half reports
                    // each field as updated, unchanged, unsupported or refused; nothing
                    // that cannot be expressed in place is silently dropped.
                    Changed.Add(new JObject
                    {
                        ["global_id"] = candidate.GlobalId,
                        ["ifc_class"] = candidate.Entity.Type,
                        ["name"] = candidate.Name,
                        ["revit_element_id"] = candidate.Previous.ElementId >= 0
                            ? (JToken)candidate.Previous.ElementId : JValue.CreateNull(),
                        ["built_from"] = candidate.Previous.SourcePath,
                        ["built_utc"] = candidate.Previous.WrittenUtc,
                        ["previous_row"] = candidate.Previous.RowFingerprint,
                        ["this_row"] = candidate.RowFingerprint,
                        ["would_be"] = row.DeepClone(),
                        ["means"] =
                            "this entity exists in the model and THIS file describes it differently. It is " +
                            "UPDATED IN PLACE - the element keeps its id, its tags and its dimensions - for " +
                            "every field this build can edit and read back. It is never recreated: that " +
                            "would lose everything attached to it since the import. The row this file " +
                            "would build is above, and the apply half reports each field as updated, " +
                            "unchanged, unsupported or refused."
                    });

                    // The element the update acts on, and the fingerprint that becomes its
                    // new baseline once the update verifies.
                    if (candidate.Previous.ElementId >= 0)
                        Updates.Add(new JObject
                        {
                            ["global_id"] = candidate.GlobalId,
                            ["element_id"] = candidate.Previous.ElementId,
                            ["ifc_class"] = candidate.Entity.Type,
                            ["name"] = candidate.Name,
                            ["previous_row"] = candidate.Previous.RowFingerprint,
                            ["row_fingerprint"] = candidate.RowFingerprint,
                            ["row"] = row.DeepClone()
                        });
                    return;
                }

                List<PlannedRow> rows;
                if (!_byStage.TryGetValue(stage, out rows)) _byStage[stage] = rows = new List<PlannedRow>();
                rows.Add(new PlannedRow
                {
                    Row = row,
                    GlobalId = candidate.GlobalId,
                    IfcClass = candidate.Entity.Type,
                    Name = candidate.Name,
                    Representation = representation,
                    HostGlobalId = hostGlobalId,
                    RowFingerprint = candidate.RowFingerprint
                });

                Count(_plannedByClass, candidate.Entity.Type);
                if (string.Equals(stage, "walls", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(candidate.GlobalId))
                    PlannedWallByEntity[candidate.Entity.Id] = candidate.GlobalId;

                Rows.Add(new JObject
                {
                    ["stage"] = stage,
                    ["kind"] = (string)row["kind"],
                    ["ifc_class"] = candidate.Entity.Type,
                    ["ifc_entity"] = candidate.Entity.Id,
                    ["global_id"] = candidate.GlobalId,
                    ["name"] = candidate.Name,
                    ["representation_used"] = representation,
                    ["hosted_on_global_id"] = hostGlobalId,
                    ["notes"] = candidate.Notes
                });
            }

            public List<PlannedRow> RowsOfStage(string stage)
            {
                List<PlannedRow> rows;
                return _byStage.TryGetValue(stage, out rows) ? rows : new List<PlannedRow>();
            }

            public Level LevelFor(IfcEntity entity, Candidate candidate)
            {
                IfcEntity storey;
                Level level = null;
                bool contained = Relations.StoreyOf.TryGetValue(entity.Id, out storey);
                if (contained && storey != null) LevelOfStorey.TryGetValue(storey.Id, out level);
                if (level == null) level = FallbackLevel;
                if (level == null)
                {
                    c_SkipNoLevel(candidate, contained ? IfcStepReader.Text(storey.At(2)) : null);
                    return null;
                }
                RememberResolved("level", level, "level '" + SafeName(level) + "'");
                return level;
            }

            private void c_SkipNoLevel(Candidate candidate, string storeyName)
            {
                Skip(candidate, "this element sits on IFC storey '" + (storeyName ?? "(none declared)") +
                                "', which maps to no Revit level, and no level_id fallback was given. Name a " +
                                "level for it in level_mapping, or pass level_id.");
            }

            /// <summary>The Revit type for this entity: by IFC type name first, by class second.</summary>
            public ElementType TypeFor(IfcEntity entity, Candidate candidate, Type revitClass, string what)
            {
                string wanted = WantedTypeName(entity, candidate);
                if (wanted == null) return null;

                ElementType found = new FilteredElementCollector(Doc).OfClass(revitClass).Cast<ElementType>()
                    .FirstOrDefault(t => string.Equals(SafeName(t), wanted, StringComparison.OrdinalIgnoreCase));
                if (found == null)
                {
                    Skip(candidate, "type_mapping asks for the " + what + " named '" + wanted + "', and no such " +
                                    "type is loaded in this document. Load it, or map this class to one that " +
                                    "exists. Creating it would be inventing this project's standards.");
                    return null;
                }
                RememberResolved(what, found, what + " '" + SafeName(found) + "'");
                return found;
            }

            public FamilySymbol SymbolFor(IfcEntity entity, Candidate candidate,
                                          BuiltInCategory category, string what)
            {
                string wanted = WantedTypeName(entity, candidate);
                if (wanted == null) return null;

                FamilySymbol found = new FilteredElementCollector(Doc).OfClass(typeof(FamilySymbol))
                    .OfCategory(category).Cast<FamilySymbol>()
                    .FirstOrDefault(s => string.Equals(SafeName(s), wanted, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(FullName(s), wanted, StringComparison.OrdinalIgnoreCase));
                if (found == null)
                {
                    Skip(candidate, "type_mapping asks for the " + what + " named '" + wanted + "', and no loaded " +
                                    "family type of that category answers to it, by type name or by " +
                                    "'Family: Type'. A family that is not loaded cannot be placed, and loading " +
                                    "one is a decision about this project's standards.");
                    return null;
                }
                RememberResolved(what, found, what + " '" + FullName(found) + "'");
                return found;
            }

            /// <summary>
            /// Which Revit type the caller mapped this entity to. Two keys are accepted and
            /// the more specific wins: "IFCWALL:Exterior - Brick" names the exporter's own
            /// type, "IFCWALL" names the class. Without the first, every wall in a building
            /// arrives as one Revit type, which is an import nobody can use.
            /// </summary>
            private string WantedTypeName(IfcEntity entity, Candidate candidate)
            {
                JObject mapping = Request["type_mapping"] as JObject;
                if (mapping == null)
                {
                    Skip(candidate, "type_mapping is required: it says which Revit type each IFC class or IFC " +
                                    "type becomes. That is a decision about this project's standards, which this " +
                                    "bridge does not carry and will not guess.");
                    return null;
                }

                string ifcTypeName;
                if (Relations.TypeNameOf.TryGetValue(entity.Id, out ifcTypeName) &&
                    !string.IsNullOrWhiteSpace(ifcTypeName))
                {
                    JToken specific = mapping[entity.Type + ":" + ifcTypeName];
                    if (specific != null && specific.Type == JTokenType.String) return (string)specific;
                }

                JToken byClass = mapping[entity.Type];
                if (byClass != null && byClass.Type == JTokenType.String) return (string)byClass;

                Skip(candidate, "type_mapping names no Revit type for " + entity.Type +
                                (string.IsNullOrWhiteSpace(ifcTypeName) ? "" : " (IFC type '" + ifcTypeName + "')") +
                                ". Add a key '" + entity.Type + "' for the whole class, or '" + entity.Type + ":" +
                                (ifcTypeName ?? "<type name>") + "' for this one.");
                return null;
            }

            /// <summary>
            /// Queue one instance parameter for this row.
            ///
            /// Collected on the CANDIDATE rather than written straight into the row, because
            /// `Parameters` builds the map at the end and would otherwise overwrite whatever a
            /// planner had already put there - a silent loss of exactly the values a planner
            /// bothered to measure.
            /// </summary>
            public void Parameter(Candidate candidate, string name, JToken value)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(name)) return;
                candidate.Parameters[name] = value;
            }

            /// <summary>Provenance, material, and whatever a planner queued, as instance parameters.</summary>
            public void Parameters(JObject row, IfcEntity entity, Candidate candidate)
            {
                var parameters = new JObject();
                foreach (JProperty queued in candidate.Parameters.Properties())
                    parameters[queued.Name] = queued.Value;

                string globalIdParameter = Request.Value<string>("global_id_parameter");
                if (!string.IsNullOrWhiteSpace(globalIdParameter) && !string.IsNullOrWhiteSpace(candidate.GlobalId))
                    parameters[globalIdParameter] = candidate.GlobalId;

                string material;
                if (Relations.MaterialOf.TryGetValue(entity.Id, out material) && !string.IsNullOrWhiteSpace(material))
                {
                    Note(candidate, "IFC material: " + material + ". Revit carries material on the TYPE, so this " +
                                    "is RECORDED rather than applied; applying it would mean creating types, " +
                                    "which is inventing a standard.");
                    string materialParameter = Request.Value<string>("material_parameter");
                    if (!string.IsNullOrWhiteSpace(materialParameter))
                    {
                        JObject mapping = Request["material_mapping"] as JObject;
                        JToken mapped = mapping == null ? null : mapping[material];
                        parameters[materialParameter] =
                            mapped != null && mapped.Type == JTokenType.String ? (string)mapped : material;
                    }
                }

                if (parameters.Count > 0) row["parameters"] = parameters;
            }

            public void RememberResolved(string what, Element element, string label)
            {
                if (element == null || !_resolvedIds.Add(Rid.Value(element.Id))) return;
                ResolvedNames.Add(new JObject
                {
                    ["what"] = what,
                    ["id"] = Rid.Value(element.Id),
                    ["name"] = SafeName(element),
                    ["label"] = label
                });
            }

            private static void Count(Dictionary<string, int> counter, string key)
            {
                int current;
                counter[key] = counter.TryGetValue(key, out current) ? current + 1 : 1;
            }

            public JObject Summary()
            {
                var stages = new JObject();
                foreach (string stage in Stages) stages[stage] = RowsOfStage(stage).Count;
                return new JObject
                {
                    ["planned_rows"] = Rows.Count,
                    ["skipped_entities"] = Skipped.Count,
                    ["already_in_model_unchanged"] = Unchanged,
                    ["already_in_model_changed"] = Changed.Count,
                    ["stages"] = stages
                };
            }

            /// <summary>
            /// FOUR STATES, not two. The brief asks for them apart and they are apart:
            ///
            ///   reconstructed           built from its own representation, nothing inferred.
            ///   partially_interpreted   built, and something the IFC stated was NOT carried -
            ///                           a section taken from the Revit type instead of the
            ///                           file, a thickness the type decides. The element is
            ///                           there and it is not the same element.
            ///   omitted                 addressable in principle and skipped for a reason
            ///                           about THIS file: no axis, a tilted placement, a host
            ///                           that was itself skipped.
            ///   unsupported             outside the subset by class or by representation.
            ///
            /// Collapsing the middle two is how a column built at the right point with the
            /// wrong section gets called "reconstructed", and how somebody finds out in a
            /// coordination meeting.
            /// </summary>
            public JObject Reconstruction()
            {
                int partial = Rows.OfType<JObject>()
                    .Count(r => (r["notes"] as JArray ?? new JArray())
                        .Values<string>()
                        .Any(n => n != null && (n.Contains("may differ") || n.Contains("takes its"))));
                return new JObject
                {
                    ["reconstructed"] = Rows.Count - partial,
                    ["partially_interpreted"] = partial,
                    ["omitted"] = Skipped.Count,
                    ["unsupported_by_class_or_representation"] =
                        "listed per class in 'inventory' and per element in 'skipped'",
                    ["means"] =
                        "PARTIALLY INTERPRETED is the state that matters and the one a two-way split " +
                        "hides: the element is in the model, at the right place, and something the IFC " +
                        "stated was not carried - a steel section taken from the Revit type rather than " +
                        "from the file, a slab thickness the type decides. It is not the same element, " +
                        "and every such row says in its own notes what was dropped.",
                    ["no_generic_substitution"] =
                        "Nothing here replaces a required native class with generic geometry. Where the " +
                        "subset cannot rebuild something it is SKIPPED with the representation that " +
                        "caused it - a DirectShape standing in for a wall would import, look right in a " +
                        "view, and schedule as nothing."
                };
            }

            public JObject Coverage()
            {
                var byClass = new JArray();
                foreach (string key in _plannedByClass.Keys.Concat(_skippedByClass.Keys)
                                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                                      .OrderBy(k => k, StringComparer.Ordinal))
                {
                    int planned, skipped;
                    _plannedByClass.TryGetValue(key, out planned);
                    _skippedByClass.TryGetValue(key, out skipped);
                    byClass.Add(new JObject
                    {
                        ["ifc_class"] = key,
                        ["planned"] = planned,
                        ["skipped"] = skipped,
                        ["coverage"] = planned + skipped == 0
                            ? 0.0 : Math.Round(planned / (double)(planned + skipped), 4)
                    });
                }
                return new JObject
                {
                    ["by_class"] = byClass,
                    ["means"] = "coverage is planned ÷ (planned + skipped) for the classes this subset addresses. " +
                                "It is NOT how much of the building came across: a class the subset does not " +
                                "address at all never appears here, and the inventory is where that is said."
                };
            }
        }

        // =====================================================================
        // Small shared helpers
        // =====================================================================

        private static JArray Coordinate(double[] point, double z) =>
            new JArray(Math.Round(point[0], 3), Math.Round(point[1], 3), Math.Round(z, 3));

        private static double LevelElevationMm(Level level)
        {
            try { return level.Elevation * 304.8; } catch { return 0; }
        }

        private static string Fingerprint(string prefix, JToken token) =>
            prefix + ":" + RequestFingerprint.Sha256Hex(RequestFingerprint.Canonical(token)).Substring(0, 32);

        private static string SafeName(Element element)
        {
            try { return element == null ? null : element.Name; } catch { return null; }
        }

        private static string FullName(FamilySymbol symbol)
        {
            try { return SafeName(symbol.Family) + ": " + SafeName(symbol); } catch { return SafeName(symbol); }
        }

        private static string SafeTitle(Document doc)
        {
            try { return doc == null ? null : doc.Title; } catch { return null; }
        }

        private static string SafeVersion(UIApplication app)
        {
            try { return app == null || app.Application == null ? null : app.Application.VersionBuild; }
            catch { return null; }
        }

        // =====================================================================
        // The verdict table — closed, printed, and about REPRESENTATIONS
        // =====================================================================

        private static readonly Dictionary<string, ClassVerdict> Verdicts =
            new Dictionary<string, ClassVerdict>(StringComparer.OrdinalIgnoreCase)
            {
                ["IFCWALL"] = ClassVerdict.Planned("Walls",
                    "planned when it carries an 'Axis' representation as a 2-point polyline. One whose only " +
                    "representation is a BREP or a tessellation is skipped BY NAME, with the representation said."),
                ["IFCWALLSTANDARDCASE"] = ClassVerdict.Planned("Walls",
                    "this class exists to carry an Axis polyline plus a thickness, which is what a Revit wall is."),
                ["IFCWALLELEMENTEDCASE"] = ClassVerdict.Planned("Walls",
                    "planned on the same Axis rule; its element decomposition is NOT rebuilt."),
                ["IFCCOLUMN"] = ClassVerdict.Planned("Structural Columns",
                    "planned from its placement point and the caller's type, rotated by the placement's plan " +
                    "angle. A placement tilted out of plan is refused rather than stood upright."),
                ["IFCBEAM"] = ClassVerdict.Planned("Structural Framing",
                    "planned when it carries an 'Axis' 2-point polyline. The section comes from the Revit type; " +
                    "the IFC section is reported for comparison, never rebuilt."),
                ["IFCSLAB"] = ClassVerdict.Planned("Floors",
                    "planned when its Body is a vertical extrusion of a horizontal polygon, holes included. " +
                    "PredefinedType=ROOF is refused: a roof in the floor schedule is wrong for the project's life."),
                ["IFCROOF"] = ClassVerdict.Unsupported(
                    "a Revit roof carries a slope per footprint edge that an extruded IFC solid does not state. " +
                    "Building one as a flat floor would lose the roof."),
                ["IFCDOOR"] = ClassVerdict.Planned("Doors",
                    "planned when it fills an opening in a planned wall: a hosted instance at the opening's " +
                    "centre, on the caller's symbol. The family cuts its own hole."),
                ["IFCWINDOW"] = ClassVerdict.Planned("Windows", "hosted like a door, on the same rule."),
                ["IFCOPENINGELEMENT"] = ClassVerdict.Planned("Openings",
                    "planned as a wall opening when it is rectangular and voids a planned wall AND is not filled " +
                    "by a door or window — a hosted family cuts its own hole, and doing both cuts twice."),
                ["IFCSPACE"] = ClassVerdict.Unsupported(
                    "a Revit room is bounded by walls, not drawn: it cannot be created from an IFC space's " +
                    "geometry, only placed inside an enclosure that must already exist."),
                ["IFCSTAIR"] = ClassVerdict.Unsupported(
                    "a Revit stair is a parametric assembly, not a solid. Rebuilding one from IFC geometry " +
                    "produces something that looks like a stair and schedules as nothing."),
                ["IFCSTAIRFLIGHT"] = ClassVerdict.Unsupported("part of a stair; see IFCSTAIR."),
                ["IFCRAMP"] = ClassVerdict.Unsupported("parametric in Revit, solid in IFC; see IFCSTAIR."),
                ["IFCRAMPFLIGHT"] = ClassVerdict.Unsupported("part of a ramp; see IFCSTAIR."),
                ["IFCRAILING"] = ClassVerdict.Unsupported("same as a stair: parametric in Revit, solid in IFC."),
                ["IFCCOVERING"] = ClassVerdict.Unsupported(
                    "a covering maps to a ceiling, a floor finish or a wall finish depending on its " +
                    "PredefinedType and its host, and choosing between them is a modelling decision."),
                ["IFCCURTAINWALL"] = ClassVerdict.Unsupported(
                    "a Revit curtain wall is a grid, panels and mullions generated by its type. Rebuilding one " +
                    "from its solid produces a wall shaped like a curtain wall with no grid in it."),
                ["IFCPLATE"] = ClassVerdict.Unsupported("a curtain panel or a steel plate; no Revit category follows from it."),
                ["IFCFURNISHINGELEMENT"] = ClassVerdict.Unsupported(
                    "placeable in principle, but the family is a project decision and an IFC furnishing's " +
                    "placement rarely coincides with a Revit family's insertion point. Not in this subset."),
                ["IFCBUILDINGELEMENTPROXY"] = ClassVerdict.Unsupported(
                    "a proxy is IFC's way of saying 'something the exporter had no class for'. There is nothing " +
                    "to map it to, and guessing would be inventing a category."),
                ["IFCFLOWSEGMENT"] = ClassVerdict.Unsupported(
                    "an MEP segment needs its system, its size and its two ends; the first two are property sets " +
                    "this does not transfer, and a pipe with no system is not a pipe anyone can use."),
                ["IFCFLOWFITTING"] = ClassVerdict.Unsupported("see IFCFLOWSEGMENT."),
                ["IFCFLOWTERMINAL"] = ClassVerdict.Unsupported("see IFCFLOWSEGMENT."),
                ["IFCDISTRIBUTIONELEMENT"] = ClassVerdict.Unsupported("see IFCFLOWSEGMENT."),
                ["IFCMEMBER"] = ClassVerdict.Unsupported(
                    "a generic structural member maps to framing, a column or a brace depending on its " +
                    "PredefinedType, and choosing wrongly puts it in the wrong schedule."),
                ["IFCFOOTING"] = ClassVerdict.Unsupported(
                    "a footing is a foundation whose Revit form — isolated, wall or slab — is the engineer's " +
                    "decision, not the solid's."),
                ["IFCPILE"] = ClassVerdict.Unsupported("see IFCFOOTING."),
                ["IFCPROJECT"] = ClassVerdict.Context("the project header, not an element."),
                ["IFCSITE"] = ClassVerdict.Context("spatial structure, not an element."),
                ["IFCBUILDING"] = ClassVerdict.Context("spatial structure, not an element."),
                ["IFCBUILDINGSTOREY"] = ClassVerdict.Context(
                    "a storey is MAPPED to a Revit level rather than imported: a project's level scheme is a " +
                    "decision, not data. The 'levels' block reports every storey and the level it matched.")
            };

        private sealed class ClassVerdict
        {
            public string Kind;
            public string Category;
            public string Reason;

            public static ClassVerdict Planned(string category, string reason) =>
                new ClassVerdict { Kind = "planned_when_representation_allows", Category = category, Reason = reason };

            public static ClassVerdict Unsupported(string reason) =>
                new ClassVerdict { Kind = "not_reconstructible", Category = null, Reason = reason };

            public static ClassVerdict Context(string reason) =>
                new ClassVerdict { Kind = "context", Category = null, Reason = reason };

            public static ClassVerdict Unknown() =>
                new ClassVerdict
                {
                    Kind = "unknown_class",
                    Category = null,
                    Reason = "this class is not in the verdict table. It is reported rather than ignored: an " +
                             "importer that lists only what it recognised is one that drops whatever it did not."
                };
        }

        /// <summary>
        /// Is this class plausibly an ELEMENT rather than a geometry primitive? A file is
        /// mostly points, directions and placements, and a verdict for each of those would
        /// bury the answer under noise.
        /// </summary>
        private static bool IsElementClass(string type)
        {
            if (Verdicts.ContainsKey(type)) return true;
            foreach (string primitive in Primitives)
                if (type.StartsWith(primitive, StringComparison.OrdinalIgnoreCase)) return false;
            return type.StartsWith("IFC", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly string[] Primitives =
        {
            "IFCCARTESIANPOINT", "IFCDIRECTION", "IFCAXIS2PLACEMENT", "IFCLOCALPLACEMENT", "IFCGRIDPLACEMENT",
            "IFCPOLYLINE", "IFCPRODUCTDEFINITIONSHAPE", "IFCSHAPEREPRESENTATION", "IFCREPRESENTATION",
            "IFCEXTRUDEDAREASOLID", "IFCREVOLVEDAREASOLID", "IFCBOOLEAN", "IFCHALFSPACE", "IFCCSG",
            "IFCPROPERTY", "IFCRELDEFINES", "IFCRELASSOCIATES", "IFCRELAGGREGATES", "IFCRELCONTAINED",
            "IFCRELVOIDS", "IFCRELFILLS", "IFCRELCONNECTS", "IFCRELSPACEBOUNDARY", "IFCRELDECLARES",
            "IFCRELNESTS", "IFCRELPROJECTS", "IFCRELREFERENCED",
            "IFCOWNERHISTORY", "IFCPERSON", "IFCORGANIZATION", "IFCAPPLICATION", "IFCPOSTALADDRESS",
            "IFCSIUNIT", "IFCUNITASSIGNMENT", "IFCMEASUREWITHUNIT", "IFCCONVERSIONBASEDUNIT",
            "IFCDERIVEDUNIT", "IFCGEOMETRICREPRESENTATION", "IFCDIMENSIONALEXPONENTS", "IFCMONETARYUNIT",
            "IFCMATERIAL", "IFCSTYLED", "IFCPRESENTATION", "IFCCOLOUR", "IFCSURFACESTYLE", "IFCFILLAREASTYLE",
            "IFCARBITRARYCLOSEDPROFILE", "IFCARBITRARYPROFILE", "IFCARBITRARYOPENPROFILE",
            "IFCRECTANGLEPROFILE", "IFCCIRCLEPROFILE", "IFCISHAPEPROFILE", "IFCTSHAPEPROFILE",
            "IFCLSHAPEPROFILE", "IFCUSHAPEPROFILE", "IFCCSHAPEPROFILE", "IFCZSHAPEPROFILE",
            "IFCASYMMETRICISHAPEPROFILE", "IFCDERIVEDPROFILE", "IFCCOMPOSITEPROFILE", "IFCPROFILEDEF",
            "IFCFACE", "IFCPOLYLOOP", "IFCCLOSEDSHELL", "IFCOPENSHELL", "IFCFACETEDBREP", "IFCADVANCEDBREP",
            "IFCTRIANGULATEDFACESET", "IFCPOLYGONALFACESET", "IFCINDEXEDPOLY", "IFCCARTESIANPOINTLIST",
            "IFCCARTESIANTRANSFORM", "IFCMAPPEDITEM", "IFCREPRESENTATIONMAP", "IFCCOMPOSITECURVE",
            "IFCTRIMMEDCURVE", "IFCCIRCLE", "IFCELLIPSE", "IFCBSPLINE", "IFCLINE", "IFCVECTOR",
            "IFCQUANTITY", "IFCELEMENTQUANTITY", "IFCCLASSIFICATION", "IFCLIBRARY", "IFCTABLE",
            "IFCTYPE", "IFCWALLTYPE", "IFCSLABTYPE", "IFCCOLUMNTYPE", "IFCBEAMTYPE", "IFCDOORTYPE",
            "IFCWINDOWTYPE", "IFCDOORSTYLE", "IFCWINDOWSTYLE", "IFCPROJECTLIBRARY", "IFCCOVERINGTYPE",
            "IFCFURNITURETYPE", "IFCMEMBERTYPE", "IFCPLATETYPE", "IFCRAILINGTYPE", "IFCSTAIRTYPE",
            "IFCSTAIRFLIGHTTYPE", "IFCRAMPTYPE", "IFCROOFTYPE", "IFCCURTAINWALLTYPE"
        };
    }

    /// <summary>
    /// The bytes a plan was made from.
    ///
    /// Hashed here rather than inside the reader because the reader answers "what does
    /// this file say" and this answers "is it still that file" — a different question,
    /// asked at a different moment, by horizun_apply_ifc_plan.
    /// </summary>
    public static class IfcSourceIdentity
    {
        public static string Sha256(string path, out long bytes, out string problem)
        {
            bytes = 0;
            problem = null;
            try
            {
                bytes = new FileInfo(path).Length;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(stream);
                    var text = new System.Text.StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    return text.ToString();
                }
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return null;
            }
        }
    }
}
