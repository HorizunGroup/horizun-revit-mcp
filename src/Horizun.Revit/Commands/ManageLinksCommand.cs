// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_links - typed link management. Until this command, unloading
// or reloading a link from the bridge meant execute_python, which meant the
// self-reported tier for an operation the API answers about directly.
//
// TWO HONESTY LINES THIS COMMAND HOLDS:
//
//   * LOAD-STATE OPERATIONS CANNOT REHEARSE PROVISIONALLY. Unload/Reload are
//     document-management calls the Revit API forbids inside a transaction;
//     a provisional unload WOULD BE an unload. So dry_run here is a MEASURED
//     PREVIEW (current status, path, instance count - the facts the token then
//     binds), not a provisional write, and the reply says which one it got.
//     After apply, the status is RE-READ from the link type: that part of the
//     contract does not bend.
//
//   * PIN/UNPIN ARE ELEMENT WRITES and go the normal way: transaction,
//     postcondition inside it, re-read after commit.
//
// ADD and CHANGE_PATH landed once their verification story was worked out:
//
//   * add validates the file BEFORE anything is touched (absolute, exists,
//     .rvt) and verifies by re-reading the created type's status AND the
//     created instance from the model; a load that does not answer Loaded
//     fails loudly with both halves' states.
//   * change_path is the REWIRE, said in the plan: the dry run names the
//     current path, the new path and every instance that will re-resolve,
//     and the apply re-reads the external reference's absolute path from
//     the type - the fact that actually moved.
//   Neither runs inside a transaction (the API forbids it), so like the
//   load-state pair their dry runs are measured previews and say so.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ManageLinksCommand : ICommand
    {
        public string Name => "horizun_manage_links";
        public string Description =>
            "List, unload, reload, pin and unpin Revit links, typed and verified: load-state changes re-read " +
            "GetLinkedFileStatus after the call, pin changes re-read Pinned after commit. Load-state dry runs are " +
            "measured previews (the API forbids a provisional unload), and the reply says so.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            string operation = (request.Value<string>("operation") ?? "list").ToLowerInvariant();
            if (operation == "list")
            {
                Document readDoc = app.ActiveUIDocument?.Document;
                if (readDoc == null) return CommandResult.Fail("No document is open.");
                return List(readDoc);
            }
            if (operation == "add") return Add(app, doc0 => doc0, request);
            if (operation == "add_instance") return AddInstance(app, request);
            if (operation == "change_path") return ChangePath(app, request);
            if (operation != "unload" && operation != "reload" && operation != "pin" && operation != "unpin")
                return CommandResult.Fail("operation '" + operation + "' is not one this command understands. " +
                    "Known: list, unload, reload, pin, unpin, add, add_instance, change_path.");

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            if (operation == "pin" || operation == "unpin") return Pin(app, gate, doc, request, operation == "pin");
            return LoadState(app, gate, doc, request, operation);
        }

        // ------------------------------------------------------------------ list
        private static CommandResult List(Document doc)
        {
            var rows = new JArray();
            foreach (RevitLinkType type in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkType)).OfType<RevitLinkType>())
            {
                var instances = new JArray();
                foreach (RevitLinkInstance instance in new FilteredElementCollector(doc)
                             .OfClass(typeof(RevitLinkInstance)).OfType<RevitLinkInstance>()
                             .Where(i => i.GetTypeId() == type.Id))
                    instances.Add(new JObject
                    {
                        ["instance_id"] = Rid.Value(instance.Id),
                        ["pinned"] = Safe<bool>(() => instance.Pinned)
                    });
                rows.Add(new JObject
                {
                    ["link_type_id"] = Rid.Value(type.Id),
                    ["name"] = SafeName(type),
                    ["status"] = SafeStatus(type),
                    ["path"] = LinkPath(type),
                    ["instances"] = instances
                });
            }
            var listing = new JObject
            {
                ["document"] = doc.Title,
                ["links"] = rows,
                ["count"] = rows.Count
            };
            // A read, declared as the no-op it is. This command may now appear in an
            // atomic plan, and a plan reads every child's declaration; a listing with
            // none would stop the plan as 'uncertain' over a call that changed nothing.
            ApplicationOutcome.StampApplied(listing, ApplicationOutcome.NotStarted, 0, 0, 0, 0, 0, 0);
            return CommandResult.Ok(listing);
        }

        // ------------------------------------------------------------- load state
        private static CommandResult LoadState(UIApplication app, GateResult gate, Document doc,
                                               JObject request, string operation)
        {
            long typeId = request.Value<long?>("link_type_id") ?? -1;
            RevitLinkType type = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) as RevitLinkType : null;
            if (type == null) return CommandResult.Fail("link_type_id must identify a RevitLinkType; operation=list shows them.");

            string statusBefore = SafeStatus(type);
            string path = LinkPath(type);
            if (operation == "unload" && statusBefore == "Unloaded")
                return CommandResult.Fail("'" + SafeName(type) + "' is already Unloaded; unloading it again is a " +
                    "no-op this command refuses rather than reports as work.");
            if (operation == "reload" && path != null && !System.IO.File.Exists(path) && !path.StartsWith("BIM 360:", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("'" + SafeName(type) + "' points at '" + path + "', which does not exist " +
                    "on disk; a reload would fail inside Revit's own dialog machinery. Fix the path first. " +
                    "Nothing was changed.");

            string hash = DocumentGate.PlanHash(request, "operation", "link_type_id");
            var resolved = new ResolvedPlan
            {
                Command = "horizun_manage_links",
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            resolved.Elements.Add(new PlannedElement
            {
                UniqueId = Safe(() => type.UniqueId) ?? typeId.ToString(),
                Category = "RevitLinkType",
                Action = PlannedAction.Modify,
                BeforeValues = new Dictionary<string, string>
                {
                    { "status", statusBefore ?? "" },
                    { "path", path ?? "" }
                }
            });

            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            if (dry)
            {
                DocumentGate.RecordResolvedPlan(resolved);
                var preview = new JObject
                {
                    ["dry_run"] = true,
                    ["rehearsal_kind"] = "measured_preview",
                    ["note"] = "Load-state changes cannot rehearse provisionally - the API forbids them inside a " +
                               "transaction, and a provisional unload would BE an unload. This preview is the " +
                               "MEASURED current state the token binds; apply performs the change and re-reads " +
                               "the status.",
                    ["link"] = new JObject
                    {
                        ["link_type_id"] = typeId, ["name"] = SafeName(type),
                        ["status"] = statusBefore, ["path"] = path
                    },
                    ["would"] = operation
                };
                ApplicationOutcome.StampRehearsal(preview, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, "horizun_manage_links", hash, true,
                    "the token binds this link type's measured status and path; a link somebody else " +
                    "unloaded or reloaded first refuses as a stale plan.");
                return CommandResult.Ok(preview);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, "horizun_manage_links",
                                                                     hash, resolved, null);
            if (refusal != null) return refusal;

            string error = null;
            try
            {
                if (operation == "unload") type.Unload(null);
                else type.Reload();
            }
            catch (Exception ex) { error = ex.Message; }

            string statusAfter = SafeStatus(doc.GetElement(type.Id) as RevitLinkType);
            string wanted = operation == "unload" ? "Unloaded" : "Loaded";
            bool verified = error == null && string.Equals(statusAfter, wanted, StringComparison.Ordinal);
            var result = new JObject
            {
                ["dry_run"] = false,
                ["operation"] = operation,
                ["link_type_id"] = typeId,
                ["name"] = SafeName(type),
                ["status_before"] = statusBefore,
                ["status_after_reread"] = statusAfter,
                ["verified"] = verified
            };
            if (error != null) result["error"] = error;
            ApplicationOutcome.StampApplied(result, verified ? ApplicationOutcome.Committed : ApplicationOutcome.NotStarted,
                                            1, verified ? 1 : 0, verified ? 1 : 0, 0, verified ? 0 : 1, 0);
            if (!verified)
                return CommandResult.FailWithDetail("The link did not re-read as " + wanted + " after " + operation +
                    (error != null ? " (" + error + ")" : "") + "; status now reads '" + statusAfter +
                    "'. Success is not claimed.", result);
            return CommandResult.Ok(result);
        }

        // -------------------------------------------------------------------- pin
        private static CommandResult Pin(UIApplication app, GateResult gate, Document doc,
                                         JObject request, bool pin)
        {
            long instanceId = request.Value<long?>("link_instance_id") ?? -1;
            RevitLinkInstance instance = Rid.CanRepresent(instanceId)
                ? doc.GetElement(Rid.Make(instanceId)) as RevitLinkInstance : null;
            if (instance == null)
                return CommandResult.Fail("link_instance_id must identify a RevitLinkInstance; operation=list shows them.");
            bool pinnedBefore = Safe<bool>(() => instance.Pinned) == true;
            if (pinnedBefore == pin)
                return CommandResult.Fail("instance " + instanceId + " is already " + (pin ? "pinned" : "unpinned") +
                    "; this command refuses a no-op rather than reporting it as work.");

            string hash = DocumentGate.PlanHash(request, "operation", "link_instance_id");
            var resolved = new ResolvedPlan
            {
                Command = "horizun_manage_links",
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            resolved.Elements.Add(new PlannedElement
            {
                UniqueId = Safe(() => instance.UniqueId) ?? instanceId.ToString(),
                Category = "RevitLinkInstance",
                Action = PlannedAction.Modify,
                BeforeValues = new Dictionary<string, string> { { "pinned", pinnedBefore ? "true" : "false" } }
            });

            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            if (dry)
            {
                DocumentGate.RecordResolvedPlan(resolved);
                var preview = new JObject
                {
                    ["dry_run"] = true,
                    ["instance_id"] = instanceId,
                    ["pinned"] = pinnedBefore,
                    ["would"] = pin ? "pin" : "unpin"
                };
                ApplicationOutcome.StampRehearsal(preview, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, "horizun_manage_links", hash, true,
                    "the token binds this instance's measured pinned state.");
                return CommandResult.Ok(preview);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, "horizun_manage_links",
                                                                     hash, resolved, null);
            if (refusal != null) return refusal;

            string txName = "Horizun: " + (pin ? "pin" : "unpin") + " link";
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    instance.Pinned = pin;
                    if ((doc.GetElement(instance.Id) as RevitLinkInstance)?.Pinned != pin)
                        throw new InvalidOperationException("the pinned state did not take inside the transaction");
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx);
                    return CommandResult.Fail("Pin change failed and was rolled back: " + ex.Message);
                }
            }
            bool pinnedAfter = Safe<bool>(() => (doc.GetElement(instance.Id) as RevitLinkInstance)?.Pinned) == true;
            var applied = new JObject
            {
                ["dry_run"] = false,
                ["instance_id"] = instanceId,
                ["pinned_before"] = pinnedBefore,
                ["pinned_after_reread"] = pinnedAfter,
                ["verified"] = pinnedAfter == pin
            };
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed, 1,
                                            pinnedAfter == pin ? 1 : 0, pinnedAfter == pin ? 1 : 0, 0,
                                            pinnedAfter == pin ? 0 : 1, 0);
            if (pinnedAfter != pin)
                return CommandResult.FailWithDetail("The transaction committed but the pinned state re-read wrong.", applied);
            return CommandResult.Ok(applied);
        }

        // ----------------------------------------------------------------- helpers
        // ---- add: a new link, validated before and re-read after. ---------------
        private static CommandResult Add(UIApplication app, Func<Document, Document> _, JObject request)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, "horizun_manage_links");
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            string path = request.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
                return CommandResult.Fail("path is required and must be absolute.");
            if (!System.IO.File.Exists(path))
                return CommandResult.Fail("'" + path + "' does not exist. Nothing was linked.");
            if (!path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("'" + path + "' is not a .rvt. This operation links Revit models; CAD " +
                    "formats have their own import surfaces.");
            // Revit refuses a second link at the SAME path with a raw ArgumentException
            // mid-transaction; measured on run 9. Refuse it here instead, by name, with
            // the existing type's id - so the caller learns what to reuse.
            foreach (RevitLinkType existing in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkType)).OfType<RevitLinkType>())
            {
                string existingPath = LinkPath(existing);
                if (existingPath != null && string.Equals(System.IO.Path.GetFullPath(existingPath),
                        System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("'" + path + "' is ALREADY LINKED as type " + Rid.Value(existing.Id) +
                        ". Revit holds one link type per path: place another instance of that type, or " +
                        "change_path it. Nothing was linked.");
            }

            string hash = DocumentGate.PlanHash(request, "operation", "path");
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            if (dryRun)
            {
                var preview = new JObject
                {
                    ["dry_run"] = true,
                    ["mode"] = "measured_preview",
                    ["would"] = "add",
                    ["path"] = path,
                    ["note"] = "RevitLinkType.Create runs outside a transaction, so this preview measured the " +
                               "file's presence and nothing else; the apply re-reads type status and instance."
                };
                ApplicationOutcome.StampRehearsal(preview, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, "horizun_manage_links", hash, true);
                return CommandResult.Ok(preview);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, "horizun_manage_links", hash);
            if (refusal != null) return refusal;

            ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
            LinkLoadResult loadResult;
            RevitLinkInstance instance;
            using (var tx = new Transaction(doc, "Horizun: add link"))
            {
                tx.Start();
                loadResult = RevitLinkType.Create(doc, modelPath, new RevitLinkOptions(false));
                if (loadResult == null || !LinkLoadResult.IsCodeSuccess(loadResult.LoadResult))
                {
                    Guard.RollBack(tx);
                    return CommandResult.Fail("RevitLinkType.Create answered '" +
                        (loadResult == null ? "(null)" : loadResult.LoadResult.ToString()) +
                        "'; the link was NOT created and the transaction rolled back.");
                }
                instance = RevitLinkInstance.Create(doc, loadResult.ElementId);
                Guard.Commit(tx, "Horizun: add link");
            }

            RevitLinkType reread = doc.GetElement(loadResult.ElementId) as RevitLinkType;
            RevitLinkInstance instanceReread = doc.GetElement(instance.Id) as RevitLinkInstance;
            string status = SafeStatus(reread);
            if (reread == null || instanceReread == null || status != "Loaded")
                return CommandResult.Fail("The link committed but the re-read does not hold: type " +
                    (reread == null ? "(gone)" : status) + ", instance " +
                    (instanceReread == null ? "(gone)" : "present") + ". Success is not claimed.");
            var added = new JObject
            {
                ["operation"] = "add",
                ["link_type_id"] = Rid.Value(reread.Id),
                ["link_instance_id"] = Rid.Value(instanceReread.Id),
                ["status_after"] = status,
                ["path"] = path,
                ["verified_after_reread"] = true
            };
            // Committed through Guard.Commit above, and both halves re-read: the literal
            // status is earned here, not assumed.
            ApplicationOutcome.StampApplied(added, ApplicationOutcome.Committed, 1, 1, 1, 0, 0, 0);
            return CommandResult.Ok(added);
        }

        // ---- add_instance: a SECOND placement of a link already in the model. ----
        //
        // THE DEAD END THIS CLOSES. Revit holds one RevitLinkType per path, and `add`
        // says so when it refuses a duplicate: "place another instance of that type, or
        // change_path it." There was no way to place another instance. The advice named
        // an operation the command did not have, so the only route to a normal Revit
        // situation - one linked file placed twice, two towers, a mirrored wing - was
        // arbitrary Python, which is off by default and reports nothing verified.
        //
        // It matters beyond the fixture. A takeoff across links reports one row per
        // element PER PLACEMENT, told apart by link_instance_id; a bridge that cannot
        // create the second placement cannot demonstrate that it tells them apart.
        //
        // RevitLinkInstance.Create runs INSIDE a transaction, so unlike add's own
        // RevitLinkType.Create this could rehearse provisionally - but rehearsing would
        // mean creating an instance and rolling it back, and the instance count is the
        // fact the token binds. The dry run measures instead, and says which it was.
        private static CommandResult AddInstance(UIApplication app, JObject request)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, "horizun_manage_links");
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            JToken typeToken = request["link_type_id"];
            if (typeToken == null || typeToken.Type != JTokenType.Integer)
                return CommandResult.Fail("add_instance needs link_type_id: the RevitLinkType to place again, from " +
                    "operation=list. Nothing was placed.");
            long rawId = (long)typeToken;
            if (!Rid.CanRepresentElementId(rawId)) return CommandResult.Fail(Rid.ElementIdRangeError(rawId));
            RevitLinkType type = doc.GetElement(Rid.ToElementId(rawId)) as RevitLinkType;
            if (type == null)
                return CommandResult.Fail("Element " + rawId + " is not a RevitLinkType in '" + doc.Title +
                    "'. operation=list names every link type this document holds. Nothing was placed.");

            string status = SafeStatus(type);
            if (status != "Loaded")
                return CommandResult.Fail("Link type " + rawId + " is '" + status + "', not Loaded. An instance of a " +
                    "link that is not loaded would be a placement of nothing: reload it first. Nothing was placed.");

            List<ElementId> before = InstancesOf(doc, type.Id);
            string hash = DocumentGate.PlanHash(request, "operation", "link_type_id");
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            if (dryRun)
            {
                var preview = new JObject
                {
                    ["dry_run"] = true,
                    ["mode"] = "measured_preview",
                    ["would"] = "add_instance",
                    ["link_type_id"] = rawId,
                    ["name"] = SafeName(type),
                    ["path"] = LinkPath(type),
                    ["status"] = status,
                    ["instances_before"] = before.Count,
                    ["instances_after_if_applied"] = before.Count + 1,
                    ["note"] = "the new instance is placed at the link type's own origin, exactly as Revit's Manage " +
                               "Links places one. Move it afterwards with horizun_transform_elements if it must sit " +
                               "somewhere else. A takeoff across links reports this placement's rows under ITS OWN " +
                               "link_instance_id, and counts the linked elements once per placement."
                };
                ApplicationOutcome.StampRehearsal(preview, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, "horizun_manage_links", hash, true);
                return CommandResult.Ok(preview);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, "horizun_manage_links", hash);
            if (refusal != null) return refusal;

            RevitLinkInstance created;
            using (var tx = new Transaction(doc, "Horizun: place link instance"))
            {
                tx.Start();
                created = RevitLinkInstance.Create(doc, type.Id);
                if (created == null)
                {
                    Guard.RollBack(tx);
                    return CommandResult.Fail("RevitLinkInstance.Create returned nothing; the transaction rolled back " +
                        "and no instance was placed.");
                }
                Guard.Commit(tx, "Horizun: place link instance");
            }

            // RE-READ, both halves: the instance is in the model AND it is an instance of
            // the type that was asked for. A created element that points somewhere else is
            // a placement of the wrong link, which is worse than a failure.
            var reread = doc.GetElement(created.Id) as RevitLinkInstance;
            List<ElementId> after = InstancesOf(doc, type.Id);
            bool ofThisType = reread != null && reread.GetTypeId() == type.Id;
            if (reread == null || !ofThisType || after.Count != before.Count + 1)
                return CommandResult.Fail("The placement committed but the re-read does not hold: instance " +
                    (reread == null ? "(gone)" : "present, of type " + Rid.Value(reread.GetTypeId())) +
                    ", instances of type " + rawId + " went from " + before.Count + " to " + after.Count +
                    ". Success is not claimed.");

            var placed = new JObject
            {
                ["operation"] = "add_instance",
                ["link_type_id"] = rawId,
                ["link_instance_id"] = Rid.Value(reread.Id),
                ["name"] = SafeName(type),
                ["path"] = LinkPath(type),
                ["status_after"] = SafeStatus(type),
                ["instances_before"] = before.Count,
                ["instances_after"] = after.Count,
                ["instance_ids_after"] = new JArray(after.Select(i => (object)Rid.Value(i)).ToArray()),
                ["verified_after_reread"] = true,
                ["means"] = "this linked file is now placed " + after.Count + " time(s) in '" + doc.Title +
                            "'. A quantity takeoff with include_links measures its elements once PER PLACEMENT and " +
                            "stamps every row with the link_instance_id it came through."
            };
            ApplicationOutcome.StampApplied(placed, ApplicationOutcome.Committed, 1, 1, 1, 0, 0, 0);
            return CommandResult.Ok(placed);
        }

        private static List<ElementId> InstancesOf(Document doc, ElementId typeId)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .OfType<RevitLinkInstance>()
                .Where(i => i.GetTypeId() == typeId)
                .Select(i => i.Id)
                .OrderBy(i => Rid.Value(i))
                .ToList();
        }

        // ---- change_path: the rewire, said and then re-read. --------------------
        private static CommandResult ChangePath(UIApplication app, JObject request)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, "horizun_manage_links");
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            long typeId = request.Value<long?>("link_type_id") ?? -1;
            RevitLinkType type = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) as RevitLinkType : null;
            if (type == null) return CommandResult.Fail("link_type_id must identify a RevitLinkType; operation=list shows them.");
            string path = request.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
                return CommandResult.Fail("path is required and must be absolute.");
            if (!System.IO.File.Exists(path))
                return CommandResult.Fail("'" + path + "' does not exist. The link still points where it did.");
            if (!path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("'" + path + "' is not a .rvt.");

            string pathBefore = LinkPath(type);
            var instances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .OfType<RevitLinkInstance>().Where(i => Rid.Value(i.GetTypeId()) == Rid.Value(type.Id)).ToList();

            string hash = DocumentGate.PlanHash(request, "operation", "link_type_id", "path");
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            if (dryRun)
            {
                var preview = new JObject
                {
                    ["dry_run"] = true,
                    ["mode"] = "measured_preview",
                    ["would"] = "change_path",
                    ["link_type_id"] = Rid.Value(type.Id),
                    ["path_before"] = pathBefore,
                    ["path_after"] = path,
                    ["instances_that_re_resolve"] = instances.Count,
                    ["note"] = "THIS IS THE REWIRE: every instance above will show the new model after apply. " +
                               "LoadFrom runs outside a transaction; the apply re-reads the type's external path."
                };
                ApplicationOutcome.StampRehearsal(preview, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, "horizun_manage_links", hash, true);
                return CommandResult.Ok(preview);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, "horizun_manage_links", hash);
            if (refusal != null) return refusal;

            LinkLoadResult loadResult = type.LoadFrom(
                ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new WorksetConfiguration());
            string status = SafeStatus(doc.GetElement(type.Id) as RevitLinkType);
            string pathAfter = LinkPath(doc.GetElement(type.Id) as RevitLinkType);
            bool ok = loadResult != null && LinkLoadResult.IsCodeSuccess(loadResult.LoadResult) &&
                      status == "Loaded" &&
                      string.Equals(System.IO.Path.GetFullPath(pathAfter ?? ""), System.IO.Path.GetFullPath(path),
                                    StringComparison.OrdinalIgnoreCase);
            if (!ok)
                return CommandResult.FailWithDetail("LoadFrom answered '" +
                    (loadResult == null ? "(null)" : loadResult.LoadResult.ToString()) + "' and the re-read shows " +
                    "status '" + status + "', path '" + pathAfter + "'. The claim 'repointed' is not made.",
                    new JObject { ["path_before"] = pathBefore, ["path_after_reread"] = pathAfter });
            var repointed = new JObject
            {
                ["operation"] = "change_path",
                ["link_type_id"] = Rid.Value(type.Id),
                ["path_before"] = pathBefore,
                ["path_after"] = pathAfter,
                ["status_after"] = status,
                ["instances_re_resolved"] = instances.Count,
                ["verified_after_reread"] = true
            };
            // The same shape the load-state path declares: LoadFrom runs outside a
            // transaction, so the status stands for 'the re-read held', which `ok`
            // above established before this line is reached.
            ApplicationOutcome.StampApplied(repointed, ApplicationOutcome.Committed, 1, 1, 1, 0, 0, 0);
            return CommandResult.Ok(repointed);
        }

        private static string SafeStatus(RevitLinkType type)
        {
            try { return type?.GetLinkedFileStatus().ToString(); } catch { return null; }
        }

        private static string LinkPath(RevitLinkType type)
        {
            try
            {
                ExternalFileReference reference = type.GetExternalFileReference();
                if (reference == null) return null;
                return ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
            }
            catch { return null; }
        }

        private static string SafeName(Element element)
        {
            try { return element?.Name; } catch { return null; }
        }

        private static T? Safe<T>(Func<T?> read) where T : struct
        {
            try { return read(); } catch { return null; }
        }

        private static string Safe(Func<string> read)
        {
            try { return read(); } catch { return null; }
        }
    }
}
