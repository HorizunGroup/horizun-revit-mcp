// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH .NET LIBRARIES A SCRIPT CAN IMPORT WITHOUT clr.AddReference.
//
// IronPython only resolves `from System.X import Y` against assemblies the engine
// has been told about. The engine used to know three: RevitAPI, RevitAPIUI and the
// one System.Uri lives in. On .NET Framework (Revit 2023-2024) that third one is
// System.dll and covers a lot; on .NET 8+ (Revit 2025-2027) it is
// System.Private.Uri and covers almost nothing - so ordinary lines an agent writes
// from memory fail before any Revit work, with errors that read like a bad script:
//
//   from System.Security.Cryptography import SHA256   -> ImportError: Cannot import name SHA256
//   from System.Text.RegularExpressions import Regex  -> ImportError: No module named RegularExpressions
//   from System.Linq import Enumerable                -> ImportError: No module named Linq
//
// Measured on IronPython 3.4.2 / .NET 8, 2026-10-27: all three fail without these
// references and import once they are loaded. Field logs carry the first one on
// real runs.
//
// Each anchor is a TYPE, not an assembly name: the compiler resolves it against the
// framework this add-in is built for, so the same list is right on net48 (where
// several of them share System.dll) and on net8/net10 (where each has its own
// assembly). Nothing here grants anything new - every one of these libraries is
// already loaded in, or loadable by, the Revit process; this only saves the script
// the clr.AddReference line. Windows Forms is deliberately absent: a script on the
// bridge's thread has nobody to click a MessageBox.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Horizun.Revit.Core
{
    public static class PythonBclAssemblies
    {
        /// <summary>One type per library, in the order they are loaded.</summary>
        public static readonly Type[] Anchors =
        {
            typeof(System.Uri),                                         // System (net48) / System.Private.Uri
            typeof(System.Linq.Enumerable),                             // System.Core / System.Linq
            typeof(System.Text.RegularExpressions.Regex),               // System / System.Text.RegularExpressions
            typeof(System.Diagnostics.Process),                         // System / System.Diagnostics.Process
            typeof(System.Security.Cryptography.SHA256),                // mscorlib / System.Security.Cryptography
            typeof(System.Collections.ObjectModel.ObservableCollection<>), // System / System.ObjectModel
            typeof(System.Collections.Generic.SortedSet<>),             // System / System.Collections
            typeof(System.Xml.XmlDocument),                             // System.Xml / System.Private.Xml
            typeof(System.Data.DataTable),                              // System.Data / System.Data.Common
            typeof(System.Drawing.Color),                               // System.Drawing / System.Drawing.Primitives
        };

        /// <summary>The distinct assemblies behind <see cref="Anchors"/>.</summary>
        public static IEnumerable<Assembly> Assemblies()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Type anchor in Anchors)
            {
                Assembly a = anchor.Assembly;
                if (seen.Add(a.FullName)) yield return a;
            }
        }
    }
}
