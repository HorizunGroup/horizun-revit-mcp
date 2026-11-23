// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_export format=cobie: a COBie 2.4 workbook (.xlsx) of the
// active model - the ISO 19650 asset-information handover the product already models.
//
// Built like the other export formats (ExportSets.cs, gbXML): foreign arguments are
// refused by name; the dry run reads the model, builds every row, judges it and
// stamps a confirmation token; the apply needs that token, writes the workbook with
// the Core writer (no dependency), RE-READS IT FROM DISK and compares every sheet,
// row and cell with the plan before it says a file was produced.
//
// What this file does is READ the model into plain facts (Core/CobieRules.cs):
//   - levels marked Building Story -> Floor rows;
//   - rooms (or MEP spaces) of the named Revit phase, placed and enclosed -> Space;
//   - instances of the caller's categories whose status in that phase is New or
//     Existing (or undefined), not nested, not view-specific -> Component, their types
//     -> Type;
//   - MechanicalSystem, PipingSystem and ElectricalSystem members and base equipment
//     -> System rows for the components in scope.
// WHERE a component sits reuses the product's rules, never a new geometric method:
// a door or window takes Revit's own To/From room in the phase (as the sequence
// writer numbers doors) or, for MEP spaces, the space on each side of its host wall
// (as room_finishes decides what an opening faces); everything else, and an opening
// those give nothing for, goes through RoomMembershipReader (include_room,
// group_by='room'). Everything that turns facts into rows, and every finding, is
// Revit-free and unit-tested; linked models are not read.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ExportCommand
    {
        /// <summary>Every horizun_export argument that belongs to another format: refused by name, never ignored.</summary>
        private static readonly string[] NotForCobie =
        {
            "view_ids", "schedule_id", "image_pixels", "pdf_combine", "emit_manifest", "units", "delivery_id", "pdf_print", "preset",
            "file_naming", "dwg_xrefs", "family_ids", "category", "acad_version", "dwg_setup", "information_container",
            "ifc_version", "ifc_filter_view_id", "ifc_export_base_quantities", "ifc_split_walls_and_columns", "ifc_space_boundary_level",
            "nwc_scope", "nwc_coordinates", "nwc_parameters", "nwc_export_links", "nwc_export_element_ids", "nwc_export_room_geometry",
            "nwc_export_parts", "fbx_without_boundary_edges", "fbx_use_lod", "fbx_lod", "fbx_stop_on_error"
        };

        /// <summary>Above this many components the apply is a long run on Revit's UI thread.</summary>
        private const int CobieLongRunComponents = 2000;

        private CommandResult ExecuteCobie(UIApplication app, GateResult gate, Document doc, JObject request, string output)
        {
            CommandResult refused = RefuseFields(request, "format cobie", NotForCobie);
            if (refused != null) return refused;
            if (!(request["cobie"] is JObject))
                return CommandResult.Fail("format cobie needs the cobie object: created_by, facility.name, phase and component_categories at least " +
                                          "(docs/TOOLS-EXTENDED.md, horizun://contract/tools/horizun_export). Nothing was exported.");

            // ---- the caller's mapping, then the project context's defaults --------------------
            CobieMapping mapping = CobieMapping.Parse(request["cobie"], out List<string> problems);
            if (problems.Count > 0) return CommandResult.Fail("cobie refused: " + string.Join("; ", problems) + ". Nothing was exported.");
            JObject contextEvidence = null;
            string contextSha = null;
            if (mapping.ProjectContextPath != null)
            {
                JObject context = CobieMapping.ReadProjectContext(mapping.ProjectContextPath, out contextSha, out string contextError);
                if (context == null) return CommandResult.Fail("cobie.project_context_path refused: " + contextError + " Nothing was exported.");
                List<string> defaults = mapping.ApplyProjectContext(context);
                contextEvidence = new JObject
                {
                    ["path"] = Path.GetFullPath(mapping.ProjectContextPath), ["sha256"] = contextSha, ["applied"] = new JArray(defaults),
                    ["note"] = "an explicit argument always wins; project-context v1 has no contact e-mail, so created_by never comes from it."
                };
            }
            List<string> missing = mapping.Missing();
            if (missing.Count > 0) return CommandResult.Fail("cobie refused: " + string.Join("; ", missing) + ". Nothing was exported.");

            string folder = Path.GetDirectoryName(output);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return CommandResult.Fail("The output directory does not exist: " + folder + ". It is not created implicitly: create that folder first, or point output_path (the full path of the file to write) at a folder that exists. Nothing was written.");
            bool overwrite = request.Value<bool?>("overwrite") == true;
            if (!overwrite && File.Exists(output))
                return CommandResult.Fail("Output already exists and overwrite=false: " + output);

            // ---- the phase and the categories, resolved against THIS document ---------------
            RoomMembershipReader reader = RoomMembershipReader.Create(doc, mapping.Phase, out string phaseProblem);
            if (reader == null) return CommandResult.Fail("cobie.phase: " + phaseProblem + " Nothing was exported.");
            List<BuiltInCategory> categories = ResolveCobieCategories(doc, mapping.ComponentCategories, problems);
            if (problems.Count > 0) return CommandResult.Fail("cobie.component_categories refused: " + string.Join("; ", problems) + ". Nothing was exported.");

            // ---- the model, read into facts; the workbook built and judged ------------------
            CobieFacts facts;
            JObject scope;
            try { facts = ReadCobieFacts(app, doc, mapping, reader, categories, out scope); }
            catch (Exception ex)
            {
                return CommandResult.Fail("The model could not be read for COBie: " + ex.Message + ". Nothing was exported.");
            }
            DateTime createdOn = mapping.CreatedOnUtc ?? DateTime.UtcNow;
            CobieWorkbookPlan plan = CobieRules.Build(mapping, facts, createdOn);
            List<string> unwritable = XlsxWorkbookWriter.Validate(plan.Sheets);
            if (unwritable.Count > 0)
                return CommandResult.Fail("The workbook cannot be written as .xlsx: " + string.Join("; ", unwritable) + ". Nothing was exported.");

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            OperationGateResult gateDecision = OperationGate.Evaluate(app, doc, request["require_gate"], GatedOperation.Export, Name);
            if (gateDecision.Refusal != null) return gateDecision.Refusal;
            string planHash = DocumentGate.PlanHash(request, "format", "output_path", "overwrite", "cobie");
            ResolvedPlan resolvedPlan = NewPlan(app, gate, new Element[0], new[] { output }, overwrite);
            // Every cell but CreatedOn: a model that moves in a way the workbook would show - a
            // renamed room, a new door, a changed parameter - makes the apply a stale plan.
            resolvedPlan.ContextFingerprint += ";cobie_content=" + plan.ContentDigest() + ";project_context=" + (contextSha ?? "-");
            JObject summary = CobieSummary(mapping, plan, scope, contextEvidence);
            bool longRun = facts.Components.Count > CobieLongRunComponents;

            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["format"] = "cobie", ["output_path"] = output, ["planned_files"] = new JArray(output),
                    ["overwrite"] = overwrite, ["deliverable_ready_if_written"] = plan.Ready, ["blocking"] = plan.BlockingJson(),
                    ["cobie"] = summary, ["long_run"] = longRun,
                    ["note"] = "Nothing was exported and no file was created. The apply writes the workbook WITH its findings (a COBie " +
                               "with gaps is a deliverable under review) and deliverable_ready stays false while a blocking one remains." +
                               (longRun ? " " + facts.Components.Count + " components are read again on the apply on Revit's UI thread: " +
                                          "send it through horizun_submit_job and poll horizun_job_status." : "")
                };
                if (gateDecision.Requested) result["prevention"] = gateDecision.Prevention;
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, true,
                    "the token binds the destination, which files exist, the cobie mapping, the project context's content and every " +
                    "cell the workbook will hold except CreatedOn.");
                return CommandResult.Ok(result);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            // ---- write beside the target, then put it in place ------------------------------
            string temporary = Path.Combine(folder, "." + Path.GetFileNameWithoutExtension(output) + ".hz-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { XlsxWorkbookWriter.WriteFile(temporary, plan.Sheets); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                TryDelete(temporary);
                return CommandResult.FailWithDetail("The workbook could not be written: " + ex.Message + ". Nothing was exported.",
                    new JObject { ["external_files_may_exist"] = false, ["planned_files"] = new JArray(output) });
            }
            try
            {
                if (File.Exists(output))
                {
                    if (!overwrite) throw new IOException(output + " appeared after the rehearsal and overwrite=false.");
                    File.Replace(temporary, output, null, true);
                }
                else File.Move(temporary, output);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(temporary);
                return CommandResult.FailWithDetail("The workbook was written but could not be put at " + output + ": " + ex.Message +
                    ". The temporary file was removed; nothing was exported.",
                    new JObject { ["external_files_may_exist"] = File.Exists(output), ["planned_files"] = new JArray(output) });
            }

            // ---- re-read from disk: the file's own sheets, rows and cells --------------------
            byte[] bytes;
            List<XlsxSheet> readBack;
            try
            {
                bytes = File.ReadAllBytes(output);
                readBack = XlsxWorkbookReader.Read(bytes);
            }
            catch (Exception ex)
            {
                // Whatever stops the re-read - a lock, a package the reader rejects - is a failed
                // verification of a file that exists, never a crash and never a success.
                return CommandResult.FailWithDetail("The workbook was written but could not be re-read: " + ex.Message + ". Success is not claimed.",
                    new JObject { ["external_files_may_exist"] = true, ["rollback_available"] = false, ["planned_files"] = new JArray(output) });
            }
            XlsxComparison comparison = XlsxWorkbookReader.Compare(plan.Sheets, readBack);
            string sha;
            using (var hasher = SHA256.Create())
                sha = string.Concat(hasher.ComputeHash(bytes).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            var info = new FileInfo(output);
            var readBackJson = new JObject
            {
                ["sheets"] = new JArray(readBack.Select(s => new JObject
                {
                    ["name"] = s.Name, ["rows"] = Math.Max(0, s.Rows.Count - 1),
                    ["columns"] = s.Rows.Count > 0 ? s.Rows[0].Length : 0
                })),
                ["cells_sha256"] = comparison.ReadDigest, ["planned_cells_sha256"] = comparison.PlannedDigest,
                ["matches_plan"] = comparison.Matches,
                ["means"] = "the file was re-opened from disk: sheet names and order, every sheet's row count and every cell (kind and exact " +
                            "text; a number is the text of its <v>) equal the plan, hashed as cells_sha256. It does not prove how Excel or a " +
                            "COBie checker renders or judges it."
            };
            var files = new JArray(new JObject
            {
                ["path"] = output, ["bytes"] = bytes.LongLength, ["sha256"] = sha, ["last_write_utc"] = info.LastWriteTimeUtc.ToString("o")
            });
            if (!comparison.Matches)
            {
                readBackJson["differences"] = new JArray(comparison.Differences);
                return CommandResult.FailWithDetail("The workbook on disk does not hold what was planned: " + string.Join("; ", comparison.Differences) +
                    ". Success is not claimed.",
                    new JObject
                    {
                        ["files"] = files, ["read_back"] = readBackJson, ["files_verified"] = 0,
                        ["external_files_may_exist"] = true, ["rollback_available"] = false
                    });
            }
            var applied = new JObject
            {
                ["format"] = "cobie", ["requested_output_path"] = output, ["files_verified"] = 1, ["files"] = files, ["read_back"] = readBackJson,
                ["deliverable_ready"] = plan.Ready, ["blocking"] = plan.BlockingJson(), ["cobie"] = summary,
                ["verdict_basis"] = "files_verified is the FILE's: re-read and equal to the plan cell for cell. deliverable_ready is the " +
                    "WORKBOOK's: true only when no blocking finding (required_field, duplicate_name, broken_reference) remains. " +
                    (plan.Ready ? "" : "The workbook is written and is NOT ready; cobie.findings names every gap.")
            };
            if (gateDecision.Requested) applied["prevention"] = gateDecision.Prevention;
            return CommandResult.Ok(applied);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static JObject CobieSummary(CobieMapping m, CobieWorkbookPlan plan, JObject scope, JObject contextEvidence) => new JObject
        {
            ["created_by"] = m.CreatedBy,
            ["created_on"] = plan.CreatedOn,
            ["created_on_source"] = m.CreatedOnUtc.HasValue ? "argument" : "export_time_utc",
            ["phase"] = m.Phase,
            ["space_source"] = m.SpaceSource,
            ["facility"] = m.Facility.Name,
            ["units"] = plan.Units.ToJson(),
            ["provenance"] = m.ProvenanceJson(),
            ["project_context"] = contextEvidence,
            ["sheets"] = plan.SheetsJson(),
            ["scope"] = scope,
            ["systems"] = new JObject
            {
                ["read"] = plan.SystemsRead, ["written"] = plan.SystemsWritten, ["without_components_in_scope"] = plan.SystemsWithoutScope,
                ["without_components_sample"] = new JArray(plan.SystemsWithoutScopeSample)
            },
            ["component_names"] = m.ComponentNameParameter == null
                ? "every Component is named <Type name>-<element id>: no component_name_parameter was given"
                : plan.NameFallbacks + " component(s) fell back to <Type name>-<element id> (each is a name_fallback finding)",
            ["findings"] = plan.FindingsJson(m.MaxFindings),
            ["content_sha256"] = plan.ContentDigest(),
            ["rules"] = "Floor: levels marked Building Story. Space: " + m.SpaceSource + " of the phase, placed, area > 0; Name = Number, " +
                        "Description = Name, RoomTag = Number, NetArea = Revit's area, GrossArea empty. Zone: one row per zone per space " +
                        "(key Name + Category + SpaceNames). Type: the types of the components in scope, 'Family: Type'. Component: " +
                        "New/Existing (or unphased) instances, not nested, not view-specific. System: one row per system per component " +
                        "in scope. Linked models are not read."
        };

        private static List<BuiltInCategory> ResolveCobieCategories(Document doc, IList<string> tokens, List<string> problems)
        {
            var result = new List<BuiltInCategory>();
            string[] names = Enum.GetNames(typeof(BuiltInCategory));
            foreach (string token in tokens)
            {
                // One exact defined name: Enum.TryParse would OR a comma list into another category
                // and accept numbers that name none (see PlanMepSystemAnalysis.cs).
                string exact = AnalysisReadRules.ExactName(token, names);
                if (exact == null) { problems.Add("'" + token + "' is not a BuiltInCategory"); continue; }
                var bic = (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), exact);
                Category category = null;
                try { category = Category.GetCategory(doc, bic); } catch { }
                if (category == null) { problems.Add("'" + exact + "' is not a category of this document"); continue; }
                if (category.CategoryType != CategoryType.Model) { problems.Add("'" + exact + "' is not a model category; COBie components are model elements"); continue; }
                if (!result.Contains(bic)) result.Add(bic);
            }
            return result;
        }

        // ---- reading the model -------------------------------------------------------------

        private static CobieFacts ReadCobieFacts(UIApplication app, Document doc, CobieMapping m, RoomMembershipReader reader,
                                                 List<BuiltInCategory> categories, out JObject scope)
        {
            Phase phase = reader.Phase;
            bool useSpaces = m.UsesSpaces;
            var f = new CobieFacts { AuthoringSystem = "Autodesk Revit " + app.Application.VersionNumber };
            Units units = doc.GetUnits();
            f.LengthUnitTypeId = UnitTypeIdOf(units, SpecTypeId.Length);
            f.AreaUnitTypeId = UnitTypeIdOf(units, SpecTypeId.Area);
            f.VolumeUnitTypeId = UnitTypeIdOf(units, SpecTypeId.Volume);
            try
            {
                f.AreaBoundary = AreaVolumeSettings.GetAreaVolumeSettings(doc)
                    .GetSpatialElementBoundaryLocation(useSpaces ? SpatialElementType.Space : SpatialElementType.Room).ToString();
            }
            catch { f.AreaBoundary = null; }

            // ---- levels ----
            int levels = 0, stories = 0;
            foreach (Level level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                levels++;
                var fact = new CobieLevelFact { Id = Rid.Value(level.Id), UniqueId = SafePlanUid(level), Name = SafePlanName(level) };
                try { fact.ElevationFeet = level.Elevation; } catch { fact.ElevationFeet = null; }
                try
                {
                    Parameter story = level.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);
                    fact.IsBuildingStory = story != null && story.AsInteger() != 0;
                }
                catch { fact.IsBuildingStory = false; }
                if (fact.IsBuildingStory) stories++;
                fact.Category = ReadCobieParameter(level, TypeOf(doc, level), m.CategoryParameter);
                f.Levels.Add(fact);
            }

            // ---- rooms or spaces of the phase ----
            int otherPhase = 0, phaseUnreadable = 0, unplaced = 0, notEnclosed = 0, areaUnreadable = 0;
            BuiltInCategory spatialCategory = useSpaces ? BuiltInCategory.OST_MEPSpaces : BuiltInCategory.OST_Rooms;
            foreach (SpatialElement s in new FilteredElementCollector(doc).OfCategory(spatialCategory).WhereElementIsNotElementType().OfType<SpatialElement>())
            {
                // The room's own phase, strictly: one whose phase cannot be read is counted as such,
                // never assumed to be in the phase asked for.
                ElementId own = null;
                try { own = s.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId(); } catch { own = null; }
                if (own == null) { phaseUnreadable++; continue; }
                if (own != phase.Id) { otherPhase++; continue; }
                if (s.Location == null) { unplaced++; continue; }
                double area;
                try { area = s.Area; } catch { areaUnreadable++; continue; }
                if (!(area > 1e-9)) { notEnclosed++; continue; }
                var fact = new CobieSpaceFact
                {
                    Id = Rid.Value(s.Id), UniqueId = SafePlanUid(s), RevitClass = s.GetType().Name, Number = SpatialNumber(s),
                    Name = SpatialName(s), AreaSquareFeet = area
                };
                try { fact.LevelName = s.Level?.Name; } catch { }
                try
                {
                    if (s is Autodesk.Revit.DB.Architecture.Room room) fact.UnboundedHeightFeet = room.UnboundedHeight;
                    else if (s is Autodesk.Revit.DB.Mechanical.Space mepSpace) fact.UnboundedHeightFeet = mepSpace.UnboundedHeight;
                }
                catch { fact.UnboundedHeightFeet = null; }
                fact.Category = ReadCobieParameter(s, null, m.CategoryParameter);
                fact.Zone = ReadCobieParameter(s, null, m.ZoneParameter);
                f.Spaces.Add(fact);
            }

            // ---- component instances ----
            var tokenOf = new Dictionary<long, string>();
            foreach (BuiltInCategory bic in categories)
            {
                try { tokenOf[Rid.Value(Category.GetCategory(doc, bic).Id)] = bic.ToString(); } catch { }
            }
            long doorCategory = Rid.Value(new ElementId(BuiltInCategory.OST_Doors));
            long windowCategory = Rid.Value(new ElementId(BuiltInCategory.OST_Windows));
            int nested = 0, viewSpecific = 0, untyped = 0;
            var phaseExcluded = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var byCategory = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var byBasis = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int withoutSpace = 0;
            var typeIds = new HashSet<long>();
            foreach (Element e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories)).WhereElementIsNotElementType())
            {
                var instance = e as FamilyInstance;
                Element super = null;
                try { super = instance?.SuperComponent; } catch { }
                if (super != null) { nested++; continue; }
                bool isViewSpecific = false;
                try { isViewSpecific = e.ViewSpecific; } catch { }
                if (isViewSpecific) { viewSpecific++; continue; }
                ElementId typeId = null;
                try { typeId = e.GetTypeId(); } catch { }
                if (typeId == null || typeId == ElementId.InvalidElementId) { untyped++; continue; }
                ElementOnPhaseStatus status;
                try { status = e.GetPhaseStatus(phase.Id); }
                catch { Count(phaseExcluded, "unreadable"); continue; }
                if (status != ElementOnPhaseStatus.New && status != ElementOnPhaseStatus.Existing && status != ElementOnPhaseStatus.None)
                { Count(phaseExcluded, status.ToString()); continue; }

                long categoryId = -1;
                try { categoryId = Rid.Value(e.Category?.Id); } catch { }
                string token = tokenOf.TryGetValue(categoryId, out string t) ? t : "(category " + categoryId + ")";
                Count(byCategory, token);
                Element type = doc.GetElement(typeId);
                var fact = new CobieComponentFact
                {
                    Id = Rid.Value(e.Id), TypeId = Rid.Value(typeId), UniqueId = SafePlanUid(e), RevitClass = e.GetType().Name, Category = token,
                    IsOpening = categoryId == doorCategory || categoryId == windowCategory,
                    NameValue = ReadCobieParameter(e, type, m.ComponentNameParameter)
                };
                foreach (KeyValuePair<string, string> map in m.ComponentFields)
                    fact.Fields[map.Key] = ReadCobieParameter(e, type, map.Value);
                LocateCobieComponent(doc, e, instance, fact, reader, useSpaces);
                Count(byBasis, fact.SpaceBasis ?? "none");
                if (fact.Spaces.Count == 0) withoutSpace++;
                typeIds.Add(fact.TypeId);
                f.Components.Add(fact);
            }

            // ---- their types ----
            foreach (long typeId in typeIds)
            {
                Element type = doc.GetElement(Rid.Make(typeId));
                if (type == null) continue;
                var fact = new CobieTypeFact
                {
                    Id = typeId, UniqueId = SafePlanUid(type), RevitClass = type.GetType().Name, TypeName = SafePlanName(type),
                    Category = ReadCobieParameter(type, null, m.CategoryParameter), Description = Builtin(type, BuiltInParameter.ALL_MODEL_DESCRIPTION)
                };
                try { fact.FamilyName = (type as ElementType)?.FamilyName; } catch { }
                foreach (KeyValuePair<string, string> map in m.TypeFields)
                    fact.Fields[map.Key] = ReadCobieParameter(type, null, map.Value);
                f.Types.Add(fact);
            }

            // ---- MEP systems ----
            var unreadableSystems = new JArray();
            var systems = new List<MEPSystem>();
            systems.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Mechanical.MechanicalSystem)).Cast<MEPSystem>());
            systems.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipingSystem)).Cast<MEPSystem>());
            systems.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Electrical.ElectricalSystem)).Cast<MEPSystem>());
            foreach (MEPSystem system in systems.GroupBy(s => Rid.Value(s.Id)).Select(g => g.First()))
            {
                var fact = new CobieSystemFact
                {
                    Id = Rid.Value(system.Id), UniqueId = SafePlanUid(system), RevitClass = system.GetType().Name, Name = SafePlanName(system),
                    Category = ReadCobieParameter(system, TypeOf(doc, system), m.CategoryParameter)
                };
                try
                {
                    // MEPSystem.Elements holds the terminals and "doesn't include the base equipment
                    // or panel" (RevitAPI.xml), so the base equipment is added on its own.
                    foreach (Element member in system.Elements) fact.MemberIds.Add(Rid.Value(member.Id));
                    Element equipment = system.BaseEquipment;
                    if (equipment != null) fact.MemberIds.Add(Rid.Value(equipment.Id));
                }
                catch (Exception ex)
                {
                    unreadableSystems.Add(new JObject { ["id"] = fact.Id, ["name"] = fact.Name, ["reason"] = ex.Message });
                    continue;
                }
                f.Systems.Add(fact);
            }

            scope = new JObject
            {
                ["phase"] = phase.Name,
                ["levels"] = new JObject { ["read"] = levels, ["building_stories"] = stories },
                ["spaces"] = new JObject
                {
                    ["source"] = m.SpaceSource, ["in_scope"] = f.Spaces.Count, ["other_phase"] = otherPhase, ["phase_unreadable"] = phaseUnreadable,
                    ["unplaced"] = unplaced, ["not_enclosed"] = notEnclosed, ["area_unreadable"] = areaUnreadable, ["area_boundary"] = f.AreaBoundary
                },
                ["components"] = new JObject
                {
                    ["in_scope"] = f.Components.Count, ["by_category"] = JObject.FromObject(byCategory),
                    ["excluded"] = new JObject
                    {
                        ["nested"] = nested, ["view_specific"] = viewSpecific, ["untyped"] = untyped,
                        ["phase_status"] = JObject.FromObject(phaseExcluded)
                    },
                    ["space_basis"] = JObject.FromObject(byBasis), ["without_space"] = withoutSpace
                },
                ["types"] = f.Types.Count,
                ["systems_unreadable"] = unreadableSystems,
                ["links"] = "linked models are not read: every row comes from the active document"
            };
            return f;
        }

        private static void Count(SortedDictionary<string, int> counts, string key) =>
            counts[key] = (counts.TryGetValue(key, out int n) ? n : 0) + 1;

        private static string UnitTypeIdOf(Units units, ForgeTypeId spec)
        {
            try { return units.GetFormatOptions(spec).GetUnitTypeId()?.TypeId; } catch { return null; }
        }

        private static Element TypeOf(Document doc, Element e)
        {
            try
            {
                ElementId id = e.GetTypeId();
                return id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id);
            }
            catch { return null; }
        }

        private static string SpatialNumber(SpatialElement s)
        {
            try { return s.Number; } catch { return null; }
        }

        /// <summary>The room's or space's own Name parameter (Element.Name is not read: for a room it is not the bare name).</summary>
        private static string SpatialName(SpatialElement s)
        {
            try { return s.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(); } catch { return null; }
        }

        /// <summary>
        /// A mapped parameter: on the element, else - when the element has no parameter by that
        /// name - on its type. Text as Revit shows it: AsString for text, AsValueString (the
        /// document's units and formatting) for everything else. Null: not mapped.
        /// </summary>
        private static CobieValue ReadCobieParameter(Element e, Element type, string name)
        {
            if (string.IsNullOrEmpty(name) || e == null) return null;
            Parameter p = null;
            try { p = e.LookupParameter(name); } catch { }
            if (p == null && type != null) { try { p = type.LookupParameter(name); } catch { } }
            if (p == null) return CobieValue.Missing();
            return ParameterValue(p);
        }

        private static CobieValue Builtin(Element e, BuiltInParameter bip)
        {
            Parameter p = null;
            try { p = e.get_Parameter(bip); } catch { }
            return p == null ? CobieValue.Missing() : ParameterValue(p);
        }

        private static CobieValue ParameterValue(Parameter p)
        {
            try
            {
                if (!p.HasValue) return CobieValue.Of(null);
                return CobieValue.Of(p.StorageType == StorageType.String ? p.AsString() : p.AsValueString());
            }
            catch (Exception ex) { return CobieValue.Unreadable(ex.Message); }
        }

        /// <summary>
        /// Where a component sits, by the product's existing rules (see the file header). The
        /// basis is recorded per component; an empty result says why.
        /// </summary>
        private static void LocateCobieComponent(Document doc, Element e, FamilyInstance instance, CobieComponentFact fact,
                                                 RoomMembershipReader reader, bool useSpaces)
        {
            Phase phase = reader.Phase;
            string word = useSpaces ? "space" : "room";
            if (fact.IsOpening && instance != null)
            {
                var sides = new List<SpatialElement>();
                if (!useSpaces)
                {
                    // Revit's own assignment for openings, the one its door schedules show.
                    try { AddSpatial(sides, instance.get_ToRoom(phase)); } catch { }
                    try { AddSpatial(sides, instance.get_FromRoom(phase)); } catch { }
                    if (sides.Count > 0) { fact.SpaceBasis = "to_from_room"; AddRefs(fact, sides); return; }
                }
                else
                {
                    // A space has no From/To: the space on each side of the host wall, half the wall
                    // plus 0.5 ft from the insert, at its box's mid-height (QuantitiesRoomFinishes).
                    try
                    {
                        var wall = instance.Host as Wall;
                        var lp = instance.Location as LocationPoint;
                        if (wall != null && lp != null)
                        {
                            BoundingBoxXYZ box = instance.get_BoundingBox(null);
                            double z = box != null ? (box.Min.Z + box.Max.Z) / 2 : lp.Point.Z + 1;
                            XYZ normal = wall.Orientation;
                            double offset = wall.Width / 2 + 0.5;
                            var centre = new XYZ(lp.Point.X, lp.Point.Y, z);
                            AddSpatial(sides, doc.GetSpaceAtPoint(centre + normal * offset, phase));
                            AddSpatial(sides, doc.GetSpaceAtPoint(centre - normal * offset, phase));
                        }
                    }
                    catch { }
                    if (sides.Count > 0) { fact.SpaceBasis = "host_wall_sides"; AddRefs(fact, sides); return; }
                }
            }
            RoomHit hit = reader.Locate(e, null);
            fact.SpaceBasis = hit.Basis ?? "none";
            if (hit.State == "unlocatable") { fact.SpaceProblem = "it could not be located: " + hit.Reason; return; }
            if (useSpaces && hit.Spaces.Count == 0 && hit.SpaceProblem != null) { fact.SpaceProblem = hit.SpaceProblem; return; }
            AddRefs(fact, useSpaces ? hit.Spaces : hit.Rooms);
            if (fact.Spaces.Count == 0)
                fact.SpaceProblem = "it is in no " + word + " of phase '" + phase.Name + "' at its sample points (basis " + fact.SpaceBasis + ")" +
                    (fact.IsOpening ? (useSpaces ? ", and no space was found beside its host wall" : ", and Revit's To/From room is empty in this phase") : "");
        }

        private static void AddSpatial(List<SpatialElement> list, SpatialElement s)
        {
            if (s != null && !list.Any(x => x.Id == s.Id)) list.Add(s);
        }

        private static void AddRefs(CobieComponentFact fact, IEnumerable<SpatialElement> spatial)
        {
            foreach (SpatialElement s in spatial)
                if (!fact.Spaces.Any(x => x.Id == Rid.Value(s.Id)))
                    fact.Spaces.Add(new CobieSpaceRef { Id = Rid.Value(s.Id), Number = SpatialNumber(s) });
        }
    }
}
