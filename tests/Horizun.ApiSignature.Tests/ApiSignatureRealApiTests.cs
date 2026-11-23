using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.ApiSignature.Tests
{
    public sealed class ApiSignatureRealApiTests
    {
        private readonly ITestOutputHelper output;
        public ApiSignatureRealApiTests(ITestOutputHelper output) { this.output = output; }

        [Theory]
        [InlineData(2023, "System.Int32")]
        [InlineData(2025, "System.Int64")]
        public void ElementIdConstructorReflectsTheActualYear(int year, string preferredInteger)
        {
            using (var api = Open(year))
            {
                Type elementId = api.LoadFromAssemblyName("RevitAPI").GetType("Autodesk.Revit.DB.ElementId", true);
                string[] arguments = elementId.GetConstructors().Where(c => c.GetParameters().Length == 1)
                    .Select(c => c.GetParameters()[0].ParameterType.FullName).ToArray();
                output.WriteLine("Revit {0}: ElementId constructors = {1}", year, string.Join(", ", arguments));
                Assert.Contains(preferredInteger, arguments);
                if (year == 2023) Assert.DoesNotContain("System.Int64", arguments);
                else
                {
                    ConstructorInfo oldInt = elementId.GetConstructors().Single(c => c.GetParameters().Length == 1
                        && c.GetParameters()[0].ParameterType.FullName == "System.Int32");
                    Assert.Contains(oldInt.GetCustomAttributesData(), a => a.AttributeType.FullName == "System.ObsoleteAttribute");
                }
            }
        }

        [Theory]
        [InlineData(2023)]
        [InlineData(2025)]
        public void DocumentDeleteHasScalarAndGenericCollectionOverloads(int year)
        {
            using (var api = Open(year))
            {
                Assembly assembly = api.LoadFromAssemblyName("RevitAPI");
                Type document = assembly.GetType("Autodesk.Revit.DB.Document", true);
                MethodInfo[] methods = document.GetMethods().Where(m => m.Name == "Delete").ToArray();
                foreach (MethodInfo method in methods) output.WriteLine("Revit {0}: {1}", year, method);
                Assert.Equal(2, methods.Length);
                Assert.Contains(methods, m => m.GetParameters()[0].ParameterType.FullName == "Autodesk.Revit.DB.ElementId");
                Assert.Contains(methods, m => m.GetParameters()[0].ParameterType.IsGenericType
                    && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition().FullName == "System.Collections.Generic.ICollection`1"
                    && m.GetParameters()[0].ParameterType.GetGenericArguments()[0].FullName == "Autodesk.Revit.DB.ElementId");
            }
        }

        [Theory]
        [InlineData(2023, "System.Int32")]
        [InlineData(2025, "System.Int64")]
        public void CheckerReportsRealElementIdExactSignatureAndIronPythonHint(int year, string integer)
        {
            using (var api = Open(year))
            {
                Type elementId = api.LoadFromAssemblyName("RevitAPI").GetType("Autodesk.Revit.DB.ElementId", true);
                JObject result = Check(api, elementId, ".ctor", integer);
                Assert.Contains((JArray)result["matches"], row => (bool)row["exact_match"]
                    && ((string)row["signature"]).Contains("(" + integer + " "));
                string expectedHint = year == 2023 ? "Int32" : "Int64";
                Assert.Contains((JArray)result["ironpython_suggestions"], hint =>
                    ((string)hint).Contains("ElementId(" + expectedHint + "(value))"));
                if (year == 2025)
                    Assert.Contains((JArray)result["ironpython_suggestions"], hint =>
                        ((string)hint).Contains("ElementId(Int32(value))") && ((string)hint).Contains("obsolete"));
                Assert.True((bool)result["coverage_complete"]);
            }
        }

        [Theory]
        [InlineData(2023)]
        [InlineData(2025)]
        public void CheckerNamesNumericWideningWithoutExecutingIt(int year)
        {
            using (var api = Open(year))
            {
                Type elementId = api.LoadFromAssemblyName("RevitAPI").GetType("Autodesk.Revit.DB.ElementId", true);
                JObject result = Check(api, elementId, ".ctor", "System.Int16");
                Assert.NotEmpty((JArray)result["matches"]);
                Assert.All((JArray)result["matches"], row =>
                {
                    Assert.False((bool)row["exact_match"]);
                    Assert.Contains((JArray)row["conversions_required"], conversion =>
                        (string)conversion["from"] == "System.Int16"
                        && !string.IsNullOrEmpty((string)conversion["conversion"]));
                });
            }
        }

        [Fact]
        public void CheckerDoesNotPretendInt64NarrowsToThe2023Constructor()
        {
            using (var api = Open(2023))
            {
                Type elementId = api.LoadFromAssemblyName("RevitAPI").GetType("Autodesk.Revit.DB.ElementId", true);
                JObject result = Check(api, elementId, ".ctor", "System.Int64");
                Assert.Empty((JArray)result["matches"]);
                Assert.All((JArray)result["overloads"], row => Assert.NotEmpty((JArray)row["reasons"]));
            }
        }

        [Theory]
        [InlineData(2023)]
        [InlineData(2025)]
        public void CheckerDistinguishesCollectionInterfaceFromScalarDelete(int year)
        {
            using (var api = Open(year))
            {
                Type document = api.LoadFromAssemblyName("RevitAPI").GetType("Autodesk.Revit.DB.Document", true);
                JObject result = Check(api, document, "Delete", "System.Collections.Generic.ICollection[Autodesk.Revit.DB.ElementId]");
                Assert.Single((JArray)result["matches"]);
                Assert.True((bool)result["matches"][0]["exact_match"]);
                Assert.Equal(2, (int)result["candidate_count"]);
                Assert.Contains((JArray)result["overloads"], row => !(bool)row["accepted"] && ((JArray)row["reasons"]).Count > 0);
            }
        }

        [Theory]
        [InlineData(2023)]
        [InlineData(2025)]
        public void CheckerRecognizesGenericListAsAssignableButRejectsPythonList(int year)
        {
            using (var api = Open(year))
            {
                Type document = api.LoadFromAssemblyName("RevitAPI").GetType("Autodesk.Revit.DB.Document", true);
                JObject clr = Check(api, document, "Delete", "System.Collections.Generic.List[Autodesk.Revit.DB.ElementId]");
                Assert.Single((JArray)clr["matches"]);
                Assert.False((bool)clr["matches"][0]["exact_match"]);
                Assert.NotEmpty((JArray)clr["matches"][0]["conversions_required"]);
                JObject python = Check(api, document, "Delete", "list");
                Assert.Empty((JArray)python["matches"]);
                Assert.Contains("List[ElementId]", python.ToString());
            }
        }

        [Theory]
        [InlineData(2023)]
        [InlineData(2025)]
        public void CheckerIncludesTheExplicitlyNamedSpecialNameOverloads(int year)
        {
            using (var api = Open(year))
            {
                Type element = api.LoadFromAssemblyName("RevitAPI").GetType("Autodesk.Revit.DB.Element", true);
                MethodInfo[] accessors = element.GetMethods().Where(m => m.Name == "get_Parameter").ToArray();
                Assert.Equal(3, accessors.Length);
                Assert.All(accessors, method => Assert.True(method.IsSpecialName));
                JObject result = Check(api, element, "get_Parameter", "System.Guid");
                Assert.Equal(3, (int)result["candidate_count"]);
                Assert.Single((JArray)result["matches"]);
                Assert.True((bool)result["matches"][0]["exact_match"]);
                Assert.Contains("System.Guid", (string)result["matches"][0]["signature"]);
                Assert.True((bool)result["coverage_complete"]);
            }
        }

        private JObject Check(MetadataLoadContext api, Type target, string member, params string[] arguments)
        {
            Assembly revit = api.LoadFromAssemblyName("RevitAPI");
            var types = revit.GetTypes().Concat(api.CoreAssembly.GetTypes()).ToArray();
            JObject result = ApiSignatureRules.Check(target, member, arguments, types);
            output.WriteLine(result.ToString());
            return result;
        }

        // A separate universe per year prevents .NET Framework/Core type identity
        // confusion. PathAssemblyResolver only reads PE metadata, never Assembly.Load.
        private static MetadataLoadContext Open(int year)
        {
            string apiDirectory = Environment.GetEnvironmentVariable("HORIZUN_TEST_REVIT_" + year + "_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", "Revit " + year);
            string apiPath = Path.Combine(apiDirectory, "RevitAPI.dll");
            Assert.True(File.Exists(apiPath), "Required genuine Revit API missing: " + apiPath);
            IEnumerable<string> frameworkFiles;
            string core;
            if (year <= 2024)
            {
                string references = Environment.GetEnvironmentVariable("HORIZUN_TEST_NET48_REFERENCES")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages",
                        "microsoft.netframework.referenceassemblies.net48", "1.0.3", "build", ".NETFramework", "v4.8");
                Assert.True(File.Exists(Path.Combine(references, "mscorlib.dll")), "Required .NET 4.8 references missing: " + references);
                frameworkFiles = Directory.GetFiles(references, "*.dll").Concat(Directory.GetFiles(Path.Combine(references, "Facades"), "*.dll"));
                core = "mscorlib";
            }
            else
            {
                frameworkFiles = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator);
                core = "System.Private.CoreLib";
            }
            // Framework references win if Autodesk bundles a facade with the same name.
            var paths = frameworkFiles.Concat(Directory.GetFiles(apiDirectory, "*.dll"))
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            return new MetadataLoadContext(new PathAssemblyResolver(paths), core);
        }
    }
}
