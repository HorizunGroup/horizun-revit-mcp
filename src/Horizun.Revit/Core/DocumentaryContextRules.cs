// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE DOCUMENTARY CONTEXT: who the project is for, what it is called, and
// whether the fields somebody will read off a title block actually say anything.
//
// WHAT THIS FILE DELIBERATELY DOES NOT DO is define which fields matter. There
// is no compiled-in list of mandatory corporate fields, because "every project
// must have a client name" is one organisation's rule and this bridge serves
// everybody's. With no profile every field is not_requested - which is NOT a
// pass, and the reply says so.
//
// THE DISTINCTION THAT CARRIES THE AREA is between a field that does not exist
// and a field that exists and is blank. They look identical on a title block
// and they are different problems: the first is a template that never carried
// the parameter, the second is somebody who never filled it in. A tool that
// reports both as "missing" sends half its readers to edit the wrong thing.
//
// IDENTITY IS THE SECOND ONE. Two parameters called "Client" - one shared with
// a GUID, one typed into the project - are different parameters, so a rule
// keyed by GUID is never satisfied by a name match. That machinery already
// exists in Core/ParameterStandardRules and is REUSED here rather than
// rewritten: this file supplies the fields, that one decides the outcomes.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// The documentary surfaces a profile may speak about. Named so a rule filed
    /// under a misspelt surface is refused rather than silently never running.
    /// </summary>
    public static class DocumentarySurface
    {
        public const string ProjectInformation = "project_information";
        public const string Units = "units";
        public const string ProjectLocation = "project_location";
        public const string Phases = "phases";
        public const string Templates = "templates";
        public const string Sheets = "sheets";
        public const string Revisions = "revisions";
        public const string Links = "links";
        public const string SharedParameters = "shared_parameters";

        public static readonly string[] All =
        {
            ProjectInformation, Units, ProjectLocation, Phases, Templates,
            Sheets, Revisions, Links, SharedParameters
        };
    }

    /// <summary>One documentary field as the model reports it.</summary>
    public sealed class DocumentaryFact
    {
        public string Field;
        public string Surface = DocumentarySurface.ProjectInformation;
        /// <summary>False when the parameter does not exist on the element at all.</summary>
        public bool Present;
        /// <summary>False when the read threw. Distinct from absent.</summary>
        public bool Readable = true;
        public string Value;
        /// <summary>The shared-parameter guid, when the field has one.</summary>
        public string Guid;
        public string BuiltIn;
        public long ElementId;
    }

    public sealed class DocumentaryVerdict
    {
        public string Field;
        public string Surface;
        public string Outcome;
        public string Severity;
        public string Detail;
        public string Explanation;
    }

    public static class DocumentaryContextRules
    {
        public const string NoProfileMeans =
            "with no documentary profile every field is not_requested, which is NOT a pass. Which fields a " +
            "project must carry is one organisation's decision - 'every project has a client name' is true of " +
            "somebody and false of somebody else - and none is compiled in here.";

        public const string AbsentVersusEmptyMeans =
            "a field that does not EXIST and a field that exists and is BLANK look identical on a title block " +
            "and are different problems: the first is a template that never carried the parameter, the second " +
            "is somebody who never filled it in. They are reported apart so a reader is sent to the right one.";

        /// <summary>
        /// Judges one documentary field by handing it to the parameter machinery,
        /// so the thirteen outcomes and the GUID-over-name rule are defined once.
        /// A field this profile does not mention comes back rule_not_requested.
        /// </summary>
        public static DocumentaryVerdict Evaluate(DocumentaryFact fact, ParameterRule rule)
        {
            if (fact == null) return null;
            var v = new DocumentaryVerdict { Field = fact.Field, Surface = fact.Surface };

            if (rule == null)
            {
                v.Outcome = ParameterOutcome.RuleNotRequested;
                v.Detail = "no rule was supplied for this field.";
                return v;
            }

            // Reuse, not reimplementation: one definition of what wrong_guid,
            // placeholder and empty mean, for parameters and for documentary
            // fields alike.
            var observation = new ParameterObservation
            {
                ElementId = fact.ElementId,
                Category = fact.Surface,
                Present = fact.Present,
                Readable = fact.Readable,
                Guid = fact.Guid,
                IsShared = fact.Guid != null,
                StorageType = "String",
                Binding = ParameterScope.Instance,
                ValueAsString = fact.Value,
                HasValue = !string.IsNullOrEmpty(fact.Value)
            };

            ParameterVerdict pv = ParameterStandardRules.Evaluate(rule, observation);
            v.Outcome = pv == null ? ParameterOutcome.Unreadable : pv.Outcome;
            v.Detail = pv == null ? null : pv.Detail;
            v.Severity = rule.Severity;
            v.Explanation = rule.Explanation;
            return v;
        }

        /// <summary>
        /// Judges the whole context. Every field the profile mentions appears, and
        /// so does every field collected - a field nobody asked about is
        /// not_requested rather than omitted, because an absent key reads as
        /// nothing to say.
        /// </summary>
        public static List<DocumentaryVerdict> EvaluateAll(IEnumerable<DocumentaryFact> facts,
                                                           ParameterProfile profile)
        {
            var verdicts = new List<DocumentaryVerdict>();
            var byId = new Dictionary<string, ParameterRule>(StringComparer.OrdinalIgnoreCase);
            if (profile != null && profile.Ok)
                foreach (ParameterRule r in profile.Rules)
                    if (r.Id != null) byId[r.Id] = r;

            foreach (DocumentaryFact f in facts ?? Enumerable.Empty<DocumentaryFact>())
            {
                if (f == null) continue;
                ParameterRule rule;
                byId.TryGetValue(f.Field ?? "", out rule);
                DocumentaryVerdict v = Evaluate(f, rule);
                if (v != null) verdicts.Add(v);
            }

            // A rule about a field nothing collected is a gap in this tool, and it
            // is named rather than dropped.
            var collected = new HashSet<string>(
                (facts ?? Enumerable.Empty<DocumentaryFact>()).Where(f => f != null).Select(f => f.Field ?? ""),
                StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, ParameterRule> kv in byId)
            {
                if (collected.Contains(kv.Key)) continue;
                verdicts.Add(new DocumentaryVerdict
                {
                    Field = kv.Key,
                    Surface = null,
                    Outcome = ParameterOutcome.Unreadable,
                    Severity = kv.Value.Severity,
                    Detail = "your profile declares this field and nothing in this scan collected it. That is a " +
                             "gap in THIS TOOL, not a statement about the model."
                });
            }
            return verdicts;
        }

        public static JObject Tally(IEnumerable<DocumentaryVerdict> verdicts, ParameterProfile profile)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string o in ParameterOutcome.All) counts[o] = 0;
            foreach (DocumentaryVerdict v in verdicts ?? Enumerable.Empty<DocumentaryVerdict>())
                if (v != null && v.Outcome != null && counts.ContainsKey(v.Outcome)) counts[v.Outcome]++;

            var o2 = new JObject();
            foreach (string k in ParameterOutcome.All) o2[k] = counts[k];
            o2["fields_assessed"] = counts[ParameterOutcome.Present] + counts[ParameterOutcome.Missing] +
                                    counts[ParameterOutcome.Empty] + counts[ParameterOutcome.Placeholder] +
                                    counts[ParameterOutcome.InvalidValue] + counts[ParameterOutcome.WrongGuid];
            o2["profile"] = profile == null ? "not_requested"
                          : profile.Absent ? "not_requested"
                          : profile.Ok ? "ok" : "refused";
            o2["no_profile_means"] = NoProfileMeans;
            o2["absent_versus_empty_means"] = AbsentVersusEmptyMeans;
            return o2;
        }

        public static JObject ToJson(DocumentaryVerdict v)
        {
            if (v == null) return null;
            return new JObject
            {
                ["field"] = v.Field,
                ["surface"] = v.Surface,
                ["outcome"] = v.Outcome,
                ["severity"] = v.Severity,
                ["detail"] = v.Detail,
                ["explanation"] = v.Explanation
            };
        }

        public static JObject ToJson(DocumentaryFact f)
        {
            if (f == null) return null;
            return new JObject
            {
                ["field"] = f.Field,
                ["surface"] = f.Surface,
                ["present"] = f.Present,
                ["readable"] = f.Readable,
                // The VALUE is reported as read. Deciding whether it is a
                // placeholder is the profile's job, not this one's.
                ["value"] = f.Value,
                ["guid"] = f.Guid,
                ["built_in_parameter"] = f.BuiltIn
            };
        }
    }
}
