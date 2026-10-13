// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ONE rule for every path a client hands this server to read or write on the
// machine: a full path on a local drive letter, decided on the STRING, before any
// file-system call.
//
// Why the string and why first. A UNC path (\\host\share\x) is not only a remote
// file: the first File.Exists on it makes Windows open an SMB session to that
// host and authenticate as the signed-in user, which hands the host the user's
// NTLM hash. The refusal must therefore come before the first touch, and it must
// cover every spelling Windows treats as a UNC or device path: \\host, //host,
// mixed \/ and /\, the device namespaces \\?\ and \\.\ (which also reach
// \\?\UNC\host), and the NT object prefix \??\.
//
// Why Path.IsPathRooted is not the test. It answers true for C:x.json (relative
// to the current folder of drive C) and for \x.bc3 (relative to the current
// drive). Neither names a file by itself: what they open depends on the server's
// working folder, which is not the project's. Path.IsPathFullyQualified accepts
// the device and UNC forms. So the rule is spelled out: a drive letter, a colon
// and a separator.
//
// What this does NOT decide: a mapped drive letter that points at a share is the
// user's own connection, and a symbolic link is the user's own file; both are
// out of reach of a string check and out of this rule's scope.
// -----------------------------------------------------------------------------
using System;
using System.IO;

namespace Horizun.Server
{
    internal static class LocalPath
    {
        /// <summary>
        /// Why <paramref name="path"/> is not a full local path, as a sentence naming
        /// <paramref name="key"/>; or null when it is one. Pure: no I/O.
        /// </summary>
        internal static string Problem(string path, string key)
        {
            if (string.IsNullOrWhiteSpace(path)) return key + " is required.";
            if (IsNetworkOrDevice(path))
                return key + " must be a full path on a local drive (C:\\folder\\file); '" + path + "' is a UNC or device " +
                       "path, and opening one makes Windows sign in to that host as you.";
            if (!IsDriveQualified(path))
            {
                string kind = path.Length >= 2 && path[1] == ':' ? "relative to the current folder of drive " + path.Substring(0, 2)
                            : IsSeparator(path[0]) ? "relative to the current drive"
                            : "relative";
                return key + " must be an absolute path on a local drive (C:\\folder\\file); '" + path + "' is " + kind +
                       ", and this server's working folder is not the project's.";
            }
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            { return key + " is not a valid path: " + ex.Message; }
            if (!IsDriveQualified(full) || IsNetworkOrDevice(full))
                return key + " must be a full path on a local drive (C:\\folder\\file); '" + path + "' resolves to '" + full + "'.";
            return null;
        }

        /// <summary>
        /// The full path, or a <see cref="ToolRefusal"/> carrying <see cref="Problem"/>
        /// followed by <paramref name="tail"/> (e.g. " Nothing was read.").
        /// </summary>
        internal static string Require(string path, string key, string tail)
        {
            string problem = Problem(path, key);
            if (problem != null) throw new ToolRefusal(problem + (tail ?? ""));
            return Path.GetFullPath(path);
        }

        /// <summary>True for \\host, //host, \/host, /\host, \\?\, \\.\ and \??\ - every form that leaves the local drives.</summary>
        internal static bool IsNetworkOrDevice(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1])) return true;
            return path.Length >= 4 && IsSeparator(path[0]) && path[1] == '?' && path[2] == '?' && IsSeparator(path[3]);
        }

        /// <summary>
        /// A drive letter, a colon and a separator. Off Windows (the test container) there
        /// are no drive letters, and a path from the root ('/tmp/x') is the local full path.
        /// </summary>
        private static bool IsDriveQualified(string path)
            => (path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2])) ||
               (!OperatingSystem.IsWindows() && path.Length >= 1 && path[0] == '/');

        private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        private static bool IsSeparator(char c) => c == '\\' || c == '/';
    }
}
