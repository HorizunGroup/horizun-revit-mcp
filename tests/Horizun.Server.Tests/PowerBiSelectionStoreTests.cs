// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Audit 2026-10-08, the Power BI selection channel file: it was written to a
// FIXED "<file>.tmp", then the live file was DELETED and the temporary moved in -
// a window with no selection at all, and two publishers sharing one temporary.
// And channel "" was accepted, becoming a file literally named ".json".
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Threading;
using Horizun.Revit.Core;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class PowerBiSelectionStoreTests : IDisposable
    {
        private readonly ExcelBackupRoot _root = new ExcelBackupRoot();
        public void Dispose() => _root.Dispose();

        private static JObject Publish(string channel, params string[] keys) => new JObject
        {
            ["operation"] = "publish", ["channel"] = channel, ["document_title"] = "Model",
            ["document_fingerprint"] = "fp", ["key_column"] = "Mark", ["key_parameter"] = "Mark",
            ["keys"] = new JArray(keys)
        };

        [Fact]
        public void An_empty_channel_is_refused()
        {
            Assert.Throws<ToolRefusal>(() => PowerBiSelection.Handle(Publish("", "M-1"), CancellationToken.None));
            Assert.False(Directory.Exists(Path.Combine(_root.Root, "selection")) &&
                         File.Exists(Path.Combine(_root.Root, "selection", ".json")));
        }

        [Fact]
        public void Publishing_twice_replaces_the_selection_and_leaves_no_temporary()
        {
            PowerBiSelection.Handle(Publish("coord", "M-1"), CancellationToken.None);
            JObject second = PowerBiSelection.Handle(Publish("coord", "M-2", "M-3"), CancellationToken.None);
            string dir = Path.Combine(_root.Root, "selection");
            Assert.Equal(new[] { Path.Combine(dir, "coord.json") }, Directory.GetFiles(dir));
            JObject read = PowerBiSelection.Handle(new JObject { ["operation"] = "read", ["channel"] = "coord", ["document_fingerprint"] = "fp" }, CancellationToken.None);
            Assert.Contains("M-3", read.ToString());
            Assert.DoesNotContain("\"M-1\"", read.ToString());
        }

        // Backlog #24: the contract says a read while a DIFFERENT document is in play
        // returns no keys. A read that names NO document at all was handed the keys
        // anyway - with whichever model happened to be open.
        [Fact]
        public void A_read_that_names_no_document_returns_no_keys()
        {
            PowerBiSelection.Handle(Publish("coord", "M-1", "M-2"), CancellationToken.None);
            JObject read = PowerBiSelection.Handle(new JObject { ["operation"] = "read", ["channel"] = "coord" }, CancellationToken.None);
            Assert.True(read.Value<bool>("present"));
            Assert.Empty((JArray)read["keys"]);
            Assert.Contains("document_fingerprint", read.Value<string>("document_unidentified"));

            JObject matching = PowerBiSelection.Handle(new JObject
            {
                ["operation"] = "read", ["channel"] = "coord", ["document_fingerprint"] = "fp"
            }, CancellationToken.None);
            Assert.Equal(2, ((JArray)matching["keys"]).Count);
        }
    }
}
