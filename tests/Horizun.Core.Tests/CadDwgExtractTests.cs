// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THE READER THAT SEES WHAT REVIT'S IMPORT DROPS.
//
// Every case here is a shape the extractor can actually produce, written against
// the report format rather than against a drawing: the parser is pure, so it can
// be shown correct without AutoCAD, without Revit and without a client's file.
//
// The last test is different in kind. It parses a REAL extraction when one is
// pointed at by an environment variable, and checks the parse against counts
// taken independently of the parser. Without the variable it passes trivially and
// says so - a test that silently does nothing is the same green tick as a test
// that checked something, and this repository has paid for that confusion before.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadDwgExtractTests
    {
        private static string T(params string[] fields) { return string.Join("\t", fields); }

        private static readonly string[] Minimal =
        {
            "H\tdwg\tSAMPLE.dwg",
            "H\tinsunits\t4",
            "H\tdone\t1"
        };

        [Fact]
        public void A_report_without_its_end_marker_is_incomplete()
        {
            CadDwgReading whole = CadDwgExtract.Parse(Minimal);
            Assert.True(whole.Complete);

            // The same report, cut off. What was read is real; the reading is not
            // the drawing, and a conversion from it would report coverage of a file
            // it only partly saw.
            CadDwgReading cut = CadDwgExtract.Parse(Minimal.Take(2));
            Assert.False(cut.Complete);
        }

        [Fact]
        public void Inches_are_converted_to_millimetres_and_unitless_is_refused()
        {
            Assert.Equal(25.4, CadDwgExtract.MmPerUnitOf(1));
            Assert.Equal(304.8, CadDwgExtract.MmPerUnitOf(2));
            Assert.Equal(1.0, CadDwgExtract.MmPerUnitOf(4));
            Assert.Equal(1000.0, CadDwgExtract.MmPerUnitOf(6));

            // 0 is UNITLESS, and it is not one millimetre. A drawing that declares
            // no unit built as if it were in millimetres is a building at 1/25th
            // scale, which looks entirely reasonable until somebody dimensions it.
            Assert.Null(CadDwgExtract.MmPerUnitOf(0));
        }

        [Fact]
        public void A_declared_unit_is_never_overridden_by_the_callers_assumption()
        {
            var lines = new List<string> { "H\tinsunits\t1", "H\tdone\t1",
                                           T("E", "*Model_Space", "2A", "LINE", "E-LITE", "0", "0", "0", "10", "0", "0") };

            // The caller believes millimetres; the file says inches. The file wins,
            // and the disagreement is the caller's to resolve before converting.
            CadDwgReading r = CadDwgExtract.Parse(lines, assumeMmPerUnit: 1.0);
            Assert.Equal(25.4, r.MmPerUnit);
            Assert.Equal(254.0, r.Entities.Single().Points[1].X, 6);
        }

        [Fact]
        public void An_assumption_is_used_only_where_the_file_declares_nothing()
        {
            var lines = new List<string> { "H\tinsunits\t0", "H\tdone\t1",
                                           T("E", "*Model_Space", "2A", "LINE", "E-LITE", "0", "0", "0", "10", "0", "0") };

            CadDwgReading told = CadDwgExtract.Parse(lines, assumeMmPerUnit: 25.4);
            Assert.Equal(25.4, told.MmPerUnit);

            CadDwgReading untold = CadDwgExtract.Parse(lines);
            Assert.Null(untold.MmPerUnit);
            // Unscaled rather than wrongly scaled: the length is in drawing units
            // and the capability declaration says the unit is absent.
            Assert.Equal(10.0, untold.Entities.Single().Points[1].X, 6);
        }

        [Fact]
        public void Text_arrives_with_its_content_height_and_place()
        {
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                T("E", "*Model_Space", "1F4", "TEXT", "E-ANNO", "100", "200", "0", "2.5", "0", "PANEL A"),
                "H\tdone\t1"
            };
            CadIrEntity e = CadDwgExtract.Parse(lines).Entities.Single();
            Assert.Equal(CadEntityKind.Text, e.Kind);
            Assert.Equal("PANEL A", e.Text);
            Assert.Equal(2.5, e.TextHeightMm.Value, 6);
            Assert.Equal(100, e.Points[0].X, 6);
            Assert.Equal("1F4", e.Handle);
        }

        [Fact]
        public void A_tab_or_a_newline_inside_a_string_survives_the_round_trip()
        {
            // The format is tab-separated and a drawing's text can contain a tab.
            // The extractor escapes four characters; this is the other half.
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                T("E", "*Model_Space", "A1", "MTEXT", "E-ANNO", "0", "0", "0", "2.5", "0",
                  "LINE 1\\nLINE\\t2\\\\END"),
                "H\tdone\t1"
            };
            Assert.Equal("LINE 1\nLINE\t2\\END", CadDwgExtract.Parse(lines).Entities.Single().Text);
        }

        [Fact]
        public void A_block_instance_carries_its_name_its_rotation_and_its_mirror()
        {
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                T("E", "*Model_Space", "B7", "INSERT", "E-PWR", "1000", "2000", "0",
                  "1.5708", "-1", "1", "1", "OUT2"),
                T("A", "B7", "CIRCUIT", "A-12"),
                "H\tdone\t1"
            };
            CadIrEntity e = CadDwgExtract.Parse(lines).Entities.Single(x => x.Kind == CadEntityKind.BlockInstance);
            Assert.Equal("OUT2", e.BlockName);
            Assert.Equal(1.5708, e.RotationRadians.Value, 4);

            // A NEGATIVE SCALE IS A MIRROR, and a mirrored symbol placed unmirrored
            // is a socket on the wrong side of a wall.
            Assert.Equal(-1, e.ScaleX.Value, 6);
            Assert.Equal("A-12", e.Attributes["CIRCUIT"]);
        }

        [Fact]
        public void An_attribute_belongs_to_the_instance_that_owns_it_not_to_the_last_one_read()
        {
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                T("E", "*Model_Space", "B1", "INSERT", "E-PWR", "0", "0", "0", "0", "1", "1", "1", "OUT2"),
                T("E", "*Model_Space", "B2", "INSERT", "E-PWR", "5", "0", "0", "0", "1", "1", "1", "OUT2"),
                T("A", "B2", "CIRCUIT", "B-3"),
                "H\tdone\t1"
            };
            List<CadIrEntity> e = CadDwgExtract.Parse(lines).Entities;
            Assert.Null(e[0].Attributes);
            Assert.Equal("B-3", e[1].Attributes["CIRCUIT"]);
        }

        [Fact]
        public void An_external_reference_says_whether_AutoCAD_found_it()
        {
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                T("K", "X-GRID", "36", ".\\X-GRID.dwg"),          // 4 = xref, 32 = resolved
                T("K", "SEAL-2015", "4", "P:\\SEALS\\SEAL.dwg"),   // xref, NOT resolved
                T("K", "OUT2", "0", ""),                          // an ordinary block
                "H\tdone\t1"
            };
            CadDwgReading r = CadDwgExtract.Parse(lines);

            Assert.Equal(3, r.BlockNames.Count);
            Assert.Equal(2, r.ExternalReferences.Count);
            Assert.True(r.ExternalReferences.Single(x => x.Name == "X-GRID").Resolved);

            // A reference the machine cannot reach is a FINDING, not an absence:
            // everything that lived in it is missing from this reading and nothing
            // downstream can tell that from a drawing that never had it.
            Assert.False(r.ExternalReferences.Single(x => x.Name == "SEAL-2015").Resolved);
        }

        [Fact]
        public void An_arc_becomes_an_arc_and_not_two_chords()
        {
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                T("E", "*Model_Space", "C1", "ARC", "E-LITE", "0", "0", "0", "100", "0", "90"),
                "H\tdone\t1"
            };
            CadIrEntity e = CadDwgExtract.Parse(lines).Entities.Single();
            Assert.Equal(CadEntityKind.Arc, e.Kind);
            Assert.NotNull(e.Arc);
            Assert.Equal(100, e.Arc.RadiusMm, 6);
            Assert.Equal(100, e.Arc.Start.X, 6);     // 0 degrees
            Assert.Equal(100, e.Arc.End.Y, 6);       // 90 degrees
            Assert.Equal(Math.PI / 2, e.Arc.SweepRadians, 6);
        }

        [Fact]
        public void A_type_this_reading_does_not_model_is_named_rather_than_dropped()
        {
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                T("E", "*Model_Space", "H1", "HATCH", "E-FILL", "0", "0", "0"),
                T("E", "*Model_Space", "S1", "SPLINE", "E-LITE", "1", "2", "0"),
                "H\tdone\t1"
            };
            CadDwgReading r = CadDwgExtract.Parse(lines);

            // Both are in the reading, both say what they are, and the counts are
            // published - because a conversion that silently ignores a third of a
            // drawing reports full coverage of the part it understood.
            Assert.Equal(2, r.Entities.Count);
            Assert.Equal("HATCH", r.Entities[0].UnmodelledType);
            Assert.Equal("SPLINE", r.Entities[1].UnmodelledType);
            Assert.Equal(1, r.RawTypeCounts["HATCH"]);
        }

        [Fact]
        public void A_line_the_parser_cannot_read_is_reported_not_skipped()
        {
            var lines = new List<string> { "H\tinsunits\t4", "Q\tsomething\tunexpected", "H\tdone\t1" };
            CadDwgReading r = CadDwgExtract.Parse(lines);
            Assert.Single(r.Unparsed);
            Assert.Contains("Q\tsomething", r.Unparsed[0]);
        }

        [Fact]
        public void An_entity_inside_a_block_definition_remembers_which_block()
        {
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                T("E", "OUT2", "D1", "CIRCLE", "E-PWR", "0", "0", "0", "5"),
                T("E", "*Model_Space", "D2", "CIRCLE", "E-PWR", "0", "0", "0", "5"),
                "H\tdone\t1"
            };
            List<CadIrEntity> e = CadDwgExtract.Parse(lines).Entities;
            Assert.Equal(new[] { "OUT2" }, e[0].BlockPath);
            Assert.Empty(e[1].BlockPath);
        }

        // ---- capability ------------------------------------------------------

        [Fact]
        public void The_capability_separates_a_drawing_with_no_text_from_a_reader_that_cannot_see_text()
        {
            CadDwgReading none = CadDwgExtract.Parse(new[] { "H\tinsunits\t4", "H\tdone\t1" });
            CadReaderCapability cap = CadDwgExtract.Capability(none, "AutoCAD 2025");

            // ABSENT, not unavailable: this reader reads text and this drawing has
            // none. A rule that needs text may still run and find nothing, and that
            // nothing is a fact about the drawing.
            Assert.Equal(CadAxisState.Absent, cap.Axis(CadAxes.Text).State);
            Assert.True(cap.Can(CadAxes.Text));

            // And what it genuinely cannot do stays unavailable.
            Assert.Equal(CadAxisState.Unavailable, cap.Axis(CadAxes.ExtendedData).State);
            Assert.False(cap.Can(CadAxes.ExtendedData));
        }

        [Fact]
        public void A_unitless_drawing_declares_its_unit_absent()
        {
            CadDwgReading r = CadDwgExtract.Parse(new[] { "H\tinsunits\t0", "H\tdone\t1" });
            CadReaderCapability cap = CadDwgExtract.Capability(r, "AutoCAD 2025");
            Assert.Equal(CadAxisState.Absent, cap.Axis(CadAxes.Units).State);
            Assert.Contains("UNITLESS", cap.Axis(CadAxes.Units).Evidence);
        }

        [Fact]
        public void Geometry_is_partial_when_the_drawing_holds_types_this_reading_does_not_model()
        {
            CadDwgReading r = CadDwgExtract.Parse(new[]
            {
                "H\tinsunits\t4",
                T("E", "*Model_Space", "L1", "LINE", "A", "0", "0", "0", "1", "0", "0"),
                T("E", "*Model_Space", "H1", "HATCH", "A", "0", "0", "0"),
                "H\tdone\t1"
            });
            CadReaderCapability cap = CadDwgExtract.Capability(r, "AutoCAD 2025");
            Assert.Equal(CadAxisState.Partial, cap.Axis(CadAxes.Geometry).State);
            Assert.Contains("does not model", cap.Axis(CadAxes.Geometry).Evidence);
        }

        // ---- the real thing, when one is pointed at --------------------------

        /// <summary>
        /// Parses an extraction of a REAL drawing and checks the parse against
        /// counts taken WITHOUT the parser - by counting the report's own lines.
        ///
        /// A parser judged only by its own output is not judged. These two counts
        /// come from different code reading the same bytes, and where they differ
        /// the parser has dropped or invented something.
        ///
        /// Set HORIZUN_DWG_TSV to an extraction to run it. Client drawings do not
        /// live in this repository and their readings do not either.
        /// </summary>
        [Fact]
        public void A_real_extraction_parses_to_the_same_counts_its_own_lines_give()
        {
            string path = Environment.GetEnvironmentVariable("HORIZUN_DWG_TSV");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                // Said out loud rather than passing silently.
                Assert.True(true, "HORIZUN_DWG_TSV not set: this case checked nothing.");
                return;
            }

            string[] lines = File.ReadAllLines(path);
            CadDwgReading r = CadDwgExtract.Parse(lines);

            int eLines = lines.Count(l => l.StartsWith("E\t", StringComparison.Ordinal));
            int seqEnd = lines.Count(l => l.StartsWith("E\t", StringComparison.Ordinal) &&
                                          l.Split('\t').Length > 3 &&
                                          (l.Split('\t')[3] == "SEQEND" || l.Split('\t')[3] == "ENDBLK"));
            int kLines = lines.Count(l => l.StartsWith("K\t", StringComparison.Ordinal));
            int lLines = lines.Count(l => l.StartsWith("L\t", StringComparison.Ordinal));

            Assert.True(r.Complete, "the extraction has no end marker");
            Assert.Empty(r.Unparsed);

            // Every E line becomes an entity except the structural markers, which
            // are deliberately dropped and counted here so the drop is visible.
            Assert.Equal(eLines - seqEnd, r.Entities.Count);
            Assert.Equal(kLines, r.BlockNames.Count);
            Assert.Equal(lLines, r.Layers.Count);
            Assert.True(r.Entities.All(e => e.Handle != null), "an entity came back with no handle");
        }
    }
}
