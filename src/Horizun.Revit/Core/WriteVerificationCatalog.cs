// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// HOW EVERY WRITING TOOL PROVES WHAT IT WROTE - declared in one table.
//
// The contract of this bridge is that no command reports work it did not verify.
// That is a property of each command's own code, and the 2026-09-24 inventory found
// it held unevenly: some commands used PostconditionCheck (an empty checklist never
// passes, an unreadable property is unmeasured, coverage is exact), others built the
// same verdict from ad hoc booleans that could say true over nothing - a colour
// legend with no rows, a recipe whose every element failed, a pin state that could
// not be read reported as "not pinned" to an unpin.
//
// This table is the declaration that closes the door on the NEXT one. Every tool
// whose contract can change something (the model, the document session, a file, a
// remote dataset) has a row naming:
//
//   * its MECHANISM - how the reply's verdict is produced;
//   * its EVIDENCE - the reply field that carries that verdict;
//   * its SOURCES - where the mechanism lives, so a test can check the claim;
//   * its KNOWN GAPS - what the inventory found and this change did not fix, by
//     file and line, so a reviewer reads the residual risk instead of assuming none.
//
// WriteVerificationCatalogTests (Core.Tests) fails when a writing tool has no row,
// when a row names a tool that no longer writes, when a PostconditionChecklist row's
// sources never construct a PostconditionCheck, and when a command file that opens a
// mutation gate (DocumentGate.ForMutation) belongs to a tool the contract classifies
// as read-only. A new writer therefore cannot ship without saying how it verifies.
//
// Revit-free and data only: nothing at runtime reads it yet. It is compiled into the
// add-in so that the declaration ships beside the code it describes.
// -----------------------------------------------------------------------------
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public enum VerificationMechanism
    {
        /// <summary>Core/PostconditionCheck: exact coverage, empty never passes, unreadable is unmeasured.</summary>
        PostconditionChecklist,

        /// <summary>Each requested row re-read from the committed model and compared field by field.</summary>
        PerRowReread,

        /// <summary>Intended counts compared with counts re-read from the model (Guard.Verify / RecipeVerdict).</summary>
        CountReconciliation,

        /// <summary>A file re-read from disk: existence, size, hash, header, pages, sidecar.</summary>
        FileArtifactReread,

        /// <summary>Document or session state re-read: active document, save evidence, ownership census.</summary>
        SessionStateReread,

        /// <summary>Composes typed children and reads their declared outcome (ApplicationOutcome).</summary>
        DelegatedChildDeclaration,

        /// <summary>A remote service's acknowledgement; the remote data cannot be re-read from here.</summary>
        RemoteAcknowledgement,

        /// <summary>Records a job and executes nothing in the call; the job's own result carries the verdict.</summary>
        QueuedNotExecuted,

        /// <summary>The script's own testimony (execute_python): never the bridge's finding.</summary>
        SelfReported,

        /// <summary>A remote record re-read over the provider's API after the write and compared field by field.</summary>
        RemoteReread
    }

    public sealed class WriteVerification
    {
        public string Tool;
        public VerificationMechanism Mechanism;
        /// <summary>Reply fields that carry the verdict ("application" = the ApplicationOutcome block).</summary>
        public string[] Evidence;
        /// <summary>Source files, relative to src/ (e.g. "Horizun.Revit/Commands/DeleteCommand.cs").</summary>
        public string[] Sources;
        /// <summary>Residual gaps found by the inventory and not fixed, each with file and approximate line.</summary>
        public string[] KnownGaps = new string[0];
    }

    public static class WriteVerificationCatalog
    {
        private const string C = "Horizun.Revit/Commands/";
        private const string S = "Horizun.Server/";

        private static WriteVerification Row(string tool, VerificationMechanism mechanism, string[] evidence,
                                             string[] sources, params string[] gaps)
            => new WriteVerification { Tool = tool, Mechanism = mechanism, Evidence = evidence, Sources = sources, KnownGaps = gaps ?? new string[0] };

        private static string[] E(params string[] fields) => fields;
        private static string[] F(params string[] files) => files;

        private static readonly string[] Recipe = { C + "RecipeCommand.cs", C + "RecipeTools.cs", "Horizun.Revit/Core/RecipeVerdict.cs" };

        public static readonly IReadOnlyList<WriteVerification> Rows = new List<WriteVerification>
        {
            // ---- document session ---------------------------------------------------------
            Row("horizun_open_document", VerificationMechanism.SessionStateReread, E("confirmed_active"), F(C + "OpenDocumentCommand.cs")),
            Row("horizun_save_document", VerificationMechanism.SessionStateReread, E("outcome"), F(C + "SaveDocumentCommand.cs")),
            Row("horizun_relinquish_all", VerificationMechanism.SessionStateReread, E("fully_relinquished"), F(C + "RelinquishAllCommand.cs"),
                "RelinquishAllCommand.cs ~l.156: a workset whose owner reads null is skipped rather than counted as unmeasured."),
            Row("horizun_document_session", VerificationMechanism.SessionStateReread, E("active_document_verified", "sync_verified"), F(C + "DocumentSessionCommand.cs", C + "DocumentSessionSync.cs"),
                "Core/WorksetConfigurationEvidence.cs ~l.59: open_all_worksets counts as applied when zero user worksets are observed, which is right for a non-workshared model and unmeasured for a workshared one whose collector returned nothing. " +
                "DocumentSessionSync.cs ~l.308: sync_verified re-reads GetModelUpdatesStatus (a local cache) on a deterministic sample (<=150 spread + 50 borrowed/newest owned), not every element; ownership is exhaustive and HasAllChangesFromCentral() is document-level."),

            // ---- typed model writes: checklists -------------------------------------------
            Row("horizun_create_schedule", VerificationMechanism.PostconditionChecklist, E("postcondition", "application"), F(C + "CreateScheduleCommand.cs")),
            Row("horizun_create_elements", VerificationMechanism.PostconditionChecklist, E("postconditions", "production_postconditions", "application"),
                F(C + "CreateElementsCommand.cs", C + "CreateElementsGeometry.cs", C + "CreateElementsProductionVerification.cs", C + "CreateStairsGeometry.cs",
                  C + "CreateElementsEnclosed.cs", C + "CreateElementsToposolid.cs"),
                "CreateElementsEnclosed.cs: placement=all_enclosed re-reads area_positive, boundary_closed (every loop closes on itself), phase_id and, for rooms, point_inside_room; whether Room Bounding walls of a LINKED model close a host circuit is not established - the reply declares link_bounding not_proven.",
                "CreateElementsEnclosed.cs: a space's circuit counts as filled by GetSpaceAtPoint half a foot above the level, not by PlanCircuit (the API has no IsSpaceLocated).",
                "CreateElementsToposolid.cs: the top is re-read at up to 50 sampled input points (all when fewer; extremes always), not at every point; whether Toposolid.Create reads Z as absolute or level-relative is unmeasured - absolute is asserted and the other reading fails the row; a landxml_path surface is re-read at its CONVERTED points, so the shared->internal step (the tabular_source formula) is not itself proven by the re-read."),
            Row("horizun_fix_planimetry", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"), F(C + "FixPlanimetryCommand.cs", C + "FixPlanimetryDisplay.cs")),
            Row("horizun_transform_elements", VerificationMechanism.PostconditionChecklist, E("operations_verified", "postconditions", "application"), F(C + "TransformElementsCommand.cs"),
                "TransformElementsCommand.cs Verify: move/rotate/mirror/pin/change_type/set_curve/wall_join compare per element with booleans (guarded: an element that does not re-read fails, an empty target list never passes); only the tag and array operations carry a PostconditionCheck.",
                "TransformElementsCommand.cs VerifyArray: a radial copy's position is checked, not whether its axes turned.",
                "TransformElementsCommand.EditSketch.cs: edit_sketch re-reads the loops, the UniqueId and every hosted instance/opening/tag that depended on the element (rolled back if one is lost); the Area parameter is held to the new sketch only when it matched the old one, else named not_applicable."),
            Row("horizun_manage_curtain", VerificationMechanism.PostconditionChecklist, E("postconditions", "evidence", "application"), F(C + "ManageCurtainCommand.cs", C + "ModelEditRunner.cs"),
                "ManageCurtainCommand.cs OnCurve: a mullion belongs to a grid line by geometry (within 1 mm); Revit keeps no link between them."),
            Row("horizun_slab_shape", VerificationMechanism.PostconditionChecklist, E("postconditions", "evidence", "application"), F(C + "SlabShapeCommand.cs", C + "ModelEditRunner.cs"),
                "SlabShapeCommand.cs: SlabShapeVertex.Position.Z is undocumented as absolute or relative; both readings are accepted and the one that held is reported in evidence.z_convention."),
            Row("horizun_create_railing", VerificationMechanism.PostconditionChecklist, E("postconditions", "evidence", "application"), F(C + "CreateRailingCommand.cs", C + "ModelEditRunner.cs"),
                "CreateRailingCommand.cs: a sketched path is compared in plan (x, y); its z is reported, not judged."),
            Row("horizun_view_visibility", VerificationMechanism.PostconditionChecklist, E("postconditions", "evidence", "application"), F(C + "ViewVisibilityCommand.cs", C + "ViewVisibilityDatums.cs", C + "ModelEditRunner.cs"),
                "ViewVisibilityCommand.cs: ownership (mark, this session, worksharing creator) is checked before the plan; IsHidden, GetCategoryHidden, the overrides and the marker scale are re-read after commit; copy_link_overrides (ViewVisibilityLinks.cs) re-reads the temporary template's deletion, each target's template and, 2024+, the readable RevitLinkGraphicsSettings against the source; datum_cleanup and set_scale (ViewVisibilityDatums.cs) re-read IsHidden of every planned datum, the view scale and that no foreign marker is left visible."),
            Row("horizun_wall_sweep", VerificationMechanism.PostconditionChecklist, E("postconditions", "evidence", "application"), F(C + "WallSweepCommand.cs", C + "WallSweepEdges.cs", C + "ModelEditRunner.cs"),
                "WallSweepCommand.cs place: the height is re-read from WallSweepInfo.Distance and checked against the body's vertical box extent (sweeps only); a reveal's cut is not measured.",
                "WallSweepEdges.cs place_edge: the sweep is re-read for existence, class, type and that its box touches the host's; WHICH edges it ended on is Revit's reading of the references and is reported (the first segment's reference curve against the first edge named), not asserted."),
            Row("horizun_join_geometry", VerificationMechanism.PostconditionChecklist, E("postconditions", "evidence", "application"), F(C + "JoinGeometryCommand.cs", C + "ModelEditRunner.cs"),
                "JoinGeometryCommand.cs: AreElementsJoined and IsCuttingElementInJoin re-read per pair; the shared volume is the Boolean intersection of the two committed solids (both operand orders tried) and must be <= 1 cm3 after a join."),
            Row("horizun_room_finishes", VerificationMechanism.PostconditionChecklist, E("postconditions", "evidence", "application"), F(C + "RoomFinishesCommand.cs", C + "ModelEditRunner.cs"),
                "RoomFinishesCommand.cs floors: HOST_AREA_COMPUTED against the shoelace area of the room's finish-face loops (0.5 %) and the Boolean volume shared with each bounding wall (<= 1 cm3); baseboards: host, type, WallSweepInfo side and distance."),
            Row("horizun_wall_finish", VerificationMechanism.PostconditionChecklist, E("postconditions", "evidence", "application"), F(C + "WallFinishCommand.cs", C + "ModelEditRunner.cs"),
                "WallFinishCommand.cs: each finish wall re-read for type, parallelism to the core, its axis offset from the core axis (1 mm) and its length (1 mm); the Boolean volume it shares with the linked core (solids moved by the link transform) must be <= 1 cm3."),
            Row("horizun_planimetry_ops", VerificationMechanism.PostconditionChecklist, E("postconditions", "evidence", "application"), F(C + "PlanimetryOpsCommand.cs", C + "ModelEditRunner.cs"),
                "PlanimetryOpsCommand.cs: datum extent type and view curve end points (1 mm) and bubbles; stagger_grid_bubbles re-reads visible straight-grid 2D endpoints (1 mm), visibility and zero overlapping declared circular envelopes, without claiming rendered glyph measurement; viewport box + label inside the title block less margin (0.5 mm); the overlapping pairs found re-read clear; each legend component's LEGEND_COMPONENT type and its label text."),
            Row("horizun_view_crop", VerificationMechanism.PostconditionChecklist, E("postconditions", "evidence", "application"), F(C + "ViewCropCommand.cs", C + "ModelEditRunner.cs"),
                "ViewCropCommand.cs: the crop box is re-read through its own (possibly new) CropBox.Transform and compared in the caller's convention within 1 mm; on a plan rotated against the model the target is the smallest crop containing the asked model rectangle, not that rectangle."),
            Row("horizun_sheet_layout", VerificationMechanism.PostconditionChecklist, E("postconditions", "evidence", "application"), F(C + "SheetLayoutCommand.cs", C + "ModelEditRunner.cs"),
                "SheetLayoutCommand.cs: anchor=crop reads the crop on paper through GetModelToProjectionTransforms (one region; a split crop is refused) and GetProjectionToSheetTransform; the title block's box, not its drawn border, bounds the usable area."),
            // horizun_framing: every planned member re-read (type, endpoints within 1 mm, inside the
            // source wall's layer / the ceiling's boundary), counts per role == plan, no stud through an
            // opening, the wall's hosted inserts untouched; read/remove by the marker on each member
            // (a copied member, whose own UniqueId differs from its marker's, is never removed);
            // a remove's cascade measured in the rehearsal, bound by the token and re-read.
            // method 'curtain': every curtain wall / layer roof / hanger wall re-read (type, line or
            // plane and footprint within 1 mm, fixed-distance grid spacing, mullion types, grid 1
            // direction), the carrier's action, line and location line (wall centreline), its inserts
            // (type, position, host) against their snapshot, no piece embedded in it, and what its
            // delete or its change took exactly as the rehearsal measured; remove restores the carrier
            // (type, line, location-line reference) from the record the pieces carry and re-reads it.
            Row("horizun_framing", VerificationMechanism.PostconditionChecklist, E("postconditions", "evidence", "application"),
                F(C + "FramingCommand.cs", C + "FramingApply.cs", C + "FramingCeiling.cs", C + "FramingCurtain.cs", C + "FramingCurtainRemove.cs", C + "FramingCurtainCeiling.cs",
                  "Horizun.Revit/Core/WallFramingRules.cs", "Horizun.Revit/Core/CeilingFramingRules.cs", "Horizun.Revit/Core/CurtainFramingRules.cs"),
                "FramingCeiling.cs: after the commit each hanger is re-cast and its top must meet the first support within 1 mm (hanger_reaches_support); that support is not compared by id with the one the planning ray hit (evidence.hanger_supports). " +
                "FramingCurtainRemove.cs: a carrier the curtain method deleted comes back under a NEW id, without its mark, comments, phase or workset, and without what its delete took (all named in the plan)."),

            Row("horizun_manage_groups", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"),
                F(C + "ManageGroupsCommand.cs", C + "ManageGroupsCommand.Execution.cs", "Horizun.Revit/Core/GroupWorksetRules.cs", "Horizun.Revit/Core/GroupRebuildChecks.cs"),
                "ManageGroupsCommand.cs add/remove_members: retained members of a swapped instance are checked by category, type and bounding box. add_members also checks the translated added members and their included dependents, preserves annotation references and verifies the observed additions and approved deletions."),
            Row("horizun_manage_worksets", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"),
                F(C + "ManageWorksetsCommand.cs", "Horizun.Revit/Core/GroupWorksetRules.cs"),
                "ManageWorksetsCommand.cs set_default: the dry run is a measured preview, not a provisional change - the active workset is a session setting."),
            Row("horizun_undo", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"), F(C + "UndoCommand.cs"),
                "UndoCapture.cs State: the drift guard compares location, type, pin, orientation and tag head; an edit to an element's OTHER parameters since the batch is not detected."),

            // ---- typed model writes: per-row re-reads -------------------------------------
            Row("horizun_write_params_verified", VerificationMechanism.PerRowReread, E("verification", "application"), F(C + "WriteParamsCommand.cs")),
            Row("horizun_set_keynote", VerificationMechanism.PerRowReread, E("writes_verified_after_commit", "verification", "application"), F(C + "SetKeynoteCommand.cs")),
            Row("horizun_manage_classification_table", VerificationMechanism.SessionStateReread, E("after", "verified_after_commit"), F(C + "ManageClassificationTableCommand.cs")),
            Row("horizun_bind_shared_param", VerificationMechanism.PerRowReread, E("outcome", "application"), F(C + "BindSharedParamCommand.cs")),
            Row("horizun_manage_parameters", VerificationMechanism.PostconditionChecklist, E("result", "application"),
                F(C + "ManageParametersCommand.cs", C + "ManageParametersCommand.Bindings.cs", C + "ManageParametersCommand.Shared.cs", C + "ManageParametersCommand.Globals.cs"),
                "ManageParametersCommand.Shared.cs: the SPF definition is written before the transaction; a binding that then fails leaves it in the file (reported as spf_definition_created).",
                "ManageParametersCommand.Globals.cs global_set: an associated element parameter is checked for its association, not for holding the global's value."),
            Row("horizun_model_diff", VerificationMechanism.PerRowReread, E("overrides_verified", "view_verified", "application"),
                F(C + "ModelDiffColorize.cs"),
                "ModelDiffColorize.cs: only the projection line colour of each override is re-read; the surface pattern is set but not compared."),
            Row("horizun_manage_views", VerificationMechanism.PerRowReread, E("actions_verified", "application"),
                F(C + "ManageViewsCommand.cs", C + "ManageViewsCommand.Execution.cs", C + "ManageViewsGraphics.cs", C + "ManageViewsLegends.cs", C + "ManageViewsControl.cs", C + "ManageViewsRenumber.cs", C + "ManageViewsPerspective.cs", C + "ManageViewsSunStudy.cs"),
                "ManageViewsControl.cs order_filters: restored overrides are compared on the fields the precedence report reads, not background patterns or detail level."),
            Row("horizun_link_schedule", VerificationMechanism.PerRowReread, E("verification", "overrides_verified", "postcondition", "application"),
                F(C + "LinkScheduleCommand.cs", C + "LinkScheduleWrite.cs", C + "LinkScheduleStatusView.cs",
                  "Horizun.Revit/Core/ScheduleLinkWriteRules.cs", "Horizun.Revit/Core/ScheduleLinkRules.cs"),
                "LinkScheduleStatusView.cs: verification reads the copy and each stored override; it does not measure element visibility or render the view."),
            Row("horizun_code_check", VerificationMechanism.PerRowReread, E("paths", "application"), F(C + "CodeCheckTravel.cs"),
                "CodeCheckTravel.cs KeepPaths: a kept PathOfTravel is re-read by owner view and total length (within max(50 mm, 1 %) of the measured route), not vertex by vertex; operation=check, energy_readiness (its energy model is always rolled back), headroom (rays only) and measure-only travel_distance write nothing."),
            Row("horizun_manage_schedules", VerificationMechanism.PerRowReread, E("actions_verified", "application"), F(C + "ManageSchedulesCommand.cs"),
                "ManageSchedulesCommand.cs set_filters / set_sorting: the field each filter or sort entry targets is not re-read, only the entries' shape."),
            Row("horizun_manage_revisions", VerificationMechanism.PerRowReread, E("rows", "application"), F(C + "ManageRevisionsCommand.cs"),
                "ManageRevisionsCommand.cs Verify: a revision cloud is re-read by revision and owner view, not by its geometry against the planned loops."),
            Row("horizun_manage_phases", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"),
                F(C + "ManagePhasesCommand.cs", C + "StagedGroupWrite.cs")),
            Row("horizun_manage_assemblies_parts", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"),
                F(C + "ManageAssembliesPartsCommand.cs", C + "StagedGroupWrite.cs"),
                "ManageAssembliesPartsCommand.cs divide_parts: a division is verified by the part count derived from each divided part (>= 2), not by the geometry of the cut."),
            Row("horizun_manage_materials", VerificationMechanism.PerRowReread, E("host_verified", "application"), F(C + "ManageMaterialsCommand.cs")),
            Row("horizun_manage_styles", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"), F(C + "ManageStylesCommand.cs", C + "VerifiedModelEdit.cs")),
            Row("horizun_manage_units", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"), F(C + "ManageUnitsCommand.cs", C + "VerifiedModelEdit.cs"),
                "ManageUnitsCommand.cs base_points: the shared position is re-read at the project base point only; linked models that follow it are not re-read."),
            Row("horizun_annotate", VerificationMechanism.PerRowReread, E("annotations_verified", "application"), F(C + "AnnotateCommand.cs", C + "AnnotateCommand.Apply.cs", C + "LoadTagFamilyEdit.cs", C + "ModelEditRunner.cs"),
                "AnnotateCommand.cs VerifyOtherAnnotation: with avoid_collisions, a tag whose own extent cannot be measured still verifies (tag_extent_measured=false says so)."),
            Row("horizun_edit_dimensions", VerificationMechanism.PerRowReread, E("actions_verified", "application"), F(C + "EditDimensionsCommand.cs"),
                "EditDimensionsCommand.cs ~l.840/870: reset_text_position is recorded match=true without a comparison - Revit publishes no reset state to re-read."),
            Row("horizun_detail_2d", VerificationMechanism.PerRowReread, E("actions_verified", "application"), F(C + "Detail2DCommand.cs")),
            Row("horizun_pack_sheets", VerificationMechanism.PerRowReread, E("host_verified", "application"), F(C + "PackSheetsCommand.cs")),
            // resize: every fitting retyped or newly present at a resized run's end gets a
            // required "fitting_size:<id>" postcondition (its connectors re-read against
            // the resize's own target size); a fitting whose id was removed and replaced
            // has no id left to check, so it is named in Report()'s fittings_removed_or_replaced
            // and its replacement (if any) is verified there under fittings_added instead.
            // route: postconditions cover free ends and bend junctions and size (1 mm) and elbow connections;
            // the spatial-check gate (SpatialCoherence.Check against every created element) runs
            // INSIDE Apply() and throws on any error against a physical host or loaded link, so a
            // committed route is one the gate already passed - it is not a separate postcondition.
            // slope: every pipe end re-read against its planned elevation (sign and height),
            // the signed slope of each non-riser pipe, each fitting centre, the held end,
            // min_clearance and every connector pair recorded before the write.
            // hangers: every placed instance re-reads its type, position (1 mm, Z from its level
            // plus the governing offset), rotation (0.5 degree) and the named rod parameter (1 mm),
            // and a "count" item holds placed == planned (MepRoutingHangers.cs).
            // retype_run: every run's type, size (0.003 mm) and connection count; every old fitting matched to the one standing in
            // its place by position and port direction (MepRetypeRules), fitting ports still connected, every link that left the
            // network still connected at the same far connector, and no joint worse than the snapshot (MepJointRules). Rules edits
            // of the target type, when sent, are re-read through the same checks as set_rules (MepRoutingRetype.cs).
            Row("horizun_structural_connections", VerificationMechanism.PerRowReread, E("host_verified", "application"), F(C + "StructuralConnectionsCommand.cs"),
                "StructuralConnectionsCommand.cs ~l.580: the per-row verified field uses a count (connected >= members), not containment; the verdict itself uses containment."),
            Row("horizun_split_multilayer_walls", VerificationMechanism.PerRowReread, E("all_verified", "application"),
                F(C + "SplitMultilayerWallsCommand.cs", C + "WallSplitVerifier.cs", C + "WallSplitExecutor.cs"),
                "WallSplitVerifier.cs ~l.676/981/1273: an insert, sweep or foundation whose bounding box could not be read before the split skips the bounds check instead of reporting it unmeasured.",
                "WallSplitVerifier.cs ~l.734: CompareParameters walks the parameters present after the split; one that vanished is never compared.",
                "SplitMultilayerWallsCommand.cs ~l.316: unexpected warnings set all_verified=false but are not folded into the application declaration."),
            Row("horizun_split_by_level", VerificationMechanism.PerRowReread, E("all_verified", "application"),
                F(C + "SplitByLevelCommand.cs", C + "SplitByLevelCommand.Apply.cs"),
                "Only supported walls, columns and vertical rigid MEP runs are eligible; other geometry is refused."),

            // ---- counts ---------------------------------------------------------------------
            Row("horizun_delete_verified", VerificationMechanism.CountReconciliation, E("verification", "application"), F(C + "DeleteCommand.cs"),
                "DeleteCommand.cs VerifyParameterBindingsRemoved: a deleted ParameterElement/SharedParameterElement is re-confirmed absent from the BindingMap by NAME (the deleted id no longer resolves to compare against), which cannot distinguish it from a DIFFERENT parameter later bound under the same name."),
            Row("horizun_split_floor_loops", VerificationMechanism.CountReconciliation, E("all_verified", "application"), Recipe),
            Row("horizun_split_multilayer_slabs", VerificationMechanism.CountReconciliation, E("all_verified", "application"), Recipe),
            Row("horizun_ungroup_and_mark", VerificationMechanism.CountReconciliation, E("all_verified", "application"), Recipe),
            Row("horizun_regroup_by_param", VerificationMechanism.CountReconciliation, E("all_verified", "application"), Recipe,
                "RecipeTools.cs ~l.102: elements_still_stamped is re-read by the recipe but is not one of the Verifications, so clearing the stamp is not part of the verdict."),
            Row("horizun_copy_slab_elevations", VerificationMechanism.CountReconciliation, E("all_verified", "application"), Recipe),
            Row("horizun_embed_floors_in_toposolid", VerificationMechanism.CountReconciliation, E("all_verified", "application"), Recipe),
            Row("horizun_grade_toposolid_around_floors", VerificationMechanism.CountReconciliation, E("all_verified", "application"), Recipe),
            Row("horizun_rectangularize_walls", VerificationMechanism.CountReconciliation, E("all_verified", "application"), Recipe,
                "Recipes/rectangularize_walls.py ~l.2013: only fragments_present is verified; the deletion of the original walls is not."),

            // ---- files ----------------------------------------------------------------------
            Row("horizun_export", VerificationMechanism.FileArtifactReread, E("files_verified"),
                F(C + "ExportCommand.cs", C + "ExportDwgSetup.cs", C + "ExportSets.cs", C + "ExportCobie.cs", "Horizun.Revit/Core/ExportFileDiff.cs",
                  "Horizun.Revit/Core/XlsxWorkbookWriter.cs", "Horizun.Revit/Core/XlsxWorkbookReader.cs", "Horizun.Revit/Core/CobieRules.cs"),
                "ExportCommand.cs dwg with dwg_setup: the named setup's layer mapping is not proved from the DWG binary; dwg_layers proves the table itself. " +
                "ExportSets.cs: a dwg/dgn/dwfx set is proved file by file from its header (a DWG's AC10xx against the asked acad_version, a DGN v8 " +
                "structured storage, a DWFx zip package), not from the drawing it holds; an .rfa re-read proves only BasicFileInfo.Format, the saved " +
                "version, not the family's contents; a gbXML is proved by its root, Campus and Space count - Zone, Surface, Opening and Construction are counted, not judged. " +
                "ExportCobie.cs: the .xlsx is re-read from disk with the Core reader and must equal the plan in sheet names and order, row counts " +
                "and every cell (kind and exact text), hashed; that proves the file holds the rows that were built and judged, not that Excel or a " +
                "COBie checker accepts it, and not that the model's data is right - deliverable_ready is the tool's own findings, not COBie QC."),
            Row("horizun_capture_view", VerificationMechanism.FileArtifactReread, E("sha256", "bytes"), F(C + "CaptureViewCommand.cs")),
            Row("horizun_verify_changes", VerificationMechanism.FileArtifactReread, E("image", "spatial_check", "baseline_png", "artifacts_verified"), F(C + "VerifyChangesCommand.cs", C + "VerifyChangesSnapshot.cs")),
            Row("horizun_excel_write_rows", VerificationMechanism.FileArtifactReread, E("verified"), F(S + "ExcelWriteRows.cs")),
            Row("horizun_budget_compare", VerificationMechanism.FileArtifactReread, E("verified"), F(S + "BudgetCompare.cs", S + "BudgetBc3Export.cs")),
            Row("horizun_project_context", VerificationMechanism.FileArtifactReread, E("written", "verification"), F(S + "ProjectContext.cs", S + "ProjectIntegrationsTool.cs")),
            Row("horizun_exchange", VerificationMechanism.FileArtifactReread, E("written", "reread"), F(S + "ExchangeTool.cs", "Horizun.Revit/Core/ExchangeManifest.cs"),
                "ExchangeManifest.Emit: every payload copy and the manifest are re-hashed and the exchange re-verified before 'written'; that proves " +
                "the bytes, not the payload's content - a wrap made by this tool carries the producer's state as testimony, capped at " +
                "self_reported_verified, and the declared rows are reported, never re-counted."),
            Row("horizun_information_container", VerificationMechanism.FileArtifactReread, E("verified"), F(S + "InformationContainerTool.cs")),

            // ---- composition ----------------------------------------------------------------
            Row("horizun_execute_plan", VerificationMechanism.DelegatedChildDeclaration, E("actions_verified", "application"), F(C + "ExecutePlanCommand.cs", "Horizun.Revit/Core/PlanLedger.cs", "Horizun.Revit/Core/CompositeVerdict.cs")),
            Row("horizun_apply_corrections", VerificationMechanism.DelegatedChildDeclaration, E("re_audit", "application"), F(C + "ApplyCorrectionsCommand.cs", "Horizun.Revit/Core/CorrectionApplyLoop.cs", "Horizun.Revit/Core/CompositeVerdict.cs"),
                "ApplyCorrectionsCommand.cs ~l.292: the re-audit (persistent / not_verifiable) is reported beside the application block, not folded into it - a step whose typed call declared verified_applied but whose re-run check still lists the finding as persistent does not downgrade this command's own application block."),

            // ---- outside the model ---------------------------------------------------------
            Row("horizun_power_bi_push", VerificationMechanism.RemoteAcknowledgement, E("http_status"), F(S + "PowerBiPush.cs"),
                "PowerBiPush.cs: a successful HTTP status is Microsoft's acknowledgement; the pushed rows cannot be re-read from the dataset by this tool, and the reply says so."),
            Row("horizun_cde_cloud", VerificationMechanism.RemoteReread, E("host_verified", "verification"), F(S + "CdeCloudIssues.cs"),
                "CdeCloudIssues.cs: only issue_create/issue_update write. web_url is built from the documented ACC path, not read from the API; published is sent and reported, not judged; the idempotency scan pages by offset (sorted by displayId), so an issue DELETED mid-scan can shift an unread one into a page already read."),
            Row("horizun_submit_job", VerificationMechanism.QueuedNotExecuted, E("status"), F(C + "SubmitJobCommand.cs")),
            Row("horizun_execute_python", VerificationMechanism.SelfReported, E("evidence_status"), F(C + "ExecutePythonCommand.cs", "Horizun.Revit/Core/ScriptEvidence.cs"),
                "Core/ScriptEvidence.cs ~l.210: any non-empty evidence array classifies as self_reported_verified - by design the script's testimony, host_verified is always false."),
        };
    }
}
