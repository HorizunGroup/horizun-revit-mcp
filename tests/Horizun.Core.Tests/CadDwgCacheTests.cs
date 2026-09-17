// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A CACHE THAT SERVES THE WRONG REVISION IS WORSE THAN NO CACHE.
//
// Six minutes per read of one permit drawing, six reads of the same file in one
// session, all of them to change a rule or a zone. So extraction is cached and
// interpretation is not - and the whole value of that depends on the key being
// honest about what the reading DEPENDED on.
//
// These fix what invalidates a reading: the file, its external references, the
// extractor's own text, the engine, and the options. What must NOT invalidate it
// is anything about the rules, because those are what the cache exists to let
// somebody iterate on.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadDwgCacheTests : IDisposable
    {
        private readonly string _root;
        private readonly string _old;

        public CadDwgCacheTests()
        {
            _old = Environment.GetEnvironmentVariable("HORIZUN_DWG_CACHE");
            _root = Path.Combine(Path.GetTempPath(), "hz-dwgcache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable("HORIZUN_DWG_CACHE", _root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("HORIZUN_DWG_CACHE", _old);
            try { Directory.Delete(_root, true); } catch { }
        }

        private string File_(string name, string content)
        {
            string p = Path.Combine(_root, name);
            File.WriteAllText(p, content);
            return p;
        }

        /// <summary>A reading of a drawing that references one other file.</summary>
        private static CadDwgReading ReadingWith(string referenceName, string referencePath)
        {
            var r = new CadDwgReading { DrawingName = "unit.dwg", Complete = true };
            r.ExternalReferences.Add(new CadIrExternalReference
            {
                Name = referenceName,
                Path = referencePath
            });
            return r;
        }

        [Fact]
        public void The_same_file_read_the_same_way_is_the_same_key()
        {
            string a = CadDwgCache.KeyFor("abc", "AcCoreConsole 25.0", "mm_per_unit=(none)");
            string b = CadDwgCache.KeyFor("abc", "AcCoreConsole 25.0", "mm_per_unit=(none)");
            Assert.Equal(a, b);
        }

        [Fact]
        public void A_new_issue_of_the_drawing_is_a_different_key()
        {
            // THE CASE THAT MATTERS MOST. A re-cut DWG keeps its name and its
            // path; only its bytes change, and serving the old reading would
            // convert last week's drawing while reporting this week's file.
            Assert.NotEqual(CadDwgCache.KeyFor("aaa", "e", "o"),
                            CadDwgCache.KeyFor("bbb", "e", "o"));
        }

        [Fact]
        public void A_different_engine_or_option_is_a_different_key()
        {
            Assert.NotEqual(CadDwgCache.KeyFor("abc", "AcCoreConsole 25.0", "o"),
                            CadDwgCache.KeyFor("abc", "AcCoreConsole 24.3", "o"));
            Assert.NotEqual(CadDwgCache.KeyFor("abc", "e", "mm_per_unit=1"),
                            CadDwgCache.KeyFor("abc", "e", "mm_per_unit=25.4"));
        }

        [Fact]
        public void The_extractor_is_part_of_the_key()
        {
            // The .lsp IS the reader. Its fingerprint is in every key, so editing
            // it retires every reading the old one made - which is what stopped
            // the model-space fix from being invisible behind a cache hit.
            string fingerprint = CadDwgCache.ReaderFingerprint();
            Assert.False(string.IsNullOrWhiteSpace(fingerprint));
            Assert.Contains(fingerprint, CadDwgCache.KeyFor("abc", "e", "o") + fingerprint);

            // And it is derived from the forms themselves, not from a constant
            // somebody has to remember to bump.
            Assert.Equal(fingerprint, CadDwgCache.ReaderFingerprint());
        }

        [Fact]
        public void A_reading_is_kept_and_found_again()
        {
            string dwg = File_("unit.dwg", "not really a dwg");
            string tsv = File_("reading.tsv", "H\tdwg\tunit.dwg\nH\tdone\t1\n");
            string sha = CadDwgCache.Sha256(dwg);

            CadDwgCacheEntry first = CadDwgCache.Lookup(dwg, sha, "engine", "opts");
            Assert.False(first.Hit);
            Assert.Equal("not_cached", first.Miss);

            JObject stored = CadDwgCache.Store(first, tsv, new CadDwgReading { Complete = true }, dwg, 343.8);
            Assert.True((bool)stored["stored"]);

            CadDwgCacheEntry again = CadDwgCache.Lookup(dwg, sha, "engine", "opts");
            Assert.True(again.Hit);
            Assert.Equal(first.Key, again.Key);
            Assert.True(File.Exists(again.TsvPath));
        }

        [Fact]
        public void A_changed_external_reference_invalidates_the_reading()
        {
            // THE INVALIDATION A CONTENT HASH OF ONE FILE CANNOT GIVE. The host
            // drawing's bytes never move; the architectural background it shows
            // is re-issued, and the reading is now about a building that changed.
            string dwg = File_("unit.dwg", "host");
            string xref = File_("background.dwg", "version one");
            string tsv = File_("reading.tsv", "H\tdone\t1\n");
            string sha = CadDwgCache.Sha256(dwg);

            CadDwgCacheEntry entry = CadDwgCache.Lookup(dwg, sha, "engine", "opts");
            CadDwgCache.Store(entry, tsv, ReadingWith("background", xref), dwg, 1.0);
            Assert.True(CadDwgCache.Lookup(dwg, sha, "engine", "opts").Hit);

            File.WriteAllText(xref, "version two");
            CadDwgCacheEntry after = CadDwgCache.Lookup(dwg, sha, "engine", "opts");
            Assert.False(after.Hit);
            Assert.Equal("dependency_changed", after.Miss);
            Assert.Contains("background.dwg", after.Detail.ToString());
        }

        [Fact]
        public void A_reference_that_has_gone_missing_invalidates_it_too()
        {
            string dwg = File_("unit.dwg", "host");
            string xref = File_("background.dwg", "version one");
            string tsv = File_("reading.tsv", "H\tdone\t1\n");
            string sha = CadDwgCache.Sha256(dwg);

            CadDwgCacheEntry entry = CadDwgCache.Lookup(dwg, sha, "engine", "opts");
            CadDwgCache.Store(entry, tsv, ReadingWith("background", xref), dwg, 1.0);
            File.Delete(xref);

            CadDwgCacheEntry after = CadDwgCache.Lookup(dwg, sha, "engine", "opts");
            Assert.False(after.Hit);
            Assert.Contains("no longer on this machine", after.Detail.ToString());
        }

        [Fact]
        public void A_reading_with_no_end_marker_is_never_stored_as_whole()
                {
            // An incomplete report describes a walk that stopped. Storing one
            // would serve that stop to every later run, so the cache refuses it -
            // and the refusal lives in Store rather than only at the call site,
            // because a second caller would not remember it.
            string dwg = File_("unit.dwg", "host");
            string tsv = File_("partial.tsv", "H\tdwg\tunit.dwg\n");
            string sha = CadDwgCache.Sha256(dwg);

            CadDwgCacheEntry entry = CadDwgCache.Lookup(dwg, sha, "engine", "opts");
            JObject stored = CadDwgCache.Store(entry, tsv, new CadDwgReading { Complete = false }, dwg, 1.0);

            Assert.False((bool)stored["stored"]);
            Assert.Equal("reading_is_not_complete", (string)stored["refused"]);
            Assert.False(File.Exists(entry.TsvPath));
            Assert.False(CadDwgCache.Lookup(dwg, sha, "engine", "opts").Hit);
        }

        // ---- one extraction per key, and a whole publication ----------------------

        [Fact]
        public void A_key_is_locked_once_and_a_second_owner_is_named()
        {
            Assert.Null(CadDwgCache.TryLock("k1"));
            // this process already owns it: a second take by the same process is a stale lock
            Assert.Null(CadDwgCache.TryLock("k1"));
            CadDwgCache.Unlock("k1");
            Assert.False(File.Exists(CadDwgCache.LockPath("k1")));
        }

        [Fact]
        public void A_lock_held_by_a_live_process_is_not_taken()
        {
            // the test runner's parent process is alive and is not this process
            int other = System.Diagnostics.Process.GetProcessesByName("dotnet")
                          .Select(p => p.Id).FirstOrDefault(id => id != System.Diagnostics.Process.GetCurrentProcess().Id);
            if (other == 0) return;   // no other dotnet process to stand in for a second reader
            File.WriteAllText(CadDwgCache.LockPath("k2"), "{\"pid\":" + other + "}");
            JObject owner = CadDwgCache.TryLock("k2");
            Assert.NotNull(owner);
            Assert.Equal(other, (int)owner["pid"]);
            CadDwgCache.Unlock("k2");                        // not ours: left alone
            Assert.True(File.Exists(CadDwgCache.LockPath("k2")));
        }

        [Fact]
        public void A_lock_whose_owner_is_gone_is_taken_over()
        {
            File.WriteAllText(CadDwgCache.LockPath("k3"), "{\"pid\":2147483000}");
            Assert.Null(CadDwgCache.TryLock("k3"));
            JObject now = JObject.Parse(File.ReadAllText(CadDwgCache.LockPath("k3")));
            Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, (int)now["pid"]);
        }

        [Fact]
        public void A_stored_reading_leaves_no_temporary_file_and_replaces_an_old_one()
        {
            string tsv = File_("fresh.tsv", "H	done	1");
            var entry = CadDwgCache.Lookup(tsv, "sha-fresh", "engine", "o");
            var reading = new CadDwgReading { DrawingName = "unit.dwg", Complete = true };
            Assert.True((bool)CadDwgCache.Store(entry, tsv, reading, tsv, 1.0)["stored"]);
            Assert.True((bool)CadDwgCache.Store(entry, tsv, reading, tsv, 2.0)["stored"]);
            Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
            Assert.True(CadDwgCache.Lookup(tsv, "sha-fresh", "engine", "o").Hit);
        }
    }
}
