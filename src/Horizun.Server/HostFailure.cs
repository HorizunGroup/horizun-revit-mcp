using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
namespace Horizun.Server
{
    internal static class HostFailure
    {
        internal static JObject Detail(string tool, Exception ex, int timeoutMs)
        {
            string code = ex is TimeoutException ? ErrorCodes.TiempoAgotado : ErrorCodes.ErrorInterno;
            bool excel = tool == "horizun_excel_read_rows" || tool == "horizun_excel_write_rows";
            if (excel && ex is FileNotFoundException && ex.Message.StartsWith("Workbook not found", StringComparison.Ordinal)) code = ErrorCodes.ArchivoNoAccesible;
            else if (excel && ex.Message.StartsWith("No worksheet named", StringComparison.Ordinal)) code = ErrorCodes.ElementoNoEncontrado;
            return ErrorCodes.Detail(code, ex.GetType().Name, ex is TimeoutException ? (int?)timeoutMs : null);
        }
    }
}
