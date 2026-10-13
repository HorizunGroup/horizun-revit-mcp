// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// One rule for the paths a client hands the server (backlog #23 and #41): a full
// path on a local drive, decided on the string before the first file-system call.
//
// No test here contacts a network share. The UNC spellings are judged by the pure
// LocalPath.Problem; the tools are driven with DEVICE paths (\\?\ and \\.\) onto
// local temporary files - a spelling the old checks let through to a real read or
// write without any network - and with drive- and root-relative paths. A refusal
// that names the path proves the check ran first: the same call without the check
// either succeeds on the local file or fails later with a different message.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Threading;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class LocalPathTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _settingsRoot;
        private readonly string _savedRoot;

        public LocalPathTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-localpath-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settingsRoot = Path.Combine(Path.GetTempPath(), "hz-localpath-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_settingsRoot);
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _settingsRoot);
            File.WriteAllText(HorizunPaths.SettingsPath(), @"{""permission_profile"":""full_write""}");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_dir, true); } catch { }
            try { Directory.Delete(_settingsRoot, true); } catch { }
        }

        private static string Device(string localPath) => @"\\?\" + localPath;

        private string Workbook()
        {
            string path = Path.Combine(_dir, "book-" + Guid.NewGuid().ToString("N") + ".xlsx");
            File.WriteAllBytes(path, ExcelWriteRows.MinimalWorkbook("Hoja1"));
            return path;
        }

        private static void AssertNamesTheRule(Exception ex)
            => Assert.Contains("local drive", ex.Message, StringComparison.Ordinal);

        // ---- the rule itself --------------------------------------------------------

        [Theory]
        [InlineData(@"\\server\share\x.xlsx")]
        [InlineData("//server/share/x.xlsx")]
        [InlineData(@"\/server\share\x.xlsx")]
        [InlineData(@"/\server\share\x.xlsx")]
        [InlineData(@"\\?\C:\temp\x.xlsx")]
        [InlineData(@"\\.\C:\temp\x.xlsx")]
        [InlineData(@"\\?\UNC\server\share\x.xlsx")]
        [InlineData(@"//?/UNC/server/share/x.xlsx")]
        [InlineData(@"\??\UNC\server\share\x.xlsx")]
        public void Unc_and_device_paths_are_refused_on_the_string(string path)
        {
            Assert.True(LocalPath.IsNetworkOrDevice(path));
            string problem = LocalPath.Problem(path, "file_path");
            Assert.NotNull(problem);
            Assert.Contains("UNC or device", problem);
        }

        [Theory]
        [InlineData("C:x.json")]
        [InlineData(@"c:..\x.json")]
        [InlineData(@"\x.bc3")]
        [InlineData("/x.bc3")]
        [InlineData(@"relative\x.csv")]
        [InlineData("x.csv")]
        [InlineData("")]
        public void Drive_relative_root_relative_and_relative_paths_are_refused(string path)
        {
            if (path == "/x.bc3" && !OperatingSystem.IsWindows()) return;   // the root path off Windows is a full path
            Assert.NotNull(LocalPath.Problem(path, "path"));
        }

        [Fact]
        public void A_full_local_path_passes_and_comes_back_normalised()
        {
            string p = Path.Combine(_dir, "sub", "..", "x.json");
            Assert.Null(LocalPath.Problem(p, "path"));
            Assert.Equal(Path.Combine(_dir, "x.json"), LocalPath.Require(p, "path", null));
        }

        // ---- #23: every reader and writer applies it --------------------------------

        [Fact]
        public void Excel_read_rows_refuses_a_device_path_before_reading()
        {
            string book = Workbook();
            Exception ex = Assert.ThrowsAny<Exception>(() => ExcelReadRows.Handle(new JObject { ["file_path"] = Device(book) }));
            AssertNamesTheRule(ex);
            Assert.Contains("UNC or device", ex.Message);
        }

        [Fact]
        public void Excel_write_rows_xlsx_refuses_a_device_path_and_leaves_the_workbook_alone()
        {
            string book = Workbook();
            byte[] before = File.ReadAllBytes(book);
            Exception ex = Assert.ThrowsAny<Exception>(() => ExcelWriteRows.Handle(new JObject
            {
                ["file_path"] = Device(book), ["rows"] = new JArray { new JArray { "a", 1 } },
                ["idempotency_key"] = Guid.NewGuid().ToString("N")
            }, ExcelTestLedger.New()));
            AssertNamesTheRule(ex);
            Assert.Equal(before, File.ReadAllBytes(book));
        }

        [Fact]
        public void Excel_write_rows_refuses_a_drive_relative_xlsx()
        {
            Exception ex = Assert.ThrowsAny<Exception>(() => ExcelWriteRows.Handle(new JObject
            {
                ["file_path"] = "C:hz-never-" + Guid.NewGuid().ToString("N") + ".xlsx", ["rows"] = new JArray { new JArray { "a" } },
                ["idempotency_key"] = Guid.NewGuid().ToString("N")
            }, ExcelTestLedger.New()));
            AssertNamesTheRule(ex);
        }

        [Fact]
        public void Csv_refuses_the_mixed_separator_unc_spelling()
        {
            Assert.Contains("UNC or device", ExcelWriteRows.CsvPathProblem(@"\/server\share\x.csv", new string[0]));
            Assert.Contains("UNC or device", ExcelWriteRows.CsvPathProblem(@"/\server\share\x.csv", new string[0]));
        }

        [Fact]
        public void Catalog_lookup_refuses_a_device_path_and_a_root_relative_one()
        {
            string csv = Path.Combine(_dir, "catalog.csv");
            File.WriteAllText(csv, "code,name\nA,Alpha\n");
            AssertNamesTheRule(Assert.Throws<ArgumentException>(() => CatalogLookup.ValidateCatalogPath(Device(csv))));
            AssertNamesTheRule(Assert.Throws<ArgumentException>(() => CatalogLookup.ValidateCatalogPath(@"\catalogs\x.csv")));
        }

        [Fact]
        public void Project_context_refuses_device_drive_relative_and_root_relative_paths()
        {
            string ctx = Path.Combine(_dir, "project-context.json");
            File.WriteAllText(ctx, "{\"schema_version\":1}");
            foreach (string p in new[] { Device(ctx), "C:project-context.json", @"\project-context.json" })
                AssertNamesTheRule(Assert.Throws<ToolRefusal>(() => ProjectContext.OptionalPath(new JObject { ["path"] = p })));
        }

        [Fact]
        public void Model_explain_does_not_open_a_device_project_context()
        {
            string ctx = Path.Combine(_dir, "project-context.json");
            File.WriteAllText(ctx, "{\"schema_version\":1}");
            JObject r = ModelExplainIso.Evaluate(Device(ctx));
            Assert.Equal("refused", (string)r["status"]);
            Assert.Contains("local drive", (string)r["why"]);
        }

        [Fact]
        public void Read_schedule_refuses_a_device_pdf_path_as_not_usable()
        {
            string pdf = Path.Combine(_dir, "missing.pdf");
            var ex = Assert.Throws<ToolRefusal>(() => ReadScheduleTool.Handle(
                Horizun.Core.Tests.ScheduleCardFixtures.Args(new JObject { ["file_path"] = Device(pdf) }), CancellationToken.None));
            AssertNamesTheRule(ex);
        }

        [Fact]
        public void Information_container_refuses_device_and_relative_paths_for_files_folders_and_the_context()
        {
            string file = Path.Combine(_dir, "HZ01-HRZ-ZZ-XX-M3-A-0001.pdf");
            File.WriteAllText(file, "x");
            JObject Run(JObject a) => InformationContainerTool.Handle(a, CancellationToken.None);
            var states = new JObject { ["wip"] = "01_WIP", ["shared"] = "02_SHARED" };

            AssertNamesTheRule(Assert.Throws<ToolRefusal>(() => Run(new JObject { ["operation"] = "verify", ["file_path"] = Device(file) })));
            AssertNamesTheRule(Assert.Throws<ToolRefusal>(() => Run(new JObject
                { ["operation"] = "inspect", ["root"] = Device(_dir), ["states"] = states })));
            AssertNamesTheRule(Assert.Throws<ToolRefusal>(() => Run(new JObject
                { ["operation"] = "inspect", ["states"] = new JObject { ["wip"] = Device(_dir) } })));
            AssertNamesTheRule(Assert.Throws<ToolRefusal>(() => Run(new JObject
                { ["operation"] = "inspect", ["root"] = @"\cde", ["states"] = states })));
            AssertNamesTheRule(Assert.Throws<ToolRefusal>(() => Run(new JObject
                { ["operation"] = "inspect", ["project_context_path"] = "C:project-context.json" })));
            AssertNamesTheRule(Assert.Throws<ToolRefusal>(() => InformationContainerTool.FilePaths(new JObject
                { ["file_paths"] = new JArray { Device(file) } })));
        }

        // ---- #41: the three places Path.IsPathRooted let a relative path through -----

        [Fact]
        public void Bc3_export_refuses_a_root_relative_and_a_drive_relative_destination()
        {
            foreach (string p in new[] { @"\hz-never.bc3", "C:hz-never.bc3" })
            {
                var ex = Assert.Throws<ToolRefusal>(() => BudgetCompare.Handle(new JObject
                {
                    ["operation"] = "export_bc3", ["model_rows"] = new JArray(), ["apu"] = "not an array",
                    ["bc3_path"] = p, ["idempotency_key"] = Guid.NewGuid().ToString("N")
                }, ExcelTestLedger.New(), null, CancellationToken.None));
                Assert.Contains("bc3_path", ex.Message);
                AssertNamesTheRule(ex);
            }
        }

        [Fact]
        public void Ids_from_loin_refuses_a_drive_relative_output_path()
        {
            string ctx = Path.Combine(_dir, "project-context.json");
            File.WriteAllText(ctx, "{\"schema_version\":1}");
            var ex = Assert.Throws<ToolRefusal>(() => LoinIds.Operation(new JObject
            {
                ["operation"] = "ids_from_loin", ["path"] = ctx, ["output_path"] = "C:out.ids"
            }, CancellationToken.None));
            Assert.Contains("output_path", ex.Message);
            AssertNamesTheRule(ex);
        }

        // ---- the budget comparison's inputs and its workbook destination -------------

        [Fact]
        public void Budget_compare_refuses_device_paths_for_rows_baseline_and_destination()
        {
            string rows = Path.Combine(_dir, "takeoff.json");
            File.WriteAllText(rows, "{\"rows\":[]}");
            JObject Baseline(string file) => new JObject
            {
                ["file_path"] = file, ["columns"] = new JObject { ["code"] = 1, ["unit"] = 2, ["quantity"] = 3 }
            };

            AssertNamesTheRule(Assert.Throws<ToolRefusal>(() => BudgetCompare.Handle(new JObject
            {
                ["model_rows_path"] = Device(rows), ["baseline"] = Baseline(Workbook())
            }, ExcelTestLedger.New(), _ => null, CancellationToken.None)));

            AssertNamesTheRule(Assert.Throws<ToolRefusal>(() => BudgetCompare.Handle(new JObject
            {
                ["model_rows"] = new JArray(), ["baseline"] = Baseline(Device(Workbook()))
            }, ExcelTestLedger.New(), _ => null, CancellationToken.None)));

            string dest = Path.Combine(_dir, "report.xlsx");
            AssertNamesTheRule(Assert.Throws<ToolRefusal>(() => BudgetCompare.Handle(new JObject
            {
                ["model_rows"] = new JArray(), ["baseline"] = Baseline(Workbook()),
                ["outputs"] = new JObject { ["excel"] = new JObject { ["file_path"] = Device(dest) } },
                ["idempotency_key"] = Guid.NewGuid().ToString("N")
            }, ExcelTestLedger.New(), _ => null, CancellationToken.None)));
            Assert.False(File.Exists(dest));
        }
    }
}
