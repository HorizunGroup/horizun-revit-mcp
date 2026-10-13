using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    public static class PythonCollectorHazard
    {
        private static readonly Regex ThreeArguments = new Regex(
            @"\bFilteredElementCollector\s*\(\s*[^,()]+\s*,\s*[^,()]+\s*,\s*[^,()]+\s*\)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        public static bool HasViewLinkOverload(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            return ThreeArguments.IsMatch(PythonSourceMask.StripCommentsAndStrings(code));
        }
    }
}
