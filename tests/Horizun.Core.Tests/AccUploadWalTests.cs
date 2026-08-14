// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// AccUploadWal (story 5.15): the rule that reads "uploaded or still pending?"
// out of the Desktop Connector's log. The synthetic content here reproduces
// what the real WAL looks like - JSON escaped inside JSON, NUL bytes
// interleaved - because that shape is exactly what the field-proven script
// parsed, and the mistakes worth pinning are the honesty ones: a name that
// merely LOOKS uploaded must not match, and the org-specific prefix the old
// script hardcoded must be gone.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class AccUploadWalTests
    {
        /// <summary>One WAL record exactly as it sits in the raw bytes: escaped quotes.</summary>
        private static string Record(string coUrn, string name)
        {
            return "{\\\"ParentFolderUrn\\\":\\\"urn:adsk.wipprod:fs.folder:" + coUrn +
                   "\\\",\\\"Name\\\":\\\"" + name + "\\\"}";
        }

        [Fact]
        public void Decode_strips_nul_bytes_and_keeps_the_text()
        {
            string rec = Record("co.AbC-123", "SAMPLE-CAMERA.rfa");
            // NULs interleaved the way a binary log interleaves them.
            var raw = new List<byte>();
            foreach (byte b in Encoding.GetEncoding("ISO-8859-1").GetBytes(rec))
            {
                raw.Add(b);
                raw.Add(0);
            }

            string decoded = AccUploadWal.Decode(raw.ToArray());
            var hits = AccUploadWal.Scan("f1", decoded);

            Assert.Single(hits);
            Assert.Equal("co.AbC-123", hits[0].FolderUrn);
            Assert.Equal("SAMPLE-CAMERA.rfa", hits[0].Name);
            Assert.Equal("f1", hits[0].SourceFile);
        }

        [Fact]
        public void Scan_is_org_neutral_where_the_field_script_was_not()
        {
            // The field prototype matched one organisation's prefix. The rule
            // must find ANY name - AGENTS.md compiles no organisation in.
            string content = Record("co.X1", "ACME-Valve.rfa") + "noise" + Record("co.X2", "modelo estructural.rvt");

            var hits = AccUploadWal.Scan("f", content);

            Assert.Equal(2, hits.Count);
            Assert.Contains(hits, h => h.Name == "ACME-Valve.rfa");
            Assert.Contains(hits, h => h.Name == "modelo estructural.rvt");
        }

        [Fact]
        public void A_recorded_name_matches_with_or_without_its_extension_and_canonically()
        {
            var hits = AccUploadWal.Scan("f", Record("co.A", "SAMPLE-JUNCTION-15x15x10.rfa"));

            foreach (string asked in new[]
            {
                "SAMPLE-JUNCTION-15x15x10.rfa",       // exact
                "SAMPLE-JUNCTION-15x15x10",           // no extension
                "sample junction 15x15x10",           // spacing/case/dashes differ
            })
            {
                var r = AccUploadWal.Match(new[] { asked }, hits).Single();
                Assert.True(r.HasFolderUrn, asked);
                Assert.Contains("co.A", r.FolderUrns);
                Assert.Contains("SAMPLE-JUNCTION-15x15x10.rfa", r.MatchedNames);
            }
        }

        [Fact]
        public void An_unrecorded_name_is_not_found_and_that_is_the_field_reports_case()
        {
            // 3 of 8 families copied into the connector folder and silently unuploaded:
            // their names are NOT in the WAL, and the verdict must say so instead of
            // being inferred from the local copy existing.
            var hits = AccUploadWal.Scan("f", Record("co.A", "SAMPLE-UPLOADED.rfa"));

            var r = AccUploadWal.Match(new[] { "SAMPLE-THROTTLED.rfa" }, hits).Single();

            Assert.False(r.HasFolderUrn);
            Assert.Empty(r.FolderUrns);
        }

        [Fact]
        public void A_name_seen_under_two_folders_reports_both_never_a_silent_choice()
        {
            // The flat-old-location case. The script took a hint list and picked one;
            // the rule reports every folder and lets the caller decide, because a
            // silent choice is a guess wearing a fact's clothes.
            string content = Record("co.OLD", "SAMPLE-X.rfa") + Record("co.NEW", "SAMPLE-X.rfa");

            var r = AccUploadWal.Match(new[] { "SAMPLE-X" }, AccUploadWal.Scan("f", content)).Single();

            Assert.True(r.HasFolderUrn);
            Assert.Equal(2, r.FolderUrns.Count);
            Assert.Contains("co.OLD", r.FolderUrns);
            Assert.Contains("co.NEW", r.FolderUrns);
        }

        [Fact]
        public void A_name_with_nothing_to_match_on_says_so_instead_of_never_matching_in_silence()
        {
            var r = AccUploadWal.Match(new[] { "-- ##" }, new List<WalHit>()).Single();

            Assert.False(r.HasFolderUrn);
            Assert.NotNull(r.Note);
            Assert.Contains("no letters or digits", r.Note);
        }

        [Fact]
        public void Stem_strips_a_real_extension_and_leaves_a_size_alone()
        {
            Assert.Equal("SAMPLE-CAMERA", AccUploadWal.Stem("SAMPLE-CAMERA.rfa"));
            Assert.Equal("modelo", AccUploadWal.Stem("modelo.rvt"));
            // ".5" is a size, not an extension: digits after the dot never strip.
            Assert.Equal("Caja 1.5x2.5", AccUploadWal.Stem("Caja 1.5x2.5"));
            Assert.Equal("sin-extension", AccUploadWal.Stem("sin-extension"));
        }

        [Fact]
        public void The_url_is_built_exactly_as_the_field_script_built_it()
        {
            string url = AccUploadWal.BuildUrl("proj-123", "co.AbC");

            Assert.Equal(
                "https://acc.autodesk.com/docs/files/projects/proj-123" +
                "?folderUrn=urn%3Aadsk.wipprod%3Afs.folder%3Aco.AbC&viewModel=detail&moduleId=folders",
                url);
            Assert.Null(AccUploadWal.BuildUrl(null, "co.A"));
            Assert.Null(AccUploadWal.BuildUrl("p", null));
        }

        [Fact]
        public void Empty_or_null_inputs_answer_empty_never_throw()
        {
            Assert.Equal("", AccUploadWal.Decode(null));
            Assert.Empty(AccUploadWal.Scan("f", null));
            Assert.Empty(AccUploadWal.Match(null, new List<WalHit>()));
        }
    }
}
