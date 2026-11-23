using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class UnrecordedWriteUndoTests
    {
        [Fact]
        public void An_unrecorded_write_blocks_undo_last_and_names_the_tool()
        {
            var batches = new List<UndoBatch>
            {
                new UndoBatch { Id = "a", Tool = "horizun_create_elements", CreatedUtc = "2026-11-11T10:00:00.0000000Z", Undoable = true,
                    Entries = new List<UndoEntry> { new UndoEntry { Op = "created" } } },
                new UndoBatch { Id = "b", Tool = "horizun_manage_system_types", CreatedUtc = "2026-11-11T10:05:00.0000000Z", Undoable = false,
                    NotUndoableReason = UndoRules.UnrecordedWriteReason("horizun_manage_system_types") }
            };
            Assert.Null(UndoRules.Last(batches, out string refusal));
            Assert.Contains("Older batches are NOT undone past it", refusal);
            Assert.True(UndoRules.UnrecordedWriteExempt("horizun_undo"));
            Assert.True(UndoRules.UnrecordedWriteExempt("horizun_save_document"));
        }

        [Fact]
        public void Both_dispatch_paths_mark_an_unrecorded_write_after_the_python_record_and_before_the_ownership_stamp()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Horizun.Revit"))) directory = directory.Parent;
            Assert.NotNull(directory);
            string source = File.ReadAllText(Path.Combine(directory.FullName, "src", "Horizun.Revit", "Core", "Dispatcher.cs"));
            Assert.Equal(2, Regex.Matches(source, @"int undoRecordsBefore = Horizun\.Revit\.Commands\.UndoCapture\.RecordCount;").Count);
            Assert.Equal(2, Regex.Matches(source, @"PythonUndoRecord\.AfterCommand\([^\n]*\n\s*(//[^\n]*\n\s*)?Horizun\.Revit\.Commands\.UnrecordedWriteUndo\.AfterCommand\(").Count);
            string capture = File.ReadAllText(Path.Combine(directory.FullName, "src", "Horizun.Revit", "Commands", "UndoCapture.cs"));
            Assert.Contains("RecordCount++;", capture);
        }
    }
}
