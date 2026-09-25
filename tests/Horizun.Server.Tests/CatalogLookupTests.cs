// -----------------------------------------------------------------------------
// Horizun MCP server — original Horizun code.
//
// Proves the PURE leaf rule and its provenance stamp — the honesty contract of
// horizun_catalog_lookup — without any file or Revit. The one rule that must hold:
// a code absent from the catalog is is_leaf=null (UNKNOWN), which is a distinct
// state from is_leaf=false. A test that let those two collapse would be certifying
// the exact lie the tool exists to prevent.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class CatalogLookupTests
    {
        // A small three-level hierarchy: "A" is a parent, "A-1" is an intermediate parent,
        // "A-1-1" and "A-2" are last-level leaves. Separator is "-".
        private static HashSet<string> Catalog() => new HashSet<string>
        {
            "A", "A-1", "A-1-1", "A-1-2", "A-2", "B"
        };

        [Fact]
        public void ExistingParentCode_IsLeafFalse()
        {
            LeafOutcome o = CatalogLookup.EvaluateLeaf(Catalog(), "A", "-");
            Assert.True(o.Exists);
            Assert.True(o.IsLeaf.HasValue);
            Assert.False(o.IsLeaf.Value);   // has descendants A-1, A-2 -> not a leaf
        }

        [Fact]
        public void ExistingIntermediateParent_IsLeafFalse()
        {
            LeafOutcome o = CatalogLookup.EvaluateLeaf(Catalog(), "A-1", "-");
            Assert.True(o.Exists);
            Assert.False(o.IsLeaf.Value);   // A-1-1, A-1-2 descend from it
        }

        [Fact]
        public void ExistingLastLevelCode_IsLeafTrue()
        {
            LeafOutcome o = CatalogLookup.EvaluateLeaf(Catalog(), "A-1-1", "-");
            Assert.True(o.Exists);
            Assert.True(o.IsLeaf.Value);    // nothing descends from it -> leaf
        }

        [Fact]
        public void MissingCode_IsLeafNull_NotFalse()
        {
            LeafOutcome o = CatalogLookup.EvaluateLeaf(Catalog(), "Z-9", "-");
            Assert.False(o.Exists);
            Assert.False(o.IsLeaf.HasValue);   // UNKNOWN — the honest state, never a fabricated false
        }

        [Fact]
        public void PrefixWithoutSeparator_IsNotADescendant()
        {
            // "AB" starts with "A" but is NOT "A" + separator, so "A2" would falsely poison "A".
            var codes = new HashSet<string> { "A", "AB", "ABC" };
            LeafOutcome o = CatalogLookup.EvaluateLeaf(codes, "A", "-");
            Assert.True(o.Exists);
            Assert.True(o.IsLeaf.Value);   // AB/ABC are siblings, not children -> A is a leaf
        }

        [Fact]
        public void OpaqueMode_AcceptsAnyDefaultDelimiter()
        {
            // No separator given: "-", ".", "_", "/", space all count as the hierarchy break.
            var codes = new HashSet<string> { "10", "10.20", "30", "30_40" };
            Assert.False(CatalogLookup.EvaluateLeaf(codes, "10", null).IsLeaf.Value);   // 10.20 descends
            Assert.False(CatalogLookup.EvaluateLeaf(codes, "30", null).IsLeaf.Value);   // 30_40 descends
            Assert.True(CatalogLookup.EvaluateLeaf(codes, "10.20", null).IsLeaf.Value); // leaf
        }

        [Fact]
        public void ParseCodes_TakesFirstColumnAndSkipsBlankLines()
        {
            string csv = "A,Title of A\r\nA-1,Title\r\n\r\n\"A-1-1\",Quoted\r\nB\n";
            List<string> codes = CatalogLookup.ParseCodes(csv);
            Assert.Equal(new List<string> { "A", "A-1", "A-1-1", "B" }, codes);
        }

        [Fact]
        public void Sha256_StableAcrossTwoReadsOfIdenticalBytes()
        {
            byte[] bytes1 = new UTF8Encoding(false).GetBytes("A\nA-1\nA-1-1\n");
            byte[] bytes2 = new UTF8Encoding(false).GetBytes("A\nA-1\nA-1-1\n");
            string h1 = CatalogLookup.Sha256Hex(bytes1);
            string h2 = CatalogLookup.Sha256Hex(bytes2);
            Assert.Equal(h1, h2);
            Assert.Equal(64, h1.Length);   // 32 bytes -> 64 lowercase hex chars
        }

        [Fact]
        public void Sha256_DiffersWhenBytesDiffer()
        {
            string h1 = CatalogLookup.Sha256Hex(new UTF8Encoding(false).GetBytes("A\n"));
            string h2 = CatalogLookup.Sha256Hex(new UTF8Encoding(false).GetBytes("B\n"));
            Assert.NotEqual(h1, h2);
        }
    }

    // -------------------------------------------------------------------------
    // Handle: the I/O boundary. EvaluateLeaf is proven above; these lock the one
    // thing that boundary must never get wrong — that a code absent from the
    // catalog serializes to a REAL JSON null (JTokenType.Null), never false and
    // never the string "null". This is the exact regression the honesty contract
    // forbids, asserted against the JObject Handle actually returns.
    // -------------------------------------------------------------------------
    public sealed class CatalogLookupHandleTests
    {
        private static string WriteTempCsv(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), "hz_catalog_" + Guid.NewGuid().ToString("N") + ".csv");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        [Fact]
        public void Handle_MissingCode_SerializesIsLeafAsRealJsonNull_NotFalse()
        {
            string path = WriteTempCsv("A\r\nA-1\r\nA-1-1\r\n");
            try
            {
                JObject r = CatalogLookup.Handle(new JObject { ["catalog_path"] = path, ["code"] = "Z-9", ["separator"] = "-" });
                Assert.False((bool)r["exists"]);
                Assert.Equal(JTokenType.Null, r["is_leaf"].Type);   // the honest unknown — must not collapse to false
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_LeafCode_SerializesIsLeafTrue()
        {
            string path = WriteTempCsv("A\r\nA-1\r\nA-1-1\r\n");
            try
            {
                JObject r = CatalogLookup.Handle(new JObject { ["catalog_path"] = path, ["code"] = "A-1-1", ["separator"] = "-" });
                Assert.True((bool)r["exists"]);
                Assert.Equal(JTokenType.Boolean, r["is_leaf"].Type);
                Assert.True((bool)r["is_leaf"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_ParentCode_SerializesIsLeafFalse()
        {
            string path = WriteTempCsv("A\r\nA-1\r\nA-1-1\r\n");
            try
            {
                JObject r = CatalogLookup.Handle(new JObject { ["catalog_path"] = path, ["code"] = "A-1", ["separator"] = "-" });
                Assert.True((bool)r["exists"]);
                Assert.Equal(JTokenType.Boolean, r["is_leaf"].Type);
                Assert.False((bool)r["is_leaf"]);   // A-1-1 descends from it
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_ReportsProvenance_Sha256AndRowCount()
        {
            string path = WriteTempCsv("A\r\nA-1\r\nA-1-1\r\n");
            try
            {
                JObject r = CatalogLookup.Handle(new JObject { ["catalog_path"] = path, ["code"] = "A", ["separator"] = "-" });
                Assert.Equal(64, ((string)r["sha256"]).Length);   // 32 bytes -> 64 hex chars
                Assert.Equal(3, (int)r["row_count"]);
            }
            finally { File.Delete(path); }
        }

        // ----- The catalog is not always UTF-8 --------------------------------
        //
        // Excel on a non-English Windows saves CSV as ANSI. Decoded leniently as UTF-8,
        // every accented byte becomes U+FFFD, the code stops matching, and the answer
        // comes back exists=false — a fabricated "not in this catalog" that is really
        // "I misread the file". The verdict must survive the encoding, and the caller
        // must be told which one was used.

        [Fact]
        public void Handle_AnsiCatalog_StillFindsAnAccentedCode_AndSaysWhichEncoding()
        {
            // 0xD1 is 'Ñ' in windows-1252 / latin-1, and is not valid UTF-8 on its own.
            byte[] ansi = Encoding.GetEncoding("ISO-8859-1").GetBytes("D01-DISEÑO\r\nD01-DISEÑO-01\r\nD02-PLANO\r\n");
            string path = Path.Combine(Path.GetTempPath(), "hz_ansi_" + Guid.NewGuid().ToString("N") + ".csv");
            File.WriteAllBytes(path, ansi);
            try
            {
                JObject r = CatalogLookup.Handle(new JObject { ["catalog_path"] = path, ["code"] = "D01-DISEÑO", ["separator"] = "-" });
                Assert.True((bool)r["exists"]);
                Assert.False((bool)r["is_leaf"]);   // D01-DISEÑO-01 descends from it
                Assert.Contains("latin-1", (string)r["encoding_used"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_Utf8Catalog_IsReportedAsUtf8()
        {
            string path = WriteTempCsv("D01-DISEÑO\r\nD02-PLANO\r\n");
            try
            {
                JObject r = CatalogLookup.Handle(new JObject { ["catalog_path"] = path, ["code"] = "D01-DISEÑO", ["separator"] = "-" });
                Assert.True((bool)r["exists"]);
                Assert.True((bool)r["is_leaf"]);
                Assert.Equal("utf-8", (string)r["encoding_used"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_MissingFile_Throws_NeverFabricatesAVerdict()
        {
            // A file that does not exist is a genuine error, not exists=false. The
            // caller must see the failure, never a confident answer about nothing.
            string path = Path.Combine(Path.GetTempPath(), "hz_nope_" + Guid.NewGuid().ToString("N") + ".csv");
            Assert.Throws<FileNotFoundException>(() =>
                CatalogLookup.Handle(new JObject { ["catalog_path"] = path, ["code"] = "A" }));
        }
    }

    // -------------------------------------------------------------------------
    // The 2026-09-25 field regression: a real classification catalog is TAB-
    // delimited, not comma-delimited. The old parser took the whole line as one
    // "code" (15,783 of them) and reported a real code absent. These prove
    // delimiter detection, code_column/has_header, and operation=search — all
    // WITHOUT any client name or real code baked in, per the neutrality rule.
    // -------------------------------------------------------------------------
    public sealed class CatalogLookupDelimiterTests
    {
        private static string WriteTemp(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), "hz_delim_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        [Fact]
        public void DetectColumnDelimiter_Tab_IsFoundWithoutBeingTold()
        {
            string csv = "D01\tGrupo\r\nD01-A1\tHoja\r\nD01-A2\tHoja\r\n";
            ColumnDelimiterDetection d = CatalogLookup.DetectColumnDelimiter(csv, null);
            Assert.Equal("\t", d.Delimiter);
            Assert.Equal("detected", d.Mode);
        }

        [Fact]
        public void DetectColumnDelimiter_Semicolon_IsFoundWithoutBeingTold()
        {
            string csv = "D01;Grupo\r\nD01-A1;Hoja\r\n";
            ColumnDelimiterDetection d = CatalogLookup.DetectColumnDelimiter(csv, null);
            Assert.Equal(";", d.Delimiter);
            Assert.Equal("detected", d.Mode);
        }

        [Fact]
        public void DetectColumnDelimiter_QuotedCommaInsideAField_DoesNotBreakDetection()
        {
            // Every row has exactly two REAL commas outside quotes; the comma embedded in
            // the quoted description must not be counted as a third delimiter occurrence.
            string csv = "D01,\"Estructura, general\",Grupo\r\nD01-A1,\"Cimentacion, superficial\",Hoja\r\n";
            ColumnDelimiterDetection d = CatalogLookup.DetectColumnDelimiter(csv, null);
            Assert.Equal(",", d.Delimiter);
            Assert.Equal("detected", d.Mode);

            List<string> row = CatalogLookup.SplitRow("D01,\"Estructura, general\",Grupo", d.Delimiter);
            Assert.Equal(new[] { "D01", "Estructura, general", "Grupo" }, row);
        }

        [Fact]
        public void DetectColumnDelimiter_NoDelimiterPresent_FallsBackToSingleColumnComma()
        {
            string csv = "D01\r\nD01-A1\r\nD01-A2\r\n";
            ColumnDelimiterDetection d = CatalogLookup.DetectColumnDelimiter(csv, null);
            Assert.Equal(",", d.Delimiter);
            Assert.Equal("single_column_default", d.Mode);
        }

        [Fact]
        public void DetectColumnDelimiter_ExplicitDelimiter_AlwaysWinsAndIsMarkedExplicit()
        {
            ColumnDelimiterDetection d = CatalogLookup.DetectColumnDelimiter("D01,Grupo\r\n", "|");
            Assert.Equal("|", d.Delimiter);
            Assert.Equal("explicit", d.Mode);
        }

        [Fact]
        public void DetectColumnDelimiter_GenuineTie_RefusesRatherThanGuesses()
        {
            // Every line has exactly one comma AND exactly one semicolon, consistently:
            // a real ambiguity, not a malformed file. Must refuse, not pick one silently.
            string csv = "D01,A;1\r\nD02,B;2\r\nD03,C;3\r\n";
            var ex = Assert.Throws<ArgumentException>(() => CatalogLookup.DetectColumnDelimiter(csv, null));
            Assert.Contains("delimiter", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Handle_Leaf_TabDelimitedCatalog_FindsTheRealCodeInsteadOfOneGiantCode()
        {
            // This is the exact shape of the field bug: without tab detection the whole
            // line "D01\tGrupo" would have been read as the code and D01-A1 would be
            // reported absent even though it is right there in column 0.
            string path = WriteTemp("D01\tGrupo\r\nD01-A1\tHoja\r\nD01-A2\tHoja\r\n");
            try
            {
                JObject r = CatalogLookup.Handle(new JObject { ["catalog_path"] = path, ["code"] = "D01-A1", ["separator"] = "-" });
                Assert.True((bool)r["exists"]);
                Assert.True((bool)r["is_leaf"]);
                Assert.Equal("\t", (string)r["delimiter_used"]);
                Assert.Equal("detected", (string)r["delimiter_mode"]);
                Assert.Equal(3, (int)r["row_count"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_Leaf_HeaderAndCodeColumnByName_ResolvesTheNamedColumn()
        {
            string path = WriteTemp("Nivel;Codigo;Descripcion\r\nGrupo;D01;Estructura\r\nHoja;D01-A1;Cimentacion\r\nHoja;D01-A2;Muros\r\n");
            try
            {
                JObject r = CatalogLookup.Handle(new JObject
                {
                    ["catalog_path"] = path,
                    ["code"] = "D01-A1",
                    ["separator"] = "-",
                    ["has_header"] = true,
                    ["code_column"] = "Codigo"
                });
                Assert.True((bool)r["exists"]);
                Assert.True((bool)r["is_leaf"]);
                Assert.Equal(1, (int)r["code_column_index"]);
                Assert.Equal("Codigo", (string)r["code_column_name"]);
                var columns = (JArray)r["columns"];
                Assert.Equal(new[] { "Nivel", "Codigo", "Descripcion" }, columns.Select(t => (string)t));
                Assert.Equal(3, (int)r["row_count"]);   // the header line itself is not a code
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_Leaf_CodeColumnHeaderName_WithoutHasHeader_Throws()
        {
            string path = WriteTemp("D01;Estructura\r\n");
            try
            {
                Assert.Throws<ArgumentException>(() => CatalogLookup.Handle(new JObject
                {
                    ["catalog_path"] = path,
                    ["code"] = "D01",
                    ["code_column"] = "Codigo"
                }));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_Search_RanksByNormalizedTokenOverlap_AccentAndCaseInsensitive()
        {
            string path = WriteTemp(
                "D01\tCielo raso liviano de yeso\r\n" +
                "D02\tCielo falso metalico\r\n" +
                "D03\tMuro de bloque de concreto\r\n");
            try
            {
                JObject r = CatalogLookup.Handle(new JObject
                {
                    ["operation"] = "search",
                    ["catalog_path"] = path,
                    ["query"] = "CIELO RASO",   // upper-case on purpose: must still match
                    ["separator"] = "-"
                });
                var matches = (JArray)r["matches"];
                Assert.True(matches.Count >= 1);
                Assert.Equal("D01", (string)matches[0]["code"]);
                Assert.Equal(1.0, (double)matches[0]["score"], 3);
                Assert.True((bool)matches[0]["exists"]);
                // D03 shares no token with "cielo raso" and must not appear at all.
                Assert.DoesNotContain(matches, m => (string)m["code"] == "D03");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_Search_AccentInsensitive_ElectricoMatchesElectricoWithoutAccent()
        {
            string path = WriteTemp("E01\tTablero Eléctrico principal\r\nE02\tPunto de red\r\n");
            try
            {
                JObject r = CatalogLookup.Handle(new JObject
                {
                    ["operation"] = "search",
                    ["catalog_path"] = path,
                    ["query"] = "electrico"   // no accent in the query
                });
                var matches = (JArray)r["matches"];
                Assert.Contains(matches, m => (string)m["code"] == "E01");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_Search_GroupVsLeaf_ReportsIsLeafCorrectlyPerResult()
        {
            string path = WriteTemp("D01\tEstructura general\r\nD01-A1\tEstructura de cimentacion\r\n");
            try
            {
                JObject r = CatalogLookup.Handle(new JObject
                {
                    ["operation"] = "search",
                    ["catalog_path"] = path,
                    ["query"] = "estructura",
                    ["separator"] = "-"
                });
                var matches = (JArray)r["matches"];
                JToken group = matches.First(m => (string)m["code"] == "D01");
                JToken leaf = matches.First(m => (string)m["code"] == "D01-A1");
                Assert.False((bool)group["is_leaf"]);   // D01-A1 descends from it
                Assert.True((bool)leaf["is_leaf"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_Search_NoMatch_ReturnsEmptyMatchesNotAnError()
        {
            string path = WriteTemp("D01\tMuro de bloque\r\n");
            try
            {
                JObject r = CatalogLookup.Handle(new JObject
                {
                    ["operation"] = "search",
                    ["catalog_path"] = path,
                    ["query"] = "ascensor"
                });
                Assert.Empty((JArray)r["matches"]);
                Assert.Equal(0, (int)r["matched_count"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_Search_BlankQuery_Throws()
        {
            string path = WriteTemp("D01\tMuro de bloque\r\n");
            try
            {
                Assert.Throws<ArgumentException>(() => CatalogLookup.Handle(new JObject
                {
                    ["operation"] = "search",
                    ["catalog_path"] = path,
                    ["query"] = "   "
                }));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Handle_UnknownOperation_MentionsSearchInTheError()
        {
            string path = WriteTemp("D01\tX\r\n");
            try
            {
                var ex = Assert.Throws<ArgumentException>(() => CatalogLookup.Handle(new JObject
                {
                    ["operation"] = "nope",
                    ["catalog_path"] = path,
                    ["code"] = "D01"
                }));
                Assert.Contains("search", ex.Message, StringComparison.Ordinal);
            }
            finally { File.Delete(path); }
        }
    }
}
