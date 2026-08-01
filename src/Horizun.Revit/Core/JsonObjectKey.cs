namespace Horizun.Revit.Core
{
    /// <summary>
    /// JSON permits an empty object-property name, but several real MCP clients
    /// (notably Windows PowerShell 5.1 ConvertFrom-Json) cannot materialize one.
    /// Summaries use model data as keys, so normalize only the unusable blank
    /// case and preserve every meaningful Revit name verbatim.
    /// </summary>
    public static class JsonObjectKey
    {
        public static string Summary(string value) =>
            string.IsNullOrWhiteSpace(value) ? "(blank)" : value;
    }
}
