using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace Horizun.Revit.Core
{
    // Revit documents representing the same session document share a hash code.
    // Collisions conservatively protect unrelated documents instead of losing a mark.
    public static class OpenRecoveryDocuments
    {
        private static readonly OpenRecoveryIdentityRegistry Origins = new OpenRecoveryIdentityRegistry();
        public static void Mark(Document document, string originalPath)
        {
            if (document == null) return;
            Origins.Mark(document.GetHashCode(), originalPath);
        }
        public static bool IsMarked(Document document)
        {
            if (document == null) return false;
            try { return Origins.IsMarked(document.GetHashCode()); }
            catch { return true; }
        }
        public static string SaveRefusal(Document document, bool saveAs, bool confirmed, string target, bool overwrite = false)
        {
            if (document == null) return null;
            int identity;
            try { identity = document.GetHashCode(); }
            catch { return "Cannot identify the document safely; nothing was saved."; }
            if (!Origins.IsMarked(identity)) return null;
            string refusal = OpenRecoveryPolicy.SaveRefusal(true, saveAs, confirmed, Origins.Original(identity), target, overwrite);
            if (refusal != null) return refusal;
            // Existing files can be hard links or reparse-point aliases of the source.
            // Refuse all overwrites instead of trying to infer identity from a path string.
            if (System.IO.File.Exists(target) || System.IO.Directory.Exists(target))
                return "The destination already exists; recovered documents can only be saved to a new file.";
            return null;
        }
    }
}
