using System;
using System.IO;

namespace Horizun.Revit.Core
{
    /// <summary>Routes paths that are valid Revit files but not openable models.</summary>
    public static class OpenPathRules
    {
        /// <summary>
        /// A background read of a library (copy_between_documents source_path, the audit's
        /// template comparison) opens a .rte as a source and closes it unsaved, so only an
        /// open meant to WORK in the file is routed to new_project.
        /// </summary>
        public static string TemplateRefusal(string path, bool allowProjectTemplate)
            => allowProjectTemplate ? null : TemplateRefusal(path);

        public static string TemplateRefusal(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string extension;
            try { extension = Path.GetExtension(path); }
            catch { return null; }
            if (!string.Equals(extension, ".rte", StringComparison.OrdinalIgnoreCase)) return null;
            return "A .rte is a project template, not an openable project. Use horizun_document_session " +
                   "with operation=new_project, template_path, save_as_path, dry_run=true and target_document; " +
                   "review the rehearsal, then apply with its confirmation_token and idempotency_key. Nothing was opened.";
        }
    }
}
