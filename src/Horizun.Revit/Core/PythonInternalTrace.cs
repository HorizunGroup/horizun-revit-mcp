using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PythonInternalTrace
    {
        public static string RedactPaths(string text)
        {
            if (text == null) return null;
            string windows = Regex.Replace(text, @"(?i)(?:[a-z]:\\|\\\\)[^\r\n:]*[\\/]([^\\/\r\n:]+)", "$1");
            return Regex.Replace(windows, @"(?<= in )/[^\r\n:]*?/([^/\r\n:]+)", "$1");
        }

        public static JObject FromException(Exception ex)
        {
            // Exception messages may contain model data. Retain the diagnostic for the
            // reported cast failure; do not echo arbitrary exception text or arguments.
            string message = ex is InvalidCastException && ex.Message.Contains("JArray") && ex.Message.Contains("JToken")
                ? "Invalid cast: JArray to JToken." : "Exception message withheld: it may contain model data.";
            var lines = (ex.StackTrace ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Take(8).Select(x => RedactPaths(Regex.Replace(x, @"\([^)]*\)", "(...)")));
            return new JObject { ["type"] = ex.GetType().FullName, ["message"] = message, ["stack"] = new JArray(lines) };
        }

        public static JArray Advisories(JToken token)
        {
            var result = new JArray();
            if (token is JArray array)
                foreach (var item in array.Children()) result.Add(item.DeepClone());
            return result;
        }
    }
}
