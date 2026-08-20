// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// EVERY PROPERTY THAT WAS ASKED FOR, RE-READ FROM THE COMMITTED MODEL AND COMPARED
// ONE BY ONE - and the rule that only a complete, fully measured checklist may be
// called verified.
//
// THE DEFECT THIS EXISTS TO FIX, found in review. create_schedule declared
// verified_applied on a post-condition that compared three of the five things the
// request contained: fields, include_links and itemized. The schedule's NAME and
// its CATEGORY were never re-read - the reply even reported `category` from the
// Category object resolved BEFORE the commit, which is the request talking, not
// the model. A schedule created under a different name, or against a category
// Revit resolved differently, came back as fully verified.
//
// THREE RULES, and each one is a way the old boolean could say true:
//
//   1. AN EMPTY CHECKLIST IS NOT A PASS. `true && true && true` over three checks
//      and `true` over none look identical once they are a bool. A checklist with
//      nothing in it is unverified, always.
//   2. AN UNREADABLE PROPERTY IS NOT A MATCH. If the re-read throws, that property
//      was not measured - which is neither "it is right" nor "it is wrong". It
//      cannot pass, and the reason travels with it.
//   3. THE CHECKLIST IS THE EVIDENCE, not a summary of it. Every comparison is
//      published with what was requested and what the model actually returned, so
//      a caller can see WHICH property failed rather than being told that one did.
//
// Revit-free: the reads need a Document, the rule about what they add up to does
// not, and the cases that matter (a property that could not be read, a checklist
// nobody added to) are exactly the ones a live Revit will not produce on demand.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class PostconditionCheck
    {
        private readonly List<JObject> _checks = new List<JObject>();
        private readonly HashSet<string> _required;
        private readonly List<string> _seen = new List<string>();
        private bool _allMatched = true;
        private bool _allMeasured = true;

        /// <summary>
        /// A checklist that knows WHICH properties it is supposed to cover.
        ///
        /// Without the required set, `Count > 0 && allMeasured && allMatched` proves that
        /// some checks passed - not that the right ones ran. Two ways it says true over an
        /// unverified request, and both are one edit away:
        ///
        ///   * five checks with "name" twice and no "category": five passes, category never
        ///     compared.
        ///   * a check silently deleted from the command: four passes, and the checklist
        ///     has no idea a fifth was ever expected.
        ///
        /// So the expectation lives WITH the checklist rather than in the caller's memory,
        /// and coverage becomes something the type can answer instead of something a reader
        /// has to audit at each call site.
        /// </summary>
        public PostconditionCheck(params string[] required)
        {
            _required = new HashSet<string>(required ?? new string[0], StringComparer.Ordinal);
        }

        /// <summary>How many properties were compared - readable ones and unreadable alike.</summary>
        public int Count => _checks.Count;

        /// <summary>Required properties that were never checked at all.</summary>
        public IEnumerable<string> Missing
        {
            get
            {
                foreach (string required in _required)
                    if (!_seen.Contains(required)) yield return required;
            }
        }

        /// <summary>Properties recorded more than once, or recorded without being required.</summary>
        public IEnumerable<string> Unexpected
        {
            get
            {
                var counted = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string seen in _seen)
                {
                    int n;
                    counted[seen] = counted.TryGetValue(seen, out n) ? n + 1 : 1;
                }
                foreach (var pair in counted)
                    if (pair.Value > 1 || !_required.Contains(pair.Key)) yield return pair.Key;
            }
        }

        private bool CoversExactly
        {
            get
            {
                if (_seen.Count != _required.Count) return false;
                foreach (string s in Missing) return false;
                foreach (string s in Unexpected) return false;
                return true;
            }
        }

        /// <summary>
        /// THE ONLY QUESTION A DECLARATION MAY REST ON. True when the checklist covers
        /// EXACTLY the properties it was told to cover - each one once, none missing, none
        /// substituted - and every one of them was measured and matched.
        ///
        /// An empty checklist is false whether or not anything was required: nothing was
        /// proven, and a vacuous truth is how the old three-of-five boolean read as
        /// complete.
        /// </summary>
        public bool AllVerified => _checks.Count > 0 && CoversExactly && _allMeasured && _allMatched;
        public bool AllMeasured => _checks.Count > 0 && CoversExactly && _allMeasured;

        /// <summary>A comparison whose verdict the caller computed (an ordered field list, a set).</summary>
        public PostconditionCheck Record(string what, JToken requested, JToken found, bool matches)
        {
            _seen.Add(what);
            _checks.Add(new JObject
            {
                ["property"] = what,
                ["measured"] = true,
                // Cloned: a JToken the caller keeps a handle on can be mutated after this
                // returns, and the evidence a reply publishes must be what was compared.
                ["requested"] = Frozen(requested),
                ["found_in_committed_model"] = Frozen(found),
                ["matches"] = matches
            });
            if (!matches) _allMatched = false;
            return this;
        }

        /// <summary>Ordinal string comparison, computed here so a caller cannot get it wrong.</summary>
        public PostconditionCheck Compare(string what, string requested, string found)
            => Record(what, requested, found, string.Equals(requested, found, StringComparison.Ordinal));

        public PostconditionCheck Compare(string what, bool requested, bool found)
            => Record(what, requested, found, requested == found);

        public PostconditionCheck Compare(string what, long requested, long found)
            => Record(what, requested, found, requested == found);

        /// <summary>
        /// The property could not be re-read. NOT a failure and NOT a pass - it is the
        /// absence of a measurement, and it makes the whole checklist unverified with the
        /// reason attached.
        /// </summary>
        public PostconditionCheck Unreadable(string what, JToken requested, string why)
        {
            _seen.Add(what);
            _checks.Add(new JObject
            {
                ["property"] = what,
                ["measured"] = false,
                ["requested"] = Frozen(requested),
                ["found_in_committed_model"] = JValue.CreateNull(),
                ["matches"] = JValue.CreateNull(),
                ["error"] = why
            });
            _allMeasured = false;
            return this;
        }

        /// <summary>The checklist as it goes into the reply, with its own verdict beside it.</summary>
        public JObject ToJson()
        {
            return new JObject
            {
                ["all_verified"] = AllVerified,
                ["checked"] = _checks.Count,
                ["all_measured"] = _allMeasured,
                ["required"] = new JArray(Sorted(_required)),
                ["missing"] = new JArray(Sorted(Missing)),
                ["unexpected"] = new JArray(Sorted(Unexpected)),
                ["verified_means"] = "true only when EXACTLY the required properties were checked - each once, " +
                                     "none missing, none substituted - and every one of them was RE-READ from the " +
                                     "committed model and matched. A property that could not be read counts as " +
                                     "unmeasured, never as agreement, and an empty checklist is not a pass.",
                ["properties"] = new JArray(_checks.ToArray())
            };
        }

        private static JToken Frozen(JToken token)
            => token == null ? JValue.CreateNull() : token.DeepClone();

        private static JToken[] Sorted(IEnumerable<string> values)
        {
            var list = new List<string>(values);
            list.Sort(StringComparer.Ordinal);
            return list.ConvertAll(v => (JToken)v).ToArray();
        }
    }

}
