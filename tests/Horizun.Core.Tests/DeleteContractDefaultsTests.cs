using System;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DeleteContractDefaultsTests
    {
        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        [Fact]
        public void Omitted_dry_run_is_published_as_rehearsal_for_ids_and_purge()
        {
            var contract = Contract.All.Single(c => c.Name == "horizun_delete_verified");
            JToken dryRun = contract.InputSchema["properties"]["dry_run"];

            Assert.True((bool)dryRun["default"]);
            Assert.Contains("TRUE in BOTH modes", (string)dryRun["description"],
                            StringComparison.Ordinal);

            string production = File.ReadAllText(Path.Combine(
                RepoRoot(), "src", "Horizun.Revit", "Commands", "DeleteCommand.cs"));
            Assert.Contains("bool dryRun = request[\"dry_run\"] == null || request.Value<bool>(\"dry_run\");",
                            production, StringComparison.Ordinal);
        }

        [Fact]
        public void Delete_mode_is_required_and_omission_never_selects_purge()
        {
            var contract = Contract.All.Single(c => c.Name == "horizun_delete_verified");
            var required = ((JArray)contract.InputSchema["required"]).Select(t => (string)t).ToArray();

            Assert.Contains("mode", required);
            Assert.Contains("Omission is refused", (string)contract.InputSchema["properties"]["mode"]["description"],
                            StringComparison.Ordinal);

            string production = File.ReadAllText(Path.Combine(
                RepoRoot(), "src", "Horizun.Revit", "Commands", "DeleteCommand.cs"));
            Assert.Contains("mode is REQUIRED", production, StringComparison.Ordinal);
            Assert.DoesNotContain("request[\"ids\"] != null ? \"ids\" : \"purge_unused\"",
                                  production, StringComparison.Ordinal);
        }
    }
}
