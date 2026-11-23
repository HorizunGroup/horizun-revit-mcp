// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The base-class libraries execute_python preloads (Core/PythonBclAssemblies.cs),
// proved against the same IronPython the add-in ships. This project runs on .NET 8,
// the runtime of Revit 2025-2026, which is exactly where the ImportErrors were:
// the negative control below shows the failure the preload removes.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Xunit;

namespace Horizun.Server.Tests
{
    public class PythonBclAssembliesTests
    {
        private const string Imports =
            "from System.Security.Cryptography import SHA256\n" +
            "from System.Text.RegularExpressions import Regex\n" +
            "from System.Linq import Enumerable\n" +
            "from System.Diagnostics import Process\n" +
            "from System.Collections.ObjectModel import ObservableCollection\n" +
            "from System.Collections.Generic import SortedSet, List\n" +
            "from System.Xml import XmlDocument\n" +
            "from System.Data import DataTable\n" +
            "from System.Drawing import Color\n" +
            "ok = Regex('a+').IsMatch('caat')\n";

        [Fact]
        public void Common_net_imports_work_once_the_anchors_are_loaded()
        {
            ScriptEngine engine = Python.CreateEngine();
            foreach (var asm in PythonBclAssemblies.Assemblies())
                engine.Runtime.LoadAssembly(asm);
            ScriptScope scope = engine.CreateScope();
            engine.Execute(Imports, scope);
            Assert.True((bool)scope.GetVariable("ok"));
        }

        [Fact]
        public void Without_them_the_first_import_fails_on_net8_which_is_the_field_error()
        {
            // Negative control: what the engine did before, with only System.Uri's
            // assembly loaded. If a future IronPython resolves these on its own this
            // test says so, and the preload becomes harmless rather than wrong.
            ScriptEngine engine = Python.CreateEngine();
            engine.Runtime.LoadAssembly(typeof(System.Uri).Assembly);
            var ex = Assert.ThrowsAny<Exception>(() =>
                engine.Execute("from System.Security.Cryptography import SHA256\n", engine.CreateScope()));
            Assert.Contains("SHA256", ex.Message);
        }

        [Fact]
        public void Each_library_is_loaded_once()
        {
            var names = new System.Collections.Generic.HashSet<string>();
            foreach (var asm in PythonBclAssemblies.Assemblies())
                Assert.True(names.Add(asm.FullName), asm.FullName + " listed twice");
            Assert.True(names.Count >= 5);
        }
    }
}
