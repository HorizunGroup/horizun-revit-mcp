// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// horizun_budget_compare against DISPOSABLE files: a baseline workbook built here
// with the existing writer, a takeoff written as JSON, a report workbook the tool
// creates and this file reads back through the same OPC reader the tool used.
// Power BI is exercised in dry_run only - no token, no network - through the
// recording HttpClient PowerBiPush's own tests use.
//
// The properties that matter: the comparison always comes back, every destination
// reports on its own (a refused Excel destination beside a validated Power BI
// one), the sheet is a real workbook with a header and one row per code, and a
// replayed key writes nothing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class BudgetCompareTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _settingsRoot;
        private readonly string _savedRoot;

        public BudgetCompareTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-budget-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            // A PROFILE OF THIS SUITE'S OWN, not the developer's.
            //
            // Declaring a destination now needs permission_profile=full_write, enforced
            // per call inside the handler. Without this the destination tests below would
            // pass or fail according to what is in the machine's real settings.json - and
            // on a default install (safe_write) every one of them would refuse, which is
            // the CORRECT behaviour being reported as a broken test. The refusal itself is
            // asserted in ExternalDestinationGateTests, deliberately.
            _settingsRoot = Path.Combine(Path.GetTempPath(), "hz-budget-settings-" + Guid.NewGuid().ToString("N"));
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

        // ---- fixtures -----------------------------------------------------------

        /// <summary>A baseline workbook: header + lines, written with the shipped writer onto a minimal package.</summary>
        private string Baseline(params object[][] lines)
        {
            string path = Path.Combine(_dir, "baseline.xlsx");
            File.WriteAllBytes(path, ExcelWriteRows.MinimalWorkbook("Presupuesto"));
            var rows = new JArray { new JArray { "Codigo", "Descripcion", "Und", "Cantidad", "Precio", "Moneda" } };
            foreach (object[] line in lines)
            {
                var r = new JArray();
                foreach (object v in line) r.Add(v == null ? JValue.CreateNull() : JToken.FromObject(v));
                rows.Add(r);
            }
            ExcelWriteRows.Handle(new JObject
            {
                ["file_path"] = path, ["sheet"] = "Presupuesto", ["rows"] = rows,
                ["idempotency_key"] = "seed-" + Guid.NewGuid().ToString("N")
            }, ExcelTestLedger.New());
            return path;
        }

        private static JObject Row(string id, string code, double? volume, string state = "measured", string doc = "Host.rvt")
            => new JObject
            {
                ["element_id"] = id, ["document"] = doc, ["category"] = "Walls", ["type"] = "Generic",
                ["classification_code"] = code,
                ["quantities"] = new JObject
                {
                    ["volume"] = new JObject
                    {
                        ["value"] = volume.HasValue ? (JToken)volume.Value : JValue.CreateNull(),
                        ["state"] = state, ["unit"] = "m3", ["reason"] = state == "measured" ? null : "test"
                    }
                }
            };

        private static JObject TakeoffReply(params JObject[] rows) => new JObject
        {
            ["mode"] = "takeoff", ["truncated"] = false, ["rows_matching"] = rows.Length, ["shown"] = rows.Length,
            ["rows"] = new JArray(rows)
        };

        private static JObject BaselineArgs(string path) => new JObject
        {
            ["file_path"] = path, ["sheet"] = "Presupuesto", ["header_row"] = 1,
            ["columns"] = new JObject
            {
                ["code"] = "codigo", ["description"] = "Descripcion", ["unit"] = "Und",
                ["quantity"] = "Cantidad", ["unit_price"] = "Precio", ["currency"] = 6
            }
        };

        private static Func<JObject, JObject> DryRunPowerBi(RecordingHandler handler, out HttpClient client)
        {
            client = new HttpClient(handler);
            HttpClient captured = client;
            string dir = Path.Combine(Path.GetTempPath(), "hz-budget-pbi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return request => PowerBiPush.Handle(request, captured, new DurableCommandLedger(() => dir),
                                                 name => name == "HORIZUN_POWER_BI_ACCESS_TOKEN" ? "test-token" : null);
        }

        // ---- the comparison alone ---------------------------------------------------

        [Fact]
        public void Compares_a_takeoff_against_a_workbook_baseline_with_no_outputs_and_no_key()
        {
            string baseline = Baseline(
                new object[] { "A-1", "Muro", "m3", 15.0, 100.0, "COP" },
                new object[] { "B-1", "Losa", "m3", 10.0, null, "COP" },
                new object[] { "", "Subtotal", null, 25.0, null, null },
                new object[] { "D-1", "Viga", "m3", 4.0, 50.0, "COP" });
            JObject reply = BudgetCompare.Handle(new JObject
            {
                ["model_rows"] = TakeoffReply(Row("1", "A-1", 10), Row("2", "A-1", 5), Row("3", "B-1", 7), Row("4", "C-1", 1)),
                ["baseline"] = BaselineArgs(baseline)
            }, ExcelTestLedger.New(), _ => throw new InvalidOperationException("no destination declared"), CancellationToken.None);

            JObject comparison = (JObject)reply["comparison"];
            var byCode = comparison["lines"].OfType<JObject>().ToDictionary(l => (string)l["code"], l => l);
            Assert.Equal("unchanged", (string)byCode["A-1"]["status"]);
            Assert.Equal("modified", (string)byCode["B-1"]["status"]);
            Assert.Equal("added", (string)byCode["C-1"]["status"]);
            Assert.Equal("removed", (string)byCode["D-1"]["status"]);
            Assert.Equal("Muro", (string)byCode["A-1"]["description"]);
            Assert.Equal(1500.0, (double)byCode["A-1"]["price"]["model_amount"]);
            Assert.Equal("not_available", (string)byCode["B-1"]["price"]["state"]);
            Assert.Equal("baseline_only", (string)byCode["D-1"]["price"]["state"]);
            Assert.Equal(200.0, (double)byCode["D-1"]["price"]["baseline_amount"]);
            Assert.Equal(new[] { 2 }, byCode["A-1"]["trace"]["baseline_rows"].Select(t => (int)t).ToArray());
            Assert.Equal(new[] { "1", "2" }, byCode["A-1"]["trace"]["element_ids"].Select(t => (string)t).ToArray());

            JObject source = (JObject)reply["baseline_source"];
            Assert.Equal(3, (int)source["lines"]);
            Assert.Equal(1, (int)source["rows_skipped_blank_code"]);
            Assert.Equal("A", (string)source["columns"]["code"]["column"]);
            Assert.Equal("F", (string)source["columns"]["currency"]["column"]);
            Assert.NotNull((string)source["sha256"]);
            Assert.Empty((JArray)reply["destinations"]);
        }

        [Fact]
        public void The_takeoff_can_come_from_a_json_file()
        {
            string baseline = Baseline(new object[] { "A-1", "Muro", "m3", 1.0, null, null });
            string rowsPath = Path.Combine(_dir, "takeoff.json");
            File.WriteAllText(rowsPath, TakeoffReply(Row("1", "A-1", 1)).ToString());
            JObject reply = BudgetCompare.Handle(new JObject
            {
                ["model_rows_path"] = rowsPath, ["baseline"] = BaselineArgs(baseline)
            }, ExcelTestLedger.New(), _ => null, CancellationToken.None);
            Assert.Equal("model_rows_path", (string)reply["model_source"]["from"]);
            Assert.Equal("unchanged", (string)reply["comparison"]["lines"][0]["status"]);
        }

        // ---- refusals ---------------------------------------------------------------

        [Fact]
        public void Unknown_keys_missing_headers_and_a_truncated_takeoff_are_refused_before_anything_is_written()
        {
            string baseline = Baseline(new object[] { "A-1", "Muro", "m3", 1.0, null, null });
            var ledger = ExcelTestLedger.New();

            ToolRefusal unknown = Assert.Throws<ToolRefusal>(() => BudgetCompare.Handle(new JObject
            {
                ["model_rows"] = TakeoffReply(Row("1", "A-1", 1)), ["baseline"] = BaselineArgs(baseline), ["tolerance"] = 5
            }, ledger, _ => null, CancellationToken.None));
            Assert.Contains("tolerance is not a known key", unknown.Message);

            JObject badColumns = BaselineArgs(baseline);
            badColumns["columns"]["quantity"] = "Qty";
            ToolRefusal header = Assert.Throws<ToolRefusal>(() => BudgetCompare.Handle(new JObject
            {
                ["model_rows"] = TakeoffReply(Row("1", "A-1", 1)), ["baseline"] = badColumns
            }, ledger, _ => null, CancellationToken.None));
            Assert.Contains("matches no header", header.Message);
            Assert.Contains("Cantidad", header.Message);

            JObject truncated = TakeoffReply(Row("1", "A-1", 1));
            truncated["truncated"] = true;
            ToolRefusal trunc = Assert.Throws<ToolRefusal>(() => BudgetCompare.Handle(new JObject
            {
                ["model_rows"] = truncated, ["baseline"] = BaselineArgs(baseline)
            }, ledger, _ => null, CancellationToken.None));
            Assert.Contains("TRUNCATED", trunc.Message);

            ToolRefusal noKey = Assert.Throws<ToolRefusal>(() => BudgetCompare.Handle(new JObject
            {
                ["model_rows"] = TakeoffReply(Row("1", "A-1", 1)), ["baseline"] = BaselineArgs(baseline),
                ["outputs"] = new JObject { ["excel"] = new JObject { ["file_path"] = Path.Combine(_dir, "out.xlsx") } }
            }, ledger, _ => null, CancellationToken.None));
            Assert.Contains("idempotency_key is required", noKey.Message);
            Assert.False(File.Exists(Path.Combine(_dir, "out.xlsx")));
        }

        // ---- Excel destination --------------------------------------------------------

        [Fact]
        public void Excel_destination_creates_a_readable_workbook_with_header_and_one_row_per_code()
        {
            string baseline = Baseline(
                new object[] { "A-1", "Muro", "m3", 15.0, 100.0, "COP" },
                new object[] { "B-1", "Losa", "m2", 10.0, null, null });
            string output = Path.Combine(_dir, "report.xlsx");
            var ledger = ExcelTestLedger.New();
            JObject args = new JObject
            {
                ["model_rows"] = TakeoffReply(Row("1", "A-1", 12), Row("2", "B-1", 3)),
                ["baseline"] = BaselineArgs(baseline),
                ["outputs"] = new JObject { ["excel"] = new JObject { ["file_path"] = output, ["sheet"] = "Comparacion" } },
                ["idempotency_key"] = "run-" + Guid.NewGuid().ToString("N")
            };
            JObject reply = BudgetCompare.Handle(args, ledger, _ => null, CancellationToken.None);

            JObject excel = reply["destinations"].OfType<JObject>().Single();
            Assert.Equal("excel", (string)excel["destination"]);
            Assert.Equal("written", (string)excel["status"]);
            Assert.True((bool)excel["created"]);
            Assert.Equal(3, (int)excel["evidence"]["rows_written"]);
            Assert.True((bool)excel["evidence"]["verified"]);

            // Read it back through the SAME reader the tool used for the baseline.
            JObject read = ExcelReadRows.Handle(new JObject { ["file_path"] = output, ["sheet"] = "Comparacion" });
            Assert.Equal((string)excel["evidence"]["sha256"], (string)read["sha256"]);
            var rows = (JArray)read["rows"];
            Assert.Equal(3, rows.Count);
            Assert.Equal(BudgetComparisonRules.SheetHeader, rows[0].Select(c => (string)c).ToArray());
            int status = 0, code = 1, unit = 3, baselineQty = 4, modelQty = 5, delta = 6, amountDelta = 11, elements = 12;
            Assert.Equal("modified", (string)rows[1][status]);
            Assert.Equal("A-1", (string)rows[1][code]);
            Assert.Equal("m3", (string)rows[1][unit]);
            Assert.Equal(15.0, (double)rows[1][baselineQty]);
            Assert.Equal(12.0, (double)rows[1][modelQty]);
            Assert.Equal(-3.0, (double)rows[1][delta]);
            Assert.Equal(-300.0, (double)rows[1][amountDelta]);
            Assert.Equal(1, (int)rows[1][elements]);
            // The incompatible line: blanks where no number exists, never zeros.
            Assert.Equal("not_comparable", (string)rows[2][status]);
            Assert.True(((JArray)rows[2]).Count <= delta || rows[2][delta].Type == JTokenType.Null);
            Assert.True(((JArray)rows[2]).Count <= amountDelta || rows[2][amountDelta].Type == JTokenType.Null);

            // The same key again: replayed, nothing rewritten.
            string shaBefore = ExcelWriteRows.Sha256Hex(File.ReadAllBytes(output));
            JObject replay = BudgetCompare.Handle((JObject)args.DeepClone(), ledger, _ => null, CancellationToken.None);
            Assert.True((bool)replay["replayed"]);
            Assert.Equal("written", (string)replay["destinations"][0]["status"]);
            Assert.Equal(shaBefore, ExcelWriteRows.Sha256Hex(File.ReadAllBytes(output)));

            // A different comparison under the same key is a conflict, not a second workbook.
            JObject changed = (JObject)args.DeepClone();
            ((JArray)changed["model_rows"]["rows"]).Add(Row("3", "Z-1", 1));
            Assert.Throws<ToolRefusal>(() => BudgetCompare.Handle(changed, ledger, _ => null, CancellationToken.None));
        }

        [Fact]
        public void An_existing_file_is_skipped_under_refuse_and_backed_up_under_replace_while_power_bi_dry_run_still_reports()
        {
            string baseline = Baseline(new object[] { "A-1", "Muro", "m3", 1.0, 10.0, "COP" });
            string output = Path.Combine(_dir, "existing.xlsx");
            File.WriteAllText(output, "not a workbook, and not ours to destroy");
            var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            HttpClient client;
            Func<JObject, JObject> powerBi = DryRunPowerBi(handler, out client);
            using (client)
            {
                JObject args = new JObject
                {
                    ["model_rows"] = TakeoffReply(Row("1", "A-1", 1)),
                    ["baseline"] = BaselineArgs(baseline),
                    ["outputs"] = new JObject
                    {
                        ["excel"] = new JObject { ["file_path"] = output },
                        ["power_bi"] = new JObject { ["dataset_id"] = "11111111-1111-1111-1111-111111111111", ["table"] = "Budget" }
                    },
                    ["idempotency_key"] = "run-" + Guid.NewGuid().ToString("N")
                };
                JObject reply = BudgetCompare.Handle(args, ExcelTestLedger.New(), powerBi, CancellationToken.None);

                // The comparison is there regardless of what the destinations did.
                Assert.Equal("unchanged", (string)reply["comparison"]["lines"][0]["status"]);

                var destinations = reply["destinations"].OfType<JObject>().ToList();
                Assert.Equal(2, destinations.Count);
                JObject excel = destinations.Single(d => (string)d["destination"] == "excel");
                Assert.Equal("skipped", (string)excel["status"]);
                Assert.Contains("overwrite_policy", (string)excel["reason"]);
                Assert.Equal("not a workbook, and not ours to destroy", File.ReadAllText(output));

                JObject pbi = destinations.Single(d => (string)d["destination"] == "power_bi");
                Assert.Equal("skipped", (string)pbi["status"]);
                Assert.Contains("dry_run", (string)pbi["reason"]);
                Assert.True((bool)pbi["evidence"]["dry_run"]);
                Assert.Equal(1, (int)pbi["evidence"]["rows_validated"]);
                Assert.Equal(0, handler.Calls);

                // replace: the original survives as a backup and the report replaces it.
                JObject replace = (JObject)args.DeepClone();
                replace["outputs"]["excel"]["overwrite_policy"] = "replace";
                replace["idempotency_key"] = "run-" + Guid.NewGuid().ToString("N");
                JObject second = BudgetCompare.Handle(replace, ExcelTestLedger.New(), powerBi, CancellationToken.None);
                JObject replaced = second["destinations"].OfType<JObject>().Single(d => (string)d["destination"] == "excel");
                Assert.Equal("written", (string)replaced["status"]);
                Assert.False((bool)replaced["created"]);
                string backup = (string)replaced["replaced_backup_path"];
                Assert.True(File.Exists(backup));
                Assert.Equal("not a workbook, and not ours to destroy", File.ReadAllText(backup));
                JObject read = ExcelReadRows.Handle(new JObject { ["file_path"] = output });
                Assert.Equal("Comparison", (string)read["sheet"]);
                Assert.Equal(2, ((JArray)read["rows"]).Count);
            }
        }

        [Fact]
        public void A_failed_excel_write_is_reported_beside_a_validated_power_bi_destination_not_instead_of_it()
        {
            string baseline = Baseline(new object[] { "A-1", "Muro", "m3", 1.0, null, null });
            // A directory where the file should be: the workbook cannot be created there.
            string output = Path.Combine(_dir, "blocked.xlsx");
            Directory.CreateDirectory(output);
            var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            HttpClient client;
            Func<JObject, JObject> powerBi = DryRunPowerBi(handler, out client);
            using (client)
            {
                JObject reply = BudgetCompare.Handle(new JObject
                {
                    ["model_rows"] = TakeoffReply(Row("1", "A-1", 1)),
                    ["baseline"] = BaselineArgs(baseline),
                    ["outputs"] = new JObject
                    {
                        ["excel"] = new JObject { ["file_path"] = output, ["overwrite_policy"] = "replace" },
                        ["power_bi"] = new JObject { ["dataset_id"] = "11111111-1111-1111-1111-111111111111", ["table"] = "Budget", ["dry_run"] = true }
                    },
                    ["idempotency_key"] = "run-" + Guid.NewGuid().ToString("N")
                }, ExcelTestLedger.New(), powerBi, CancellationToken.None);

                var destinations = reply["destinations"].OfType<JObject>().ToList();
                Assert.Equal("failed", (string)destinations.Single(d => (string)d["destination"] == "excel")["status"]);
                Assert.Equal("skipped", (string)destinations.Single(d => (string)d["destination"] == "power_bi")["status"]);
                Assert.Contains("no global transaction", (string)reply["destinations_note"]);
                Assert.NotNull(reply["comparison"]);
            }
        }

        [Fact]
        public void A_power_bi_refusal_is_a_failed_destination_and_an_in_doubt_answer_is_reported_as_in_doubt()
        {
            string baseline = Baseline(new object[] { "A-1", "Muro", "m3", 1.0, null, null });
            JObject Args() => new JObject
            {
                ["model_rows"] = TakeoffReply(Row("1", "A-1", 1)),
                ["baseline"] = BaselineArgs(baseline),
                ["outputs"] = new JObject
                {
                    ["power_bi"] = new JObject { ["dataset_id"] = "11111111-1111-1111-1111-111111111111", ["table"] = "Budget", ["dry_run"] = false }
                },
                ["idempotency_key"] = "run-" + Guid.NewGuid().ToString("N")
            };

            JObject failed = BudgetCompare.Handle(Args(), ExcelTestLedger.New(),
                _ => throw new ToolRefusal("Power BI returned HTTP 400 Bad Request."), CancellationToken.None);
            JObject failedDest = (JObject)failed["destinations"][0];
            Assert.Equal("failed", (string)failedDest["status"]);
            Assert.Contains("HTTP 400", (string)failedDest["error"]);
            Assert.EndsWith("/powerbi", (string)failedDest["evidence"]["ledger_key"]);

            JObject doubt = BudgetCompare.Handle(Args(), ExcelTestLedger.New(),
                _ => throw new ToolRefusal("Power BI delivery ended without a trustworthy HTTP response. The durable key is now in_doubt."),
                CancellationToken.None);
            JObject doubtDest = (JObject)doubt["destinations"][0];
            Assert.Equal("in_doubt", (string)doubtDest["status"]);
            Assert.Contains("re-sent automatically", (string)doubtDest["evidence"]["note"]);
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
            public int Calls;
            public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) { _answer = answer; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                try { return Task.FromResult(_answer(request)); }
                catch (Exception ex) { return Task.FromException<HttpResponseMessage>(ex); }
            }
        }
    }
}
