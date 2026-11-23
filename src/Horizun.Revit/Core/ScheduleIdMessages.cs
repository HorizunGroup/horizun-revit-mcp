// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_get_schedule_data: why schedule_id names no readable schedule, and which ones exist. A
// bare "does not identify a native ViewSchedule" sent callers to guess ids. The id they hold may
// be absent, name no element (stale after a synchronize), name another kind of element, or name a
// schedule TEMPLATE - which IS a ViewSchedule, so it has to be recognised before reading, not
// after a failed cast (a first version put the template case behind the cast and never reached it).
//
// No Revit types: the command hands what it found as plain facts.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public enum ScheduleIdHit
    {
        /// <summary>schedule_id was absent, null or -1.</summary>
        NotSent,
        /// <summary>A negative id other than -1: not an element id at all.</summary>
        Invalid,
        /// <summary>No element has that id.</summary>
        NoElement,
        /// <summary>A ViewSchedule whose IsTemplate is true.</summary>
        Template,
        /// <summary>Some other kind of element.</summary>
        OtherElement,
        /// <summary>A ViewSchedule that is not a template: the one case that is read.</summary>
        Readable
    }

    /// <summary>What the document says about the element an id names (null element = nothing has that id).</summary>
    public struct ScheduleIdFacts
    {
        public bool Exists;
        public bool IsViewSchedule;
        public bool IsTemplate;

        public ScheduleIdFacts(bool exists, bool isViewSchedule, bool isTemplate)
        {
            Exists = exists;
            IsViewSchedule = isViewSchedule;
            IsTemplate = isTemplate;
        }
    }

    public static class ScheduleIdMessages
    {
        /// <summary>Whether a schedule_id value means "not sent": absent/null (no value) or the -1 sentinel.</summary>
        public static bool IsNotSent(long? id) => !id.HasValue || id.Value == -1;

        /// <summary>
        /// The decision, in its one order: not sent, then negative, then (only now asking the document, through
        /// <paramref name="lookup"/>) no element, then another kind of element, then a TEMPLATE - which IS a
        /// ViewSchedule, so it must be told apart before the schedule is treated as readable - and only then a
        /// readable schedule. <paramref name="lookup"/> is never called for an id that is not sent or negative.
        /// </summary>
        public static ScheduleIdHit Classify(long? sentId, Func<long, ScheduleIdFacts> lookup)
        {
            if (IsNotSent(sentId)) return ScheduleIdHit.NotSent;
            if (sentId.Value < 0) return ScheduleIdHit.Invalid;
            if (lookup == null) throw new ArgumentNullException(nameof(lookup));
            ScheduleIdFacts facts = lookup(sentId.Value);
            if (!facts.Exists) return ScheduleIdHit.NoElement;
            if (!facts.IsViewSchedule) return ScheduleIdHit.OtherElement;
            if (facts.IsTemplate) return ScheduleIdHit.Template;
            return ScheduleIdHit.Readable;
        }

        /// <summary>
        /// The refusal. <paramref name="hitType"/> / <paramref name="hitName"/> describe the element the id found
        /// (OtherElement / Template); <paramref name="schedules"/> are the readable schedules (id, name), or null when
        /// they could not be listed.
        /// </summary>
        public static string Refusal(ScheduleIdHit hit, long? id, string hitType, string hitName,
                                     IList<KeyValuePair<long, string>> schedules)
        {
            string what;
            switch (hit)
            {
                case ScheduleIdHit.NotSent: what = "schedule_id was not sent."; break;
                case ScheduleIdHit.Invalid: what = "schedule_id " + id + " is not an element id (ids are 0 or more)."; break;
                case ScheduleIdHit.NoElement:
                    what = "No element has id " + id + " in the active document (an id read before a synchronize or in another document can be stale)."; break;
                case ScheduleIdHit.Template:
                    what = "Id " + id + " is a schedule view TEMPLATE" + (string.IsNullOrEmpty(hitName) ? "" : " ('" + hitName + "')") +
                           ": a template defines fields, filters and formatting for the schedules that use it, and has no rows of its own to read."; break;
                default:
                    what = "Id " + id + " is a " + (string.IsNullOrEmpty(hitType) ? "element" : hitType) +
                           (string.IsNullOrEmpty(hitName) ? "" : " ('" + hitName + "')") + ", not a native ViewSchedule."; break;
            }
            string list;
            if (schedules == null) list = "horizun_list_schedules lists the schedules and their ids.";
            else if (schedules.Count == 0) list = "This document has no schedules.";
            else
            {
                var ordered = schedules.OrderBy(s => s.Value, StringComparer.OrdinalIgnoreCase).ToList();
                list = "Schedules here: " + string.Join("; ", ordered.Take(15).Select(s => s.Key + " '" + s.Value + "'")) +
                       (ordered.Count > 15 ? "; ... (" + ordered.Count + " in all, horizun_list_schedules lists them)" : "") + ".";
            }
            return what + " " + list + " Nothing was read.";
        }
    }
}
