// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Revision A is in the model. Revision B is on the screen. What now?
//
// This is the part of a DWG-to-BIM tool that earns its keep, and it is the part
// where a wrong answer is most expensive: a first conversion that goes wrong is
// noticed, because nothing was there before. An incremental update goes wrong
// quietly, on top of a week of somebody's work.
//
// So the rules here are built around one distinction the naive version cannot
// make. When the element in the model does not match the new drawing, there are
// TWO possible reasons and they need opposite treatment:
//
//   the DRAWING moved   - update the element, that is the whole point
//   a PERSON moved it   - updating would silently destroy their work
//   BOTH moved          - nobody but they can say which is right
//
// Telling them apart needs the geometry the element was BUILT with, which is why
// provenance records it. Without that record the honest answer is "something
// changed and I cannot say what", and this file says exactly that rather than
// guessing - a guess here is somebody's afternoon.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// WHAT CHANGED, as distinct from WHAT TO DO ABOUT IT.
    ///
    /// Kind answers the second question and is the shorter list, because several
    /// different changes need the same treatment: a retyped wall, a relayered
    /// wall and a wall somebody moved by hand all end in "a person decides". A
    /// reader who only has the Kind cannot tell those apart, and they are not
    /// the same news.
    ///
    /// So every action also carries a classification, and the vocabulary is
    /// closed. Nothing here is a guess dressed as a fact: ambiguous and conflict
    /// are first-class answers, and they are the ones that stop an unattended
    /// run rather than letting it choose.
    /// </summary>
    public static class CadChange
    {
        /// <summary>The drawing says what it said, and nobody has touched the element.</summary>
        public const string Unchanged = "unchanged";
        /// <summary>In the drawing, and nothing in the model remembers being built from it.</summary>
        public const string Added = "added";
        /// <summary>Built from this drawing under these rules, and this revision no longer says it.</summary>
        public const string Removed = "removed";
        /// <summary>The same thing, somewhere else: same shape, same layer, same rule.</summary>
        public const string Moved = "moved";
        /// <summary>Recognisably the same thing with a different outline.</summary>
        public const string Reshaped = "reshaped";
        /// <summary>The drawing now asks for a different family type on the same geometry.</summary>
        public const string Retyped = "retyped";
        /// <summary>Same geometry, different layer - so possibly a different rule, and a different element.</summary>
        public const string Relayered = "relayered";
        /// <summary>Same run, different thickness or diameter.</summary>
        public const string Resized = "resized";
        /// <summary>The element now lives in a different host from the one it was built in.</summary>
        public const string Rehosted = "rehosted";
        /// <summary>The drawing did not change and a PERSON changed the element.</summary>
        public const string ManuallyDiverged = "manually_diverged";
        /// <summary>More than one reading fits, and only somebody who knows the building can choose.</summary>
        public const string Ambiguous = "ambiguous";
        /// <summary>The drawing changed AND a person changed the element. Nobody here can reconcile that.</summary>
        public const string Conflict = "conflict";

        /// <summary>Every classification this bridge will ever emit, so a reader can switch exhaustively.</summary>
        public static readonly string[] All =
        {
            Unchanged, Added, Removed, Moved, Reshaped, Retyped, Relayered, Resized, Rehosted,
            ManuallyDiverged, Ambiguous, Conflict
        };
    }

    /// <summary>One thing the update proposes to do, or refuses to.</summary>
    public sealed class CadUpdateAction
    {
        /// <summary>create | set_curve | review | leave | orphan</summary>
        public string Kind;

        /// <summary>
        /// What CHANGED, from the closed vocabulary in <see cref="CadChange"/>.
        /// Never null on an action this file produces: a change nobody can name
        /// is <see cref="CadChange.Ambiguous"/>, which is a name.
        /// </summary>
        public string Classification;
        public string CandidateId;
        public string SemanticId;
        /// <summary>
        /// WHAT THE THING IS, without the layer. Carried so the apply can stamp
        /// it: the first version never emitted it, and every element an
        /// incremental run created was stamped with GeometryId null - which is
        /// the one field the relayered rung matches on, so those elements could
        /// never be recognised as "the same shape on another layer".
        /// </summary>
        public string GeometryId;
        public long? ElementId;
        public string Says;
        public List<CadPoint> Geometry = new List<CadPoint>();
        public JObject Evidence = new JObject();
        /// <summary>False when a person must decide before anything is written.</summary>
        public bool Automatic;
        /// <summary>orphan: the candidate this MIGHT be, offered as a judgement rather than taken as one.</summary>
        public string PairedWith;
        public double? PairConfidence;
        /// <summary>orphan: where it was built, kept so a pairing can be judged on geometry.</summary>
        public List<CadPoint> AsBuiltGeometry;

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["kind"] = Kind,
                ["classification"] = Classification,
                ["automatic"] = Automatic,
                ["says"] = Says
            };
            if (PairedWith != null) o["may_be_the_same_as"] = PairedWith;
            if (PairConfidence.HasValue) o["pair_confidence"] = PairConfidence.Value;
            if (CandidateId != null) o["candidate_id"] = CandidateId;
            if (SemanticId != null) o["semantic_id"] = SemanticId;
            if (GeometryId != null) o["geometry_id"] = GeometryId;
            if (ElementId.HasValue) o["element_id"] = ElementId.Value;
            if (Geometry.Count > 0)
                o["geometry_mm"] = new JArray(Geometry.Select(p => new JArray(
                    Math.Round(p.X, 4, MidpointRounding.AwayFromZero),
                    Math.Round(p.Y, 4, MidpointRounding.AwayFromZero),
                    Math.Round(p.Z, 4, MidpointRounding.AwayFromZero))));
            if (Evidence != null && Evidence.HasValues) o["evidence"] = Evidence;
            return o;
        }
    }

    public sealed class CadUpdate
    {
        public List<CadUpdateAction> Actions = new List<CadUpdateAction>();
        public int CandidatesRead;
        public int SubjectsExamined;
        /// <summary>Pairings the caller asked for that this plan cannot honour, and why.</summary>
        public List<string> Rejected = new List<string>();

        public IEnumerable<CadUpdateAction> Of(string kind) => Actions.Where(a => a.Kind == kind);
        public int Count(string kind) => Actions.Count(a => a.Kind == kind);
        public bool NeedsAPerson => Actions.Any(a => !a.Automatic);

        public JObject CountsByKind()
        {
            var o = new JObject();
            foreach (var g in Actions.GroupBy(a => a.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
                o[g.Key] = g.Count();
            return o;
        }

        /// <summary>
        /// EVERY classification, including the zeros. A reader comparing two runs
        /// needs to see that conflict went from one to none, and a key that
        /// simply disappears reads as "not measured" rather than "none found".
        /// </summary>
        public JObject CountsByClassification()
        {
            var o = new JObject();
            foreach (string name in CadChange.All) o[name] = 0;
            foreach (CadUpdateAction a in Actions)
            {
                if (string.IsNullOrEmpty(a.Classification)) continue;
                o[a.Classification] = (int)o[a.Classification] + 1;
            }
            return o;
        }
    }

    public static class CadUpdateRules
    {
        /// <summary>
        /// Work out what revision B asks for, given what revision A left behind.
        /// Pure: no Revit, so the decision that can destroy somebody's work is
        /// provable at a desk.
        /// </summary>
        public static CadUpdate Plan(IList<CadCandidate> candidates, IList<CadAuditSubject> subjects,
                                     CadRequirementSet set, string sourceFileSha256,
                                     IDictionary<long, string> accepted = null,
                                     IEnumerable<string> lineage = null,
                                     IEnumerable<string> rejectedPairings = null,
                                     IDictionary<string, long> hostBySemanticId = null)
        {
            // WHICH ELEMENTS THIS UPDATE IS ABOUT.
            //
            // An incremental update reads a DIFFERENT FILE by definition - that is
            // what a new revision is - so "same file hash" cannot be the test for
            // whether an element belongs to this conversion. The first version
            // used it, and every element from revision A was therefore excluded:
            // the plan reported the whole model as untouched and revision B as
            // entirely new work.
            //
            // The lineage is the set of source hashes this drawing SUPERSEDES,
            // and it is the caller's statement, not a guess. Nothing in a DWG
            // says one file is a re-issue of another.
            //
            // This overload scopes by FILE and is blind to placement. The command
            // uses the scoped overload below; this one remains for the rules that
            // predate placement identity and for a caller with no placement facts.
            return Plan(candidates, subjects, set, CadUpdateScope.ByFile(sourceFileSha256, lineage),
                        accepted, rejectedPairings, hostBySemanticId, null);
        }

        /// <summary>
        /// The same plan, scoped by an explicit membership - which elements this
        /// PLACEMENT may claim, decided once by <see cref="CadPlacementRules.Resolve"/>
        /// - and, when the caller has accepted that the placement itself moved,
        /// re-derived under the new transform.
        /// </summary>
        public static CadUpdate Plan(IList<CadCandidate> candidates, IList<CadAuditSubject> subjects,
                                     CadRequirementSet set, CadUpdateScope scope,
                                     IDictionary<long, string> accepted,
                                     IEnumerable<string> rejectedPairings,
                                     IDictionary<string, long> hostBySemanticId,
                                     CadPlacementMove acceptedMove)
        {
            var update = new CadUpdate();
            candidates = candidates ?? new List<CadCandidate>();
            subjects = subjects ?? new List<CadAuditSubject>();
            scope = scope ?? CadUpdateScope.ByFile(null, null);
            update.CandidatesRead = candidates.Count;
            update.SubjectsExamined = subjects.Count;

            double tolerance = Math.Max(set != null ? set.PointToleranceMm : 1.0, 0.001);

            // THE PLACEMENT MOVED, AND THE CALLER SAID SO. Every semantic id is
            // derived from model coordinates, so after a placement move none of
            // them match and the ordinary matching would read the whole drawing
            // as deleted and redrawn. The re-derived plan carries each element's
            // as-built line through the move and matches on where it WOULD be.
            if (acceptedMove != null && acceptedMove.Moved && acceptedMove.From != null && acceptedMove.To != null)
            {
                PlanUnderMovedPlacement(update, candidates, subjects, set, scope, tolerance, acceptedMove);
                ProposePairings(update, set, tolerance,
                                new HashSet<string>(rejectedPairings ?? new string[0], StringComparer.Ordinal));
                ApplyAccepted(update, accepted);
                return update;
            }

            // WHICH ELEMENTS THIS RUN IS ABOUT - asked ONCE, and answered the
            // same way at both ends of it.
            //
            // The orphan loop was scoped to `known` and the MATCHING was not, so
            // an update for one drawing could claim an element built from another:
            // two sibling plans under one set, on two storeys, with the same
            // geometry on the same layer produce the same semantic id, and the
            // first unclaimed holder won. The reply then said "the drawing still
            // says exactly what this element was built from" about a wall on a
            // storey this drawing has never mentioned.
            //
            // AND AN ELEMENT WITH NO RECORDED SOURCE IS NOBODY'S. It used to be
            // everybody's: `string.IsNullOrEmpty(p.SourceFileSha256)` counted as
            // this drawing, so every identified run claimed and then ORPHANED
            // every anonymous element in the model - a proposal to delete work
            // whose origin is simply unknown, which is the one thing an unknown
            // origin is not evidence for.
            // SCOPED BY THE DRAWING, AND NOT BY THE RULES.
            //
            // The first version of this folded the requirement-set hash in, and
            // the live suite refused it within the hour: a set's hash changes
            // whenever the set does, so requiring the provenance to match the
            // CURRENT set means an update can never see a change made in the
            // rules - and "the drawing is the same and the set now asks for a
            // different type" is precisely what `retyped` and `resized` are. Half
            // of what an incremental is for went silent, and every count read zero
            // rather than wrong, which is the quietest way to lose a feature.
            //
            // The set still matters where it always did: in the ORPHAN loop, where
            // "built under other rules" is a reason not to propose deleting
            // something. Belonging to this run and being deletable by it are two
            // questions, and only the second one is about the rules.
            //
            // AND NOW SCOPED BY PLACEMENT, not by file. Two placements of one
            // file share a hash and nothing else; the scope object was resolved
            // from the placement id (v2) or from what a v1 record can still
            // prove, and this predicate only asks it.
            Func<CadAuditSubject, bool> mine = scope.Includes;

            var bySemantic = new Dictionary<string, List<CadAuditSubject>>(StringComparer.Ordinal);
            foreach (CadAuditSubject s in subjects)
            {
                if (s?.Provenance == null || string.IsNullOrEmpty(s.Provenance.SemanticId)) continue;
                if (!mine(s)) continue;
                List<CadAuditSubject> bucket;
                if (!bySemantic.TryGetValue(s.Provenance.SemanticId, out bucket))
                    bySemantic[s.Provenance.SemanticId] = bucket = new List<CadAuditSubject>();
                bucket.Add(s);
            }

            var claimed = new HashSet<long>();

            foreach (CadCandidate c in candidates)
            {
                if (c == null) continue;

                // The SEMANTIC id is what survives a re-issue: same layer, same
                // shape. A wall whose LINE moved between issues has a different
                // semantic id, so it will not be found here - it arrives as a
                // create and its old element as an orphan, and pairing those two
                // is a judgement about the drawing, not an identity. Say so
                // rather than inventing a match.
                CadAuditSubject held = null;
                List<CadAuditSubject> holders;
                if (bySemantic.TryGetValue(c.SemanticId ?? "", out holders))
                    held = holders.FirstOrDefault(h => !claimed.Contains(h.ElementId));

                // SAME SHAPE, DIFFERENT LAYER.
                //
                // The semantic id folds the layer in, so moving a wall's lines
                // from A-WALL to A-WALL-FIRE between revisions produces a create
                // and an orphan - the drawing says the same wall is now something
                // else, and the plan says it was deleted and a different one
                // built. The geometry id does NOT fold the layer in, so the two
                // can be recognised as one thing that was relayered, which is a
                // different piece of news and often a different wall TYPE.
                // SCOPED BY THE SAME PREDICATE. This rung searched every subject in
                // the model, so a drawing could be relayered into claiming a wall
                // another drawing built - the shape matches and the layer does not,
                // which is exactly the shape of a sibling plan on another storey.
                if (held == null && !string.IsNullOrEmpty(c.GeometryId))
                {
                    CadAuditSubject sameShape = subjects.FirstOrDefault(s =>
                        s?.Provenance != null && !claimed.Contains(s.ElementId) && mine(s) &&
                        string.Equals(s.Provenance.GeometryId, c.GeometryId, StringComparison.Ordinal) &&
                        !string.Equals(s.Provenance.SemanticId, c.SemanticId, StringComparison.Ordinal));
                    if (sameShape != null)
                    {
                        claimed.Add(sameShape.ElementId);
                        update.Actions.Add(new CadUpdateAction
                        {
                            Kind = "review",
                            Classification = CadChange.Relayered,
                            CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId, ElementId = sameShape.ElementId,
                            Geometry = new List<CadPoint>(c.Geometry),
                            Automatic = false,
                            Says = "the same shape is drawn on a DIFFERENT LAYER from the one this element was " +
                                   "built from (" + (sameShape.Provenance.Layer ?? "(none)") + " then, " +
                                   (c.Layer ?? "(none)") + " now). In a set where the layer decides the type - " +
                                   "which is the usual reason to change one - this element is now the wrong " +
                                   "type. Nothing was changed: which of the two the building actually has is " +
                                   "not something a drawing answers.",
                            Evidence = new JObject
                            {
                                ["was_layer"] = sameShape.Provenance.Layer,
                                ["now_layer"] = c.Layer,
                                ["was_rule"] = sameShape.Provenance.RuleId,
                                ["now_rule"] = c.RuleId,
                                ["geometry_id"] = c.GeometryId,
                                ["element_type_now"] = sameShape.TypeName,
                                ["rule_asks_for_type"] = c.FamilyType
                            }
                        });
                        continue;
                    }
                }

                if (held == null)
                {
                    update.Actions.Add(new CadUpdateAction
                    {
                        Kind = "create",
                        Classification = CadChange.Added,
                        CandidateId = c.Id,
                        SemanticId = c.SemanticId, GeometryId = c.GeometryId,
                        Geometry = new List<CadPoint>(c.Geometry),
                        Automatic = c.EligibleForAutomaticApply,
                        Says = c.EligibleForAutomaticApply
                            ? "this " + (c.ProposedKind ?? "element") + " is in the drawing and nothing in the " +
                              "model remembers being built from it."
                            : "this " + (c.ProposedKind ?? "element") + " is in the drawing and nothing was built " +
                              "from it, but the reading is not one to act on unreviewed: " +
                              string.Join("; ", c.IneligibleReasons),
                        Evidence = new JObject
                        {
                            ["rule_id"] = c.RuleId,
                            ["layer"] = c.Layer,
                            ["confidence"] = Math.Round(c.Confidence, 4)
                        }
                    });
                    continue;
                }

                claimed.Add(held.ElementId);
                CadProvenance p = held.Provenance;

                // MATCHED BY SEMANTIC ID MEANS THE DRAWING DID NOT MOVE.
                //
                // The semantic id is derived FROM the geometry, so a line that
                // moved has a different one by construction - it cannot arrive
                // here at all. The first version compared the as-built geometry
                // against the new drawing and called any difference "the drawing
                // moved", which was wrong twice over: unreachable in the case it
                // was written for, and TRUE FOR EVERY JOINED WALL, because Revit
                // trims a location curve back to where the centrelines cross and
                // the as-built line is therefore shorter than the drawing's. It
                // would have proposed to undo every join, on every update.
                //
                // What can still have happened is that a PERSON moved the element.
                List<CadPoint> asBuilt = AsBuilt(p);
                if (asBuilt == null)
                {
                    // Built before provenance recorded what it built. The element
                    // may be exactly as built or may have been moved since, and
                    // nothing here can tell - so nothing here decides.
                    update.Actions.Add(new CadUpdateAction
                    {
                        Kind = "leave",
                        Classification = CadChange.Unchanged,
                        CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId, ElementId = held.ElementId,
                        Automatic = true,
                        Says = "the drawing still says exactly what this element was built from, so this update " +
                               "has nothing to do to it. Its provenance does not record the geometry it was " +
                               "BUILT with, so whether somebody has moved it since cannot be answered here - " +
                               "horizun_audit_cad_model measures that against the drawing.",
                        Evidence = Where(c, held, p)
                    });
                    continue;
                }

                if (SamePlace(asBuilt, held.Geometry, tolerance))
                {
                    // THE GEOMETRY AGREES. That is not the same as nothing having
                    // changed: a revision can leave a wall exactly where it was
                    // and ask for a different TYPE, and a reading that only ever
                    // compares position reports that as "nothing to do".
                    // THE SAME POINT, A DIFFERENT WALL.
                    //
                    // A door drawn in the same place can end up in a different
                    // wall: somebody re-drew the partition it was in, or moved
                    // the door to the wall opposite by hand. The element still
                    // exists, still matches the drawing, and lives somewhere the
                    // drawing does not put it - which no comparison of positions
                    // can see, because the position agrees.
                    //
                    // The implied host is resolved by the CALLER, against the
                    // open document, using the same rule the first conversion
                    // used. This file stays Revit-free and only compares.
                    // KEYED BY THE SEMANTIC ID, not the revision id. A revision
                    // id is scoped to one issue of one file, so a caller building
                    // this map would have to guess which revision the comparison
                    // is against - and a key that silently misses reports NO
                    // rehosting rather than an error. The semantic id survives a
                    // re-issue, which is the whole reason it exists.
                    long impliedHost;
                    if (held.HostElementId.HasValue && hostBySemanticId != null &&
                        hostBySemanticId.TryGetValue(c.SemanticId ?? "", out impliedHost) &&
                        impliedHost != held.HostElementId.Value)
                    {
                        update.Actions.Add(new CadUpdateAction
                        {
                            Kind = "review",
                            Classification = CadChange.Rehosted,
                            CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId, ElementId = held.ElementId,
                            Geometry = new List<CadPoint>(c.Geometry),
                            Automatic = false,
                            Says = "the drawing puts this where it always was, and the element now lives in a " +
                                   "DIFFERENT WALL: element " + held.HostElementId.Value + " holds it, and the " +
                                   "wall at this point is element " + impliedHost + ". Re-hosting is not a move - " +
                                   "Revit cuts a new opening and closes the old one - so nothing was changed.",
                            Evidence = Where(c, held, p)
                        });
                        update.Actions[update.Actions.Count - 1].Evidence["hosted_in_now"] = held.HostElementId.Value;
                        update.Actions[update.Actions.Count - 1].Evidence["drawing_implies_host"] = impliedHost;
                        continue;
                    }


                    // WHAT ONLY THE REQUIREMENT SET KNOWS.
                    //
                    // A name, a number, a fire rating: a drawing carries none of
                    // them, so the set is the sole source and any difference is a
                    // person having changed it by hand. The audit reports those
                    // with a code each; here they are one classification, because
                    // the update's question is not "what differs" but "what should
                    // happen", and the answer to all of them is the same - nobody
                    // in this process can reconcile a value a person chose.
                    //
                    // WITHOUT THIS the update was BLIND to them. A grid renamed by
                    // hand, a room renumbered, a rating edited: all reported
                    // unchanged, on a run whose whole purpose is to say what
                    // changed since the last one.
                    CadDivergence divergence = FirstDivergence(c, held);
                    if (divergence != null)
                    {
                        var edited = new CadUpdateAction
                        {
                            Kind = "review",
                            Classification = CadChange.ManuallyDiverged,
                            CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId, ElementId = held.ElementId,
                            Geometry = new List<CadPoint>(c.Geometry),
                            Automatic = false,
                            Says = "the drawing has not moved and this element's " + divergence.Field +
                                   " is not what the requirement set asks for: the set says " +
                                   Quoted(divergence.Wanted) + " and the model holds " +
                                   Quoted(divergence.Held) + ". A drawing carries no " + divergence.Field +
                                   ", so the set is the only place this value ever came from and the difference " +
                                   "is somebody's decision. Nothing was changed: overwriting it would discard that " +
                                   "decision, and leaving it silent would hide it.",
                            Evidence = Where(c, held, p)
                        };
                        edited.Evidence["field"] = divergence.Field;
                        edited.Evidence["set_says"] = divergence.Wanted;
                        edited.Evidence["model_holds"] = divergence.Held;
                        update.Actions.Add(edited);
                        continue;
                    }

                    // THE SAME LINE, A DIFFERENT SIZE.
                    //
                    // A revision routinely leaves a run exactly where it is and
                    // makes it thicker: a partition promoted to a fire wall, a
                    // 100 mm branch grown to 150. Nothing about the position
                    // changes, so a reading that compares position alone reports
                    // it as nothing to do - and the model keeps carrying the old
                    // size into every quantity and every clash.
                    double? wantsWidth = c.ThicknessMm ?? c.DiameterMm;
                    if (wantsWidth.HasValue && held.WidthMm.HasValue &&
                        Math.Abs(wantsWidth.Value - held.WidthMm.Value) > Math.Max(tolerance, 1.0))
                    {
                        update.Actions.Add(new CadUpdateAction
                        {
                            Kind = "review",
                            Classification = CadChange.Resized,
                            CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId, ElementId = held.ElementId,
                            Geometry = new List<CadPoint>(c.Geometry),
                            Automatic = false,
                            Says = "the element is exactly where the drawing says and the drawing now asks for a " +
                                   "DIFFERENT SIZE: " +
                                   wantsWidth.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm where the " +
                                   "element measures " +
                                   held.WidthMm.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm. Size " +
                                   "lives in the TYPE, so honouring this means changing the element's type or " +
                                   "its type's definition - and the second would change every other element of " +
                                   "that type. Nothing was changed.",
                            Evidence = Where(c, held, p)
                        });
                        update.Actions[update.Actions.Count - 1].Evidence["drawing_asks_mm"] =
                            Math.Round(wantsWidth.Value, 3);
                        update.Actions[update.Actions.Count - 1].Evidence["element_measures_mm"] =
                            Math.Round(held.WidthMm.Value, 3);
                        continue;
                    }

                    string wantsType = c.FamilyType;
                    if (!string.IsNullOrWhiteSpace(wantsType) && !SameType(wantsType, held.TypeName))
                    {
                        update.Actions.Add(new CadUpdateAction
                        {
                            Kind = "review",
                            Classification = CadChange.Retyped,
                            CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId, ElementId = held.ElementId,
                            Geometry = new List<CadPoint>(c.Geometry),
                            Automatic = false,
                            Says = "the element is exactly where the drawing says, and the rule now asks for a " +
                                   "DIFFERENT TYPE: '" + wantsType + "' where the element is '" +
                                   (held.TypeName ?? "(unnamed)") + "'. Changing a type changes thickness, fire " +
                                   "rating and cost, and can move every face joined to it, so nothing was " +
                                   "changed here.",
                            Evidence = Where(c, held, p)
                        });
                        continue;
                    }

                    update.Actions.Add(new CadUpdateAction
                    {
                        Kind = "leave",
                        Classification = CadChange.Unchanged,
                        CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId, ElementId = held.ElementId,
                        Automatic = true,
                        Says = "unchanged in both: the drawing says what it said, and the element is where it " +
                               "was built. Nothing to do.",
                        Evidence = Where(c, held, p)
                    });
                    continue;
                }

                update.Actions.Add(new CadUpdateAction
                {
                    Kind = "review",
                    Classification = CadChange.ManuallyDiverged,
                    CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId, ElementId = held.ElementId,
                    Geometry = new List<CadPoint>(c.Geometry),
                    Automatic = false,
                    Says = "A PERSON MOVED THIS and the drawing did not change. Putting it back would undo their " +
                           "edit to match a drawing that never disagreed with them - which is the one thing an " +
                           "incremental update must never do on its own. Left alone; decide and say so " +
                           "explicitly.",
                    Evidence = Where(c, held, p)
                });
            }

            // Elements this drawing and this set built, that revision B no longer says.
            foreach (CadAuditSubject s in subjects.Where(x => x?.Provenance != null).OrderBy(x => x.ElementId))
            {
                if (claimed.Contains(s.ElementId)) continue;
                CadProvenance p = s.Provenance;

                // THE SAME DRAWING SCOPE the matching used, AND the set check that
                // only deletion needs. Two answers to "which elements is this run
                // about" was one too many - the matching claimed elements this
                // loop would have skipped, and this loop proposed to delete
                // anonymous elements the matching had already claimed - but
                // proposing to DELETE something built under different rules is a
                // separate question, and the answer to it is still no.
                if (!mine(s)) continue;   // not this run's business; the audit reports it
                bool sameSet = set == null || string.IsNullOrEmpty(p.RequirementSetSha256) ||
                               string.Equals(p.RequirementSetSha256, set.Sha256, StringComparison.Ordinal);
                if (!sameSet) continue;   // built under other rules; deleting it is not this run's call

                // AN ORPHAN THAT SOMEBODY ALSO MOVED IS A CONFLICT.
                //
                // The drawing no longer says this element AND it is not where it
                // was built. Those are two independent changes to one thing, and
                // reconciling them means knowing which of the two people was
                // right - which is not a fact about the drawing.
                List<CadPoint> orphanAsBuilt = AsBuilt(p);
                bool alsoMovedByHand = orphanAsBuilt != null && s.Geometry != null && s.Geometry.Count >= 2 &&
                                       !SamePlace(orphanAsBuilt, s.Geometry, tolerance);

                update.Actions.Add(new CadUpdateAction
                {
                    Kind = "orphan",
                    Classification = alsoMovedByHand ? CadChange.Conflict : CadChange.Removed,
                    SemanticId = p.SemanticId,
                    GeometryId = p.GeometryId,
                    ElementId = s.ElementId,
                    Automatic = false,
                    Says = "built from this drawing under these rules, and revision B no longer says it. It may " +
                           "have been deleted from the DWG, or it may have MOVED far enough to read as a new " +
                           "entity - in which case there is a create in this same plan that is really this " +
                           "element. Deleting is never automatic here: the two cases look identical from the " +
                           "outside and only one of them is a deletion.",
                    AsBuiltGeometry = AsBuilt(p),
                    Evidence = new JObject
                    {
                        ["was_layer"] = p.Layer,
                        ["was_rule"] = p.RuleId,
                        ["built_from_revision"] = p.CandidateId,
                        ["as_built_mm"] = p.BuiltGeometry,
                        ["also_moved_by_hand"] = alsoMovedByHand
                    }
                });
                if (alsoMovedByHand)
                    update.Actions[update.Actions.Count - 1].Says +=
                        " AND SOMEBODY MOVED IT since it was built, so this is a conflict rather than a " +
                        "deletion: the drawing dropped it and a person edited it, and which of those to " +
                        "honour is not a question about the drawing.";
            }

            ProposePairings(update, set, tolerance,
                            new HashSet<string>(rejectedPairings ?? new string[0], StringComparer.Ordinal));
            ApplyAccepted(update, accepted);
            return update;
        }

        /// <summary>
        /// The plan for a placement that MOVED since its elements were built, once
        /// a person has accepted that it did.
        ///
        /// Matching by semantic id is impossible here - every id changed with the
        /// coordinates - so each in-scope element's as-built line is CARRIED
        /// through the move to where it would be if it had followed the drawing,
        /// and candidates are matched on that. Three things can then be true of
        /// an element, and they are the same three the ordinary plan separates:
        ///
        ///   it is still where it was built     → the DRAWING moved and nobody
        ///                                        touched it: set_curve, automatic
        ///   it is already where it would be     → somebody moved it along with
        ///                                        the placement: leave, and re-stamp
        ///   it is somewhere else                → the placement moved AND a person
        ///                                        moved it: conflict, review
        ///
        /// Anything the carried lines do not account for is a create or an
        /// orphan exactly as before, and the pairing judgement runs on top.
        /// </summary>
        private static void PlanUnderMovedPlacement(CadUpdate update, IList<CadCandidate> candidates,
                                                    IList<CadAuditSubject> subjects, CadRequirementSet set,
                                                    CadUpdateScope scope, double tolerance, CadPlacementMove move)
        {
            var claimed = new HashSet<long>();
            List<CadAuditSubject> mine = subjects.Where(s => s?.Provenance != null && scope.Includes(s)).ToList();
            var carriedBy = new Dictionary<long, List<CadPoint>>();
            foreach (CadAuditSubject s in mine)
            {
                List<CadPoint> asBuilt = AsBuilt(s.Provenance);
                if (asBuilt != null) carriedBy[s.ElementId] = move.Carry(asBuilt);
            }
            JObject moveJson = move.ToJson();

            foreach (CadCandidate c in candidates)
            {
                if (c == null || c.Geometry == null || c.Geometry.Count == 0) continue;
                CadAuditSubject held = mine.FirstOrDefault(s =>
                    !claimed.Contains(s.ElementId) && carriedBy.ContainsKey(s.ElementId) &&
                    string.Equals(s.Provenance.Layer, c.Layer, StringComparison.Ordinal) &&
                    SamePlace(carriedBy[s.ElementId], c.Geometry, tolerance));

                if (held == null)
                {
                    update.Actions.Add(new CadUpdateAction
                    {
                        Kind = "create",
                        Classification = CadChange.Added,
                        CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId,
                        Geometry = new List<CadPoint>(c.Geometry),
                        Automatic = c.EligibleForAutomaticApply,
                        Says = "under the moved placement, no element's as-built line lands here: this " +
                               (c.ProposedKind ?? "element") + " is new in the drawing" +
                               (c.EligibleForAutomaticApply ? "." : ", but the reading is not one to act on unreviewed: " +
                                                                    string.Join("; ", c.IneligibleReasons)),
                        Evidence = new JObject
                        {
                            ["rule_id"] = c.RuleId, ["layer"] = c.Layer,
                            ["confidence"] = Math.Round(c.Confidence, 4),
                            ["placement_move"] = moveJson
                        }
                    });
                    continue;
                }

                claimed.Add(held.ElementId);
                List<CadPoint> asBuilt = AsBuilt(held.Provenance);
                List<CadPoint> carried = carriedBy[held.ElementId];
                JObject where = Where(c, held, held.Provenance);
                where["would_be_at_mm"] = Points(carried);
                where["placement_move"] = moveJson;

                if (SamePlace(carried, held.Geometry, tolerance))
                {
                    update.Actions.Add(new CadUpdateAction
                    {
                        Kind = "leave",
                        Classification = CadChange.Unchanged,
                        CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId,
                        ElementId = held.ElementId, Automatic = true,
                        Says = "the placement moved and this element is already where the drawing now puts it - " +
                               "somebody moved it along. Nothing to do but re-stamp it under the new transform.",
                        Evidence = where
                    });
                    continue;
                }
                if (SamePlace(asBuilt, held.Geometry, tolerance))
                {
                    update.Actions.Add(new CadUpdateAction
                    {
                        Kind = "set_curve",
                        Classification = CadChange.Moved,
                        CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId,
                        ElementId = held.ElementId,
                        Geometry = new List<CadPoint>(c.Geometry),
                        Automatic = true,
                        Says = "the PLACEMENT moved and nobody has touched this element since it was built: it is " +
                               "still on the line the drawing used to be on. Re-shaped to follow the drawing, and it " +
                               "keeps its id, its parameters and everything hosted on it.",
                        Evidence = where
                    });
                    continue;
                }
                update.Actions.Add(new CadUpdateAction
                {
                    Kind = "review",
                    Classification = CadChange.Conflict,
                    CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId,
                    ElementId = held.ElementId,
                    Geometry = new List<CadPoint>(c.Geometry),
                    Automatic = false,
                    Says = "the PLACEMENT moved and A PERSON ALSO MOVED THIS ELEMENT: it is neither where it was " +
                           "built nor where the drawing now puts it. Which of the two to honour is not a question " +
                           "about the drawing. Nothing was changed.",
                    Evidence = where
                });
            }

            foreach (CadAuditSubject s in mine.OrderBy(x => x.ElementId))
            {
                if (claimed.Contains(s.ElementId)) continue;
                CadProvenance p = s.Provenance;
                bool sameSet = set == null || string.IsNullOrEmpty(p.RequirementSetSha256) ||
                               string.Equals(p.RequirementSetSha256, set.Sha256, StringComparison.Ordinal);
                if (!sameSet) continue;
                List<CadPoint> asBuilt = AsBuilt(p);
                if (asBuilt == null)
                {
                    // No as-built line: it could not be carried, so nothing here
                    // can say whether the drawing still has it. Review, not orphan.
                    update.Actions.Add(new CadUpdateAction
                    {
                        Kind = "review", Classification = CadChange.Ambiguous,
                        SemanticId = p.SemanticId, GeometryId = p.GeometryId, ElementId = s.ElementId,
                        Automatic = false,
                        Says = "the placement moved and this element's provenance does not record where it was " +
                               "BUILT, so its line cannot be carried to where the drawing now is and nothing here " +
                               "can say whether the drawing still has it. Left alone.",
                        Evidence = new JObject { ["was_layer"] = p.Layer, ["placement_move"] = moveJson }
                    });
                    continue;
                }
                List<CadPoint> carried = move.Carry(asBuilt);
                bool alsoMovedByHand = s.Geometry != null && s.Geometry.Count >= 2 &&
                                       !SamePlace(asBuilt, s.Geometry, tolerance) &&
                                       !SamePlace(carried, s.Geometry, tolerance);
                update.Actions.Add(new CadUpdateAction
                {
                    Kind = "orphan",
                    Classification = alsoMovedByHand ? CadChange.Conflict : CadChange.Removed,
                    SemanticId = p.SemanticId, GeometryId = p.GeometryId, ElementId = s.ElementId,
                    Automatic = false,
                    Says = "built from this placement, and carried through its move no candidate lands on its " +
                           "line: the drawing no longer says it, or it moved within the drawing as well. Deleting " +
                           "is never automatic here.",
                    // The CARRIED line is what a pairing must be judged against:
                    // a create in this plan is in new coordinates, and the
                    // as-built line is in old ones.
                    AsBuiltGeometry = carried,
                    Evidence = new JObject
                    {
                        ["was_layer"] = p.Layer, ["was_rule"] = p.RuleId,
                        ["built_from_revision"] = p.CandidateId,
                        ["as_built_mm"] = p.BuiltGeometry,
                        ["would_be_at_mm"] = Points(carried),
                        ["also_moved_by_hand"] = alsoMovedByHand,
                        ["placement_move"] = moveJson
                    }
                });
            }
        }

        /// <summary>
        /// A wall that MOVED between revisions leaves a create and an orphan, and
        /// they are the same wall. Nothing in a DWG says so - there is no handle
        /// anywhere in the Revit CAD API, measured - so this is a JUDGEMENT, and
        /// it is offered as one: each plausible pairing is reported with what it
        /// was judged on, and a caller who accepts it passes it back explicitly.
        ///
        /// Guessing here would mean re-shaping an existing wall on a resemblance,
        /// which is the incremental-update version of building the wrong
        /// building.
        /// </summary>
        private static void ProposePairings(CadUpdate update, CadRequirementSet set, double tolerance,
                                            HashSet<string> rejected)
        {
            List<CadUpdateAction> creates = update.Of("create").ToList();
            List<CadUpdateAction> orphans = update.Of("orphan").ToList();
            if (creates.Count == 0 || orphans.Count == 0) return;

            foreach (CadUpdateAction orphan in orphans)
            {
                List<CadPoint> was = orphan.AsBuiltGeometry;
                if (was == null || was.Count < 2) continue;

                CadUpdateAction best = null;
                double bestScore = 0;
                string bestWhy = null;
                // EVERY plausible partner, not only the winner. When more than
                // one candidate could be this element moved, the winner is a
                // preference and the others are still live possibilities - and
                // each of them, left automatic, builds a wall that may be this
                // element in a place it is not.
                var plausibleCreates = new List<CadUpdateAction>();
                foreach (CadUpdateAction create in creates)
                {
                    if (create.Geometry.Count < 2) continue;
                    if (!string.Equals(create.Evidence.Value<string>("layer"), orphan.Evidence.Value<string>("was_layer"),
                                       StringComparison.Ordinal)) continue;
                    if (!string.Equals(create.Evidence.Value<string>("rule_id"), orphan.Evidence.Value<string>("was_rule"),
                                       StringComparison.Ordinal)) continue;

                    double wasLength = was[0].PlanDistanceTo(was[was.Count - 1]);
                    double nowLength = create.Geometry[0].PlanDistanceTo(create.Geometry[create.Geometry.Count - 1]);
                    if (wasLength <= 0 || nowLength <= 0) continue;
                    double lengthRatio = Math.Min(wasLength, nowLength) / Math.Max(wasLength, nowLength);
                    if (lengthRatio < 0.8) continue;

                    double angle = UndirectedAngle(was, create.Geometry);
                    if (angle > Math.Max(set.AngleToleranceDegrees, 5.0)) continue;

                    double distance = Math.Min(
                        Midpoint(was).PlanDistanceTo(Midpoint(create.Geometry)),
                        was[0].PlanDistanceTo(create.Geometry[0]));
                    // Beyond a couple of metres this stops being "the same wall,
                    // moved" and starts being "some other wall".
                    if (distance > 2000) continue;

                    plausibleCreates.Add(create);
                    double score = lengthRatio * (1 - Math.Min(1, distance / 2000.0));
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = create;
                    bestWhy = "same layer and rule, length within " +
                              ((1 - lengthRatio) * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%, " +
                              angle.ToString("0.##", CultureInfo.InvariantCulture) + " degrees off parallel, " +
                              distance.ToString("0", CultureInfo.InvariantCulture) + " mm away";
                }

                if (best == null) continue;

                // WHAT KIND OF CHANGE THE PAIRING IMPLIES. A pairing found at all
                // means the orphan is not simply gone - and the shape says which
                // news it is. Both remain judgements: the classification names
                // what is being judged, it does not decide it.
                bool onlyCandidate = plausibleCreates.Count == 1;
                string implied = SameShape(was, best.Geometry, tolerance) ? CadChange.Moved : CadChange.Reshaped;
                if (!onlyCandidate) implied = CadChange.Ambiguous;
                if (orphan.Classification == CadChange.Conflict) implied = CadChange.Conflict;
                orphan.Classification = implied;
                best.Classification = implied == CadChange.Conflict ? CadChange.Conflict : implied;

                orphan.PairedWith = best.CandidateId;
                orphan.PairConfidence = Math.Round(bestScore, 4);
                orphan.Evidence["may_be_the_same_wall_as"] = best.CandidateId;
                orphan.Evidence["paired_on"] = bestWhy;
                orphan.Evidence["pair_confidence"] = Math.Round(bestScore, 4);
                orphan.Says += " A candidate in this same plan looks like it (" + bestWhy + "). Nothing in a DWG " +
                               "says whether it IS - there is no handle anywhere in the CAD API - so this is a " +
                               "judgement offered, not taken: pass it back in accept_pairings and the update will " +
                               "re-shape this element instead of building a second one.";
                best.Evidence["may_be_element"] = orphan.ElementId;
                best.Evidence["paired_on"] = bestWhy;

                // AND HOLD EVERY CANDIDATE THAT COULD BE IT. A candidate that may
                // be an existing element MOVED is ambiguous, and building it
                // unattended puts a second wall beside the first - measured live,
                // 2026-08-27: applying the "automatic half" of an unresolved plan
                // produced two walls where the drawing shows one.
                //
                // Holding only the WINNER is not enough, and that was the first
                // version. With two plausible partners the runner-up stayed
                // automatic and built itself - so if the element had really moved
                // to the runner-up's line, the run produced a wall in the wrong
                // place AND left the original behind. The winner is a preference;
                // the others are still live possibilities, and an unattended run
                // may not act on any of them.
                foreach (CadUpdateAction maybe in plausibleCreates)
                {
                    if (rejected.Contains(maybe.CandidateId))
                    {
                        maybe.Evidence["pairing_rejected"] = true;
                        maybe.Says += " A caller rejected the pairing with element " + orphan.ElementId +
                                      ", so this is built as new.";
                        continue;
                    }

                    maybe.Automatic = false;
                    if (ReferenceEquals(maybe, best))
                    {
                        maybe.Says += " HELD: it may instead be element " + orphan.ElementId + " moved (" +
                                      bestWhy + "). Building it now would put a second wall beside that one. " +
                                      "Accept the pairing to re-shape the element, or reject it to say this " +
                                      "really is new.";
                    }
                    else
                    {
                        maybe.Classification = CadChange.Ambiguous;
                        maybe.Evidence["may_be_element"] = orphan.ElementId;
                        maybe.Evidence["held_because"] =
                            "another candidate in this plan is a closer match for element " + orphan.ElementId +
                            ", but this one is close enough that it could be that element too";
                        maybe.Says += " HELD: element " + orphan.ElementId + " could be this rather than the " +
                                      "candidate it was paired with. Building it unattended would put a wall " +
                                      "where that element may already be. Reject the pairing to say this is new.";
                    }
                }
            }
        }

        /// <summary>
        /// Pairings a PERSON accepted become set_curve: the element keeps its id,
        /// its parameters and everything hosted on it, and the create that would
        /// have duplicated it disappears from the plan.
        /// </summary>
        private static void ApplyAccepted(CadUpdate update, IDictionary<long, string> accepted)
        {
            if (accepted == null || accepted.Count == 0) return;
            foreach (KeyValuePair<long, string> pair in accepted)
            {
                CadUpdateAction orphan = update.Of("orphan").FirstOrDefault(o => o.ElementId == pair.Key);
                CadUpdateAction create = update.Of("create").FirstOrDefault(c => c.CandidateId == pair.Value);
                if (orphan == null || create == null)
                {
                    update.Rejected.Add("accept_pairings names element " + pair.Key + " with candidate '" +
                                        pair.Value + "', and this plan has " +
                                        (orphan == null ? "no such orphan" : "no such create") +
                                        ". A pairing accepted against a DIFFERENT plan would re-shape whichever " +
                                        "element happens to carry that id now. Nothing was paired.");
                    continue;
                }
                // ACCEPTING RESOLVES IT. A pairing offered as ambiguous and then
                // accepted by a person is no longer ambiguous - it is the change
                // the shape said it was, now with somebody's name on it. Leaving
                // it classified ambiguous would report the open question after it
                // had been answered.
                string settled = SameShape(orphan.AsBuiltGeometry, create.Geometry, 1.0)
                    ? CadChange.Moved : CadChange.Reshaped;
                orphan.Classification = settled;
                create.Classification = settled;

                orphan.Kind = "paired_away";
                orphan.Automatic = true;
                orphan.Says = "paired with '" + create.CandidateId + "' by the caller: this element is being " +
                              "re-shaped to the new line rather than left behind and duplicated.";
                create.Kind = "set_curve";
                create.ElementId = orphan.ElementId;
                create.Automatic = true;
                create.Says = "a caller accepted that this is element " + orphan.ElementId + " moved. It is " +
                              "re-shaped in place, so it keeps its id, its parameters and everything hosted on it.";
                create.Evidence["accepted_pairing"] = true;
            }
        }

        private static double UndirectedAngle(List<CadPoint> a, List<CadPoint> b)
        {
            CadVector ua = Unit(a), ub = Unit(b);
            if ((ua.X == 0 && ua.Y == 0) || (ub.X == 0 && ub.Y == 0)) return 180;
            return ua.UndirectedAngleDegrees(ub);
        }

        private static CadVector Unit(List<CadPoint> pts)
        {
            double dx = pts[pts.Count - 1].X - pts[0].X, dy = pts[pts.Count - 1].Y - pts[0].Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            return len <= 0 ? new CadVector(0, 0) : new CadVector(dx / len, dy / len);
        }

        private static CadPoint Midpoint(List<CadPoint> pts) =>
            new CadPoint((pts[0].X + pts[pts.Count - 1].X) / 2, (pts[0].Y + pts[pts.Count - 1].Y) / 2);

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Two type names for the same type. Revit reports a family instance's
        /// type as the type name alone and a requirement set may name it either
        /// way round, so "Family: Type" and "Type" are the same answer.
        /// </summary>
        private static bool SameType(string wanted, string actual)
        {
            if (string.IsNullOrWhiteSpace(actual)) return false;
            if (string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase)) return true;
            int colon = wanted.IndexOf(':');
            if (colon >= 0 && string.Equals(wanted.Substring(colon + 1).Trim(), actual,
                                            StringComparison.OrdinalIgnoreCase)) return true;
            colon = actual.IndexOf(':');
            if (colon >= 0 && string.Equals(actual.Substring(colon + 1).Trim(), wanted,
                                            StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Is this the same shape somewhere else, or a different shape? Length
        /// decides: a run of the same length that has moved is MOVED, and one
        /// whose length changed has been RESHAPED, whatever else also happened.
        /// </summary>
        private static bool SameShape(List<CadPoint> was, List<CadPoint> now, double tolerance)
        {
            if (was == null || now == null || was.Count < 2 || now.Count < 2) return false;
            double wasLength = was[0].PlanDistanceTo(was[was.Count - 1]);
            double nowLength = now[0].PlanDistanceTo(now[now.Count - 1]);
            return Math.Abs(wasLength - nowLength) <= Math.Max(tolerance, 1.0);
        }

        private static List<CadPoint> AsBuilt(CadProvenance p)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.BuiltGeometry)) return null;
            var points = new List<CadPoint>();
            foreach (string part in p.BuiltGeometry.Split(';'))
            {
                string[] xyz = part.Split(',');
                if (xyz.Length < 2) continue;
                double x, y, z = 0;
                if (!double.TryParse(xyz[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) continue;
                if (!double.TryParse(xyz[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) continue;
                if (xyz.Length > 2) double.TryParse(xyz[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
                points.Add(new CadPoint(x, y, z));
            }
            return points.Count == 0 ? null : points;
        }

        /// <summary>
        /// The same line, either way round, within tolerance. A null on either
        /// side is NOT a match: an unmeasurable thing is not an unchanged thing.
        /// </summary>

        /// <summary>One value the set declares and the model does not hold.</summary>
        private sealed class CadDivergence
        {
            public string Field;
            public string Wanted;
            public string Held;
        }

        /// <summary>
        /// The FIRST value a rule declares that the element does not carry, or
        /// null when they agree about all of them.
        ///
        /// First, not all: one review is one decision for a person to make about
        /// one element, and a list of six differences on the same wall is six
        /// entries somebody has to reconcile into the same single answer. The
        /// evidence names which value was compared, so the next run - after that
        /// answer - surfaces the next one.
        ///
        /// A value the element does not have AT ALL is NOT a divergence here.
        /// That is the audit's parameter_missing, and it means something different:
        /// nobody changed it, the element cannot hold it. Reporting it as a person's
        /// decision would send somebody looking for a decision that was never made.
        /// </summary>
        private static CadDivergence FirstDivergence(CadCandidate c, CadAuditSubject held)
        {
            if (c == null || held == null) return null;

            if (!string.IsNullOrWhiteSpace(c.AssignedName) && held.ElementName != null &&
                !string.Equals(c.AssignedName, held.ElementName, StringComparison.Ordinal))
                return new CadDivergence { Field = "name", Wanted = c.AssignedName, Held = held.ElementName };

            if (!string.IsNullOrWhiteSpace(c.AssignedNumber) && held.ElementNumber != null &&
                !string.Equals(c.AssignedNumber, held.ElementNumber, StringComparison.Ordinal))
                return new CadDivergence { Field = "number", Wanted = c.AssignedNumber, Held = held.ElementNumber };

            foreach (CadParameterWrite write in c.Parameters ?? new List<CadParameterWrite>())
            {
                if (write == null || string.IsNullOrWhiteSpace(write.Parameter)) continue;
                // Unreadable is not a difference either: the audit says so with a
                // code of its own, and guessing here would turn "could not look"
                // into "somebody changed it".
                if (held.ParametersUnreadable != null && held.ParametersUnreadable.Contains(write.Parameter)) continue;
                string now;
                if (held.ParameterValues == null || !held.ParameterValues.TryGetValue(write.Parameter, out now)) continue;
                string wanted = write.Value == null ? null : write.Value.ToString();
                if (wanted == null || string.Equals(wanted, now, StringComparison.Ordinal)) continue;
                return new CadDivergence { Field = "parameter " + Quoted(write.Parameter), Wanted = wanted, Held = now };
            }
            return null;
        }

        private static string Quoted(string s) { return s == null ? "(nothing)" : "'" + s + "'"; }

        private static bool SamePlace(List<CadPoint> a, List<CadPoint> b, double tolerance)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0) return false;
            if (a.Count != b.Count) return false;
            bool forward = true, reverse = true;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].PlanDistanceTo(b[i]) > tolerance) forward = false;
                if (a[i].PlanDistanceTo(b[b.Count - 1 - i]) > tolerance) reverse = false;
            }
            return forward || reverse;
        }

        private static JObject Where(CadCandidate c, CadAuditSubject s, CadProvenance p)
        {
            return new JObject
            {
                ["drawing_says_mm"] = Points(c?.Geometry),
                ["element_is_at_mm"] = Points(s?.Geometry),
                ["was_built_at_mm"] = p == null || string.IsNullOrWhiteSpace(p.BuiltGeometry)
                    ? (JToken)JValue.CreateNull() : Points(AsBuilt(p)),
                ["as_built_recorded"] = p != null && !string.IsNullOrWhiteSpace(p.BuiltGeometry)
            };
        }

        private static JToken Points(List<CadPoint> points)
        {
            if (points == null) return JValue.CreateNull();
            return new JArray(points.Select(p => new JArray(
                Math.Round(p.X, 3), Math.Round(p.Y, 3), Math.Round(p.Z, 3))));
        }

        /// <summary>The canonical as-built string provenance records. Read back by AsBuilt.</summary>
        public static string Encode(IEnumerable<CadPoint> points)
        {
            if (points == null) return null;
            var parts = points.Select(p =>
                p.X.ToString("0.####", CultureInfo.InvariantCulture) + "," +
                p.Y.ToString("0.####", CultureInfo.InvariantCulture) + "," +
                p.Z.ToString("0.####", CultureInfo.InvariantCulture)).ToList();
            return parts.Count == 0 ? null : string.Join(";", parts);
        }
    }
}
