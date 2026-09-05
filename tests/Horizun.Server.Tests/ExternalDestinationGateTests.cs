// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE DEFECT. horizun_budget_compare was classified ToolEffect.ExternalSideEffect
// because it CAN create a workbook and CAN push a Power BI table. Both are true.
// But the permission ladder decides admission on that enum, so the comparison
// itself - which reads an .xlsx, computes arithmetic and writes nothing at all -
// was hidden from read_only and safe_write and demanded full_write. A machine
// allowed to read a budget was refused the reading, because the same surface can
// also write one.
//
// Downgrading the tool would have been the opposite mistake: the destinations
// really are external, and full_write really is the rung that authorizes them.
// So the guarantee is in two halves and this file asserts BOTH:
//
//   ADMISSION      by ToolEffect.ExternalSideEffectOnRequest, every rung. Proved
//                  in SettingsEffectMatrixTests, next to the rest of the ladder.
//   THE DESTINATION by Settings.AllowsExternalSideEffect, INSIDE the handler,
//                  per call. Proved here, by calling the tool.
//
// The tests below run the real handler against real disposable files under a
// settings root of their own - never the machine's - so what they measure is the
// rule and not the developer's settings.json.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class ExternalDestinationGateTests : IDisposable
    {
        private const string ReadOnly = "read_only";
        private const string SafeWrite = "safe_write";
        private const string FullWrite = "full_write";
        private const string UnsafeCode = "unsafe_code";

        private readonly string _dir;
        private readonly string _settingsRoot;
        private readonly string _savedRoot;

        public ExternalDestinationGateTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settingsRoot = Path.Combine(Path.GetTempPath(), "hz-gate-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_settingsRoot);
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _settingsRoot);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_dir, true); } catch { }
            try { Directory.Delete(_settingsRoot, true); } catch { }
        }

        private void Profile(string profile)
            => File.WriteAllText(HorizunPaths.SettingsPath(), @"{""permission_profile"":""" + profile + @"""}");

        // ---- fixtures ---------------------------------------------------------

        private string Baseline()
        {
            string path = Path.Combine(_dir, "baseline.xlsx");
            File.WriteAllBytes(path, ExcelWriteRows.MinimalWorkbook("Presupuesto"));
            ExcelWriteRows.Handle(new JObject
            {
                ["file_path"] = path,
                ["sheet"] = "Presupuesto",
                ["rows"] = new JArray
                {
                    new JArray { "Codigo", "Descripcion", "Und", "Cantidad" },
                    new JArray { "A-1", "Muro", "m3", 15.0 }
                },
                ["idempotency_key"] = "seed-" + Guid.NewGuid().ToString("N")
            }, ExcelTestLedger.New());
            return path;
        }

        private static JObject Args(string baseline) => new JObject
        {
            ["model_rows"] = new JObject
            {
                ["mode"] = "takeoff", ["truncated"] = false, ["rows_matching"] = 1, ["shown"] = 1,
                ["rows"] = new JArray
                {
                    new JObject
                    {
                        ["element_id"] = "1", ["document"] = "Host.rvt", ["classification_code"] = "A-1",
                        ["quantities"] = new JObject
                        {
                            ["volume"] = new JObject
                            { ["value"] = 15.0, ["state"] = "measured", ["unit"] = "m3" }
                        }
                    }
                }
            },
            ["baseline"] = new JObject
            {
                ["file_path"] = baseline, ["sheet"] = "Presupuesto", ["header_row"] = 1,
                ["columns"] = new JObject { ["code"] = "Codigo", ["unit"] = "Und", ["quantity"] = "Cantidad" }
            }
        };

        private JObject Run(JObject args)
            => BudgetCompare.Handle(args, ExcelTestLedger.New(),
                                    request => new JObject { ["dry_run"] = true, ["rows_validated"] = request["rows"].Count() },
                                    CancellationToken.None);

        // ---- the reading that was being refused --------------------------------

        /// <summary>
        /// THE DEFECT, as a test. No outputs, nothing written, and the whole comparison
        /// comes back - at the two profiles that used to hide the tool entirely.
        /// </summary>
        [Theory]
        [InlineData(ReadOnly)]
        [InlineData(SafeWrite)]
        [InlineData(FullWrite)]
        [InlineData(UnsafeCode)]
        public void A_comparison_with_no_outputs_runs_at_every_profile(string profile)
        {
            Profile(profile);
            JObject reply = Run(Args(Baseline()));
            Assert.Equal("unchanged", (string)reply["comparison"]["lines"][0]["status"]);
            Assert.Empty((JArray)reply["destinations"]);
            Assert.Null((string)reply["idempotency_key"]);
        }

        // ---- and the destinations that must stay gated -------------------------

        /// <summary>
        /// The SAME call with an Excel destination, at the SAME profile: refused, naming
        /// the profile, and the workbook is not created. "Refused" without which setting
        /// refused it is a support call rather than an answer.
        /// </summary>
        [Theory]
        [InlineData(ReadOnly)]
        [InlineData(SafeWrite)]
        public void An_excel_destination_is_refused_below_full_write(string profile)
        {
            Profile(profile);
            string report = Path.Combine(_dir, "report.xlsx");
            JObject args = Args(Baseline());
            args["outputs"] = new JObject { ["excel"] = new JObject { ["file_path"] = report } };
            args["idempotency_key"] = Guid.NewGuid().ToString("N");

            ToolRefusal refusal = Assert.Throws<ToolRefusal>(() => Run(args));
            Assert.Contains("permission_profile=" + profile, refusal.Message);
            Assert.Contains(report, refusal.Message);
            Assert.Contains("without 'outputs'", refusal.Message);
            Assert.False(File.Exists(report), "the workbook must not exist: the call was refused before anything was written");
        }

        /// <summary>
        /// A Power BI destination, including a DRY RUN. It sends nothing, but its reply
        /// reports whether this machine has Power BI credentials configured, and a profile
        /// that forbids reaching outside the model forbids answering that too. One rule
        /// for a declared destination beats two rules split by a flag.
        /// </summary>
        [Theory]
        [InlineData(ReadOnly, true)]
        [InlineData(ReadOnly, false)]
        [InlineData(SafeWrite, true)]
        [InlineData(SafeWrite, false)]
        public void A_power_bi_destination_is_refused_below_full_write_dry_run_included(string profile, bool dryRun)
        {
            Profile(profile);
            JObject args = Args(Baseline());
            args["outputs"] = new JObject
            {
                ["power_bi"] = new JObject
                {
                    ["dataset_id"] = "11111111-2222-3333-4444-555555555555",
                    ["table"] = "Comparison",
                    ["dry_run"] = dryRun
                }
            };
            args["idempotency_key"] = Guid.NewGuid().ToString("N");

            ToolRefusal refusal = Assert.Throws<ToolRefusal>(() =>
                BudgetCompare.Handle(args, ExcelTestLedger.New(),
                                     _ => throw new InvalidOperationException("the push must never be reached"),
                                     CancellationToken.None));
            Assert.Contains("permission_profile=" + profile, refusal.Message);
            Assert.Contains("Power BI table 'Comparison'", refusal.Message);
        }

        /// <summary>
        /// NOTHING WAS READ, and the refusal says so truthfully. The baseline points at a
        /// file that does not exist: if the gate ran after the read, this would be a
        /// FileNotFoundException instead - which is how you tell an argument check placed
        /// before the work from one placed after it.
        /// </summary>
        [Fact]
        public void The_destination_is_refused_before_the_baseline_is_read()
        {
            Profile(ReadOnly);
            JObject args = Args(Path.Combine(_dir, "there-is-no-such-workbook.xlsx"));
            args["outputs"] = new JObject
            { ["excel"] = new JObject { ["file_path"] = Path.Combine(_dir, "report.xlsx") } };
            args["idempotency_key"] = Guid.NewGuid().ToString("N");

            ToolRefusal refusal = Assert.Throws<ToolRefusal>(() => Run(args));
            Assert.Contains("Nothing was read and nothing was written", refusal.Message);
        }

        /// <summary>Both destinations still work where the ladder authorizes them.</summary>
        [Theory]
        [InlineData(FullWrite)]
        [InlineData(UnsafeCode)]
        public void An_excel_destination_is_written_at_the_authorized_rungs(string profile)
        {
            Profile(profile);
            string report = Path.Combine(_dir, "report-" + profile + ".xlsx");
            JObject args = Args(Baseline());
            args["outputs"] = new JObject { ["excel"] = new JObject { ["file_path"] = report } };
            args["idempotency_key"] = Guid.NewGuid().ToString("N");

            JObject reply = Run(args);
            JObject destination = ((JArray)reply["destinations"]).OfType<JObject>().Single();
            Assert.Equal("written", (string)destination["status"]);
            Assert.True(File.Exists(report));
        }

        // ---- the classification and the enforcement cannot drift apart ---------

        /// <summary>
        /// EVERY tool carrying ToolEffect.ExternalSideEffectOnRequest must enforce its own
        /// destinations, or the effect is a hole in the ladder rather than a finer reading
        /// of it. The list is hand-kept ON PURPOSE: a second tool given this effect fails
        /// here until somebody writes down where its enforcement is, which is the point at
        /// which they will notice whether it has any.
        /// </summary>
        [Fact]
        public void Every_tool_that_reaches_outside_only_on_request_enforces_it_per_call()
        {
            var enforced = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["horizun_budget_compare"] = Path.Combine("src", "Horizun.Server", "BudgetCompare.cs")
            };

            var carrying = Contract.All
                .Where(c => c.Effect == ToolEffect.ExternalSideEffectOnRequest)
                .Select(c => c.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(enforced.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray(), carrying.ToArray());

            foreach (KeyValuePair<string, string> pair in enforced)
            {
                string source = File.ReadAllText(Path.Combine(RepositoryRoot(), pair.Value));
                Assert.True(source.IndexOf("Settings.AllowsExternalSideEffect", StringComparison.Ordinal) >= 0,
                    pair.Key + " is classified ExternalSideEffectOnRequest, which every permission_profile admits, " +
                    "and " + pair.Value + " never asks Settings.AllowsExternalSideEffect. That combination is a " +
                    "tool a read_only machine can use to write outside the model.");
            }
        }

        private static string RepositoryRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}
