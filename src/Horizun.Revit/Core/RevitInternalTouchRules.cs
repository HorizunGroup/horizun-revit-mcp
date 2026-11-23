// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// REVIT'S OWN BROWSER VIEWS ARE NOT MODEL CHANGES. Measured 2026-11-17 (Revit 2025, a
// new project from Systems-DefaultESPESP.rte, and before that on 5.0.5): the rehearsal
// of horizun_apply_cad_plan (Pro edition) created pipes inside a transaction group that it rolled
// back, and its reply still said model_changes {modified 1: View "System Browser"}.
// Revit refreshes the System Browser (and the Project Browser) whenever MEP systems or
// browser-listed elements come and go, and that refresh is not taken back by the
// rollback event. The model's content is unchanged; the call said otherwise.
//
// The rule is deliberately narrow: only a MODIFIED element that is a View whose
// ViewType is SystemBrowser or ProjectBrowser, and only on a call whose transactions
// were rolled back (Revit raised a rollback or undo event) and that left NOTHING ELSE
// in the model. A committed write keeps them in model_changes, the change ledger, the
// sync reminder and the undo records: there the refresh came with real work and is not
// ours to judge. Set aside, they are not dropped: ChangeWatch moves them to a separate
// set that the reply reports as revit_internal_touches.
// Revit-free so it can be unit-tested (tests/Horizun.Core.Tests).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class RevitInternalTouchRules
    {
        public const string Means =
            "Revit refreshed its own browser pane (System Browser / Project Browser) during this call, whose transactions were rolled " +
            "back and which left nothing else in the model - Revit does that when MEP or browser-listed elements are created and " +
            "rolled back. Only then are those views set aside: not counted in model_changes, not recorded as this document's last " +
            "write, not spatially checked. On a call that kept a change they stay in model_changes.";

        /// <summary>
        /// True when a MODIFIED element is one of Revit's browser panes: its .NET class is View (or a subclass) and its
        /// ViewType name is SystemBrowser or ProjectBrowser. Anything else - an Internal view, a drafting view, a schedule,
        /// an element whose type could not be read - stays a model change.
        /// </summary>
        public static bool IsBrowserPane(bool isView, string viewTypeName)
        {
            if (!isView || string.IsNullOrEmpty(viewTypeName)) return false;
            return string.Equals(viewTypeName, "SystemBrowser", StringComparison.Ordinal) ||
                   string.Equals(viewTypeName, "ProjectBrowser", StringComparison.Ordinal);
        }

        /// <summary>
        /// ChangeWatch.Settle's reconciliation, in order: every set is reconciled against the model FIRST (added or modified ids
        /// that no longer resolve are gone, the bookkeeping element is dropped, deleted ids that resolve again came back), and
        /// only then are browser panes set aside - so a deletion the rollback restored does not count as something left behind.
        /// </summary>
        public static void Reconcile(HashSet<long> added, HashSet<long> modified, HashSet<long> deleted, HashSet<long> internalTouches,
            bool sawRollback, Func<long, bool> exists, Func<long, bool> bookkeeping, Func<long, bool> isBrowserPane)
        {
            added.RemoveWhere(v => !exists(v));
            modified.RemoveWhere(v => !exists(v) || bookkeeping(v));
            deleted.RemoveWhere(v => exists(v));
            foreach (long v in SetAside(modified, added.Count, deleted.Count, sawRollback, isBrowserPane))
            { modified.Remove(v); internalTouches.Add(v); }
        }

        /// <summary>
        /// The ids Settle moves out of Modified: browser panes, and only when the call saw a rollback or undo and nothing but
        /// browser panes is left (no added, no deleted, no other modified element). Otherwise none - a committed write keeps them.
        /// </summary>
        public static IList<long> SetAside(IEnumerable<long> modified, int addedLeft, int deletedLeft, bool sawRollback, Func<long, bool> isBrowserPane)
        {
            List<long> all = (modified ?? Enumerable.Empty<long>()).ToList();
            if (!sawRollback || addedLeft > 0 || deletedLeft > 0 || all.Count == 0 || isBrowserPane == null) return new List<long>();
            List<long> panes = all.Where(isBrowserPane).ToList();
            return panes.Count == all.Count ? panes : new List<long>();
        }
    }
}
