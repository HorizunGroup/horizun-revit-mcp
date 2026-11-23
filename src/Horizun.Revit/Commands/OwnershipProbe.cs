// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Reads the evidence OwnershipRules decides on (FIELD FEEDBACK 2.6,
// 4.3, 4.4): the Horizun ownership mark, this session's own additions, and the
// worksharing creator. Read-only; it never borrows anything to answer.
//
// The ownership MARK (4.1) is stamped by the Pro edition's marking; this probe
// asks for it through ReadMark so both halves agree on one definition. ReadMark reads the
// HRZ Extensible Storage stamp (Core/HrzOwnership.cs, 4.1); an unmarked element
// falls to the other two pieces of evidence.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class OwnershipProbe
    {
        /// <summary>
        /// What was read about the element's Horizun mark. The verdict believes it only through <see cref="HrzOwnershipRules.Verdict"/> (R2V M-2): the mark of
        /// this user, not a copy made in this session, not version 1 (no user), not another user's. The same question the identity guard asks.
        /// </summary>
        internal static Func<Element, HrzOwnershipRules.MarkFacts> ReadMark = e => HrzOwnership.ReadFacts(e);

        /// <summary>
        /// Every UniqueId a Horizun write of this session CREATED in this document (the ledger of creations, R2V M-1): what a script or a copy
        /// between documents added is not in it. UniqueIds, not ElementIds (R2U M-A).
        /// </summary>
        internal static HashSet<string> SessionAdded(Document doc)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try { foreach (string uid in SessionCreationLedger.All(ChangeWatch.Key(doc))) set.Add(uid); }
            catch { }
            return set;
        }

        internal static OwnershipVerdict Of(Document doc, Element e, HashSet<string> sessionAdded, out string creator)
        {
            creator = null;
            bool workshared = false;
            try { workshared = doc.IsWorkshared; } catch { }
            HrzOwnershipRules.MarkFacts mark = null;
            try { mark = ReadMark?.Invoke(e); } catch { mark = null; }
            bool session = sessionAdded != null && SafeUid(e) != null && sessionAdded.Contains(SafeUid(e));
            bool? creatorIsMe = null;
            if (workshared)
            {
                try
                {
                    creator = WorksharingUtils.GetWorksharingTooltipInfo(doc, e.Id).Creator;
                    creatorIsMe = WorksharingStatusRules.SameUser(creator, doc.Application.Username);
                }
                catch { creatorIsMe = null; }
            }
            return HrzOwnershipRules.Verdict(workshared, mark ?? new HrzOwnershipRules.MarkFacts { Present = false }, HrzOwnership.Session,
                                             HrzOwnership.CurrentUserRef(doc), session, creatorIsMe, creator);
        }

        private static string SafeUid(Element e) { try { return e.UniqueId; } catch { return null; } }

        internal static JObject Row(Document doc, Element e, HashSet<string> sessionAdded)
        {
            string creator;
            OwnershipVerdict v = Of(doc, e, sessionAdded, out creator);
            return new JObject
            {
                ["element_id"] = Rid.Value(e.Id), ["own"] = v.Own, ["evidence"] = v.Evidence,
                ["creator"] = creator, ["reason"] = v.Reason
            };
        }
    }

    /// <summary>The markers of a view in other views, and its "Hide at scales coarser than".</summary>
    internal static class ViewMarkers
    {
        private static readonly BuiltInCategory[] MarkerCategories =
            { BuiltInCategory.OST_Viewers, BuiltInCategory.OST_Sections, BuiltInCategory.OST_Callouts, BuiltInCategory.OST_Elev };

        internal static ElementMulticategoryFilter Filter() => new ElementMulticategoryFilter(MarkerCategories.ToList());

        /// <summary>The marker elements that stand for <paramref name="view"/> in other views (same name, marker category).</summary>
        internal static List<ElementId> MarkersOf(Document doc, View view)
        {
            string name = view.Name;
            return new FilteredElementCollector(doc).WherePasses(Filter()).WhereElementIsNotElementType()
                .Where(e => { try { return e.Id != view.Id && string.Equals(e.Name, name, StringComparison.Ordinal); } catch { return false; } })
                .Select(e => e.Id).ToList();
        }

        // Both pulldowns exist on a view; only the one matching the project's units is
        // writable (MEASURED live, Revit 2026, imperial Snowdon sample: METRIC read-only).
        private static Parameter Coarser(Element e)
        {
            Parameter metric = e.get_Parameter(BuiltInParameter.SECTION_COARSER_SCALE_PULLDOWN_METRIC);
            Parameter imperial = e.get_Parameter(BuiltInParameter.SECTION_COARSER_SCALE_PULLDOWN_IMPERIAL);
            if (metric != null && !metric.IsReadOnly) return metric;
            if (imperial != null && !imperial.IsReadOnly) return imperial;
            return metric ?? imperial;
        }

        // A CALLOUT keeps the value on its marker element, not on the view: MEASURED live
        // (Revit 2026) - the callout view's parameter is read-only, the marker's carries it.
        private static Parameter Writable(View v, out Element on)
        {
            on = v;
            Parameter p = Coarser(v);
            if (p != null && !p.IsReadOnly) return p;
            foreach (ElementId id in MarkersOf(v.Document, v))
            {
                Element m = v.Document.GetElement(id);
                Parameter pm = m == null ? null : Coarser(m);
                if (pm != null && !pm.IsReadOnly) { on = m; return pm; }
            }
            return null;
        }

        internal static int? ReadCoarser(View v)
        {
            try
            {
                Element on;
                Parameter p = Writable(v, out on) ?? Coarser(v);
                return p == null || !p.HasValue ? (int?)null : p.AsInteger();
            }
            catch { return null; }
        }

        /// <summary>Writes "Hide at scales coarser than" (on the view, or on a callout's marker); throws with the reason when nothing carries it writable.</summary>
        internal static void SetCoarser(View v, int scale)
        {
            Element on;
            Parameter p = Writable(v, out on);
            if (p == null)
            {
                if (Coarser(v) == null) throw new InvalidOperationException("view '" + v.Name + "' has no 'Hide at scales coarser than' parameter (only sections, callouts and elevations do).");
                throw new InvalidOperationException("'Hide at scales coarser than' is read-only on view '" + v.Name + "' and on its markers" +
                    (v.ViewTemplateId != ElementId.InvalidElementId ? " (its view template may control it)." : "."));
            }
            if (!p.Set(scale)) throw new InvalidOperationException("Revit refused 'Hide at scales coarser than' = " + scale + " on view '" + v.Name + "'.");
        }
    }
}
