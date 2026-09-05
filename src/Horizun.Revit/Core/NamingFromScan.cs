// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE ROUTE FROM A MODEL'S NAMES TO A NAMING VERDICT.
//
// NamingProfileRules could already judge a list of names. Nothing handed it
// one, so the rules were provable and unused - and a rule nobody runs is
// indistinguishable, in a report, from a rule everything passes.
//
// This is the half that makes the classes accountable to each other. Its whole
// job is to guarantee that EVERY class the profile can mention appears in the
// answer with a status a reader can act on, and that the four ways a class can
// have no findings stay four different answers:
//
//   not_requested   the profile said nothing about this class.
//   not_applicable  the class cannot exist in this document at all - worksets
//                   in a file that was never workshared. Not empty: absent.
//   not_collected   nobody handed this class a population. A WIRING DEFECT,
//                   printed in the reply rather than left to look like a pass.
//   ok              a rule ran, over a population, and every name matched.
//
// Collapsing any of those into "ok" produces the one output this project
// exists to prevent: a clean report about a check that never happened.
//
// Revit-free, so the whole route is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class NamingStatus
    {
        public const string Ok = "ok";
        public const string Failed = "failed";
        public const string NotRequested = "not_requested";
        public const string NotApplicable = "not_applicable";
        public const string NotCollected = "not_collected";
    }

    /// <summary>A class the command could not collect, and why. Absent is not empty.</summary>
    public sealed class NamingNotApplicable
    {
        public string Class;
        public string Reason;
    }

    public static class NamingFromScan
    {
        /// <summary>
        /// What each class MEANS, published beside its verdict. Two people counting
        /// "types" get different numbers until somebody writes down whether a
        /// system family type is one.
        /// </summary>
        public static readonly Dictionary<string, string> Populations =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "levels", "every Level in the document." },
                { "grids", "every Grid in the document." },
                { "views", "every View that is neither a template nor a sheet." },
                { "view_templates", "every View whose IsTemplate is true." },
                { "sheets", "every ViewSheet, judged on its NAME - the sheet number is a separate field." },
                { "families", "every loadable Family. System families are not Family elements and are not here." },
                { "types", "every ElementType in the document, loadable and system alike." },
                { "worksets", "every user workset. A document that was never workshared has none, which is " +
                             "reported as not_applicable rather than as an empty pass." },
                { "links", "every RevitLinkType, which is the link as loaded rather than each placed instance." },
                { "groups", "every GroupType, placed or not." },
                { "rooms", "every Room element, placed or not." },
                { "spaces", "every MEP Space element." },
                { "systems", "every MEP system." },
                { "filters", "every ParameterFilterElement, whether or not a view uses it." },
            };

        /// <summary>
        /// Judges every class, always. A class missing from `populations` is
        /// reported not_collected rather than omitted: an absent key in a reply is
        /// read as "nothing to say", and the one thing it must never be read as is
        /// a pass.
        /// </summary>
        public static JObject Judge(IDictionary<string, List<NamedThing>> populations,
                                    IEnumerable<NamingNotApplicable> notApplicable,
                                    NamingProfile profile)
        {
            var na = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (NamingNotApplicable n in notApplicable ?? Enumerable.Empty<NamingNotApplicable>())
                if (n != null && n.Class != null) na[n.Class] = n.Reason;

            var classes = new JObject();
            int assessed = 0, failedClasses = 0, notCollected = 0;

            foreach (string cls in NamingClasses.All)
            {
                string reason;
                if (na.TryGetValue(cls, out reason))
                {
                    classes[cls] = new JObject
                    {
                        ["status"] = NamingStatus.NotApplicable,
                        ["reason"] = reason,
                        ["population"] = Populations.ContainsKey(cls) ? Populations[cls] : null
                    };
                    continue;
                }

                List<NamedThing> things;
                if (populations == null || !populations.TryGetValue(cls, out things) || things == null)
                {
                    notCollected++;
                    classes[cls] = new JObject
                    {
                        ["status"] = NamingStatus.NotCollected,
                        ["reason"] = "no population was collected for '" + cls + "', so nothing was judged. " +
                                     "This is a defect in this tool, NOT a statement about the model, and it " +
                                     "is reported rather than omitted because an absent class reads as a pass.",
                        ["population"] = Populations.ContainsKey(cls) ? Populations[cls] : null
                    };
                    continue;
                }

                NamingVerdict v = NamingProfileRules.Check(cls, things, profile);
                JObject row = v.ToJson();
                row["population"] = Populations.ContainsKey(cls) ? Populations[cls] : null;
                classes[cls] = row;

                if (v.Status == NamingStatus.Ok || v.Status == NamingStatus.Failed) assessed++;
                if (v.Status == NamingStatus.Failed) failedClasses++;
            }

            return new JObject
            {
                ["profile"] = ProfileJson(profile),
                ["classes_assessed"] = assessed,
                ["classes_failed"] = failedClasses,
                ["classes_not_collected"] = notCollected,
                ["means"] = "not_requested, not_applicable and not_collected are three different reasons a class " +
                            "has no findings, and NONE of them is a pass. Only 'ok' means a rule ran over a " +
                            "population and every name matched it.",
                ["classes"] = classes
            };
        }

        private static JObject ProfileJson(NamingProfile p)
        {
            if (p == null)
                return new JObject { ["status"] = NamingStatus.NotRequested };
            // Absent is not refused. Both produce no findings; only one of them is
            // the caller's mistake, and telling a caller their profile was rejected
            // when they sent none sends them looking for a bug they do not have.
            if (!p.Ok && p.Code == NamingCodes.NoProfile)
                return new JObject { ["status"] = NamingStatus.NotRequested, ["means"] = p.Message };
            if (!p.Ok)
                return new JObject { ["status"] = "refused", ["code"] = p.Code, ["message"] = p.Message };
            return new JObject { ["status"] = NamingStatus.Ok, ["version"] = p.Version };
        }
    }
}
