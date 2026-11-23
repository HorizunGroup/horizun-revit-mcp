// Horizun MCP — original Horizun code.
// operation=match (explainable pairing) and UTF-16 keynote files in horizun_catalog_lookup.
using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class CatalogMatchTests
    {
        private const string Keynotes =
            "22\tElementos de construcción\t\r\n" +
            "22.03\tConcreto\t22\r\n" +
            "22.03.30-001\tMuro de concreto e=15 cm\t22.03\r\n" +
            "22.03.30-002\tMuro de concreto e=20 cm\t22.03\r\n" +
            "22.09.21-001\tMuro en drywall doble cara\t22\r\n" +
            "22.08.71-001\tPuerta de madera entamborada\t22\r\n";

        private static string WriteUtf16(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), "hz_kn_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, content, new UnicodeEncoding(false, true));
            return path;
        }

        private static JObject Match(string path, params string[] texts)
        {
            var q = new JArray(texts.Select((t, i) => (JToken)new JObject { ["id"] = i + 1, ["text"] = t }));
            return CatalogLookup.Handle(new JObject { ["operation"] = "match", ["catalog_path"] = path, ["queries"] = q });
        }

        [Fact]
        public void Utf16KeynoteFileIsReadBySearch()
        {
            string path = WriteUtf16(Keynotes);
            try
            {
                JObject r = CatalogLookup.Handle(new JObject { ["operation"] = "search", ["catalog_path"] = path, ["query"] = "drywall" });
                Assert.Equal("utf-16le", (string)r["encoding_used"]);
                Assert.Equal("22.09.21-001", (string)r["matches"][0]["code"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void SizeDecidesBetweenTwoWallsAndIsAccepted()
        {
            string path = WriteUtf16(Keynotes);
            try
            {
                JObject r = Match(path, "Muro de concreto 200 mm");
                JObject first = (JObject)r["results"][0];
                Assert.Equal("accept", (string)first["decision"]);
                Assert.Equal("22.03.30-002", (string)first["code"]);
                Assert.True((double)first["confidence"] >= 80);
                JObject loser = (JObject)first["candidates"].First(c => (string)c["code"] == "22.03.30-001");
                Assert.Contains("15 cm", loser["number_conflicts"].Select(x => (string)x));
                Assert.NotNull((string)loser["rejection_reason"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void WithoutSizeTheTwoWallsAreAmbiguous()
        {
            string path = WriteUtf16(Keynotes);
            try
            {
                JObject first = (JObject)Match(path, "Muro de concreto")["results"][0];
                Assert.Equal("review", (string)first["decision"]);
                Assert.StartsWith("ambiguous", (string)first["reason"]);
                Assert.Null((string)first["code"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void NoSharedWordIsNoCandidateAndParentsAreNotCandidates()
        {
            string path = WriteUtf16(Keynotes);
            try
            {
                JObject r = Match(path, "Ventana corrediza aluminio", "Concreto");
                Assert.StartsWith("no_candidate", (string)r["results"][0]["reason"]);
                Assert.DoesNotContain(r["results"][1]["candidates"], c => (string)c["code"] == "22.03");
                Assert.Equal(2, (int)r["skipped_non_leaf"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void BelowThresholdIsReviewWithItsReason()
        {
            string path = WriteUtf16(Keynotes);
            try
            {
                JObject first = (JObject)Match(path, "Puerta metálica cortafuego 90 min")["results"][0];
                Assert.Equal("review", (string)first["decision"]);
                Assert.StartsWith("below_threshold", (string)first["reason"]);
            }
            finally { File.Delete(path); }
        }
    }
}
