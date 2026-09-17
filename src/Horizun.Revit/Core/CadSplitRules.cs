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

        public JObject ToJson() => new JObject
        {
            ["element_id"] = ElementId,
            ["category"] = Category,
            ["along_mm"] = new JArray(Math.Round(Lo, 1), Math.Round(Hi, 1)),
            ["class"] = Class,
            ["target_candidate_id"] = TargetCandidateId,
            ["alternatives"] = Alternatives.Count == 0 ? null : new JArray(Alternatives)
        };
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
