// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// 4D AND 5D READINESS, WHICH IS NOT THE SAME AS 4D AND 5D.
//
// This measures whether a model CARRIES THE EVIDENCE a scheduler or an estimator
// would need. It does not connect to a programme, it does not price anything,
// and it never claims a model is "4D ready" as a yes or a no. Nothing in the
// eight-product benchmark measures this at all - the row is empty across the
// whole market - and the reason it is easy to do badly is the rule below.
//
// THE RULE THAT MATTERS: AN EMPTY PARAMETER IS NOT AN ABSENT INTEGRATION.
//
// A model where every element has a "Cost Code" parameter that is blank is in a
// completely different state from one where the parameter does not exist. The
// first has been set up and not filled in; the second has not been set up. A
// tool that reports both as "no 5D connection" has destroyed the only piece of
// information the reader needed. So every role resolves to one of five words,
// and `not_assessable` is a real answer rather than a polite failure:
//
//   integration_evidence_found  the parameter exists AND carries values
//   readiness_complete          every element in scope carries a value
//   readiness_partial           some do
//   readiness_absent            the parameter exists on nothing in scope
//   not_assessable              it could not be measured - not a verdict
//
// The roles and their parameter names arrive as a DECLARATION. No cost-code
// standard, no WBS convention and no parameter name is compiled in, because
// every organisation spells these differently and a tool that assumed one would
// be measuring its author's employer.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class ReadinessState
    {
        public const string EvidenceFound = "integration_evidence_found";
        public const string Complete = "readiness_complete";
        public const string Partial = "readiness_partial";
        public const string Absent = "readiness_absent";
        public const string NotAssessable = "not_assessable";
    }

    /// <summary>One declared role: what it means, and the names it may go by here.</summary>
    public sealed class ReadinessRole
    {
        public string Id;
        public string Dimension;          // "4d" | "5d" | "traceability" | "classification" | "quantity"
        public List<string> Aliases = new List<string>();
        /// <summary>When true, a value that is only whitespace counts as absent.</summary>
        public bool BlankIsAbsent = true;
    }

    /// <summary>What was measured for one role over the elements in scope.</summary>
    public sealed class RoleMeasurement
    {
        public string RoleId;
        public string Dimension;
        /// <summary>The alias that actually resolved, so a reader knows which name the model uses.</summary>
        public string MatchedAlias;
        public bool ParameterExists;
        public long ElementsInScope;
        public long ElementsCarryingValue;
        public long ElementsUnreadable;
        public List<string> SampleValues = new List<string>();
    }

    public sealed class RoleVerdict
    {
        public string RoleId;
        public string Dimension;
        public string State;
        public string MatchedAlias;
        public double? Coverage;          // null when not assessable
        public long ElementsInScope;
        public long ElementsCarryingValue;
        public long ElementsUnreadable;
        public string Why;
    }

    public sealed class DimensionScore
    {
        public string Dimension;
        public string State;
        public int RolesDeclared;
        public int RolesWithEvidence;
        public int RolesComplete;
        public int RolesAbsent;
        public int RolesNotAssessable;
        public double? Coverage;
        public string Why;
    }

    public static class ReadinessRules
    {
        public const string CodeNoRoles = "no_roles_declared";
        public const string CodeDuplicateRole = "duplicate_role_id";
        public const string CodeNoAliases = "role_declares_no_parameter_names";
        public const string CodeUnknownDimension = "dimension_not_in_vocabulary";

        public static readonly string[] Dimensions = { "4d", "5d", "traceability", "classification", "quantity" };

        /// <summary>Validate a declaration. A non-null return is the refusal; nothing is measured.</summary>
        public static string Validate(IEnumerable<ReadinessRole> roles, out List<string> codes)
        {
            codes = new List<string>();
            var list = new List<ReadinessRole>(roles ?? new List<ReadinessRole>());
            if (list.Count == 0)
            {
                codes.Add(CodeNoRoles);
                return "no roles were declared. This command measures a declaration; with nothing declared " +
                       "there is nothing to measure, and answering 'not ready' would be inventing a standard.";
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ReadinessRole r in list)
            {
                if (r == null || string.IsNullOrWhiteSpace(r.Id))
                {
                    codes.Add(CodeDuplicateRole);
                    return "every role needs an id.";
                }
                if (!seen.Add(r.Id))
                {
                    codes.Add(CodeDuplicateRole);
                    return "role id '" + r.Id + "' is declared twice.";
                }
                if (r.Aliases == null || r.Aliases.Count == 0)
                {
                    codes.Add(CodeNoAliases);
                    return "role '" + r.Id + "' declares no parameter names. This bridge compiles in no " +
                           "parameter naming convention, so a role with no names cannot be looked for.";
                }
                if (Array.IndexOf(Dimensions, r.Dimension) < 0)
                {
                    codes.Add(CodeUnknownDimension);
                    return "role '" + r.Id + "' names dimension '" + r.Dimension + "', which is not one of: " +
                           string.Join(", ", Dimensions) + ".";
                }
            }
            return null;
        }

        /// <summary>
        /// Turn a measurement into one of the five words.
        ///
        /// The order of the tests is the whole point. "Could it be measured" comes
        /// first, then "does the parameter exist at all", and only then how much of
        /// it is filled in - so a parameter that exists and is empty can never be
        /// reported the same way as one that does not exist.
        /// </summary>
        public static RoleVerdict Judge(RoleMeasurement m)
        {
            var v = new RoleVerdict
            {
                RoleId = m == null ? null : m.RoleId,
                Dimension = m == null ? null : m.Dimension,
                MatchedAlias = m == null ? null : m.MatchedAlias,
                ElementsInScope = m == null ? 0 : m.ElementsInScope,
                ElementsCarryingValue = m == null ? 0 : m.ElementsCarryingValue,
                ElementsUnreadable = m == null ? 0 : m.ElementsUnreadable
            };

            if (m == null)
            {
                v.State = ReadinessState.NotAssessable;
                v.Why = "nothing was measured for this role.";
                return v;
            }

            if (m.ElementsInScope == 0)
            {
                v.State = ReadinessState.NotAssessable;
                v.Why = "no element was in scope, so there was nothing to carry a value. This is a fact about " +
                        "the scope, not about the model's readiness.";
                return v;
            }

            // EVERY ELEMENT UNREADABLE IS NOT ZERO COVERAGE. It is no measurement.
            if (m.ElementsUnreadable >= m.ElementsInScope)
            {
                v.State = ReadinessState.NotAssessable;
                v.Why = "none of the " + m.ElementsInScope + " element(s) in scope could be read, so nothing is " +
                        "known. Unreadable is not the same as empty.";
                return v;
            }

            long readable = m.ElementsInScope - m.ElementsUnreadable;
            v.Coverage = readable > 0 ? (double?)((double)m.ElementsCarryingValue / readable) : null;

            if (!m.ParameterExists)
            {
                v.State = ReadinessState.Absent;
                v.Why = "no parameter matching any declared name exists on the elements in scope. The model has " +
                        "not been set up for this role - which is a different state from a parameter that " +
                        "exists and is blank.";
                return v;
            }

            if (m.ElementsCarryingValue == 0)
            {
                v.State = ReadinessState.Absent;
                v.Why = "the parameter '" + (m.MatchedAlias ?? "?") + "' EXISTS on the elements in scope and " +
                        "carries no value on any of them. The model has been set up for this role and not " +
                        "filled in, which is much closer to ready than a model with no parameter at all.";
                if (m.ElementsUnreadable > 0)
                    v.Why += " " + m.ElementsUnreadable + " element(s) could not be read, so this is a lower bound.";
                return v;
            }

            if (m.ElementsCarryingValue >= readable)
            {
                v.State = ReadinessState.Complete;
                v.Why = "every one of the " + readable + " readable element(s) in scope carries a value for '" +
                        (m.MatchedAlias ?? "?") + "'.";
                if (m.ElementsUnreadable > 0)
                    v.Why += " " + m.ElementsUnreadable + " could not be read and are not counted either way.";
                return v;
            }

            v.State = ReadinessState.Partial;
            v.Why = m.ElementsCarryingValue + " of " + readable + " readable element(s) carry a value for '" +
                    (m.MatchedAlias ?? "?") + "'.";
            if (m.ElementsUnreadable > 0)
                v.Why += " " + m.ElementsUnreadable + " could not be read and are not counted either way.";
            return v;
        }

        /// <summary>
        /// Roll the roles of one dimension into a dimension state. A dimension is
        /// only `integration_evidence_found` when at least one role carries values -
        /// that is the claim worth making, and it is deliberately weaker than
        /// "ready".
        /// </summary>
        public static DimensionScore Score(string dimension, IEnumerable<RoleVerdict> verdicts)
        {
            var mine = new List<RoleVerdict>();
            foreach (RoleVerdict v in verdicts ?? new List<RoleVerdict>())
                if (v != null && string.Equals(v.Dimension, dimension, StringComparison.Ordinal)) mine.Add(v);

            var s = new DimensionScore { Dimension = dimension, RolesDeclared = mine.Count };
            if (mine.Count == 0)
            {
                s.State = ReadinessState.NotAssessable;
                s.Why = "no role was declared for this dimension.";
                return s;
            }

            double sum = 0; int counted = 0;
            foreach (RoleVerdict v in mine)
            {
                switch (v.State)
                {
                    case ReadinessState.Complete: s.RolesComplete++; s.RolesWithEvidence++; break;
                    case ReadinessState.Partial: s.RolesWithEvidence++; break;
                    case ReadinessState.Absent: s.RolesAbsent++; break;
                    default: s.RolesNotAssessable++; break;
                }
                if (v.Coverage.HasValue) { sum += v.Coverage.Value; counted++; }
            }
            s.Coverage = counted > 0 ? (double?)(sum / counted) : null;

            if (s.RolesNotAssessable == mine.Count)
            {
                s.State = ReadinessState.NotAssessable;
                s.Why = "none of the " + mine.Count + " declared role(s) could be measured.";
            }
            else if (s.RolesComplete == mine.Count)
            {
                s.State = ReadinessState.Complete;
                s.Why = "every declared role carries a value on every readable element in scope.";
            }
            else if (s.RolesWithEvidence > 0)
            {
                s.State = ReadinessState.Partial;
                s.Why = s.RolesWithEvidence + " of " + mine.Count + " declared role(s) carry values.";
            }
            else
            {
                s.State = ReadinessState.Absent;
                s.Why = "no declared role carries a value on any element in scope.";
            }

            if (s.RolesNotAssessable > 0 && s.State != ReadinessState.NotAssessable)
                s.Why += " " + s.RolesNotAssessable + " role(s) could not be measured and are not counted either way.";
            return s;
        }

        /// <summary>
        /// The sentence that keeps this honest, published beside the scores.
        /// </summary>
        public const string Means =
            "readiness is whether the model CARRIES THE EVIDENCE a scheduler or an estimator would need. It is " +
            "not a connection to a programme or a cost plan, and nothing here prices or sequences anything. An " +
            "empty parameter and an absent parameter are reported differently on purpose: the first is a model " +
            "set up and not filled in, the second is a model not set up, and collapsing them into 'no " +
            "connection' throws away the only thing the reader needed to know.";
    }
}
