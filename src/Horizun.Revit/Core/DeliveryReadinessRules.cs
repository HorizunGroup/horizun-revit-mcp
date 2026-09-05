// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// 4D AND 5D READINESS: evidence found, never absence assumed.
//
// The sentence this file refuses to produce is "this model is not ready for
// 4D". It cannot be produced honestly, because readiness is measured against a
// declaration nobody made until the caller makes it: which parameter carries an
// activity id, on which categories, on the instance or the type. With no
// profile every role is not_required and the reply says that is not a verdict.
//
// PER CATEGORY, NOT PER MODEL. A global "78% of elements carry an activity id"
// is the number that makes a model look ready while every pipe is missing one:
// the walls carry it, the walls are numerous, and the average hides the
// discipline that has nothing. Roles are measured against the LEAF CATEGORIES
// the caller declared, and a category that scores zero is visible next to one
// that scores a hundred.
//
// AND THE 5D SENTENCE, refused in the same way: a cost parameter carrying a
// value is not a connection to a budget. It is evidence that somebody typed a
// code. Whether that code exists, whether it is a leaf or a group nobody can
// price, and whether the catalogue it belongs to was even supplied are separate
// answers - and no catalogue is compiled in, because OmniClass, UniFormat,
// MasterFormat and every house standard are somebody's and not everybody's.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class DeliveryDimension
    {
        public const string FourD = "4d";
        public const string FiveD = "5d";

        public static readonly string[] All = { FourD, FiveD };
    }

    public static class RoleState
    {
        /// <summary>Something carries the parameter somewhere. Evidence, not completeness.</summary>
        public const string Found = "found";
        public const string Complete = "complete";
        public const string Partial = "partial";
        public const string Absent = "absent";
        public const string Unreadable = "unreadable";
        public const string NotAssessable = "not_assessable";
        public const string NotRequired = "not_required";

        public static readonly string[] All =
        {
            Found, Complete, Partial, Absent, Unreadable, NotAssessable, NotRequired
        };
    }

    /// <summary>How a code sits in a caller-supplied classification catalogue.</summary>
    public static class CodeStatus
    {
        public const string Leaf = "leaf";
        /// <summary>A real code, but a group - nobody prices a group.</summary>
        public const string GroupNotTerminal = "group_not_terminal";
        public const string NotInCatalogue = "not_in_catalogue";
        public const string Invalid = "invalid";
        public const string CatalogueNotSupplied = "catalogue_not_supplied";
        public const string CatalogueUnreadable = "catalogue_unreadable";
        public const string NotRequired = "not_required";

        public static readonly string[] All =
        {
            Leaf, GroupNotTerminal, NotInCatalogue, Invalid,
            CatalogueNotSupplied, CatalogueUnreadable, NotRequired
        };
    }

    public sealed class DeliveryRole
    {
        public string Id;
        public string Dimension;
        /// <summary>Identity, scope, categories and validation - reused, not redefined.</summary>
        public ParameterRule Rule;
        public bool Required = true;
        /// <summary>When set, values are checked against the caller's catalogue.</summary>
        public bool ValidateAgainstCatalogue;
    }

    /// <summary>What one role measured on ONE category.</summary>
    public sealed class RoleCategoryMeasurement
    {
        public string RoleId;
        public string Category;
        /// <summary>Elements of this category in scope. Zero means the category is empty.</summary>
        public long Population;
        /// <summary>Elements whose parameter could be examined at all.</summary>
        public long Evaluated;
        public long Complete;
        public long Incomplete;
        public long Unreadable;
        public List<long> IncompleteIds = new List<long>();
        public List<string> SampleValues = new List<string>();

        /// <summary>Null when nothing was evaluated - never 0, which reads as a result.</summary>
        public double? Coverage
        {
            get
            {
                if (Evaluated <= 0) return null;
                return Math.Round(Complete * 100.0 / Evaluated, 4);
            }
        }
    }

    public sealed class RoleVerdictByCategory
    {
        public string RoleId;
        public string Dimension;
        public string Category;
        public string State;
        public string Why;
        public double? Coverage;
        public long Population, Evaluated, Complete, Incomplete, Unreadable;
    }

    public static class DeliveryReadinessRules
    {
        public const string EvidenceMeans =
            "readiness is EVIDENCE FOUND, never absence assumed. With no profile every role is not_required, " +
            "which is not a verdict: which parameter carries an activity id, on which categories, on the " +
            "instance or the type, is a declaration nobody has made until the caller makes it.";

        public const string PerCategoryMeans =
            "roles are measured per LEAF CATEGORY, not over the model. A global percentage is the number that " +
            "makes a model look ready while a whole discipline has nothing: the walls carry the parameter, the " +
            "walls are numerous, and the average hides the pipes.";

        public const string NotAnIntegrationMeans =
            "a parameter carrying a value is not a connection to a schedule or a budget. It is evidence that " +
            "somebody typed something. Nothing here reads a programme file, queries a cost system or verifies " +
            "that an activity id matches an activity that exists - and a text that looks like an activity id " +
            "is not proof that it is one.";

        public const string CatalogueMeans =
            "no classification catalogue is compiled in. OmniClass, UniFormat, MasterFormat and every house " +
            "standard belong to somebody and not to everybody, so the catalogue arrives as an argument. " +
            "Without one, codes are reported as catalogue_not_supplied - which is not the same as a code that " +
            "is absent from a catalogue we did have.";

        /// <summary>
        /// One role on one category. The order matters: not_required first, because
        /// a role nobody asked for cannot be absent; then unreadable, because a
        /// population nobody could read has not told us anything.
        /// </summary>
        public static RoleVerdictByCategory Judge(RoleCategoryMeasurement m, bool required)
        {
            var v = new RoleVerdictByCategory
            {
                RoleId = m == null ? null : m.RoleId,
                Category = m == null ? null : m.Category
            };
            if (m == null)
            {
                v.State = RoleState.NotAssessable;
                v.Why = "nothing was measured.";
                return v;
            }

            v.Population = m.Population;
            v.Evaluated = m.Evaluated;
            v.Complete = m.Complete;
            v.Incomplete = m.Incomplete;
            v.Unreadable = m.Unreadable;
            v.Coverage = m.Coverage;

            if (!required)
            {
                v.State = RoleState.NotRequired;
                v.Why = "your profile does not require this role here.";
                return v;
            }

            // A CATEGORY WITH NOTHING IN IT IS NOT A FAILURE. There is nothing to
            // carry the parameter, and calling that "absent" invents a gap.
            if (m.Population == 0)
            {
                v.State = RoleState.NotAssessable;
                v.Why = "no element of this category is in the model, so nothing could carry the role.";
                return v;
            }

            if (m.Evaluated == 0)
            {
                v.State = m.Unreadable > 0 ? RoleState.Unreadable : RoleState.NotAssessable;
                v.Why = m.Unreadable > 0
                    ? m.Unreadable + " element(s) would not report this parameter and none could be read, so " +
                      "nothing is known here."
                    : "nothing in this category could be evaluated.";
                return v;
            }

            if (m.Complete == 0)
            {
                v.State = RoleState.Absent;
                v.Why = "none of the " + m.Evaluated + " element(s) evaluated carries a value for this role.";
                return v;
            }

            if (m.Complete == m.Evaluated)
            {
                // COMPLETE ONLY WHEN NOTHING WAS UNREADABLE. A hundred per cent of
                // what we could read is not a hundred per cent.
                v.State = m.Unreadable > 0 ? RoleState.Partial : RoleState.Complete;
                v.Why = m.Unreadable > 0
                    ? "every element that could be read carries a value, but " + m.Unreadable +
                      " could not be read, so this is a lower bound rather than complete."
                    : "every one of the " + m.Evaluated + " element(s) evaluated carries a value.";
                return v;
            }

            v.State = RoleState.Partial;
            v.Why = m.Complete + " of " + m.Evaluated + " element(s) evaluated carry a value.";
            return v;
        }

        /// <summary>
        /// A role across its categories. `found` is deliberately weaker than
        /// `complete`: it says the model carries this somewhere, which is what
        /// "evidence found" means and all it means.
        /// </summary>
        public static string RollUp(IEnumerable<RoleVerdictByCategory> byCategory)
        {
            List<RoleVerdictByCategory> all =
                (byCategory ?? Enumerable.Empty<RoleVerdictByCategory>()).Where(v => v != null).ToList();
            if (all.Count == 0) return RoleState.NotAssessable;

            var judged = all.Where(v => v.State != RoleState.NotRequired &&
                                        v.State != RoleState.NotAssessable).ToList();
            if (judged.Count == 0)
                return all.All(v => v.State == RoleState.NotRequired)
                    ? RoleState.NotRequired : RoleState.NotAssessable;

            if (judged.All(v => v.State == RoleState.Complete)) return RoleState.Complete;
            if (judged.All(v => v.State == RoleState.Absent)) return RoleState.Absent;
            if (judged.All(v => v.State == RoleState.Unreadable)) return RoleState.Unreadable;
            if (judged.Any(v => v.State == RoleState.Complete || v.State == RoleState.Partial))
                return RoleState.Partial;
            return RoleState.Found;
        }

        /// <summary>
        /// A dimension across its roles. Never a score: a number here would be read
        /// as a percentage of readiness, and readiness is not a scalar.
        /// </summary>
        public static JObject Dimension(string dimension, IEnumerable<RoleVerdictByCategory> verdicts,
                                        IEnumerable<DeliveryRole> roles)
        {
            List<DeliveryRole> declared = (roles ?? Enumerable.Empty<DeliveryRole>())
                .Where(r => r != null && r.Dimension == dimension).ToList();
            List<RoleVerdictByCategory> all = (verdicts ?? Enumerable.Empty<RoleVerdictByCategory>())
                .Where(v => v != null && v.Dimension == dimension).ToList();

            var byRole = new JObject();
            var states = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string s in RoleState.All) states[s] = 0;

            foreach (DeliveryRole r in declared)
            {
                List<RoleVerdictByCategory> mine = all.Where(v => v.RoleId == r.Id).ToList();
                string state = RollUp(mine);
                states[state]++;
                byRole[r.Id] = new JObject
                {
                    ["state"] = state,
                    ["categories_judged"] = mine.Count,
                    ["categories_complete"] = mine.Count(v => v.State == RoleState.Complete),
                    ["categories_absent"] = mine.Count(v => v.State == RoleState.Absent),
                    ["categories_not_assessable"] = mine.Count(v => v.State == RoleState.NotAssessable)
                };
            }

            var o = new JObject
            {
                ["dimension"] = dimension,
                ["roles_declared"] = declared.Count,
                ["evidence_means"] = EvidenceMeans,
                ["per_category_means"] = PerCategoryMeans,
                ["not_an_integration_means"] = NotAnIntegrationMeans,
                ["by_role"] = byRole
            };
            foreach (string s in RoleState.All) o["roles_" + s] = states[s];

            // NO SCORE. Readiness is not a scalar, and a number here would be read
            // as one.
            o["score"] = null;
            o["score_means"] = "no readiness score is published. A single number would be read as a percentage " +
                               "of readiness, and the states below are not commensurable - a role that is " +
                               "not_assessable is not half of one that is complete.";
            return o;
        }

        public static JObject ToJson(RoleVerdictByCategory v)
        {
            if (v == null) return null;
            return new JObject
            {
                ["role"] = v.RoleId,
                ["dimension"] = v.Dimension,
                ["category"] = v.Category,
                ["state"] = v.State,
                ["population"] = v.Population,
                ["evaluated"] = v.Evaluated,
                ["complete"] = v.Complete,
                ["incomplete"] = v.Incomplete,
                ["unreadable"] = v.Unreadable,
                ["coverage_percent"] = v.Coverage,
                ["why"] = v.Why
            };
        }
    }
}
