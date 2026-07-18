// -----------------------------------------------------------------------------
// Horizun — proves the "cannot lie" thesis for the catalog leaf rule.
//
// The single business rule three of Horizun's processes depend on: a code is
// assignable ONLY if it is a LEAF — nobody's parent. A code with children is a
// CHAPTER and must be refused, even though it is a real row. The classic lie is
// confirming a code "exists" by prefix / substring / segment-count / regex shape
// while it is actually a chapter. These tests make that lie fail the build.
//
// HorizunCatalogTools is host-side (no Revit), so it is exercised directly
// through the Server ProjectReference — no Autodesk assemblies loaded.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public class HorizunCatalogLookupTests : IDisposable
    {
        private readonly string _dir;

        public HorizunCatalogLookupTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz_catalog_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // A small catalog with the SAME shape as the real one: CODE<TAB>TITLE<TAB>PARENT.
        // Root has empty parent. The tree deliberately puts a 3-segment code (X001-A1-A01)
        // as a CHAPTER — it is the parent of X001-A1-A01-A001 — so that any test relying on
        // "3 or 4 segments == leaf" would be wrong. Leaf-ness must come from the parent column.
        private string WriteCatalog()
        {
            var path = Path.Combine(_dir, "catalog.tsv");
            File.WriteAllText(path, string.Join("\n", new[]
            {
                "CX\tROOT COSTS\t",
                "X001\tCHAPTER ONE\tCX",
                "X001-A1\tSUBCHAPTER\tX001",
                "X001-A1-A01\tSTILL A CHAPTER (3 segments)\tX001-A1",
                "X001-A1-A01-A001\tLEAF ITEM ALPHA\tX001-A1-A01",
                "X001-A1-A01-A002\tLEAF ITEM BETA\tX001-A1-A01",
                "X002\tCHAPTER TWO\tCX",
                "X002-A1\tLEAF UNDER TWO (2 segments)\tX002",
            }));
            return path;
        }

        private static JObject Validate(string catalog, string codesJson)
            => JObject.Parse(HorizunCatalogTools.CatalogLookup("validate", catalog, codes: codesJson).Result);

        private static JObject Search(string catalog, string term, int? limit = null)
            => JObject.Parse(HorizunCatalogTools.CatalogLookup("search", catalog, term: term, limit: limit).Result);

        private static JToken ResultFor(JObject validateResponse, string code)
        {
            foreach (var r in (JArray)validateResponse["results"])
                if ((string)r["code"] == code) return r;
            throw new Xunit.Sdk.XunitException($"code {code} not in results");
        }

        // ---- THE rule: a chapter is refused, and leaf-ness is NOT segment count ----

        [Fact]
        public void Leaf_is_assignable()
        {
            var r = ResultFor(Validate(WriteCatalog(), "[\"X001-A1-A01-A001\"]"), "X001-A1-A01-A001");
            Assert.True((bool)r["exists"]);
            Assert.True((bool)r["is_leaf"]);
            Assert.True((bool)r["assignable"]);
        }

        [Fact]
        public void Chapter_is_refused_even_though_it_exists()
        {
            var r = ResultFor(Validate(WriteCatalog(), "[\"X001-A1\"]"), "X001-A1");
            Assert.True((bool)r["exists"]);          // it IS a real row
            Assert.False((bool)r["is_leaf"]);        // but it has children
            Assert.False((bool)r["assignable"]);     // so it may not be assigned
            Assert.True((int)r["children_count"] > 0);
        }

        [Fact]
        public void Three_segment_code_that_is_a_parent_is_a_chapter_not_a_leaf()
        {
            // The exact real-catalog case: D001-A1-A01 "PILOTES TIPO TORNILLO" is 3 segments
            // and a chapter. A segment-count or regex rule would wave it through; the parent
            // column does not. This is the test that pins the whole design decision.
            var r = ResultFor(Validate(WriteCatalog(), "[\"X001-A1-A01\"]"), "X001-A1-A01");
            Assert.Equal(3, (int)r["segments"]);
            Assert.False((bool)r["is_leaf"]);
            Assert.False((bool)r["assignable"]);
        }

        [Fact]
        public void Two_segment_code_can_be_a_leaf_if_it_is_nobodys_parent()
        {
            // Symmetric to the above: leaf-ness is not "has 4 segments" either.
            var r = ResultFor(Validate(WriteCatalog(), "[\"X002-A1\"]"), "X002-A1");
            Assert.Equal(2, (int)r["segments"]);
            Assert.True((bool)r["is_leaf"]);
            Assert.True((bool)r["assignable"]);
        }

        [Fact]
        public void Nonexistent_code_is_unknown_not_false()
        {
            // "I could not determine leaf-ness" must be distinct from "it is not a leaf".
            var r = ResultFor(Validate(WriteCatalog(), "[\"X001-A1-A01-A999\"]"), "X001-A1-A01-A999");
            Assert.False((bool)r["exists"]);
            Assert.Equal(JTokenType.Null, r["is_leaf"].Type);   // null, NOT false
            Assert.False((bool)r["assignable"]);
        }

        // ---- search returns only assignable leaves ----

        [Fact]
        public void Search_returns_only_leaves()
        {
            var s = Search(WriteCatalog(), "ITEM")["search"];
            foreach (var m in (JArray)s["matches"])
            {
                // Every returned code must itself validate as an assignable leaf.
                var code = (string)m["code"];
                var v = ResultFor(Validate(WriteCatalog(), $"[\"{code}\"]"), code);
                Assert.True((bool)v["assignable"], $"search returned non-leaf {code}");
            }
        }

        [Fact]
        public void Search_reports_total_vs_returned_when_capped()
        {
            var s = Search(WriteCatalog(), "LEAF", limit: 1)["search"];
            Assert.Equal(1, (int)s["returned"]);
            Assert.True((int)s["total_leaf_matches"] >= 2);
            Assert.True((bool)s["truncated"]);
        }

        // ---- provenance and refusal-not-empty ----

        [Fact]
        public void Every_response_carries_provenance()
        {
            var sources = Validate(WriteCatalog(), "[\"X002-A1\"]")["sources"];
            Assert.False(string.IsNullOrEmpty((string)sources["sha256"]));
            Assert.True((int)sources["rows_parsed"] == 8);
            Assert.False(string.IsNullOrEmpty((string)sources["path"]));
        }

        [Fact]
        public void Missing_file_is_a_hard_error_not_an_empty_result()
        {
            var raw = HorizunCatalogTools.CatalogLookup("validate",
                Path.Combine(_dir, "does_not_exist.tsv"), codes: "[\"X002-A1\"]").Result;
            Assert.StartsWith("Error", raw);
        }

        [Fact]
        public void Empty_catalog_is_a_hard_error_not_zero_rows_of_silence()
        {
            var path = Path.Combine(_dir, "empty.tsv");
            File.WriteAllText(path, "");
            var raw = HorizunCatalogTools.CatalogLookup("validate", path, codes: "[\"X\"]").Result;
            Assert.StartsWith("Error", raw);
        }

        [Fact]
        public void Duplicate_code_is_reported_as_a_warning_not_silently_collapsed()
        {
            var path = Path.Combine(_dir, "dup.tsv");
            File.WriteAllText(path, string.Join("\n", new[]
            {
                "CX\tROOT\t",
                "X001\tONE\tCX",
                "X001\tONE AGAIN (duplicate code)\tCX",
            }));
            var resp = Validate(path, "[\"X001\"]");
            Assert.NotNull(resp["warnings"]);
            Assert.NotEmpty((JArray)resp["warnings"]);
        }
    }
}
