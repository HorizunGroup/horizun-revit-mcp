// -----------------------------------------------------------------------------
// Horizun Revit MCP - the DWG export layer table of a named export setup.
// Original Horizun code.
//
// Field evidence: 25 scripts on ExportDWGSettings / ExportLayerTable, and one note
// from the operator that framed this whole file: "can it be written? measure it,
// do not assume it". So nothing here assumes it:
//
//   The table lives on an ExportDWGSettings ELEMENT (a named setup). Editing it is
//   GetDWGExportOptions -> GetExportLayerTable -> change rows -> SetExportLayerTable
//   -> SetDWGExportOptions, inside a transaction. That the calls do not throw proves
//   nothing, so the table is then read back from a FRESH FindByName twice: once
//   before the commit (a mismatch rolls back and names each row that did not
//   stick) and once after it (what the reply reports as persisted). Every year
//   2023-2027 exposes the same calls (BaseExportOptions.Get/SetExportLayerTable,
//   ExportDWGSettings.Create/FindByName/SetDWGExportOptions - checked in each
//   RevitAPI.xml); whether each year KEEPS the write is what the live probe
//   records, per year, instead of this comment claiming it.
//
//   Keys are Revit's own. A row is matched against the keys the table already has
//   (category, subcategory and SpecialType - Walls has five rows, one per special)
//   by DwgLayerRows.Find; a category is named by its BuiltInCategory token or by
//   the name Revit shows, which follows the Revit language. A key the table does
//   not have is refused rather than added: an added key nobody exports to is a row
//   that reports success and changes no file.
//
//   MEASURED on 2026 (live matrix 2026-09-24): a NEW setup created as
//   Create(doc, name, new DWGExportOptions()) had an EMPTY layer table - so reading
//   it gave no rows and every layer edit was refused. A new setup is now seeded by
//   creating each candidate (source, layer_standard, Revit's own default overload,
//   the document's active setup, its predefined setups) inside a transaction that
//   is rolled back, reading the table Revit really gave it, and keeping the first
//   seed with rows. When every seed is empty the setup is not created at all.
//
// The produced .json is the table as re-read after the commit, so the file on disk
// is evidence, not an echo of the request.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ExportCommand
    {
        private CommandResult ExecuteDwgLayers(UIApplication app, GateResult gate, Document doc, JObject request, string output)
        {
            JObject setup = request["dwg_setup"] as JObject;
            string name = setup?.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.Fail("dwg_layers needs dwg_setup.name: the export setup to read or write.");
            string folder = Path.GetDirectoryName(output);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return CommandResult.Fail("The output directory does not exist: " + folder + ". It is not created implicitly.");
            bool overwrite = request.Value<bool?>("overwrite") == true;
            if (!overwrite && File.Exists(output))
                return CommandResult.Fail("Output already exists and overwrite=false: " + output);

            ExportDWGSettings existing = ExportDWGSettings.FindByName(doc, name);
            string source = setup.Value<string>("source");
            string standard = setup.Value<string>("layer_standard");
            if (existing != null && (source != null || standard != null))
                return CommandResult.Fail("dwg_setup '" + name + "' already exists; source and layer_standard only seed a NEW setup. Nothing was changed.");
            if (source != null && standard != null)
                return CommandResult.Fail("dwg_setup takes source OR layer_standard, not both: each is a whole layer table. Nothing was changed.");
            if (standard != null && !LayerStandards.Contains(standard))
                return CommandResult.Fail("dwg_setup.layer_standard '" + standard + "' is not one Revit ships: " + string.Join(", ", LayerStandards) + ".");
            ExportDWGSettings seed = source == null ? null : ExportDWGSettings.FindByName(doc, source);
            if (source != null && seed == null)
                return CommandResult.Fail("dwg_setup.source '" + source + "' is not a DWG export setup. It has: " +
                                          string.Join(", ", ExportDWGSettings.ListNames(doc)) + ".");

            // The table the edits are matched against. For an existing setup it is its
            // own. For a NEW one it is whatever Revit really gives the created setup -
            // MEASURED on 2026: Create(doc, name, new DWGExportOptions()) left the table
            // EMPTY, so a table read from an options object nobody created is not
            // evidence. Each seed is created inside a transaction that is rolled back,
            // its table read, and the first seed with rows wins.
            DwgSeed chosen = null;
            var tried = new List<string>();
            List<DwgLayerRow> tableRows;
            if (existing != null) tableRows = ReadRows(existing.GetDWGExportOptions().GetExportLayerTable());
            else
            {
                foreach (DwgSeed candidate in SeedCandidates(doc, seed, source, standard))
                {
                    List<DwgLayerRow> probed = ProbeSeed(doc, name, candidate, out string probeNote);
                    tried.Add(candidate.Label + " -> " + (probed == null ? probeNote : probed.Count + " rows"));
                    if (probed == null || probed.Count == 0) continue;
                    chosen = candidate; chosen.Rows = probed;
                    break;
                }
                if (chosen == null)
                    return CommandResult.FailWithDetail("dwg_setup '" + name + "' was not created: every seed gave it an EMPTY layer " +
                        "table (" + string.Join("; ", tried) + "). An empty table exports nothing a layer row can change. Seed it " +
                        "with dwg_setup.source (an existing setup) or dwg_setup.layer_standard (" + string.Join(", ", LayerStandards) + "). Nothing was changed.",
                        new JObject { ["setup"] = name, ["measured"] = "empty_layer_table", ["seeds_tried"] = new JArray(tried) });
                tableRows = chosen.Rows;
            }
            List<LayerEdit> edits;
            try { edits = ReadLayerEdits(doc, setup["layers"] as JArray, tableRows); }
            catch (ArgumentException ex) { return CommandResult.Fail(ex.Message + " Nothing was changed."); }
            bool create = existing == null;
            bool writes = create || edits.Count > 0;
            string seededFrom = chosen?.Label;

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "format", "output_path", "overwrite", "dwg_setup");
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber, DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            resolvedPlan.Elements.Add(new PlannedElement
            {
                UniqueId = existing != null ? existing.UniqueId : "dwg_setup:" + name,
                Category = "dwg_export_setup", TypeName = name,
                Action = create ? PlannedAction.Create : writes ? PlannedAction.Modify : PlannedAction.Read,
                BeforeValues = edits.ToDictionary(e => e.Label, e => e.Before)
            });
            resolvedPlan.ContextFingerprint = "existing=" + (File.Exists(output) ? output : "") + ";overwrite=" + (overwrite ? "1" : "0") +
                                              ";seed=" + (seededFrom ?? "") + ";rows=" + tableRows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (dryRun)
            {
                var rehearsal = new JObject
                {
                    ["dry_run"] = true, ["format"] = "dwg_layers", ["output_path"] = output, ["setup"] = name,
                    ["setup_exists"] = !create, ["will_create"] = create, ["seeded_from"] = seededFrom,
                    ["seeds_tried"] = create ? new JArray(tried) : null,
                    ["table_rows"] = tableRows.Count,
                    ["edits"] = new JArray(edits.Select(e => new JObject { ["key"] = e.Label, ["before"] = e.Before, ["after"] = e.After })),
                    ["note"] = writes ? "Nothing was written. Apply writes the setup in one transaction and re-reads it."
                                      : "Read only: apply writes the table, as read now, to output_path."
                };
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(rehearsal, gate, Name, planHash, true,
                    "the token binds the setup name, the seed, every layer row and its value BEFORE the edit - a table " +
                    "changed by someone else since this rehearsal refuses as a stale plan.");
                return CommandResult.Ok(rehearsal);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            string commitStatus = "not_needed";
            if (writes)
            {
                using (var tx = new Transaction(doc, "Horizun: DWG export setup"))
                {
                    tx.Start();
                    try
                    {
                        ExportDWGSettings target = existing ?? CreateSeeded(doc, name, chosen);
                        DWGExportOptions writable = target.GetDWGExportOptions();
                        ExportLayerTable edited = writable.GetExportLayerTable();
                        if (create && edited.Count != tableRows.Count)
                            throw new InvalidOperationException("the created setup holds " + edited.Count + " layer rows; the rehearsal of the " +
                                "same seed (" + seededFrom + ") held " + tableRows.Count);
                        foreach (LayerEdit e in edits)
                        {
                            ExportLayerKey key = FindKey(edited, e.Row);
                            if (key == null) throw new InvalidOperationException("the row '" + e.Label + "' is not in the setup being written");
                            ExportLayerInfo info = edited.GetExportLayerInfo(key);
                            if (e.Layer != null) info.LayerName = e.Layer;
                            if (e.Color != null) info.ColorNumber = e.Color.Value;
                            if (e.CutLayer != null) info.CutLayerName = e.CutLayer;
                            if (e.CutColor != null) info.CutColorNumber = e.CutColor.Value;
                            edited.Remove(key);
                            edited.Add(key, info);
                        }
                        writable.SetExportLayerTable(edited);
                        target.SetDWGExportOptions(writable);

                        // BEFORE the commit, from a fresh lookup: a write the API accepts
                        // but does not keep rolls back here, with the rows named.
                        List<string> lost = LostEdits(doc, name, edits);
                        if (lost == null || lost.Count > 0)
                            throw new InvalidOperationException("the DWG setup did not keep " +
                                (lost == null ? "itself (FindByName returned nothing)" : "these rows: " + string.Join("; ", lost)) +
                                ". The API accepted the calls; the re-read disagrees.");
                        commitStatus = Guard.Commit(tx, "DWG export setup").ToString();
                    }
                    catch (Exception ex)
                    {
                        string rb = PlanFailure.NotAttempted; bool attempted = false;
                        if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                        return CommandResult.FailWithDetail("DWG export setup write failed: " + ex.Message + ". " +
                            PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing in it was kept"),
                            new JObject { ["setup"] = name, ["measured"] = "write_not_persisted_or_refused" });
                    }
                }
            }

            // AFTER the commit, from a fresh lookup again: what the reply calls persisted.
            ExportDWGSettings reread = ExportDWGSettings.FindByName(doc, name);
            List<string> lostAfter = LostEdits(doc, name, edits);
            if (reread == null || lostAfter == null || lostAfter.Count > 0)
                return CommandResult.FailWithDetail("The transaction committed, but the DWG setup re-read does not hold the edit: " +
                    (lostAfter == null ? "setup missing" : string.Join("; ", lostAfter)),
                    new JObject { ["setup"] = name, ["commit_status"] = commitStatus, ["persisted"] = false });

            JArray rows = TableRows(reread.GetDWGExportOptions().GetExportLayerTable());
            var file = new JObject
            {
                ["schema"] = "horizun.dwg-layer-table/1", ["document"] = doc.Title, ["setup"] = name,
                ["revit"] = app?.Application?.VersionNumber, ["utc"] = DateTime.UtcNow.ToString("o"), ["rows"] = rows
            };
            try
            {
                File.WriteAllText(output, file.ToString(Formatting.Indented));
                JObject back = JObject.Parse(File.ReadAllText(output));
                if (!JToken.DeepEquals(back["rows"], rows)) throw new IOException("the file re-read differs from the table written");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return CommandResult.FailWithDetail("The setup is " + (writes ? "written and verified" : "unchanged") +
                    ", but its table could not be written to " + output + ": " + ex.Message,
                    new JObject { ["setup"] = name, ["commit_status"] = commitStatus, ["persisted"] = writes });
            }

            var result = new JObject
            {
                ["format"] = "dwg_layers", ["setup"] = name, ["setup_id"] = Rid.Value(reread.Id), ["created"] = create, ["seeded_from"] = seededFrom,
                ["commit_status"] = commitStatus, ["table_rows"] = rows.Count, ["files_verified"] = 1,
                ["edits"] = new JArray(edits.Select(e => new JObject { ["key"] = e.Label, ["before"] = e.Before, ["after"] = e.After, ["persisted"] = true })),
                ["files"] = new JArray(new JObject { ["path"] = output, ["bytes"] = new FileInfo(output).Length }),
                ["means"] = "persisted = the row read back from a fresh FindByName after the commit. Whether the value " +
                            "also survives save/close/reopen is not measured by this call."
            };
            if (writes) ApplicationOutcome.Stamp(result, WriteTally.OneObject(commitStatus, true));
            return CommandResult.Ok(result);
        }

        private static readonly string[] LayerStandards = { "AIA", "ISO13567", "CP83", "BS1192" };

        /// <summary>One way to give a NEW setup its layer table.</summary>
        private sealed class DwgSeed
        {
            public string Label;
            /// <summary>Null: the (document, name) overload - "default values", Revit's own.</summary>
            public DWGExportOptions Options;
            public List<DwgLayerRow> Rows;
        }

        /// <summary>The seeds to try, in order. An explicit source or standard is the only candidate.</summary>
        private static IEnumerable<DwgSeed> SeedCandidates(Document doc, ExportDWGSettings seed, string source, string standard)
        {
            if (seed != null) { yield return new DwgSeed { Label = "source:" + source, Options = seed.GetDWGExportOptions() }; yield break; }
            if (standard != null)
            {
                yield return new DwgSeed { Label = "layer_standard:" + standard, Options = new DWGExportOptions { LayerMapping = standard } };
                yield break;
            }
            yield return new DwgSeed { Label = "revit_default" };
            ExportDWGSettings active = null;
            try { active = ExportDWGSettings.GetActivePredefinedSettings(doc); } catch { }
            if (active != null) yield return new DwgSeed { Label = "active_setup:" + active.Name, Options = active.GetDWGExportOptions() };
            IList<string> names = null;
            try { names = BaseExportOptions.GetPredefinedSetupNames(doc); } catch { }
            foreach (string n in names ?? new List<string>())
            {
                if (active != null && string.Equals(n, active.Name, StringComparison.Ordinal)) continue;
                DWGExportOptions o = null;
                try { o = DWGExportOptions.GetPredefinedOptions(doc, n); } catch { }
                if (o != null) yield return new DwgSeed { Label = "predefined_setup:" + n, Options = o };
            }
        }

        private static ExportDWGSettings CreateSeeded(Document doc, string name, DwgSeed seed) =>
            seed?.Options == null ? ExportDWGSettings.Create(doc, name) : ExportDWGSettings.Create(doc, name, seed.Options);

        /// <summary>
        /// Create the setup from this seed inside a transaction that is ROLLED BACK, and read the
        /// table Revit really gave it. Null (with a note) when the rehearsal could not run or did
        /// not roll back cleanly - which the caller reports and never treats as a table.
        /// </summary>
        private static List<DwgLayerRow> ProbeSeed(Document doc, string name, DwgSeed seed, out string note)
        {
            note = null;
            using (var tx = new Transaction(doc, "Horizun: DWG setup seed rehearsal"))
            {
                List<DwgLayerRow> rows = null;
                try
                {
                    if (tx.Start() != TransactionStatus.Started) { note = "rehearsal transaction did not start"; return null; }
                    ExportDWGSettings probe = CreateSeeded(doc, name, seed);
                    rows = ReadRows(probe.GetDWGExportOptions().GetExportLayerTable());
                }
                catch (Exception ex) { note = "refused: " + ex.Message; rows = null; }
                finally
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                    {
                        Guard.RollbackResult rb = Guard.RollBack(tx);
                        if (!rb.Confirmed) { note = "rehearsal rollback returned " + rb.StatusName; rows = null; }
                    }
                }
                return rows;
            }
        }

        private sealed class LayerEdit
        {
            public DwgLayerRow Row; public string Label, Layer, CutLayer; public int? Color, CutColor;
            public string Before, After;
        }

        private static List<LayerEdit> ReadLayerEdits(Document doc, JArray raw, List<DwgLayerRow> rows)
        {
            var edits = new List<LayerEdit>();
            if (raw == null) return edits;
            if (raw.Count > 500) throw new ArgumentException("dwg_setup.layers holds more than 500 rows.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken token in raw)
            {
                JObject row = token as JObject;
                if (row == null) throw new ArgumentException("each dwg_setup.layers row must be an object.");
                List<string> names = CategoryNames(doc, row.Value<string>("category"));
                int index = DwgLayerRows.Find(rows, names, row.Value<string>("subcategory") ?? "", row.Value<string>("special"), out string error);
                if (index < 0) throw new ArgumentException(error);
                DwgLayerRow found = rows[index];
                string label = found.Label;
                if (!seen.Add(label)) throw new ArgumentException("dwg_setup.layers names '" + label + "' twice.");
                var edit = new LayerEdit
                {
                    Row = found, Label = label, Layer = row.Value<string>("layer"), CutLayer = row.Value<string>("cut_layer"),
                    Color = row.Value<int?>("color"), CutColor = row.Value<int?>("cut_color")
                };
                if (edit.Layer == null && edit.CutLayer == null && edit.Color == null && edit.CutColor == null)
                    throw new ArgumentException("layer row '" + label + "' changes nothing: set layer, color, cut_layer or cut_color.");
                foreach (int? c in new[] { edit.Color, edit.CutColor })
                    if (c != null && (c < 1 || c > 255)) throw new ArgumentException("layer row '" + label + "': colors are AutoCAD index colors 1..255.");
                edit.Before = found.ValueText;
                edit.After = DwgLayerRows.RowText(edit.Layer ?? found.Layer, edit.Color ?? found.Color,
                                                  edit.CutLayer ?? found.CutLayer, edit.CutColor ?? found.CutColor);
                edits.Add(edit);
            }
            return edits;
        }

        private static ExportLayerKey FindKey(ExportLayerTable table, DwgLayerRow row) =>
            table.GetKeys().FirstOrDefault(k => DwgLayerRows.SameKey(row, k.CategoryName, k.SubCategoryName ?? "", k.SpecialType.ToString()));

        /// <summary>Rows whose re-read value differs from the edit; null when the setup itself is gone.</summary>
        private static List<string> LostEdits(Document doc, string name, List<LayerEdit> edits)
        {
            ExportDWGSettings fresh = ExportDWGSettings.FindByName(doc, name);
            if (fresh == null) return null;
            ExportLayerTable table = fresh.GetDWGExportOptions().GetExportLayerTable();
            var lost = new List<string>();
            foreach (LayerEdit e in edits)
            {
                ExportLayerKey key = FindKey(table, e.Row);
                ExportLayerInfo info = key == null ? null : table.GetExportLayerInfo(key);
                string now = info == null ? "(row missing)" : DwgLayerRows.RowText(info.LayerName, info.ColorNumber, info.CutLayerName, info.CutColorNumber);
                if (now != e.After) lost.Add(e.Label + ": wanted " + e.After + ", read " + now);
            }
            return lost;
        }

        private static List<DwgLayerRow> ReadRows(ExportLayerTable table)
        {
            var rows = new List<DwgLayerRow>();
            foreach (ExportLayerKey key in table.GetKeys())
            {
                ExportLayerInfo info = table.GetExportLayerInfo(key);
                rows.Add(new DwgLayerRow
                {
                    Category = key.CategoryName ?? "", SubCategory = key.SubCategoryName ?? "", Special = key.SpecialType.ToString(),
                    Layer = info.LayerName ?? "", Color = info.ColorNumber, CutLayer = info.CutLayerName ?? "", CutColor = info.CutColorNumber
                });
            }
            return rows;
        }

        private static JArray TableRows(ExportLayerTable table)
        {
            var rows = new JArray();
            foreach (DwgLayerRow r in ReadRows(table)
                         .OrderBy(k => k.Category, StringComparer.Ordinal).ThenBy(k => k.SubCategory, StringComparer.Ordinal)
                         .ThenBy(k => k.Special, StringComparer.Ordinal))
            {
                rows.Add(new JObject
                {
                    ["category"] = r.Category, ["subcategory"] = r.SubCategory, ["special"] = r.Special,
                    ["layer"] = r.Layer, ["color"] = r.Color, ["cut_layer"] = r.CutLayer, ["cut_color"] = r.CutColor
                });
            }
            return rows;
        }

        /// <summary>
        /// The spellings a layer row's category may match: for a BuiltInCategory token (OST_Walls)
        /// the name THIS document gives it - which follows Revit's language - and the text as given.
        /// </summary>
        private static List<string> CategoryNames(Document doc, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("each layer row needs a category.");
            var names = new List<string>();
            if (Enum.TryParse(text, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
            {
                try { Category c = Category.GetCategory(doc, bic); if (c != null && !string.IsNullOrEmpty(c.Name)) names.Add(c.Name); } catch { }
            }
            names.Add(text);
            return names;
        }
    }
}
