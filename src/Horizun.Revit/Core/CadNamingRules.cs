// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// NAMES A DRAWING CANNOT SUPPLY.
//
// MEASURED on Revit 2026: no string is reachable from imported DWG geometry at
// any depth. Text arrives as curves on its own layer - the layer name survives,
// the words do not. A grid bubble reading "A" is, to this bridge, a few arcs.
//
// So the names come from the requirement set, and this file decides which name
// belongs to which candidate. Everything here is arithmetic over geometry and a
// declared table, which is why it lives in Core and can be argued with at a desk
// rather than only against a running Revit.
//
// THE ONE RULE THAT MATTERS: a candidate this file cannot name does not get a
// name. There is no fallback to enumeration order, because enumeration order is
// whatever Revit happened to return first - not stable between runs, let alone
// between machines - and a grid named that way puts the wrong reference on every
// dimension drawn from it, silently.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What a naming pass decided, including everything it could not decide.</summary>
    public sealed class CadNamingOutcome
    {
        /// <summary>Candidate semantic id to the name it was given.</summary>
        public Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Candidate semantic id to the number it was given, where the strategy supplies one.</summary>
        public Dictionary<string, string> Numbers = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Per candidate, what it was named ON - the evidence a reviewer reads.</summary>
        public Dictionary<string, string> Evidence = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Candidates no strategy names.</summary>
        public List<string> Unnamed = new List<string>();
        /// <summary>Names the set supplies that nothing in the drawing matched.</summary>
        public List<string> Unmatched = new List<string>();
        /// <summary>Everything that makes this reading unusable as it stands.</summary>
        public List<string> Problems = new List<string>();
        /// <summary>The candidates in the order the strategy put them, for a reader to check.</summary>
        public List<string> CanonicalOrder = new List<string>();

        public bool Refused => Problems.Count > 0;

        public JObject ToJson(CadNaming naming)
        {
            var o = new JObject
            {
                ["strategy"] = naming?.Strategy,
                ["assigned"] = Names.Count,
                ["unnamed"] = new JArray(Unnamed),
                ["unmatched_names"] = new JArray(Unmatched),
                ["problems"] = new JArray(Problems),
                ["canonical_order"] = new JArray(CanonicalOrder)
            };
            var rows = new JArray();
            foreach (var kv in Names.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var row = new JObject { ["semantic_id"] = kv.Key, ["name"] = kv.Value };
                string number;
                if (Numbers.TryGetValue(kv.Key, out number)) row["number"] = number;
                string why;
                if (Evidence.TryGetValue(kv.Key, out why)) row["named_on"] = why;
                rows.Add(row);
            }
            o["names"] = rows;
            return o;
        }
    }

    public static class CadNamingRules
    {
        /// <summary>
        /// Give each candidate the name the requirement set says it has.
        ///
        /// <paramref name="existingNames"/> is what the DOCUMENT already holds
        /// for this category. A grid name must be unique, so a set that asks for
        /// one the model already has is refused HERE, before anything is built -
        /// Revit would otherwise take the whole batch down after creating half of
        /// it.
        /// </summary>
        public static CadNamingOutcome Assign(CadNaming naming, IList<CadCandidate> candidates,
                                              double pointToleranceMm,
                                              IEnumerable<string> existingNames = null)
        {
            var outcome = new CadNamingOutcome();
            if (naming == null || candidates == null) return outcome;

            List<CadCandidate> named = candidates
                .Where(c => c != null && !string.IsNullOrEmpty(c.SemanticId) &&
                            c.Geometry != null && c.Geometry.Count > 0)
                .ToList();

            switch (naming.Strategy)
            {
                case "ordered": Ordered(naming, named, outcome, pointToleranceMm); break;
                case "by_semantic_id": BySemanticId(naming, named, outcome); break;
                case "by_position": ByPosition(naming, named, outcome); break;
                default:
                    outcome.Problems.Add("naming strategy '" + (naming.Strategy ?? "(none)") +
                                         "' is not one this bridge knows.");
                    return outcome;
            }

            // COLLISION INSIDE THIS PLAN. Two candidates given one name is a
            // model Revit will refuse halfway through building.
            var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in outcome.Names)
            {
                List<string> bucket;
                if (!byName.TryGetValue(kv.Value, out bucket)) byName[kv.Value] = bucket = new List<string>();
                bucket.Add(kv.Key);
            }
            foreach (var kv in byName.Where(x => x.Value.Count > 1))
                outcome.Problems.Add("the name '" + kv.Key + "' was assigned to " + kv.Value.Count +
                                     " candidates. A name identifies one thing or it identifies nothing.");

            // AND THE SAME FOR NUMBERS, which had no check of any kind.
            //
            // A room NUMBER is the one identity Revit genuinely requires to be
            // unique, and it was the only one nothing here looked at: not against
            // the other candidates, not against the model. Worse, Revit treats a
            // duplicate room number as a WARNING - the commit stands, the read-back
            // agrees with itself, and the row reports number_verified: true while
            // the document carries two rooms numbered 101 and every schedule
            // double-counts.
            var byNumber = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in outcome.Numbers)
            {
                List<string> bucket;
                if (!byNumber.TryGetValue(kv.Value, out bucket)) byNumber[kv.Value] = bucket = new List<string>();
                bucket.Add(kv.Key);
            }
            foreach (var kv in byNumber.Where(x => x.Value.Count > 1))
                outcome.Problems.Add("the number '" + kv.Key + "' was assigned to " + kv.Value.Count +
                                     " candidates. Revit takes a duplicate room number as a warning rather " +
                                     "than a refusal, so this would be built, would verify, and would " +
                                     "double-count in every schedule.");

            // COLLISION WITH THE MODEL. Checked before anything is built, because
            // Revit refuses the name at creation and takes the batch with it.
            if (existingNames != null)
            {
                var already = new HashSet<string>(existingNames.Where(x => !string.IsNullOrEmpty(x)),
                                                  StringComparer.OrdinalIgnoreCase);
                foreach (var kv in outcome.Names.OrderBy(k => k.Key, StringComparer.Ordinal))
                    if (already.Contains(kv.Value))
                        outcome.Problems.Add("the model already holds something called '" + kv.Value +
                                             "'. Revit refuses a duplicate name at creation, so this would " +
                                             "fail after building part of the batch.");
                foreach (var kv in outcome.Numbers.OrderBy(k => k.Key, StringComparer.Ordinal))
                    if (already.Contains(kv.Value))
                        outcome.Problems.Add("the model already holds something numbered '" + kv.Value +
                                             "'. Revit does not refuse a duplicate number - it warns, builds " +
                                             "it, and lets every schedule count it twice.");
            }

            // WHAT WAS NOT NAMED, and what the rule said to do about it.
            foreach (CadCandidate c in named)
                if (!outcome.Names.ContainsKey(c.SemanticId)) outcome.Unnamed.Add(c.SemanticId);

            if (outcome.Unnamed.Count > 0 && naming.OnUnnamed == "refuse")
                outcome.Problems.Add(outcome.Unnamed.Count + " candidate(s) matched no name, and this rule " +
                                     "says on_unnamed=refuse. Naming some of a grid line-up and not the rest " +
                                     "is worse than naming none of it.");

            if (outcome.Unmatched.Count > 0 && naming.OnUnmatched == "refuse")
                outcome.Problems.Add(outcome.Unmatched.Count + " name(s) matched nothing in the drawing (" +
                                     string.Join(", ", outcome.Unmatched.Take(6)) +
                                     "), and this rule says on_unmatched=refuse. That usually means the " +
                                     "drawing changed under the set.");
            return outcome;
        }

        // ---------------------------------------------------------- ordered

        private static void Ordered(CadNaming naming, List<CadCandidate> candidates,
                                    CadNamingOutcome outcome, double pointToleranceMm)
        {
            double tolerance = naming.OrderToleranceMm ?? Math.Max(pointToleranceMm, 1.0);

            var keyed = candidates
                .Select(c => new { Candidate = c, Key = OrderKey(c, naming.Axis) })
                .Where(x => x.Key.HasValue)
                .ToList();

            foreach (CadCandidate c in candidates)
                if (!OrderKey(c, naming.Axis).HasValue)
                    outcome.Problems.Add("a candidate has no readable " + naming.Axis +
                                         " to order by, so this line-up cannot be numbered.");

            // ORDER, and then check that the order MEANS anything. Two grids at
            // the same coordinate have no first, and calling one of them "1" is
            // picking whichever Revit returned first.
            List<double> sortedKeys = keyed.Select(x => x.Key.Value).OrderBy(v => v).ToList();
            for (int i = 1; i < sortedKeys.Count; i++)
                if (Math.Abs(sortedKeys[i] - sortedKeys[i - 1]) <= tolerance)
                {
                    outcome.Problems.Add("two candidates are " +
                        Math.Abs(sortedKeys[i] - sortedKeys[i - 1]).ToString("0.##", CultureInfo.InvariantCulture) +
                        " mm apart along " + naming.Axis + ", within the ordering tolerance of " +
                        tolerance.ToString("0.##", CultureInfo.InvariantCulture) +
                        " mm. There is no first one, so an ordered naming would be picking whichever the " +
                        "reading happened to return first.");
                    break;
                }

            var ordered = naming.Direction == "descending"
                ? keyed.OrderByDescending(x => x.Key.Value).ThenBy(x => x.Candidate.SemanticId, StringComparer.Ordinal).ToList()
                : keyed.OrderBy(x => x.Key.Value).ThenBy(x => x.Candidate.SemanticId, StringComparer.Ordinal).ToList();

            foreach (var x in ordered) outcome.CanonicalOrder.Add(x.Candidate.SemanticId);

            if (ordered.Count != naming.Values.Count)
                outcome.Problems.Add("the drawing produced " + ordered.Count + " candidate(s) and the set " +
                                     "supplies " + naming.Values.Count + " name(s). An ordered naming that " +
                                     "runs out shifts every name after the gap, so nothing is named until " +
                                     "the two agree.");

            int take = Math.Min(ordered.Count, naming.Values.Count);
            for (int i = 0; i < take; i++)
            {
                string id = ordered[i].Candidate.SemanticId;
                outcome.Names[id] = naming.Values[i];
                outcome.Evidence[id] = "position " + (i + 1) + " of " + ordered.Count + " along " +
                                       naming.Axis + " " + naming.Direction + ", at " +
                                       ordered[i].Key.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm";
            }
            for (int i = take; i < naming.Values.Count; i++) outcome.Unmatched.Add(naming.Values[i]);
        }

        /// <summary>The coordinate a candidate is ordered by, or null when it has none.</summary>
        private static double? OrderKey(CadCandidate c, string axis)
        {
            if (c?.Geometry == null || c.Geometry.Count == 0) return null;
            double x = c.Geometry.Average(p => p.X);
            double y = c.Geometry.Average(p => p.Y);
            switch (axis)
            {
                case "x": return x;
                case "y": return y;
                case "distance_from_origin": return Math.Sqrt(x * x + y * y);
                default: return null;
            }
        }

        // -------------------------------------------------- by_semantic_id

        private static void BySemanticId(CadNaming naming, List<CadCandidate> candidates,
                                         CadNamingOutcome outcome)
        {
            var present = new HashSet<string>(candidates.Select(c => c.SemanticId), StringComparer.Ordinal);
            foreach (CadCandidate c in candidates.OrderBy(c => c.SemanticId, StringComparer.Ordinal))
            {
                outcome.CanonicalOrder.Add(c.SemanticId);
                string name;
                if (!naming.BySemanticId.TryGetValue(c.SemanticId, out name)) continue;
                outcome.Names[c.SemanticId] = name;
                outcome.Evidence[c.SemanticId] = "mapped by semantic id, which survives a re-issue of the file";
            }
            foreach (var kv in naming.BySemanticId.OrderBy(k => k.Key, StringComparer.Ordinal))
                if (!present.Contains(kv.Key)) outcome.Unmatched.Add(kv.Value);
        }

        // ----------------------------------------------------- by_position

        private static void ByPosition(CadNaming naming, List<CadCandidate> candidates,
                                       CadNamingOutcome outcome)
        {
            foreach (CadCandidate c in candidates.OrderBy(c => c.SemanticId, StringComparer.Ordinal))
                outcome.CanonicalOrder.Add(c.SemanticId);

            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (CadNamedPosition pos in naming.ByPosition)
            {
                var within = new List<CadCandidate>();
                foreach (CadCandidate c in candidates)
                {
                    if (claimed.Contains(c.SemanticId)) continue;
                    if (!Near(c, pos)) continue;
                    within.Add(c);
                }

                if (within.Count == 0)
                {
                    outcome.Unmatched.Add(pos.Name ?? pos.Number);
                    continue;
                }
                if (within.Count > 1)
                {
                    // TWO THINGS AT ONE DECLARED POSITION. Choosing between them
                    // is exactly the judgement this file must not make.
                    outcome.Problems.Add("the declared position for '" + (pos.Name ?? pos.Number) + "' has " +
                                         within.Count + " candidates within " +
                                         pos.ToleranceMm.ToString("0.##", CultureInfo.InvariantCulture) +
                                         " mm of it. Tighten the tolerance or name them by semantic id.");
                    continue;
                }

                CadCandidate hit = within[0];
                claimed.Add(hit.SemanticId);
                if (pos.Name != null) outcome.Names[hit.SemanticId] = pos.Name;
                if (pos.Number != null) outcome.Numbers[hit.SemanticId] = pos.Number;
                outcome.Evidence[hit.SemanticId] = "within " +
                    pos.ToleranceMm.ToString("0.##", CultureInfo.InvariantCulture) + " mm of a declared position";
            }
        }

        private static bool Near(CadCandidate c, CadNamedPosition pos)
        {
            if (c?.Geometry == null || c.Geometry.Count == 0) return false;
            double x = c.Geometry.Average(p => p.X);
            double y = c.Geometry.Average(p => p.Y);
            if (pos.X.HasValue && Math.Abs(x - pos.X.Value) > pos.ToleranceMm) return false;
            if (pos.Y.HasValue && Math.Abs(y - pos.Y.Value) > pos.ToleranceMm) return false;
            return true;
        }
    }
}
