using System;
using System.IO;
using System.Reflection;
#if REVIT2025 || REVIT2026 || REVIT2027
using System.Runtime.Loader;
#endif

namespace Horizun.Revit.Core
{
    // Resolve only the private IronPython/DLR closure. Revit API, the add-in and
    // shared contracts must continue to use the host's normal load context.
    internal static class PythonDependencyIsolation
    {
        private static readonly string[] Names = {
            "IronPython", "IronPython.Modules", "IronPython.SQLite", "IronPython.Wpf",
            "Microsoft.Dynamic", "Microsoft.Scripting", "Microsoft.Scripting.Metadata"
        };
        private static bool installed;
        private static readonly object Gate = new object();
#if REVIT2025 || REVIT2026 || REVIT2027
        private static PrivatePythonContext context;
#endif

        internal static void Install()
        {
            lock (Gate)
            {
                if (installed) return;
                string directory = Path.GetDirectoryName(typeof(PythonDependencyIsolation).Assembly.Location);
#if REVIT2025 || REVIT2026 || REVIT2027
                context = new PrivatePythonContext(directory);
                AssemblyLoadContext.Default.Resolving += ResolveModern;
#else
                AppDomain.CurrentDomain.AssemblyResolve += ResolveLegacy;
#endif
                installed = true;
            }
        }

        private static string PrivatePath(string directory, AssemblyName request)
        {
            if (request == null || Array.IndexOf(Names, request.Name) < 0) return null;
            string path = Path.Combine(directory, request.Name + ".dll");
            if (!File.Exists(path)) return null;
            // Never accept a different DLR version merely because its simple name
            // matches. That is the exact failure this resolver is intended to avoid.
            AssemblyName candidate = AssemblyName.GetAssemblyName(path);
            return AssemblyName.ReferenceMatchesDefinition(request, candidate) &&
                   request.Version == candidate.Version ? path : null;
        }

#if REVIT2025 || REVIT2026 || REVIT2027
        private static Assembly ResolveModern(AssemblyLoadContext _, AssemblyName request)
        {
            string path = PrivatePath(context.Directory, request);
            return path == null ? null : context.LoadFromAssemblyPath(path);
        }

        private sealed class PrivatePythonContext : AssemblyLoadContext
        {
            private readonly string directory;
            internal string Directory => directory;
            internal PrivatePythonContext(string directory) : base("Horizun.Python", isCollectible: false)
            {
                this.directory = directory;
            }
            protected override Assembly Load(AssemblyName request)
            {
                string path = PrivatePath(directory, request);
                return path == null ? null : LoadFromAssemblyPath(path);
            }
        }
#else
        private static Assembly ResolveLegacy(object _, ResolveEventArgs args)
        {
            AssemblyName request = new AssemblyName(args.Name);
            string directory = Path.GetDirectoryName(typeof(PythonDependencyIsolation).Assembly.Location);
            string path = PrivatePath(directory, request);
            // Load from bytes, not LoadFrom: keep the private DLR outside the
            // load-from probing path used by unrelated Revit add-ins.
            return path == null ? null : Assembly.Load(File.ReadAllBytes(path));
        }
#endif
    }
}
