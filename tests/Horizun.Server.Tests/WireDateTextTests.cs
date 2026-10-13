// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// TEXT THAT LOOKS LIKE A DATE STAYS TEXT ON THE WAY TO REVIT AND BACK (backlog P0 #1,
// 2026-10-10). Every hop parsed with JObject.Parse, whose default DateParseHandling
// turns ISO 8601 text into a DateTime: "2026-10-10T12:00:00+02:00" reached the command
// as 10/10/2026 05:00:00 (local zone, invariant culture), was written to the parameter
// like that, and the re-read compared against the same converted value - so it was
// reported verified. The stdin reader, the pipe on both sides, the dispatcher and every
// command's own parse of paramsJson now go through JsonText.ParseObject.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class WireDateTextTests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            Assert.True(d != null, "repository root not found");
            return d.FullName;
        }

        [Fact]
        public void An_iso_looking_string_survives_the_parse_as_the_same_text()
        {
            JObject o = JsonText.ParseObject("{\"value\":\"2026-10-10T12:00:00+02:00\",\"z\":\"2026-10-10T10:00:00.000Z\"}");
            Assert.Equal(JTokenType.String, o["value"].Type);
            Assert.Equal("2026-10-10T12:00:00+02:00", (string)o["value"]);
            Assert.Equal("2026-10-10T10:00:00.000Z", (string)o["z"]);
            Assert.Contains("\"2026-10-10T12:00:00+02:00\"", o.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void No_hop_on_the_wire_parses_requests_or_replies_with_date_conversion()
        {
            string root = Root();
            var files = new List<string>
            {
                Path.Combine(root, "src", "Horizun.Server", "Program.cs"),
                Path.Combine(root, "src", "Horizun.Server", "PipeClient.cs"),
                Path.Combine(root, "src", "Horizun.Revit", "Core", "Dispatcher.cs")
            };
            files.AddRange(Directory.GetFiles(Path.Combine(root, "src", "Horizun.Revit", "Transport"), "*.cs"));
            files.AddRange(Directory.GetFiles(Path.Combine(root, "src", "Horizun.Revit", "Commands"), "*.cs"));
            var wire = new Regex(@"JObject\.Parse\((paramsJson|req\.ParamsJson|line|line\.Line|reply\.Line)\)");
            var offenders = files.SelectMany(f => File.ReadAllLines(f).Select((l, i) => new { f, l, i }))
                                 .Where(x => wire.IsMatch(x.l))
                                 .Select(x => Path.GetFileName(x.f) + ":" + (x.i + 1)).ToList();
            Assert.True(offenders.Count == 0,
                offenders.Count + " wire parse(s) still convert ISO text to dates; use JsonText.ParseObject: " +
                string.Join(", ", offenders.Take(12)));
        }
    }
}
