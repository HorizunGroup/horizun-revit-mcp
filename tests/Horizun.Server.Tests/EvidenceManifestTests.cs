// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE COMMITTED EVIDENCE MAY NOT LIE AND MAY NOT LEAK.
//
// docs/evidence/live-matrix.json is the one durable record of the five-year
// live matrix the repository keeps: the full verify-live reports stay outside
// version control (artifacts/ is ignored; per docs/RELEASE-POLICY.md they are
// attached to each release), because they carry machine-local facts - absolute
// paths, process ids, fixture locations.
//
// Two properties make that summary trustworthy, and both are enforced here
// against the COMMITTED file, independently of the generator that wrote it:
//
//   1. It is green and internally coherent. A summary showing a failed year,
//      a probe count that disagrees with itself, or a dimension case that did
//      not pass has no business being versioned - in the tree it would read
//      exactly like proof.
//   2. It is sanitized. No drive letters, user directories, model file names
//      or process ids - the file must be publishable as-is.
//
// The file's ABSENCE is allowed: not every commit sits behind a completed
// matrix, and docs/RELEASE-POLICY.md governs when the matrix itself is
// mandatory. What is never allowed is a PRESENT file that is stale-shaped,
// ungreen or machine-local.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class EvidenceManifestTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "scripts", "verify-live.ps1")))
                d = d.Parent;
            Assert.True(d != null, "Could not locate the repository root (scripts/verify-live.ps1)");
            return d.FullName;
        }

        private static string ManifestPath() =>
            Path.Combine(RepoRoot(), "docs", "evidence", "live-matrix.json");

        [Fact]
        public void The_evidence_generator_exists_where_the_manifest_says_it_does()
        {
            // The manifest names its generator so a reader can reproduce it. That
            // pointer must not dangle, whether or not a manifest is committed yet.
            string generator = Path.Combine(RepoRoot(), "scripts", "generate-live-evidence.ps1");
            Assert.True(File.Exists(generator),
                "scripts/generate-live-evidence.ps1 is the documented producer of docs/evidence/live-matrix.json " +
                "and must exist in the tree.");
        }

        [Fact]
        public void A_committed_evidence_manifest_is_green_and_internally_coherent()
        {
            string path = ManifestPath();
            if (!File.Exists(path)) return; // absence is legitimate; a present file is held to the contract

            JObject doc = JObject.Parse(File.ReadAllText(path));
            // Schema 1 predates the planimetry surface; schema 2 carries it; schema 3
            // pins the exact committed harness; schema 4 additionally separates
            // correction and autonomous-production coverage for every year.
            // Historical manifests retain their original contract and are not upgraded
            // by assertion.
            int schema = (int)doc["schema"];
            Assert.True(schema >= 1 && schema <= 4, "unknown evidence schema " + schema);
            Assert.Matches("^[0-9a-f]{40}$", (string)doc["candidate_commit"]);
            Assert.Matches("^[0-9a-f]{64}$", (string)doc["server_sha256"]);
            Assert.Equal("scripts/generate-live-evidence.ps1", (string)doc["generator"]);
            if (schema >= 3)
            {
                Assert.Matches("^[0-9a-f]{40}$", (string)doc["harness_commit"]);
                Assert.Matches("^[0-9a-f]{40,64}$", (string)doc["harness_git_blob"]);
                Assert.Matches("^[0-9a-f]{64}$", (string)doc["harness_sha256"]);
            }

            var years = (JArray)doc["years"];
            Assert.Equal(new[] { 2023, 2024, 2025, 2026, 2027 },
                         years.Select(y => (int)y["revit_year"]).ToArray());

            var artifactHashes = years.Select(y => (string)y["artifact_sha256"]).ToArray();
            Assert.Equal(artifactHashes.Length, artifactHashes.Distinct(StringComparer.Ordinal).Count());

            foreach (JToken y in years)
            {
                string label = "Revit " + (string)y["revit_year"];
                Assert.Matches("^[0-9a-f]{64}$", (string)y["artifact_sha256"]);
                Assert.Matches("^[0-9a-f]{64}$", (string)y["addin_sha256"]);

                int probes = (int)y["probes"];
                Assert.True(probes > 0, label + ": a matrix of zero probes proves nothing.");
                Assert.Equal(probes, (int)y["actual_probes"]);
                Assert.Equal(probes, (int)y["passed"]);
                Assert.Equal(0, (int)y["failed"]);
                Assert.Equal(0, (int)y["unverified"]);
                Assert.Equal(0, (int)y["not_covered"]);

                foreach (string block in new[] { "dimension_cases", "detail_2d_cases" })
                {
                    int passed = (int)y[block]!["passed"];
                    int total = (int)y[block]!["total"];
                    Assert.True(total > 0, label + ": " + block + " must exist to be claimed.");
                    Assert.True(passed == total,
                        label + ": " + block + " says " + passed + "/" + total +
                        " - an ungreen matrix may not be versioned as evidence.");
                }

                if (schema >= 2)
                {
                    // The planimetry section: green, and exercising BOTH tools. "Planimetry
                    // is green" is two claims - the query read what was staged and the
                    // auditor judged it - and a year may not claim one over the other.
                    JObject plan = (JObject)y["planimetry"];
                    Assert.True(plan != null, label + ": schema 2 requires a planimetry block.");
                    int planPassed = (int)plan["passed"];
                    int planTotal = (int)plan["total"];
                    Assert.True(planTotal > 0, label + ": planimetry must exist to be claimed.");
                    Assert.Equal(planTotal, planPassed);
                    Assert.Equal(0, (int)plan["failed"]);
                    Assert.Equal(0, (int)plan["unverified"]);
                    Assert.Equal(0, (int)plan["not_covered"]);
                    foreach (string coverage in new[] { "query_coverage", "audit_coverage" })
                    {
                        int covPassed = (int)plan[coverage]!["passed"];
                        int covTotal = (int)plan[coverage]!["total"];
                        Assert.True(covTotal > 0, label + ": planimetry." + coverage + " exercises nothing.");
                        Assert.Equal(covTotal, covPassed);
                    }
                }
                if (schema >= 4)
                {
                    JObject fix = (JObject)y["fix_planimetry_cases"];
                    Assert.NotNull(fix);
                    Assert.Equal(23, (int)fix["total"]);
                    Assert.Equal((int)fix["total"], (int)fix["passed"]);

                    JObject production = (JObject)y["planimetry_production"];
                    Assert.NotNull(production);
                    Assert.Equal(5, (int)production["total"]);
                    Assert.Equal((int)production["total"], (int)production["passed"]);
                    JObject tools = (JObject)production["tools"];
                    Assert.Equal(1, (int)tools["horizun_pack_sheets"]);
                    Assert.Equal(2, (int)tools["horizun_plan_annotations"]);
                    Assert.Equal(1, (int)tools["horizun_manage_revisions"]);
                    Assert.Equal(1, (int)tools["horizun_capture_view"]);
                }
            }
        }

        [Fact]
        public void New_live_evidence_pins_one_clean_committed_harness()
        {
            string root = RepoRoot();
            string harness = File.ReadAllText(Path.Combine(root, "scripts", "verify-live.ps1"));
            string generator = File.ReadAllText(Path.Combine(root, "scripts", "generate-live-evidence.ps1"));

            foreach (string field in new[]
            {
                "harness_file", "harness_commit", "harness_git_blob", "harness_sha256",
                "harness_path_matches_repository", "harness_tracked_clean"
            })
                Assert.Contains(field, harness, StringComparison.Ordinal);

            Assert.Contains("The release gate harness is not pinned to a clean Git commit", harness,
                            StringComparison.Ordinal);
            Assert.Contains("schema           = 4", generator, StringComparison.Ordinal);
            Assert.Contains("predates harness provenance", generator, StringComparison.Ordinal);
            Assert.Contains("used harness", generator, StringComparison.Ordinal);
            Assert.Contains("current verify-live.ps1 SHA-256", generator, StringComparison.Ordinal);
            Assert.Contains("recorded harness blob", generator, StringComparison.Ordinal);
        }

        [Fact]
        public void A_committed_evidence_manifest_carries_no_machine_local_data()
        {
            string path = ManifestPath();
            if (!File.Exists(path)) return;

            string text = File.ReadAllText(path);
            foreach (string forbidden in new[]
            {
                @"[A-Za-z]:[\\/]",   // any absolute Windows path
                @"(?i)users[\\/]",   // a user-profile directory, however reached
                "(?i)onedrive",
                "(?i)%userprofile%",
                "(?i)appdata",
                @"(?i)\.rvt\b",      // model files are project data, never evidence
                @"(?i)\.rfa\b",
                "\"pid\""
            })
            {
                Assert.False(Regex.IsMatch(text, forbidden),
                    "docs/evidence/live-matrix.json matches the forbidden pattern '" + forbidden +
                    "' - machine-local or project data must never be versioned as evidence.");
            }
        }
    }
}
