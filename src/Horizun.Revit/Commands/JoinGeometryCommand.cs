// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_join_geometry: join, unjoin and switch the cut order of walls, floors,
// structural framing, columns and foundations, with the cut priority stated and
// the duplicated volume measured. Through ModelEditRunner: rehearse by default,
// single-use token, write in a TransactionGroup, re-read the committed model and
// ROLL BACK on any disagreement in atomic mode; per_pair isolates unreadable pairs.
//
//   read          per pair: joined, who cuts, each solid's volume and the volume the
//                 two solids share (the concrete counted twice). With element_ids the
//                 pairs are found among them: joined, or boxes touching AND sharing
//                 volume. Read-only.
//   join          joins each pair and sets who cuts: first_cuts (pair [cutting, cut]),
//                 priority (JoinGeometryRules) or keep_revit. Re-read: joined, the
//                 cutting element, and the shared volume after = 0 (within 1 cm3).
//   unjoin        re-read: not joined.
//   switch_order  re-read: joined, and the other element now cuts.
//   corners       read-only: open L/T wall corners within tolerance (JoinGeometryCommand.Corners).
//   fix_corners   allows the join and moves the open end to the crossing; re-read: joined to the partner.
//
// Refused by name before anything is written: an element that is not in this
// document, a type, a category this tool does not join, a member of a group, a
// nested family instance, an element with no readable solid (join), a pair whose
// boxes do not touch (Revit would join it with a "do not intersect" warning).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class JoinGeometryCommand : ICommand
    {
        public string Name => "horizun_join_geometry";
        public string Description => "Join, unjoin and switch the cut order of walls, floors, framing, columns and foundations with a stated priority; re-reads the join, who cuts and the shared volume after commit.";

        private const double CubicMetresPerCubicFoot = 0.028316846592;
        private const double TouchToleranceFeet = 1.0 / 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            // excavate / unexcavate (a toposolid cut by another element) take their own path with a real rehearsal.
            try
            {
                JObject request = string.IsNullOrWhiteSpace(paramsJson) ? null : Horizun.Revit.Core.JsonText.ParseObject(paramsJson);
                if (request != null && ToposolidExcavationRules.IsExcavation((request.Value<string>("operation") ?? "").Trim()))
                {
                    string invalid = JoinGeometryRules.Validate(request);
                    return invalid != null ? CommandResult.RefuseBeforeWrite(invalid, ErrorCodes.EsquemaInvalido) : ExecuteExcavation(app, request);
                }
            }
            catch (Newtonsoft.Json.JsonException) { /* the runner reports malformed JSON */ }
            return ModelEditRunner.Run(app, paramsJson, Name, "Horizun: join geometry", JoinGeometryRules.Validate, Plan);
        }

        private sealed class Member
        {
            public Element Element;
            public long Id;
            public string Class;
            public string Category;
            public double[] Box;
        }

        private sealed class Pair
        {
            public Member A, B;
            public bool JoinedBefore;
            public bool? ACutsBefore;
            public double? OverlapBeforeM3;
            public string OverlapBeforeError;
            public string UnverifiedReason;
            public bool RetainedUnverified;
            public int WantedCutter;
            public string Action;
        }

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            string op = (r.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (op == JoinGeometryRules.Corners || op == JoinGeometryRules.FixCorners) return PlanCorners(doc, r, scale, op);
            bool perPair = (string)r["verification_mode"] == "per_pair";
            var members = new Dictionary<long, Member>();
            // auto_priority: every joinable element of one level, its overlapping pairs found and joined by priority -
            // the same join, with the level's concrete volume before and after in the evidence.
            JObject auto = null;
            List<Pair> pairs;
            if (op == JoinGeometryRules.AutoPriority)
            {
                JArray levelIds = LevelMembers(doc, r.Value<long>("level_id"), out auto);
                pairs = Discover(doc, members, levelIds, op, JoinGeometryRules.MaxAutoPairs, perPair);
                op = JoinGeometryRules.Join;
            }
            else
                pairs = r["pairs"] is JArray given
                    ? given.Select(t => new Pair { A = Resolve(doc, members, ((JArray)t)[0].Value<long>()), B = Resolve(doc, members, ((JArray)t)[1].Value<long>()) }).ToList()
                    : Discover(doc, members, (JArray)r["element_ids"], op, JoinGeometryRules.MaxPairs, perPair);

            foreach (Pair p in pairs)
            {
                try { p.JoinedBefore = JoinGeometryUtils.AreElementsJoined(doc, p.A.Element, p.B.Element); }
                catch (Exception ex) { throw new ArgumentException("whether " + p.A.Id + " and " + p.B.Id + " are joined could not be read: " + ex.Message); }
                if (p.JoinedBefore) p.ACutsBefore = CutsOrNull(doc, p.A.Element, p.B.Element);
                if (op == JoinGeometryRules.Read || op == JoinGeometryRules.Join)
                    p.OverlapBeforeM3 = SharedVolumeM3(p.A.Element, p.B.Element, out p.OverlapBeforeError);
            }

            if (op == JoinGeometryRules.Read) return new ArchModelEdit { ReadResult = ReadResult(pairs, members.Values) };

            if (op == JoinGeometryRules.Join && !perPair && !JoinGeometryRules.SharedVolumeSum(pairs.Select(p => p.OverlapBeforeM3)).HasValue)
                throw new ArgumentException("The shared volume before joining could not be measured for every pair. Nothing was written.");

            var edit = new ArchModelEdit();
            foreach (Member m in members.Values.OrderBy(m => m.Id)) edit.Planned.Add(ModelEditRunner.Planned(m.Element, PlannedAction.Modify, r));
            List<string> priority = auto == null ? JoinGeometryRules.PriorityOf(r["priority"])
                : ((JArray)r["priority"]).Select(t => (string)t).ToList();
            if (auto != null)
            {
                string coverage = JoinGeometryRules.PriorityCoverageError(members.Values.Select(m => m.Class), priority);
                if (coverage != null) throw new ArgumentException(coverage);
            }
            string order = auto != null ? JoinGeometryRules.ByPriority
                         : r.Value<string>("cut_order") ?? (r["element_ids"] != null ? JoinGeometryRules.ByPriority : JoinGeometryRules.FirstCuts);

            foreach (Pair p in pairs)
            {
                if (op == JoinGeometryRules.Join)
                {
                    if (!p.JoinedBefore && !JoinGeometryRules.BoxesTouch(p.A.Box, p.B.Box, TouchToleranceFeet))
                        throw new ArgumentException("elements " + p.A.Id + " and " + p.B.Id + " do not touch: Revit would join them and warn that they do not intersect. Nothing was written.");
                    if (!p.JoinedBefore && (SolidsOf(p.A.Element).Count == 0 || SolidsOf(p.B.Element).Count == 0))
                        throw new ArgumentException("element " + (SolidsOf(p.A.Element).Count == 0 ? p.A.Id : p.B.Id) + " has no readable solid, so the join and its shared volume cannot be checked. Nothing was written.");
                    p.WantedCutter = order == JoinGeometryRules.FirstCuts ? 1
                                   : order == JoinGeometryRules.ByPriority ? JoinGeometryRules.Cutter(p.A.Class, p.B.Class, priority) : 0;
                    p.Action = JoinGeometryRules.JoinAction(p.JoinedBefore, p.ACutsBefore, p.WantedCutter);
                }
                else if (op == JoinGeometryRules.Unjoin) p.Action = p.JoinedBefore ? "unjoin" : "nothing";
                else
                {
                    if (!p.JoinedBefore)
                        throw new ArgumentException("elements " + p.A.Id + " and " + p.B.Id + " are not joined, so there is no cut order to switch. Join them first (operation=join). Nothing was written.");
                    if (p.ACutsBefore == null)
                        throw new ArgumentException("who cuts between " + p.A.Id + " and " + p.B.Id + " could not be read, so a switch could not be verified. Nothing was written.");
                    p.Action = "switch";
                }
            }

            edit.Summary["operation"] = auto != null ? JoinGeometryRules.AutoPriority : op;
            if (auto != null)
            {
                double? sharedBefore = SharedScopeVolumeM3(doc, members.Values);
                if (!sharedBefore.HasValue && !perPair)
                    throw new ArgumentException("The complete level's shared volume could not be measured. Nothing was written.");
                edit.Evidence["shared_volume_scope_before_m3"] = sharedBefore.HasValue ? (JToken)sharedBefore.Value : null;
                double? before = 0;
                foreach (Member m in members.Values) { double? v = VolumeM3(m.Element); before = before.HasValue && v.HasValue ? before + v : null; }
                auto["volume_of_members_before_m3"] = before.HasValue ? (JToken)Round(before.Value) : null;
                auto["pairs_found"] = pairs.Count;
                edit.Summary["auto_priority"] = auto;
                edit.Evidence["volume_of_members_before_m3"] = auto["volume_of_members_before_m3"];
            }
            if (op == JoinGeometryRules.Join) { edit.Summary["cut_order"] = order; if (order == JoinGeometryRules.ByPriority) edit.Summary["priority"] = new JArray(priority); }
            if (op == JoinGeometryRules.Join)
            {
                edit.Summary["verification_mode"] = perPair ? "per_pair" : "atomic";
                edit.Evidence["verification_mode"] = perPair ? "per_pair" : "atomic";
                if (edit.Evidence["volume_of_members_before_m3"] == null)
                    edit.Evidence["volume_of_members_before_m3"] = SumMemberVolumes(doc, members.Values);
            }
            edit.Summary["pairs"] = new JArray(pairs.Select(p => PairRow(p)));
            edit.Summary["pairs_to_change"] = pairs.Count(p => p.Action != "nothing");
            if (op == JoinGeometryRules.Join)
            {
                double? beforeSum = auto != null ? edit.Evidence.Value<double?>("shared_volume_scope_before_m3")
                    : JoinGeometryRules.SharedVolumeSum(pairs.Select(p => p.OverlapBeforeM3));
                edit.Summary["shared_volume_before_m3"] = beforeSum.HasValue ? (JToken)Round(beforeSum.Value) : null;
                edit.Summary["closure_tolerance_m3"] = JoinGeometryRules.OverlapToleranceM3;
                edit.Summary["shared_volume_method"] = "Sum of pairwise solid intersections (Sigma), not the union of intersections; triple overlaps contribute to each pair.";
                int ties = pairs.Count(p => order == JoinGeometryRules.ByPriority && p.WantedCutter == 0);
                if (ties > 0) edit.Summary["ties"] = ties + " pair(s) of the same class: the priority does not decide who cuts, so Revit's order is kept and reported.";
            }

            if (pairs.All(p => p.Action == "nothing"))
            {
                // A previously joined level can still contain residual overlap. Re-read it and expose
                // the closure finding even though no transaction is necessary.
                PostconditionCheck check = Verify(doc, op, pairs, members.Values, edit.Evidence, auto != null);
                edit.ReadResult = new JObject
                {
                    ["no_op"] = true, ["operation"] = auto != null ? JoinGeometryRules.AutoPriority : op,
                    ["pairs"] = edit.Evidence["pairs"],
                    ["plan"] = edit.Summary, ["postconditions"] = check.ToJson(), ["evidence"] = edit.Evidence,
                    ["closure_verified"] = op == JoinGeometryRules.Join && check.AllVerified,
                    ["note"] = "No pair needs a join-order change; the current model was re-read. Inspect postconditions for residual overlap or unreadable geometry. Nothing was written."
                };
                return edit;
            }

            PairProgress progress = null;
            edit.Apply = d =>
            {
                progress = new PairProgress(d, op, pairs);
                edit.Evidence["pair_progress_path"] = progress.Path;
                edit.Evidence["pair_progress_note"] = "Diagnostic checkpoints are flushed before native calls. Pair subtransactions isolate managed failures, not native process crashes. After a crash reopen the last saved model, re-read recorded pairs and rehearse again; pair_checked is not a saved-model checkpoint.";
                foreach (Pair p in pairs.Where(x => x.Action != "nothing"))
                {
                    progress.Record(p, "pair_pending");
                    if (perPair)
                    {
                        using (var tx = new SubTransaction(d))
                        {
                            if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("The pair subtransaction did not start.");
                            try
                            {
                                ApplyPair(d, op, p, progress);
                                progress.Record(p, "verification_pending");
                                d.Regenerate();
                                double? shared = SharedVolumeM3(d.GetElement(Rid.Make(p.A.Id)), d.GetElement(Rid.Make(p.B.Id)), out string cause);
                                string why = JoinGeometryRules.JoinedPairVerdict(JoinedOrNull(d, p) == true, p.WantedCutter,
                                    CutsOrNull(d, d.GetElement(Rid.Make(p.A.Id)), d.GetElement(Rid.Make(p.B.Id))), shared);
                                if (why != null) throw new InvalidOperationException(why + (cause == null ? "" : ": " + cause));
                                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Pair subtransaction did not commit.");
                                progress.Record(p, "pair_checked");
                            }
                            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                            catch (Exception ex)
                            {
                                if (tx.GetStatus() != TransactionStatus.Started || !Guard.RollBack(tx).Confirmed) throw;
                                p.UnverifiedReason = ex.Message;
                                progress.Record(p, "pair_rolled_back", ex.Message);
                            }
                        }
                        continue;
                    }
                    try
                    {
                        ApplyPair(d, op, p, progress);
                        progress.Record(p, "pair_applied_uncommitted");
                    }
                    catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("Revit refused to " + op.Replace('_', ' ') + " elements " + p.A.Id + " (" + p.A.Category + ") and " +
                            p.B.Id + " (" + p.B.Category + "): " + ex.Message);
                    }
                }
                progress.Record(null, "batch_applied_uncommitted");
            };

            edit.Verify = d =>
            {
                progress?.Record(null, "post_transaction_verification_pending");
                var measurements = new Dictionary<Pair, Tuple<double?, string>>();
                // A later pair can alter an earlier pair's geometry. Re-read all of them
                // again after the transaction, without making an unreadable boolean roll
                // back the other, measurable pairs in this explicitly independent mode.
                if (perPair)
                    foreach (Pair p in pairs.Where(p => p.UnverifiedReason == null))
                    {
                        progress?.Record(p, "post_transaction_pair_measurement_pending");
                        double? overlap = SharedVolumeM3(d.GetElement(Rid.Make(p.A.Id)), d.GetElement(Rid.Make(p.B.Id)), out string cause);
                        measurements[p] = Tuple.Create(overlap, cause);
                        string why = JoinGeometryRules.JoinedPairVerdict(JoinedOrNull(d, p) == true, p.WantedCutter,
                            CutsOrNull(d, d.GetElement(Rid.Make(p.A.Id)), d.GetElement(Rid.Make(p.B.Id))), overlap);
                        if (why != null) { p.UnverifiedReason = why + (cause == null ? "" : ": " + cause); p.RetainedUnverified = true; }
                    }
                var retained = pairs.Where(p => p.UnverifiedReason == null).ToList();
                PostconditionCheck check = Verify(d, op, retained, members.Values, edit.Evidence, auto != null && !perPair, perPair ? measurements : null);
                edit.Evidence["unverified"] = new JArray(pairs.Where(p => p.UnverifiedReason != null).Select(p => new JObject
                { ["a"] = p.A.Id, ["b"] = p.B.Id, ["state"] = "unverified", ["reason"] = p.UnverifiedReason, ["changes_applied"] = p.RetainedUnverified }));
                edit.Evidence["coverage_complete"] = retained.Count == pairs.Count;
                if (auto != null && perPair)
                {
                    progress?.Record(null, "scope_volume_measurement_pending");
                    double? scopeAfter = SharedScopeVolumeM3(d, members.Values);
                    edit.Evidence["shared_volume_scope_after_m3"] = scopeAfter.HasValue ? (JToken)Round(scopeAfter.Value) : null;
                    edit.Evidence["scope_closure_verified"] = JoinGeometryRules.SharedVolumeClosed(scopeAfter);
                    edit.Evidence["note"] = "per_pair retains only independently verified pairs; scope totals include skipped pairs and are reported separately.";
                }
                progress?.Record(null, "post_transaction_verification_returned");
                return check;
            };
            if (perPair)
            {
                edit.PartialFailures = () => pairs.Count(p => p.UnverifiedReason != null);
                edit.VerifiedItems = () => pairs.Count(p => p.UnverifiedReason == null);
            }
            return edit;
        }

        private static void ApplyPair(Document d, string op, Pair p, PairProgress progress)
        {
            int wanted = op == JoinGeometryRules.SwitchOrder ? (p.ACutsBefore == true ? -1 : 1) : p.WantedCutter;
            JoinGeometryRules.ApplyCurrentPair(op, wanted,
                () => CurrentPair(d, p, progress),
                action =>
                {
                    Element a = d.GetElement(Rid.Make(p.A.Id)), b = d.GetElement(Rid.Make(p.B.Id));
                    string invalid = CurrentMemberError(d, a) ?? CurrentMemberError(d, b);
                    if (invalid != null) throw new InvalidOperationException(invalid);
                    progress.Record(p, action + "_pending");
                    if (action == JoinGeometryRules.Unjoin) JoinGeometryUtils.UnjoinGeometry(d, a, b);
                    else if (action == JoinGeometryRules.SwitchOrder) JoinGeometryUtils.SwitchJoinOrder(d, a, b);
                    else JoinGeometryUtils.JoinGeometry(d, a, b);
                    progress.Record(p, action + "_returned");
                },
                () => { progress.Record(p, "regenerate_pending"); d.Regenerate(); progress.Record(p, "regenerate_returned"); });
        }

        private static string CurrentMemberError(Document d, Element e)
        {
            if (e == null || !e.IsValidObject) return "member_missing_or_invalid: a pair member was deleted or invalidated; pair skipped";
            if (!e.Document.Equals(d)) return "wrong_document: a pair member is no longer in the target document";
            if (e is ElementType) return "member_is_type: a pair member is no longer a placed element";
            if (e.GroupId != ElementId.InvalidElementId) return "group_member: a pair member belongs to a group";
            if (e is FamilyInstance fi && fi.SuperComponent != null) return "nested_member: a pair member is nested";
            string category = null;
            try { category = e.Category?.BuiltInCategory.ToString(); } catch { }
            if (JoinGeometryRules.ClassOf(category, Structural(e)) == null) return "category_not_joinable: a pair member changed category";
            return null;
        }

        private static JoinGeometryRules.CurrentPair CurrentPair(Document d, Pair p, PairProgress progress)
        {
            progress.Record(p, "resolve_current_pending");
            Element a = d.GetElement(Rid.Make(p.A.Id)), b = d.GetElement(Rid.Make(p.B.Id));
            string invalid = p.A.Id == p.B.Id ? "same_member: a pair cannot join an element to itself"
                : CurrentMemberError(d, a) ?? CurrentMemberError(d, b);
            if (invalid != null) return new JoinGeometryRules.CurrentPair { InvalidReason = invalid };
            progress.Record(p, "read_join_state_pending");
            bool joined = JoinGeometryUtils.AreElementsJoined(d, a, b);
            bool? cuts = null;
            if (joined)
            {
                progress.Record(p, "read_cut_order_pending");
                cuts = CutsOrNull(d, a, b);
            }
            // Existing joins can legitimately consume all of a member's volume.
            bool touching = joined || JoinGeometryRules.BoxesTouch(Box(a), Box(b), TouchToleranceFeet);
            bool solids = joined || (touching && SolidsOf(a).Count > 0 && SolidsOf(b).Count > 0);
            return new JoinGeometryRules.CurrentPair { Joined = joined, ACuts = cuts, Touching = touching, HasSolids = solids };
        }

        private static JToken SumMemberVolumes(Document doc, IEnumerable<Member> members)
        {
            double? sum = JoinGeometryRules.SharedVolumeSum(members.Select(m => VolumeM3(doc.GetElement(Rid.Make(m.Id)))));
            return sum.HasValue ? (JToken)sum.Value : JValue.CreateNull();
        }

        private static PostconditionCheck Verify(Document d, string op, List<Pair> pairs, IEnumerable<Member> members, JObject evidence, bool levelWide = false,
            Dictionary<Pair, Tuple<double?, string>> measurements = null)
        {
            var rows = new JArray();
            if (op == JoinGeometryRules.Unjoin)
            {
                var check = new PostconditionCheck("unjoined");
                int ok = 0, unread = 0;
                foreach (Pair p in pairs)
                {
                    bool? joined = JoinedOrNull(d, p);
                    if (joined == null) unread++; else if (!joined.Value) ok++;
                    rows.Add(new JObject { ["a"] = p.A.Id, ["b"] = p.B.Id, ["joined"] = joined });
                }
                if (unread > 0) check.Unreadable("unjoined", pairs.Count, unread + " pair(s) whose join state could not be read");
                else check.Compare("unjoined", pairs.Count, ok);
                evidence["pairs"] = rows;
                return check;
            }

            bool ordered = op == JoinGeometryRules.SwitchOrder || pairs.Any(p => p.WantedCutter != 0);
            var required = new List<string> { "joined" };
            if (ordered) required.Add("cut_order");
            if (op == JoinGeometryRules.Join) { required.Add("shared_volume"); required.Add("shared_volume_closure"); }
            var c = new PostconditionCheck(required.ToArray());
            int joinedOk = 0, joinedUnread = 0, orderAsked = 0, orderOk = 0, orderUnread = 0, volumeOk = 0, volumeUnread = 0;
            var afterReadings = new List<double?>();
            foreach (Pair p in pairs)
            {
                bool? joined = JoinedOrNull(d, p);
                bool? aCuts = joined == true ? CutsOrNull(d, d.GetElement(Rid.Make(p.A.Id)), d.GetElement(Rid.Make(p.B.Id))) : null;
                var row = new JObject { ["a"] = p.A.Id, ["b"] = p.B.Id, ["joined"] = joined, ["a_cuts"] = aCuts };
                if (joined == null) joinedUnread++; else if (joined.Value) joinedOk++;
                int wanted = op == JoinGeometryRules.SwitchOrder ? (p.ACutsBefore == true ? -1 : 1) : p.WantedCutter;
                if (wanted != 0)
                {
                    orderAsked++;
                    row["cutting_id"] = wanted > 0 ? p.A.Id : p.B.Id;
                    if (aCuts == null) orderUnread++; else if (aCuts.Value == wanted > 0) orderOk++;
                }
                if (op == JoinGeometryRules.Join)
                {
                    double? shared; string cause;
                    if (measurements != null && measurements.TryGetValue(p, out var measured)) { shared = measured.Item1; cause = measured.Item2; }
                    else shared = SharedVolumeM3(d.GetElement(Rid.Make(p.A.Id)), d.GetElement(Rid.Make(p.B.Id)), out cause);
                    afterReadings.Add(shared);
                    row["shared_volume_before_m3"] = p.OverlapBeforeM3.HasValue ? (JToken)Round(p.OverlapBeforeM3.Value) : null;
                    row["shared_volume_after_m3"] = shared.HasValue ? (JToken)Round(shared.Value) : null;
                    row["measurement_error"] = cause;
                    row["state"] = shared.HasValue ? "measured" : "unverified";
                    if (!JoinGeometryRules.SharedVolumeSum(new[] { shared }).HasValue) volumeUnread++;
                    else if (shared.Value <= JoinGeometryRules.OverlapToleranceM3) volumeOk++;
                    string why = JoinGeometryRules.JoinedPairVerdict(joined == true, wanted, aCuts, shared);
                    if (why != null) row["problem"] = why;
                }
                rows.Add(row);
            }
            if (joinedUnread > 0) c.Unreadable("joined", pairs.Count, joinedUnread + " pair(s) whose join state could not be read");
            else c.Compare("joined", pairs.Count, joinedOk);
            if (ordered)
            {
                if (orderUnread > 0) c.Unreadable("cut_order", orderAsked, orderUnread + " pair(s) where who cuts could not be read");
                else c.Compare("cut_order", orderAsked, orderOk);
            }
            if (op == JoinGeometryRules.Join)
            {
                if (volumeUnread > 0) c.Unreadable("shared_volume", pairs.Count, volumeUnread + " pair(s) whose shared volume could not be measured");
                else c.Compare("shared_volume", pairs.Count, volumeOk);
                double? before = levelWide ? evidence.Value<double?>("shared_volume_scope_before_m3")
                    : JoinGeometryRules.SharedVolumeSum(pairs.Select(p => p.OverlapBeforeM3));
                // Recompute the complete level, including pairs that were below the discovery threshold
                // or whose geometry changed because a third member's join order was switched.
                double? after = levelWide ? SharedScopeVolumeM3(d, members) : JoinGeometryRules.SharedVolumeSum(afterReadings);
                evidence["shared_volume_before_m3"] = before.HasValue ? (JToken)Round(before.Value) : null;
                evidence["shared_volume_after_m3"] = after.HasValue ? (JToken)Round(after.Value) : null;
                evidence["shared_volume_after_unrounded_m3"] = after.HasValue ? (JToken)after.Value : null;
                evidence["duplicated_volume_removed_m3"] = before.HasValue && after.HasValue ? (JToken)Round(before.Value - after.Value) : null;
                evidence["shared_volume_closure"] = after.HasValue ? (JToken)JoinGeometryRules.SharedVolumeClosed(after) : null;
                evidence["closure_tolerance_m3"] = JoinGeometryRules.OverlapToleranceM3;
                if (!after.HasValue) c.Unreadable("shared_volume_closure", 0, "The sum contains an unreadable pair intersection.");
                else c.Measure("shared_volume_closure", 0, after.Value, JoinGeometryRules.OverlapToleranceM3, "m3", "Sum of all pairwise solid intersections re-read after commit.");
                double? total = 0;
                foreach (Member m in members) { double? v = VolumeM3(d.GetElement(Rid.Make(m.Id))); total = total.HasValue && v.HasValue ? total + v : null; }
                evidence["volume_of_members_after_m3"] = total.HasValue ? (JToken)Round(total.Value) : null;
                // auto_priority: the level's concrete before and after - the duplicated volume a takeoff no longer counts
                double? reduction = JoinGeometryRules.MemberVolumeReduction(evidence.Value<double?>("volume_of_members_before_m3"), total);
                evidence["volume_reduction_m3"] = reduction.HasValue ? (JToken)Round(reduction.Value) : null;
            }
            evidence["pairs"] = rows;
            return c;
        }

        // ---- resolution ---------------------------------------------------------------

        private static Member Resolve(Document doc, Dictionary<long, Member> members, long id)
        {
            if (members.TryGetValue(id, out Member known)) return known;
            Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
            if (e == null) throw new ArgumentException("element " + id + " is not an element of this document. Nothing was written.");
            if (e is ElementType) throw new ArgumentException("element " + id + " is a type; joins are between placed elements. Nothing was written.");
            string token = null;
            try { token = e.Category?.BuiltInCategory.ToString(); } catch { }
            string cls = JoinGeometryRules.ClassOf(token, Structural(e));
            if (cls == null)
                throw new ArgumentException("element " + id + " is a " + (e.Category?.Name ?? e.GetType().Name) + "; this tool joins walls, floors, roofs, structural framing, structural columns and foundations. Nothing was written.");
            if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId)
                throw new ArgumentException("element " + id + " is a member of group " + Rid.Value(e.GroupId) + "; Revit does not keep a join made on one group instance. Nothing was written.");
            if (e is FamilyInstance fi && fi.SuperComponent != null)
                throw new ArgumentException("element " + id + " is nested in family instance " + Rid.Value(fi.SuperComponent.Id) + " and cannot be joined on its own. Nothing was written.");
            var m = new Member { Element = e, Id = id, Class = cls, Category = e.Category?.Name, Box = Box(e) };
            members[id] = m;
            return m;
        }

        /// <summary>
        /// The joinable elements of one level (walls by base constraint, framing by reference level, columns by base
        /// level, the rest by LevelId), skipping - and naming - grouped and nested ones instead of refusing the call.
        /// </summary>
        private static JArray LevelMembers(Document doc, long levelId, out JObject info)
        {
            Level level = Rid.CanRepresent(levelId) ? doc.GetElement(Rid.Make(levelId)) as Level : null;
            if (level == null) throw new ArgumentException("level_id " + levelId + " is not a level of this document. Nothing was written.");
            var cats = JoinGeometryRules.Categories.Select(c => (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), c)).ToList();
            var ids = new JArray();
            int grouped = 0, nested = 0;
            foreach (Element e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType())
            {
                if (LevelOf(e) != level.Id) continue;
                if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId) { grouped++; continue; }
                if (e is FamilyInstance fi && fi.SuperComponent != null) { nested++; continue; }
                ids.Add(Rid.Value(e.Id));
            }
            if (ids.Count > JoinGeometryRules.MaxAutoElements)
                throw new ArgumentException("level '" + level.Name + "' has " + ids.Count + " joinable elements, above " + JoinGeometryRules.MaxAutoElements +
                                            "; send element_ids by zone with operation=join cut_order=priority. Nothing was written.");
            info = new JObject
            {
                ["level_id"] = levelId, ["level"] = level.Name, ["members"] = ids.Count,
                ["skipped_in_groups"] = grouped, ["skipped_nested"] = nested,
                ["note"] = "Elements of this level only (walls by base constraint, framing by reference level, columns by base level); a column or wall " +
                           "based on the level below is not a member, so its joins with this level are not made."
            };
            return new JArray(ids.Values<long>().OrderBy(id => id));
        }

        private static ElementId LevelOf(Element e)
        {
            BuiltInParameter[] keys = e is Wall ? new[] { BuiltInParameter.WALL_BASE_CONSTRAINT }
                : e is FamilyInstance ? new[] { BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.FAMILY_LEVEL_PARAM }
                : new BuiltInParameter[0];
            foreach (BuiltInParameter k in keys)
                try { ElementId id = e.get_Parameter(k)?.AsElementId(); if (id != null && id != ElementId.InvalidElementId) return id; } catch { }
            return e.LevelId;
        }

        private static List<Pair> Discover(Document doc, Dictionary<long, Member> members, JArray ids, string op, int maxPairs, bool perPair = false)
        {
            List<Member> list = ids.Select(t => Resolve(doc, members, t.Value<long>())).ToList();
            var found = new List<Pair>();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    Member a = list[i], b = list[j];
                    bool joined = false;
                    try { joined = JoinGeometryUtils.AreElementsJoined(doc, a.Element, b.Element); }
                    catch
                    {
                        if (op == JoinGeometryRules.AutoPriority)
                            throw new ArgumentException("Join state could not be read for elements " + a.Id + " and " + b.Id + ". Nothing was written.");
                    }
                    if (!joined)
                    {
                        if (op == JoinGeometryRules.AutoPriority && (a.Box == null || b.Box == null))
                            throw new ArgumentException("Pair discovery cannot be complete: a bounding box is unreadable. Nothing was written.");
                        if (!JoinGeometryRules.BoxesTouch(a.Box, b.Box, TouchToleranceFeet)) continue;
                        double? shared = SharedVolumeM3(a.Element, b.Element);
                        if (!JoinGeometryRules.SharedVolumeSum(new[] { shared }).HasValue)
                        {
                            if (op != JoinGeometryRules.Read && !perPair)
                                throw new ArgumentException("Shared volume could not be measured for candidate pair " + a.Id + "/" + b.Id + ". Nothing was written.");
                            // Preserve the unreadable candidate in read output; skipping it would
                            // publish coverage_complete=true for an incomplete geometry traversal.
                        }
                        else if (shared.Value <= JoinGeometryRules.OverlapToleranceM3) continue;
                    }
                    found.Add(new Pair { A = a, B = b });
                    if (found.Count > maxPairs)
                        throw new ArgumentException("the elements yield more than " + maxPairs + " pairs; narrow the set (one level, one zone). Nothing was written.");
                }
            return found;
        }

        private static bool? Structural(Element e)
        {
            try
            {
                Parameter p = e is Wall ? e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)
                            : e is Floor ? e.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL) : null;
                return p == null ? (bool?)null : p.AsInteger() == 1;
            }
            catch { return null; }
        }

        // ---- readings -----------------------------------------------------------------

        private static double? SharedScopeVolumeM3(Document doc, IEnumerable<Member> members)
        {
            var current = members.Select(m => doc.GetElement(Rid.Make(m.Id))).ToList();
            var boxes = current.Select(Box).ToList();
            var readings = new List<double?>();
            for (int i = 0; i < current.Count; i++)
                for (int j = i + 1; j < current.Count; j++)
                {
                    if (current[i] == null || current[j] == null || boxes[i] == null || boxes[j] == null) return null;
                    if (JoinGeometryRules.BoxesTouch(boxes[i], boxes[j], TouchToleranceFeet))
                        readings.Add(SharedVolumeM3(current[i], current[j]));
                }
            return JoinGeometryRules.SharedVolumeSum(readings);
        }

        private static bool? JoinedOrNull(Document d, Pair p)
        {
            try { return JoinGeometryUtils.AreElementsJoined(d, d.GetElement(Rid.Make(p.A.Id)), d.GetElement(Rid.Make(p.B.Id))); }
            catch { return null; }
        }

        private static bool? CutsOrNull(Document d, Element a, Element b)
        {
            try
            {
                if (a == null || b == null || !a.IsValidObject || !b.IsValidObject ||
                    !JoinGeometryUtils.AreElementsJoined(d, a, b)) return null;
                return JoinGeometryUtils.IsCuttingElementInJoin(d, a, b);
            }
            catch { return null; }
        }

        private static double[] Box(Element e)
        {
            try
            {
                BoundingBoxXYZ b = e.get_BoundingBox(null);
                return b == null ? null : new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z };
            }
            catch { return null; }
        }

        internal static List<Solid> SolidsOf(Element e)
        {
            if (e == null) return new List<Solid>();
            return JoinGeometryRules.CollectComplete<Solid>(found =>
            {
                GeometryElement ge = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false });
                Collect(ge, found);
            });
        }

        private static void Collect(GeometryElement ge, List<Solid> into)
        {
            if (ge == null) throw new InvalidOperationException("A geometry branch is unreadable.");
            foreach (GeometryObject o in ge)
            {
                if (o == null) throw new InvalidOperationException("A geometry object is unreadable.");
                if (o is Solid s)
                {
                    double volume = s.Volume;
                    if (double.IsNaN(volume) || double.IsInfinity(volume) || volume < 0)
                        throw new InvalidOperationException("A solid's volume is unreadable or invalid.");
                    if (volume > 0)
                    {
                        if (s.Faces == null || s.Faces.Size == 0) throw new InvalidOperationException("A solid's faces are unreadable.");
                        into.Add(s);
                    }
                }
                else if (o is GeometryInstance gi) Collect(gi.GetInstanceGeometry(), into);
                else if (o is Mesh) throw new InvalidOperationException("Mesh geometry cannot provide an exact solid-volume measurement.");
            }
        }

        private static double? VolumeM3(Element e)
        {
            try
            {
                List<Solid> s = MeasuredSolids(e);
                double? volume = JoinGeometryRules.SharedVolumeSum(s.Select(x => (double?)x.Volume));
                return !volume.HasValue ? (double?)null : volume.Value * CubicMetresPerCubicFoot;
            }
            catch { return null; }
        }

        private static List<Solid> MeasuredSolids(Element e)
        {
            if (e == null) throw new InvalidOperationException("The element is missing.");
            var solids = new List<Solid>();
            Collect(e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false }), solids);
            return solids;
        }

        /// <summary>The volume both elements' solids occupy, in m3; null when an intersection could not be computed.</summary>
        internal static double? SharedVolumeM3(Element a, Element b) => SharedVolumeM3(a, b, out _);

        internal static double? SharedVolumeM3(Element a, Element b, out string cause)
        {
            cause = null;
            try
            {
                // A completely cut member may have no positive-volume solids. A successful
                // traversal of that geometry is empty, unlike an unreadable geometry branch.
                List<Solid> sa = MeasuredSolids(a), sb = MeasuredSolids(b);
                var readings = new List<double?>();
                foreach (Solid x in sa)
                    foreach (Solid y in sb)
                    {
                        if (!ClashBooleanRetry.Try(
                                () => BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect),
                                () => BooleanOperationsUtils.ExecuteBooleanOperation(y, x, BooleanOperationsType.Intersect),
                                out Solid inter, out bool recovered, out string error))
                        { cause = error; return null; }
                        readings.Add(JoinGeometryRules.IntersectionVolume(true, inter?.Volume));
                    }
                double? total = JoinGeometryRules.SharedVolumeSum(readings);
                return total.HasValue ? (double?)(total.Value * CubicMetresPerCubicFoot) : null;
            }
            catch (Exception ex) { cause = ex.Message; return null; }
        }

        // ---- replies ------------------------------------------------------------------

        private static JObject PairRow(Pair p) => new JObject
        {
            ["a"] = p.A.Id, ["a_class"] = p.A.Class, ["b"] = p.B.Id, ["b_class"] = p.B.Class,
            ["joined"] = p.JoinedBefore, ["a_cuts"] = p.ACutsBefore,
            ["shared_volume_m3"] = p.OverlapBeforeM3.HasValue ? (JToken)Round(p.OverlapBeforeM3.Value) : null,
            ["measurement_error"] = p.OverlapBeforeError,
            ["action"] = p.Action,
            ["cutting_id"] = p.WantedCutter == 0 ? null : (JToken)(p.WantedCutter > 0 ? p.A.Id : p.B.Id)
        };

        private static JObject ReadResult(List<Pair> pairs, IEnumerable<Member> members)
        {
            var unreadable = new JArray(pairs.Where(p => p.OverlapBeforeM3 == null)
                .Select(p => new JObject { ["a"] = p.A.Id, ["b"] = p.B.Id, ["state"] = "unverified", ["reason"] = p.OverlapBeforeError ?? "shared volume could not be measured" }));
            return new JObject
            {
                ["read_only"] = true,
                ["pairs"] = new JArray(pairs.Select(p => PairRow(p))),
                ["members"] = new JArray(members.OrderBy(m => m.Id).Select(m => new JObject
                {
                    ["element_id"] = m.Id, ["class"] = m.Class, ["category"] = m.Category,
                    ["volume_m3"] = VolumeM3(m.Element) is double v ? (JToken)Round(v) : null
                })),
                ["shared_volume_total_m3"] = JoinGeometryRules.SharedVolumeSum(pairs.Select(p => p.OverlapBeforeM3)) is double sum ? (JToken)Round(sum) : null,
                ["unreadable"] = unreadable,
                ["coverage_complete"] = unreadable.Count == 0,
                ["note"] = "shared_volume_m3 is concrete counted twice while the pair is not joined; a joined pair reads (near) 0. Volumes are m3."
            };
        }

        private static double Round(double v) => Math.Round(v, 6);
    }
}
