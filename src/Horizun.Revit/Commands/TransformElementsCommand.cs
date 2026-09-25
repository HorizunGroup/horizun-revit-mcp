// -----------------------------------------------------------------------------
// Horizun Revit MCP - generic transforms with post-commit location verification.
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
    public sealed class TransformElementsCommand : ICommand
    {
        public string Name => "horizun_transform_elements";
        public string Description => "Move, copy, rotate, pin or change type atomically and verify from the committed model.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            JArray input = request["operations"] as JArray;
            if (input == null || input.Count == 0) return CommandResult.Fail("operations is required and must be non-empty.");
            if (input.Count > 500) return CommandResult.Fail("operations exceeds 500 entries.");
            // rename_level rehearses and reports Copy/Monitor alerts for the WHOLE
            // transaction (RevitErrorRecorder has no per-operation scope), so mixing it
            // with anything else would blur which operation raised what.
            if (input.Count != 1 && input.OfType<JObject>().Any(x => (x.Value<string>("operation") ?? "").ToLowerInvariant() == "rename_level"))
                return CommandResult.Fail("rename_level must be the sole operation in its batch: the Copy/Monitor " +
                    "rehearsal and the view census read the whole transaction, and mixing operations would blur " +
                    "which one raised them.");
            double scale;
            if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale))
                return CommandResult.Fail("units must be mm, m or feet.");

            var plans = new List<Plan>();
            var errors = new JArray();
            var claimed = new HashSet<long>();
            // Every action's outcome, so the fallback is decided once over the whole
            // batch rather than granted because one entry was uncovered.
            var outcomes = new List<ActionOutcome>();
            for (int i = 0; i < input.Count; i++)
            {
                string error = null, reason = null;
                Plan plan = PlanOperation(doc, i, input[i] as JObject, scale, claimed, out error, out reason);
                if (plan == null)
                {
                    string message = error ?? "entry is not an object";
                    errors.Add(new JObject { ["index"] = i, ["error"] = message });
                    outcomes.Add(new ActionOutcome { Index = i, Error = message, UnsupportedReason = reason });
                }
                else plans.Add(plan);
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "operations");

            // ---- The MATERIALISED plan: the ELEMENTS this resolves to, right now. ----
            // planHash above binds the REQUEST - the operations as written. It cannot see
            // that the model moved: an operation naming a filter, or a type whose instances
            // changed, resolves to a different set of elements without a single character of
            // the request changing. Built identically here on both paths, so the dry run's
            // fingerprint rides in the token and the apply recomputes it.
            //
            // CONTRIBUTING points new commands at this file as the shape to copy, which is
            // exactly why the plan belongs here: whatever this command does, the next ten
            // will do.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (Plan p in plans)
            {
                foreach (ElementId id in (p.Ids ?? new List<ElementId>()))
                {
                    Element e = doc.GetElement(id);
                    if (e == null) continue;
                    var planned = new PlannedElement
                    {
                        UniqueId = SafePlanUniqueId(e),
                        ElementId = Rid.Value(e.Id),
                        Category = e.Category == null ? null : e.Category.Name,
                        TypeName = SafePlanTypeName(doc, e),
                        // change_type REPLACES the type; move/rotate/copy do not. Both are
                        // modifications of this element, and 'copy' additionally creates -
                        // but the created ids do not exist yet at plan time, so what is
                        // fingerprinted is the SOURCE set and the operation, which is what
                        // the caller actually approved.
                        Action = PlannedAction.Modify,
                        // The geometry the operation is ABOUT. A move approved against an
                        // element somebody has since moved is a different move, and only a
                        // position read can show that.
                        GeometryFingerprint = SafePlanGeometry(e),
                        BeforeValues = new Dictionary<string, string>
                        {
                            { "operation", p.Operation ?? "" },
                            { "type_id", Rid.Value(e.GetTypeId()).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                            { "pinned", e.Pinned.ToString() },
                            { "wall_join_allowed", p.Operation == "wall_join" ? WallUtils.IsWallJoinAllowedAtEnd((Wall)e, p.JoinEnd).ToString() : "" },
                        }
                    };
                    planned.ProposedValues = new Dictionary<string, string> {
                        { "operation", p.Operation },
                        { "specification", input[p.Index].ToString(Formatting.None) }
                    };
                    if (p.Operation == "pin" || p.Operation == "unpin") planned.ProposedValues["pinned"] = (p.Operation == "pin").ToString();
                    if (p.TypeId != null) planned.ProposedValues["type_id"] = Rid.Value(p.TypeId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    resolvedPlan.Elements.Add(planned);
                }
            }

            // rename_level: the dry run must show BOTH which plan views will rename
            // (a deterministic read - Revit only renames a plan view whose name exactly
            // matches the level's, before the rename) and which Copy/Monitor alerts the
            // rename would raise (NOT deterministic: Revit's coordination engine is the
            // only source of truth for those). The second half needs an actual rename,
            // so this performs one inside its OWN transaction and rolls it back -
            // "no transaction was opened" below is still true of the real dry-run path;
            // this is a separate, self-contained rehearsal.
            if (dryRun && errors.Count == 0)
            {
                Plan renamePlan = plans.FirstOrDefault(p => p.Operation == "rename_level");
                if (renamePlan != null)
                {
                    JObject rehearsal = RehearseRenameLevel(doc, renamePlan);
                    if (rehearsal.Value<bool>("rollback_confirmed") != true)
                        return CommandResult.FailWithDetail(
                            "Level rename rehearsal rollback was not confirmed; model state is uncertain.",
                            new JObject { ["state"] = "uncertain", ["rehearsal"] = rehearsal, ["write_started"] = true });
                    renamePlan.Summary["level_rename_rehearsal"] = rehearsal;
                }
            }

            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["operations"] = input.Count,
                    ["targets"] = claimed.Count, ["valid_operations"] = plans.Count, ["invalid_operations"] = errors.Count,
                    ["errors"] = errors, ["plan"] = new JArray(plans.Select(p => p.Summary)),
                    ["note"] = "Nothing was transformed; no transaction was opened."
                };
                if (errors.Count == 0) DocumentGate.RecordResolvedPlan(resolvedPlan);
                // Invalid entries make this a partial rehearsal, not a clean one: the token
                // below is already withheld for them, and a plan must read the same fact.
                ApplicationOutcome.StampRehearsal(result, input.Count, errors.Count, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0 ? "the token binds the ordered operations, targets, vectors, axes and types" :
                    "no usable confirmation is issued while any operation is invalid");
                // THE REHEARSAL CARRIES THE VERDICT TOO. dry_run defaults to true, so this
                // is the first call a caller makes; without the block here they got
                // success=true with invalid rows and no way to tell a capability gap
                // from a typo except by sending an apply they had no reason to send.
                return FallbackDecision.Attach(
                    CommandResult.Ok(result),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (errors.Count > 0)
                return FallbackDecision.Refuse(
                    "Invalid operations; nothing ran: " + errors.ToString(Formatting.None),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            // The rehearsed fingerprint travels in the token; the rehearsed PLAN does not,
            // so a stale refusal names the drift generically rather than per element.
            // Still refused, still nothing written.
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                    resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            // horizun_undo: the state each target is in BEFORE the commit.
            var undoBefore = plans.ToDictionary(p => p, p => UndoCapture.States(doc, p.Ids.Select(Rid.Value)));
            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: transform elements";
            // rename_level is the sole operation whenever it appears (refused above
            // otherwise), so capturing warnings for the whole transaction is exactly
            // capturing them for that one operation.
            bool captureLevelWarnings = plans.Any(p => p.Operation == "rename_level");
            RevitErrorRecorder revitSaid = null;
            using (var tx = new Transaction(doc, txName))
            {
                revitSaid = RevitErrorRecorder.On(tx, captureWarnings: captureLevelWarnings);
                tx.Start();
                try
                {
                    foreach (Plan p in plans) Apply(doc, p);
                    // A HOSTED ELEMENT STAYS ON ITS HOST, OR NOTHING IS WRITTEN.
                    // MEASURED on a face-hosted receptacle: moved 300 mm along its wall
                    // it left the face, Revit kept it with NO host, and the point
                    // check verified the move; moved 200 mm across the wall it kept
                    // its host and stood 38 mm out of the far face. A device that must
                    // change host or face is re-placed, not moved, so the whole
                    // transform is rolled back.
                    if (plans.Any(p => p.HostBefore.Count > 0))
                    {
                        doc.Regenerate();
                        foreach (Plan p in plans) GuardHosts(doc, p);
                    }
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    bool attempted = false; string rb = PlanFailure.NotAttempted;
                    if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                    return CommandResult.Fail("Atomic transform failed: " + ex.Message + revitSaid.Said() + " " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing was transformed"));
                }
            }

            var verification = new JArray();
            int verified = 0;
            foreach (Plan p in plans)
            {
                bool ok = Verify(doc, p, out JObject detail);
                detail["index"] = p.Index; detail["operation"] = p.Operation; detail["verified"] = ok;
                if (p.Operation == "rename_level")
                    detail["copy_monitor_alerts"] = new JArray(revitSaid?.Warnings ?? new List<string>());
                verification.Add(detail); if (ok) verified++;
            }
            if (plans.Count == 0 || verified != plans.Count)
                return CommandResult.Fail("The transaction committed, but " + (plans.Count - verified) +
                    " operation(s) failed post-commit verification. Inspect the model. " + verification.ToString(Formatting.None));
            var trResult = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["operations_verified"] = verified, ["targets"] = claimed.Count, ["rows"] = verification
            };
            // Reached only when every planned operation passed its post-commit check.
            ApplicationOutcome.StampApplied(trResult, ApplicationOutcome.Committed,
                                            plans.Count, verified, verified, 0, 0, 0);
            trResult["undo"] = RecordUndo(doc, plans, undoBefore);
            return CommandResult.Ok(trResult);
        }

        /// <summary>The batch's inverse for horizun_undo. Ops without a recorded inverse make the batch not undoable, by name.</summary>
        private JObject RecordUndo(Document doc, List<Plan> plans, Dictionary<Plan, JObject> before)
        {
            var entries = new List<UndoEntry>(); string blocked = null;
            foreach (Plan p in plans)
            {
                IEnumerable<long> ids = p.Ids.Select(Rid.Value);
                switch (p.Operation)
                {
                    case "move": entries.Add(UndoCapture.Entry(doc, "move", ids, before[p], new JObject { ["vector"] = new JArray(p.Vector.X, p.Vector.Y, p.Vector.Z) })); break;
                    case "copy": entries.Add(UndoCapture.Entry(doc, "created", (p.Created ?? new List<ElementId>()).Select(Rid.Value), new JObject(), new JObject())); break;
                    case "rotate":
                        entries.Add(UndoCapture.Entry(doc, "rotate", ids, before[p], new JObject
                        {
                            ["axis_start"] = new JArray(p.Axis.GetEndPoint(0).X, p.Axis.GetEndPoint(0).Y, p.Axis.GetEndPoint(0).Z),
                            ["axis_end"] = new JArray(p.Axis.GetEndPoint(1).X, p.Axis.GetEndPoint(1).Y, p.Axis.GetEndPoint(1).Z),
                            ["angle"] = p.Angle
                        })); break;
                    case "mirror":
                        entries.Add(UndoCapture.Entry(doc, "mirror", ids, before[p], new JObject
                        {
                            ["plane_origin"] = new JArray(p.MirrorPlane.Origin.X, p.MirrorPlane.Origin.Y, p.MirrorPlane.Origin.Z),
                            ["plane_normal"] = new JArray(p.MirrorPlane.Normal.X, p.MirrorPlane.Normal.Y, p.MirrorPlane.Normal.Z)
                        })); break;
                    case "pin": case "unpin": entries.Add(UndoCapture.Entry(doc, "pin", ids, before[p], null)); break;
                    case "change_type": entries.Add(UndoCapture.Entry(doc, "type", ids, before[p], null)); break;
                    case "set_curve":
                        if (before[p].Properties().Any(x => x.Value["line"]?.Value<bool>() != true)) blocked = "set_curve replaced a non-line curve";
                        else entries.Add(UndoCapture.Entry(doc, "curve", ids, before[p], null));
                        break;
                    case "move_tag_head": entries.Add(UndoCapture.Entry(doc, "tag_head", ids, before[p], null)); break;
                    default: blocked = p.Operation + " has no recorded inverse"; break;
                }
            }
            return UndoCapture.Record(doc, Name, entries, blocked);
        }

        private static Plan PlanOperation(Document doc, int index, JObject o, double scale, HashSet<long> claimed,
                                          out string error, out string unsupportedReason)
        {
            error = null; unsupportedReason = null;
            if (o == null) { error = "entry is not an object"; return null; }
            try
            {
                string op = (o.Value<string>("operation") ?? "").ToLowerInvariant();
                if (op != "move" && op != "copy" && op != "rotate" && op != "mirror" && op != "pin" && op != "unpin" &&
                    op != "change_type" && op != "set_curve" && op != "move_tag_head" && op != "set_tag_leader" && op != "wall_join" &&
                    op != "array_linear" && op != "array_radial" && op != "rename_level")
                    throw new UnsupportedCapability(
                        "unsupported operation '" + op + "' - horizun_transform_elements does move, copy, " +
                        "rotate, mirror, pin, unpin, change_type, set_curve, move_tag_head, set_tag_leader, array_linear, array_radial and rename_level only. Nothing was written.",
                        FallbackSignal.ReasonUnsupportedOperation);
                var allowed = new HashSet<string>(new[] { "operation", "element_ids" });
                if(op=="rename_level") allowed.Add("name");
                if(op=="move" || op=="copy") allowed.Add("vector");
                if(op=="rotate") allowed.UnionWith(new[] { "axis_start", "axis_end", "angle_degrees" });
                if(op=="mirror") allowed.UnionWith(new[] { "plane_origin", "plane_normal" });
                if(op=="change_type") allowed.Add("type_id");
                if(op=="wall_join") allowed.UnionWith(new[] { "join_end", "allow" });
                if(op=="set_curve") allowed.UnionWith(new[] { "start", "end" });
                if(op=="array_linear") allowed.UnionWith(new[] { "vector", "count", "anchor", "group" });
                if(op=="array_radial") allowed.UnionWith(new[] { "axis_start", "axis_end", "angle_degrees", "count", "anchor", "group" });
                if(op=="move_tag_head") allowed.UnionWith(new[] { "point", "vector" });
                if(op=="set_tag_leader") allowed.UnionWith(new[] { "has_leader", "leader_end_condition", "leader_end", "leader_elbow", "leader_visible" });
                foreach(var field in o.Properties()) if(!allowed.Contains(field.Name)) throw new ArgumentException(field.Name+" is not applicable to "+op);
                JArray ids = o["element_ids"] as JArray;
                if (ids == null || ids.Count == 0 || ids.Count > 2000) throw new ArgumentException("element_ids must contain 1..2000 ids");
                var p = new Plan { Index = index, Operation = op, Ids = new List<ElementId>(), Samples = new Dictionary<long, List<XYZ>>() };
                foreach (JToken token in ids)
                {
                    long raw = token.Value<long>();
                    if (!Rid.CanRepresent(raw)) throw new ArgumentException("ElementId " + raw + " is outside the supported range");
                    Element element = doc.GetElement(Rid.Make(raw));
                    if (element == null) throw new ArgumentException("ElementId " + raw + " does not exist in the host document");
                    if (!claimed.Add(raw)) throw new ArgumentException("ElementId " + raw + " appears in more than one operation; sequential transforms are ambiguous, combine them deliberately first");
                    p.Ids.Add(element.Id);
                    if (op == "move_tag_head" || op == "set_tag_leader")
                    {
                        // The 8B tag block. IndependentTag only: room/space tags carry a head
                        // but a different leader API, and a rule that half-applies is worse
                        // than a named limit.
                        var tag = element as IndependentTag;
                        if (tag == null)
                            throw new ArgumentException("ElementId " + raw + " is a " + element.GetType().Name + ", not an " +
                                "IndependentTag; " + op + " edits independent tags only (room, space and area tags are not " +
                                "covered by this operation - a documented limit, not a guess).");
                        bool pinnedTag;
                        try { pinnedTag = tag.Pinned; } catch { pinnedTag = false; }
                        if (pinnedTag)
                            throw new ArgumentException("ElementId " + raw + " is PINNED; unpin it deliberately first.");
                        XYZ head;
                        try { head = tag.TagHeadPosition; }
                        catch (Exception ex) { throw new ArgumentException("the head position of tag " + raw + " could not be read (" + ex.Message + ")"); }
                        p.HeadBefore[raw] = head;
                        if (op == "set_tag_leader")
                        {
                            IList<Reference> refs;
                            try { refs = tag.GetTaggedReferences(); }
                            catch (Exception ex) { throw new ArgumentException("the tagged references of tag " + raw + " could not be read (" + ex.Message + ")"); }
                            if (refs == null || refs.Count != 1)
                                throw new ArgumentException("tag " + raw + " tags " + (refs == null ? 0 : refs.Count) + " references; " +
                                    "set_tag_leader addresses the leader of exactly one tagged reference, so a multi-reference tag is refused rather than guessed.");
                            p.TagRefs[raw] = refs[0];
                        }
                    }
                    if (op == "move" || op == "rotate" || op == "copy" || op == "mirror" || op == "array_linear" || op == "array_radial")
                    {
                        List<XYZ> sample = Samples(element);
                        if (sample.Count == 0) throw new ArgumentException("ElementId " + raw + " has no LocationPoint/LocationCurve sample, so " + op + " cannot be verified");
                        p.Samples[raw] = sample;
                        // A TURN ABOUT AN AXIS THROUGH THE ELEMENT'S OWN POINT MOVES NO
                        // POINT. Measured on a face-hosted device: the point samples
                        // alone would verify a rotation that turned nothing, so an
                        // instance's axes are sampled too and must turn as asked.
                        if (op == "rotate" || op == "mirror") p.Axes[raw] = Axes(element);
                        long? host = HostOf(element);
                        if (host.HasValue) p.HostBefore[raw] = host.Value;
                    }
                    if (op == "set_curve")
                    {
                        // A curve belongs to ONE element. Giving the same line to
                        // several would stack them on top of each other, which is
                        // never what anybody meant and is hard to see afterwards.
                        if (ids.Count != 1)
                            throw new ArgumentException(
                                "set_curve takes exactly one element_id: the line is that element's own. " +
                                "Sending " + ids.Count + " ids with one line would stack them on top of each " +
                                "other. Use one operation per element.");
                        var lc = element.Location as LocationCurve;
                        if (lc == null || lc.Curve == null)
                            throw new ArgumentException(
                                "ElementId " + raw + " is a " + element.GetType().Name + " with no LocationCurve, " +
                                "so it has no line to set. Walls, beams, pipes and ducts have one; a family " +
                                "instance placed at a point does not.");
                        bool pinned;
                        try { pinned = element.Pinned; } catch { pinned = false; }
                        if (pinned)
                            throw new ArgumentException(
                                "ElementId " + raw + " is PINNED. Revit silently refuses to move a pinned " +
                                "element, so this would report a failed verification rather than a refusal. " +
                                "Unpin it deliberately first.");
                        p.Samples[raw] = Samples(element);
                    }
                }
                if (op == "rename_level")
                {
                    if (p.Ids.Count != 1)
                        throw new ArgumentException("rename_level takes exactly one element_id: the level being renamed.");
                    Element el = doc.GetElement(p.Ids[0]);
                    var level = el as Level;
                    if (level == null)
                        throw new ArgumentException("ElementId " + Rid.Value(p.Ids[0]) + " is a " + el.GetType().Name +
                            ", not a Level; rename_level only renames levels.");
                    string newName = (o.Value<string>("name") ?? "").Trim();
                    if (string.IsNullOrEmpty(newName))
                        throw new ArgumentException("rename_level requires a non-empty name.");
                    if (string.Equals(newName, level.Name, StringComparison.Ordinal))
                        throw new ArgumentException("level " + Rid.Value(level.Id) + " is already named '" + newName + "'; a no-op is refused.");
                    bool collision = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .Any(l => l.Id != level.Id && string.Equals(l.Name, newName, StringComparison.OrdinalIgnoreCase));
                    if (collision)
                        throw new ArgumentException("the name '" + newName + "' is already used by another level (Revit level names must be unique).");
                    p.OldLevelName = level.Name;
                    p.NewLevelName = newName;
                    // WHICH plan views Revit will rename SOLA - a deterministic read, not a
                    // guess: Revit only renames a plan view whose name exactly matches the
                    // level's, and only non-template ones (a template has no GenLevel to
                    // match with in the first place).
                    foreach (ViewPlan vp in new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>())
                    {
                        if (vp.IsTemplate) continue;
                        Level gl = null;
                        try { gl = vp.GenLevel; } catch { }
                        if (gl != null && gl.Id == level.Id && string.Equals(vp.Name, level.Name, StringComparison.Ordinal))
                            p.LevelCandidateViewIds.Add(Rid.Value(vp.Id));
                    }
                }
                if(op=="wall_join")
                {
                    if(o["join_end"]?.Type!=JTokenType.Integer || (o.Value<int>("join_end")!=0 && o.Value<int>("join_end")!=1) || o["allow"]?.Type!=JTokenType.Boolean)
                        throw new ArgumentException("wall_join requires join_end=0 or 1 and boolean allow.");
                    if(p.Ids.Any(id=>!(doc.GetElement(id) is Wall))) throw new ArgumentException("wall_join targets must all be walls.");
                    p.JoinEnd=o.Value<int>("join_end"); p.JoinAllowed=o.Value<bool>("allow");
                }
                if (op == "move" || op == "copy") p.Vector = Point(o["vector"], scale, "vector");
                if (op == "array_linear" || op == "array_radial") PlanArray(doc, o, scale, p);
                if (op == "rotate")
                {
                    XYZ a = Point(o["axis_start"], scale, "axis_start"), b = Point(o["axis_end"], scale, "axis_end");
                    if (a.DistanceTo(b) < 1e-9) throw new ArgumentException("rotation axis endpoints must differ");
                    p.Axis = Line.CreateBound(a, b);
                    if (o["angle_degrees"] == null) throw new ArgumentException("angle_degrees is required for rotate");
                    p.Angle = o.Value<double>("angle_degrees") * Math.PI / 180.0;
                    p.Rotation = Transform.CreateRotationAtPoint((b - a).Normalize(), p.Angle, a);
                }
                if (op == "mirror")
                {
                    // IN PLACE, not a mirrored copy: the element keeps its id, its
                    // parameters and whatever is hosted on it.
                    XYZ origin = Point(o["plane_origin"], scale, "plane_origin");
                    XYZ normal = Point(o["plane_normal"], 1.0, "plane_normal");
                    if (normal.GetLength() < 1e-9) throw new ArgumentException("plane_normal must not be zero");
                    normal = normal.Normalize();
                    p.MirrorPlane = Plane.CreateByNormalAndOrigin(normal, origin);
                    p.Rotation = Transform.CreateReflection(p.MirrorPlane);
                }
                if (op == "set_curve")
                {
                    XYZ a = Point(o["start"], scale, "start"), b = Point(o["end"], scale, "end");
                    if (a.DistanceTo(b) < 1e-9)
                        throw new ArgumentException("start and end must differ: a zero-length curve is not a line");
                    p.Curve = Line.CreateBound(a, b);
                }
                if (op == "change_type")
                {
                    long typeId = o.Value<long?>("type_id") ?? -1;
                    if (!Rid.CanRepresent(typeId) || !(doc.GetElement(Rid.Make(typeId)) is ElementType))
                        throw new ArgumentException("type_id must identify an ElementType");
                    p.TypeId = Rid.Make(typeId);
                    foreach (ElementId id in p.Ids)
                        if (!doc.GetElement(id).IsValidType(p.TypeId))
                            throw new ArgumentException("type_id " + typeId + " is not valid for ElementId " + Rid.Value(id));
                }
                if (op == "move_tag_head")
                {
                    bool hasPoint = o["point"] != null, hasVector = o["vector"] != null;
                    if (hasPoint == hasVector)
                        throw new ArgumentException("move_tag_head takes exactly one of point (an absolute head position, one tag only) or vector (a displacement applied to every tag listed).");
                    if (hasPoint)
                    {
                        if (ids.Count != 1) throw new ArgumentException("move_tag_head with point takes exactly one element_id: one absolute position belongs to one head.");
                        p.HeadPoint = Point(o["point"], scale, "point");
                    }
                    else p.Vector = Point(o["vector"], scale, "vector");
                }
                if (op == "set_tag_leader")
                {
                    int named = 0;
                    if (o["has_leader"] != null)
                    {
                        if (o["has_leader"].Type != JTokenType.Boolean) throw new ArgumentException("has_leader must be a boolean");
                        p.HasLeader = o.Value<bool>("has_leader"); named++;
                    }
                    if (o["leader_end_condition"] != null)
                    {
                        string c = (o.Value<string>("leader_end_condition") ?? "").ToLowerInvariant();
                        if (c != "attached" && c != "free") throw new ArgumentException("leader_end_condition must be attached or free");
                        p.EndCondition = c == "free" ? LeaderEndCondition.Free : LeaderEndCondition.Attached; named++;
                    }
                    if (o["leader_visible"] != null)
                    {
                        if (o["leader_visible"].Type != JTokenType.Boolean) throw new ArgumentException("leader_visible must be a boolean");
                        p.LeaderVisible = o.Value<bool>("leader_visible"); named++;
                    }
                    if (o["leader_end"] != null) { p.LeaderEnd = Point(o["leader_end"], scale, "leader_end"); named++; }
                    if (o["leader_elbow"] != null) { p.LeaderElbow = Point(o["leader_elbow"], scale, "leader_elbow"); named++; }
                    if (named == 0)
                        throw new ArgumentException("set_tag_leader names no edit: give at least one of has_leader, leader_end_condition, leader_end, leader_elbow, leader_visible.");
                    foreach (ElementId id in p.Ids)
                    {
                        var tag = (IndependentTag)doc.GetElement(id);
                        long raw = Rid.Value(id);
                        bool hasLeaderNow; try { hasLeaderNow = tag.HasLeader; } catch { hasLeaderNow = false; }
                        bool willHaveLeader = p.HasLeader ?? hasLeaderNow;
                        LeaderEndCondition condNow; try { condNow = tag.LeaderEndCondition; } catch { condNow = LeaderEndCondition.Attached; }
                        LeaderEndCondition willBe = p.EndCondition ?? condNow;
                        if (p.EndCondition.HasValue)
                        {
                            bool assignable; try { assignable = tag.CanLeaderEndConditionBeAssigned(p.EndCondition.Value); } catch { assignable = false; }
                            if (!assignable)
                                throw new ArgumentException("tag " + raw + " cannot take leader_end_condition=" + (p.EndCondition.Value == LeaderEndCondition.Free ? "free" : "attached") +
                                    " (CanLeaderEndConditionBeAssigned is false: its tagged element or category does not allow it). Nothing was written.");
                        }
                        if ((p.LeaderEnd != null || p.LeaderElbow != null || p.LeaderVisible.HasValue) && !willHaveLeader)
                            throw new ArgumentException("tag " + raw + " has no leader and this operation does not set has_leader=true, so leader_end/leader_elbow/leader_visible have nothing to act on.");
                        if (p.LeaderEnd != null && willBe != LeaderEndCondition.Free)
                            throw new ArgumentException("tag " + raw + ": leader_end applies only to a FREE leader end; this leader is attached and the operation does not set leader_end_condition=free. Revit ignores a free end on an attached leader, so the request is refused rather than dropped.");
                    }
                }
                p.Summary = new JObject { ["index"] = index, ["operation"] = op, ["targets"] = p.Ids.Count, ["verifiable"] = true };
                if (op == "rename_level")
                {
                    p.Summary["from"] = p.OldLevelName;
                    p.Summary["to"] = p.NewLevelName;
                    p.Summary["views_expected_to_rename"] = new JArray(p.LevelCandidateViewIds.Select(id => (JToken)id));
                    // level_rename_rehearsal (Copy/Monitor alerts) is added by the caller,
                    // which alone knows whether this is the dry-run path.
                }
                return p;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return null;
            }
        }

        private static void Apply(Document doc, Plan p)
        {
            switch (p.Operation)
            {
                case "move": ElementTransformUtils.MoveElements(doc, p.Ids, p.Vector); break;
                case "copy": p.Created = ElementTransformUtils.CopyElements(doc, p.Ids, p.Vector).ToList(); break;
                case "array_linear": case "array_radial": ApplyArray(doc, p); break;
                case "rotate": ElementTransformUtils.RotateElements(doc, p.Ids, p.Axis, p.Angle); break;
                case "mirror": ElementTransformUtils.MirrorElements(doc, p.Ids, p.MirrorPlane, false); break;
                case "pin": foreach (ElementId id in p.Ids) doc.GetElement(id).Pinned = true; break;
                case "unpin": foreach (ElementId id in p.Ids) doc.GetElement(id).Pinned = false; break;
                case "move_tag_head":
                    foreach (ElementId id in p.Ids)
                    {
                        var tag = (IndependentTag)doc.GetElement(id);
                        tag.TagHeadPosition = p.HeadPoint ?? p.HeadBefore[Rid.Value(id)].Add(p.Vector);
                    }
                    break;
                case "set_tag_leader":
                    foreach (ElementId id in p.Ids)
                    {
                        var tag = (IndependentTag)doc.GetElement(id);
                        Reference r = p.TagRefs[Rid.Value(id)];
                        // Order matters: a leader must exist before its condition, end,
                        // elbow or visibility can be addressed.
                        if (p.HasLeader.HasValue) tag.HasLeader = p.HasLeader.Value;
                        if (p.EndCondition.HasValue) tag.LeaderEndCondition = p.EndCondition.Value;
                        if (p.LeaderEnd != null) tag.SetLeaderEnd(r, p.LeaderEnd);
                        if (p.LeaderElbow != null) tag.SetLeaderElbow(r, p.LeaderElbow);
                        if (p.LeaderVisible.HasValue) tag.SetIsLeaderVisible(r, p.LeaderVisible.Value);
                    }
                    break;
                case "change_type": foreach (ElementId id in p.Ids) doc.GetElement(id).ChangeTypeId(p.TypeId); break;
                case "rename_level": ((Level)doc.GetElement(p.Ids[0])).Name = p.NewLevelName; break;
                case "set_curve":
                    foreach (ElementId id in p.Ids)
                    {
                        var lc = doc.GetElement(id).Location as LocationCurve;
                        if (lc != null) lc.Curve = p.Curve;
                    }
                    break;
                case "wall_join":
                    foreach(var id in p.Ids) { if(p.JoinAllowed) WallUtils.AllowWallJoinAtEnd((Wall)doc.GetElement(id),p.JoinEnd); else WallUtils.DisallowWallJoinAtEnd((Wall)doc.GetElement(id),p.JoinEnd); }
                    break;
            }
        }

        private static bool Verify(Document doc, Plan p, out JObject detail)
        {
            detail = new JObject { ["targets"] = p.Ids.Count };
            if (p.Operation == "array_linear" || p.Operation == "array_radial") return VerifyArray(doc, p, detail);
            if (p.Operation == "copy")
            {
                int present = p.Created == null ? 0 : p.Created.Count(id => doc.GetElement(id) != null);
                detail["copies_expected"] = p.Ids.Count; detail["copies_present"] = present;
                detail["created_ids"] = new JArray((p.Created ?? new List<ElementId>()).Select(id => (JToken)Rid.Value(id)));
                var remaining=(p.Created ?? new List<ElementId>()).Select(doc.GetElement).Where(e=>e!=null).ToList();
                int matched=0;
                foreach(var sourceId in p.Ids)
                {
                    var source=doc.GetElement(sourceId); var before=p.Samples[Rid.Value(sourceId)];
                    int index=remaining.FindIndex(e=>e.GetTypeId()==source.GetTypeId() && e.GetType()==source.GetType() &&
                        Samples(e).Count==before.Count && (MatchesSamples(before,Samples(e),p,false) || (before.Count==2 && MatchesSamples(before,Samples(e),p,true))));
                    if(index<0) continue; matched++; remaining.RemoveAt(index);
                }
                detail["copies_geometry_verified"]=matched;
                // An operation over no source proves nothing: 0 == 0 is not a copy.
                return p.Ids.Count > 0 && present == p.Ids.Count && matched==p.Ids.Count;
            }
            if (p.Operation == "rename_level")
            {
                Level level = doc.GetElement(p.Ids[0]) as Level;
                string nowName = null; try { nowName = level?.Name; } catch { }
                var renamedViews = new JArray();
                int viewsRenamed = 0;
                foreach (long vid in p.LevelCandidateViewIds)
                {
                    View v = doc.GetElement(Rid.Make(vid)) as View;
                    string nowViewName = null; try { nowViewName = v?.Name; } catch { }
                    bool renamed = nowViewName == p.NewLevelName;
                    if (renamed) viewsRenamed++;
                    renamedViews.Add(new JObject { ["view_id"] = vid, ["from"] = p.OldLevelName, ["to"] = nowViewName, ["renamed"] = renamed });
                }
                detail["level_name"] = nowName;
                detail["views_candidate"] = p.LevelCandidateViewIds.Count;
                detail["views_renamed"] = renamedViews;
                detail["views_renamed_count"] = viewsRenamed;
                return nowName == p.NewLevelName;
            }
            int good = 0;
            var tagRows = new JArray();
            foreach (ElementId id in p.Ids)
            {
                Element e = doc.GetElement(id); if (e == null) continue;
                if (p.Operation == "move_tag_head" || p.Operation == "set_tag_leader")
                {
                    var tag = e as IndependentTag;
                    var row = new JObject { ["element_id"] = Rid.Value(id) };
                    // THE TAG'S CHECKLIST, typed. The requested properties are declared up
                    // front, so a property nobody compared cannot pass by omission, and a
                    // property that could not be re-read is UNMEASURED: it used to be
                    // reported as the opposite of the request ("read": !requested), a
                    // measurement nobody took.
                    var check = new PostconditionCheck(TagProperties(p).ToArray());
                    if (tag == null)
                    {
                        foreach (string property in TagProperties(p))
                            check.Unreadable(property, JValue.CreateNull(), "the element is no longer an IndependentTag");
                    }
                    else if (p.Operation == "move_tag_head")
                    {
                        XYZ expected = p.HeadPoint ?? p.HeadBefore[Rid.Value(id)].Add(p.Vector);
                        XYZ actual = null; string why = null;
                        try { actual = tag.TagHeadPosition; } catch (Exception ex) { why = ex.Message; }
                        bool m = actual != null && actual.DistanceTo(expected) <= TagPositionToleranceFeet;
                        row["head_before_feet"] = Arr(p.HeadBefore[Rid.Value(id)]);
                        row["head_expected_feet"] = Arr(expected);
                        row["head_read_feet"] = actual == null ? (JToken)JValue.CreateNull() : Arr(actual);
                        row["tolerance_feet"] = TagPositionToleranceFeet;
                        if (actual == null) check.Unreadable("head_position", Arr(expected), why ?? "TagHeadPosition returned null");
                        else check.Record("head_position", Arr(expected), Arr(actual), m);
                    }
                    else
                    {
                        Reference r = p.TagRefs[Rid.Value(id)];
                        if (p.HasLeader.HasValue)
                        {
                            bool? v = null; string why = null;
                            try { v = tag.HasLeader; } catch (Exception ex) { why = ex.Message; }
                            row["has_leader"] = new JObject { ["requested"] = p.HasLeader.Value, ["read"] = v.HasValue ? (JToken)v.Value : JValue.CreateNull() };
                            if (v.HasValue) check.Compare("has_leader", p.HasLeader.Value, v.Value);
                            else check.Unreadable("has_leader", p.HasLeader.Value, why);
                        }
                        if (p.EndCondition.HasValue)
                        {
                            LeaderEndCondition c = LeaderEndCondition.Attached; bool readable = true; string why = null;
                            try { c = tag.LeaderEndCondition; } catch (Exception ex) { readable = false; why = ex.Message; }
                            string requested = p.EndCondition.Value.ToString().ToLowerInvariant();
                            row["leader_end_condition"] = new JObject { ["requested"] = requested, ["read"] = readable ? (JToken)c.ToString().ToLowerInvariant() : JValue.CreateNull() };
                            if (readable) check.Compare("leader_end_condition", requested, c.ToString().ToLowerInvariant());
                            else check.Unreadable("leader_end_condition", requested, why);
                        }
                        if (p.LeaderEnd != null)
                        {
                            XYZ v = null; string why = null;
                            try { v = tag.GetLeaderEnd(r); } catch (Exception ex) { why = ex.Message; }
                            bool m = v != null && v.DistanceTo(p.LeaderEnd) <= TagPositionToleranceFeet;
                            row["leader_end"] = new JObject { ["requested_feet"] = Arr(p.LeaderEnd), ["read_feet"] = v == null ? (JToken)JValue.CreateNull() : Arr(v), ["tolerance_feet"] = TagPositionToleranceFeet, ["match"] = m };
                            if (v == null) check.Unreadable("leader_end", Arr(p.LeaderEnd), why ?? "GetLeaderEnd returned null");
                            else check.Record("leader_end", Arr(p.LeaderEnd), Arr(v), m);
                        }
                        if (p.LeaderElbow != null)
                        {
                            XYZ v = null; string why = null;
                            try { v = tag.GetLeaderElbow(r); } catch (Exception ex) { why = ex.Message; }
                            bool m = v != null && v.DistanceTo(p.LeaderElbow) <= TagPositionToleranceFeet;
                            row["leader_elbow"] = new JObject { ["requested_feet"] = Arr(p.LeaderElbow), ["read_feet"] = v == null ? (JToken)JValue.CreateNull() : Arr(v), ["tolerance_feet"] = TagPositionToleranceFeet, ["match"] = m };
                            if (v == null) check.Unreadable("leader_elbow", Arr(p.LeaderElbow), why ?? "GetLeaderElbow returned null");
                            else check.Record("leader_elbow", Arr(p.LeaderElbow), Arr(v), m);
                        }
                        if (p.LeaderVisible.HasValue)
                        {
                            bool? v = null; string why = null;
                            try { v = tag.IsLeaderVisible(r); } catch (Exception ex) { why = ex.Message; }
                            row["leader_visible"] = new JObject { ["requested"] = p.LeaderVisible.Value, ["read"] = v.HasValue ? (JToken)v.Value : JValue.CreateNull() };
                            if (v.HasValue) check.Compare("leader_visible", p.LeaderVisible.Value, v.Value);
                            else check.Unreadable("leader_visible", p.LeaderVisible.Value, why);
                        }
                    }
                    bool ok = check.AllVerified;
                    row["postconditions"] = check.ToJson();
                    row["verified"] = ok;
                    tagRows.Add(row);
                    if (ok) good++;
                    continue;
                }
                if (p.Operation == "pin" && e.Pinned) good++;
                else if (p.Operation == "unpin" && !e.Pinned) good++;
                else if (p.Operation == "change_type" && e.GetTypeId() == p.TypeId) good++;
                else if (p.Operation == "set_curve")
                {
                    // RE-READ THE CURVE, and accept what a JOIN legitimately does
                    // to it. Revit trims a wall's location curve back to where the
                    // centrelines of the walls it meets cross, so the endpoints
                    // after a commit are not the endpoints that were asked for -
                    // by up to half the joined wall's thickness. Demanding an
                    // exact match would report every corner as a failure; ignoring
                    // the difference would report a wall placed anywhere on the
                    // right line as a success. The line itself is what was set, so
                    // the line itself is what is checked: same direction, and both
                    // endpoints ON it.
                    var lc = e.Location as LocationCurve;
                    if (lc == null || lc.Curve == null) continue;
                    XYZ want0 = p.Curve.GetEndPoint(0), want1 = p.Curve.GetEndPoint(1);
                    XYZ got0 = lc.Curve.GetEndPoint(0), got1 = lc.Curve.GetEndPoint(1);
                    XYZ dir = (want1 - want0).Normalize();
                    double off0 = (got0 - want0).CrossProduct(dir).GetLength();
                    double off1 = (got1 - want0).CrossProduct(dir).GetLength();
                    // 0.5 mm in feet: the same tolerance the rest of this file uses
                    // for "the model came back where we put it".
                    const double onTheLine = 0.5 / 304.8;
                    if (off0 <= onTheLine && off1 <= onTheLine) good++;
                }
                else if(p.Operation=="wall_join" && WallUtils.IsWallJoinAllowedAtEnd((Wall)e,p.JoinEnd)==p.JoinAllowed) good++;
                else if (p.Operation == "move" || p.Operation == "rotate" || p.Operation == "mirror")
                {
                    List<XYZ> after = Samples(e), before = p.Samples[Rid.Value(id)];
                    if (after.Count != before.Count) continue;
                    bool same = MatchesSamples(before, after, p, false);
                    // Revit may normalize a LocationCurve by reversing its endpoint
                    // order. That is the same committed geometry, not a failed move.
                    if (!same && before.Count == 2) same = MatchesSamples(before, after, p, true);
                    XYZ[] axesBefore;
                    if (p.Operation == "mirror" && p.Axes.TryGetValue(Rid.Value(id), out axesBefore) && axesBefore != null)
                    {
                        // Reported, not judged: how Revit writes a reflected instance's
                        // axes is what the first measurement is for.
                        XYZ[] mirrored = Axes(e);
                        var seen = detail["mirror_axes"] as JArray ?? new JArray();
                        seen.Add(new JObject
                        {
                            ["element_id"] = Rid.Value(id),
                            ["reflected_basis_x"] = Arr(p.Rotation.OfVector(axesBefore[0])),
                            ["reflected_basis_z"] = Arr(p.Rotation.OfVector(axesBefore[1])),
                            ["basis_x"] = mirrored == null ? null : Arr(mirrored[0]),
                            ["basis_z"] = mirrored == null ? null : Arr(mirrored[1]),
                            ["mirrored_flag"] = (e as FamilyInstance)?.Mirrored
                        });
                        detail["mirror_axes"] = seen;
                    }
                    if (same && p.Operation == "rotate" && p.Axes.TryGetValue(Rid.Value(id), out axesBefore) &&
                        axesBefore != null)
                    {
                        XYZ[] axesAfter = Axes(e);
                        bool turned = axesAfter != null &&
                                      p.Rotation.OfVector(axesBefore[0]).IsAlmostEqualTo(axesAfter[0], 1e-6) &&
                                      p.Rotation.OfVector(axesBefore[1]).IsAlmostEqualTo(axesAfter[1], 1e-6);
                        if (!turned)
                        {
                            same = false;
                            var why = detail["orientation_not_turned"] as JArray ?? new JArray();
                            why.Add(new JObject
                            {
                                ["element_id"] = Rid.Value(id),
                                ["expected_basis_x"] = Arr(p.Rotation.OfVector(axesBefore[0])),
                                ["expected_basis_z"] = Arr(p.Rotation.OfVector(axesBefore[1])),
                                ["basis_x"] = axesAfter == null ? null : Arr(axesAfter[0]),
                                ["basis_z"] = axesAfter == null ? null : Arr(axesAfter[1]),
                                ["means"] = "the element's point is where the turn puts it, and its axes are not: " +
                                            "Revit did not turn it as asked (a hosted instance keeps the " +
                                            "orientation its host allows)."
                            });
                            detail["orientation_not_turned"] = why;
                        }
                    }
                    if (same) good++;
                }
            }
            detail["targets_verified"] = good;
            if (tagRows.Count > 0) detail["tags"] = tagRows;
            // An operation that targeted nothing verified nothing: good == Ids.Count over an
            // empty list is the vacuous pass PostconditionCheck exists to refuse.
            return p.Ids.Count > 0 && good == p.Ids.Count;
        }

        /// <summary>The properties a tag operation asked for - the tag checklist's required set.</summary>
        private static List<string> TagProperties(Plan p)
        {
            var required = new List<string>();
            if (p.Operation == "move_tag_head") { required.Add("head_position"); return required; }
            if (p.HasLeader.HasValue) required.Add("has_leader");
            if (p.EndCondition.HasValue) required.Add("leader_end_condition");
            if (p.LeaderEnd != null) required.Add("leader_end");
            if (p.LeaderElbow != null) required.Add("leader_elbow");
            if (p.LeaderVisible.HasValue) required.Add("leader_visible");
            return required;
        }

        /// <summary>0.003 mm: under anything a drawing shows, over floating-point noise.</summary>
        private const double TagPositionToleranceFeet = 1e-5;

        private static JArray Arr(XYZ p) => new JArray(p.X, p.Y, p.Z);

        private static bool MatchesSamples(List<XYZ> before, List<XYZ> after, Plan p, bool reverse)
        {
            for (int i = 0; i < before.Count; i++)
            {
                XYZ expected = p.Operation == "move" || p.Operation == "copy" ? before[i] + p.Vector : p.Rotation.OfPoint(before[i]);
                int actualIndex = reverse ? after.Count - 1 - i : i;
                if (expected.DistanceTo(after[actualIndex]) > 1e-6) return false;
            }
            return true;
        }

        /// <summary>How far a face-hosted element's point is from the plane-bounded face it is hosted on.</summary>
        private const double FaceToleranceMm = 0.5;

        private static double? OffItsFaceMm(Element e)
        {
            try
            {
                var fi = e as FamilyInstance;
                Reference faceRef = fi?.HostFace;
                var point = (fi?.Location as LocationPoint)?.Point;
                if (faceRef == null || point == null) return null;
                var face = e.Document.GetElement(faceRef)?.GetGeometryObjectFromReference(faceRef) as Face;
                if (face == null) return null;
                IntersectionResult projection = face.Project(point);
                if (projection == null) return double.MaxValue;   // it projects onto no part of the face
                return projection.Distance * 304.8;
            }
            catch { return null; }
        }

        private static long? HostOf(Element e)
        {
            try
            {
                Element host = (e as FamilyInstance)?.Host;
                return host == null ? (long?)null : Rid.Value(host.Id);
            }
            catch { return null; }
        }

        private static void GuardHosts(Document doc, Plan p)
        {
            foreach (KeyValuePair<long, long> kv in p.HostBefore)
            {
                Element element = doc.GetElement(Rid.Make(kv.Key));
                long? now = HostOf(element);
                if (now == kv.Value)
                {
                    // ON ITS FACE, NOT MERELY OWNED BY ITS WALL. MEASURED: a face-hosted
                    // receptacle moved 200 mm across its wall kept the same host and
                    // ended up 38 mm out of the far face, and the point check verified it.
                    double? off = OffItsFaceMm(element);
                    if (off.HasValue && off.Value > FaceToleranceMm)
                        throw new InvalidOperationException(
                            "host_changed: " + p.Operation + " would leave element " + kv.Key + " " +
                            off.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                            " mm off the face of its host (element " + kv.Value + "). A face-hosted element moves " +
                            "along its face; one that belongs on another face is placed again there, which this " +
                            "command does not do");
                    continue;
                }
                throw new InvalidOperationException(
                    "host_changed: " + p.Operation + " would take element " + kv.Key + " off its host (element " +
                    kv.Value + ")" + (now.HasValue ? " onto element " + now.Value : ", leaving it with no host") +
                    ". A hosted element moves or turns only where its host carries it; one that must change host " +
                    "is placed again on the new one, which this command does not do");
            }
        }

        /// <summary>An instance's X and Z axes as Revit stores them; null for anything that has none.</summary>
        private static XYZ[] Axes(Element e)
        {
            var fi = e as FamilyInstance;
            if (fi == null) return null;
            try
            {
                Transform t = fi.GetTotalTransform();
                return new[] { t.BasisX, t.BasisZ };
            }
            catch { return null; }
        }

        private static List<XYZ> Samples(Element e)
        {
            var result = new List<XYZ>();
            LocationPoint point = e.Location as LocationPoint;
            if (point != null) { result.Add(point.Point); return result; }
            LocationCurve curve = e.Location as LocationCurve;
            if (curve != null && curve.Curve != null)
            { result.Add(curve.Curve.GetEndPoint(0)); result.Add(curve.Curve.GetEndPoint(1)); return result; }
            // A RevitLinkInstance exposes NO sampleable Location (measured live,
            // 2026-08-26) - yet moving a link is an everyday coordination operation,
            // and its verification story is as sound as any location's: the total
            // transform's origin is where the placement stands, and a committed move
            // must show that origin displaced by exactly the requested vector.
            // The same holds for an ImportInstance - a placed DWG - whose move is
            // what an incremental update has to detect (measured 2026-09-03: the
            // typed move refused it as unsampleable). Instance is the base of both.
            var instance = e as Instance;
            if (instance != null)
            {
                try
                {
                    Transform total = instance.GetTotalTransform();
                    if (total != null) result.Add(total.Origin);
                }
                catch { /* no sample stays no sample, and the refusal names it */ }
            }
            return result;
        }
        private static XYZ Point(JToken token, double scale, string name)
        {
            JArray a = token as JArray;
            if (a == null || a.Count != 3) throw new ArgumentException(name + " must contain three coordinates");
            return new XYZ(a[0].Value<double>() * scale, a[1].Value<double>() * scale, a[2].Value<double>() * scale);
        }
        private static bool Scale(string units, out double scale)
        { if (units == "feet") { scale = 1; return true; } if (units == "m") { scale = 1 / 0.3048; return true; } if (units == "mm") { scale = 1 / 304.8; return true; } scale = 0; return false; }

        /// <summary>
        /// Identity for the plan. Every read here is guarded: a plan that throws while
        /// being MEASURED would turn a safety feature into a new way to fail.
        /// </summary>
        private static string SafePlanUniqueId(Element e)
        {
            try { return e.UniqueId; } catch { return null; }
        }

        private static string SafePlanTypeName(Document doc, Element e)
        {
            try
            {
                ElementId tid = e.GetTypeId();
                if (tid == null || tid == ElementId.InvalidElementId) return null;
                Element t = doc.GetElement(tid);
                return t == null ? null : t.Name;
            }
            catch { return null; }
        }

        /// <summary>
        /// A rounded bounding box: enough to notice that an element moved or changed shape
        /// between the rehearsal and the apply, cheap enough to take for every element in a
        /// batch. Rounded to a millimetre because Revit's own regeneration jitters the last
        /// digits, and a fingerprint that changes on its own would refuse every apply.
        /// </summary>
        private static string SafePlanGeometry(Element e)
        {
            try
            {
                BoundingBoxXYZ b = e.get_BoundingBox(null);
                if (b == null) return null;
                const double mm = 304.8;   // feet -> mm
                return string.Join(",", new[]
                {
                    Math.Round(b.Min.X * mm), Math.Round(b.Min.Y * mm), Math.Round(b.Min.Z * mm),
                    Math.Round(b.Max.X * mm), Math.Round(b.Max.Y * mm), Math.Round(b.Max.Z * mm)
                }.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
            }
            catch { return null; }
        }

        // ---- arrays -------------------------------------------------------------------
        // LinearArray / RadialArray, with or without association (group=true keeps the
        // Revit array element). Every copy is re-read and matched against the formula:
        // member k of each source sits at the source's samples moved by k*step (linear)
        // or turned by k*step angle about the axis (radial). anchor=last divides the
        // vector/angle over count-1 steps, a full turn over count
        // (ArchitecturalEditRules.ArrayStepVector / ArrayStepAngle).

        private static void PlanArray(Document doc, JObject o, double scale, Plan p)
        {
            bool radial = p.Operation == "array_radial";
            if (o["count"]?.Type != JTokenType.Integer) throw new ArgumentException(p.Operation + " requires an integer count (members including the original).");
            p.Count = o.Value<int>("count");
            string countError = ArchitecturalEditRules.ValidateArrayCount(radial, p.Count);
            if (countError != null) throw new ArgumentException(countError);
            string anchor = (o.Value<string>("anchor") ?? "second").ToLowerInvariant();
            if (anchor != "second" && anchor != "last") throw new ArgumentException("anchor must be second or last.");
            p.AnchorLast = anchor == "last";
            if (o["group"] != null && o["group"].Type != JTokenType.Boolean) throw new ArgumentException("group must be a boolean.");
            p.Grouped = o.Value<bool?>("group") ?? false;
            View view = doc.ActiveView;
            if (view == null || view is ViewSheet || view is ViewSchedule)
                throw new ArgumentException("arrays are created in the ACTIVE view, and the active view is " + (view == null ? "none" : "a " + view.GetType().Name) + "; activate a plan, section or 3D view.");
            foreach (ElementId id in p.Ids)
                if (!LinearArray.IsElementArrayable(doc, id))
                    throw new ArgumentException("ElementId " + Rid.Value(id) + " cannot be arrayed (LinearArray.IsElementArrayable is false).");
            if (!radial)
            {
                XYZ v = Point(o["vector"], scale, "vector");
                if (v.GetLength() < 1e-9) throw new ArgumentException("vector must not be zero");
                double[] s = ArchitecturalEditRules.ArrayStepVector(new[] { v.X, v.Y, v.Z }, p.Count, p.AnchorLast);
                p.Vector = v; p.Step = new XYZ(s[0], s[1], s[2]);
            }
            else
            {
                XYZ a = Point(o["axis_start"], scale, "axis_start"), b = Point(o["axis_end"], scale, "axis_end");
                if (a.DistanceTo(b) < 1e-9) throw new ArgumentException("rotation axis endpoints must differ");
                if (o["angle_degrees"] == null) throw new ArgumentException("angle_degrees is required for array_radial");
                p.Axis = Line.CreateBound(a, b); p.AxisOrigin = a; p.AxisDirection = (b - a).Normalize();
                p.Angle = o.Value<double>("angle_degrees") * Math.PI / 180.0;
                if (Math.Abs(p.Angle) < 1e-9) throw new ArgumentException("angle_degrees must not be zero");
                p.StepAngle = ArchitecturalEditRules.ArrayStepAngle(p.Angle, p.Count, p.AnchorLast);
            }
        }

        private static void ApplyArray(Document doc, Plan p)
        {
            View view = doc.ActiveView;
            ArrayAnchorMember anchor = p.AnchorLast ? ArrayAnchorMember.Last : ArrayAnchorMember.Second;
            ICollection<ElementId> made;
            if (p.Operation == "array_linear")
            {
                if (p.Grouped)
                {
                    LinearArray array = LinearArray.Create(doc, view, p.Ids, p.Count, p.Vector, anchor);
                    p.ArrayId = Rid.Value(array.Id); made = array.GetCopiedMemberIds();
                }
                else made = LinearArray.ArrayElementsWithoutAssociation(doc, view, p.Ids, p.Count, p.Vector, anchor);
            }
            else
            {
                if (p.Grouped)
                {
                    RadialArray array = RadialArray.Create(doc, view, p.Ids, p.Count, p.Axis, p.Angle, anchor);
                    p.ArrayId = Rid.Value(array.Id); made = array.GetCopiedMemberIds();
                }
                else made = RadialArray.ArrayElementsWithoutAssociation(doc, view, p.Ids, p.Count, p.Axis, p.Angle, anchor);
            }
            p.Created = (made ?? new List<ElementId>()).ToList();
        }

        private static bool VerifyArray(Document doc, Plan p, JObject detail)
        {
            bool grouped = p.Grouped;
            var required = new List<string> { "copies_present", "copies_at_formula" };
            if (grouped) required.Add("array_element");
            var check = new PostconditionCheck(required.ToArray());
            // Leaves: a member that Revit wrapped in a group is judged by what it holds.
            var leaves = new List<Element>();
            foreach (ElementId id in p.Created ?? new List<ElementId>())
            {
                Element e = doc.GetElement(id);
                if (e is Group g) leaves.AddRange(g.GetMemberIds().Select(doc.GetElement).Where(x => x != null));
                else if (e != null) leaves.Add(e);
            }
            var sources = new HashSet<long>(p.Ids.Select(Rid.Value));
            leaves = leaves.Where(e => !sources.Contains(Rid.Value(e.Id))).ToList();
            int expected = p.Ids.Count * (p.Count - 1);
            check.Compare("copies_present", expected, leaves.Count);
            int matched = 0;
            var misses = new JArray();
            var remaining = new List<Element>(leaves);
            foreach (ElementId sourceId in p.Ids)
            {
                Element source = doc.GetElement(sourceId);
                List<XYZ> before = p.Samples[Rid.Value(sourceId)];
                for (int k = 1; k < p.Count; k++)
                {
                    Func<XYZ, XYZ> member = p.Operation == "array_linear"
                        ? (Func<XYZ, XYZ>)(x => x + p.Step * k)
                        : (x => Transform.CreateRotationAtPoint(p.AxisDirection, p.StepAngle * k, p.AxisOrigin).OfPoint(x));
                    var want = before.Select(member).ToList();
                    int hit = remaining.FindIndex(e => source != null && e.GetTypeId() == source.GetTypeId() && e.GetType() == source.GetType() &&
                        SamplesNear(want, Samples(e)));
                    if (hit < 0) { misses.Add(new JObject { ["source_id"] = Rid.Value(sourceId), ["member"] = k, ["expected_feet"] = new JArray(want.Select(Arr)) }); continue; }
                    matched++; remaining.RemoveAt(hit);
                }
            }
            check.Compare("copies_at_formula", expected, matched);
            if (grouped)
                check.Compare("array_element", true, p.ArrayId.HasValue && doc.GetElement(Rid.Make(p.ArrayId.Value)) is BaseArray);
            detail["created_ids"] = new JArray(leaves.Select(e => (JToken)Rid.Value(e.Id)));
            detail["array_id"] = p.ArrayId.HasValue ? (JToken)p.ArrayId.Value : JValue.CreateNull();
            detail["step_feet"] = p.Step == null ? null : Arr(p.Step);
            if (p.Operation == "array_radial") detail["step_degrees"] = p.StepAngle * 180 / Math.PI;
            if (misses.Count > 0) detail["members_not_at_formula"] = misses;
            detail["postconditions"] = check.ToJson();
            return check.AllVerified;
        }

        /// <summary>Same samples within 1e-5 ft, in order or (for a curve) reversed.</summary>
        private static bool SamplesNear(List<XYZ> want, List<XYZ> got)
        {
            if (want.Count != got.Count || want.Count == 0) return false;
            bool fwd = true, rev = true;
            for (int i = 0; i < want.Count; i++)
            {
                fwd &= want[i].DistanceTo(got[i]) <= 1e-5;
                rev &= want[i].DistanceTo(got[got.Count - 1 - i]) <= 1e-5;
            }
            return fwd || (want.Count == 2 && rev);
        }

        private sealed class Plan
        {
            public int JoinEnd; public bool JoinAllowed;
            public int Index; public string Operation; public List<ElementId> Ids, Created; public XYZ Vector;
            public Line Axis; public double Angle; public Transform Rotation; public ElementId TypeId;
            /// <summary>mirror: the plane reflected about. Rotation then holds the reflection, for verification.</summary>
            public Plane MirrorPlane;
            /// <summary>set_curve: the line this element's LocationCurve is being set to.</summary>
            public Line Curve;
            public Dictionary<long, List<XYZ>> Samples; public JObject Summary;
            /// <summary>rotate: each instance's X and Z axes before the turn.</summary>
            public readonly Dictionary<long, XYZ[]> Axes = new Dictionary<long, XYZ[]>();
            /// <summary>move/rotate: the host each hosted instance had before, which it must keep.</summary>
            public readonly Dictionary<long, long> HostBefore = new Dictionary<long, long>();
            // move_tag_head / set_tag_leader
            public XYZ HeadPoint;
            public readonly Dictionary<long, XYZ> HeadBefore = new Dictionary<long, XYZ>();
            public readonly Dictionary<long, Reference> TagRefs = new Dictionary<long, Reference>();
            public bool? HasLeader, LeaderVisible;
            public LeaderEndCondition? EndCondition;
            public XYZ LeaderEnd, LeaderElbow;
            // array_linear / array_radial
            public int Count; public bool AnchorLast, Grouped; public XYZ Step; public double StepAngle; public XYZ AxisDirection, AxisOrigin;
            public long? ArrayId;
            // rename_level
            public string OldLevelName, NewLevelName;
            public readonly List<long> LevelCandidateViewIds = new List<long>();
        }

        /// <summary>
        /// The ONLY way to know which Copy/Monitor alerts a level rename raises: perform
        /// the rename inside a transaction whose failure preprocessor captures every
        /// message (errors AND warnings - Copy/Monitor alerts are warnings), regenerate
        /// so Revit's coordination engine actually runs, then roll back. Which plan views
        /// will rename is NOT measured here - that is a deterministic read done once at
        /// plan time (p.LevelCandidateViewIds), because Revit's rule (exact name match)
        /// needs no rehearsal.
        /// </summary>
        private static JObject RehearseRenameLevel(Document doc, Plan p)
        {
            var views = new JArray();
            RevitErrorRecorder log = null;
            Guard.RollbackResult? rb = null; string rbError = null; string error = null;
            using (var tx = new Transaction(doc, "Horizun: rehearse rename_level"))
            {
                try
                {
                    log = RevitErrorRecorder.On(tx, captureWarnings: true);
                    tx.Start();
                    Level level = doc.GetElement(p.Ids[0]) as Level;
                    level.Name = p.NewLevelName;
                    doc.Regenerate();
                    foreach (long vid in p.LevelCandidateViewIds)
                    {
                        View v = doc.GetElement(Rid.Make(vid)) as View;
                        string now = null; try { now = v?.Name; } catch { }
                        views.Add(new JObject { ["view_id"] = vid, ["from"] = p.OldLevelName, ["to"] = now, ["will_rename"] = now == p.NewLevelName });
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                try { rb = Guard.RollBack(tx); } catch (Exception ex) { rbError = ex.Message; }
            }
            return new JObject
            {
                ["level_id"] = Rid.Value(p.Ids[0]),
                ["from"] = p.OldLevelName,
                ["to"] = p.NewLevelName,
                ["views_expected_to_rename"] = views,
                ["copy_monitor_alerts"] = new JArray(log?.Warnings ?? new List<string>()),
                ["errors_raised"] = new JArray(log?.Errors ?? new List<string>()),
                ["error"] = error,
                ["rollback_status"] = rb.HasValue ? rb.Value.StatusName : "exception: " + rbError,
                ["rollback_confirmed"] = rb.HasValue && rb.Value.Confirmed,
                ["means"] = "views_expected_to_rename is a deterministic read: Revit renames a plan view only " +
                            "when its name exactly matches the level's, before the rename. copy_monitor_alerts " +
                            "and errors_raised come from an ACTUAL rename performed inside this rolled-back " +
                            "transaction, because Revit's Copy/Monitor coordination engine is the only source of " +
                            "truth for what it will raise."
            };
        }
    }
}
