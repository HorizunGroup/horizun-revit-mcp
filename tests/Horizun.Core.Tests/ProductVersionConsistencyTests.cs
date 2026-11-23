using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ProductVersionConsistencyTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
        }

        [Fact]
        public void Registry_and_compiled_assembly_match_the_canonical_version()
        {
            string root = Root();
            string version = XDocument.Load(Path.Combine(root, "Directory.Build.props"))
                .Descendants("Version").Single().Value;
            Assert.False(string.IsNullOrWhiteSpace(version));
            var registry = JObject.Parse(File.ReadAllText(Path.Combine(root, ".mcp", "server.json")));
            Assert.Equal(version, (string)registry["version"]);
            var inventory = JObject.Parse(File.ReadAllText(Path.Combine(root, "docs", "inventory.json")));
            Assert.Equal(version, (string)inventory["code_candidate_version"]);
            string assemblyVersion = typeof(ProductVersionConsistencyTests).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
            Assert.Equal(version, assemblyVersion);
        }

        [Fact]
        public void Packaging_and_runtime_take_their_versions_from_the_shared_source()
        {
            string root = Root();
            string Read(string path) => File.ReadAllText(Path.Combine(root, path));
            foreach (string script in new[] { "scripts/build-mcpb.ps1", "scripts/generate-mcp-manifest.ps1", "scripts/pack.ps1" })
                Assert.Contains("Directory.Build.props", Read(script));
            Assert.Contains("New-HorizunMcpbManifest -Version $version", Read("scripts/build-mcpb.ps1"));
            Assert.Contains("version          = $Version", Read("scripts/mcpb-manifest.lib.ps1"));
            Assert.Contains("/DAppVersion=$version", Read("scripts/pack.ps1"));
            Assert.Contains("AppVersion={#AppVersion}", Read("installer/horizun-mcp.iss"));
            Assert.Contains("AssemblyInformationalVersionAttribute", Read("src/Horizun.Server/Program.cs"));
            Assert.Contains("AssemblyInformationalVersionAttribute", Read("src/Horizun.Revit/Core/Build.cs"));
            foreach (string project in new[] { "src/Horizun.Server/Horizun.Server.csproj", "src/Horizun.Revit/Horizun.Revit.csproj" })
            {
                var xml = XDocument.Load(Path.Combine(root, project));
                Assert.DoesNotContain(xml.Descendants(), e => e.Name.LocalName == "Version" || e.Name.LocalName == "VersionPrefix" || e.Name.LocalName == "VersionSuffix");
            }
        }
    }
}
