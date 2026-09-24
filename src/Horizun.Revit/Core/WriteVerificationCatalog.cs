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
        SelfReported
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
            Row("horizun_document_session", VerificationMechanism.SessionStateReread, E("active_document_verified"), F(C + "DocumentSessionCommand.cs"),
                "Core/WorksetConfigurationEvidence.cs ~l.59: open_all_worksets counts as applied when zero user worksets are observed, which is right for a non-workshared model and unmeasured for a workshared one whose collector returned nothing."),

            // ---- typed model writes: checklists -------------------------------------------
            Row("horizun_create_schedule", VerificationMechanism.PostconditionChecklist, E("postcondition", "application"), F(C + "CreateScheduleCommand.cs")),
            Row("horizun_create_elements", VerificationMechanism.PostconditionChecklist, E("postconditions", "production_postconditions", "application"),
                F(C + "CreateElementsCommand.cs", C + "CreateElementsGeometry.cs", C + "CreateElementsProductionVerification.cs", C + "CreateStairsGeometry.cs")),
            Row("horizun_fix_planimetry", VerificationMechanism.PostconditionChecklist, E("postconditions", "application"), F(C + "FixPlanimetryCommand.cs")),
            Row("horizun_transform_elements", VerificationMechanism.PostconditionChecklist, E("operations_verified", "postconditions", "application"), F(C + "TransformElementsCommand.cs"),
                "TransformElementsCommand.cs Verify: move/rotate/mirror/pin/change_type/set_curve/wall_join compare per element with booleans (guarded: an element that does not re-read fails, an empty target list never passes); only the tag operations carry a PostconditionCheck."),

            // ---- typed model writes: per-row re-reads -------------------------------------
            Row("horizun_write_params_verified", VerificationMechanism.PerRowReread, E("verification", "application"), F(C + "WriteParamsCommand.cs")),
            Row("horizun_set_keynote", VerificationMechanism.PerRowReread, E("writes_verified_after_commit", "verification", "application"), F(C + "SetKeynoteCommand.cs")),
            Row("horizun_bind_shared_param", VerificationMechanism.PerRowReread, E("outcome", "application"), F(C + "BindSharedParamCommand.cs")),
            Row("horizun_family_apply", VerificationMechanism.PerRowReread, E("fully_verified", "application"), F(C + "FamilyApplyCommand.cs"),
                "FamilyApplyCommand.cs ~l.713: the application block counts only value writes (plan.Sets); adds, removals, formula clears and type deletes are not counted, so a batch of only those reads no_op.",
                "FamilyApplyCommand.cs ~l.636: rows confirmed only by reading back Revit's own parse are counted as verified.",
                "FamilyApplyCommand.cs ~l.603-622: an invariant that reads violated_after_commit / unknown_after_commit does not reach the application verdict."),
            Row("horizun_manage_system_types", VerificationMechanism.PerRowReread, E("created_verified", "application"), F(C + "ManageSystemTypesCommand.cs"),
                "ManageSystemTypesCommand.cs: a parameter Revit parsed from text passes with intent_verified=false beside verified=true."),
            Row("horizun_manage_views", VerificationMechanism.PerRowReread, E("actions_verified", "application"),
                F(C + "ManageViewsCommand.cs", C + "ManageViewsGraphics.cs", C + "ManageViewsLegends.cs", C + "ManageViewsControl.cs"),
                "ManageViewsCommand.cs ~l.909-985: names set on created views, placeholder sheets and duplicated sheets are not re-read.",
                "ManageViewsGraphics.cs hide_elements: a temporary hide checks that the view is in temporary mode, not which elements are hidden.",
                "ManageViewsGraphics.cs color_by_value: override colours are not re-read (the legend's filters and their visibility are).",
                "ManageViewsLegends.cs ~l.190: detail_level and location of legend components are not re-read.",
                "ManageViewsControl.cs order_filters: restored overrides are compared on the fields the precedence report reads, not background patterns or detail level."),
            Row("horizun_manage_schedules", VerificationMechanism.PerRowReread, E("actions_verified", "application"), F(C + "ManageSchedulesCommand.cs"),
                "ManageSchedulesCommand.cs set_filters / set_sorting: the field each filter or sort entry targets is not re-read, only the entries' shape."),
            Row("horizun_manage_revisions", VerificationMechanism.PerRowReread, E("rows", "application"), F(C + "ManageRevisionsCommand.cs"),
                "ManageRevisionsCommand.cs Verify: a revision cloud is re-read by revision and owner view, not by its geometry against the planned loops."),
            Row("horizun_manage_materials", VerificationMechanism.PerRowReread, E("host_verified", "application"), F(C + "ManageMaterialsCommand.cs")),
            Row("horizun_manage_links", VerificationMechanism.PerRowReread, E("verified", "application"), F(C + "ManageLinksCommand.cs"),
                "ManageLinksCommand.cs ~l.196: reloading a link that is already Loaded verifies by construction (status Loaded before and after)."),
            Row("horizun_manage_cad_links", VerificationMechanism.PerRowReread, E("host_verified", "application"), F(C + "ManageCadLinksCommand.cs"),
                "ManageCadLinksCommand.cs add ~l.847: a file hash unreadable on either side reads as no disagreement, and a disagreement does not downgrade the verdict.",
                "ManageCadLinksCommand.cs repoint ~l.507: the commit status is not checked."),
            Row("horizun_annotate", VerificationMechanism.PerRowReread, E("annotations_verified", "application"), F(C + "AnnotateCommand.cs"),
                "AnnotateCommand.cs ~l.1650: when the rehearsal never got a value, a post-commit null reads as a match unless expected_value was given.",
                "AnnotateCommand.cs ~l.1969: with avoid_collisions, a tag whose own extent cannot be measured still verifies (tag_extent_measured=false says so)."),
            Row("horizun_edit_dimensions", VerificationMechanism.PerRowReread, E("actions_verified", "application"), F(C + "EditDimensionsCommand.cs"),
                "EditDimensionsCommand.cs ~l.840/870: reset_text_position is recorded match=true without a comparison - Revit publishes no reset state to re-read."),
            Row("horizun_detail_2d", VerificationMechanism.PerRowReread, E("actions_verified", "application"), F(C + "Detail2DCommand.cs")),
            Row("horizun_pack_sheets", VerificationMechanism.PerRowReread, E("host_verified", "application"), F(C + "PackSheetsCommand.cs")),
            Row("horizun_apply_reinforcement", VerificationMechanism.PerRowReread, E("created_verified", "cover_verified", "application"), F(C + "ApplyReinforcementCommand.cs")),
            Row("horizun_connect_mep", VerificationMechanism.PerRowReread, E("host_verified", "application"), F(C + "ConnectMepCommand.cs")),
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
            Row("horizun_delete_verified", VerificationMechanism.CountReconciliation, E("verification", "application"), F(C + "DeleteCommand.cs")),
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
            Row("horizun_export", VerificationMechanism.FileArtifactReread, E("files_verified"), F(C + "ExportCommand.cs", C + "ExportDwgSetup.cs"),
                "ExportCommand.cs dwg with dwg_setup: the named setup's layer mapping is not proved from the DWG binary; dwg_layers proves the table itself.",
                "ExportCommand.cs ~l.409: for non-PDF formats one new or changed matching file suffices; no expected file count is checked.",
                "ExportCommand.cs Snapshot ~l.861: a file that could not be stat'ed before the export is missing from the before-snapshot, so it counts as produced."),
            Row("horizun_deliver_ifc", VerificationMechanism.FileArtifactReread, E("deliverable_ready"), F(C + "DeliverIfcCommand.cs")),
            Row("horizun_capture_view", VerificationMechanism.FileArtifactReread, E("sha256", "bytes"), F(C + "CaptureViewCommand.cs")),
            Row("horizun_excel_write_rows", VerificationMechanism.FileArtifactReread, E("verified"), F(S + "ExcelWriteRows.cs")),
            Row("horizun_budget_compare", VerificationMechanism.FileArtifactReread, E("verified"), F(S + "BudgetCompare.cs")),
            Row("horizun_project_context", VerificationMechanism.FileArtifactReread, E("written", "verification"), F(S + "ProjectContext.cs")),
            Row("horizun_information_container", VerificationMechanism.FileArtifactReread, E("verified"), F(S + "InformationContainerTool.cs")),

            // ---- composition ----------------------------------------------------------------
            Row("horizun_execute_plan", VerificationMechanism.DelegatedChildDeclaration, E("actions_verified"), F(C + "ExecutePlanCommand.cs", "Horizun.Revit/Core/PlanLedger.cs"),
                "ExecutePlanCommand.cs / Core/PlanLedger.cs ~l.251: the plan's own reply carries no top-level application block, and a plan whose every child answered no_op reports actions_verified=N."),
            Row("horizun_apply_corrections", VerificationMechanism.DelegatedChildDeclaration, E("re_audit", "application"), F(C + "ApplyCorrectionsCommand.cs", "Horizun.Revit/Core/CorrectionApplyLoop.cs"),
                "Core/CorrectionApplyLoop.cs ~l.89: a child that answers no_op counts as an applied step.",
                "ApplyCorrectionsCommand.cs ~l.292: the re-audit (persistent / not_verifiable) is reported beside the application block, not folded into it."),
            Row("horizun_apply_ifc_plan", VerificationMechanism.DelegatedChildDeclaration, E("state", "created_verified"), F(C + "ApplyIfcPlanCommand.cs"),
                "ApplyIfcPlanCommand.cs: the reply carries no application block, so a composing plan reads it as uncertain."),
            Row("horizun_apply_cad_plan", VerificationMechanism.DelegatedChildDeclaration, E("created_verified", "state"), F(C + "ApplyCadPlanCommand.cs"),
                "ApplyCadPlanCommand.cs ~l.379/420: a stage reads applied on the child's Success without comparing created_verified with the rows sent.",
                "ApplyCadPlanCommand.cs ~l.802: required_missing is keyed by parameter NAME across the stage, so a value missing on one element hides behind the same name on another."),
            Row("horizun_apply_cad_update", VerificationMechanism.DelegatedChildDeclaration, E("verdict", "state"), F(C + "ApplyCadUpdateCommand.cs"),
                "ApplyCadUpdateCommand.cs ~l.511/545/869: an action counts as landed on the child's transport Success, not on its application block."),
            Row("horizun_cad_connect", VerificationMechanism.DelegatedChildDeclaration, E("state"), F(C + "CadConnectCommand.cs", C + "CadRefit.cs"),
                "CadConnectCommand.cs ~l.1751/1855: direct joins and elbow/tee/cross rows become joined/created on the child's Success, not on its application block.",
                "CadConnectCommand.cs ~l.429, CadRefit.cs ~l.131: state reads applied whenever nothing was refused, including when zero junctions were joined; neither reply carries an application block.",
                "CadConnectCommand.cs ~l.328, CadRefit.cs ~l.94: CheckedWriteGroup.Keep() is followed without checking Outcome == kept or Started."),

            // ---- outside the model ---------------------------------------------------------
            Row("horizun_power_bi_push", VerificationMechanism.RemoteAcknowledgement, E("http_status"), F(S + "PowerBiPush.cs"),
                "PowerBiPush.cs: a successful HTTP status is Microsoft's acknowledgement; the pushed rows cannot be re-read from the dataset by this tool, and the reply says so."),
            Row("horizun_submit_job", VerificationMechanism.QueuedNotExecuted, E("status"), F(C + "SubmitJobCommand.cs")),
            Row("horizun_execute_python", VerificationMechanism.SelfReported, E("evidence_status"), F(C + "ExecutePythonCommand.cs", "Horizun.Revit/Core/ScriptEvidence.cs"),
                "Core/ScriptEvidence.cs ~l.210: any non-empty evidence array classifies as self_reported_verified - by design the script's testimony, host_verified is always false."),
        };
    }
}
