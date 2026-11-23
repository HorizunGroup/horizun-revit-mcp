// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// THE PER-CALL HALF OF THE PERMISSION PROFILE.
//
// Settings.IsToolAllowed answers about a TOOL: is it listed, may it be dispatched.
// Three tools are admitted by profiles that authorize only PART of what they can do,
// and this file is that part, decided once and asked by each command before it
// touches anything:
//
//   horizun_save_document  save_local and relinquish_own on every profile that admits
//                          the tool; sync_with_central only on full_write/unsafe_code, or
//                          safe_write when the owner granted the sync (CT-P1-2)
//                          (and the owner's grant, checked by the sync itself). It
//                          publishes into a file other people work from, with no
//                          rollback - visibility in a toolset is not that privilege.
//   horizun_export         read_only, documentation and safe_write may export PDF and a
//                          single-view image and nothing else. A PDF of sheets is the
//                          deliverable those profiles exist to produce, and an image of
//                          one view is a picture of it (ExportImage writes nothing to the
//                          model; field report: refusing it on safe_write sent agents to
//                          screenshots). A DWG/IFC/NWC is a model leaving the building
//                          and stays on full_write.
//   delete/write_params    on documentation, every target must be documentation itself:
//                          a view, sheet, viewport or schedule-on-sheet, an element owned
//                          by a view, or an annotation-category element. One modelled
//                          element refuses the WHOLE batch, naming the ids, before any
//                          transaction opens.
//
// Revit-free on purpose: the commands classify each element (Revit) and this decides
// (pure), so every rule here is an ordinary unit test.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What a documentation-profile write would touch, as the command classified it.</summary>
    public sealed class DocumentationTarget
    {
        /// <summary>View, ViewSheet, Viewport, ScheduleSheetInstance.</summary>
        public const string ViewKind = "view";
        /// <summary>An element with a valid OwnerViewId: lives in one view.</summary>
        public const string ViewOwnedKind = "view_owned";
        /// <summary>An element whose category is an annotation category.</summary>
        public const string AnnotationKind = "annotation";
        /// <summary>Anything else: a modelled element, a type, a definition.</summary>
        public const string ModelKind = "model";
        /// <summary>The id resolved to nothing; refused, never assumed harmless.</summary>
        public const string MissingKind = "missing";

        public long? Id { get; set; }
        public string Kind { get; set; }
        public string Category { get; set; }

        public static bool IsDocumentation(string kind) =>
            kind == ViewKind || kind == ViewOwnedKind || kind == AnnotationKind;
    }

    public static class OperationPermission
    {
        public const string DocumentationProfile = "documentation";
        public const string SaveLocal = "save_local";
        public const string SyncWithCentral = "sync_with_central";
        public const string RelinquishOwn = "relinquish_own";

        public static readonly string[] SaveOperations = { SaveLocal, SyncWithCentral, RelinquishOwn };

        /// <summary>The formats a restricted profile may export: paper and a picture of one view.</summary>
        public static readonly string[] RestrictedExportFormats = { "pdf", "pdf_qa", "image" };

        /// <summary>Profiles that may export PDF and a single-view image, and no other format.</summary>
        public static bool PdfOnly(string profile) =>
            profile == "read_only" || profile == DocumentationProfile || profile == "safe_write";

        private static bool Full(string profile) => profile == "full_write" || profile == "unsafe_code";

        /// <summary>
        /// Null when this profile may run this save operation; otherwise the refusal
        /// sentence, naming the profile. An unknown operation is refused here too, so the
        /// command never falls through to a default it did not choose.
        /// </summary>
        public static string SaveRefusal(string profile, string operation, bool ownerSyncGrant = false,
                                         bool syncOnlyFlag = false)
        {
            if (!SaveOperations.Contains(operation ?? "", StringComparer.Ordinal))
                return "operation must be save_local, sync_with_central or relinquish_own (got '" + operation +
                       "'). Nothing was saved.";
            if (operation != SyncWithCentral || Full(profile)) return null;
            // CT-P1-2, THE SYNC-ONLY PERMISSION: safe_write plus the owner's own grant of
            // "Synchronize with central" (Advanced options) admits THIS operation and nothing
            // else of full_write. The grant is the owner's act in Revit, never an MCP call, and
            // the sync still refuses under force_read_only_on_workshared and needs its token.
            if (profile == "safe_write" && ownerSyncGrant) return null;
            // M10: the owner's OTHER, stricter flag (allow_sync_only_grant, OFF by default) opens the same
            // operation to safe_write, and the sync then always passes SyncOnlyGrantRules.
            if (profile == "safe_write" && syncOnlyFlag) return null;
            return "operation=sync_with_central is refused by permission_profile=" + (profile ?? "?") +
                   ": a synchronize publishes into the central model other people work from and cannot be rolled " +
                   "back, so it needs full_write or unsafe_code, or safe_write with the owner's grant of " +
                   "'Synchronize with central' from Revit's Advanced options (that grant alone opens this " +
                   "operation, nothing else of full_write). save_local and relinquish_own remain available. Nothing was synchronized.";
        }

        /// <summary>Null when this profile may export this format; otherwise the refusal.</summary>
        public static string ExportRefusal(string profile, string format)
        {
            if (!PdfOnly(profile) || RestrictedExportFormats.Contains(format ?? "", StringComparer.OrdinalIgnoreCase)) return null;
            return "format=" + (format ?? "?") + " is refused by permission_profile=" + profile + ": this profile " +
                   "exports PDF (format=pdf) and a single-view image (format=image) only. Other formats need full_write. " +
                   "Nothing was exported.";
        }

        /// <summary>
        /// Null when every target is documentation (or the profile is not documentation);
        /// otherwise ONE refusal for the whole batch naming every offending id, so the
        /// caller can split the batch instead of discovering offenders one call at a time.
        /// </summary>
        public static string DocumentationTargetRefusal(string profile, IEnumerable<DocumentationTarget> targets,
                                                        string tool)
        {
            if (profile != DocumentationProfile) return null;
            List<DocumentationTarget> bad = (targets ?? Enumerable.Empty<DocumentationTarget>())
                .Where(t => t == null || !DocumentationTarget.IsDocumentation(t.Kind)).ToList();
            if (bad.Count == 0) return null;
            const int shown = 50;
            string names = string.Join(", ", bad.Take(shown).Select(t => t == null ? "(null)"
                : (t.Id.HasValue ? t.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(no id)") +
                  " (" + (t.Kind ?? "unknown") + (string.IsNullOrEmpty(t.Category) ? "" : ", " + t.Category) + ")"));
            if (bad.Count > shown) names += " and " + (bad.Count - shown) + " more";
            return (tool ?? "this tool") + " is limited by permission_profile=documentation to views, sheets, " +
                   "viewports, schedules on sheets, view-owned elements and annotation; " + bad.Count +
                   " target(s) are not: " + names + ". The whole batch was refused and nothing was written.";
        }
    }
}
