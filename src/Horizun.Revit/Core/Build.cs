// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// One version number, read from the assembly it is compiled into.
//
// The version was living as a hand-typed const in the server and nowhere in the
// plugin, which is how a support conversation turns into "which build is that?"
// with no answer. Now the .csproj carries it, the compiler stamps it into both
// assemblies, and this reads it back — so the number a user reports and the code
// they are running cannot drift apart.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.Reflection;

namespace Horizun.Revit.Core
{
    public static class Build
    {
        private static string _version;
        private static string _commit;

        /// <summary>
        /// The git commit this assembly was built from, or "unknown".
        ///
        /// A version number changes once a release; this changes with the code. It is
        /// what turns "what is actually loaded in that Revit?" into a question with an
        /// answer, asked of the running add-in rather than of whoever remembers what
        /// they deployed - which was the gap behind the acceptance report's note that
        /// a manifest hash identifies a FILE, not a SOURCE.
        ///
        /// A "-dirty" suffix means the working tree had uncommitted changes when this
        /// was built, so the sha names a commit this binary is NOT exactly.
        /// </summary>
        public static string Commit
        {
            get
            {
                if (_commit != null) return _commit;
                try
                {
                    var attr = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                        typeof(Build).Assembly, typeof(AssemblyInformationalVersionAttribute));
                    string v = attr != null ? attr.InformationalVersion : null;
                    int plus = string.IsNullOrEmpty(v) ? -1 : v.IndexOf('+');
                    string sha = plus > 0 && plus < v.Length - 1 ? v.Substring(plus + 1) : null;
                    _commit = string.IsNullOrEmpty(sha) ? "unknown" : sha;
                }
                catch { _commit = "unknown"; }
                return _commit;
            }
        }

        /// <summary>
        /// False when the build carried uncommitted changes, or when it cannot be
        /// determined. Never true on a guess: "unknown" is not clean.
        /// </summary>
        public static bool BuiltFromCleanTree
        {
            get
            {
                string c = Commit;
                return c != "unknown" && !c.EndsWith("-dirty", StringComparison.Ordinal);
            }
        }

        /// <summary>The informational version stamped by the build, e.g. "0.2.0". Never throws.</summary>
        public static string Version
        {
            get
            {
                if (_version != null) return _version;
                try
                {
                    Assembly asm = typeof(Build).Assembly;
                    var attr = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                        asm, typeof(AssemblyInformationalVersionAttribute));
                    string v = attr != null ? attr.InformationalVersion : null;
                    if (string.IsNullOrEmpty(v)) v = asm.GetName().Version != null ? asm.GetName().Version.ToString() : null;

                    // SourceLink appends "+<commit sha>"; the number is the useful part.
                    if (!string.IsNullOrEmpty(v))
                    {
                        int plus = v.IndexOf('+');
                        if (plus > 0) v = v.Substring(0, plus);
                    }
                    _version = string.IsNullOrEmpty(v) ? "unknown" : v;
                }
                catch
                {
                    _version = "unknown";
                }
                return _version;
            }
        }

        private static Newtonsoft.Json.Linq.JObject _assembly;

        /// <summary>
        /// The FILE Revit actually loaded, hashed: path, SHA-256, size and write time.
        ///
        /// WHY THE ADD-IN ANSWERS THIS AND NOT THE HARNESS. A live run used to hash
        /// %APPDATA%\Autodesk\Revit\&lt;year&gt;\Horizun\Horizun.Revit.dll - a path that
        /// does not exist (the real one has \Addins\ in it) - so every campaign
        /// recorded addin_sha256: null and nothing tied a result to the bytes that
        /// produced it. A development session moves the file somewhere else again,
        /// so no path a harness can guess is right for every run. The loaded
        /// assembly knows where it came from; this reports that, and an
        /// unreadable value stays null rather than becoming a guess.
        /// </summary>
        public static Newtonsoft.Json.Linq.JObject Assembly_
        {
            get
            {
                if (_assembly != null) return _assembly;
                var o = new Newtonsoft.Json.Linq.JObject
                {
                    ["path"] = null,
                    ["sha256"] = null,
                    ["bytes"] = null,
                    ["written_utc"] = null,
                    ["means"] = "the add-in assembly Revit loaded in THIS process, hashed here rather than " +
                                "guessed from a deployment path. A null field could not be read and is not a zero."
                };
                try
                {
                    Assembly asm = typeof(Build).Assembly;
                    string path = null;
                    try { path = asm.Location; } catch { }
                    if (!string.IsNullOrEmpty(path)) o["path"] = path;
                    if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    {
                        var info = new System.IO.FileInfo(path);
                        o["bytes"] = info.Length;
                        o["written_utc"] = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture);
                        using (var sha = System.Security.Cryptography.SHA256.Create())
                        using (System.IO.FileStream fs = System.IO.File.OpenRead(path))
                        {
                            byte[] h = sha.ComputeHash(fs);
                            var sb = new System.Text.StringBuilder(h.Length * 2);
                            foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                            o["sha256"] = sb.ToString();
                        }
                    }
                }
                catch { /* a field that could not be read stays null */ }
                _assembly = o;
                return _assembly;
            }
        }
    }
}
