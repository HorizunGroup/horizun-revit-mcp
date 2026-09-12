using System;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Horizun.Contracts;

namespace Horizun.Revit.Core
{
    /// <summary>One bounded read, shared by admission and execution. No Revit dependencies.</summary>
    public sealed class PythonSourceSnapshot
    {
        public string Code, Path, Encoding, Error;
        public bool ReadNow, NewlinesNormalized;
        public JArray Includes = new JArray();
        public string Sha256 => Code == null ? null : RequestFingerprint.Sha256Hex(Code);
        public string ExecutionSha256 => Code == null ? null : RequestFingerprint.Sha256Hex(new JObject
        { ["main"] = Sha256, ["includes"] = Includes, ["helpers_version"] = 1 }.ToString(Newtonsoft.Json.Formatting.None));

        public static PythonSourceSnapshot Resolve(JObject request)
        {
            var result = new PythonSourceSnapshot();
            try
            {
                string code = request.Value<string>("code"), path = request.Value<string>("code_path");
                bool hasCode = !string.IsNullOrWhiteSpace(code), hasPath = !string.IsNullOrWhiteSpace(path);
                if (hasCode == hasPath) throw new ArgumentException("Send exactly one of code or code_path.");
                if (hasCode)
                {
                    result.Code = code;
                    result.Path = request.Value<string>("code_origin_path");
                    if (request.Value<int?>("source_snapshot_version") == 1)
                    {
                        result.Encoding = request.Value<string>("source_encoding");
                        result.NewlinesNormalized = request.Value<bool?>("source_newlines_normalized") ?? false;
                        result.ReadNow = request.Value<bool?>("source_read_at_admission") ?? false;
                    }
                }
                else
                {
                    string root = request.Value<string>("scripts_root");
                    if (!string.IsNullOrEmpty(root))
                    {
                        bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
                        string rootWithoutPrefix = WithoutExtendedPrefix(root.Replace('/', '\\'));
                        bool fullyQualifiedWindows = rootWithoutPrefix.StartsWith(@"\\", StringComparison.Ordinal) ||
                            (rootWithoutPrefix.Length >= 3 && char.IsLetter(rootWithoutPrefix[0]) && rootWithoutPrefix[1] == ':' && rootWithoutPrefix[2] == '\\');
                        if (!System.IO.Path.IsPathRooted(root) || (windows && !fullyQualifiedWindows)) throw new ArgumentException("scripts_root must be absolute.");
                        if (System.IO.Path.IsPathRooted(path)) throw new ArgumentException("code_path must be relative when scripts_root is supplied.");
                        string resolvedRoot = FullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
                        path = FullPath(System.IO.Path.Combine(resolvedRoot, path));
                        var comparison = Environment.OSVersion.Platform == PlatformID.Win32NT ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                        if (!WithoutExtendedPrefix(path).StartsWith(WithoutExtendedPrefix(resolvedRoot), comparison))
                            throw new ArgumentException("code_path escapes scripts_root.");
                    }
                    result.Path = FullPath(path);
                    // Deny concurrent writes while taking the snapshot; later edits do not
                    // alter its identity or the admitted execution.
                    using (var stream = new FileStream(result.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var bytes = new MemoryStream())
                    {
                        int cap = checked(Contract.MaxScriptTextChars * 4 + 4);
                        byte[] buffer = new byte[8192]; int read;
                        while ((read = stream.Read(buffer, 0, Math.Min(buffer.Length, cap + 1 - (int)bytes.Length))) > 0)
                        {
                            bytes.Write(buffer, 0, read);
                            if (bytes.Length > cap) throw new ArgumentException("code_path exceeds the source byte limit.");
                        }
                        var decoded = PythonSourceText.Decode(bytes.ToArray());
                        if (!decoded.Ok) throw new ArgumentException(decoded.Error);
                        result.Code = decoded.Text; result.Encoding = decoded.Encoding;
                        result.NewlinesNormalized = decoded.NewlinesNormalized; result.ReadNow = true;
                    }
                }
                if (string.IsNullOrWhiteSpace(result.Code)) throw new ArgumentException("The script is empty.");
                if (result.Code.Length > Contract.MaxScriptTextChars) throw new ArgumentException("The script exceeds the source character limit.");
                if (request["includes"] != null && request["source_includes"] != null)
                    throw new ArgumentException("Cannot mix includes and an admitted source snapshot.");
                if (request["includes"] != null)
                {
                    if (!(request["includes"] is JArray paths) || paths.Count > 16) throw new ArgumentException("includes must be an array of at most 16 paths.");
                    foreach (var include in paths)
                    {
                        if (include.Type != JTokenType.String) throw new ArgumentException("Each include must be a path string.");
                        var child = Resolve(new JObject { ["code_path"] = include, ["scripts_root"] = request["scripts_root"] });
                        if (child.Error != null) throw new ArgumentException(child.Error);
                        result.Includes.Add(new JObject { ["path"] = child.Path, ["code"] = child.Code, ["sha256"] = child.Sha256 });
                    }
                }
                else if (request["source_includes"] != null)
                {
                    if (!(request["source_includes"] is JArray frozen) || frozen.Count > 16) throw new ArgumentException("Invalid include snapshot.");
                    result.Includes = (JArray)frozen.DeepClone();
                    foreach (var include in result.Includes)
                        if (include["code"]?.Type != JTokenType.String || (string)include["sha256"] != RequestFingerprint.Sha256Hex((string)include["code"]))
                            throw new ArgumentException("Include snapshot integrity mismatch.");
                }
                int totalChars = result.Code.Length;
                foreach (var include in result.Includes) totalChars = checked(totalChars + ((string)include["code"]).Length);
                if (totalChars > Contract.MaxScriptTextChars) throw new ArgumentException("Combined main source and includes exceed the source character limit.");
            }
            catch (Exception ex) { result.Error = "Cannot resolve Python source: " + ex.Message + ". Nothing ran."; }
            return result;
        }

        public JObject ExecutionRequest(JObject original)
        {
            if (Error != null) throw new InvalidOperationException(Error);
            var frozen = (JObject)original.DeepClone();
            frozen.Remove("code_path"); frozen.Remove("scripts_root");
            frozen.Remove("includes");
            frozen["source_includes"] = Includes.DeepClone();
            frozen["code"] = Code;
            if (Path != null) frozen["code_origin_path"] = Path;
            frozen["source_snapshot_version"] = 1;
            frozen["source_encoding"] = Encoding;
            frozen["source_newlines_normalized"] = NewlinesNormalized;
            frozen["source_read_at_admission"] = ReadNow;
            frozen["source_sha256"] = Sha256;
            frozen["execution_sha256"] = ExecutionSha256;
            frozen["helpers_version"] = 1;
            return frozen;
        }

        public static string FullPath(string path)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && path != null)
            {
                path = WithoutExtendedPrefix(path.Replace('/', '\\'));
                // Extended paths bypass Win32 dot-segment normalization. Normalize
                // BEFORE adding that prefix, including when a long relative child is
                // joined to a short scripts_root.
                bool driveAbsolute = path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\';
                bool uncAbsolute = path.StartsWith(@"\\", StringComparison.Ordinal);
                if (driveAbsolute || uncAbsolute)
                {
                    string root;
                    string remainder;
                    if (driveAbsolute) { root = path.Substring(0, 3); remainder = path.Substring(3); }
                    else
                    {
                        var unc = path.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                        if (unc.Length < 2 || unc[0] == "." || unc[0] == "?") throw new ArgumentException("Invalid UNC script path.");
                        root = @"\\" + unc[0] + @"\" + unc[1] + @"\";
                        remainder = string.Join(@"\", unc, 2, unc.Length - 2);
                    }
                    var parts = new List<string>();
                    foreach (string part in remainder.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (part == ".") continue;
                        if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                        else parts.Add(part);
                    }
                    path = root + string.Join(@"\", parts);
                }
            }
            // Extended Windows paths avoid MAX_PATH in the .NET Framework host too.
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && path != null && path.Length >= 248 &&
                !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                if (path.StartsWith(@"\\", StringComparison.Ordinal)) path = @"\\?\UNC\" + path.Substring(2);
                else if (path.Length > 2 && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) path = @"\\?\" + path.Replace('/', '\\');
            }
            return System.IO.Path.GetFullPath(path);
        }

        private static string WithoutExtendedPrefix(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        }
    }
}
