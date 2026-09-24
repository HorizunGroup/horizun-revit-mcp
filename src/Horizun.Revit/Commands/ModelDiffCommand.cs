// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_model_diff - "what changed between these two deliveries?", the model
// explained from what was measured, and quality measured over time.
//
//   snapshot       read the active document, or a .rvt opened DETACHED in the
//                  background and closed WITHOUT saving, into
//                  %USERPROFILE%\.horizun\snapshots\<id>.json.gz (+ .meta.json).
//   list           the snapshots on disk.
//   compare        before vs after (a snapshot id, or 'active'). Pure logic in
//                  Core/ModelDiffRules.cs. CSV + JSON written under the data root.
//   colorize       ModelDiffColorize.cs - the only write: a duplicate view of the
//                  caller's view with per-state overrides, dry_run + token + reread.
//   explain / record_quality / quality_trend   ModelDiffQuality.cs.
//
// Nothing here writes the model except colorize, and nothing here saves a file.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ModelDiffCommand : ICommand
    {
        private const string ToolName = "horizun_model_diff";

        private readonly Func<string, ICommand> _resolve;

        public ModelDiffCommand(Func<string, ICommand> resolve) { _resolve = resolve; }

        public string Name => ToolName;
        public string Description => "Snapshot, compare and colorize model deliveries; explain; quality history.";

        internal static string SnapshotDir() => Path.Combine(HorizunPaths.DataRoot(), "snapshots");
        internal static string ExportDir() => Path.Combine(SnapshotDir(), "exports");

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Arguments are not a JSON object: " + ex.Message); }

            string op = request.Value<string>("operation");
            try
            {
                switch (op)
                {
                    case "snapshot": return Snapshot(app, request);
                    case "list": return List();
                    case "compare": return Compare(app, request);
                    case "colorize": return Colorize(app, request);
                    case "explain": return Explain(app, request);
                    case "record_quality": return RecordQuality(app, request);
                    case "quality_trend": return QualityTrend(app, request);
                    default:
                        return CommandResult.Fail("operation must be one of snapshot, list, compare, colorize, explain, " +
                                                  "record_quality, quality_trend; got '" + (op ?? "(none)") + "'. Nothing ran.");
                }
            }
            catch (IOException ex)
            {
                return CommandResult.Fail("horizun_model_diff " + op + " could not read or write its store under " +
                                          HorizunPaths.DataRoot() + ": " + ex.Message + ". The model was not changed.");
            }
        }

        // ---- snapshot ------------------------------------------------------------------

        private CommandResult Snapshot(UIApplication app, JObject request)
        {
            ReadScope scope = Scope(request, null);
            string version = app.Application.VersionNumber;
            string filePath = request.Value<string>("file_path");
            DiffSnapshot snap;
            var session = new JObject();

            if (string.IsNullOrWhiteSpace(filePath))
            {
                Document doc = app.ActiveUIDocument?.Document;
                if (doc == null) return CommandResult.Fail("No document is active; open one or pass file_path. Nothing was recorded.");
                CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
                if (wrong != null) return wrong;
                snap = ReadDocument(doc, version, scope, "active_document");
                session["opened"] = false;
            }
            else
            {
                CommandResult failure = ReadFile(app, filePath, request.Value<string>("expected_version"), scope, session, out snap);
                if (failure != null) return failure;
            }

            snap.Id = ModelDiffRules.NewId(DateTime.UtcNow, (string)snap.Document["project_info_unique_id"] + (string)snap.Document["title"]);
            JObject meta = Store(snap);
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "snapshot",
                ["snapshot_id"] = snap.Id,
                ["stored"] = true,
                ["path"] = Path.Combine(SnapshotDir(), snap.Id + ".json.gz"),
                ["meta"] = meta,
                ["session"] = session,
                ["note"] = snap.Truncated
                    ? "TRUNCATED: " + snap.ElementsSeen + " elements matched, only max_elements were recorded. A compare " +
                      "against this snapshot cannot see the rest."
                    : null
            });
        }

        private CommandResult ReadFile(UIApplication app, string path, string expectedVersion, ReadScope scope,
                                       JObject session, out DiffSnapshot snap)
        {
            snap = null;
            string version = app.Application.VersionNumber;
            Document already = app.Application.Documents.Cast<Document>().FirstOrDefault(d =>
                !d.IsLinked && string.Equals(SafePath(d), path, StringComparison.OrdinalIgnoreCase));
            if (already != null)
            {
                snap = ReadDocument(already, version, scope, "already_open_document");
                session["opened"] = false;
                session["note"] = "The file was already open; it was read as it is in memory, unsaved changes included.";
                return null;
            }

            var open = new OpenRequest
            {
                CommandName = Name, Path = path, ExpectedVersion = expectedVersion, ExpectedVersionRequired = true,
                AllowUpgrade = false, Detach = true
            };
            OpenPlan plan = OpenGuard.Check(app, open);
            if (!plan.Ok) return plan.Refusal;
            if (plan.IsCloud) return CommandResult.Fail("snapshot opens local files only. Nothing was opened.");
            // Detaching is how a central is read without touching it; a file that is not
            // workshared has nothing to detach from.
            if (plan.FileIsWorkshared != true) open.Detach = false;

            Document bg = null;
            bool closed = false;
            try
            {
                using (Interference.WithDialogAnswer(DialogAnswer.Cancel))
                    bg = app.Application.OpenDocumentFile(plan.ModelPath, plan.Options());
                if (bg == null) return CommandResult.Fail("Revit returned no document for '" + path + "'. Nothing was recorded.");
                snap = ReadDocument(bg, version, scope, open.Detach ? "file_detached" : "file_background");
                snap.Document["path"] = path;
                try
                {
                    BasicFileInfo info = BasicFileInfo.Extract(path);
                    snap.Document["saved_format"] = info.Format;
                    DocumentVersion dv = info.GetDocumentVersion();
                    if (dv != null) { snap.Document["version_guid"] = dv.VersionGUID.ToString(); snap.Document["number_of_saves"] = dv.NumberOfSaves; }
                }
                catch { /* the facts read from the open document stand */ }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Could not open '" + path + "' in the background: " + ex.Message +
                                          ". Nothing was recorded; the file was not modified.");
            }
            finally
            {
                if (bg != null)
                {
                    try { closed = bg.Close(false); } catch (Exception ex) { session["close_error"] = ex.Message; }
                }
                session["opened"] = bg != null;
                session["detached"] = open.Detach;
                session["closed_without_saving"] = closed;
            }
            return null;
        }

        internal static ReadScope Scope(JObject request, JObject snapshotScope)
        {
            var scope = new ReadScope();
            JArray cats = request["categories"] as JArray ?? snapshotScope?["categories"] as JArray;
            if (cats != null && cats.Count > 0)
                scope.Categories = new HashSet<string>(cats.Select(c => (string)c).Where(c => !string.IsNullOrWhiteSpace(c)), StringComparer.Ordinal);
            int max = request.Value<int?>("max_elements") ?? snapshotScope?.Value<int?>("max_elements") ?? 50000;
            scope.MaxElements = Math.Max(1, Math.Min(max, 500000));
            if (snapshotScope?["parameters"] != null) scope.Parameters = snapshotScope.Value<bool>("parameters");
            return scope;
        }

        // ---- store -------------------------------------------------------------------------

        internal static JObject Store(DiffSnapshot snap)
        {
            string dir = SnapshotDir();
            Directory.CreateDirectory(dir);
            byte[] bytes = ModelDiffRules.Serialize(snap);
            string sha = ModelDiffRules.Sha256Hex(bytes);
            string file = Path.Combine(dir, snap.Id + ".json.gz");
            string tmp = file + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
            JObject meta = ModelDiffRules.Meta(snap, sha, bytes.LongLength);
            File.WriteAllText(Path.Combine(dir, snap.Id + ".meta.json"), meta.ToString(Formatting.Indented));
            // Re-read what was written: a snapshot nobody can load is not a snapshot.
            if (ModelDiffRules.Sha256Hex(File.ReadAllBytes(file)) != sha)
                throw new IOException("the snapshot written to disk does not hash to what was serialised");
            return meta;
        }

        internal static DiffSnapshot Load(string id, out string refusal)
        {
            refusal = null;
            if (!ModelDiffRules.IsValidId(id)) { refusal = "'" + id + "' is not a snapshot id."; return null; }
            string file = Path.Combine(SnapshotDir(), id + ".json.gz");
            string metaFile = Path.Combine(SnapshotDir(), id + ".meta.json");
            if (!File.Exists(file)) { refusal = "No snapshot '" + id + "' under " + SnapshotDir() + ". operation=list shows what exists."; return null; }
            byte[] bytes = File.ReadAllBytes(file);
            if (File.Exists(metaFile))
            {
                string expected = JObject.Parse(File.ReadAllText(metaFile)).Value<string>("content_sha256");
                if (expected != null && expected != ModelDiffRules.Sha256Hex(bytes))
                { refusal = "Snapshot '" + id + "' does not match the hash recorded when it was written; it is refused, not repaired."; return null; }
            }
            try { return ModelDiffRules.Deserialize(bytes); }
            catch (Exception ex) { refusal = "Snapshot '" + id + "' could not be read: " + ex.Message; return null; }
        }

        private static CommandResult List()
        {
            var rows = new JArray();
            string dir = SnapshotDir();
            if (Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir, "*.meta.json").OrderByDescending(x => x, StringComparer.Ordinal))
                {
                    try
                    {
                        JObject m = JObject.Parse(File.ReadAllText(f));
                        rows.Add(new JObject
                        {
                            ["snapshot_id"] = m["id"], ["taken_utc"] = m["taken_utc"], ["title"] = m["document"]?["title"],
                            ["version_guid"] = m["document"]?["version_guid"], ["elements"] = m["elements"], ["truncated"] = m["truncated"]
                        });
                    }
                    catch (Exception ex) { rows.Add(new JObject { ["file"] = Path.GetFileName(f), ["unreadable"] = ex.Message }); }
                }
            return CommandResult.Ok(new JObject { ["operation"] = "list", ["directory"] = dir, ["total"] = rows.Count, ["snapshots"] = rows });
        }

        /// <summary>colorize is the one write; it names its document like every mutating command.</summary>
        private static GateResult MutationGate(UIApplication app, JObject request)
            => DocumentGate.ForMutation(app, request, ToolName);

        private static string SafePath(Document d) { try { return d.PathName; } catch { return null; } }
    }
}
