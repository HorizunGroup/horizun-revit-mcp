// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// WHO GETS A PIPE CONNECTION WHEN THEY ARE ALL TAKEN.
//
// The add-in serves a bounded number of connections, and every ordinary command
// waiting in Revit's queue holds one. With all of them held, the control messages
// that exist precisely for that moment - remove my request from the queue, what is
// my request doing - were refused as "too many connections" (audit 2026-10-08). A
// few slots are now reserved for them: a connection admitted on a reserved slot
// may only carry a control message (__horizun_*).
// -----------------------------------------------------------------------------
using System;
using System.Threading;

namespace Horizun.Revit.Core
{
    public sealed class PipeAdmission
    {
        /// <summary>Slots held back for control messages only.</summary>
        public const int ControlReserve = 4;

        public enum Slot { None, Ordinary, ControlOnly }

        /// <summary>How many over-limit connections may be answered at once; past it they are closed unanswered.</summary>
        public const int RefusalReserve = 8;

        private readonly SemaphoreSlim _ordinary;
        private readonly SemaphoreSlim _control;
        private readonly SemaphoreSlim _refusals;

        public PipeAdmission(int ordinarySlots, int controlSlots = ControlReserve, int refusalSlots = RefusalReserve)
        {
            _ordinary = new SemaphoreSlim(ordinarySlots, ordinarySlots);
            _control = new SemaphoreSlim(controlSlots, controlSlots);
            _refusals = new SemaphoreSlim(refusalSlots, refusalSlots);
        }

        /// <summary>
        /// May one more over-limit connection be ANSWERED (on a thread of its own, which
        /// reads the request before writing the refusal)? When not, the caller closes it
        /// unanswered. The refusal used to be written on the listener thread before the
        /// request was read; the pipe has no buffer, so the client's write and the
        /// listener's write blocked each other and the listener stopped accepting
        /// (measured live 2026-10-08).
        /// </summary>
        public bool TryTakeRefusal() => _refusals.Wait(0);

        public void ReleaseRefusal() => _refusals.Release();

        /// <summary>An ordinary slot if one is free, else a control-only slot, else none.</summary>
        public Slot TryAdmit()
        {
            if (_ordinary.Wait(0)) return Slot.Ordinary;
            if (_control.Wait(0)) return Slot.ControlOnly;
            return Slot.None;
        }

        public void Release(Slot slot)
        {
            if (slot == Slot.Ordinary) _ordinary.Release();
            else if (slot == Slot.ControlOnly) _control.Release();
        }

        /// <summary>A control message: answered on the pipe thread, never queued behind Revit's UI thread.</summary>
        public static bool IsControl(string command) =>
            command != null && command.StartsWith("__horizun_", StringComparison.Ordinal);

        /// <summary>May a connection admitted on this slot carry this command?</summary>
        public static bool Allows(Slot slot, string command) =>
            slot == Slot.Ordinary || (slot == Slot.ControlOnly && IsControl(command));
    }
}
