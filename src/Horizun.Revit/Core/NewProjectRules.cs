// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_document_session operation=new_project: a blank project from a template,
// saved to a path that did not exist, decided out of facts before Revit is asked.
//
// Course dry run 2026-09-30, defect #18: "start a new project for the DWG" had no
// typed route - the session offered open, save, save_as, close, inspect and sync -
// so the run fell back to a copy of another model with a new level, which dragged
// 172 unrelated element modifications along. The Revit half is one call
// (Application.NewProjectDocument) and a SaveAs; what has to be right is around it:
//
//   * IT NEVER OVERWRITES. There is no overwrite flag for new_project. A path that
//     holds a file, or whose existence cannot be tested, is refused: "create a new
//     project" that replaced last month's model would be the worst reading of a
//     harmless request.
//   * THE TEMPLATE IS NAMED, NEVER GUESSED. template_path when given; otherwise the
//     Revit installation's own DefaultProjectTemplate (Options > File Locations, set
//     per locale by the installer) and the reply says which. Neither existing is a
//     refusal - not a silent "no template" project, which has no levels, views or
//     families and is not what anybody means by a new project.
//   * REHEARSED FIRST. dry_run defaults to true, as for sync_with_central: the
//     rehearsal reads both paths and issues a token bound to them; the apply spends
//     it, and a template or target that changed in between is refused.
//
// No `using Autodesk.*`: the command gathers the facts, this decides.
// -----------------------------------------------------------------------------
using System;
using System.IO;

namespace Horizun.Revit.Core
{
    public static class NewProjectRules
    {
        public const string Operation = "new_project";
        public const string ConfirmationScope = "horizun_document_session:new_project";

        public static string FailureStage(bool saving, Exception error)
        {
            if (error == null) throw new ArgumentNullException(nameof(error));
            return saving ? "save" : "create";
        }

        /// <summary>Where the template came from, reported verbatim.</summary>
        public const string FromArgument = "template_path";
        public const string FromRevitDefault = "revit_default_project_template";

        /// <summary>
        /// Why a new project cannot be saved to <paramref name="targetPath"/>, or null.
        /// <paramref name="exists"/> is tri-state: null means the path could not be tested,
        /// and that is refused like an existing file - not knowing is not "nothing there".
        /// </summary>
        public static string TargetProblem(string targetPath, bool? exists, bool folderExists)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
                return "save_as_path is required for new_project: the absolute path of the new .rvt. Nothing was created.";
            if (!SafeRooted(targetPath))
                return "save_as_path must be an absolute rooted path: " + targetPath + ". Nothing was created.";
            if (!string.Equals(SafeExtension(targetPath), ".rvt", StringComparison.OrdinalIgnoreCase))
                return "save_as_path must end in .rvt - new_project creates a project file: " + targetPath +
                       ". Nothing was created.";
            if (!folderExists)
                return "Destination folder does not exist: " + (SafeDirectory(targetPath) ?? "(none)") +
                       ". Refusing to guess where this should go. Nothing was created.";
            if (exists == null)
                return "Whether a file already exists at '" + targetPath + "' could not be tested, and new_project " +
                       "never overwrites: not knowing is not 'nothing there'. Nothing was created.";
            if (exists == true)
                return "A file already exists at '" + targetPath + "'. new_project NEVER overwrites - there is no flag " +
                       "for it - because a new project that replaced an existing model would be the worst reading of the " +
                       "request. Choose another path, or open that file instead. Nothing was created.";
            return null;
        }

        /// <summary>
        /// The template to create from: the caller's, or Revit's own default. Returns the
        /// refusal, or null with <paramref name="resolved"/> and <paramref name="source"/> set.
        /// </summary>
        public static string ResolveTemplate(string requested, string revitDefault, Func<string, bool> fileExists,
                                             out string resolved, out string source)
        {
            resolved = null;
            source = null;
            if (fileExists == null) throw new ArgumentNullException(nameof(fileExists));

            if (!string.IsNullOrWhiteSpace(requested))
            {
                if (!SafeRooted(requested))
                    return "template_path must be an absolute rooted path: " + requested + ". Nothing was created.";
                if (!string.Equals(SafeExtension(requested), ".rte", StringComparison.OrdinalIgnoreCase))
                    return "template_path must be a Revit project template (.rte): " + requested + ". Nothing was created.";
                if (!fileExists(requested))
                    return "Template not found: " + requested + ". Nothing was created.";
                resolved = requested;
                source = FromArgument;
                return null;
            }

            if (string.IsNullOrWhiteSpace(revitDefault))
                return "No template_path was given and this Revit has no default project template configured " +
                       "(Application.DefaultProjectTemplate is empty; Options > File Locations). Pass template_path - " +
                       "Autodesk's own templates are usually under C:\\ProgramData\\Autodesk\\RVT <year>\\Templates\\<language>. " +
                       "A project with NO template has no levels, views or families, and is not created in its place. " +
                       "Nothing was created.";
            if (!fileExists(revitDefault))
                return "No template_path was given, and this Revit's default project template does not exist on disk: " +
                       revitDefault + ". Pass template_path. Nothing was created.";
            resolved = revitDefault;
            source = FromRevitDefault;
            return null;
        }

        /// <summary>
        /// Why the template's own version rules it out, or null. Only a NEWER template is
        /// refused: Revit cannot read it. An older one is upgraded IN MEMORY while the
        /// project is created and the template file itself is never written.
        /// </summary>
        public static string TemplateVersionProblem(string templatePath, string templateVersion, string hostVersion)
        {
            int t, h;
            if (OpenDecision.TryYear(templateVersion, out t) && OpenDecision.TryYear(hostVersion, out h) && t > h)
                return "The template '" + templatePath + "' was saved in Revit " + t + " and this is Revit " + h +
                       ": an older Revit cannot read it. Use a template from Revit " + h + " or older. Nothing was created.";
            return null;
        }

        /// <summary>
        /// The identity the confirmation token binds: both paths as compared (normalized) and
        /// the template file's own stamp, so a template replaced between the rehearsal and the
        /// apply is refused rather than used.
        /// </summary>
        public static string PlanHash(string templatePath, string templateStamp, string targetPath)
            => ConfirmationStore.HashPlan(
                "op=" + Operation,
                "template=" + (DocIdentity.NormalizePath(templatePath) ?? "-"),
                "template_stamp=" + (templateStamp ?? "-"),
                "target=" + (DocIdentity.NormalizePath(targetPath) ?? "-"));

        // WINDOWS PATHS ON ANY HOST. Revit runs on Windows, but these rules are also tested on
        // the Linux CI runner, where Path treats "C:\x\y.rvt" as one relative file name: rooted
        // false, no directory. A drive letter or a UNC prefix is rooted here wherever this runs.
        private static bool SafeRooted(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            if (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) return true;
            if (p.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            try { return Path.IsPathRooted(p); } catch { return false; }
        }
        private static string SafeExtension(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            int sep = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
            int dot = p.LastIndexOf('.');
            return dot > sep ? p.Substring(dot) : "";
        }
        private static string SafeDirectory(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            int sep = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
            return sep > 0 ? p.Substring(0, sep) : null;
        }
    }
}
