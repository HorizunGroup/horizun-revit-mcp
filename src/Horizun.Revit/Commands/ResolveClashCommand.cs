// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_resolve_clash - RESOLVE a recorded clash, with a verification.
//
//   propose  READ-ONLY. For findings of the horizun_clash ledger, proposes a
//            conservative correction (Core/ClashResolveRules.cs): move the flexible
//            MEP run the minimum distance plus clearance, as a perpendicular shift
//            or an elevation offset - and, if the run is CONNECTED, its whole
//            eligible network moves as one rigid body instead (run_shift; ineligible
//            networks stay report-only, naming the blocking element). Third-element
//            contact is checked against the host AND every loaded Revit link
//            (box-only prediction; apply re-measures on solids). Structure/
//            architecture, elements in a link, pinned runs and moves that would touch
//            a third element are REPORTED, never auto-resolved. Each proposal carries
//            the typed action and a verifiable prediction.
//   apply    dry_run (default) -> token -> commit inside a TransactionGroup, then
//            RE-DETECTS on solids over the affected neighbourhood, HOST AND LINKS:
//            every targeted pair must be gone and no pair may appear that was not
//            there before the move. A run_shift also re-reads every member's
//            position and every internal connection. Anything else rolls the WHOLE
//            group back and says why. Kept work is recorded for horizun_undo, and
//            the finding becomes resolved_by_model only from that measurement.
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
    public sealed partial class ResolveClashCommand : ICommand
    {
        public string Name => "horizun_resolve_clash";
        public string Description => "Propose and apply verified clash resolutions for findings of the horizun_clash ledger.";
        private const double MmPerFoot = 304.8;
        private const double TinyVolume = 1e-6;   // ft3

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            string op = request.Value<string>("operation") ?? "propose";
            double clearance = request.Value<double?>("clearance_mm") ?? 50;
            double maxMove = request.Value<double?>("max_move_mm") ?? 600;
            if (clearance < 0 || clearance > 500) return CommandResult.Fail("clearance_mm must be 0..500.");
            if (maxMove <= 0 || maxMove > 5000) return CommandResult.Fail("max_move_mm must be in (0, 5000].");
            if (op == "propose") return Propose(app, request, clearance, maxMove);
            if (op == "apply") return Apply(app, request, clearance, maxMove);
            // Sleeves/openings (ResolveClashSleeves.cs): for a finding a move cannot resolve -
            // one side an MEP run, the other a wall/floor/roof/framing/column - propose an
            // opening or sleeve at the crossing instead of relocating anything.
            if (op == "propose_opening") return ProposeOpening(app, request, clearance);
            if (op == "apply_opening") return ApplyOpening(app, request, clearance);
            return CommandResult.Fail("operation must be propose, apply, propose_opening or apply_opening.");
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
            if (!ResolveMoveScope(doc, m, out List<long> memberIds, out string mode, out List<Tuple<long, long>> internalEdges, out code, out reason)) return null;
            List<Element> moving = memberIds.Select(id => doc.GetElement(Rid.Make(id))).Where(e => e != null).ToList();
            var excludeIds = new HashSet<long>(memberIds) { Rid.Value(other.Id) };
            ResolveRun run = Run(m);
            if (run == null) { code = ClashResolveRules.CodeNoGeometry; reason = "the run has no straight centreline or readable section"; return null; }
            ResolveBox fixedBox = other != null ? Box(other.get_BoundingBox(null)) : null;
            if (fixedBox == null) { code = ClashResolveRules.CodeNoGeometry; reason = "the fixed side has no bounding box in the host"; return null; }
            List<ResolveCandidate> candidates = ClashResolveRules.Candidates(run, fixedBox, clearance, out string geomCode);
            var linksSkipped = new List<string>();
            List<LinkContext> links = ResolveLoadedLinks(doc, linksSkipped);
            var considered = new JArray();
            foreach (ResolveCandidate c in candidates)
            {
                var cr = new JObject { ["kind"] = c.Kind, ["distance_mm"] = c.DistanceMm, ["vector_mm"] = new JArray(c.VectorMm) };
                considered.Add(cr);
                if (c.DistanceMm > maxMove) { cr["rejected"] = ClashResolveRules.CodeTooFar; continue; }
                List<long> contacts = PredictedContacts(doc, moving, excludeIds, c.VectorMm, clearance);
                List<string> linkContacts = PredictedLinkContacts(moving, links, c.VectorMm);
                if (contacts.Count > 0 || linkContacts.Count > 0)
                {
                    cr["rejected"] = "would_touch_other_elements";
                    if (contacts.Count > 0) cr["contacts"] = new JArray(contacts);
                    if (linkContacts.Count > 0) cr["link_contacts"] = new JArray(linkContacts);
                    continue;
                }
                row["candidates"] = considered;
                if (linksSkipped.Count > 0) row["links_skipped"] = new JArray(linksSkipped);
                row["kind"] = c.Kind; row["distance_mm"] = c.DistanceMm; row["mode"] = mode;
                row["affected_elements"] = new JArray(memberIds);
                row["prediction"] = "after apply, the pair " + Rid.Value(m.Id) + "-" + Rid.Value(other.Id) +
                    " does not intersect (box clearance >= " + clearance + " mm) and no new clash appears with the elements " +
                    "or loaded links around the moved " + (mode == ClashResolveRules.ModeRunShift ? "network (" + memberIds.Count + " elements, moved rigidly, internal connections re-verified)" : "run") +
                    " - verified by solid re-detection at apply, or rolled back.";
                if (geomCode != null) row["note"] = geomCode;
                var action = new JObject { ["finding_id"] = f.Id, ["element_id"] = Rid.Value(m.Id), ["vector_mm"] = new JArray(c.VectorMm), ["kind"] = c.Kind };
                if (mode == ClashResolveRules.ModeRunShift) { action["mode"] = mode; action["network_ids"] = new JArray(memberIds); }
                return action;
            }
            row["candidates"] = considered;
            if (linksSkipped.Count > 0) row["links_skipped"] = new JArray(linksSkipped);
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

        /// <summary>
        /// What moves for `m`: itself alone when it has no connection, or its whole
        /// connected network when every member and boundary qualifies (run_shift) - re-derived
        /// fresh from the LIVE model every time (propose and apply alike), never trusted from a
        /// prior call. `memberIds` always includes `m` itself and is never null.
        /// </summary>
        private static bool ResolveMoveScope(Document doc, Element m, out List<long> memberIds, out string mode,
                                             out List<Tuple<long, long>> internalEdges, out string code, out string reason)
        {
            memberIds = new List<long> { Rid.Value(m.Id) }; mode = ClashResolveRules.ModeSingle;
            internalEdges = new List<Tuple<long, long>>(); code = null; reason = null;
            bool pinned; try { pinned = m.Pinned; } catch { pinned = true; }
            if (pinned) { code = ClashResolveRules.CodePinned; reason = "the run is pinned; unpin it deliberately first"; return false; }
            if (!IsConnected(m)) return true;
            var blocks = new List<ClashResolveRules.BoundaryBlock>();
            var edges = new List<Tuple<long, long>>();
            List<long> ids = ClashResolveRules.CollectNetwork(Rid.Value(m.Id),
                id => NetworkNeighbours(doc, id, blocks, edges), ClashResolveRules.MaxNetworkMembers, out bool truncated);
            List<ClashResolveRules.NetworkMemberFacts> facts = MemberFacts(doc, ids);
            if (!ClashResolveRules.EligibleForRunShift(facts, blocks, truncated, out code, out reason)) return false;
            memberIds = ids; mode = ClashResolveRules.ModeRunShift; internalEdges = edges.Distinct().ToList();
            return true;
        }

        private static bool IsConnected(Element m)
        {
            foreach (Connector c in MepFacts.Ordered(MepFacts.ManagerOf(m)))
            {
                bool connected; try { connected = c.IsConnected; } catch { connected = true; }
                if (connected) return true;
            }
            return false;
        }

        /// <summary>
        /// Live connectors out of `id`, restricted to run/fitting/accessory categories
        /// (<see cref="ClashResolveRules.IsNetworkMember"/>): the walk follows those, and
        /// records every OTHER connected owner (equipment, fixtures, terminals...) as a
        /// <see cref="ClashResolveRules.BoundaryBlock"/> instead. Every followed connection is
        /// also recorded as an internal edge (member id, member id), for the post-move
        /// "connections still connected" re-check.
        /// </summary>
        private static IEnumerable<long> NetworkNeighbours(Document doc, long id, List<ClashResolveRules.BoundaryBlock> blocks,
                                                            List<Tuple<long, long>> edges)
        {
            var result = new List<long>();
            Element e = doc.GetElement(Rid.Make(id));
            if (e == null) return result;
            foreach (Connector c in MepFacts.Ordered(MepFacts.ManagerOf(e)))
            {
                bool connected; try { connected = c.IsConnected; } catch { connected = false; }
                if (!connected) continue;
                foreach (Connector r in c.AllRefs.OfType<Connector>())
                {
                    Element owner = r.Owner;
                    if (owner == null) continue;
                    long oid = Rid.Value(owner.Id);
                    if (oid == id) continue;
                    if (ClashResolveRules.IsNetworkMember(CategoryBic(owner)))
                    {
                        result.Add(oid);
                        edges.Add(Tuple.Create(Math.Min(id, oid), Math.Max(id, oid)));
                    }
                    else blocks.Add(new ClashResolveRules.BoundaryBlock { OwnerId = id, BlockedByDescription = Describe(owner) });
                }
            }
            return result.Distinct();
        }

        private static List<ClashResolveRules.NetworkMemberFacts> MemberFacts(Document doc, List<long> ids)
        {
            var list = new List<ClashResolveRules.NetworkMemberFacts>();
            foreach (long id in ids)
            {
                Element e = doc.GetElement(Rid.Make(id));
                bool pinned; try { pinned = e == null || e.Pinned; } catch { pinned = true; }
                bool inGroup; try { inGroup = e != null && e.GroupId != ElementId.InvalidElementId; } catch { inGroup = false; }
                list.Add(new ClashResolveRules.NetworkMemberFacts { Id = id, Host = e != null, Pinned = pinned, InGroup = inGroup });
            }
            return list;
        }

        private static string CategoryBic(Element e)
        {
            try { return ((BuiltInCategory)Rid.Value(e.Category.Id)).ToString(); } catch { return null; }
        }

        private static string Describe(Element e)
        {
            return (CategoryBic(e) ?? "an unknown category") + " " + Rid.Value(e.Id);
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

        /// <summary>
        /// Host model elements whose box any of the moving elements' boxes would reach, after
        /// the shift (`excludeIds` = every moving element plus the fixed side). A single move
        /// passes a one-element `moving`; a run_shift passes the whole network, so a fitting
        /// far from the clash that would hit something is caught too - not just the mover.
        /// </summary>
        private static List<long> PredictedContacts(Document doc, List<Element> moving, HashSet<long> excludeIds, double[] vectorMm, double clearance)
        {
            var result = new List<long>();
            var seen = new HashSet<long>();
            foreach (Element m in moving)
            {
                BoundingBoxXYZ bb = m.get_BoundingBox(null);
                if (bb == null) continue;
                var moved = new ResolveBox(
                    bb.Min.X * MmPerFoot + vectorMm[0], bb.Min.Y * MmPerFoot + vectorMm[1], bb.Min.Z * MmPerFoot + vectorMm[2],
                    bb.Max.X * MmPerFoot + vectorMm[0], bb.Max.Y * MmPerFoot + vectorMm[1], bb.Max.Z * MmPerFoot + vectorMm[2]);
                foreach (Element e in Neighbours(doc, moved, 0))
                {
                    long eid = Rid.Value(e.Id);
                    if (excludeIds.Contains(eid) || !seen.Add(eid)) continue;
                    ResolveBox other = Box(e.get_BoundingBox(null));
                    if (other != null && ClashResolveRules.BoxesOverlap(moved, other, 0)) result.Add(eid);
                }
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
                .Where(e => !(e is RevitLinkInstance) && SpatialCoherence.IsPhysical(e))
                .ToList();
        }

        // ---- loaded links (propose's box prediction AND apply's solid re-detection) ------
        // A clash a move creates against a LINKED element is exactly as real as one against a
        // host element - it is just split across files, which is how real projects are built
        // (SpatialCoherence.AgainstLinks judges the same way). Each host box/solid is carried
        // into the link's own coordinates (the link instance's total transform, inverted); the
        // basis vectors are unitless direction cosines so they need no unit conversion, only
        // the origin does (ClashResolveRules.TransformBox is the Revit-free half of this).

        private sealed class LinkContext
        {
            public string Name;
            public Document Doc;
            public double[] BasisX, BasisY, BasisZ, OriginMm;
        }

        /// <summary>Every loaded link, ready to carry a host box/solid into its coordinates. Unloaded links go to <paramref name="skipped"/>, never silently dropped.</summary>
        private static List<LinkContext> ResolveLoadedLinks(Document doc, List<string> skipped)
        {
            var list = new List<LinkContext>();
            List<RevitLinkInstance> links;
            try { links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList(); }
            catch { return list; }
            foreach (RevitLinkInstance link in links)
            {
                string name = LinkName(link);
                Document linked; try { linked = link.GetLinkDocument(); } catch { linked = null; }
                if (linked == null) { skipped.Add(name); continue; }
                Transform toLink; try { toLink = link.GetTotalTransform().Inverse; } catch { skipped.Add(name + " (no transform)"); continue; }
                list.Add(new LinkContext
                {
                    Name = name, Doc = linked,
                    BasisX = new[] { toLink.BasisX.X, toLink.BasisX.Y, toLink.BasisX.Z },
                    BasisY = new[] { toLink.BasisY.X, toLink.BasisY.Y, toLink.BasisY.Z },
                    BasisZ = new[] { toLink.BasisZ.X, toLink.BasisZ.Y, toLink.BasisZ.Z },
                    OriginMm = new[] { toLink.Origin.X * MmPerFoot, toLink.Origin.Y * MmPerFoot, toLink.Origin.Z * MmPerFoot }
                });
            }
            return list;
        }

        private static string LinkName(Element link)
        {
            try { return link.Name; } catch { return "link " + Rid.Value(link.Id); }
        }

        /// <summary>Linked elements whose box any of the moving elements' boxes would reach, after the shift - conservative (box-only); apply re-measures on solids.</summary>
        private static List<string> PredictedLinkContacts(List<Element> moving, List<LinkContext> links, double[] vectorMm)
        {
            var result = new List<string>();
            if (links == null || links.Count == 0) return result;
            foreach (Element m in moving)
            {
                BoundingBoxXYZ bb = m.get_BoundingBox(null);
                if (bb == null) continue;
                var hostBox = new ResolveBox(
                    bb.Min.X * MmPerFoot + vectorMm[0], bb.Min.Y * MmPerFoot + vectorMm[1], bb.Min.Z * MmPerFoot + vectorMm[2],
                    bb.Max.X * MmPerFoot + vectorMm[0], bb.Max.Y * MmPerFoot + vectorMm[1], bb.Max.Z * MmPerFoot + vectorMm[2]);
                foreach (LinkContext link in links)
                {
                    ResolveBox inLink = ClashResolveRules.TransformBox(hostBox, link.BasisX, link.BasisY, link.BasisZ, link.OriginMm);
                    var outline = new Outline(
                        new XYZ(inLink.MinX / MmPerFoot, inLink.MinY / MmPerFoot, inLink.MinZ / MmPerFoot),
                        new XYZ(inLink.MaxX / MmPerFoot, inLink.MaxY / MmPerFoot, inLink.MaxZ / MmPerFoot));
                    List<Element> hits;
                    try
                    {
                        hits = new FilteredElementCollector(link.Doc).WhereElementIsNotElementType()
                            .WherePasses(new BoundingBoxIntersectsFilter(outline))
                            .Where(e => SpatialCoherence.IsPhysical(e)).ToList();
                    }
                    catch { continue; }
                    foreach (Element e in hits)
                    {
                        ResolveBox eb = Box(e.get_BoundingBox(null));
                        if (eb != null && ClashResolveRules.BoxesOverlap(inLink, eb, 0))
                            result.Add(link.Name + ":" + Rid.Value(e.Id));
                    }
                }
            }
            return result.Distinct().ToList();
        }

        // ---- apply -----------------------------------------------------------------

        private sealed class Move
        {
            public string FindingId; public Element El; public XYZ Vector; public long FixedId;
            /// <summary>Every element that moves with this Move - always includes El.Id; a single-element move is a list of one.</summary>
            public List<long> MemberIds;
            public string Mode;
            /// <summary>Member-to-member connector pairs re-verified after the move (run_shift only).</summary>
            public List<Tuple<long, long>> InternalEdges;
            public Dictionary<long, JObject> MemberBefore;
        }

        /// <summary>The resolved plan: each moving member, its finding, mode and vector, and its location now.</summary>
        private ResolvedPlan ResolvedMoves(UIApplication app, Document doc, GateResult gate, List<Move> moves)
        {
            var plan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (Move mv in moves)
                foreach (long id in mv.MemberIds)
                {
                    Element member = doc.GetElement(Rid.Make(id));
                    var row = new PlannedElement
                    {
                        UniqueId = member?.UniqueId ?? ("missing:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Category = member?.Category?.Name,
                        Action = PlannedAction.Modify,
                        BeforeValues = new Dictionary<string, string>
                        {
                            { "finding_id", mv.FindingId ?? "" },
                            { "mode", mv.Mode ?? "" },
                            { "vector_mm", string.Join(",", new[] { mv.Vector.X, mv.Vector.Y, mv.Vector.Z }
                                .Select(v => Math.Round(v * MmPerFoot, 1).ToString(System.Globalization.CultureInfo.InvariantCulture))) }
                        }
                    };
                    row.BeforeValues["loc"] = UndoCapture.State(doc, id)?["loc"]?.ToString(Formatting.None) ?? "";
                    plan.Elements.Add(row);
                }
            return plan;
        }

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
            // Two different findings' networks must never share a member: moving one would
            // silently double-move (or half-move) the other's network. The whole batch is
            // refused rather than guessing which proposal wins - the same "ask, do not assume"
            // rule this bridge holds everywhere else.
            var ownerOfMember = new Dictionary<long, int>();
            for (int i = 0; i < moves.Count; i++)
                foreach (long id in moves[i].MemberIds)
                {
                    if (ownerOfMember.TryGetValue(id, out int prior) && prior != i)
                        errors.Add(new JObject { ["index"] = i, ["error"] = "element " + id + " is also part of the connected network moved by proposal " + prior + "; combine them into one proposal" });
                    else ownerOfMember[id] = i;
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
                        ["mode"] = mv.Mode, ["network_ids"] = mv.Mode == ClashResolveRules.ModeRunShift ? new JArray(mv.MemberIds) : (JToken)JValue.CreateNull(),
                        ["vector_mm"] = new JArray(mv.Vector.X * MmPerFoot, mv.Vector.Y * MmPerFoot, mv.Vector.Z * MmPerFoot)
                    })),
                    ["note"] = "Nothing was moved. The apply keeps the group only if solid re-detection shows every targeted pair gone and no new clash."
                };
                ApplicationOutcome.StampRehearsal(result, input.Count, errors.Count, 0, 0);
                if (errors.Count == 0) DocumentGate.RecordResolvedPlan(ResolvedMoves(app, doc, gate, moves));
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0 ? "the token binds the proposals and clearance" : "no token while any proposal is invalid");
                return CommandResult.Ok(result);
            }
            if (errors.Count > 0) return CommandResult.Fail("Invalid proposals; nothing ran: " + errors.ToString(Formatting.None));
            // The network is worked out again here, so the token must bind what it resolves
            // to: every member and where it is now. A member added to the run since the
            // rehearsal, or a member already moved, is a different plan (backlog P0 #6).
            ResolvedPlan atApply = ResolvedMoves(app, doc, gate, moves);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, atApply, null);
            if (refusal != null) return refusal;

            // The neighbourhood: every model element whose box meets the swept region of any
            // MEMBER of any move (before AND after) - the whole network for a run_shift, not
            // just its primary mover - so "before" and "after" are measured over one set.
            var movedIds = new HashSet<long>(moves.SelectMany(mv => mv.MemberIds));
            var region = new Dictionary<long, Element>();
            foreach (Move mv in moves)
                foreach (long id in mv.MemberIds)
                {
                    Element member = doc.GetElement(Rid.Make(id));
                    BoundingBoxXYZ bb = member?.get_BoundingBox(null);
                    ResolveBox b0 = Box(bb);
                    if (b0 == null) continue;
                    double[] v = { mv.Vector.X * MmPerFoot, mv.Vector.Y * MmPerFoot, mv.Vector.Z * MmPerFoot };
                    var swept = new ResolveBox(Math.Min(b0.MinX, b0.MinX + v[0]), Math.Min(b0.MinY, b0.MinY + v[1]), Math.Min(b0.MinZ, b0.MinZ + v[2]),
                                               Math.Max(b0.MaxX, b0.MaxX + v[0]), Math.Max(b0.MaxY, b0.MaxY + v[1]), Math.Max(b0.MaxZ, b0.MaxZ + v[2]));
                    foreach (Element e in Neighbours(doc, swept, clearance)) region[Rid.Value(e.Id)] = e;
                }
            bool completeBefore, completeBeforeLinks;
            List<string> before = Detect(doc, movedIds, region.Keys, out completeBefore);
            List<string> linksSkippedBefore;
            before = before.Concat(DetectLinks(doc, movedIds, out completeBeforeLinks, out linksSkippedBefore)).ToList();
            completeBefore = completeBefore && completeBeforeLinks;
            foreach (Move mv in moves)
                mv.MemberBefore = mv.MemberIds.ToDictionary(id => id, id => UndoCapture.State(doc, id));

            string txName = "Horizun: resolve clash";
            var postconditions = new PostconditionCheck(moves.SelectMany(mv => mv.MemberIds.Select(id => "position:" + id))
                .Concat(moves.Select(mv => "pair_cleared:" + mv.FindingId))
                .Concat(moves.Where(mv => mv.Mode == ClashResolveRules.ModeRunShift).SelectMany(mv => mv.InternalEdges.Select(ed => "connections:" + ed.Item1 + "-" + ed.Item2)))
                .Concat(new[] { "no_new_clash" }).ToArray());
            List<string> after = null; bool completeAfter = false; string verdict = null; bool keep = false;
            List<string> linksSkippedAfter = new List<string>();
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
                        foreach (Move mv in moves)
                            ElementTransformUtils.MoveElements(doc, mv.MemberIds.Select(Rid.Make).ToList(), mv.Vector);
                        Guard.Commit(tx, txName);
                    }
                    bool positions = true;
                    foreach (Move mv in moves)
                        foreach (long id in mv.MemberIds)
                        {
                            JObject now = UndoCapture.State(doc, id);
                            JToken expected = Shift(mv.MemberBefore.TryGetValue(id, out JObject b0) ? b0 : null, mv.Vector);
                            bool ok = now != null && UndoRules.StatesMatch(expected?["loc"], now["loc"], PositionToleranceFt);
                            postconditions.Record("position:" + id, expected?["loc"], now?["loc"], ok);
                            positions &= ok;
                        }
                    // A run_shift moves rigidly, so every internal connection SHOULD survive on
                    // its own - but the contract re-reads rather than assumes: a connector that
                    // silently dropped is exactly the kind of thing a rigid translation should
                    // never do, and is worth rolling back over if it somehow did.
                    bool connections = true;
                    foreach (Move mv in moves.Where(m => m.Mode == ClashResolveRules.ModeRunShift))
                        foreach (Tuple<long, long> edge in mv.InternalEdges)
                        {
                            bool ok = StillConnected(doc, edge.Item1, edge.Item2);
                            postconditions.Record("connections:" + edge.Item1 + "-" + edge.Item2, "connected", ok ? "connected" : "disconnected", ok);
                            connections &= ok;
                        }
                    positions &= connections;
                    after = Detect(doc, movedIds, region.Keys, out completeAfter);
                    bool completeAfterLinks;
                    after = after.Concat(DetectLinks(doc, movedIds, out completeAfterLinks, out linksSkippedAfter)).ToList();
                    completeAfter = completeAfter && completeAfterLinks;
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
                            ["new_clashes"] = new JArray(fresh), ["postconditions"] = postconditions.ToJson(),
                            ["links_skipped"] = new JArray(linksSkippedAfter)
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
                foreach (long id in mv.MemberIds)
                {
                    JObject now = UndoCapture.State(doc, id);
                    JObject memberBefore = mv.MemberBefore.TryGetValue(id, out JObject b0) ? b0 : null;
                    if (now == null || !UndoRules.StatesMatch(Shift(memberBefore, mv.Vector)?["loc"], now["loc"], PositionToleranceFt))
                        return CommandResult.FailWithDetail("The group was kept but element " + id + " does not re-read at its verified position; inspect the model.",
                            new JObject { ["state"] = "uncertain" });
                }

            // Undo covers every MEMBER, not just the primary mover - horizun_undo must move
            // the whole network back, or the undone document would be a network moved apart.
            var entries = moves.Select(mv =>
            {
                var beforeStates = new JObject();
                foreach (long id in mv.MemberIds)
                    beforeStates[id.ToString(CultureInfo.InvariantCulture)] = (JToken)(mv.MemberBefore.TryGetValue(id, out JObject b0) ? b0 : null) ?? JValue.CreateNull();
                return UndoCapture.Entry(doc, "move", mv.MemberIds, beforeStates, new JObject { ["vector"] = new JArray(mv.Vector.X, mv.Vector.Y, mv.Vector.Z) });
            }).ToList();
            JObject undo = UndoCapture.Record(doc, Name, entries);

            // The ledger learns the MEASURED outcome - and only that.
            var resolved = new JArray(); string ledgerNote = null;
            try
            {
                string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                foreach (Move mv in moves)
                    if (ledger.TryGetValue(mv.FindingId, out CoordinationFinding f) &&
                        ClashResolveRules.ResolveMeasured(f, true, "solid re-detection after moving " +
                            (mv.Mode == ClashResolveRules.ModeRunShift ? "the connected network of " + mv.MemberIds.Count + " elements led by " + Rid.Value(mv.El.Id) : Rid.Value(mv.El.Id).ToString(CultureInfo.InvariantCulture)) +
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
                ["neighbourhood_elements"] = region.Count, ["links_skipped"] = new JArray(linksSkippedAfter)
            };
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed, moves.Count, moves.Count, moves.Count, 0, 0, 0);
            return CommandResult.Ok(applied);
        }

        private static JObject Shift(JObject state, XYZ v)
        {
            if (state == null) return null;
            // Both captured shapes (a curve's pair of points, a point's flat triple) - see
            // UndoRules.ShiftLoc for why a point is flat and must stay so.
            JToken shifted = UndoRules.ShiftLoc(state["loc"], v.X, v.Y, v.Z);
            if (shifted == null) return null;
            var o = (JObject)state.DeepClone();
            o["loc"] = shifted;
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
            // Re-derived fresh from the live model, never trusted from the propose call that
            // may be minutes old: a network that changed since propose must be re-approved,
            // not moved on stale information.
            if (!ResolveMoveScope(doc, e, out List<long> memberIds, out string mode, out List<Tuple<long, long>> edges, out string _, out string why)) return why;
            JArray v = o["vector_mm"] as JArray;
            if (v == null || v.Count != 3) return "vector_mm must be [x,y,z]";
            var vec = new XYZ((double)v[0] / MmPerFoot, (double)v[1] / MmPerFoot, (double)v[2] / MmPerFoot);
            if (vec.GetLength() * MmPerFoot > maxMove) return "the move exceeds max_move_mm";
            if (vec.GetLength() < 1e-6) return "vector_mm is zero";
            mv = new Move { FindingId = fid, El = e, Vector = vec, FixedId = Rid.Value(other.Id), MemberIds = memberIds, Mode = mode, InternalEdges = edges };
            return null;
        }

        private const double PositionToleranceFt = 1.0 / MmPerFoot;   // 1 mm

        private static bool StillConnected(Document doc, long idA, long idB)
        {
            Element a = doc.GetElement(Rid.Make(idA));
            if (a == null) return false;
            foreach (Connector c in MepFacts.Ordered(MepFacts.ManagerOf(a)))
            {
                bool connected; try { connected = c.IsConnected; } catch { connected = false; }
                if (!connected) continue;
                foreach (Connector r in c.AllRefs.OfType<Connector>())
                    if (r?.Owner != null && Rid.Value(r.Owner.Id) == idB) return true;
            }
            return false;
        }

        /// <summary>
        /// Solid intersections between each mover and every element of every LOADED link, as
        /// <see cref="ClashResolveRules.LinkPairKey"/> pairs. `complete` is false only when a
        /// solid read or boolean actually failed - an unloaded link is never counted against
        /// completeness (it is listed in <paramref name="linksSkipped"/> instead, "never as
        /// clear": the caller must not report it clean, but a link nobody could load does not
        /// block every apply either, matching SpatialCoherence.AgainstLinks).
        /// </summary>
        private static List<string> DetectLinks(Document doc, HashSet<long> movers, out bool complete, out List<string> linksSkipped)
        {
            complete = true;
            linksSkipped = new List<string>();
            var pairs = new List<string>();
            var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            var hostCache = new Dictionary<long, List<Solid>>();
            List<RevitLinkInstance> links;
            try { links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList(); }
            catch { return pairs; }
            foreach (RevitLinkInstance link in links)
            {
                string name = LinkName(link);
                Document linked; try { linked = link.GetLinkDocument(); } catch { linked = null; }
                if (linked == null) { linksSkipped.Add(name); continue; }
                Transform toLink; try { toLink = link.GetTotalTransform().Inverse; } catch { linksSkipped.Add(name + " (no transform)"); continue; }
                var linkCache = new Dictionary<long, List<Solid>>();
                foreach (long m in movers)
                {
                    List<Solid> sm = Solids(doc, m, options, hostCache);
                    if (sm == null || sm.Count == 0) { complete = false; continue; }
                    var moved = new List<Solid>();
                    foreach (Solid s in sm) { try { moved.Add(SolidUtils.CreateTransformed(s, toLink)); } catch { complete = false; } }
                    if (moved.Count == 0) continue;
                    var hits = new Dictionary<long, Element>();
                    foreach (Solid ms in moved)
                    {
                        try
                        {
                            BoundingBoxXYZ bb = ms.GetBoundingBox();
                            Transform t = bb.Transform;
                            XYZ p0 = t.OfPoint(bb.Min), p1 = t.OfPoint(bb.Max);
                            var outline = new Outline(
                                new XYZ(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Min(p0.Z, p1.Z)),
                                new XYZ(Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y), Math.Max(p0.Z, p1.Z)));
                            foreach (Element e in new FilteredElementCollector(linked).WhereElementIsNotElementType()
                                         .WherePasses(new BoundingBoxIntersectsFilter(outline))
                                         .WherePasses(new ElementIntersectsSolidFilter(ms))
                                         .Where(e => SpatialCoherence.IsPhysical(e)))
                                hits[Rid.Value(e.Id)] = e;
                        }
                        catch { complete = false; }
                    }
                    foreach (Element e in hits.Values)
                    {
                        long eid = Rid.Value(e.Id);
                        List<Solid> se = Solids(linked, eid, options, linkCache);
                        if (se == null) { complete = false; continue; }
                        if (se.Count == 0) continue;
                        bool hit = false;
                        foreach (Solid x in moved)
                            foreach (Solid y in se)
                            {
                                try
                                {
                                    Solid i = BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect);
                                    if (i != null && i.Volume > TinyVolume) hit = true;
                                }
                                catch { complete = false; }
                            }
                        if (hit) pairs.Add(ClashResolveRules.LinkPairKey(m, name, eid));
                    }
                }
            }
            return pairs;
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
