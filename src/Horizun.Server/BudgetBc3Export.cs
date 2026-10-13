// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_budget_compare operation=export_bc3 - HOST-RESIDENT, no Revit API.
// Writes a FIEBDC-3 (.bc3) budget from a horizun_quantities TAKEOFF and the
// caller's own APU table (unit prices per code). The per-code quantity is
// chosen by the SAME rules the comparison uses (Core/BudgetComparisonRules:
// by unit, an explicit quantity_field, declared conversions only, partial
// coverage refused unless opted into), so an exported line and a compared line
// can never disagree about what the model measured.
//
// ALL OR NOTHING. A code the takeoff carries and the APU does not price is
// refused - a price is never invented - and so is a code whose quantity could
// not be established; the refusal names every such code and nothing is written.
// A .bc3 missing lines is a smaller budget wearing the project's name.
//
// VERIFIED FROM DISK: the bytes are written to a temporary file beside the
// target, READ BACK, decoded as windows-1252 and held to the budget by
// Core/Bc3Rules.Verify (~V, ~C, ~D, ~M, totals); only then is the file moved
// into place (never over an existing one) and its SHA-256 re-read against the
// verified bytes.
//
// ONCE PER KEY. The call requires an idempotency_key and claims it in the
// durable ledger before the file is touched: a retry after a lost reply gets the
// recorded reply back - after the file it names is re-hashed on disk - and
// writes nothing; a refusal once claimed is the recorded answer for that key.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class BudgetBc3Export
    {
        internal static JObject Handle(JObject args, DurableCommandLedger ledger, CancellationToken cancellationToken, DateTime? today = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (JProperty p in args.Properties())
                if (Array.IndexOf(new[] { "operation", "model_rows", "model_rows_path", "mapping", "apu", "bc3_path", "idempotency_key" }, p.Name) < 0)
                    throw new ToolRefusal(p.Name + " is not applicable to operation=export_bc3 (known: model_rows, model_rows_path, mapping, " +
                                          "apu, bc3_path, idempotency_key). Refused rather than ignored. Nothing was written.");

            string profileRefusal;
            if (!Settings.AllowsExternalSideEffect(out profileRefusal))
                throw new ToolRefusal("export_bc3 writes a file, and that needs the profile: " + profileRefusal + " Nothing was read or written.");

            string key = (string)args["idempotency_key"];
            if (string.IsNullOrWhiteSpace(key))
                throw new ToolRefusal("idempotency_key is required for export_bc3: a budget written twice is two budgets. Generate a new UUID for " +
                                      "each deliberate export and keep it unchanged only for retries. Nothing was written.");
            if (key.Length > 200) throw new ToolRefusal("idempotency_key must be at most 200 characters. Nothing was written.");

            string path = (string)args["bc3_path"];
            if (string.IsNullOrWhiteSpace(path))
                throw new ToolRefusal("bc3_path must be an absolute path. Nothing was written.");
            path = LocalPath.Require(path, "bc3_path", " Nothing was written.");
            if (!string.Equals(Path.GetExtension(path), ".bc3", StringComparison.OrdinalIgnoreCase))
                throw new ToolRefusal("bc3_path must end in .bc3. Nothing was written.");
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!Directory.Exists(dir)) throw new ToolRefusal("the folder of bc3_path does not exist: " + dir + ". Nothing was written.");

            string problem;
            BudgetComparisonMapping mapping = BudgetComparisonRules.ReadMapping(args["mapping"], out problem);
            if (mapping == null) throw new ToolRefusal(problem + " Nothing was written.");

            List<BudgetComparisonRules.BaselineLine> apu = ReadApu(args["apu"]);
            List<BudgetComparisonRules.ModelRow> rows = ReadRows(args, mapping.CodeField);

            // The comparison needs a baseline quantity to reach the unit selection; the APU has
            // none, so a zero is supplied. It is internal, never exported and never reported: only
            // the model side of each line (selected quantity, coverage) is used below.
            JObject comparison = BudgetComparisonRules.Compare(rows, apu, mapping);

            var budget = new Bc3Budget { Date = (today ?? DateTime.UtcNow).Date };
            var refused = new JArray();
            var unused = new JArray();
            var report = new JArray();
            foreach (JObject line in comparison["lines"].OfType<JObject>())
            {
                string code = (string)line["code"];
                string status = (string)line["status"];
                if (status == BudgetLineStatus.Removed) { unused.Add(code); continue; }
                if (status == BudgetLineStatus.Added)
                { refused.Add(new JObject { ["code"] = code, ["reason"] = "no unit price in apu: a price is never invented." }); continue; }
                JObject selected = line["model"]?["selected"] as JObject;
                if (status == BudgetLineStatus.NotComparable || selected == null)
                { refused.Add(new JObject { ["code"] = code, ["reason"] = (string)line["reason"], ["detail"] = line["detail"] }); continue; }

                string qName = (string)selected["quantity_name"];
                double factor = (double)selected["conversion_factor"];
                BudgetComparisonRules.BaselineLine a = apu.First(x => x.Code == code);
                var bl = new Bc3Line { Code = code, Unit = a.Unit, Description = a.Description, UnitPrice = a.UnitPrice.Value };
                foreach (BudgetComparisonRules.ModelRow r in rows.Where(x => (x.Code ?? "").Trim() == code))
                {
                    BudgetComparisonRules.QuantityReading q;
                    if (!r.Quantities.TryGetValue(qName, out q) || q.State != QuantityState.Measured || !q.Value.HasValue) continue;
                    string comment = (string.IsNullOrEmpty(r.LinkInstanceId) ? "" : "link " + r.LinkInstanceId + " ") + "id " + r.ElementId;
                    string bad = Bc3Rules.CheckText(comment, "the measurement comment of element " + r.ElementId);
                    if (bad != null) throw new ToolRefusal(bad + " Nothing was written.");
                    bl.Measures.Add(new Bc3Measure { Comment = comment, Value = q.Value.Value * factor });
                }
                bl.Quantity = bl.Measures.Sum(m => m.Value);
                // The takeoff total the comparison computed, held against the sum of the lines about
                // to be written: two readings of the same rows that disagree are a defect, not a rounding.
                double expected = (double)selected["quantity_in_baseline_unit"];
                if (Math.Abs(expected - bl.Quantity) > 1e-6 * Math.Max(1, Math.Abs(expected)))
                    throw new ToolRefusal("internal disagreement for " + code + ": the takeoff sums to " + expected + " and its element lines to " +
                                          bl.Quantity + ". Nothing was written.");
                budget.Lines.Add(bl);
                report.Add(new JObject
                {
                    ["code"] = code, ["unit"] = bl.Unit, ["unit_price"] = bl.UnitPrice, ["quantity"] = Math.Round(bl.Quantity, 6),
                    ["amount"] = Math.Round(bl.Quantity * bl.UnitPrice, 2), ["elements"] = bl.Measures.Count,
                    ["quantity_name"] = qName, ["conversion_factor"] = factor, ["coverage"] = selected["coverage"]
                });
            }
            if (refused.Count > 0)
                throw new ToolRefusal("export_bc3 refused " + refused.Count + " code(s), so nothing was written - a .bc3 missing lines is a " +
                                      "smaller budget: " + refused.ToString(Formatting.None));
            if (budget.Lines.Count == 0)
                throw new ToolRefusal("no takeoff code matched the apu table; there is nothing to export. Nothing was written.");

            string text = Bc3Rules.Write(budget);
            byte[] bytes = Bc3Rules.Encode1252(text, out problem);
            if (bytes == null) throw new ToolRefusal("the budget could not be encoded: " + problem + " Nothing was written.");

            cancellationToken.ThrowIfCancellationRequested();
            // The claim covers the whole call, so a retry after a lost reply finds the recorded
            // answer (its file re-hashed on disk) before the never-overwrite rule refuses its own file.
            string fingerprint = RequestFingerprint.OfOperation(BudgetCompare.ToolName, "export_bc3", args, "idempotency_key");
            DurableCommandDecision decision = ledger.Claim(key, BudgetCompare.ToolName, fingerprint);
            if (decision.Outcome == DurableCommandOutcome.Replay) return ReplayExport(decision.ReplayResult);
            if (!decision.IsFresh) throw new ToolRefusal(decision.Message);

            bool moved = false;
            try
            {
                JObject reply = WriteVerified(path, bytes, budget, rows, report, unused, key, out moved);
                ledger.Complete(decision, CommandResult.Ok(reply));
                return reply;
            }
            catch (Exception ex) when (!moved)
            {
                // Nothing reached bc3_path, so this refusal IS the answer for the key. Once the file
                // is in place a failure is left in doubt instead, and the ledger names it on a retry.
                ledger.Complete(decision, CommandResult.Fail(ex.Message));
                throw;
            }
        }

        private static JObject WriteVerified(string path, byte[] bytes, Bc3Budget budget, List<BudgetComparisonRules.ModelRow> rows,
                                             JArray report, JArray unused, string key, out bool moved)
        {
            moved = false;
            if (File.Exists(path))
                throw new ToolRefusal("bc3_path already exists and is never overwritten: " + path + ". Choose a new name and a new idempotency_key. Nothing was written.");
            string temp = path + ".horizun-" + Guid.NewGuid().ToString("N") + ".tmp";
            JObject counts;
            try
            {
                File.WriteAllBytes(temp, bytes);
                List<string> problems = Bc3Rules.Verify(Bc3Rules.Decode1252(File.ReadAllBytes(temp)), budget, out counts);
                if (problems.Count > 0)
                    throw new ToolRefusal("the written .bc3 did not read back as the budget, so it was discarded: " + string.Join(" ", problems));
                File.Move(temp, path);   // never overwrites: a file that appeared meanwhile is left alone
                moved = true;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            string written = Sha256(bytes), onDisk = Sha256(File.ReadAllBytes(path));
            bool verified = string.Equals(written, onDisk, StringComparison.Ordinal);
            int unclassified = rows.Count(r => string.IsNullOrWhiteSpace(r.Code) || ClassificationNonValue.IsNonValue(r.Code.Trim()));
            return new JObject
            {
                ["operation"] = "export_bc3",
                ["verified"] = verified,
                ["file_path"] = path,
                ["bytes"] = bytes.Length,
                ["sha256"] = onDisk,
                ["format"] = "FIEBDC-3/2020, character set ANSI (windows-1252)",
                ["records"] = counts,
                ["root_code"] = budget.RootCode + "##",
                ["idempotency_key"] = key,
                ["total_amount"] = Math.Round(Bc3Rules.Total(budget), 2),
                ["lines"] = report,
                ["apu_codes_unused"] = unused,
                ["not_exported"] = new JObject
                {
                    ["unclassified_elements"] = unclassified,
                    ["note"] = unclassified == 0 ? null : "elements without a classification code carry no budget line; they are counted here, not priced."
                },
                ["verification"] = verified
                    ? "the file was re-read from disk: ~V, every ~C (unit, summary, price), the ~D yields and every ~M line and total match the takeoff; the SHA-256 on disk equals the verified bytes."
                    : "the file on disk does not hash to the verified bytes - something changed it after the move."
            };
        }

        /// <summary>A retry's answer: the recorded reply, but only while the file it names still hashes as recorded.</summary>
        private static JObject ReplayExport(CommandResult result)
        {
            if (result == null) throw new ToolRefusal("The durable replay record had no result.");
            if (!result.Success)
                throw new ToolRefusal((result.Error ?? "The recorded export failed.") +
                                      " This is the recorded answer for that idempotency_key; nothing was written now.");
            if (!(result.Data is JObject recorded))
                throw new ToolRefusal("The durable replay record for this key is not an export_bc3 result.");
            string path = (string)recorded["file_path"], sha = (string)recorded["sha256"];
            string now = path != null && File.Exists(path) ? Sha256(File.ReadAllBytes(path)) : null;
            if (!string.Equals(now, sha, StringComparison.Ordinal))
                throw new ToolRefusal("this idempotency_key already exported " + path + " (sha256 " + sha + "), but that file " +
                                      (now == null ? "no longer exists" : "now hashes to " + now) + ". Nothing was written now; export " +
                                      "again with a new idempotency_key.");
            var clone = (JObject)recorded.DeepClone();
            clone["replayed"] = true;
            clone["replay_note"] = "This reply was recorded by an EARLIER call with this same idempotency_key; the file was re-hashed now " +
                                   "and still matches. Nothing was written now.";
            return clone;
        }

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static List<BudgetComparisonRules.BaselineLine> ReadApu(JToken token)
        {
            var arr = token as JArray;
            if (arr == null || arr.Count == 0)
                throw new ToolRefusal("apu is required for export_bc3: [{code, unit, unit_price, description?}] - your unit prices. Nothing was written.");
            var lines = new List<BudgetComparisonRules.BaselineLine>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < arr.Count; i++)
            {
                var o = arr[i] as JObject;
                string at = "apu[" + i + "]";
                if (o == null) throw new ToolRefusal(at + " is not an object.");
                foreach (JProperty p in o.Properties())
                    if (p.Name != "code" && p.Name != "unit" && p.Name != "unit_price" && p.Name != "description")
                        throw new ToolRefusal(at + "." + p.Name + " is not a known key (code, unit, unit_price, description).");
                string code = ((string)o["code"])?.Trim(), unit = ((string)o["unit"])?.Trim(), desc = (string)o["description"];
                string bad = Bc3Rules.CheckCode(code, at + ".code") ?? (string.IsNullOrWhiteSpace(unit) ? at + ".unit is required." : null)
                             ?? Bc3Rules.CheckText(unit, at + ".unit") ?? Bc3Rules.CheckText(desc, at + ".description");
                if (bad != null) throw new ToolRefusal(bad + " Nothing was written.");
                JToken priceTok = o["unit_price"];
                if (priceTok == null || (priceTok.Type != JTokenType.Float && priceTok.Type != JTokenType.Integer))
                    throw new ToolRefusal(at + ".unit_price must be a number: a price is never inferred. Nothing was written.");
                double price = priceTok.Value<double>();
                if (double.IsNaN(price) || double.IsInfinity(price) || price < 0)
                    throw new ToolRefusal(at + ".unit_price must be a finite number >= 0. Nothing was written.");
                if (!seen.Add(code)) throw new ToolRefusal(at + ".code '" + code + "' appears twice in apu; one code, one price. Nothing was written.");
                lines.Add(new BudgetComparisonRules.BaselineLine
                {
                    RowIndex = i, Code = code, Unit = unit, Description = desc,
                    QuantityState = "measured", Quantity = 0, QuantityRaw = "0",
                    UnitPriceState = "measured", UnitPrice = price
                });
            }
            return lines;
        }

        private static List<BudgetComparisonRules.ModelRow> ReadRows(JObject args, string codeField)
        {
            JToken tok = args["model_rows"];
            string rowsPath = (string)args["model_rows_path"];
            bool inline = tok != null && tok.Type != JTokenType.Null, fromPath = !string.IsNullOrWhiteSpace(rowsPath);
            if (inline == fromPath)
                throw new ToolRefusal("Pass exactly one of model_rows or model_rows_path (a horizun_quantities mode='takeoff' reply or its rows). Nothing was written.");
            if (fromPath)
            {
                rowsPath = LocalPath.Require(rowsPath, "model_rows_path", " Nothing was written.");
                if (!File.Exists(rowsPath)) throw new ToolRefusal("model_rows_path not found: " + rowsPath);
                try { tok = JToken.Parse(File.ReadAllText(rowsPath, Encoding.UTF8)); }
                catch (Exception ex) when (ex is IOException || ex is JsonException)
                { throw new ToolRefusal("model_rows_path could not be read as JSON: " + ex.Message); }
            }
            string problem;
            List<BudgetComparisonRules.ModelRow> rows = BudgetComparisonRules.ReadModelRows(tok, codeField, out problem);
            if (rows == null) throw new ToolRefusal(problem);
            return rows;
        }
    }
}
