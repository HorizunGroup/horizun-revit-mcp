// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// 4.2: N5's Python undo batch (PythonUndoRecord) and 4.2-a's ownership mark after a
// failure (HrzOwnership.AfterCommand) run at the same point of the dispatcher. The undo
// batch must be recorded FIRST: the stamp opens a transaction of its own, and what the
// batch captures is the script's work, not the mark. A source check keeps a later edit
// from swapping them.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DispatcherPostCommandOrderTests
    {
        private static string Dispatcher()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                string p = Path.Combine(d.FullName, "src", "Horizun.Revit", "Core", "Dispatcher.cs");
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) && File.Exists(p)) return File.ReadAllText(p);
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        [Fact]
        public void Python_undo_batch_is_recorded_before_the_ownership_mark()
        {
            string s = Dispatcher();
            int undo = s.IndexOf("PythonUndoRecord.AfterCommand(req.Name", StringComparison.Ordinal);
            int mark = s.IndexOf("HrzOwnership.AfterCommand(req.Name", StringComparison.Ordinal);
            Assert.True(undo > 0, "the dispatcher no longer records the Python undo batch");
            Assert.True(mark > 0, "the dispatcher no longer stamps ownership");
            Assert.True(undo < mark, "the undo batch must be recorded before the ownership mark");
            // Review m1: the async path journals too (once), and records before ITS ownership mark.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(s, @"PythonUndoRecord\.AfterCommand\(").Count);
            int asyncUndo = s.IndexOf("PythonUndoRecord.AfterCommand(work.Command", StringComparison.Ordinal);
            int asyncMark = s.IndexOf("HrzOwnership.AfterCommand(work.Command", StringComparison.Ordinal);
            Assert.True(asyncUndo > 0 && asyncUndo < asyncMark, "the async path must record the undo batch before the ownership mark");
        }
    }
}
