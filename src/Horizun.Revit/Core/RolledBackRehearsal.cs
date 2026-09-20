// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A REHEARSAL THAT REALLY RUNS, AND LEAVES NOTHING.
//
// Some rehearsals cannot be judged one item at a time: each MEP fitting trims the
// runs it joins, so the next fitting meets a shorter run. MEASURED (campaign 6):
// eight elbows rehearsed one by one, one could be built. A sequential rehearsal
// carries every item out inside a transaction group and rolls the group back.
//
// This class is that group and nothing else: it opens, it rolls back on Dispose
// through Guard.RollBack, and it KEEPS the status Revit returned - a rollback that
// did not report RolledBack means the model is in an uncertain state, and the
// caller must say so rather than claim nothing was written.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.DB;

namespace Horizun.Revit.Core
{
    public sealed class RolledBackRehearsal : IDisposable
    {
        private TransactionGroup _group;

        /// <summary>True when the group started; false means the caller must fall back and say so.</summary>
        public bool Started { get; private set; }

        /// <summary>The status the rollback returned, once disposed. Null before.</summary>
        public string RollbackStatus { get; private set; }

        /// <summary>True only when Revit reported RolledBack.</summary>
        public bool RollbackConfirmed { get; private set; }

        public RolledBackRehearsal(Document doc, string name)
        {
            try
            {
                _group = new TransactionGroup(doc, name);
                Started = _group.Start() == TransactionStatus.Started;
                if (!Started) { _group.Dispose(); _group = null; }
            }
            catch
            {
                _group = null;
                Started = false;
            }
        }

        public void Dispose()
        {
            if (_group == null) return;
            try
            {
                Guard.RollbackResult r = Guard.RollBack(_group);
                RollbackStatus = r.StatusName;
                RollbackConfirmed = r.Confirmed;
            }
            catch (Exception ex)
            {
                RollbackStatus = "threw: " + ex.Message;
                RollbackConfirmed = false;
            }
            finally
            {
                _group.Dispose();
                _group = null;
            }
        }
    }
}
