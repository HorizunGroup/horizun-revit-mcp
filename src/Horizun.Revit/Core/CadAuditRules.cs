// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// What the drawing says, against what the model holds.
//
// The audit answers one question - "does this model still agree with this
// drawing?" - and it answers it WITHOUT writing anything, because the moment an
// audit can change the thing it is measuring, nobody can use its output as
// evidence.
//
// The matching is a LADDER, and which rung a pair matched on is part of the
// finding, because the rungs mean different things:
//
//   revision   the same entity, in the same issue of the same file. Nothing to do.
//   semantic   the same entity, same layer, same shape - in a DIFFERENT issue of
//              the drawing. The file was re-cut; the building did not change.
//   geometry   the same shape on a DIFFERENT layer. Somebody relayered it, and
//              that usually means it now means something else.
//   position   no provenance at all, but something is standing exactly where the
//              drawing says. Built by hand, or by a run older than provenance.
//
// A pair that matches on a lower rung is not a failure. It is a fact about what
// happened between the two, and reporting it as a match with no qualifier would
// hide the only interesting part.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What one element in the model looks like to the audit. Measured by the command; pure data here.</summary>
    public sealed class CadAuditSubject
    {
        public long ElementId;
        public string Category;
        public string TypeName;
        public string LevelName;
        /// <summary>Plan geometry: two points for a located curve, one for a point. Empty when Revit would not say.</summary>
        public List<CadPoint> Geometry = new List<CadPoint>();
        /// <summary>Null when the element carries no Horizun CAD provenance - which is not a fault, just a fact.</summary>
        public CadProvenance Provenance;
        /// <summary>Set when an entity exists but could not be believed.</summary>
        public string ProvenanceProblem;

        /// <summary>
        /// The element's own CURVATURE, when it has any: centre and radius in mm.
        /// Null for a straight thing.
        ///
        /// Without it a curved wall is compared by its two endpoints alone, and
        /// two arcs through the same ends - a minor and a major arc, or two radii -
        /// read as the same element. The audit would then say a wall agrees with a
        /// drawing it does not match at any point between its ends.
        /// </summary>
        public CadPoint? ArcCentre;
        public double? ArcRadiusMm;

        /// <summary>
        /// How wide the element IS, in millimetres - a wall's thickness, a pipe's
        /// or duct's diameter - or null when the question does not apply.
        ///
        /// Null is a real answer and never collapses into zero: a drawing asking
        /// for 200 mm against an element nobody can measure is "not comparable",
        /// and against an element measuring 150 it is a wall of the wrong
        /// thickness. Those are different findings and deserve different words.
        /// </summary>
        public double? WidthMm;

        /// <summary>What the element lives IN, when it lives in anything. Null for a free-standing thing.</summary>
        public long? HostElementId;

        /// <summary>
        /// What the element is CALLED, for the kinds that carry an identity of
        /// their own - a grid, a level, a room. Null where the question does not
        /// apply, which is not the same as an empty name.
        /// </summary>
        public string ElementName;
        /// <summary>A room's number. Revit assigns one the instant a room is placed.</summary>
        public string ElementNumber;

        /// <summary>
        /// Parameter values read off the element, keyed as the requirement set
        /// named them. Only the ones a rule asked about are read - sweeping every
        /// parameter of every element would cost more than the audit.
        /// </summary>
        public Dictionary<string, string> ParameterValues =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Parameters a rule named that this element does not carry, or could not be read.</summary>
        public List<string> ParametersUnreadable = new List<string>();
    }

    public sealed class CadFinding
    {
        public string Code;
        public string Severity;          // "blocking" | "review" | "informational"
        public string CandidateId;
        public string SemanticId;
        public long? ElementId;
        public string Says;
        public JObject Evidence = new JObject();

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["code"] = Code,
                ["severity"] = Severity,
                ["says"] = Says
            };
            if (CandidateId != null) o["candidate_id"] = CandidateId;
            if (SemanticId != null) o["semantic_id"] = SemanticId;
            if (ElementId.HasValue) o["element_id"] = ElementId.Value;
            if (Evidence != null && Evidence.HasValues) o["evidence"] = Evidence;
            return o;
        }
    }

    public sealed class CadMatch
    {
        public string CandidateId;
        public string SemanticId;
        public long ElementId;
        /// <summary>Which rung: revision | semantic | geometry | position.</summary>
        public string MatchedOn;
        /// <summary>How far OFF THE LINE, perpendicular. This is what "moved" means.</summary>
        public double? OffsetMm;
        /// <summary>How far the ends differ ALONG the line. A wall join accounts for most of this.</summary>
        public double? ExtentMm;

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["candidate_id"] = CandidateId,
                ["semantic_id"] = SemanticId,
                ["element_id"] = ElementId,
                ["matched_on"] = MatchedOn
            };
            if (OffsetMm.HasValue) o["offset_mm"] = Math.Round(OffsetMm.Value, 3);
            if (ExtentMm.HasValue) o["extent_mm"] = Math.Round(ExtentMm.Value, 3);
            return o;
        }
    }

    public sealed class CadAudit
    {
        public List<CadMatch> Matches = new List<CadMatch>();
        public List<CadFinding> Findings = new List<CadFinding>();
        public int CandidatesRead;
        public int SubjectsExamined;

        public int MatchedOn(string rung) => Matches.Count(m => m.MatchedOn == rung);
        public int Count(string code) => Findings.Count(f => f.Code == code);

        /// <summary>
        /// EVERY code, including the zeros.
        ///
        /// A key that simply disappears reads as "not measured", and for the
        /// codes that matter most that is the opposite of the truth: "no
        /// unhosted doors" and "hosting was never checked" are the same absent
        /// key and completely different news. Any code that somehow appears
        /// without being in the vocabulary is still reported, because losing a
        /// finding to a typo would be worse than an untidy list.
        /// </summary>
        public JObject CountsByCode()
        {
            var o = new JObject();
            foreach (string code in CadFindingCode.All) o[code] = 0;
            foreach (var g in Findings.GroupBy(f => f.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
                o[g.Key] = g.Count();
            return o;
        }
    }

    /// <summary>
    /// Every finding this audit can report, as a closed list.
    ///
    /// Closed so that a reader can switch on it exhaustively, and so that the
    /// counts can report the zeros - which is the half that carries the news
    /// nobody goes looking for.
    /// </summary>
    public static class CadFindingCode
    {
        // The drawing and the model disagree about something that exists.
        public const string DrawingNotBuilt = "drawing_not_built";
        public const string BuiltNotInDrawing = "built_not_in_drawing";
        public const string DuplicateInModel = "duplicate_in_model";
        public const string Unhosted = "unhosted";

        // They agree about the thing and not about its history or its substance.
        public const string Moved = "moved";
        public const string ExtentDiffers = "extent_differs";
        public const string Relayered = "relayered";
        public const string TypeDiffers = "type_differs";
        public const string SizeDiffers = "size_differs";
        /// <summary>The set names this grid and the model calls it something else.</summary>
        public const string GridNameDiffers = "grid_name_differs";
        public const string RoomNameDiffers = "room_name_differs";
        public const string RoomNumberDiffers = "room_number_differs";
        /// <summary>A rule writes this parameter and the element does not carry the value.</summary>
        public const string ParameterDiffers = "parameter_differs";
        /// <summary>A rule writes this parameter and the element does not have it at all.</summary>
        public const string ParameterMissing = "parameter_missing";
        /// <summary>The parameter exists and could not be read. Not a pass, and not a difference.</summary>
        public const string ParameterUnreadable = "parameter_unreadable";
        public const string BuiltByAnotherRequirementSet = "built_by_another_requirement_set";
        public const string BuiltFromAnotherDrawing = "built_from_another_drawing";
        public const string ProvenanceUnreadable = "provenance_unreadable";

        // Facts worth recording that need no decision.
        public const string Reissued = "reissued";
        public const string AnonymousButCoincident = "anonymous_but_coincident";

        public static readonly string[] All =
        {
            DrawingNotBuilt, BuiltNotInDrawing, DuplicateInModel, Unhosted,
            Moved, ExtentDiffers, Relayered, TypeDiffers, SizeDiffers,
            GridNameDiffers, RoomNameDiffers, RoomNumberDiffers,
            ParameterDiffers, ParameterMissing, ParameterUnreadable,
            BuiltByAnotherRequirementSet, BuiltFromAnotherDrawing, ProvenanceUnreadable,
            Reissued, AnonymousButCoincident
        };
    }

    public static class CadAuditRules
    {
        // Severity is about what a reader must DO, not about how alarming it
        // sounds. blocking: the model and the drawing disagree about a thing that
        // exists. review: they agree about the thing but not about its history,
        // and somebody has to say which is right. informational: a fact worth
        // recording that needs no decision.
        public const string Blocking = "blocking";
        public const string Review = "review";
        public const string Informational = "informational";

        /// <summary>
        /// Compare a reading of the drawing against a reading of the model.
        /// Nothing here touches Revit, which is what makes it testable and what
        /// makes the audit safe to run on somebody's live deliverable.
        /// </summary>
        public static CadAudit Compare(IList<CadCandidate> candidates, IList<CadAuditSubject> subjects,
                                       CadRequirementSet set, string sourceFingerprint, string sourceFileSha256)
        {
            var audit = new CadAudit();
            candidates = candidates ?? new List<CadCandidate>();
            subjects = subjects ?? new List<CadAuditSubject>();
            audit.CandidatesRead = candidates.Count;
            audit.SubjectsExamined = subjects.Count;

            double tolerance = Math.Max(set != null ? set.PointToleranceMm : 1.0, 0.001);
            // Position matching needs a real bound, not the point tolerance: a
            // wall built by hand from the same drawing lands within millimetres,
            // not within one. Ten times the point tolerance, floored at 25 mm,
            // and the deviation is REPORTED so the reader judges rather than
            // trusting the constant.
            double positionBound = Math.Max(tolerance * 10.0, 25.0);

            // ------------------------------------------------------------- model
            var byCandidate = new Dictionary<string, List<CadAuditSubject>>(StringComparer.Ordinal);
            var bySemantic = new Dictionary<string, List<CadAuditSubject>>(StringComparer.Ordinal);
            var byGeometry = new Dictionary<string, List<CadAuditSubject>>(StringComparer.Ordinal);
            var anonymous = new List<CadAuditSubject>();

            foreach (CadAuditSubject s in subjects)
            {
                if (s == null) continue;
                if (s.ProvenanceProblem != null)
                {
                    audit.Findings.Add(new CadFinding
                    {
                        Code = "provenance_unreadable",
                        Severity = Review,
                        ElementId = s.ElementId,
                        Says = "this element carries a Horizun CAD provenance entity that could not be believed (" +
                               s.ProvenanceProblem + "), so the audit treated it as anonymous. It may be a genuine " +
                               "match this run cannot see.",
                        Evidence = new JObject { ["problem"] = s.ProvenanceProblem }
                    });
                }
                if (s.Provenance == null) { anonymous.Add(s); continue; }
                Add(byCandidate, s.Provenance.CandidateId, s);
                Add(bySemantic, s.Provenance.SemanticId, s);
                Add(byGeometry, s.Provenance.GeometryId, s);
            }

            // Two elements claiming to be the same drawing entity. Whichever run
            // produced them, an incremental update now has no single thing to
            // update, so this is named before anything else uses the index.
            foreach (var pair in bySemantic.Where(p => p.Value.Count > 1)
                                           .OrderBy(p => p.Key, StringComparer.Ordinal))
                audit.Findings.Add(new CadFinding
                {
                    Code = "duplicate_in_model",
                    Severity = Blocking,
                    SemanticId = pair.Key,
                    Says = pair.Value.Count + " elements carry the SAME drawing entity's provenance. An incremental " +
                           "update has no single element to update, and a quantity take-off counts this entity " +
                           "more than once. Delete the copies, or re-apply from a clean model.",
                    Evidence = new JObject { ["element_ids"] = new JArray(pair.Value.Select(v => v.ElementId)) }
                });

            // ------------------------------------------------------------ ladder
            var claimed = new HashSet<long>();
            var candidateSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (CadCandidate c in candidates)
            {
                if (c == null) continue;
                candidateSeen.Add(c.SemanticId ?? "");
                CadAuditSubject hit = FirstFree(byCandidate, c.Id, claimed);
                if (hit != null)
                {
                    Record(audit, c, hit, "revision", claimed, tolerance);
                    continue;
                }

                hit = FirstFree(bySemantic, c.SemanticId, claimed, sourceFileSha256);
                if (hit != null)
                {
                    Record(audit, c, hit, "semantic", claimed, tolerance);
                    audit.Findings.Add(new CadFinding
                    {
                        Code = "reissued",
                        Severity = Informational,
                        CandidateId = c.Id,
                        SemanticId = c.SemanticId,
                        ElementId = hit.ElementId,
                        Says = "the same entity on the same layer with the same shape, from a DIFFERENT issue of " +
                               "the drawing. The file was re-cut; this element still says what the drawing says, " +
                               "and its provenance is one issue behind.",
                        Evidence = new JObject
                        {
                            ["provenance_revision"] = hit.Provenance.CandidateId,
                            ["drawing_revision"] = c.Id,
                            ["provenance_source"] = hit.Provenance.SourceFingerprint,
                            ["drawing_source"] = sourceFingerprint
                        }
                    });
                    continue;
                }

                hit = FirstFree(byGeometry, c.GeometryId, claimed, sourceFileSha256);
                if (hit != null)
                {
                    Record(audit, c, hit, "geometry", claimed, tolerance);
                    audit.Findings.Add(new CadFinding
                    {
                        Code = "relayered",
                        Severity = Review,
                        CandidateId = c.Id,
                        SemanticId = c.SemanticId,
                        ElementId = hit.ElementId,
                        Says = "the same shape, on a DIFFERENT layer. The drawing now says '" + (c.Layer ?? "(none)") +
                               "' where this element was built from '" + (hit.Provenance.Layer ?? "(none)") +
                               "'. A layer change is usually a change of meaning - what was a wall may now be a " +
                               "handrail - so this needs somebody to decide, not an automatic update.",
                        Evidence = new JObject
                        {
                            ["was_layer"] = hit.Provenance.Layer,
                            ["now_layer"] = c.Layer,
                            ["was_rule"] = hit.Provenance.RuleId,
                            ["now_rule"] = c.RuleId
                        }
                    });
                    continue;
                }

                // Nothing remembers being this entity. Is something standing there anyway?
                double deviation;
                CadAuditSubject coincident = NearestFree(anonymous, c, positionBound, claimed, out deviation);
                if (coincident != null)
                {
                    claimed.Add(coincident.ElementId);
                    audit.Matches.Add(new CadMatch
                    {
                        CandidateId = c.Id, SemanticId = c.SemanticId,
                        ElementId = coincident.ElementId, MatchedOn = "position", OffsetMm = deviation
                    });
                    audit.Findings.Add(new CadFinding
                    {
                        Code = "anonymous_but_coincident",
                        Severity = Informational,
                        CandidateId = c.Id,
                        SemanticId = c.SemanticId,
                        ElementId = coincident.ElementId,
                        Says = "an element with no Horizun provenance sits within " +
                               deviation.ToString("0.#", CultureInfo.InvariantCulture) + " mm of the line this " +
                               "drawing draws here. It was probably built by hand, or by a run older than " +
                               "provenance. The audit treats it as built; an incremental update will NOT recognise " +
                               "it, because position is not identity.",
                        Evidence = new JObject
                        {
                            ["offset_mm"] = Math.Round(deviation, 3),
                            ["bound_mm"] = Math.Round(positionBound, 3)
                        }
                    });
                    continue;
                }

                audit.Findings.Add(new CadFinding
                {
                    Code = "drawing_not_built",
                    Severity = Blocking,
                    CandidateId = c.Id,
                    SemanticId = c.SemanticId,
                    Says = "the drawing says there is a " + (c.ProposedKind ?? "element") + " here on layer '" +
                           (c.Layer ?? "(none)") + "' and the model holds nothing that matches it, by provenance " +
                           "or by position.",
                    Evidence = new JObject
                    {
                        ["rule_id"] = c.RuleId,
                        ["confidence"] = Math.Round(c.Confidence, 4),
                        ["eligible_for_automatic_apply"] = c.EligibleForAutomaticApply,
                        ["geometry_mm"] = new JArray(c.Geometry.Select(p => new JArray(
                            Math.Round(p.X, 3), Math.Round(p.Y, 3), Math.Round(p.Z, 3))))
                    }
                });
            }

            // ------------------------------------------- what the model holds and the drawing does not
            foreach (CadAuditSubject s in subjects.Where(x => x != null && x.Provenance != null)
                                                  .OrderBy(x => x.ElementId))
            {
                if (claimed.Contains(s.ElementId)) continue;
                CadProvenance p = s.Provenance;

                bool sameSet = set == null || string.IsNullOrEmpty(p.RequirementSetSha256) ||
                               string.Equals(p.RequirementSetSha256, set.Sha256, StringComparison.Ordinal);
                bool sameDrawing = string.IsNullOrEmpty(p.SourceFileSha256) ||
                                   string.IsNullOrEmpty(sourceFileSha256) ||
                                   string.Equals(p.SourceFileSha256, sourceFileSha256, StringComparison.Ordinal);

                if (!sameDrawing)
                {
                    audit.Findings.Add(new CadFinding
                    {
                        Code = "built_from_another_drawing",
                        Severity = Informational,
                        SemanticId = p.SemanticId,
                        ElementId = s.ElementId,
                        Says = "this element was built from a DIFFERENT DWG, so this audit says nothing about it. " +
                               "Audit that drawing to find out whether it is still current.",
                        Evidence = new JObject
                        {
                            ["built_from_sha256"] = p.SourceFileSha256,
                            ["audited_sha256"] = sourceFileSha256
                        }
                    });
                    continue;
                }

                if (!sameSet)
                {
                    audit.Findings.Add(new CadFinding
                    {
                        Code = "built_by_another_requirement_set",
                        Severity = Review,
                        SemanticId = p.SemanticId,
                        ElementId = s.ElementId,
                        Says = "this element came from the SAME drawing under a different requirement set (" +
                               (p.RequirementSetId ?? "?") + "@" + (p.RequirementSetVersion ?? "?") + "). The two " +
                               "sets disagree about what this drawing means, and no audit can settle that - " +
                               "somebody has to say which set is the current one.",
                        Evidence = new JObject
                        {
                            ["built_under"] = (p.RequirementSetId ?? "?") + "@" + (p.RequirementSetVersion ?? "?"),
                            ["built_under_sha256"] = p.RequirementSetSha256,
                            ["audited_under_sha256"] = set == null ? null : set.Sha256
                        }
                    });
                    continue;
                }

                audit.Findings.Add(new CadFinding
                {
                    Code = "built_not_in_drawing",
                    Severity = Blocking,
                    SemanticId = p.SemanticId,
                    ElementId = s.ElementId,
                    Says = "this element remembers being built from THIS drawing under THIS requirement set, and " +
                           "the drawing no longer says it. Either the entity was deleted from the DWG, or it moved " +
                           "far enough to read as a different one. Deleting the element is a decision about " +
                           "somebody's model, so the audit names it and stops.",
                    Evidence = new JObject
                    {
                        ["was_layer"] = p.Layer,
                        ["was_rule"] = p.RuleId,
                        ["built_from_revision"] = p.CandidateId,
                        ["plan_fingerprint"] = p.PlanFingerprint
                    }
                });
            }

            return audit;
        }

        // ---------------------------------------------------------------- helpers

        private static void Add(Dictionary<string, List<CadAuditSubject>> index, string key, CadAuditSubject s)
        {
            if (string.IsNullOrEmpty(key)) return;
            List<CadAuditSubject> bucket;
            if (!index.TryGetValue(key, out bucket)) index[key] = bucket = new List<CadAuditSubject>();
            bucket.Add(s);
        }

        /// <summary>Which produced kinds Revit will not place without a host wall.</summary>
        private static bool NeedsWallHost(string produces)
        {
            return produces == "door" || produces == "window";
        }

        /// <summary>
        /// The part of an audit that is not about coordinates: what the element
        /// is made of, what it is made to, and what it lives in.
        ///
        /// Every check here is CONDITIONAL on the rule having said something. A
        /// requirement set that names no family type is not disagreeing about the
        /// type, and an element whose width cannot be read is not the wrong
        /// width - it is unmeasured, which is a different sentence.
        /// </summary>
        private static void AuditSubstance(CadAudit audit, CadCandidate c, CadAuditSubject hit, double tolerance)
        {
            // UNHOSTED. A door that is in the right place and hosted in nothing
            // schedules, tags and renders exactly like one that is not - and cuts
            // no opening. It is the failure the hosting path exists to prevent,
            // so the audit names it rather than leaving it to be noticed in a
            // section drawing three weeks later.
            if (NeedsWallHost(c.ProposedKind) && !hit.HostElementId.HasValue)
                audit.Findings.Add(new CadFinding
                {
                    Code = "unhosted",
                    Severity = Blocking,
                    CandidateId = c.Id,
                    SemanticId = c.SemanticId,
                    ElementId = hit.ElementId,
                    Says = "this is the " + (c.ProposedKind ?? "element") + " the drawing means, in the right " +
                           "place, and it is hosted in NOTHING. Revit cuts no opening for an unhosted instance, " +
                           "so there is a " + (c.ProposedKind ?? "element") + "-shaped object standing where the " +
                           "opening should be. It schedules and tags exactly like a real one.",
                    Evidence = new JObject { ["host_element_id"] = JValue.CreateNull() }
                });

            // THE WRONG TYPE. Only when the rule named one: substituting silently
            // is what this whole path exists to prevent, and so is complaining
            // about a substitution nobody asked for.
            if (!string.IsNullOrWhiteSpace(c.FamilyType) && !string.IsNullOrWhiteSpace(hit.TypeName) &&
                !SameTypeName(c.FamilyType, hit.TypeName))
                audit.Findings.Add(new CadFinding
                {
                    Code = "type_differs",
                    Severity = Review,
                    CandidateId = c.Id,
                    SemanticId = c.SemanticId,
                    ElementId = hit.ElementId,
                    Says = "this is the element the drawing means and it is of the WRONG TYPE: the rule asks for " +
                           "'" + c.FamilyType + "' and the element is '" + hit.TypeName + "'. Type carries " +
                           "thickness, fire rating and cost, so this is a difference in the building rather than " +
                           "in the drawing of it.",
                    Evidence = new JObject
                    {
                        ["rule_asks_for"] = c.FamilyType,
                        ["element_is"] = hit.TypeName
                    }
                });

            // THE WRONG NAME. A grid the set calls 3 and the model calls A is a
            // model where every dimension cites a reference nobody chose - and
            // nothing about it looks wrong until somebody reads a drawing.
            if (!string.IsNullOrWhiteSpace(c.AssignedName) && hit.ElementName != null &&
                !string.Equals(c.AssignedName, hit.ElementName, StringComparison.Ordinal))
                audit.Findings.Add(new CadFinding
                {
                    Code = c.ProposedKind == "room" ? CadFindingCode.RoomNameDiffers
                                                    : CadFindingCode.GridNameDiffers,
                    Severity = Review,
                    CandidateId = c.Id,
                    SemanticId = c.SemanticId,
                    ElementId = hit.ElementId,
                    Says = "the requirement set calls this '" + c.AssignedName + "' and the model calls it '" +
                           hit.ElementName + "'. A drawing carries no text this bridge can read, so the set is " +
                           "the only place either name could have come from - which means one of them was " +
                           "changed by hand.",
                    Evidence = new JObject
                    {
                        ["set_says"] = c.AssignedName,
                        ["model_says"] = hit.ElementName,
                        ["named_on"] = c.NamedOn
                    }
                });

            if (!string.IsNullOrWhiteSpace(c.AssignedNumber) && hit.ElementNumber != null &&
                !string.Equals(c.AssignedNumber, hit.ElementNumber, StringComparison.Ordinal))
                audit.Findings.Add(new CadFinding
                {
                    Code = CadFindingCode.RoomNumberDiffers,
                    Severity = Review,
                    CandidateId = c.Id,
                    SemanticId = c.SemanticId,
                    ElementId = hit.ElementId,
                    Says = "the requirement set numbers this room '" + c.AssignedNumber + "' and the model " +
                           "numbers it '" + hit.ElementNumber + "'. Revit assigns a number the instant a room " +
                           "is placed, so a room nobody numbered still HAS one.",
                    Evidence = new JObject
                    {
                        ["set_says"] = c.AssignedNumber,
                        ["model_says"] = hit.ElementNumber
                    }
                });

            // THE PARAMETERS THE RULE WRITES. Each one is checked only because a
            // rule asked for it; nothing sweeps an element for values nobody
            // named. Unreadable is its own answer and never a pass.
            foreach (CadParameterWrite write in c.Parameters ?? new List<CadParameterWrite>())
            {
                if (write?.Parameter == null) continue;
                string expected = write.Value?.ToString();
                if (expected == null) continue;

                if (hit.ParametersUnreadable.Contains(write.Parameter, StringComparer.OrdinalIgnoreCase))
                {
                    audit.Findings.Add(new CadFinding
                    {
                        Code = CadFindingCode.ParameterUnreadable,
                        Severity = Review,
                        CandidateId = c.Id, SemanticId = c.SemanticId, ElementId = hit.ElementId,
                        Says = "the rule writes '" + write.Parameter + "' and it could not be read back off " +
                               "this element. An unreadable parameter is not a matching one.",
                        Evidence = new JObject { ["parameter"] = write.Parameter }
                    });
                    continue;
                }

                string actual;
                if (!hit.ParameterValues.TryGetValue(write.Parameter, out actual))
                {
                    audit.Findings.Add(new CadFinding
                    {
                        Code = CadFindingCode.ParameterMissing,
                        Severity = write.Required ? Blocking : Review,
                        CandidateId = c.Id, SemanticId = c.SemanticId, ElementId = hit.ElementId,
                        Says = "the rule writes '" + write.Parameter + "' and this element does not carry it. " +
                               "The value the set declares has nowhere to go.",
                        Evidence = new JObject
                        {
                            ["parameter"] = write.Parameter,
                            ["rule_says"] = expected,
                            ["required"] = write.Required
                        }
                    });
                    continue;
                }

                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                    audit.Findings.Add(new CadFinding
                    {
                        Code = CadFindingCode.ParameterDiffers,
                        Severity = Review,
                        CandidateId = c.Id, SemanticId = c.SemanticId, ElementId = hit.ElementId,
                        Says = "the rule writes '" + write.Parameter + "' as '" + expected +
                               "' and the element reads '" + actual + "'. Either the set changed or somebody " +
                               "edited the element.",
                        Evidence = new JObject
                        {
                            ["parameter"] = write.Parameter,
                            ["rule_says"] = expected,
                            ["element_says"] = actual
                        }
                    });
            }

            // THE WRONG SIZE. Null width is unmeasured, never zero: an element
            // nobody can measure is not an element of the wrong thickness.
            double? asked = c.ThicknessMm ?? c.DiameterMm;
            if (asked.HasValue && hit.WidthMm.HasValue &&
                Math.Abs(asked.Value - hit.WidthMm.Value) > Math.Max(tolerance, 1.0))
                audit.Findings.Add(new CadFinding
                {
                    Code = "size_differs",
                    Severity = Review,
                    CandidateId = c.Id,
                    SemanticId = c.SemanticId,
                    ElementId = hit.ElementId,
                    Says = "this is the element the drawing means and it is the WRONG SIZE: the reading says " +
                           asked.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm and the element " +
                           "measures " + hit.WidthMm.Value.ToString("0.#", CultureInfo.InvariantCulture) +
                           " mm. Every quantity taken off this model carries the second number.",
                    Evidence = new JObject
                    {
                        ["drawing_says_mm"] = Math.Round(asked.Value, 3),
                        ["element_measures_mm"] = Math.Round(hit.WidthMm.Value, 3),
                        ["tolerance_mm"] = tolerance
                    }
                });
        }

        /// <summary>
        /// Two names for one type. Revit reports an instance's type as the type
        /// name alone and a requirement set may write it either way round, so
        /// "Family: Type" and "Type" are the same answer - and reporting them as
        /// a disagreement would put a finding on every element in the model.
        /// </summary>
        private static bool SameTypeName(string wanted, string actual)
        {
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(wanted)) return false;
            if (string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase)) return true;
            int colon = wanted.IndexOf(':');
            if (colon >= 0 && string.Equals(wanted.Substring(colon + 1).Trim(), actual,
                                            StringComparison.OrdinalIgnoreCase)) return true;
            colon = actual.IndexOf(':');
            if (colon >= 0 && string.Equals(actual.Substring(colon + 1).Trim(), wanted,
                                            StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static CadAuditSubject FirstFree(Dictionary<string, List<CadAuditSubject>> index, string key,
                                                 HashSet<long> claimed)
        {
            return FirstFree(index, key, claimed, null);
        }

        /// <summary>
        /// The first unclaimed element under this key, optionally restricted to
        /// the drawing being audited.
        ///
        /// The revision rung folds the source into its key already, so it needs no
        /// restriction. The SEMANTIC and GEOMETRY rungs do not: their keys are the
        /// layer and the shape, and two sibling drawings that draw the same thing
        /// on the same layer - which is what a plan repeated on two storeys IS -
        /// therefore collide. Unrestricted, an audit of one drawing reported
        /// another drawing's element as 'reissued', and then reported the element
        /// actually built from this drawing as belonging to somebody else.
        /// </summary>
        private static CadAuditSubject FirstFree(Dictionary<string, List<CadAuditSubject>> index, string key,
                                                 HashSet<long> claimed, string onlyFromSource)
        {
            List<CadAuditSubject> bucket;
            if (string.IsNullOrEmpty(key) || !index.TryGetValue(key, out bucket)) return null;
            // AN ELEMENT THAT RECORDS NO SOURCE IS NOBODY'S, and this used to admit
            // it - while the incremental update, on the same element and the same
            // drawing, refused it. Two answers to "whose is this" in two files: the
            // audit called such an element this drawing's re-issue and the update
            // called it missing, in the same model, in the same minute. The
            // update's rule is the right one, because "we do not know where this
            // came from" is not evidence that it came from here.
            return bucket.FirstOrDefault(s =>
                !claimed.Contains(s.ElementId) &&
                (string.IsNullOrEmpty(onlyFromSource) ||
                 string.Equals(s.Provenance?.SourceFileSha256, onlyFromSource, StringComparison.Ordinal)));
        }

        private static void Record(CadAudit audit, CadCandidate c, CadAuditSubject hit, string rung,
                                   HashSet<long> claimed, double tolerance)
        {
            claimed.Add(hit.ElementId);
            CadDeviation d = Deviation(c, hit);
            audit.Matches.Add(new CadMatch
            {
                CandidateId = c.Id, SemanticId = c.SemanticId,
                ElementId = hit.ElementId, MatchedOn = rung,
                OffsetMm = d.Offset, ExtentMm = d.Extent
            });

            // A match is not agreement. The element may be the right one and
            // still be in the wrong PLACE - somebody nudged it, or the drawing
            // moved and the provenance did not.
            if (d.Offset.HasValue && d.Offset.Value > tolerance)
                audit.Findings.Add(new CadFinding
                {
                    Code = "moved",
                    Severity = Review,
                    CandidateId = c.Id,
                    SemanticId = c.SemanticId,
                    ElementId = hit.ElementId,
                    Says = "this IS the element the drawing means - matched on " + rung + " - but it sits " +
                           d.Offset.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm OFF the line the " +
                           "drawing draws, against a tolerance of " +
                           tolerance.ToString("0.###", CultureInfo.InvariantCulture) + " mm. Somebody moved one " +
                           "of the two, and only they know which is right.",
                    Evidence = new JObject
                    {
                        ["offset_mm"] = Math.Round(d.Offset.Value, 3),
                        ["tolerance_mm"] = tolerance
                    }
                });

            // WHAT THE ELEMENT IS, not only where it is.
            //
            // Everything above compares POSITION, and a model can agree with a
            // drawing about every coordinate while being made of the wrong
            // things. These four ask the rest of the question, and each one is a
            // difference somebody has to see:
            //
            //   a door with no host at all - the failure the whole hosting path
            //   exists to prevent, and the one that looks perfect in plan;
            //   a door in the wrong wall;
            //   an element of a type the rule did not ask for;
            //   and a run of the wrong size.
            //
            // Each is reported only where the rule actually SAID something. A set
            // that names no type is not disagreeing about the type.
            AuditSubstance(audit, c, hit, tolerance);

            // A difference ALONG the line is a different thing, and usually not a
            // fault at all: Revit joins walls that meet and pulls each location
            // curve back to the intersection of the centrelines, which is half
            // the other wall's thickness. Reporting that as a move made every
            // corner in a correctly built model look wrong.
            if (!d.Extent.HasValue || d.Extent.Value <= tolerance) return;

            double joinAllowance = (c.ThicknessMm ?? 0);
            bool aJoinExplainsIt = d.Extent.Value <= joinAllowance + tolerance;
            audit.Findings.Add(new CadFinding
            {
                Code = "extent_differs",
                Severity = aJoinExplainsIt ? Informational : Review,
                CandidateId = c.Id,
                SemanticId = c.SemanticId,
                ElementId = hit.ElementId,
                Says = "this element is ON the line the drawing draws, and its ends differ by " +
                       d.Extent.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm along it" +
                       (aJoinExplainsIt
                           ? ". A Revit wall join pulls each location curve back to where the centrelines cross, " +
                             "by up to the thickness of the wall it meets (" +
                             joinAllowance.ToString("0.#", CultureInfo.InvariantCulture) + " mm here), so this is " +
                             "what a correctly built corner looks like rather than a disagreement."
                           : ", which is more than a join of this thickness can account for (" +
                             joinAllowance.ToString("0.#", CultureInfo.InvariantCulture) + " mm). It has been " +
                             "trimmed, extended or drawn to a different length."),
                Evidence = new JObject
                {
                    ["extent_mm"] = Math.Round(d.Extent.Value, 3),
                    ["offset_mm"] = d.Offset.HasValue ? (JToken)Math.Round(d.Offset.Value, 3) : JValue.CreateNull(),
                    ["join_allowance_mm"] = Math.Round(joinAllowance, 3),
                    ["tolerance_mm"] = tolerance
                }
            });
        }

        /// <summary>
        /// Two DIFFERENT numbers, because they mean different things.
        ///
        /// MEASURED live, 2026-08-27: every wall the bridge itself had just built
        /// from the drawing was reported as standing 176.2 mm from where the
        /// drawing puts it - exactly half the wall thickness, at every corner.
        /// Nothing had moved. Revit JOINS walls that meet, and a join pulls each
        /// location curve back to the intersection of the centrelines. A single
        /// distance could not tell that apart from somebody nudging a wall, so
        /// the audit called a correct model wrong three times out of three, and
        /// an audit that cries wolf on its own output is worse than none.
        ///
        /// OFFSET is perpendicular: does the element sit ON the line the drawing
        /// draws? That is what "moved" means.
        ///
        /// EXTENT is along that line: does it span the same run? A join, a trim
        /// or an extend changes this and nothing else.
        ///
        /// NULL IS NOT ZERO. "We could not measure it" and "it is exactly right"
        /// are opposite answers and must never share a value.
        /// </summary>
        private struct CadDeviation
        {
            public double? Offset;
            public double? Extent;
            public double Overlap;      // fraction of the drawing's run the element covers
            public bool Measured;
        }

        private static CadDeviation Deviation(CadCandidate c, CadAuditSubject s)
        {
            var d = new CadDeviation();
            if (c == null || s == null) return d;

            // AN ARC IS COMPARED AS AN ARC.
            //
            // Perpendicular offset from a straight line is meaningless for a curve,
            // and comparing two arcs by their endpoints alone matches a minor arc to
            // a major one. What separates them is the centre and the radius, so those
            // are what is measured - and OFFSET keeps its meaning: how far the built
            // curve is from where the drawing puts it.
            if (c.Arc != null && s.ArcCentre.HasValue && s.ArcRadiusMm.HasValue)
            {
                double centreOff = c.Arc.Centre.PlanDistanceTo(s.ArcCentre.Value);
                double radiusOff = Math.Abs(c.Arc.RadiusMm - s.ArcRadiusMm.Value);
                d.Offset = Math.Max(centreOff, radiusOff);
                d.Extent = ArcExtentDifference(c, s);
                d.Overlap = 1;
                d.Measured = true;
                return d;
            }
            if (c.Arc != null && !s.ArcCentre.HasValue)
            {
                // The drawing says a curve and the element is not one. That is a
                // real disagreement, and reporting it as an unmeasurable pair
                // would hide it - so it is measured as maximally off the line.
                d.Offset = double.MaxValue;
                d.Measured = true;
                d.Overlap = 0;
                return d;
            }
            if (c.Geometry == null || s.Geometry == null) return d;
            if (c.Geometry.Count == 0 || s.Geometry.Count == 0) return d;

            // A point against anything, or anything against a point: there is no
            // line to be off, so the whole difference is offset.
            if (c.Geometry.Count == 1 || s.Geometry.Count == 1)
            {
                d.Offset = c.Geometry[0].PlanDistanceTo(s.Geometry[0]);
                d.Measured = true;
                return d;
            }

            CadPoint c0 = c.Geometry[0], c1 = c.Geometry[c.Geometry.Count - 1];
            CadPoint a = s.Geometry[0], b = s.Geometry[s.Geometry.Count - 1];

            double dx = c1.X - c0.X, dy = c1.Y - c0.Y;
            double runLength = Math.Sqrt(dx * dx + dy * dy);
            if (runLength <= 1e-9)
            {
                d.Offset = c0.PlanDistanceTo(a);
                d.Measured = true;
                return d;
            }
            double ux = dx / runLength, uy = dy / runLength;
            double nx = -uy, ny = ux;

            Func<CadPoint, double> along = p => (p.X - c0.X) * ux + (p.Y - c0.Y) * uy;
            Func<CadPoint, double> off = p => Math.Abs((p.X - c0.X) * nx + (p.Y - c0.Y) * ny);

            double ta = along(a), tb = along(b);
            // Either way round: a wall drawn left to right and built right to
            // left is the same wall.
            double straight = Math.Abs(ta - 0) + Math.Abs(tb - runLength);
            double swapped = Math.Abs(tb - 0) + Math.Abs(ta - runLength);
            if (swapped < straight) { double t = ta; ta = tb; tb = t; }

            d.Offset = Math.Max(off(a), off(b));
            d.Extent = Math.Max(Math.Abs(ta - 0), Math.Abs(tb - runLength));

            double lo = Math.Max(0, Math.Min(ta, tb));
            double hi = Math.Min(runLength, Math.Max(ta, tb));
            d.Overlap = runLength <= 0 ? 0 : Math.Max(0, hi - lo) / runLength;
            d.Measured = true;
            return d;
        }

        /// <summary>
        /// The nearest element that is ON the drawing's line and actually runs
        /// along it. Offset decides, not endpoint distance - otherwise a wall
        /// correctly joined at both corners never matches its own drawing - and
        /// an overlap floor stops a stub somewhere else on the same infinite
        /// line from being called the same wall.
        /// </summary>
        /// <summary>
        /// How much of the arc is missing or extra, along the curve, in mm. A join
        /// trims a curved wall exactly as it trims a straight one, so this is the
        /// same distinction as extent on a line: on the drawing's curve, but not
        /// spanning the same run of it.
        /// </summary>
        private static double? ArcExtentDifference(CadCandidate c, CadAuditSubject s)
        {
            if (c?.Arc == null || s?.Geometry == null || s.Geometry.Count < 2) return null;
            if (!s.ArcRadiusMm.HasValue || s.ArcRadiusMm.Value <= 0) return null;
            double drawnLength = c.Arc.RadiusMm * c.Arc.SweepRadians;

            // The built arc's own sweep, from its ends about its own centre.
            CadPoint centre = s.ArcCentre.Value;
            double a0 = Math.Atan2(s.Geometry[0].Y - centre.Y, s.Geometry[0].X - centre.X);
            double a1 = Math.Atan2(s.Geometry[s.Geometry.Count - 1].Y - centre.Y,
                                   s.Geometry[s.Geometry.Count - 1].X - centre.X);
            double sweep = Math.Abs(a1 - a0);
            while (sweep > 2 * Math.PI) sweep -= 2 * Math.PI;
            if (sweep > Math.PI && c.Arc.SweepRadians <= Math.PI) sweep = 2 * Math.PI - sweep;
            double builtLength = s.ArcRadiusMm.Value * sweep;
            return Math.Abs(drawnLength - builtLength);
        }

        private static CadAuditSubject NearestFree(List<CadAuditSubject> pool, CadCandidate c, double bound,
                                                   HashSet<long> claimed, out double deviation)
        {
            deviation = 0;
            CadAuditSubject best = null;
            double bestD = double.MaxValue;
            foreach (CadAuditSubject s in pool)
            {
                if (claimed.Contains(s.ElementId)) continue;
                CadDeviation d = Deviation(c, s);
                if (!d.Measured || !d.Offset.HasValue || d.Offset.Value > bound) continue;
                // A line must overlap the drawing's run; a point has no run to overlap.
                if (d.Extent.HasValue && d.Overlap < 0.5) continue;
                if (d.Offset.Value < bestD) { bestD = d.Offset.Value; best = s; }
            }
            if (best != null) deviation = bestD;
            return best;
        }
    }
}
