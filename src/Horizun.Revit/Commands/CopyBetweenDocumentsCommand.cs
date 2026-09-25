// -----------------------------------------------------------------------------
// Horizun Revit MCP - copying elements from one open document into this one.
// Original Horizun code.
//
// G21 of the 2026-09-14 competitive inventory, the half that was missing. The
// DWG-to-BIM half is extensive already (query_cad, plan_from_cad, apply_cad_plan,
// plan_cad_update, apply_cad_update, audit_cad_model, manage_cad_links); what
// nothing could do was copy between two Revit documents.
// ElementTransformUtils.CopyElements appeared once in the whole tree, and only in
// its single-document form.
//
// THE DIRECTION IS FIXED, AND THAT IS THE DESIGN. This bridge's central guard is
// that a write goes to the ACTIVE document and nowhere else - every typed command
// refuses a target that is not active, because "the right edit in the wrong model"
// is the failure nobody notices until much later. A copy has two documents, so
// the rule is applied to the one being written: THE DESTINATION IS THE ACTIVE
// DOCUMENT, always, and the source is read. Copying "out" into a model somebody
// else has open would be exactly the write this bridge exists to refuse.
//
// WHAT REVIT DOES THAT A CALLER MUST BE TOLD ABOUT. CopyElements brings the
// TYPES with it, and Revit CANNOT rename one on the way in: DuplicateTypeAction
// has exactly two members, Abort and UseDestinationTypes - checked against the
// installed RevitAPI.dll rather than remembered. So a wall type named
// "Basic Wall - 200" that already exists in the destination with different layers
// leaves one real choice: take the destination's type, and accept that nothing
// compared their layers, or refuse the copy. Refusing is the default, and the
// reply names every type that DID arrive - measured as the difference in the
// destination's type set, not reported by the copy.
//
// AMBIGUITY REFUSES BEFORE WRITING. A source element whose category the
// destination does not have, a view-specific element copied without its view, a
// hosted element whose host is not coming along - each is measured and named
// first, because all three produce something that looks copied and is not.
//
// source_path (2026-09-25 field session): a library .rvt/.rte is USUALLY not
// open anywhere - it lives on a share, not in anyone's session - so requiring
// an open source shut out the exact files this exists for. source_path opens
// one in the BACKGROUND, shares the SAME version guard horizun_open_document
// uses (a file from another Revit year is refused, never silently upgraded),
// ALWAYS detaches (this is a read, never a worksharing session), and is closed
// WITHOUT SAVING before this command returns on every exit path - success,
// refusal or exception alike. Because ids from a document that was never open
// cannot be known ahead of time, type_names resolves by NAME instead: each name
// must match exactly one ElementType in the source (category narrows a
// collision), so what gets copied is what was named, not a guess among several.
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
    public sealed class CopyBetweenDocumentsCommand : ICommand
    {
        public string Name => "horizun_copy_between_documents";

        public string Description =>
            "Copy elements from another OPEN document into the active one, with type arrival and every " +
            "ambiguity named before anything is written.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            // The gate is applied to the DESTINATION, because the destination is what is
            // written. There is no mode in which this writes to a document that is not
            // the one in front of the user.
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document destination = gate.Document;

            double scale;
            if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale))
                return CommandResult.Fail("units must be mm, m or feet.");

            string sourceTitle = request.Value<string>("source_document");
            string sourcePath = request.Value<string>("source_path");
            bool wantsOpenTitle = !string.IsNullOrWhiteSpace(sourceTitle);
            bool wantsPath = !string.IsNullOrWhiteSpace(sourcePath);
            if (wantsOpenTitle == wantsPath)
                return CommandResult.Fail(
                    "give exactly one of source_document (the title of an OTHER document already open in this " +
                    "session) or source_path (a .rvt/.rte to open in the background, copy from and close without " +
                    "saving), not both, not neither.");

            Document source;
            bool sourceOpenedHere = false;
            JObject sourceOpenInfo = null;
            if (wantsOpenTitle)
            {
                source = FindOpen(app, sourceTitle, destination, out string sourceError);
                if (source == null) return CommandResult.Fail(sourceError);
            }
            else
            {
                // ONE guard for "should this file be opened", shared with
                // horizun_open_document: version mismatch refuses (never silently
                // upgrades - a library is somebody's shared reference, not this
                // caller's to upgrade in passing) and a central is ALWAYS detached,
                // because this is a read that closes unsaved, never a worksharing
                // session.
                var openReq = new OpenRequest
                {
                    CommandName = Name,
                    Path = sourcePath,
                    AllowUpgrade = false,
                    Detach = true,
                    Audit = false,
                    OpenAllWorksets = false,
                    OnOpenDialog = DialogAnswer.Cancel
                };
                OpenPlan plan = OpenGuard.Check(app, openReq);
                if (!plan.Ok) return plan.Refusal;
                try { source = app.Application.OpenDocumentFile(plan.ModelPath, plan.Options()); }
                catch (Exception ex)
                {
                    return CommandResult.Fail("Revit refused to open '" + sourcePath + "' in the background: " + ex.Message +
                        " (file is Revit " + (plan.FileVersion ?? "unknown") + ", host is Revit " + plan.HostVersion + "). Nothing was opened.");
                }
                if (source == null)
                    return CommandResult.Fail("Revit returned no document for '" + sourcePath + "'. Nothing is open, nothing was copied.");
                sourceOpenedHere = true;
                sourceOpenInfo = new JObject
                {
                    ["source_path"] = sourcePath,
                    ["opened_in_background"] = true,
                    ["detached"] = true,
                    ["file_saved_in_version"] = plan.FileVersion,
                    ["running_revit_version"] = plan.HostVersion,
                    ["was_central"] = plan.FileIsCentral,
                    ["will_be_closed_without_saving"] = true
                };
            }

            try
            {
                return CopyInto(app, request, destination, source, scale, gate, sourceOpenInfo);
            }
            finally
            {
                // CLOSED WITHOUT SAVING on every exit path - success, refusal or
                // exception alike. A background source is never this command's to keep
                // open, and never this command's to save.
                if (sourceOpenedHere)
                {
                    try { source.Close(false); } catch { }
                }
            }
        }

        private CommandResult CopyInto(UIApplication app, JObject request, Document destination, Document source,
            double scale, GateResult gate, JObject sourceOpenInfo)
        {
            JArray rawIds = request["element_ids"] as JArray;
            JArray typeNames = request["type_names"] as JArray;
            bool hasIds = rawIds != null && rawIds.Count > 0;
            bool hasNames = typeNames != null && typeNames.Count > 0;
            if (hasIds == hasNames)
                return CommandResult.Fail(
                    "give exactly one of element_ids (1..2000 ids from the source document) or type_names " +
                    "(1..500 type names to resolve BY NAME in the source document - the way to name what to copy " +
                    "from a file that was never open before this call, so no id could be known ahead of it), not both, not neither.");

            var ids = new List<ElementId>();
            var elements = new List<Element>();
            var problems = new JArray();

            if (hasIds)
            {
                if (rawIds.Count > 2000) return CommandResult.Fail("element_ids must hold 1..2000 element ids from the source document.");
                foreach (JToken token in rawIds)
                {
                    long raw = token.Value<long?>() ?? -1;
                    if (!Rid.CanRepresent(raw))
                        return CommandResult.Fail("element_ids holds a value that is not an element id.");
                    Element element = source.GetElement(Rid.Make(raw));
                    if (element == null)
                        return CommandResult.Fail(
                            "element " + raw + " does not exist in '" + source.Title + "'. Ids are per document: an " +
                            "id from the destination means nothing in the source, and copying whatever happens to " +
                            "carry that number would be a different element entirely.");
                    ids.Add(element.Id);
                    elements.Add(element);
                }
            }
            else
            {
                if (typeNames.Count > 500) return CommandResult.Fail("type_names must hold 1..500 names.");
                string category = request.Value<string>("category");
                BuiltInCategory? bic = null;
                if (!string.IsNullOrWhiteSpace(category))
                {
                    if (!Enum.TryParse(category.Trim(), true, out BuiltInCategory parsed))
                        return CommandResult.Fail("category must be a BuiltInCategory token such as OST_Walls.");
                    bic = parsed;
                }
                var seenNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (JToken token in typeNames)
                {
                    string name = token.Value<string>();
                    if (string.IsNullOrWhiteSpace(name)) return CommandResult.Fail("type_names holds a blank entry.");
                    if (!seenNames.Add(name)) return CommandResult.Fail("type_names repeats '" + name + "'.");
                    var collector = new FilteredElementCollector(source).WhereElementIsElementType();
                    if (bic.HasValue) collector = collector.OfCategory(bic.Value);
                    List<Element> matches = collector.Where(e => string.Equals(SafeName(e), name, StringComparison.Ordinal)).ToList();
                    if (matches.Count == 0)
                        return CommandResult.Fail("no type named '" + name + "' in '" + source.Title + "'" +
                            (bic.HasValue ? " under category " + category : "") + ". type_names matches EXACTLY, so a " +
                            "typo or a wrong category produces this refusal rather than a guess.");
                    if (matches.Count > 1)
                        return CommandResult.Fail("'" + name + "' names " + matches.Count + " types in '" + source.Title +
                            "' (ids " + string.Join(", ", matches.Select(m => Rid.Value(m.Id))) + "); narrow with category.");
                    ids.Add(matches[0].Id);
                    elements.Add(matches[0]);
                }
            }

            foreach (Element element in elements)
            {
                string problem = NotCopyable(source, destination, element);
                if (problem != null)
                    problems.Add(new JObject
                    {
                        ["element_id"] = Rid.Value(element.Id),
                        ["category"] = SafeCategory(element),
                        ["problem"] = problem
                    });
            }

            if (problems.Count > 0)
                return CommandResult.Fail(
                    "The copy was NOT started: " + problems.Count + " of " + ids.Count + " element(s) would " +
                    "produce something that looks copied and is not. " + problems.ToString(Formatting.None));

            XYZ offset = request["offset"] == null ? XYZ.Zero : Point(request["offset"]) * scale;
            Transform transform = Transform.CreateTranslation(offset);

            // THE ENUM HAS TWO MEMBERS AND NEITHER ONE RENAMES. DuplicateTypeAction is
            // exactly { Abort, UseDestinationTypes } - checked against the installed
            // RevitAPI.dll, not remembered - so "copy it in under a new name" is not
            // something this API offers, and offering it as an option would be inventing
            // a behaviour the caller would then rely on.
            //
            // The real choice is therefore: take the destination's type where a name
            // collides, or refuse the copy. Refusing is the default, because taking the
            // destination's type silently means geometry arrives wearing a type whose
            // layers nobody compared.
            var options = new CopyPasteOptions();
            string duplicates = (request.Value<string>("duplicate_types") ?? "abort_on_collision").ToLowerInvariant();
            if (duplicates != "use_destination" && duplicates != "abort_on_collision")
                return CommandResult.Fail(
                    "duplicate_types must be use_destination or abort_on_collision. Revit's " +
                    "DuplicateTypeAction has exactly two members and neither renames a type, so there is no " +
                    "third option to offer.");
            var typeCollisions = new DuplicateTypeRecorder(duplicates);
            options.SetDuplicateTypeNamesHandler(typeCollisions);

            // What the destination had BEFORE, so "which types arrived" is a measured
            // difference rather than a guess from the handler's callbacks.
            HashSet<long> typesBefore = TypeIds(destination);

            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            string hash = DocumentGate.PlanHash(request, "units", "source_document", "source_path", "element_ids",
                                                "type_names", "category", "offset", "duplicate_types");
            ResolvedPlan resolved = Resolved(gate, app, source, elements, duplicates);

            if (dry)
            {
                DocumentGate.RecordResolvedPlan(resolved);
                var preview = new JObject
                {
                    ["dry_run"] = true,
                    ["source_document"] = source.Title,
                    ["destination_document"] = destination.Title,
                    ["elements"] = ids.Count,
                    ["by_category"] = ByCategory(elements),
                    ["duplicate_types"] = duplicates,
                    ["offset_internal_feet"] = new JArray(offset.X, offset.Y, offset.Z),
                    ["means"] =
                        "Revit brings the TYPES with the elements. Where a type NAME already exists in the " +
                        "destination, duplicate_types=use_destination keeps the destination's type - whose " +
                        "layers may differ from the source's, and nothing compares them - while " +
                        "abort_on_collision refuses the whole copy instead. Revit's own enum offers no third " +
                        "option: it cannot rename a type on the way in. The applied reply names every type that " +
                        "arrived and every collision that occurred."
                };
                if (sourceOpenInfo != null) preview["source_open"] = sourceOpenInfo;
                ApplicationOutcome.StampRehearsal(preview, ids.Count, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, Name, hash, true,
                    "the token binds the source document, every source element's unique id and type name, and " +
                    "the duplicate-name policy; a source edited between the rehearsal and the apply refuses. A " +
                    "background source is reopened fresh for the apply, so the binding is by UniqueId, not by " +
                    "the ElementId this rehearsal happened to see.");
                return CommandResult.Ok(preview);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;

            ICollection<ElementId> created;
            string txName = request.Value<string>("transaction_name") ?? "Horizun: copy between documents";
            using (var tx = new Transaction(destination, txName))
            {
                if (tx.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("Could not start the copy transaction.");
                try
                {
                    created = ElementTransformUtils.CopyElements(source, ids, destination, transform, options);
                    destination.Regenerate();
                    if (created == null || created.Count == 0)
                        throw new InvalidOperationException(
                            "CopyElements returned nothing. Revit accepted the call and produced no element; " +
                            "nothing is claimed.");
                    TransactionStatus status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        throw new InvalidOperationException("The copy transaction returned " + status + ".");
                }
                catch (Exception ex)
                {
                    Guard.RollbackResult? rollback = null;
                    try { if (tx.GetStatus() == TransactionStatus.Started) rollback = Guard.RollBack(tx); } catch { }
                    return CommandResult.FailWithDetail(
                        "The copy failed and was rolled back: " + ex.Message,
                        new JObject
                        {
                            ["state"] = rollback.HasValue && rollback.Value.Confirmed ? "rolled_back" : "uncertain",
                            ["transaction_status"] = rollback.HasValue ? rollback.Value.StatusName : "Error"
                        });
                }
            }

            // RE-READ. Every created id is asked for from the destination, because a
            // count from the call is a count from the call.
            var rows = new JArray();
            int present = 0;
            foreach (ElementId id in created)
            {
                Element element = destination.GetElement(id);
                if (element != null) present++;
                rows.Add(new JObject
                {
                    ["element_id"] = Rid.Value(id),
                    ["present_after_commit"] = element != null,
                    ["category"] = element == null ? null : SafeCategory(element),
                    ["name"] = element == null ? null : SafeName(element)
                });
            }

            if (present != created.Count)
                return CommandResult.FailWithDetail(
                    "The copy committed and only " + present + " of " + created.Count + " new elements can be " +
                    "read back. Success is not claimed.",
                    new JObject { ["state"] = "uncertain", ["host_verified"] = false, ["rows"] = rows });

            var arrived = new JArray();
            foreach (long id in TypeIds(destination).Except(typesBefore).OrderBy(v => v))
            {
                Element type = destination.GetElement(Rid.Make(id));
                arrived.Add(new JObject
                {
                    ["type_id"] = id,
                    ["name"] = type == null ? null : SafeName(type),
                    ["category"] = type == null ? null : SafeCategory(type)
                });
            }

            // THE VERDICT IS DERIVED, NOT WRITTEN. state/host_verified used to be the literals
            // "committed_verified"/true while the application block below was computed from
            // ids against created - so a copy that produced fewer elements than it was asked
            // for said verified in one field and partial in the other. Revit copies dependents
            // along (a hosted door with its wall), so MORE created than requested is normal:
            // what was requested is the larger of the two, and every created element must
            // re-read present.
            int requestedCount = Math.Max(ids.Count, created.Count);
            ApplicationState copyState = ApplicationOutcome.Applied(ApplicationOutcome.Committed,
                                                                    requestedCount, present, present, 0, 0, 0);
            bool copyVerified = copyState == ApplicationState.VerifiedApplied;
            var done = new JObject
            {
                ["state"] = copyVerified ? "committed_verified" : ApplicationOutcome.Name(copyState),
                ["host_verified"] = copyVerified,
                ["source_document"] = source.Title,
                ["destination_document"] = destination.Title,
                ["requested"] = ids.Count,
                ["created"] = created.Count,
                ["rows"] = rows,
                ["types_that_arrived"] = arrived,
                ["duplicate_types"] = duplicates,
                ["type_name_collisions"] = new JArray(typeCollisions.Collisions),
                ["source_open"] = sourceOpenInfo,
                ["means"] =
                    "types_that_arrived is the DIFFERENCE between the destination's types before and after, " +
                    "not a report from the copy. A copy that silently duplicated a type catalogue is how a " +
                    "project acquires a second 'Basic Wall - 200' and nobody knows when."
            };
            if (created.Count < ids.Count)
                done["created_fewer_than_requested"] = "Revit produced " + created.Count + " element(s) for " +
                    ids.Count + " requested; the difference was not copied.";
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed,
                                            requestedCount, present, present, 0, 0, 0);
            return CommandResult.Ok(done);
        }

        // =====================================================================

        /// <summary>
        /// The other open document, by title. Never opened here: opening a document is a
        /// decision with its own consequences and its own tool.
        /// </summary>
        private static Document FindOpen(UIApplication app, string title, Document destination, out string error)
        {
            error = null;
            var matches = new List<Document>();
            foreach (Document candidate in app.Application.Documents)
            {
                if (candidate == null || candidate.IsLinked) continue;
                if (string.Equals(candidate.Title, destination.Title, StringComparison.Ordinal)) continue;
                if (string.Equals(candidate.Title, title, StringComparison.OrdinalIgnoreCase)) matches.Add(candidate);
            }

            if (matches.Count == 1) return matches[0];

            var open = new List<string>();
            foreach (Document candidate in app.Application.Documents)
                if (candidate != null && !candidate.IsLinked) open.Add(candidate.Title);

            error = matches.Count == 0
                ? "no OPEN document titled '" + title + "'. Open documents: " + string.Join(", ", open) +
                  ". This command never opens one."
                : matches.Count + " open documents share the title '" + title + "'. Nothing was copied: a " +
                  "source chosen between two documents with the same name would be a guess about which " +
                  "project the geometry came from.";
            return null;
        }

        /// <summary>
        /// Why copying this element would produce something that looks copied and is not.
        /// Measured before the transaction; every case here is one Revit accepts.
        /// </summary>
        private static string NotCopyable(Document source, Document destination, Element element)
        {
            Category category;
            try { category = element.Category; } catch { category = null; }
            if (category == null)
                return "has no category. Revit copies it and the result is unusable.";

            if (element.ViewSpecific)
                return "is VIEW-SPECIFIC (it belongs to view " + Rid.Value(element.OwnerViewId) + "). Copying " +
                       "it between documents without its view produces an annotation with nothing to annotate. " +
                       "Copy it view-to-view, or copy the model element it describes.";

            if (element is FamilyInstance instance)
            {
                Element host = null;
                try { host = instance.Host; } catch { }
                if (host != null)
                    return "is HOSTED on element " + Rid.Value(host.Id) + " in the source. Its host is not " +
                           "coming with it, so Revit will place it on whatever it finds - or on nothing. " +
                           "Copy the host in the same call, or place it in the destination instead.";
            }

            if (element.GroupId != null && element.GroupId != ElementId.InvalidElementId)
                return "is a member of group " + Rid.Value(element.GroupId) + ". Copying one member out of a " +
                       "group copies geometry and loses the group; copy the group itself if that is what you " +
                       "meant.";

            if (element.Pinned)
                return "is PINNED in the source. That is usually somebody saying it must not move, and a copy " +
                       "of it lands unpinned somewhere else. Unpin it deliberately if the copy is intended.";

            return null;
        }

        /// <summary>
        /// Revit asks what to do about a type name that already exists. The answer is
        /// the caller's, declared up front, and it is recorded so the reply can say a
        /// collision happened at all.
        /// </summary>
        private sealed class DuplicateTypeRecorder : IDuplicateTypeNamesHandler
        {
            private readonly string _policy;

            /// <summary>Every type name Revit reported as already present in the destination.</summary>
            public readonly List<string> Collisions = new List<string>();

            public DuplicateTypeRecorder(string policy) { _policy = policy; }

            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            {
                // GetTypeIds, not GetDuplicateTypeNames: the argument object carries the
                // colliding TYPE IDS and the source Document, and there is no member that
                // hands back names. Read out of the installed RevitAPI.xml after the first
                // version of this file invented a friendlier-sounding method.
                //
                // The ids are resolved to names through args.Document, because "wall type
                // 318451 already exists" is not something a person can act on.
                try
                {
                    Document from = args.Document;
                    foreach (ElementId id in args.GetTypeIds() ?? new List<ElementId>())
                    {
                        string name = null;
                        try { name = from?.GetElement(id)?.Name; } catch { name = null; }
                        Collisions.Add(string.IsNullOrWhiteSpace(name)
                            ? "type id " + Rid.Value(id)
                            : name + " (id " + Rid.Value(id) + ")");
                    }
                }
                catch
                {
                    // The ids could not be read. The COLLISION still happened, and
                    // recording that it did - without which one - beats recording nothing.
                    Collisions.Add("(a duplicate type Revit would not name)");
                }
                return _policy == "use_destination"
                    ? DuplicateTypeAction.UseDestinationTypes
                    : DuplicateTypeAction.Abort;
            }
        }

        private static HashSet<long> TypeIds(Document doc)
        {
            var ids = new HashSet<long>();
            try
            {
                foreach (Element type in new FilteredElementCollector(doc).WhereElementIsElementType())
                    ids.Add(Rid.Value(type.Id));
            }
            catch { }
            return ids;
        }

        private static JObject ByCategory(List<Element> elements)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Element element in elements)
            {
                string name = SafeCategory(element) ?? "(no category)";
                counts[name] = counts.TryGetValue(name, out int existing) ? existing + 1 : 1;
            }
            var row = new JObject();
            foreach (KeyValuePair<string, int> pair in counts.OrderBy(p => p.Key, StringComparer.Ordinal))
                row[pair.Key] = pair.Value;
            return row;
        }

        private static ResolvedPlan Resolved(GateResult gate, UIApplication app, Document source,
                                             List<Element> elements, string duplicates)
        {
            var resolved = new ResolvedPlan
            {
                Command = "horizun_copy_between_documents",
                DocumentKey = gate.Fingerprint,
                RevitVersion = app.Application.VersionNumber,
                DocumentFingerprint = gate.Identity.FingerprintDigest()
            };
            foreach (Element element in elements)
                resolved.Elements.Add(new PlannedElement
                {
                    UniqueId = "source:" + SafeUniqueId(element),
                    Category = SafeCategory(element),
                    TypeName = TypeNameOf(source, element),
                    Action = PlannedAction.Read,
                    BeforeValues = new Dictionary<string, string>
                    {
                        ["source_document"] = source.Title,
                        ["duplicate_types"] = duplicates
                    }
                });
            return resolved;
        }

        private static string TypeNameOf(Document doc, Element element)
        {
            try
            {
                Element type = doc.GetElement(element.GetTypeId());
                return type == null ? "<none>" : SafeName(type);
            }
            catch { return "<unreadable>"; }
        }

        private static string SafeName(Element element)
        { try { return element.Name; } catch { return null; } }

        private static string SafeCategory(Element element)
        { try { return element.Category?.Name; } catch { return null; } }

        private static string SafeUniqueId(Element element)
        { try { return element.UniqueId; } catch { return "<unreadable>"; } }

        private static bool Scale(string units, out double scale)
        {
            if (units == "feet") { scale = 1; return true; }
            if (units == "m") { scale = 1 / 0.3048; return true; }
            if (units == "mm") { scale = 1 / 304.8; return true; }
            scale = 0; return false;
        }

        private static XYZ Point(JToken token)
        {
            var array = token as JArray;
            if (array == null || array.Count < 2 || array.Count > 3)
                throw new ArgumentException("offset must be an array of 2 or 3 numbers");
            return new XYZ(array[0].Value<double>(), array[1].Value<double>(),
                           array.Count > 2 ? array[2].Value<double>() : 0);
        }
    }
}
