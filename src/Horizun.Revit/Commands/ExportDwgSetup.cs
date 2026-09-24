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
//   (category and subcategory as Revit NAMES them - the display name, which follows
//   the Revit language). A key the table does not have is refused rather than
//   added: an added key nobody exports to is a row that reports success and
//   changes no file.
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
            if (existing != null && source != null)
                return CommandResult.Fail("dwg_setup '" + name + "' already exists; source only seeds a NEW setup. Nothing was changed.");
            ExportDWGSettings seed = source == null ? null : ExportDWGSettings.FindByName(doc, source);
            if (source != null && seed == null)
                return CommandResult.Fail("dwg_setup.source '" + source + "' is not a DWG export setup. It has: " +
                                          string.Join(", ", ExportDWGSettings.ListNames(doc)) + ".");

            DWGExportOptions options = existing?.GetDWGExportOptions() ?? seed?.GetDWGExportOptions() ?? new DWGExportOptions();
            ExportLayerTable table = options.GetExportLayerTable();
            List<LayerEdit> edits;
            try { edits = ReadLayerEdits(doc, setup["layers"] as JArray, table); }
            catch (ArgumentException ex) { return CommandResult.Fail(ex.Message + " Nothing was changed."); }
            bool create = existing == null;
            bool writes = create || edits.Count > 0;

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
            resolvedPlan.ContextFingerprint = "existing=" + (File.Exists(output) ? output : "") + ";overwrite=" + (overwrite ? "1" : "0");

            if (dryRun)
            {
                var rehearsal = new JObject
                {
                    ["dry_run"] = true, ["format"] = "dwg_layers", ["output_path"] = output, ["setup"] = name,
                    ["setup_exists"] = !create, ["will_create"] = create, ["seeded_from"] = source,
                    ["table_rows"] = table.Count,
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
                        ExportDWGSettings target = existing ?? ExportDWGSettings.Create(doc, name, options);
                        DWGExportOptions writable = target.GetDWGExportOptions();
                        ExportLayerTable edited = writable.GetExportLayerTable();
                        foreach (LayerEdit e in edits)
                        {
                            ExportLayerInfo info = edited.GetExportLayerInfo(e.Key);
                            if (e.Layer != null) info.LayerName = e.Layer;
                            if (e.Color != null) info.ColorNumber = e.Color.Value;
                            if (e.CutLayer != null) info.CutLayerName = e.CutLayer;
                            if (e.CutColor != null) info.CutColorNumber = e.CutColor.Value;
                            edited.Remove(e.Key);
                            edited.Add(e.Key, info);
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
                ["format"] = "dwg_layers", ["setup"] = name, ["setup_id"] = Rid.Value(reread.Id), ["created"] = create, ["seeded_from"] = source,
                ["commit_status"] = commitStatus, ["table_rows"] = rows.Count, ["files_verified"] = 1,
                ["edits"] = new JArray(edits.Select(e => new JObject { ["key"] = e.Label, ["before"] = e.Before, ["after"] = e.After, ["persisted"] = true })),
                ["files"] = new JArray(new JObject { ["path"] = output, ["bytes"] = new FileInfo(output).Length }),
                ["means"] = "persisted = the row read back from a fresh FindByName after the commit. Whether the value " +
                            "also survives save/close/reopen is not measured by this call."
            };
            if (writes) ApplicationOutcome.Stamp(result, WriteTally.OneObject(commitStatus, true));
            return CommandResult.Ok(result);
        }

        private sealed class LayerEdit
        {
            public ExportLayerKey Key; public string Label, Layer, CutLayer; public int? Color, CutColor;
            public string Before, After;
        }

        private static List<LayerEdit> ReadLayerEdits(Document doc, JArray raw, ExportLayerTable table)
        {
            var edits = new List<LayerEdit>();
            if (raw == null) return edits;
            if (raw.Count > 500) throw new ArgumentException("dwg_setup.layers holds more than 500 rows.");
            IList<ExportLayerKey> keys = table.GetKeys();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken token in raw)
            {
                JObject row = token as JObject;
                if (row == null) throw new ArgumentException("each dwg_setup.layers row must be an object.");
                string category = CategoryDisplayName(doc, row.Value<string>("category"));
                string sub = row.Value<string>("subcategory") ?? "";
                ExportLayerKey key = keys.FirstOrDefault(k =>
                    string.Equals(k.CategoryName, category, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(k.SubCategoryName ?? "", sub, StringComparison.OrdinalIgnoreCase));
                if (key == null)
                    throw new ArgumentException("the layer table has no row for category '" + category + "'" +
                        (sub.Length > 0 ? " / subcategory '" + sub + "'" : "") + ". Rows are matched by the names Revit " +
                        "gives them (the display name, in this Revit's language); read the table first with no layers.");
                string label = key.CategoryName + (string.IsNullOrEmpty(key.SubCategoryName) ? "" : " / " + key.SubCategoryName);
                if (!seen.Add(label)) throw new ArgumentException("dwg_setup.layers names '" + label + "' twice.");
                var edit = new LayerEdit
                {
                    Key = key, Label = label, Layer = row.Value<string>("layer"), CutLayer = row.Value<string>("cut_layer"),
                    Color = row.Value<int?>("color"), CutColor = row.Value<int?>("cut_color")
                };
                if (edit.Layer == null && edit.CutLayer == null && edit.Color == null && edit.CutColor == null)
                    throw new ArgumentException("layer row '" + label + "' changes nothing: set layer, color, cut_layer or cut_color.");
                foreach (int? c in new[] { edit.Color, edit.CutColor })
                    if (c != null && (c < 1 || c > 255)) throw new ArgumentException("layer row '" + label + "': colors are AutoCAD index colors 1..255.");
                ExportLayerInfo info = table.GetExportLayerInfo(key);
                edit.Before = RowText(info.LayerName, info.ColorNumber, info.CutLayerName, info.CutColorNumber);
                edit.After = RowText(edit.Layer ?? info.LayerName, edit.Color ?? info.ColorNumber,
                                     edit.CutLayer ?? info.CutLayerName, edit.CutColor ?? info.CutColorNumber);
                edits.Add(edit);
            }
            return edits;
        }

        /// <summary>Rows whose re-read value differs from the edit; null when the setup itself is gone.</summary>
        private static List<string> LostEdits(Document doc, string name, List<LayerEdit> edits)
        {
            ExportDWGSettings fresh = ExportDWGSettings.FindByName(doc, name);
            if (fresh == null) return null;
            ExportLayerTable table = fresh.GetDWGExportOptions().GetExportLayerTable();
            var lost = new List<string>();
            foreach (LayerEdit e in edits)
            {
                ExportLayerInfo info = table.ContainsKey(e.Key) ? table.GetExportLayerInfo(e.Key) : null;
                string now = info == null ? "(row missing)" : RowText(info.LayerName, info.ColorNumber, info.CutLayerName, info.CutColorNumber);
                if (now != e.After) lost.Add(e.Label + ": wanted " + e.After + ", read " + now);
            }
            return lost;
        }

        private static JArray TableRows(ExportLayerTable table)
        {
            var rows = new JArray();
            foreach (ExportLayerKey key in table.GetKeys()
                         .OrderBy(k => k.CategoryName, StringComparer.Ordinal).ThenBy(k => k.SubCategoryName ?? "", StringComparer.Ordinal))
            {
                ExportLayerInfo info = table.GetExportLayerInfo(key);
                rows.Add(new JObject
                {
                    ["category"] = key.CategoryName, ["subcategory"] = key.SubCategoryName ?? "",
                    ["special"] = key.SpecialType.ToString(),
                    ["layer"] = info.LayerName, ["color"] = info.ColorNumber,
                    ["cut_layer"] = info.CutLayerName, ["cut_color"] = info.CutColorNumber
                });
            }
            return rows;
        }

        private static string RowText(string layer, int color, string cutLayer, int cutColor)
            => "layer=" + layer + ";color=" + color + ";cut_layer=" + cutLayer + ";cut_color=" + cutColor;

        /// <summary>A BuiltInCategory token becomes the name Revit uses in the layer table.</summary>
        private static string CategoryDisplayName(Document doc, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("each layer row needs a category.");
            if (Enum.TryParse(text, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
            {
                try { Category c = Category.GetCategory(doc, bic); if (c != null) return c.Name; } catch { }
            }
            return text;
        }
    }
}
