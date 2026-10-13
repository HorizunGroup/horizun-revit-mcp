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
                F(C + "ManageGroupsCommand.cs", "Horizun.Revit/Core/GroupWorksetRules.cs"),
                "ManageGroupsCommand.cs add/remove_members: a swapped instance is checked by (category, type, bounding box) signatures, not by member identity - the API gives no correspondence between an instance's old and new members."),
            Row("horizun_manage_worksets", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"),
                F(C + "ManageWorksetsCommand.cs", "Horizun.Revit/Core/GroupWorksetRules.cs"),
                "ManageWorksetsCommand.cs set_default: the dry run is a measured preview, not a provisional change - the active workset is a session setting."),
            Row("horizun_coordination", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"),
                F(C + "CoordinationShow.cs", C + "CoordinationNavisworksReadiness.cs"),
                "CoordinationShow.cs: only a bounded sample (20 host + 10 link overrides) is re-read per apply, not every painted element."),
            Row("horizun_resolve_clash", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"), F(C + "ResolveClashCommand.cs", C + "ResolveClashSleeves.cs"),
                "ResolveClashCommand.cs DetectLinks: an unloaded link is listed in links_skipped and its pairs are simply not measured - the no_new_clash postcondition does not fail solely for that, matching SpatialCoherence.AgainstLinks's own convention.",
                "ResolveClashSleeves.cs apply_opening: a sleeve that does not cut its host (no void, or a host that refuses InstanceVoidCutUtils) leaves the run inside the host solid by design - host_cleared is only asked of a cut, and the finding stays open for that reason; a point-placed sleeve's axis follows its family author's modelling and is caught only by the containment check."),
            Row("horizun_undo", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"), F(C + "UndoCommand.cs"),
                "UndoCapture.cs State: the drift guard compares location, type, pin, orientation and tag head; an edit to an element's OTHER parameters since the batch is not detected."),

            // ---- typed model writes: per-row re-reads -------------------------------------
            Row("horizun_write_params_verified", VerificationMechanism.PerRowReread, E("verification", "application"), F(C + "WriteParamsCommand.cs")),
            Row("horizun_set_keynote", VerificationMechanism.PerRowReread, E("writes_verified_after_commit", "verification", "application"), F(C + "SetKeynoteCommand.cs")),
            Row("horizun_bind_shared_param", VerificationMechanism.PerRowReread, E("outcome", "application"), F(C + "BindSharedParamCommand.cs")),
            Row("horizun_manage_parameters", VerificationMechanism.PostconditionChecklist, E("result", "application"),
                F(C + "ManageParametersCommand.cs", C + "ManageParametersCommand.Bindings.cs", C + "ManageParametersCommand.Shared.cs", C + "ManageParametersCommand.Globals.cs"),
                "ManageParametersCommand.Shared.cs: the SPF definition is written before the transaction; a binding that then fails leaves it in the file (reported as spf_definition_created).",
                "ManageParametersCommand.Globals.cs global_set: an associated element parameter is checked for its association, not for holding the global's value."),
            Row("horizun_family_apply", VerificationMechanism.PerRowReread, E("fully_verified", "application"), F(C + "FamilyApplyCommand.cs"),
                "FamilyApplyCommand.cs ~l.713: the application block counts only value writes (plan.Sets); adds, removals, formula clears and type deletes are not counted, so a batch of only those reads no_op.",
                "FamilyApplyCommand.cs ~l.636: rows confirmed only by reading back Revit's own parse are counted as verified (disclosed via params_set_confirmed_by_parse_read_back_only, same weaker-confirmation pattern horizun_write_params_verified documents)."),
            Row("horizun_manage_system_types", VerificationMechanism.PerRowReread, E("created_verified", "application"), F(C + "ManageSystemTypesCommand.cs"),
                "ManageSystemTypesCommand.cs: a parameter Revit parsed from text passes with intent_verified=false beside verified=true."),
            Row("horizun_model_diff", VerificationMechanism.PerRowReread, E("overrides_verified", "view_verified", "application"),
                F(C + "ModelDiffColorize.cs"),
                "ModelDiffColorize.cs: only the projection line colour of each override is re-read; the surface pattern is set but not compared."),
            Row("horizun_manage_views", VerificationMechanism.PerRowReread, E("actions_verified", "application"),
                F(C + "ManageViewsCommand.cs", C + "ManageViewsGraphics.cs", C + "ManageViewsLegends.cs", C + "ManageViewsControl.cs", C + "ManageViewsRenumber.cs", C + "ManageViewsPerspective.cs", C + "ManageViewsSunStudy.cs"),
                "ManageViewsControl.cs order_filters: restored overrides are compared on the fields the precedence report reads, not background patterns or detail level."),
            Row("horizun_link_schedule", VerificationMechanism.PerRowReread, E("verification", "overrides_verified", "postcondition", "application"),
                F(C + "LinkScheduleCommand.cs", C + "LinkScheduleWrite.cs", C + "LinkScheduleStatusView.cs"),
                "LinkScheduleStatusView.cs: only the surface foreground colour of each override is re-read, not the cut pattern or line colour."),
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
            Row("horizun_electrical", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"), F(C + "ElectricalCommand.cs", C + "VerifiedModelEdit.cs"),
                "ElectricalCommand.cs create_circuit: circuit number, loads and voltage drop are reported from the committed circuit, not compared against a request."),
            Row("horizun_manage_links", VerificationMechanism.PerRowReread, E("verified", "postconditions", "host_verified", "application"), F(C + "ManageLinksCommand.cs", C + "ManageLinksCoordinates.cs", C + "ManageLinksPointCloud.cs", C + "ManageLinksIfc.cs", C + "VerifiedModelEdit.cs"),
                "ManageLinksCommand.cs ~l.196: reloading a link that is already Loaded verifies by construction (status Loaded before and after).",
                "ManageLinksCoordinates.cs acquire_coordinates from a CAD link: the proof compares three import points with the DWG's own coordinates, a rule taken from RevitAPI's text, not from horizun_federation_check (which reads RVT links only).",
                "ManageLinksIfc.cs add kind=ifc: the intermediate .ifc.RVT the apply writes stays on disk when the link step fails and is rolled back; the refusal reports whether the file changed. Instance type, DirectShape count and self-link are read inside the transaction and a failing link is rolled back; only when the linked document is unreadable before the commit are they judged after it, where a failure stays committed and is reported unverified.",
                "Mixed mechanisms: list..change_path and add kind=ifc publish a top-level verified; acquire_coordinates and add kind=point_cloud go through VerifiedModelEdit (state, host_verified, postconditions; the point cloud also result.verified)."),
            Row("horizun_manage_cad_links", VerificationMechanism.PerRowReread, E("host_verified", "application"), F(C + "ManageCadLinksCommand.cs"),
                "ManageCadLinksCommand.cs add ~l.847: a file hash unreadable on either side reads as no disagreement, and a disagreement does not downgrade the verdict.",
                "ManageCadLinksCommand.cs repoint ~l.507: the commit status is not checked."),
            Row("horizun_annotate", VerificationMechanism.PerRowReread, E("annotations_verified", "application"), F(C + "AnnotateCommand.cs"),
                "AnnotateCommand.cs ~l.1969: with avoid_collisions, a tag whose own extent cannot be measured still verifies (tag_extent_measured=false says so)."),
            Row("horizun_edit_dimensions", VerificationMechanism.PerRowReread, E("actions_verified", "application"), F(C + "EditDimensionsCommand.cs"),
                "EditDimensionsCommand.cs ~l.840/870: reset_text_position is recorded match=true without a comparison - Revit publishes no reset state to re-read."),
            Row("horizun_detail_2d", VerificationMechanism.PerRowReread, E("actions_verified", "application"), F(C + "Detail2DCommand.cs")),
            Row("horizun_pack_sheets", VerificationMechanism.PerRowReread, E("host_verified", "application"), F(C + "PackSheetsCommand.cs")),
            Row("horizun_apply_reinforcement", VerificationMechanism.PerRowReread, E("created_verified", "cover_verified", "application"), F(C + "ApplyReinforcementCommand.cs")),
            Row("horizun_connect_mep", VerificationMechanism.PerRowReread, E("host_verified", "application"), F(C + "ConnectMepCommand.cs")),
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
            Row("horizun_mep_routing", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"),
                F(C + "MepRoutingCommand.cs", C + "MepRoutingRoute.cs", C + "MepRoutingSlope.cs", "Horizun.Revit/Core/SlopeRules.cs", C + "MepRoutingHangers.cs"),
                "MepRoutingSlope.cs ~l.469: slope's connection:<pair> checks IsConnectedTo, not origin coincidence; an elbow flagged connected but off its pipe end passes (the pipe-slope live probe re-reads origins).",
                "MepRoutingSlope.cs ~l.333: flow direction (Connector.Direction, element-relative) is not measured live; ':high'/':low' do not depend on it.",
                "MepRoutingCommand.cs ~l.146: warnings Revit posts in slope's apply transaction (AutoRouteFailures.AttemptToConnectNonSlopingElementToSlopedPipeWarning) are not captured in the reply.",
                "MepRoutingSlope.cs ~l.481: fitting_centre tolerance allows slope x the longest leg, room for Revit re-orienting an elbow (TO MEASURE LIVE)."),
            Row("horizun_structural_connections", VerificationMechanism.PerRowReread, E("host_verified", "application"), F(C + "StructuralConnectionsCommand.cs"),
                "StructuralConnectionsCommand.cs ~l.580: the per-row verified field uses a count (connected >= members), not containment; the verdict itself uses containment."),
            Row("horizun_copy_between_documents", VerificationMechanism.PerRowReread, E("host_verified", "application"), F(C + "CopyBetweenDocumentsCommand.cs"),
                "CopyBetweenDocumentsCommand.cs: every copy is re-read for presence only; its category and type are reported, not compared with the source."),
            Row("horizun_split_multilayer_walls", VerificationMechanism.PerRowReread, E("all_verified", "application"),
                F(C + "SplitMultilayerWallsCommand.cs", C + "WallSplitVerifier.cs", C + "WallSplitExecutor.cs"),
                "WallSplitVerifier.cs ~l.676/981/1273: an insert, sweep or foundation whose bounding box could not be read before the split skips the bounds check instead of reporting it unmeasured.",
                "WallSplitVerifier.cs ~l.734: CompareParameters walks the parameters present after the split; one that vanished is never compared.",
                "SplitMultilayerWallsCommand.cs ~l.316: unexpected warnings set all_verified=false but are not folded into the application declaration."),

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
            Row("horizun_create_family", VerificationMechanism.FileArtifactReread, E("output_verified", "reopened_verification"), F(C + "CreateFamilyCommand.cs"),
                "CreateFamilyCommand.cs: forms (solid geometry) are verified in memory before saving; the saved file is re-read for dimensions, parameters and types, not forms."),
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
            Row("horizun_deliver_ifc", VerificationMechanism.FileArtifactReread, E("deliverable_ready"), F(C + "DeliverIfcCommand.cs")),
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
            Row("horizun_apply_ifc_plan", VerificationMechanism.DelegatedChildDeclaration, E("state", "created_verified", "application"), F(C + "ApplyIfcPlanCommand.cs", "Horizun.Revit/Core/CompositeVerdict.cs"),
                "ApplyIfcPlanCommand.cs ApplyUpdates: the dry-run (rehearse-and-rollback) reply stays undeclared - it wrote and undid real values, which is not what ApplicationState.Rehearsed means, and there is no vocabulary word for it yet."),
            Row("horizun_apply_cad_plan", VerificationMechanism.DelegatedChildDeclaration, E("created_verified", "state", "application"), F(C + "ApplyCadPlanCommand.cs", "Horizun.Revit/Core/CompositeVerdict.cs")),
            Row("horizun_apply_cad_update", VerificationMechanism.DelegatedChildDeclaration, E("verdict", "state", "application"), F(C + "ApplyCadUpdateCommand.cs", "Horizun.Revit/Core/CompositeVerdict.cs"),
                "ApplyCadUpdateCommand.cs: `verdict`/`state`/`failures` still count an action landed on the child's transport Success, not on its application block - `application` (added) is the accurate one; a caller reading the older fields alone still gets the pre-existing, more lenient answer."),
            Row("horizun_cad_connect", VerificationMechanism.DelegatedChildDeclaration, E("state", "application"),
                F(C + "CadConnectCommand.cs", C + "CadRefit.cs", "Horizun.Revit/Core/CadConnectVerdict.cs", "Horizun.Revit/Core/CompositeVerdict.cs"),
                "CadConnectCommand.cs ~l.429: the PROSE `state` field (rehearsed/applied/partial) still reads applied whenever nothing was refused, unchanged - `application` (added, via CadConnectVerdict + CompositeVerdict) is the accurate one a caller should read instead."),

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
