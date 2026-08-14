// -----------------------------------------------------------------------------
// Horizun Server tests - a durable ledger that lives in the test's own temp dir.
//
// horizun_excel_write_rows now claims a durable idempotency key before it writes.
// The production ledger writes into %USERPROFILE%\.horizun\idempotency, and a
// test suite has no business appending to the machine's real operation record -
// nor should one test run's keys be visible to the next. Every Excel test builds
// its ledger through here instead.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;

namespace Horizun.Server.Tests
{
    internal static class ExcelTestLedger
    {
        private static readonly List<string> Created = new List<string>();

        /// <summary>A ledger with a private directory, fresh for each call.</summary>
        internal static DurableCommandLedger New()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-xls-ledger-" + Guid.NewGuid().ToString("N"));
            lock (Created) Created.Add(dir);
            return new DurableCommandLedger(() => dir);
        }

        /// <summary>
        /// A ledger that keeps ONE directory across calls, for a test that needs two
        /// Handle invocations to see each other's keys.
        /// </summary>
        internal static Func<DurableCommandLedger> Shared()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-xls-ledger-" + Guid.NewGuid().ToString("N"));
            lock (Created) Created.Add(dir);
            return () => new DurableCommandLedger(() => dir);
        }

        internal static void SweepAll()
        {
            lock (Created)
            {
                foreach (string dir in Created)
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                Created.Clear();
            }
        }
    }
}
