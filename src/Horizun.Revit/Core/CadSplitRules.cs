// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code.
//
// WHERE EACH DEPENDENT OF A SPLIT WALL GOES.
//
// A wall the drawing now shows as several pieces keeps its id on one of them; what
// it hosts (devices, and in other models doors and windows) stands somewhere along
// its old line. Each dependent is classified by its extent along that line against
// the pieces, never by a guess:
//   stays          wholly inside the piece that keeps the element
//   moves_to       wholly inside exactly one NEW piece
//   in_gap         inside no piece (the drawing removed that stretch)
//   ambiguous      across a piece boundary, or within tolerance of two pieces
//   unsupported    a class this build cannot re-create on another host
// Only the first two are carried out; the rest hold the split with alternatives.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class CadSplitPiece
    {
        public string CandidateId;
        public double Lo, Hi;
        public double? ThicknessMm;
        public bool KeepsTheElement;
    }

    public sealed class CadSplitDependent
    {
        public long ElementId;
        public string Category;
        public double Lo, Hi;
        public bool Recreatable = true;
        public string Class;
        public string TargetCandidateId;
        public List<string> Alternatives = new List<string>();
        /// <summary>What a person's decision must quote to be applied (held dependents only).</summary>
        public string DecisionKey;
        /// <summary>The class before a decision changed it; null when nobody decided.</summary>
        public string DecidedFrom;
        public string Decision;
        /// <summary>How far along the wall the decision moves it (clamped onto its piece), mm.</summary>
        public double? MoveAlongMm;
        public bool Delete;
        /// <summary>A move onto the piece that KEEPS the element: the host is the element itself.</summary>
        public bool TargetIsKept;

        public JObject ToJson() => new JObject
        {
            ["element_id"] = ElementId,
            ["category"] = Category,
            ["along_mm"] = new JArray(Math.Round(Lo, 1), Math.Round(Hi, 1)),
            ["class"] = Class,
            ["target_candidate_id"] = TargetCandidateId,
            ["target_is_kept"] = TargetIsKept ? (bool?)true : null,
            ["alternatives"] = Alternatives.Count == 0 ? null : new JArray(Alternatives),
            ["decision_key"] = DecisionKey,
            ["decided_from"] = DecidedFrom,
            ["decision"] = Decision,
            ["move_along_mm"] = MoveAlongMm.HasValue ? Math.Round(MoveAlongMm.Value, 1) : (double?)null,
            ["delete"] = Delete ? (bool?)true : null
        };
    }

    /// <summary>A person's answer for one held dependent of a split.</summary>
    public sealed class CadDependentDecision
    {
        public long ElementId;
        /// <summary>stay | move_to | delete</summary>
        public string Decision;
        /// <summary>The piece (candidate id) for move_to.</summary>
        public string Piece;
        /// <summary>The decision_key the proposal gave this dependent.</summary>
        public string Key;
        public bool Used;
    }

    public static class CadSplitRules
    {
        public const string Stays = "stays";
        public const string MovesTo = "moves_to";
        public const string InGap = "in_gap";
        public const string Ambiguous = "ambiguous";
        public const string Unsupported = "unsupported";

        /// <summary>Classify every dependent against the pieces; the pieces are intervals along the old line.</summary>
        public static void Classify(IList<CadSplitPiece> pieces, IList<CadSplitDependent> dependents, double tolMm)
        {
            foreach (CadSplitDependent d in dependents)
            {
                var holding = pieces.Where(p => d.Lo >= p.Lo - tolMm && d.Hi <= p.Hi + tolMm).ToList();
                var touching = pieces.Where(p => d.Hi > p.Lo - tolMm && d.Lo < p.Hi + tolMm).ToList();
                if (holding.Count == 1 && touching.Count == 1)
                {
                    CadSplitPiece p = holding[0];
                    if (p.KeepsTheElement) { d.Class = Stays; continue; }
                    if (!d.Recreatable)
                    {
                        d.Class = Unsupported;
                        d.Alternatives.Add("keep the element on piece '" + p.CandidateId + "' (accept that pairing instead)");
                        d.Alternatives.Add("re-create the " + d.Category + " by hand on the new piece");
                        continue;
                    }
                    d.Class = MovesTo;
                    d.TargetCandidateId = p.CandidateId;
                    continue;
                }
                if (touching.Count == 0)
                {
                    d.Class = InGap;
                    d.Alternatives.Add("delete it (resolve: delete on element " + d.ElementId + ")");
                    CadSplitPiece nearest = pieces.OrderBy(p => Math.Min(Math.Abs(p.Lo - d.Hi), Math.Abs(d.Lo - p.Hi))).FirstOrDefault();
                    if (nearest != null)
                        d.Alternatives.Add("move it along its wall onto piece '" + nearest.CandidateId + "'");
                    continue;
                }
                d.Class = Ambiguous;
                foreach (CadSplitPiece p in touching) d.Alternatives.Add("piece '" + p.CandidateId + "'");
            }
        }

        /// <summary>
        /// THE KEY A DECISION MUST QUOTE: the document and revision it was proposed for, the split
        /// element, the dependent, its class and the pieces as they are now. A decision made for another
        /// plan - another drawing set, another document, pieces that moved - quotes another key.
        /// </summary>
        public static string DecisionKey(string context, long splitElementId, CadSplitDependent d,
                                         IList<CadSplitPiece> pieces)
        {
            var parts = new List<string> { context ?? "", splitElementId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                           d.ElementId.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Class ?? "" };
            parts.AddRange(pieces.OrderBy(p => p.CandidateId, StringComparer.Ordinal)
                                 .Select(p => p.CandidateId + "@" + Math.Round(p.Lo, 0).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                              ":" + Math.Round(p.Hi, 0).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                              (p.KeepsTheElement ? "*" : "")));
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts)));
                return "cadsplitdec:" + BitConverter.ToString(h, 0, 12).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Apply a person's decisions to the dependents a split HELD. Every held dependent gets its key;
        /// a decision is applied only when it quotes that key and names an allowed outcome. What is
        /// returned is every decision that cannot stand - a refusal, never a skip.
        /// </summary>
        public static List<string> ApplyDecisions(IList<CadSplitPiece> pieces, IList<CadSplitDependent> dependents,
                                                  IList<CadDependentDecision> decisions, string context,
                                                  long splitElementId, double tolMm)
        {
            var problems = new List<string>();
            foreach (CadSplitDependent d in dependents)
            {
                if (d.Class == Stays || d.Class == MovesTo) continue;
                d.DecisionKey = DecisionKey(context, splitElementId, d, pieces);
                CadDependentDecision dec = decisions?.FirstOrDefault(x => x.ElementId == d.ElementId && !x.Used);
                if (dec == null) continue;
                dec.Used = true;
                if (!string.Equals(dec.Key, d.DecisionKey, StringComparison.Ordinal))
                {
                    problems.Add("stale_decision: element " + d.ElementId + " was decided for key '" + dec.Key +
                                 "' and its proposal now carries '" + d.DecisionKey + "' - another plan, document, revision " +
                                 "or set of pieces. Plan again and decide on what it proposes.");
                    continue;
                }
                CadSplitPiece kept = pieces.FirstOrDefault(p => p.KeepsTheElement);
                CadSplitPiece target = null;
                switch (dec.Decision)
                {
                    case "delete":
                        d.DecidedFrom = d.Class; d.Decision = "delete"; d.Delete = true; d.Class = "deleted_by_decision";
                        continue;
                    case "stay":
                        target = kept;
                        break;
                    case "move_to":
                        target = pieces.FirstOrDefault(p => string.Equals(p.CandidateId, dec.Piece, StringComparison.Ordinal));
                        if (target == null)
                        {
                            problems.Add("unknown_piece: element " + d.ElementId + " was sent to piece '" + dec.Piece +
                                         "', which is not one of this split's pieces (" +
                                         string.Join(", ", pieces.Select(p => p.CandidateId)) + ").");
                            continue;
                        }
                        break;
                    default:
                        problems.Add("unknown_decision: '" + dec.Decision + "' for element " + d.ElementId +
                                     " (stay, move_to or delete).");
                        continue;
                }
                if (target == null)
                {
                    problems.Add("no_kept_piece: element " + d.ElementId + " cannot stay: no piece keeps the element.");
                    continue;
                }
                if (!d.Recreatable && !target.KeepsTheElement)
                {
                    problems.Add("cannot_recreate: element " + d.ElementId + " (" + d.Category + ") cannot be re-created " +
                                 "on another host by this build; it can stay on the kept piece or be deleted.");
                    continue;
                }
                // CLAMPED ONTO ITS PIECE: the whole of it, with the tolerance, and moved as little as that takes.
                double half = (d.Hi - d.Lo) / 2.0, centre = (d.Lo + d.Hi) / 2.0;
                double lo = target.Lo + half + tolMm, hi = target.Hi - half - tolMm;
                if (lo > hi)
                {
                    problems.Add("piece_too_short: element " + d.ElementId + " is " + Math.Round(d.Hi - d.Lo, 1) +
                                 " mm long and piece '" + target.CandidateId + "' is " + Math.Round(target.Hi - target.Lo, 1) + " mm.");
                    continue;
                }
                double to = Math.Min(Math.Max(centre, lo), hi);
                d.DecidedFrom = d.Class;
                d.Decision = dec.Decision == "stay" ? "stay" : "move_to";
                d.MoveAlongMm = Math.Abs(to - centre) < 0.05 ? (double?)null : to - centre;
                d.TargetIsKept = target.KeepsTheElement;
                d.TargetCandidateId = target.CandidateId;
                // on the kept piece and where it stands: it simply stays; anywhere else it is re-created there
                d.Class = target.KeepsTheElement && !d.MoveAlongMm.HasValue ? Stays : MovesTo;
            }
            return problems;
        }

        /// <summary>
        /// Which piece should keep the element: the one carrying most dependents, then the one
        /// of the element's own thickness, then the longest.
        /// </summary>
        public static CadSplitPiece RecommendKeep(IList<CadSplitPiece> pieces, IList<CadSplitDependent> dependents,
                                                  double elementWidthMm, double widthToleranceMm, double tolMm)
        {
            return pieces
                .OrderByDescending(p => dependents.Count(d => d.Lo >= p.Lo - tolMm && d.Hi <= p.Hi + tolMm))
                .ThenByDescending(p => p.ThicknessMm.HasValue &&
                                       Math.Abs(p.ThicknessMm.Value - elementWidthMm) <= widthToleranceMm)
                .ThenByDescending(p => p.Hi - p.Lo)
                .FirstOrDefault();
        }
    }
}
