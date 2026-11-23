// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_parameters operation=purge_legacy_marks (5.0.4, privacy).
//
// Before 5.0.4 every typed write stamped the element with ownership mark version 2
// (HrzOwnership.SchemaGuidV2), which holds the Revit user name, the Windows account and
// the SID. Models handed out (course kits, templates, client models) still carry it.
// This operation deletes ONLY that entity, on every element and type that has it:
// version 1 and version 3 marks, other schemas and the elements themselves are untouched.
// It counts before, rehearses in a rolled-back transaction, needs the token, and
// re-reads the count after the commit. In a collaborative model an element owned by
// another user is skipped and reported (its mark stays until that person relinquishes).
// No reply ever shows the personal values the marks hold.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageParametersCommand
    {
        /// <summary>Every element and type that carries a valid version 2 ownership mark.</summary>
        internal static List<Element> LegacyMarked(Document doc)
        {
            var found = new List<Element>();
            Schema v2 = Schema.Lookup(HrzOwnership.SchemaGuidV2);
            if (v2 == null) return found;
            foreach (bool types in new[] { false, true })
            {
                var collector = new FilteredElementCollector(doc)
                    .WherePasses(new ElementIsElementTypeFilter(!types))
                    .WherePasses(new ExtensibleStorageFilter(HrzOwnership.SchemaGuidV2));
                foreach (Element e in collector)
                {
                    try { Entity en = e.GetEntity(v2); if (en != null && en.IsValid()) found.Add(e); } catch { }
                }
            }
            return found;
        }

        /// <summary>The read-only answer when the model holds no version 2 mark: nothing to purge, nothing written.</summary>
        private static JObject LegacyMarksNone(Document doc) => new JObject
        {
            ["operation"] = "purge_legacy_marks", ["state"] = "nothing_to_purge", ["write_started"] = false,
            ["v2_marks_before"] = 0, ["schema_in_document"] = Schema.Lookup(HrzOwnership.SchemaGuidV2) != null,
            ["note"] = "No element or type carries an ownership mark version 2 (user, account, SID). Nothing was written."
        };

        private static Plan BuildPurgeLegacyMarks(Document doc, JObject r, out string error)
        {
            error = null;
            Schema v2 = Schema.Lookup(HrzOwnership.SchemaGuidV2);
            List<Element> marked = LegacyMarked(doc);
            if (v2 == null || marked.Count == 0) { error = "No ownership mark version 2 to purge."; return null; }

            var targets = new List<ElementId>();
            var skipped = new List<long>();
            bool shared = false;
            try { shared = doc.IsWorkshared; } catch { shared = true; }
            foreach (Element e in marked)
            {
                if (shared)
                {
                    CheckoutStatus st;
                    try { st = WorksharingUtils.GetCheckoutStatus(doc, e.Id); } catch { st = CheckoutStatus.OwnedByOtherUser; }
                    if (st == CheckoutStatus.OwnedByOtherUser) { skipped.Add(Rid.Value(e.Id)); continue; }
                }
                targets.Add(e.Id);
            }
            if (targets.Count == 0)
            {
                error = marked.Count + " element(s) carry an ownership mark version 2, but every one is owned by another user of this " +
                        "collaborative model; ask them to synchronize and relinquish, then run this again.";
                return null;
            }

            var byCategory = new JObject();
            foreach (var g in marked.GroupBy(e => e is ElementType ? "(types)" : (e.Category?.Name ?? "(no category)"))
                                    .OrderByDescending(g => g.Count()).Take(25))
                byCategory[g.Key] = g.Count();

            int total = marked.Count, skippedCount = skipped.Count;
            var targetSet = new HashSet<long>(targets.Select(Rid.Value));
            var p = new Plan
            {
                Op = "purge_legacy_marks", Subject = "HorizunOwnershipV2", Category = "ExtensibleStorage", Action = PlannedAction.Delete,
                ValuesLost = targets.Count,
                Apply = (app, d, def, s) =>
                {
                    Schema schema = Schema.Lookup(HrzOwnership.SchemaGuidV2);
                    foreach (ElementId id in targets)
                    {
                        Element e = d.GetElement(id);
                        if (e != null) e.DeleteEntity(schema);
                    }
                },
                Verify = (d, def, s) =>
                {
                    List<Element> left = LegacyMarked(d);
                    long targetsLeft = left.LongCount(e => targetSet.Contains(Rid.Value(e.Id)));
                    return new PostconditionCheck("targets_still_marked", "v2_marks_after")
                        .Compare("targets_still_marked", 0L, targetsLeft)
                        .Compare("v2_marks_after", (long)skippedCount, (long)left.Count);
                },
                Report = (d, def, s) =>
                {
                    List<Element> left = LegacyMarked(d);
                    return new JObject
                    {
                        ["v2_marks_before"] = total, ["removed"] = total - left.Count, ["v2_marks_after"] = left.Count,
                        ["skipped_owned_by_others"] = skippedCount
                    };
                }
            };
            p.Before["v2_marks"] = total.ToString();
            p.Before["targets"] = string.Join(",", targetSet.OrderBy(x => x));
            p.Preview = new JObject
            {
                ["v2_marks_before"] = total, ["to_remove"] = targets.Count, ["skipped_owned_by_others"] = skippedCount,
                ["skipped_ids"] = new JArray(skipped.Take(50)), ["by_category"] = byCategory,
                ["keeps"] = "the elements, their version 1 and version 3 marks and every other schema",
                ["personal_fields"] = "not shown"
            };
            return p;
        }
    }
}
