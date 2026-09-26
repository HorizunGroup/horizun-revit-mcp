// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_resolve_clash - RESOLVE a recorded clash, with a verification.
//
//   propose  READ-ONLY. For findings of the horizun_clash ledger, proposes a
//            conservative correction (Core/ClashResolveRules.cs): move the flexible
//            MEP run the minimum distance plus clearance, as a perpendicular shift
//            or an elevation offset. Structure/architecture, linked elements,
//            connected runs, pinned runs and moves that would touch a third element
//            are REPORTED, never auto-resolved. Each proposal carries the typed action
//            and a verifiable prediction.
//   apply    dry_run (default) -> token -> commit inside a TransactionGroup, then
//            RE-DETECTS on solids over the affected neighbourhood: every targeted pair
//            must be gone and no pair may appear that was not there before the move.
//            Anything else rolls the WHOLE group back and says why. Kept work is
//            recorded for horizun_undo, and the finding becomes resolved_by_model
//            only from that measurement.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ResolveClashCommand : ICommand
    {
        public string Name => "horizun_resolve_clash";
        public string Description => "Propose and apply verified clash resolutions for findings of the horizun_clash ledger.";
        private const double MmPerFoot = 304.8;
        private const double TinyVolume = 1e-6;   // ft3

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            string op = request.Value<string>("operation") ?? "propose";
            double clearance = request.Value<double?>("clearance_mm") ?? 50;
            double maxMove = request.Value<double?>("max_move_mm") ?? 600;
            if (clearance < 0 || clearance > 500) return CommandResult.Fail("clearance_mm must be 0..500.");
            if (maxMove <= 0 || maxMove > 5000) return CommandResult.Fail("max_move_mm must be in (0, 5000].");
            if (op == "propose") return Propose(app, request, clearance, maxMove);
            if (op == "apply") return Apply(app, request, clearance, maxMove);
            return CommandResult.Fail("operation must be propose or apply.");
        }

        // ---- propose --------------------------------------------------------------

        private CommandResult Propose(UIApplication app, JObject request, double clearance, double maxMove)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            CommandResult readRefusal = DocumentGate.ReadGuard(doc, request, Name);
            if (readRefusal != null) return readRefusal;
            List<string> ids = (request["finding_ids"] as JArray ?? new JArray()).Select(t => (string)t).ToList();
            if (ids.Count == 0 || ids.Count > 50) return CommandResult.Fail("finding_ids must list 1..50 findings (horizun_coordination list).");
            string ledgerPath = CoordinationLedger.PathFor(doc.Title, UndoCapture.SafePath(doc));
            Dictionary<string, CoordinationFinding> ledger = CoordinationLedger.Load(ledgerPath, out string _);

            var rows = new JArray();
            var actions = new JArray();
            foreach (string id in ids)
            {
                var row = new JObject { ["finding_id"] = id };
                rows.Add(row);
                if (!ledger.TryGetValue(id, out CoordinationFinding f))
                { row["status"] = "unknown_finding"; row["reason"] = "not in this document's ledger"; continue; }
                if (f.Status == CoordinationRules.StatusResolvedByModel || f.Status == CoordinationRules.StatusClosedByDecision)
                { row["status"] = "not_open"; row["reason"] = "finding is " + f.Status; continue; }
                string code, reason;
                JObject action = Plan(doc, f, clearance, maxMove, row, out code, out reason);
                if (action == null) { row["status"] = "report_only"; row["code"] = code; row["reason"] = reason; continue; }
                row["status"] = "proposed";
                actions.Add(action);
            }
            var result = new JObject
            {
                ["read_only"] = true, ["proposals"] = rows, ["proposed"] = actions.Count,
                ["clearance_mm"] = clearance, ["max_move_mm"] = maxMove,
                ["note"] = "Candidates only: nothing was written. Box-based distances are conservative; apply re-measures on solids."
            };
            if (actions.Count > 0)
                result["next_arguments"] = new JObject
                {
                    ["operation"] = "apply", ["target_document"] = doc.Title, ["clearance_mm"] = clearance,
                    ["proposals"] = actions, ["dry_run"] = true
                };
            return CommandResult.Ok(result);
        }

        /// <summary>One finding's proposal, or null with the report-only code.</summary>
        private static JObject Plan(Document doc, CoordinationFinding f, double clearance, double maxMove, JObject row,
                                    out string code, out string reason)
        {
            code = null; reason = null;
            if (!ClashResolveRules.ParseSide(f.SideA, out bool hostA, out string uidA) ||
                !ClashResolveRules.ParseSide(f.SideB, out bool hostB, out string uidB))
            { code = ClashResolveRules.CodeNoGeometry; reason = "the finding's sides cannot be parsed"; return null; }
            Element a = hostA ? doc.GetElement(uidA) : null, b = hostB ? doc.GetElement(uidB) : null;
            if ((hostA && a == null) || (hostB && b == null))
            { code = ClashResolveRules.CodeNoGeometry; reason = "an element of the pair no longer exists in the host"; return null; }
            string roleA = Role(a, hostA, f.CategoryA), roleB = Role(b, hostB, f.CategoryB);
            int mover = ClashResolveRules.ChooseMover(roleA, hostA, Section(a), roleB, hostB, Section(b), out code, out reason);
            row["roles"] = new JArray(roleA, roleB);
            // An external tool (e.g. a navisworks issue) may name one side immovable. That
            // preference is enforced HERE, on top of the natural choice above, never inside
            // ChooseMover: ChooseMover is the arithmetic over roles and sections alone, and
            // stays testable without knowing where a finding came from.
            int? forcedImmovable = f.ImmovableSideIsA.HasValue ? (f.ImmovableSideIsA.Value ? 0 : 1) : (int?)null;
            if (forcedImmovable.HasValue)
            {
                bool aEligible = roleA == ClashResolveRules.RoleMovable && hostA;
                bool bEligible = roleB == ClashResolveRules.RoleMovable && hostB;
                if (!ClashResolveRules.EnforceImmovableSide(mover, aEligible, bEligible, forcedImmovable,
                        out mover, out string immovCode, out string immovReason))
                { code = immovCode; reason = immovReason; return null; }
                if (immovReason != null) row["immovable_side_note"] = immovReason;
            }
            if (mover < 0) return null;
            Element m = mover == 0 ? a : b, other = mover == 0 ? b : a;
            row["mover_id"] = Rid.Value(m.Id);
            if (other != null) row["fixed_id"] = Rid.Value(other.Id);
            if (!Validate(doc, m, out code, out reason)) return null;
            ResolveRun run = Run(m);
            if (run == null) { code = ClashResolveRules.CodeNoGeometry; reason = "the run has no straight centreline or readable section"; return null; }
            ResolveBox fixedBox = other != null ? Box(other.get_BoundingBox(null)) : null;
            if (fixedBox == null) { code = ClashResolveRules.CodeNoGeometry; reason = "the fixed side has no bounding box in the host"; return null; }
            List<ResolveCandidate> candidates = ClashResolveRules.Candidates(run, fixedBox, clearance, out string geomCode);
            var considered = new JArray();
            foreach (ResolveCandidate c in candidates)
            {
                var cr = new JObject { ["kind"] = c.Kind, ["distance_mm"] = c.DistanceMm, ["vector_mm"] = new JArray(c.VectorMm) };
                considered.Add(cr);
                if (c.DistanceMm > maxMove) { cr["rejected"] = ClashResolveRules.CodeTooFar; continue; }
                List<long> contacts = PredictedContacts(doc, m, other, c.VectorMm, clearance);
                if (contacts.Count > 0) { cr["rejected"] = "would_touch_other_elements"; cr["contacts"] = new JArray(contacts); continue; }
                row["candidates"] = considered;
                row["kind"] = c.Kind; row["distance_mm"] = c.DistanceMm;
                row["affected_elements"] = new JArray(Rid.Value(m.Id));
                row["prediction"] = "after apply, the pair " + Rid.Value(m.Id) + "-" + Rid.Value(other.Id) +
                    " does not intersect (box clearance >= " + clearance + " mm) and no new clash appears with the elements " +
                    "around the moved run - verified by solid re-detection at apply, or rolled back.";
                if (geomCode != null) row["note"] = geomCode;
                return new JObject { ["finding_id"] = f.Id, ["element_id"] = Rid.Value(m.Id), ["vector_mm"] = new JArray(c.VectorMm), ["kind"] = c.Kind };
            }
            row["candidates"] = considered;
            code = candidates.Count == 0 ? (geomCode ?? ClashResolveRules.CodeNoGeometry) : "no_safe_candidate";
            reason = candidates.Count == 0 ? "no escape direction exists for this run" :
                "every candidate either exceeds max_move_mm or would touch another element - report only";
            return null;
        }

        private static string Role(Element e, bool host, string categoryName)
        {
            if (e == null) return host ? ClashResolveRules.RoleOther : (categoryName != null && (categoryName.Contains("Pipe") || categoryName.Contains("Duct") || categoryName.Contains("Conduit") || categoryName.Contains("Cable")) ? ClashResolveRules.RoleMovable : ClashResolveRules.RoleOther);
            string bic = null;
            try { bic = ((BuiltInCategory)Rid.Value(e.Category.Id)).ToString(); } catch { }
            return ClashResolveRules.RoleOf(bic, IsStructural(e));
        }

        private static bool IsStructural(Element e)
        {
            try
            {
                if (e is Wall) return e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1;
                if (e is Floor) return e.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1;
            }
            catch { }
            return false;
        }

        private static double Section(Element e)
        {
            if (e == null) return 0;
            return MepFacts.TryProfile(e, out string _, out double w, out double h) ? w * h : 0;
        }

        private static bool Validate(Document doc, Element m, out string code, out string reason)
        {
            code = null; reason = null;
            bool pinned; try { pinned = m.Pinned; } catch { pinned = true; }
            if (pinned) { code = ClashResolveRules.CodePinned; reason = "the run is pinned; unpin it deliberately first"; return false; }
            foreach (Connector c in MepFacts.Ordered(MepFacts.ManagerOf(m)))
            {
                bool connected; try { connected = c.IsConnected; } catch { connected = true; }
                if (connected)
                {
                    code = ClashResolveRules.CodeConnected;
                    reason = "the run is connected to fittings/other runs; moving one segment would tear the network - not safe, reroute the run deliberately";
                    return false;
                }
            }
            return true;
        }

        private static ResolveRun Run(Element m)
        {
            if (!((m.Location as LocationCurve)?.Curve is Line line)) return null;
            if (!MepFacts.TryProfile(m, out string _, out double w, out double h)) return null;
            return new ResolveRun
            {
                Start = Mm(line.GetEndPoint(0)), End = Mm(line.GetEndPoint(1)),
                Width = w * MmPerFoot, Height = h * MmPerFoot
            };
        }

        private static double[] Mm(XYZ p) => new[] { p.X * MmPerFoot, p.Y * MmPerFoot, p.Z * MmPerFoot };

        private static ResolveBox Box(BoundingBoxXYZ bb) => bb == null ? null :
            new ResolveBox(bb.Min.X * MmPerFoot, bb.Min.Y * MmPerFoot, bb.Min.Z * MmPerFoot,
                           bb.Max.X * MmPerFoot, bb.Max.Y * MmPerFoot, bb.Max.Z * MmPerFoot);

        /// <summary>Model elements whose box the moved run's box would reach (excluding the fixed side).</summary>
        private static List<long> PredictedContacts(Document doc, Element m, Element fixedSide, double[] vectorMm, double clearance)
        {
            BoundingBoxXYZ bb = m.get_BoundingBox(null);
            var moved = new ResolveBox(
                bb.Min.X * MmPerFoot + vectorMm[0], bb.Min.Y * MmPerFoot + vectorMm[1], bb.Min.Z * MmPerFoot + vectorMm[2],
                bb.Max.X * MmPerFoot + vectorMm[0], bb.Max.Y * MmPerFoot + vectorMm[1], bb.Max.Z * MmPerFoot + vectorMm[2]);
            var result = new List<long>();
            foreach (Element e in Neighbours(doc, moved, 0))
            {
                if (e.Id == m.Id || (fixedSide != null && e.Id == fixedSide.Id)) continue;
                ResolveBox other = Box(e.get_BoundingBox(null));
                if (other != null && ClashResolveRules.BoxesOverlap(moved, other, 0)) result.Add(Rid.Value(e.Id));
            }
            return result;
        }

        private static IEnumerable<Element> Neighbours(Document doc, ResolveBox box, double growMm)
        {
            var outline = new Outline(
                new XYZ((box.MinX - growMm) / MmPerFoot, (box.MinY - growMm) / MmPerFoot, (box.MinZ - growMm) / MmPerFoot),
                new XYZ((box.MaxX + growMm) / MmPerFoot, (box.MaxY + growMm) / MmPerFoot, (box.MaxZ + growMm) / MmPerFoot));
            return new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .Where(e => { try { return e.Category != null && e.Category.CategoryType == CategoryType.Model && !(e is RevitLinkInstance); } catch { return false; } })
                .ToList();
        }

        // ---- apply -----------------------------------------------------------------

        private sealed class Move { public string FindingId; public Element El; public XYZ Vector; public long FixedId; public JObject BeforeState; }

        private CommandResult Apply(UIApplication app, JObject request, double clearance, double maxMove)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            JArray input = request["proposals"] as JArray;
            if (input == null || input.Count == 0 || input.Count > 50) return CommandResult.Fail("proposals must list 1..50 entries from operation=propose.");
            string ledgerPath = CoordinationLedger.PathFor(doc.Title, UndoCapture.SafePath(doc));
            Dictionary<string, CoordinationFinding> ledger = CoordinationLedger.Load(ledgerPath, out string ledgerTitle);

            var moves = new List<Move>(); var errors = new JArray(); var claimed = new HashSet<long>();
            for (int i = 0; i < input.Count; i++)
            {
                var o = input[i] as JObject;
                string error = Parse(doc, o, ledger, maxMove, claimed, out Move mv);
                if (error != null) errors.Add(new JObject { ["index"] = i, ["error"] = error }); else moves.Add(mv);
            }
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "proposals", "clearance_mm");
            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["valid"] = moves.Count, ["invalid"] = errors.Count,
                    ["errors"] = errors,
                    ["plan"] = new JArray(moves.Select(mv => (JToken)new JObject
                    {
                        ["finding_id"] = mv.FindingId, ["element_id"] = Rid.Value(mv.El.Id), ["fixed_id"] = mv.FixedId,
                        ["vector_mm"] = new JArray(mv.Vector.X * MmPerFoot, mv.Vector.Y * MmPerFoot, mv.Vector.Z * MmPerFoot)
                    })),
                    ["note"] = "Nothing was moved. The apply keeps the group only if solid re-detection shows every targeted pair gone and no new clash."
                };
                ApplicationOutcome.StampRehearsal(result, input.Count, errors.Count, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0 ? "the token binds the proposals and clearance" : "no token while any proposal is invalid");
                return CommandResult.Ok(result);
            }
            if (errors.Count > 0) return CommandResult.Fail("Invalid proposals; nothing ran: " + errors.ToString(Formatting.None));
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash);
            if (refusal != null) return refusal;

            // The neighbourhood: every model element whose box meets the swept region of any
            // mover (before AND after), so "before" and "after" are measured over one set.
            var movedIds = new HashSet<long>(moves.Select(mv => Rid.Value(mv.El.Id)));
            var region = new Dictionary<long, Element>();
            foreach (Move mv in moves)
            {
                BoundingBoxXYZ bb = mv.El.get_BoundingBox(null);
                ResolveBox b0 = Box(bb);
                double[] v = { mv.Vector.X * MmPerFoot, mv.Vector.Y * MmPerFoot, mv.Vector.Z * MmPerFoot };
                var swept = new ResolveBox(Math.Min(b0.MinX, b0.MinX + v[0]), Math.Min(b0.MinY, b0.MinY + v[1]), Math.Min(b0.MinZ, b0.MinZ + v[2]),
                                           Math.Max(b0.MaxX, b0.MaxX + v[0]), Math.Max(b0.MaxY, b0.MaxY + v[1]), Math.Max(b0.MaxZ, b0.MaxZ + v[2]));
                foreach (Element e in Neighbours(doc, swept, clearance)) region[Rid.Value(e.Id)] = e;
            }
            bool completeBefore;
            List<string> before = Detect(doc, movedIds, region.Keys, out completeBefore);
            foreach (Move mv in moves) mv.BeforeState = UndoCapture.State(doc, Rid.Value(mv.El.Id));

            string txName = "Horizun: resolve clash";
            var postconditions = new PostconditionCheck(moves.Select(mv => "position:" + Rid.Value(mv.El.Id))
                .Concat(moves.Select(mv => "pair_cleared:" + mv.FindingId)).Concat(new[] { "no_new_clash" }).ToArray());
            List<string> after = null; bool completeAfter = false; string verdict = null; bool keep = false;
            using (var group = new TransactionGroup(doc, txName))
            {
                RevitErrorRecorder said = null;
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("transaction group did not start");
                    using (var tx = new Transaction(doc, txName))
                    {
                        said = RevitErrorRecorder.On(tx);
                        tx.Start();
                        foreach (Move mv in moves) ElementTransformUtils.MoveElements(doc, new List<ElementId> { mv.El.Id }, mv.Vector);
                        Guard.Commit(tx, txName);
                    }
                    bool positions = true;
                    foreach (Move mv in moves)
                    {
                        JObject now = UndoCapture.State(doc, Rid.Value(mv.El.Id));
                        JToken expected = Shift(mv.BeforeState, mv.Vector);
                        bool ok = now != null && UndoRules.StatesMatch(expected?["loc"], now["loc"]);
                        postconditions.Record("position:" + Rid.Value(mv.El.Id), expected?["loc"], now?["loc"], ok);
                        positions &= ok;
                    }
                    after = Detect(doc, movedIds, region.Keys, out completeAfter);
                    bool? cleared = completeAfter ? (bool?)true : null;
                    foreach (Move mv in moves)
                    {
                        string key = ClashResolveRules.PairKey(Rid.Value(mv.El.Id), mv.FixedId);
                        bool gone = !after.Contains(key);
                        if (completeAfter) postconditions.Record("pair_cleared:" + mv.FindingId, "absent", gone ? "absent" : "present", gone);
                        else postconditions.Unreadable("pair_cleared:" + mv.FindingId, "absent", "re-detection incomplete");
                        if (!gone && cleared == true) cleared = false;
                    }
                    List<string> fresh = ClashResolveRules.NewPairs(before, after);
                    if (completeAfter && completeBefore) postconditions.Record("no_new_clash", new JArray(), new JArray(fresh), fresh.Count == 0);
                    else postconditions.Unreadable("no_new_clash", new JArray(), "detection before or after the move was incomplete");
                    keep = ClashResolveRules.Keep(positions, cleared, fresh.Count, completeBefore && completeAfter, out verdict);
                    if (!keep)
                    {
                        Guard.RollbackResult rb = Guard.RollBack(group);
                        return CommandResult.FailWithDetail("Rolled back, nothing kept: " + verdict + ".", new JObject
                        {
                            ["state"] = rb.Confirmed ? "rolled_back" : "uncertain", ["rollback_status"] = rb.StatusName,
                            ["new_clashes"] = new JArray(fresh), ["postconditions"] = postconditions.ToJson()
                        });
                    }
                    Guard.Assimilate(group, txName);
                }
                catch (Exception ex)
                {
                    string rb = PlanFailure.NotAttempted; bool attempted = false;
                    if (group.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(group).StatusName; }
                    return CommandResult.Fail("Resolve failed: " + ex.Message + (said == null ? "" : said.Said()) + " " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing was moved"));
                }
            }

            // Post-assimilation re-read: positions only (the solids were measured inside the group).
            foreach (Move mv in moves)
            {
                JObject now = UndoCapture.State(doc, Rid.Value(mv.El.Id));
                if (now == null || !UndoRules.StatesMatch(Shift(mv.BeforeState, mv.Vector)?["loc"], now["loc"]))
                    return CommandResult.FailWithDetail("The group was kept but element " + Rid.Value(mv.El.Id) + " does not re-read at its verified position; inspect the model.",
                        new JObject { ["state"] = "uncertain" });
            }

            var entries = moves.Select(mv => UndoCapture.Entry(doc, "move", new[] { Rid.Value(mv.El.Id) },
                new JObject { [Rid.Value(mv.El.Id).ToString(CultureInfo.InvariantCulture)] = mv.BeforeState },
                new JObject { ["vector"] = new JArray(mv.Vector.X, mv.Vector.Y, mv.Vector.Z) })).ToList();
            JObject undo = UndoCapture.Record(doc, Name, entries);

            // The ledger learns the MEASURED outcome - and only that.
            var resolved = new JArray(); string ledgerNote = null;
            try
            {
                string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                foreach (Move mv in moves)
                    if (ledger.TryGetValue(mv.FindingId, out CoordinationFinding f) &&
                        ClashResolveRules.ResolveMeasured(f, true, "solid re-detection after moving " + Rid.Value(mv.El.Id) +
                            " found the pair gone and no new clash (undo batch " + undo.Value<string>("batch_id") + ")", now))
                        resolved.Add(mv.FindingId);
                CoordinationLedger.Save(ledgerPath, ledgerTitle ?? doc.Title, ledger);
            }
            catch (Exception ex) { ledgerNote = "the model change is kept and verified, but the ledger could not be updated: " + ex.Message; }

            var applied = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["verdict"] = verdict, ["postconditions"] = postconditions.ToJson(),
                ["findings_resolved_by_model"] = resolved, ["ledger_note"] = ledgerNote, ["undo"] = undo,
                ["neighbourhood_elements"] = region.Count
            };
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed, moves.Count, moves.Count, moves.Count, 0, 0, 0);
            return CommandResult.Ok(applied);
        }

        private static JObject Shift(JObject state, XYZ v)
        {
            if (state == null || !(state["loc"] is JArray loc)) return null;
            var o = (JObject)state.DeepClone();
            o["loc"] = new JArray(loc.Select(p => (JToken)new JArray((double)p[0] + v.X, (double)p[1] + v.Y, (double)p[2] + v.Z)));
            return o;
        }

        private static string Parse(Document doc, JObject o, Dictionary<string, CoordinationFinding> ledger, double maxMove,
                                    HashSet<long> claimed, out Move mv)
        {
            mv = null;
            if (o == null) return "entry is not an object";
            string fid = o.Value<string>("finding_id");
            if (string.IsNullOrEmpty(fid) || !ledger.TryGetValue(fid, out CoordinationFinding f)) return "finding_id is not in this document's ledger";
            long raw = o.Value<long?>("element_id") ?? -1;
            if (!Rid.CanRepresent(raw)) return "element_id is invalid";
            Element e = doc.GetElement(Rid.Make(raw));
            if (e == null) return "element " + raw + " does not exist in the host";
            if (!claimed.Add(raw)) return "element " + raw + " appears twice; combine the moves deliberately";
            // The mover must be one side of THIS finding, in the host.
            ClashResolveRules.ParseSide(f.SideA, out bool hostA, out string uidA);
            ClashResolveRules.ParseSide(f.SideB, out bool hostB, out string uidB);
            string otherUid = (hostA && uidA == e.UniqueId) ? uidB : (hostB && uidB == e.UniqueId) ? uidA : null;
            bool otherHost = (hostA && uidA == e.UniqueId) ? hostB : hostA;
            if (otherUid == null) return "element " + raw + " is not a host side of finding " + fid;
            Element other = otherHost ? doc.GetElement(otherUid) : null;
            if (other == null) return "the other side of finding " + fid + " is not a host element; its clash cannot be re-measured here";
            string bic = null; try { bic = ((BuiltInCategory)Rid.Value(e.Category.Id)).ToString(); } catch { }
            if (ClashResolveRules.RoleOf(bic, false) != ClashResolveRules.RoleMovable) return "element " + raw + " is not a flexible MEP run; it is never moved automatically";
            if (!Validate(doc, e, out string _, out string why)) return why;
            JArray v = o["vector_mm"] as JArray;
            if (v == null || v.Count != 3) return "vector_mm must be [x,y,z]";
            var vec = new XYZ((double)v[0] / MmPerFoot, (double)v[1] / MmPerFoot, (double)v[2] / MmPerFoot);
            if (vec.GetLength() * MmPerFoot > maxMove) return "the move exceeds max_move_mm";
            if (vec.GetLength() < 1e-6) return "vector_mm is zero";
            mv = new Move { FindingId = fid, El = e, Vector = vec, FixedId = Rid.Value(other.Id) };
            return null;
        }

        /// <summary>
        /// Solid intersections between each mover and every element of the region, as
        /// canonical pair keys. `complete` is false if any solid read or boolean failed:
        /// an unmeasured pair is never taken as a clean one.
        /// </summary>
        private static List<string> Detect(Document doc, HashSet<long> movers, IEnumerable<long> region, out bool complete)
        {
            complete = true;
            var pairs = new List<string>();
            var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            var cache = new Dictionary<long, List<Solid>>();
            List<long> others = region.ToList();
            foreach (long m in movers)
            {
                List<Solid> sm = Solids(doc, m, options, cache);
                if (sm == null || sm.Count == 0) { complete = false; continue; }
                BoundingBoxXYZ mb = doc.GetElement(Rid.Make(m))?.get_BoundingBox(null);
                foreach (long o in others)
                {
                    if (o == m) continue;
                    Element oe = doc.GetElement(Rid.Make(o));
                    BoundingBoxXYZ ob = oe?.get_BoundingBox(null);
                    if (mb == null || ob == null || !Overlap(mb, ob)) continue;
                    List<Solid> so = Solids(doc, o, options, cache);
                    if (so == null) { complete = false; continue; }
                    bool hit = false;
                    foreach (Solid x in sm)
                        foreach (Solid y in so)
                        {
                            try
                            {
                                Solid i = BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect);
                                if (i != null && i.Volume > TinyVolume) hit = true;
                            }
                            catch { complete = false; }
                        }
                    if (hit) pairs.Add(ClashResolveRules.PairKey(m, o));
                }
            }
            return pairs;
        }

        private static bool Overlap(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
            a.Min.X <= b.Max.X && a.Max.X >= b.Min.X && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

        private static List<Solid> Solids(Document doc, long id, Options options, Dictionary<long, List<Solid>> cache)
        {
            if (cache.TryGetValue(id, out List<Solid> hit)) return hit;
            List<Solid> acc = new List<Solid>();
            try
            {
                GeometryElement g = doc.GetElement(Rid.Make(id))?.get_Geometry(options);
                if (g != null) Harvest(g, acc);
            }
            catch { acc = null; }
            cache[id] = acc;
            return acc;
        }

        private static void Harvest(GeometryObject go, List<Solid> acc)
        {
            if (go is Solid s) { if (s.Volume > 1e-9 && s.Faces.Size > 0) acc.Add(s); }
            else if (go is GeometryInstance gi) { var g = gi.GetInstanceGeometry(); if (g != null) foreach (var o in g) Harvest(o, acc); }
            else if (go is GeometryElement ge) foreach (var o in ge) Harvest(o, acc);
        }
    }
}
