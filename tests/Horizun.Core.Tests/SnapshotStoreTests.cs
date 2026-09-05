// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The snapshot store, proved by running it. A snapshot is read weeks later by
// somebody comparing today against a run nobody remembers, so every test here
// is about that gap in time: a file that was half-written must not read as an
// empty run, and a file that was edited must not read at all.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SnapshotStoreTests
    {
        private static JObject Content()
        {
            return JObject.Parse(@"{
                ""schema"": ""horizun.diagnostics-snapshot/1"",
                ""model_fingerprint"": ""aaa"",
                ""taken_utc"": ""2026-01-01T00:00:00Z"",
                ""checks"": [ { ""check"": ""warnings"", ""count"": 5 } ]
            }");
        }

        // ------------------------------------------------------------ hashing

        [Fact]
        public void A_snapshot_carries_a_hash_of_its_own_content()
        {
            string sha;
            JObject env = SnapshotStore.Envelope(Content(), out sha);
            Assert.Equal(SnapshotStore.EnvelopeSchema, env.Value<string>("schema"));
            Assert.Equal(sha, env.Value<string>("sha256"));

            SnapshotReadResult r = SnapshotStore.Read(env.ToString(Formatting.Indented));
            Assert.True(r.Ok, r.Message);
            Assert.Equal(sha, r.Sha256);
        }

        [Fact]
        public void A_snapshot_whose_content_was_edited_is_refused_and_not_repaired()
        {
            // THE ONE THAT MATTERS. A trend built on a changed snapshot is worse
            // than no trend, because it looks like evidence.
            string sha;
            JObject env = SnapshotStore.Envelope(Content(), out sha);
            env["content"]["checks"][0]["count"] = 0;      // somebody "fixed" the model in a text editor

            SnapshotReadResult r = SnapshotStore.Read(env.ToString());
            Assert.False(r.Ok);
            Assert.Equal(SnapshotStoreCodes.HashMismatch, r.Code);
            Assert.Contains("refused rather than repaired", r.Message);
        }

        [Fact]
        public void The_hash_is_stable_across_key_order()
        {
            // Two writes of the same facts must hash the same, or every snapshot
            // looks tampered with the moment a field moves.
            JObject a = JObject.Parse(@"{ ""b"": 2, ""a"": 1 }");
            JObject b = JObject.Parse(@"{ ""a"": 1, ""b"": 2 }");
            Assert.Equal(SnapshotStore.Sha256Of(SnapshotStore.Canonical(a)),
                         SnapshotStore.Sha256Of(SnapshotStore.Canonical(b)));
        }

        [Fact]
        public void A_different_value_hashes_differently()
        {
            JObject a = JObject.Parse(@"{ ""a"": 1 }");
            JObject b = JObject.Parse(@"{ ""a"": 2 }");
            Assert.NotEqual(SnapshotStore.Sha256Of(SnapshotStore.Canonical(a)),
                            SnapshotStore.Sha256Of(SnapshotStore.Canonical(b)));
        }

        // ----------------------------------------------------- partial files

        [Fact]
        public void A_file_that_does_not_parse_is_partial_and_never_an_empty_run()
        {
            SnapshotReadResult r = SnapshotStore.Read("{ \"content\": { \"checks\": [");
            Assert.False(r.Ok);
            Assert.Equal(SnapshotStoreCodes.Partial, r.Code);
            Assert.Contains("NOT an empty run", r.Message);
        }

        [Fact]
        public void A_file_that_parses_without_an_envelope_is_partial()
        {
            SnapshotReadResult r = SnapshotStore.Read(@"{ ""checks"": [] }");
            Assert.False(r.Ok);
            Assert.Equal(SnapshotStoreCodes.Partial, r.Code);
        }

        [Fact]
        public void A_missing_file_is_not_found_rather_than_partial()
        {
            // Different problems: one means nobody has run this yet, the other
            // means a run was interrupted.
            SnapshotReadResult r = SnapshotStore.Read(null);
            Assert.Equal(SnapshotStoreCodes.NotFound, r.Code);
        }

        [Fact]
        public void A_snapshot_from_another_envelope_schema_is_named_as_such()
        {
            JObject env = JObject.Parse(@"{ ""schema"": ""something/9"", ""sha256"": ""x"",
                                            ""content"": { ""a"": 1 } }");
            SnapshotReadResult r = SnapshotStore.Read(env.ToString());
            Assert.Equal(SnapshotStoreCodes.WrongSchema, r.Code);
            Assert.Contains("something/9", r.Message);
        }

        // ------------------------------------------------------ sanitisation

        [Fact]
        public void A_personal_path_never_reaches_a_stored_snapshot()
        {
            var content = JObject.Parse(@"{ ""central_model_path"": ""C:\\Users\\someone\\Projects\\Tower.rvt"" }");
            int redacted = SnapshotStore.Sanitise(content);
            Assert.Equal(1, redacted);
            Assert.DoesNotContain("someone", content.Value<string>("central_model_path"));
            Assert.Contains("<redacted>", content.Value<string>("central_model_path"));
        }

        [Fact]
        public void A_network_share_is_redacted_because_it_names_somebody_elses_server()
        {
            var content = JObject.Parse(@"{ ""path"": ""\\\\bimserver01\\Projects\\Tower.rvt"" }");
            Assert.Equal(1, SnapshotStore.Sanitise(content));
            Assert.DoesNotContain("bimserver01", content.Value<string>("path"));
        }

        [Fact]
        public void Anything_announcing_itself_as_a_secret_is_redacted()
        {
            var content = JObject.Parse(@"{ ""note"": ""token: abc123secret"" }");
            Assert.Equal(1, SnapshotStore.Sanitise(content));
            Assert.DoesNotContain("abc123secret", content.Value<string>("note"));
        }

        [Fact]
        public void Sanitisation_reaches_nested_objects_and_arrays()
        {
            var content = JObject.Parse(@"{ ""a"": { ""b"": [ ""C:\\Users\\x\\f.rvt"" ] } }");
            Assert.Equal(1, SnapshotStore.Sanitise(content));
            Assert.DoesNotContain("Users", content.ToString());
        }

        [Fact]
        public void Ordinary_values_are_left_alone()
        {
            var content = JObject.Parse(@"{ ""title"": ""Tower - Structural"", ""count"": 42 }");
            Assert.Equal(0, SnapshotStore.Sanitise(content));
            Assert.Equal("Tower - Structural", content.Value<string>("title"));
        }

        [Fact]
        public void The_write_reports_how_many_values_it_redacted()
        {
            // Silent redaction would leave a caller comparing two snapshots unaware
            // that something was removed from one of them.
            var content = JObject.Parse(@"{ ""p"": ""C:\\Users\\a\\x.rvt"", ""q"": ""/home/b/y.rvt"" }");
            SnapshotWriteResult w = SnapshotStore.Write("root", "s.json", content, (path, text) => true);
            Assert.True(w.Ok);
            Assert.Equal(2, w.RedactedValues);
            Assert.Contains("redacted", w.Message);
        }

        [Fact]
        public void Sanitisation_happens_before_the_hash_so_the_stored_file_verifies()
        {
            var content = JObject.Parse(@"{ ""p"": ""C:\\Users\\a\\x.rvt"" }");
            SnapshotWriteResult w = SnapshotStore.Write("root", "s.json", content, (path, text) =>
            {
                // The text handed to the writer must verify on its own.
                SnapshotReadResult back = SnapshotStore.Read(text);
                Assert.True(back.Ok, back.Message);
                Assert.DoesNotContain("Users", text);
                return true;
            });
            Assert.True(w.Ok);
        }

        // ---------------------------------------------------------- location

        [Fact]
        public void Snapshots_live_under_horizuns_own_root_and_not_beside_the_model()
        {
            Assert.Equal(System.IO.Path.Combine("root", "snapshots"), SnapshotStore.DirectoryUnder("root"));
            Assert.Null(SnapshotStore.DirectoryUnder(null));
            Assert.Contains("NEVER beside the .rvt", SnapshotStore.LocationMeans);
        }

        [Fact]
        public void A_write_with_no_directory_is_refused_rather_than_guessed()
        {
            SnapshotWriteResult w = SnapshotStore.Write(null, "s.json", Content(), (p, t) => true);
            Assert.False(w.Ok);
            Assert.Equal(SnapshotStoreCodes.RefusedPath, w.Code);
        }

        [Fact]
        public void A_write_that_fails_is_reported_as_failed_and_never_as_written()
        {
            SnapshotWriteResult w = SnapshotStore.Write("root", "s.json", Content(), (p, t) => false);
            Assert.False(w.Ok);
            Assert.Equal(SnapshotStoreCodes.Unreadable, w.Code);
        }
    }
}
