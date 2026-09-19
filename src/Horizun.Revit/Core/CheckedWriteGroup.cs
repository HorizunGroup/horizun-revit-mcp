// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A WRITE THAT IS KEPT ONLY IF ITS OWN CHECK PASSES.
//
// Some results can only be judged after Revit has built them: a transition fitting's
// length is Revit's, not the caller's, and whether it fits the space the drawing gave
// it is known only once it exists. This group wraps such a write: the delegated command
// commits inside it, the caller measures, and the group is then either ASSIMILATED (kept,
// one undo step) or ROLLED BACK through Guard - never left open, and the status Revit
// returned for the rollback is kept, because an unconfirmed rollback is an uncertain model.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.DB;

namespace Horizun.Revit.Core
{
    public sealed class CheckedWriteGroup : IDisposable
    {
        private TransactionGroup _group;

        public bool Started { get; private set; }
        /// <summary>kept | rolled_back | uncertain | not_started</summary>
        public string Outcome { get; private set; } = "not_started";
        public string RollbackStatus { get; private set; }

        public CheckedWriteGroup(Document doc, string name)
        {
            try
            {
                _group = new TransactionGroup(doc, name);
                Started = _group.Start() == TransactionStatus.Started;
                if (!Started) { _group.Dispose(); _group = null; }
            }
            catch { _group = null; Started = false; }
        }

        /// <summary>Keep what was written inside the group.</summary>
        public void Keep()
        {
            if (_group == null) return;
            try { Outcome = _group.Assimilate() == TransactionStatus.Committed ? "kept" : "uncertain"; }
            catch { Outcome = "uncertain"; }
            finally { _group.Dispose(); _group = null; }
        }

        /// <summary>Undo everything written inside the group; also what Dispose does when nothing was decided.</summary>
        public void Undo()
        {
            if (_group == null) return;
            try
            {
                Guard.RollbackResult r = Guard.RollBack(_group);
                RollbackStatus = r.StatusName;
                Outcome = r.Confirmed ? "rolled_back" : "uncertain";
            }
            catch (Exception ex) { RollbackStatus = "threw: " + ex.Message; Outcome = "uncertain"; }
            finally { _group.Dispose(); _group = null; }
        }

        public void Dispose() => Undo();
    }
}
