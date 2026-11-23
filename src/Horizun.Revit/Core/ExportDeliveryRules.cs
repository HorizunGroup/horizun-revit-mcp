// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. No Revit.
//
// THE DELIVERY ARITHMETIC OF horizun_export format=pdf (4.2-N4: CT-P1-4c, CT-P1-4e, LP-P3-11,
// LP-P3-13a). Names, versions, which sheets go in which file, and how long an approval lives.
//
//   FolderOutput     output_path may be a FOLDER when the PDFs are not combined: the files then
//                    take the folder's name as their stem. (LP-P3-13a)
//   NextVersion      pdf_version "auto": one more than the highest {version} already in the
//                    folder under the same pattern. (CT-P1-4c)
//   PreviousVersions the files of OLDER versions of the same pattern, to be MOVED (never
//                    deleted) into the archive folder once the new version is verified.
//   Jobs             which file holds which sheets: one combined file, one per sheet, N parts
//                    (pdf_parts), or the combined set AND one file per sheet. (CT-P1-4c, LP-P3-11)
//   TokenTtl         a rehearsal over many sheets takes long to read and approve; the approval
//                    lives longer the more sheets it covers. (CT-P1-4e)
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Horizun.Revit.Core
{
    /// <summary>Filesystem plan for exports that can create their destination directory.</summary>
    internal sealed class ExportDirectoryPlan
    {
        private readonly string output;
        internal string Folder { get; }
        internal bool NeedsCreation { get; }
        internal string Fingerprint => ";create_directory=" + (NeedsCreation ? "1" : "0");

        internal ExportDirectoryPlan(string output, bool requireExisting)
        {
            this.output = output;
            Folder = Path.GetDirectoryName(output);
            if (string.IsNullOrEmpty(Folder))
                throw new DirectoryNotFoundException("The export must name a file in an output directory.");
            NeedsCreation = !Directory.Exists(Folder);
            if (!NeedsCreation) return;
            if (requireExisting)
                throw new DirectoryNotFoundException("The output directory does not exist: " + Folder + ". Nothing was written.");
            ValidateLocalDestination();
        }

        internal JObject Assumption(bool created) => AssumptionNotes.Row(
            (created ? "Created the output directory: " : "Will create the output directory: ") + Folder,
            "The requested directory does not exist and the export needs it.",
            "Set require_existing_folder=true to require an existing directory.");

        internal static TRefusal Apply<TRefusal>(ExportDirectoryPlan plan, Func<TRefusal> confirm,
            Func<TRefusal> stillTheSame, out JObject assumption, out IDisposable directoryGuard) where TRefusal : class
        {
            assumption = null;
            directoryGuard = null;
            TRefusal refusal = confirm() ?? stillTheSame();
            if (refusal == null && plan != null)
                directoryGuard = plan.PrepareDirectory(out assumption);
            return refusal;
        }

        private IDisposable PrepareDirectory(out JObject assumption)
        {
            assumption = null;
            if (!NeedsCreation)
            {
                if (!Directory.Exists(Folder))
                    throw new DirectoryNotFoundException("The output directory disappeared after planning: " + Folder);
                return null;
            }
            ValidateLocalDestination();
            assumption = Assumption(true);
            return CreateLocalDirectory();
        }

        private DirectoryGuard CreateLocalDirectory()
        {
            var chain = new Stack<string>();
            for (DirectoryInfo directory = new DirectoryInfo(Folder); directory != null; directory = directory.Parent)
                chain.Push(directory.FullName);
            var guard = new DirectoryGuard();
            var handles = guard.Handles;
            try
            {
                handles.Push(OpenDirectory(chain.Pop()));
                while (chain.Count > 0)
                {
                    string path = chain.Pop();
                    handles.Push(OpenOrCreateChild(handles.Peek(), path, out bool created));
                    if (chain.Count == 0 && !created)
                        throw new IOException("The output directory appeared after planning: " + Folder + ". Rehearse the export again.");
                }
                return guard;
            }
            catch
            {
                guard.Dispose();
                throw;
            }
        }

        // Keep every component open without sharing write or delete while the
        // exporter resolves the destination by path, including its ancestors.
        private sealed class DirectoryGuard : IDisposable
        {
            internal readonly Stack<SafeFileHandle> Handles = new Stack<SafeFileHandle>();

            public void Dispose()
            {
                while (Handles.Count > 0) Handles.Pop().Dispose();
            }
        }

        private static SafeFileHandle OpenDirectory(string path)
        {
            const uint openExisting = 3;
            const uint backupSemantics = 0x02000000, openReparsePoint = 0x00200000;
            SafeFileHandle handle = CreateFile(path, ReadDirectory, FileShare.Read, IntPtr.Zero,
                openExisting, backupSemantics | openReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw DirectoryError(path, error);
            }
            return ValidateOpenedDirectory(path, handle);
        }

        private static SafeFileHandle OpenOrCreateChild(SafeFileHandle parent, string path, out bool created)
        {
            // There are no intermediate names to resolve. The parent is already open,
            // and OPEN_REPARSE_POINT opens the child itself so it can be checked by handle.
            const uint caseInsensitive = 0x40;
            const uint openIf = 3, directoryFile = 1, openReparsePoint = 0x00200000, fileCreated = 2;
            string leaf = Path.GetFileName(path);
            ushort length = checked((ushort)(leaf.Length * sizeof(char)));
            IntPtr nameBuffer = Marshal.StringToHGlobalUni(leaf);
            IntPtr name = IntPtr.Zero;
            try
            {
                name = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NtName)));
                Marshal.StructureToPtr(new NtName { Length = length, MaximumLength = length, Buffer = nameBuffer }, name, false);
                var attributes = new NtObjectAttributes
                {
                    Length = Marshal.SizeOf(typeof(NtObjectAttributes)),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = name,
                    Attributes = caseInsensitive
                };
                int status = NtCreateFile(out SafeFileHandle handle, ReadDirectory, ref attributes, out var ioStatus,
                    IntPtr.Zero, 0, FileShare.Read, openIf, directoryFile | openReparsePoint, IntPtr.Zero, 0);
                GC.KeepAlive(parent);
                if (status < 0)
                {
                    handle?.Dispose();
                    throw DirectoryError(path, unchecked((int)RtlNtStatusToDosError(status)));
                }
                created = ioStatus.Information.ToUInt64() == fileCreated;
                return ValidateOpenedDirectory(path, handle);
            }
            finally
            {
                Marshal.FreeHGlobal(name);
                Marshal.FreeHGlobal(nameBuffer);
            }
        }

        private static SafeFileHandle ValidateOpenedDirectory(string path, SafeFileHandle handle)
        {
            const int fileAttributeTagInfo = 9;
            if (!GetFileInformationByHandleEx(handle, fileAttributeTagInfo, out var info, 8))
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw DirectoryError(path, error);
            }
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory)
                return handle;
            handle.Dispose();
            throw new IOException("The directory cannot be created through a file or redirected folder: " + path);
        }

        private const uint ReadDirectory = 1 | 0x80; // FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES

        private static IOException DirectoryError(string path, int error)
        {
            var cause = new Win32Exception(error);
            return new IOException("Could not secure the local export directory '" + path + "': " + cause.Message, cause);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DirectoryAttributes
        {
            public FileAttributes Attributes;
            public uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NtName
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NtObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NtIoStatus
        {
            public IntPtr Status;
            public UIntPtr Information;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref NtObjectAttributes attributes,
            out NtIoStatus ioStatus, IntPtr allocationSize, uint fileAttributes, FileShare share,
            uint disposition, uint options, IntPtr eaBuffer, uint eaLength);

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern uint RtlNtStatusToDosError(int status);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
            out DirectoryAttributes information, uint size);

        private void ValidateLocalDestination()
        {
            string root = Path.GetPathRoot(output);
            if (string.IsNullOrEmpty(root) || (Path.DirectorySeparatorChar == '\\' &&
                (root.Length != 3 || root[1] != ':' || root[2] != '\\' || !char.IsLetter(root[0]))))
                throw new IOException("A missing output directory can only be created on a local drive: " + Folder);
            DriveType drive = new DriveInfo(root).DriveType;
            if (drive != DriveType.Fixed && drive != DriveType.Removable && drive != DriveType.Ram)
                throw new IOException("A missing output directory can only be created on a local drive: " + Folder);

            string current = root;
            string[] parts = output.Substring(root.Length).Split(Path.DirectorySeparatorChar);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                string name = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (part.Length == 0 || part.EndsWith(".") || part.EndsWith(" ") ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    (Path.DirectorySeparatorChar == '\\' && (name == "CON" || name == "PRN" || name == "AUX" || name == "NUL" ||
                     (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
                      "123456789¹²³".IndexOf(name[3]) >= 0))))
                    throw new IOException("Invalid component in export destination: " + part);
                current = Path.Combine(current, part);
                if (i == parts.Length - 1) break;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The directory cannot be created through a file or redirected folder: " + current);
            }
        }
    }

    public sealed class PdfJobs
    {
        public string[] Paths;
        /// <summary>For each path, the indexes (into view_ids order) of the sheets it holds.</summary>
        public int[][] Groups;
    }

    public static class ExportDeliveryRules
    {
        public static string InvalidFormatMessage(string format)
        {
            string message = "format must be pdf, pdf_qa, dwg, dgn, dwfx, ifc, nwc, fbx, image, schedule_csv, dwg_layers, ifc_setup, gbxml, rfa, cobie or mesh_json.";
            if (new[] { "png", "jpg", "jpeg", "tif" }.Contains(format, StringComparer.OrdinalIgnoreCase))
                message += " use format=image.";
            return message;
        }
        public const string DefaultArchiveFolder = "_anteriores";
        public static string MeshArguments(JObject r)
        {
            foreach (var p in r.Properties())
                if (!new[] { "target_document", "format", "output_path", "categories", "include_links", "skip_directshape", "dry_run", "confirmation_token", "idempotency_key" }.Contains(p.Name))
                    return p.Name + " does not apply to mesh_json.";
            foreach (string name in new[] { "include_links", "skip_directshape", "dry_run" })
                if (r[name] != null && r[name].Type != JTokenType.Boolean) return name + " must be boolean.";
            if (r["categories"] != null && (!(r["categories"] is JArray cats) || cats.Count < 1 || cats.Count > 500 ||
                cats.Any(c => c.Type != JTokenType.String || !((string)c).StartsWith("OST_", StringComparison.Ordinal)) || cats.Values<string>().Distinct().Count() != cats.Count))
                return "categories must contain 1..500 distinct OST_ category names.";
            return null;
        }

        public static string MeshHash(string path)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        public static string VerifyMeshFile(string path, long expectedTriangles, string expectedHash)
        {
            try
            {
                if (expectedTriangles < 0 || expectedTriangles > long.MaxValue / 9) return "Invalid expected triangle count.";
                if (MeshHash(path) != expectedHash) return "Written mesh hash differs.";
                long values = 0; int arrays = 0; bool rootEnded = false;
                using (var text = File.OpenText(path))
                using (var reader = new JsonTextReader(text))
                {
                    if (!reader.Read() || reader.TokenType != JsonToken.StartObject) return "Mesh root must be an object.";
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonToken.EndObject && reader.Depth == 0) rootEnded = true;
                        if (reader.TokenType != JsonToken.PropertyName || (string)reader.Value != "triangles" || reader.Depth != 1) continue;
                        if (++arrays != 1 || !reader.Read() || reader.TokenType != JsonToken.StartArray) return "Mesh must have one flat triangles array.";
                        while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                        {
                            if (reader.TokenType != JsonToken.Integer && reader.TokenType != JsonToken.Float) return "Triangle coordinates must be numeric and flat.";
                            double value = Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture);
                            if (double.IsNaN(value) || double.IsInfinity(value)) return "Triangle coordinate is not finite.";
                            values++;
                        }
                        if (reader.TokenType != JsonToken.EndArray) return "Triangles array is incomplete.";
                    }
                }
                return rootEnded && arrays == 1 && values == expectedTriangles * 9 ? null : "Mesh JSON is incomplete or its triangle count differs.";
            }
            catch (Exception ex) { return "Mesh re-read failed: " + ex.Message; }
        }
        public const int MaxParts = 50;
        public static readonly TimeSpan BaseTtl = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(60);

        /// <summary>True when output_path names a folder rather than a .pdf file: no extension, or a trailing separator, or an existing directory.</summary>
        public static bool LooksLikeFolder(string output, Func<string, bool> directoryExists)
        {
            if (string.IsNullOrWhiteSpace(output)) return false;
            if (output.EndsWith("\\", StringComparison.Ordinal) || output.EndsWith("/", StringComparison.Ordinal)) return true;
            if (string.Equals(Path.GetExtension(output), ".pdf", StringComparison.OrdinalIgnoreCase)) return false;
            return string.IsNullOrEmpty(Path.GetExtension(output)) || (directoryExists != null && directoryExists(output));
        }

        /// <summary>The stem file for a folder output: folder\leaf.pdf. Throws when the folder has no usable name.</summary>
        public static string FolderStem(string folder)
        {
            string trimmed = folder.TrimEnd('\\', '/');
            string leaf = Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(leaf)) throw new ArgumentException("output_path names a drive root; name a folder inside it.");
            return Path.Combine(trimmed, leaf + ".pdf");
        }

        // ---- versions ---------------------------------------------------------------------

        private static Regex PatternRegex(string pattern)
        {
            string p = pattern.Trim();
            if (p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 4);
            var sb = new System.Text.StringBuilder("^");
            int i = 0;
            while (i < p.Length)
            {
                if (p[i] == '{')
                {
                    int close = p.IndexOf('}', i);
                    if (close < 0) { sb.Append(Regex.Escape(p.Substring(i))); break; }
                    switch (p.Substring(i + 1, close - i - 1))
                    {
                        case "version": sb.Append("(?<v>[0-9]+)"); break;
                        case "date": sb.Append("[0-9]{8}"); break;
                        case "index": sb.Append("[0-9]+"); break;
                        default: sb.Append(".+?"); break;
                    }
                    i = close + 1;
                }
                else { sb.Append(Regex.Escape(p[i].ToString())); i++; }
            }
            sb.Append("\\.pdf$");
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public static bool HasVersionToken(string pattern) => pattern != null && pattern.IndexOf("{version}", StringComparison.Ordinal) >= 0;

        /// <summary>Versions found in the file names under the pattern, by file.</summary>
        public static Dictionary<string, int> VersionsFound(string pattern, IEnumerable<string> fileNames)
        {
            Regex rx = PatternRegex(pattern);
            var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in fileNames ?? new string[0])
            {
                Match m = rx.Match(Path.GetFileName(name) ?? "");
                if (m.Success && m.Groups["v"].Success && int.TryParse(m.Groups["v"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int v))
                    found[name] = v;
            }
            return found;
        }

        /// <summary>1 when the folder holds no version of the pattern; otherwise the highest found plus one.</summary>
        public static int NextVersion(string pattern, IEnumerable<string> fileNames)
        {
            if (!HasVersionToken(pattern)) throw new ArgumentException("pdf_version 'auto' fills {version} in pdf_name_pattern; the pattern has none.");
            Dictionary<string, int> found = VersionsFound(pattern, fileNames);
            return found.Count == 0 ? 1 : found.Values.Max() + 1;
        }

        /// <summary>Files of the pattern with a version below the new one: what moves to the archive.</summary>
        public static List<string> PreviousVersions(string pattern, IEnumerable<string> fileNames, int newVersion)
            => VersionsFound(pattern, fileNames).Where(kv => kv.Value < newVersion).Select(kv => kv.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        public static string ArchiveFolderError(string name)
        {
            if (name == null) return null;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 60 || name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || name == "." || name == "..")
                return "pdf_archive_folder must be a plain folder name (no path separators), at most 60 characters.";
            return null;
        }

        // ---- which file holds which sheets -------------------------------------------------

        /// <summary>The path a part takes: stem + "-parte-i-de-N".</summary>
        public static string PartPath(string combinedPath, int part, int parts)
            => Path.Combine(Path.GetDirectoryName(combinedPath) ?? "", Path.GetFileNameWithoutExtension(combinedPath) + "-parte-" + part.ToString(CultureInfo.InvariantCulture) + "-de-" + parts.ToString(CultureInfo.InvariantCulture) + ".pdf");

        public static string SetPath(string combinedPath)
            => Path.Combine(Path.GetDirectoryName(combinedPath) ?? "", Path.GetFileNameWithoutExtension(combinedPath) + "-set.pdf");

        /// <summary>Contiguous, balanced chunks (the first ones one sheet larger when it does not divide).</summary>
        public static int[][] Split(int count, int parts)
        {
            int baseSize = count / parts, extra = count % parts, at = 0;
            var result = new int[parts][];
            for (int i = 0; i < parts; i++)
            {
                int size = baseSize + (i < extra ? 1 : 0);
                result[i] = Enumerable.Range(at, size).ToArray();
                at += size;
            }
            return result;
        }

        public static string ValidateJobOptions(bool combine, int parts, bool alsoPerSheet, int viewCount)
        {
            if (parts < 0 || parts == 1 || parts > MaxParts) return "pdf_parts must be 2.." + MaxParts + " (omit it for one combined file).";
            if (parts > 0 && !combine) return "pdf_parts splits ONE combined set into parts; with pdf_combine=false every sheet already has its own file.";
            if (parts > 0 && parts > viewCount) return "pdf_parts " + parts + " is more than the " + viewCount + " sheet(s) exported.";
            if (alsoPerSheet && !combine) return "pdf_also_per_sheet adds per-sheet files to a combined set; pdf_combine=false already writes them.";
            if (alsoPerSheet && parts > 0) return "Give pdf_parts OR pdf_also_per_sheet, not both. Nothing was exported.";
            return null;
        }

        /// <summary>
        /// The files this export writes and the sheets in each. combinedPath names the combined
        /// set (the first sheet's pattern name, or output_path); perSheetPaths has one path per sheet.
        /// </summary>
        public static PdfJobs Jobs(string combinedPath, string[] perSheetPaths, int viewCount, bool combine, int parts, bool alsoPerSheet)
        {
            string bad = ValidateJobOptions(combine, parts, alsoPerSheet, viewCount);
            if (bad != null) throw new ArgumentException(bad);
            int[] all = Enumerable.Range(0, viewCount).ToArray();
            PdfJobs jobs;
            if (!combine)
                jobs = new PdfJobs { Paths = perSheetPaths, Groups = all.Select(i => new[] { i }).ToArray() };
            else if (parts > 0)
            {
                int[][] chunks = Split(viewCount, parts);
                jobs = new PdfJobs { Paths = Enumerable.Range(1, parts).Select(i => PartPath(combinedPath, i, parts)).ToArray(), Groups = chunks };
            }
            else if (alsoPerSheet)
                jobs = new PdfJobs { Paths = new[] { SetPath(combinedPath) }.Concat(perSheetPaths).ToArray(), Groups = new[] { all }.Concat(all.Select(i => new[] { i })).ToArray() };
            else
                jobs = new PdfJobs { Paths = new[] { combinedPath }, Groups = new[] { all } };
            if (jobs.Paths.Length != jobs.Groups.Length) throw new ArgumentException("the export plan names " + jobs.Paths.Length + " file(s) for " + jobs.Groups.Length + " group(s).");
            var dup = jobs.Paths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ArgumentException("two exports would write the same file '" + dup.Key + "'. Nothing was exported.");
            return jobs;
        }

        /// <summary>The approval lives 10 minutes plus 30 seconds per file written, up to an hour.</summary>
        public static TimeSpan TokenTtl(int fileCount)
        {
            TimeSpan t = BaseTtl + TimeSpan.FromSeconds(30.0 * Math.Max(0, fileCount));
            return t > MaxTtl ? MaxTtl : t;
        }
    }
}
