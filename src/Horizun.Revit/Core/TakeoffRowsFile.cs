// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_quantities mode='takeoff', rows_file=true: the COMPLETE reply, every
// row, written to the bridge's own folder so horizun_budget_compare can read it
// through model_rows_path.
//
// Why it exists (course rehearsal, 2026-10-03): budget_compare refuses a
// truncated takeoff, and rightly - a comparison over a prefix of the model
// prices a smaller building. A real structural model gave 905 rows, so the only
// route was to pass all of them through the agent's context, or to export them
// with Python. This file is the third route: the rows go to disk, and only a
// path, a count and a hash come back.
//
// The location is NOT caller-chosen. A takeoff is a read, and a read that can
// write to any path the agent names is an external write wearing a read's
// permission. <data root>/takeoffs is the bridge's own state, like the job
// records next to it.
//
// Revit-free: what is written, and that it round-trips through
// BudgetComparisonRules.ReadModelRows, is provable without a model open.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class TakeoffRowsFile
    {
        /// <summary>
        /// Sortable, collision-free: the UTC instant first so a folder listing reads as a
        /// history, eight hex characters of a fresh GUID so two takeoffs in one second
        /// do not overwrite each other.
        /// </summary>
        public static string FileName(DateTime utc, Guid nonce)
        {
            return "takeoff-" + utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) +
                   "-" + nonce.ToString("N").Substring(0, 8) + ".json";
        }

        /// <summary>
        /// The file's content: the inline reply with the row cap lifted. Every count that
        /// described the cap is rewritten to describe the file, because a file carrying
        /// truncated=true would be refused by budget_compare, and one carrying shown=200
        /// beside 905 rows would contradict itself.
        /// </summary>
        public static JObject Document(JObject reply, JArray allRows)
        {
            if (reply == null) throw new ArgumentNullException(nameof(reply));
            if (allRows == null) throw new ArgumentNullException(nameof(allRows));
            var doc = (JObject)reply.DeepClone();
            doc.Remove("rows_file");
            doc["rows"] = allRows.DeepClone();
            doc["rows_matching"] = allRows.Count;
            doc["shown"] = allRows.Count;
            doc["top"] = null;
            doc["truncated"] = false;
            doc["truncated_note"] = null;
            doc["rows_file_note"] = "This is the COMPLETE takeoff written by horizun_quantities rows_file=true: every " +
                                    "row, with no 'top' cap. Pass this file's path as horizun_budget_compare " +
                                    "model_rows_path.";
            return doc;
        }

        /// <summary>
        /// Write <paramref name="document"/> into <paramref name="directory"/> and return the
        /// rows_file block. Written to a temporary name and moved into place, so a reader
        /// never sees half a file under the final name. A failure is RETURNED, not thrown:
        /// the measurement already happened and is worth returning without the file.
        /// </summary>
        public static JObject Write(string directory, JObject document, DateTime utc, Guid nonce)
        {
            int rows = document?["rows"] is JArray a ? a.Count : 0;
            string path = null;
            string temp = null;
            try
            {
                if (string.IsNullOrWhiteSpace(directory)) throw new IOException("no takeoffs directory is configured.");
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, FileName(utc, nonce));
                byte[] bytes = new UTF8Encoding(false).GetBytes(document.ToString(Formatting.None));
                temp = path + ".partial";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path);
                temp = null;
                return new JObject
                {
                    ["written"] = true,
                    ["path"] = path,
                    ["rows"] = rows,
                    ["bytes"] = bytes.LongLength,
                    ["sha256"] = Sha256Hex(bytes),
                    ["format"] = "json: the complete horizun_quantities takeoff reply",
                    ["use"] = "horizun_budget_compare model_rows_path = this path. The inline 'rows' above are capped " +
                              "by 'top'; this file is not."
                };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                if (temp != null) { try { File.Delete(temp); } catch { /* the error below is the one that matters */ } }
                return new JObject
                {
                    ["written"] = false,
                    ["path"] = path,
                    ["rows"] = rows,
                    ["error"] = ex.Message,
                    ["means"] = "the takeoff above was measured, but the rows file was NOT written. budget_compare " +
                                "cannot read it; re-run with top >= rows_matching and pass the reply inline instead."
                };
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder(64);
                foreach (byte b in sha.ComputeHash(bytes)) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
