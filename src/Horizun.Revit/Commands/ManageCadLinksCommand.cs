// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_cad_links - the typed way to get a DWG into a model.
//
// Until this existed, every step of the DWG->BIM chain was typed except the
// first one: the harnesses linked their fixture through horizun_execute_python,
// and said so. A chain with one untyped link is a chain whose first step has no
// rehearsal, no confirmation token, no post-commit re-read, and no refusal it
// can be trusted to make.
//
// WHY THIS IS NOT AN OPERATION ON horizun_manage_links, decided from the API and
// not from taste - three reasons, each on its own sufficient:
//
//   1. THERE IS NO CAD UNLOAD. RevitLinkType has Unload and UnloadLocally;
//      CADLinkType declares exactly four methods in every year 2023-2027 -
//      Reload(), Reload(CADLinkOptions), LoadFrom(string),
//      LoadFrom(ExternalResourceReference). Putting 'unload' in one enum where
//      it works for .rvt and permanently refuses for .dwg is one word reading
//      two ways, which is the thing this bridge refuses on principle.
//
//   2. THE ARGUMENTS ARE DISJOINT, NOT NESTED. Creating a CAD link is
//      Document.Link(string, DWGImportOptions, View, out ElementId) - there is
//      no view-free overload - carrying nine option fields that mean nothing to
//      an RVT link, while attachment/overlay, worksets and relative-vs-absolute
//      path type mean nothing to a DWG.
//
//   3. THE VERIFICATION DIVERGES, and this bridge is built on verification. An
//      RVT link proves a reload by re-reading a STATUS that changes. CADLinkType
//      has no GetLinkedFileStatus, and a CAD reload reads the same before and
//      after - so a status-based 'verified' would be true by construction. The
//      only honest post-condition for a CAD link is the drawing's CONTENT: the
//      file's SHA-256 and the geometry fingerprint horizun_query_cad already
//      publishes. That engine is here, not there.
//
// The API limit is published rather than hidden: 'unload' is reported by name in
// every reply, with what to do instead.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ManageCadLinksCommand : ICommand
    {
        /// <summary>The tool name, as a constant: the helpers below are static and cannot read the property.</summary>
        internal const string ToolName = "horizun_manage_cad_links";

        public string Name => ToolName;

        public string Description =>
            "List, add, reload and repoint CAD (DWG) links, typed and verified by content.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            string operation = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (operation.Length == 0)
                return CommandResult.Fail(
                    "operation is required: list, add, reload or repoint. There is deliberately no default - a " +
                    "command that guesses which of those you meant can link a drawing you did not ask for.");

            if (operation == "unload")
                return CommandResult.Fail(
                    "no_cad_unload: Revit's API cannot unload a CAD link. RevitLinkType has Unload and " +
                    "UnloadLocally; CADLinkType has only Reload and LoadFrom, in every version 2023-2027 - " +
                    "MEASURED, not assumed. This command will not pretend otherwise by doing something else that " +
                    "looks similar. To stop a drawing appearing, delete the ImportInstance with " +
                    "horizun_delete_verified, which is the destructive operation it actually is and asks for its " +
                    "own confirmation; to point the link somewhere else, use repoint.");

            if (operation == "list")
                return List(app, request);

            // Everything below WRITES.
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            switch (operation)
            {
                case "add": return Add(app, doc, request, gate);
                case "reload": return Reload(app, doc, request, gate);
                case "repoint": return Repoint(app, doc, request, gate);
                default:
                    return UnsupportedOperation(operation);
            }
        }

        /// <summary>
        /// A CAPABILITY GAP, HANDED OVER RATHER THAN ANNOUNCED.
        ///
        /// No typed path covers this operation and nothing has been written, which
        /// is exactly the case the Python fallback exists for - so the refusal
        /// carries the machine-readable grant instead of leaving a caller to
        /// recognise it from the wording of a sentence. The gap is raised as
        /// UnsupportedCapability first so it travels as a TYPE, and the whole
        /// batch - one operation, here - goes to the central decision, because the
        /// batch rule is not a per-command opinion.
        ///
        /// 'unload' never reaches this: it is refused above WITHOUT a grant,
        /// because Revit has no CAD unload in any version 2023-2027 and sending a
        /// caller to Python would only find the same absent API.
        /// </summary>
        private static CommandResult UnsupportedOperation(string operation)
        {
            var gap = new UnsupportedCapability(
                "unsupported operation '" + operation + "' - horizun_manage_cad_links does list, add, reload " +
                "and repoint. Nothing was written.",
                FallbackSignal.ReasonUnsupportedOperation);

            var outcomes = new List<ActionOutcome>
            {
                new ActionOutcome
                {
                    Index = 0,
                    Error = gap.Message,
                    UnsupportedReason = UnsupportedCapability.ReasonOf(gap)
                }
            };
            return FallbackDecision.Refuse(gap.Message, FallbackDecision.Decide(outcomes, writeStarted: false));
        }

        // =====================================================================
        // LIST
        // =====================================================================
        private static CommandResult List(UIApplication app, JObject request)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            List<JObject> unreadable;
            List<CadInstanceFacts> facts = CadFacts.Collect(doc, out unreadable);

            var rows = new JArray();
            foreach (CadInstanceFacts f in facts.OrderBy(x => x.ElementId))
                rows.Add(f.ToJson());

            return CommandResult.Ok(new JObject
            {
                ["document"] = SafeTitle(doc),
                ["read_only"] = true,
                ["cad_instances"] = rows,
                ["count"] = rows.Count,
                ["unreadable"] = new JArray(unreadable),
                ["unreadable_means"] = "a CAD instance this bridge could not measure. It is listed rather than " +
                                       "dropped: an instance missing from a list reads as an instance that is " +
                                       "not there.",
                ["api_limits"] = ApiLimits()
            });
        }

        // =====================================================================
        // ADD
        // =====================================================================
        private static CommandResult Add(UIApplication app, Document doc, JObject request, GateResult gate)
        {
            string path = request.Value<string>("file_path");
            if (string.IsNullOrWhiteSpace(path))
                return CommandResult.Fail("file_path is required for add.");

            JObject fileFacts;
            string fileError = ReadFile(path, out fileFacts);
            if (fileError != null) return CommandResult.Fail(fileError);

            // THE VIEW IS NOT OPTIONAL, because the API has no overload without
            // one: Document.Link(string, DWGImportOptions, View, out ElementId)
            // is the only signature, in every year. A caller who does not name a
            // view is told that, rather than having one chosen for them - the
            // choice decides whether the drawing appears in one view or in all
            // of them, which is the whole of current_view_only.
            long viewId = request.Value<long?>("view_id") ?? -1;
            View view = null;
            if (viewId >= 0 && Rid.CanRepresent(viewId)) view = doc.GetElement(Rid.Make(viewId)) as View;
            if (view == null)
                return CommandResult.Fail(
                    "view_id is required for add, and must identify a View in '" + SafeTitle(doc) + "'. Revit's " +
                    "only Link overload takes one, in every supported version - there is no view-free form to " +
                    "fall back to. List views with horizun_query_planimetry mode='views'.");
            if (view.IsTemplate)
                return CommandResult.Fail("view_id " + viewId + " is a view TEMPLATE; a drawing cannot be linked into one.");

            string optionError;
            JObject declared;
            DWGImportOptions options = BuildOptions(request, out optionError, out declared);
            if (optionError != null) return CommandResult.Fail(optionError);

            // Is this drawing already here? Linking it twice is not an error
            // Revit raises - it is two ImportInstances of one file, which every
            // reader downstream then has to disambiguate.
            List<JObject> unreadable;
            List<CadInstanceFacts> before = CadFacts.Collect(doc, out unreadable);
            string wanted = NormalisePath(path);
            List<CadInstanceFacts> already = before
                .Where(f => string.Equals(NormalisePath(f.ExternalPath), wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
            bool allowDuplicate = request.Value<bool?>("allow_duplicate") ?? false;
            if (already.Count > 0 && !allowDuplicate)
                return CommandResult.Fail(
                    "already_linked: '" + Path.GetFileName(path) + "' is already in this document as instance " +
                    string.Join(", ", already.Select(a => a.ElementId.ToString(CultureInfo.InvariantCulture))) +
                    ". Linking it again makes two ImportInstances of one drawing, and every reader downstream " +
                    "then has to guess which one a rule meant. Use reload to pick up new content, repoint to " +
                    "aim it elsewhere, or pass allow_duplicate=true if two placements is genuinely what you want.");

            // WHAT THE TOKEN BINDS. Not the request - the resolved facts: the view
            // that will host it, the bytes of the file, and the options that decide
            // where the drawing lands. A token bound to the words of a request
            // would survive somebody replacing the file between rehearsal and apply.
            string hash = DocumentGate.PlanHash(request, "operation", "file_path", "view_id");
            var resolved = new ResolvedPlan();
            resolved.Elements.Add(new PlannedElement
            {
                UniqueId = Safe(() => view.UniqueId) ?? Rid.Value(view.Id).ToString(CultureInfo.InvariantCulture),
                Category = "View",
                TypeName = SafeViewType(view),
                Action = PlannedAction.Create,
                BeforeValues = new Dictionary<string, string>
                {
                    { "file_sha256", fileFacts.Value<string>("sha256") ?? "" },
                    { "units", declared.Value<string>("units") ?? "" },
                    { "placement", declared.Value<string>("placement") ?? "" },
                    { "this_view_only", (declared.Value<bool?>("this_view_only") == true).ToString() },
                    { "already_linked", already.Count.ToString(CultureInfo.InvariantCulture) }
                }
            });

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            var rehearsal = new JObject
            {
                ["operation"] = "add",
                ["document"] = SafeTitle(doc),
                ["file"] = fileFacts,
                ["view"] = new JObject { ["element_id"] = Rid.Value(view.Id), ["name"] = SafeName(view),
                                         ["view_type"] = SafeViewType(view) },
                ["options"] = declared,
                ["already_linked"] = new JArray(already.Select(a => (JToken)a.ElementId)),
                ["would_create"] = "one ImportInstance, linked (not imported): the drawing stays a reference to " +
                                   "the file on disk, and reload picks up a new issue of it.",
                ["api_limits"] = ApiLimits()
            };
            if (dryRun)
            {
                DocumentGate.RecordResolvedPlan(resolved);
                rehearsal["dry_run"] = true;
                rehearsal["wrote_nothing"] = true;
                rehearsal["rehearsal_kind"] = "measured_preview";
                rehearsal["rehearsal_note"] =
                    "Linking cannot rehearse provisionally: a provisional link IS a link. This preview is the " +
                    "MEASURED state the token binds - the file's bytes, the view, and the options - and the " +
                    "apply performs the link and re-reads what it made.";
                ApplicationOutcome.StampRehearsal(rehearsal, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(rehearsal, gate, ToolName, hash, true,
                    "the token binds the SHA-256 of the file as it is now; a file somebody replaced first " +
                    "refuses as a stale plan.");
                return CommandResult.Ok(rehearsal);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, ToolName, hash, resolved, null);
            if (refusal != null) return refusal;

            ElementId created = ElementId.InvalidElementId;
            bool linked;
            using (var t = new Transaction(doc, "Horizun: link a CAD drawing"))
            {
                t.Start();
                linked = doc.Link(path, options, view, out created);
                t.Commit();
            }
            if (!linked || created == ElementId.InvalidElementId)
                return CommandResult.Fail(
                    "Revit refused to link '" + Path.GetFileName(path) + "' and gave no element back. Nothing " +
                    "was created. The commonest causes are a file another process holds open, and a DWG saved " +
                    "in a format this Revit does not read.");

            // RE-READ IT. Everything below comes from the model after the commit,
            // never from the request - including the file hash, which is read off
            // the path the LINK resolved to rather than the path that was asked
            // for.
            JObject verified;
            string verifyError = Measure(doc, Rid.Value(created), out verified);
            if (verifyError != null)
                return CommandResult.Fail(
                    "the link committed as element " + Rid.Value(created) + " and then could not be re-read (" +
                    verifyError + "). It is IN the model; this command cannot tell you what it points at.");

            var result = new JObject
            {
                ["operation"] = "add",
                ["document"] = SafeTitle(doc),
                ["dry_run"] = false,
                ["element_id"] = Rid.Value(created),
                ["instance"] = verified,
                ["requested"] = new JObject { ["file"] = fileFacts, ["options"] = declared },
                ["host_verified"] = true,
                ["verified_by"] = "the ImportInstance was re-read from the model after the commit: its resolved " +
                                  "path, the SHA-256 of the file THAT path names, whether it is linked or " +
                                  "imported, its owner view and its declared units.",
                ["api_limits"] = ApiLimits()
            };
            JObject drift = ComparePathAndHash(fileFacts, verified);
            if (drift != null) result["disagreement"] = drift;
            ApplicationOutcome.StampApplied(result, "Committed", 1, 1, 1, 0, 0, 0);
            DocumentGate.StampConfirmation(result, gate, ToolName, hash, false);
            return CommandResult.Ok(result);
        }

        // =====================================================================
        // RELOAD
        // =====================================================================
        private static CommandResult Reload(UIApplication app, Document doc, JObject request, GateResult gate)
        {
            CadInstanceFacts facts;
            CADLinkType type;
            string find = Resolve(doc, request, out facts, out type);
            if (find != null) return CommandResult.Fail(find);

            JObject beforeFile;
            string fileError = ReadFile(facts.ExternalPath, out beforeFile);
            if (fileError != null)
                return CommandResult.Fail(
                    "the link points at a file this machine cannot read, so a reload would replace the drawing " +
                    "with nothing: " + fileError);

            // WHAT THE DRAWING SAYS NOW, before touching it. A reload is verified
            // by content, because CADLinkType publishes no status that changes -
            // reading 'Loaded' before and after would make 'verified' true by
            // construction.
            // WHAT THE DRAWING SAYS NOW, before touching it. A reload is verified
            // by CONTENT, because CADLinkType publishes no status that changes -
            // reading 'Loaded' before and after would make 'verified' true by
            // construction.
            string beforePrint = Fingerprint(doc, facts.ElementId);

            string hash = DocumentGate.PlanHash(request, "operation", "instance_id");
            var resolved = new ResolvedPlan();
            resolved.Elements.Add(new PlannedElement
            {
                UniqueId = Safe(() => type.UniqueId) ?? Rid.Value(type.Id).ToString(CultureInfo.InvariantCulture),
                Category = "CADLinkType",
                Action = PlannedAction.Modify,
                GeometryFingerprint = beforePrint,
                BeforeValues = new Dictionary<string, string>
                {
                    { "path", facts.ExternalPath ?? "" },
                    { "file_sha256", beforeFile.Value<string>("sha256") ?? "" }
                }
            });

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            if (dryRun)
            {
                var rehearsal = new JObject
                {
                    ["operation"] = "reload",
                    ["document"] = SafeTitle(doc),
                    ["dry_run"] = true,
                    ["wrote_nothing"] = true,
                    ["instance"] = facts.ToJson(),
                    ["file_on_disk_now"] = beforeFile,
                    ["geometry_fingerprint_now"] = beforePrint,
                    ["would_do"] = "CADLinkType.Reload(). The drawing's geometry is replaced by whatever the " +
                                   "file on disk says NOW. Anything built from the old reading keeps its " +
                                   "provenance and becomes checkable with horizun_audit_cad_model.",
                    ["rehearsal_kind"] = "measured_preview",
                    ["api_limits"] = ApiLimits()
                };
                DocumentGate.RecordResolvedPlan(resolved);
                ApplicationOutcome.StampRehearsal(rehearsal, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(rehearsal, gate, ToolName, hash, true,
                    "the token binds the file's bytes and the drawing's current geometry fingerprint; a file " +
                    "or a link somebody changed first refuses as a stale plan.");
                return CommandResult.Ok(rehearsal);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, ToolName, hash, resolved, null);
            if (refusal != null) return refusal;

            LinkLoadResult outcome;
            using (var t = new Transaction(doc, "Horizun: reload a CAD drawing"))
            {
                t.Start();
                outcome = type.Reload();
                t.Commit();
            }

            JObject after;
            string verifyError = Measure(doc, facts.ElementId, out after);
            if (verifyError != null)
                return CommandResult.Fail("the reload ran and the instance could not be re-read: " + verifyError);
            string afterPrint = Fingerprint(doc, facts.ElementId);

            var result = new JObject
            {
                ["operation"] = "reload",
                ["document"] = SafeTitle(doc),
                ["dry_run"] = false,
                ["element_id"] = facts.ElementId,
                ["load_result"] = LoadResultJson(outcome),
                ["instance"] = after,
                ["geometry_fingerprint_before"] = beforePrint,
                ["geometry_fingerprint_after"] = afterPrint,
                ["geometry_changed"] = !string.Equals(beforePrint, afterPrint, StringComparison.Ordinal),
                ["host_verified"] = true,
                ["verified_by"] = "the drawing's CONTENT, before and after: the SHA-256 of the file the link " +
                                  "resolves to and a fingerprint over the geometry Revit hands back. CADLinkType " +
                                  "publishes no status that changes across a reload, so a status check would " +
                                  "have been true by construction.",
                ["unchanged_means"] = "geometry_changed=false is a real answer, not a failure: reloading a file " +
                                      "nobody has edited changes nothing, and saying so is more useful than " +
                                      "implying work happened.",
                ["api_limits"] = ApiLimits()
            };
            ApplicationOutcome.StampApplied(result, "Committed", 1, 1, 1, 0, 0, 0);
            DocumentGate.StampConfirmation(result, gate, ToolName, hash, false);
            return CommandResult.Ok(result);
        }

        // =====================================================================
        // REPOINT
        // =====================================================================
        private static CommandResult Repoint(UIApplication app, Document doc, JObject request, GateResult gate)
        {
            CadInstanceFacts facts;
            CADLinkType type;
            string find = Resolve(doc, request, out facts, out type);
            if (find != null) return CommandResult.Fail(find);

            string target = request.Value<string>("file_path");
            if (string.IsNullOrWhiteSpace(target))
                return CommandResult.Fail("file_path is required for repoint: the file to point this link at.");

            JObject targetFile;
            string fileError = ReadFile(target, out targetFile);
            if (fileError != null) return CommandResult.Fail(fileError);

            string beforePrint = Fingerprint(doc, facts.ElementId);
            string hash = DocumentGate.PlanHash(request, "operation", "instance_id", "file_path");
            var resolved = new ResolvedPlan();
            resolved.Elements.Add(new PlannedElement
            {
                UniqueId = Safe(() => type.UniqueId) ?? Rid.Value(type.Id).ToString(CultureInfo.InvariantCulture),
                Category = "CADLinkType",
                Action = PlannedAction.Modify,
                GeometryFingerprint = beforePrint,
                BeforeValues = new Dictionary<string, string>
                {
                    { "path", facts.ExternalPath ?? "" },
                    { "target_sha256", targetFile.Value<string>("sha256") ?? "" }
                }
            });

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            if (dryRun)
            {
                var rehearsal = new JObject
                {
                    ["operation"] = "repoint",
                    ["document"] = SafeTitle(doc),
                    ["dry_run"] = true,
                    ["wrote_nothing"] = true,
                    ["instance"] = facts.ToJson(),
                    ["from_path"] = facts.ExternalPath,
                    ["to_file"] = targetFile,
                    ["geometry_fingerprint_now"] = beforePrint,
                    ["would_do"] = "CADLinkType.LoadFrom(path). The link keeps its element id and everything " +
                                   "hosted on it, and starts reading a DIFFERENT drawing. If elements were " +
                                   "built from the old one, their provenance still names it - audit before " +
                                   "assuming the model still matches.",
                    ["rehearsal_kind"] = "measured_preview",
                    ["api_limits"] = ApiLimits()
                };
                DocumentGate.RecordResolvedPlan(resolved);
                ApplicationOutcome.StampRehearsal(rehearsal, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(rehearsal, gate, ToolName, hash, true,
                    "the token binds the link's current path and the bytes of the file it is being aimed at.");
                return CommandResult.Ok(rehearsal);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, ToolName, hash, resolved, null);
            if (refusal != null) return refusal;

            LinkLoadResult outcome;
            using (var t = new Transaction(doc, "Horizun: repoint a CAD link"))
            {
                t.Start();
                outcome = type.LoadFrom(target);
                t.Commit();
            }

            JObject after;
            string verifyError = Measure(doc, facts.ElementId, out after);
            if (verifyError != null)
                return CommandResult.Fail("the repoint ran and the instance could not be re-read: " + verifyError);

            string nowPath = after.Value<string>("external_path");
            bool arrived = !string.IsNullOrEmpty(nowPath) &&
                           string.Equals(NormalisePath(nowPath), NormalisePath(target), StringComparison.OrdinalIgnoreCase);
            var result = new JObject
            {
                ["operation"] = "repoint",
                ["document"] = SafeTitle(doc),
                ["dry_run"] = false,
                ["element_id"] = facts.ElementId,
                ["load_result"] = LoadResultJson(outcome),
                ["instance"] = after,
                ["from_path"] = facts.ExternalPath,
                ["to_path_requested"] = target,
                ["points_at_requested_file"] = arrived,
                ["geometry_fingerprint_before"] = beforePrint,
                ["geometry_fingerprint_after"] = Fingerprint(doc, facts.ElementId),
                ["host_verified"] = arrived,
                ["verified_by"] = "the link's resolved path was re-read from the model after the commit and " +
                                  "compared with the path that was asked for.",
                ["api_limits"] = ApiLimits()
            };
            if (!arrived)
                result["disagreement"] = new JObject
                {
                    ["what"] = "the link's resolved path",
                    ["asked_for"] = target,
                    ["now"] = nowPath,
                    ["means"] = "Revit accepted the call and the link resolves somewhere else. host_verified is " +
                                "false; do not treat this as done."
                };
            ApplicationOutcome.StampApplied(result, "Committed", 1, 1, (arrived ? 1 : 0), 0, (arrived ? 0 : 1), 0);
            DocumentGate.StampConfirmation(result, gate, ToolName, hash, false);
            return CommandResult.Ok(result);
        }

        // =====================================================================
        // SHARED
        // =====================================================================

        /// <summary>
        /// Which CAD instance, and the type behind it. A caller names the
        /// INSTANCE, because that is what every other CAD command in this bridge
        /// takes; the type is derived rather than asked for.
        /// </summary>
        private static string Resolve(Document doc, JObject request, out CadInstanceFacts facts, out CADLinkType type)
        {
            facts = null; type = null;
            long instanceId = request.Value<long?>("instance_id") ?? -1;
            if (instanceId < 0 || !Rid.CanRepresent(instanceId))
                return "instance_id is required: which CAD instance. List them with operation='list', or with " +
                       "horizun_query_cad mode='instances'.";

            Element element = doc.GetElement(Rid.Make(instanceId));
            if (element == null) return "No element " + instanceId + " in '" + SafeTitle(doc) + "'.";
            var instance = element as ImportInstance;
            if (instance == null)
                return "Element " + instanceId + " is a " + element.GetType().Name + ", not an ImportInstance.";

            List<JObject> unreadable;
            facts = CadFacts.Collect(doc, out unreadable).FirstOrDefault(f => f.ElementId == instanceId);
            if (facts == null)
                return "CAD instance " + instanceId + " could not be measured; horizun_query_cad lists it as " +
                       "unreadable, and this command will not act on a drawing it cannot describe.";

            type = doc.GetElement(instance.GetTypeId()) as CADLinkType;
            if (type == null)
                return "CAD instance " + instanceId + " has no CADLinkType behind it. That is what an IMPORTED " +
                       "drawing looks like - its geometry was copied into the model and there is no link to " +
                       "reload or repoint. Only a LINKED drawing can be.";
            // IsLinked is bool?, and NULL IS NOT FALSE: null means Revit would not
            // say, which is a different answer from "this is an import". Only an
            // explicit false is treated as one.
            if (facts.IsLinked == false)
                return "CAD instance " + instanceId + " is IMPORTED, not linked. An import has no external file " +
                       "to reload from: its geometry is part of the model. Delete it and add a link instead if " +
                       "that is what you meant.";
            return null;
        }

        /// <summary>Read the DWG on disk, or say exactly why not. Never guesses from the extension alone.</summary>
        private static string ReadFile(string path, out JObject facts)
        {
            facts = null;
            if (string.IsNullOrWhiteSpace(path)) return "no path was given.";
            FileInfo info;
            try { info = new FileInfo(path); }
            catch (Exception ex) { return "the path is not one this machine can use: " + ex.Message; }
            if (!info.Exists)
                return "file_not_found: '" + path + "' does not exist ON THE MACHINE RUNNING REVIT, which is " +
                       "this one. Nothing was changed.";
            if (info.Length == 0)
                return "empty_file: '" + Path.GetFileName(path) + "' is zero bytes. Linking it would put an " +
                       "empty drawing in the model and every rule would report matching nothing.";

            string extension = (info.Extension ?? "").ToLowerInvariant();
            if (extension != ".dwg" && extension != ".dxf")
                return "not_a_drawing: '" + info.Name + "' has extension '" + extension + "'. This command links " +
                       "DWG and DXF.";

            // THE EXTENSION IS A CLAIM; the first bytes are evidence. A .dwg that
            // is really something else fails inside Revit with a message about
            // the file rather than about the name.
            string magic = null;
            try
            {
                using (FileStream fs = info.OpenRead())
                {
                    var head = new byte[6];
                    int read = fs.Read(head, 0, head.Length);
                    if (read >= 6) magic = System.Text.Encoding.ASCII.GetString(head, 0, 6);
                }
            }
            catch (Exception ex) { return "the file is there and could not be read: " + ex.Message; }

            bool looksLikeDwg = magic != null && magic.StartsWith("AC10", StringComparison.Ordinal);
            string sha;
            try { sha = Sha256(info.FullName); }
            catch (Exception ex) { return "the file could not be hashed: " + ex.Message; }

            facts = new JObject
            {
                ["name"] = info.Name,
                ["bytes"] = info.Length,
                ["sha256"] = sha,
                ["modified_utc"] = info.LastWriteTimeUtc.ToString("o"),
                ["extension"] = extension,
                ["header"] = magic,
                ["header_looks_like_dwg"] = looksLikeDwg,
                ["header_means"] = looksLikeDwg
                    ? "the first bytes are a DWG version marker (AC10xx)"
                    : "the first bytes are NOT a DWG version marker. The extension says one thing and the " +
                      "content says another; Revit will decide, and it may refuse."
            };
            return null;
        }

        /// <summary>The import options a caller declared, validated, plus what was actually set.</summary>
        private static DWGImportOptions BuildOptions(JObject request, out string error, out JObject declared)
        {
            error = null;
            var options = new DWGImportOptions();
            declared = new JObject();

            string units = (request.Value<string>("units") ?? "default").Trim().ToLowerInvariant();
            int? ordinal = UnitOrdinal(units);
            if (ordinal == null)
            {
                error = "units '" + units + "' is not one this Revit knows. THE MEMBER SET DIFFERS BETWEEN " +
                        "VERSIONS - ussurveyfoot exists from Revit 2024 and not in 2023 - so this is resolved " +
                        "against the enum THIS build was compiled with rather than a list written down " +
                        "somewhere. Known here: " + string.Join(", ", KnownUnits()) + ".";
                return null;
            }
            options.Unit = (ImportUnit)ordinal.Value;
            declared["units"] = units;
            declared["units_ordinal"] = ordinal.Value;

            string placement = (request.Value<string>("placement") ?? "origin").Trim().ToLowerInvariant();
            switch (placement)
            {
                case "site": options.Placement = ImportPlacement.Site; break;
                case "origin": options.Placement = ImportPlacement.Origin; break;
                case "centered": options.Placement = ImportPlacement.Centered; break;
                case "shared": options.Placement = ImportPlacement.Shared; break;
                default:
                    error = "placement '" + placement + "' is not one of site, origin, centered, shared.";
                    return null;
            }
            declared["placement"] = placement;

            string colours = (request.Value<string>("colors") ?? "preserved").Trim().ToLowerInvariant();
            switch (colours)
            {
                case "preserved": options.ColorMode = ImportColorMode.Preserved; break;
                case "inverted": options.ColorMode = ImportColorMode.Inverted; break;
                case "black_and_white": options.ColorMode = ImportColorMode.BlackAndWhite; break;
                default:
                    error = "colors '" + colours + "' is not one of preserved, inverted, black_and_white.";
                    return null;
            }
            declared["colors"] = colours;

            bool thisViewOnly = request.Value<bool?>("current_view_only") ?? true;
            options.ThisViewOnly = thisViewOnly;
            declared["this_view_only"] = thisViewOnly;

            bool orient = request.Value<bool?>("orient_to_view") ?? false;
            options.OrientToView = orient;
            declared["orient_to_view"] = orient;

            bool visibleOnly = request.Value<bool?>("visible_layers_only") ?? false;
            options.VisibleLayersOnly = visibleOnly;
            declared["visible_layers_only"] = visibleOnly;

            bool autoCorrect = request.Value<bool?>("auto_correct_almost_vertical") ?? false;
            options.AutoCorrectAlmostVHLines = autoCorrect;
            declared["auto_correct_almost_vertical"] = autoCorrect;

            double? scale = request.Value<double?>("custom_scale");
            if (scale.HasValue)
            {
                if (scale.Value <= 0 || double.IsNaN(scale.Value) || double.IsInfinity(scale.Value))
                {
                    error = "custom_scale must be a positive finite number.";
                    return null;
                }
                options.CustomScale = scale.Value;
                declared["custom_scale"] = scale.Value;
            }

            JArray layers = request["visible_layers"] as JArray;
            if (layers != null && layers.Count > 0)
            {
                var wanted = layers.Select(l => (string)l).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                if (wanted.Count > 0)
                {
                    try { options.SetLayerSelection(wanted); }
                    catch (Exception ex)
                    {
                        error = "visible_layers was refused by Revit: " + ex.Message;
                        return null;
                    }
                    declared["visible_layers"] = new JArray(wanted);
                }
            }
            return options;
        }

        /// <summary>
        /// The ordinal for a unit NAME, resolved against the enum this build was
        /// compiled against. ImportUnit.USSurveyFoot exists from 2024 and not in
        /// 2023, so naming it in source would fail the 2023 build - and falling
        /// back to Foot would be 2 parts per million, which is metres across a
        /// site plan. Unknown here means refused, by name.
        /// </summary>
        private static int? UnitOrdinal(string name)
        {
            foreach (object value in Enum.GetValues(typeof(ImportUnit)))
            {
                string known = value.ToString().ToLowerInvariant();
                if (string.Equals(known, name, StringComparison.Ordinal)) return (int)value;
            }
            return null;
        }

        private static IEnumerable<string> KnownUnits() =>
            Enum.GetValues(typeof(ImportUnit)).Cast<object>()
                .Select(v => v.ToString().ToLowerInvariant())
                .OrderBy(x => x, StringComparer.Ordinal);

        /// <summary>Re-read one CAD instance from the model. This is the post-commit evidence.</summary>
        private static string Measure(Document doc, long instanceId, out JObject json)
        {
            json = null;
            List<JObject> unreadable;
            CadInstanceFacts facts = CadFacts.Collect(doc, out unreadable).FirstOrDefault(f => f.ElementId == instanceId);
            if (facts == null) return "the instance is not in the document's CAD inventory";
            json = facts.ToJson();
            return null;
        }

        /// <summary>
        /// A fingerprint over the geometry Revit hands back for this instance -
        /// the only post-condition a CAD reload actually has.
        /// </summary>
        private static string Fingerprint(Document doc, long instanceId)
        {
            try
            {
                Element e = doc.GetElement(Rid.Make(instanceId));
                if (e == null) return null;
                CadHarvest harvest = CadGeometryHarvest.Harvest(doc, e, 5.0, 20000);
                // The SAME surrogates horizun_query_cad publishes, so a fingerprint
                // here and a set_fingerprint there are the same number and can be
                // compared across the two commands.
                return CadIdentity.SetFingerprint(harvest.Segments.Select(seg =>
                    CadIdentity.SurrogateUndirected(null, seg.Layer, "root", seg.SourceKind,
                        new List<CadPoint> { seg.A, seg.B }, 1.0)));
            }
            catch { return null; }
        }

        private static JObject ComparePathAndHash(JObject requested, JObject verified)
        {
            string want = requested?.Value<string>("sha256");
            string got = verified?.Value<string>("file_sha256");
            if (string.IsNullOrEmpty(want) || string.IsNullOrEmpty(got)) return null;
            if (string.Equals(want, got, StringComparison.Ordinal)) return null;
            return new JObject
            {
                ["what"] = "the file behind the link",
                ["asked_for_sha256"] = want,
                ["link_resolves_to_sha256"] = got,
                ["means"] = "the link committed, and the file it RESOLVES to is not the file that was asked " +
                            "for. Revit resolves a path through its own search rules, so this can happen when a " +
                            "file of the same name sits somewhere it looks first."
            };
        }

        private static JObject LoadResultJson(LinkLoadResult outcome)
        {
            if (outcome == null) return null;
            var o = new JObject();
            try { o["result"] = outcome.LoadResult.ToString(); } catch { }
            try { o["element_id"] = Rid.Value(outcome.ElementId); } catch { }
            try { o["is_nested"] = outcome.IsNested; } catch { }
            return o;
        }

        private static JObject ApiLimits() => new JObject
        {
            ["unload"] = "unavailable",
            ["unload_means"] = "Revit's API has no unload for a CAD link. CADLinkType declares Reload and " +
                               "LoadFrom and nothing else, in every version 2023-2027 - measured by reflection " +
                               "over each year's RevitAPI.dll. Delete the ImportInstance with " +
                               "horizun_delete_verified, or repoint the link.",
            ["path_type"] = "unavailable",
            ["path_type_means"] = "ExternalFileReference.PathType is read-only for a CAD link; there is no " +
                                  "setter to make one relative or absolute. RevitLinkType has one; CADLinkType " +
                                  "does not.",
            ["reload_status"] = "no status changes across a CAD reload, so this command verifies by CONTENT - " +
                                "the file's SHA-256 and a fingerprint over the geometry Revit hands back."
        };

        private static string NormalisePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return path.TrimEnd('\\', '/'); }
        }

        private static string Sha256(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static T Safe<T>(Func<T> read) where T : class { try { return read(); } catch { return null; } }
        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafeName(Element e) { try { return e.Name; } catch { return null; } }
        private static string SafeViewType(View v) { try { return v.ViewType.ToString(); } catch { return null; } }
    }
}
