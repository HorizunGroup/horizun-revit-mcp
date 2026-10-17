using System.Text;

namespace Horizun.Server
{
    // Invariant globalization does not reliably decompose precomposed accents.
    internal static class TextFold
    {
        private const string Accented = "áàäâãåéèëêíìïîóòöôõúùüûñçÁÀÄÂÃÅÉÈËÊÍÌÏÎÓÒÖÔÕÚÙÜÛÑÇ";
        private const string Plain = "aaaaaaeeeeiiiiooooouuuuncaaaaaaeeeeiiiiooooouuuunc";

        internal static string Fold(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var result = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                int index = Accented.IndexOf(c);
                if (index >= 0) { result.Append(Plain[index]); continue; }
                if (c >= '\u0300' && c <= '\u036f') continue;
                result.Append(char.ToLowerInvariant(c));
            }
            return result.ToString();
        }
    }
}
