// -----------------------------------------------------------------------------
// Horizun server tests - original Horizun code.
//
// horizun_read_schedule end to end on SYNTHETIC vector PDFs: the sheets of
// ScheduleCardFixtures (linked from the Core tests) drawn by SyntheticPdf into
// disposable files, read back through PdfPig, and held against the reading of the
// very same primitives - so what the PDF route adds (points to millimetres,
// letters to texts, the page's /Rotate) is what is under test. Plus the
// refusals that belong to the tool: no source, two sources, no scale, a file that
// is not a PDF, a page that does not exist, a page with no text.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Horizun.Contracts;
using Horizun.Core.Tests;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ReadScheduleToolTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hz-read-schedule-" + Guid.NewGuid().ToString("N"));

        public ReadScheduleToolTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private string Pdf(string name, IList<JObject> sheets, int rotate = 0, bool withText = true)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, SyntheticPdf.Write(sheets, rotate, withText: withText));
            return path;
        }

        private static JObject Call(JObject args) => ReadScheduleTool.Handle(args, CancellationToken.None);

        private static JObject WithProposal(JObject a)
        {
            a["requirement_set"] = ScheduleCardFixtures.Proposal();
            return a;
        }

        private static string Code(Action act)
        {
            ToolRefusal r = Assert.Throws<ToolRefusal>(act);
            return (string)r.Detail?["code"];
        }

        [Fact]
        public void A_beam_card_drawn_into_a_pdf_reads_to_the_same_set_as_its_primitives()
        {
            SyntheticSheet sheet = ScheduleCardFixtures.BeamSheet();
            JObject fromPrimitives = Call(WithProposal(ScheduleCardFixtures.Args(new JObject { ["primitives"] = sheet.ToJson() })));
            JObject fromPdf = Call(WithProposal(ScheduleCardFixtures.Args(new JObject { ["file_path"] = Pdf("beam.pdf", new[] { sheet.ToJson() }) })));

            Assert.Equal(JTokenType.Object, fromPrimitives["requirement_set"].Type);
            Assert.True(JToken.DeepEquals(fromPrimitives["requirement_set"], fromPdf["requirement_set"]),
                        fromPrimitives["requirement_set"].ToString(Newtonsoft.Json.Formatting.None) + "\n" +
                        fromPdf["requirement_set"].ToString(Newtonsoft.Json.Formatting.None) + "\n" + fromPdf["requirement_set_not_proposed"] + "\n" + fromPdf["refusals"]);
            Assert.Empty((JArray)fromPdf["refusals"]);
            Assert.Equal("read_clean", (string)fromPdf["summary"]["verdict"]);
            JObject card = (JObject)fromPdf["cards"].Single();
            Assert.Equal("V-101", (string)card["title"]);
            Assert.Equal(new[] { "A", "B" }, card["axes"].Select(a => (string)a["name"]));
            Assert.Equal(6000, (double)card["axes"][1]["station_mm"], 0);
            JObject bottom = (JObject)card["bars"].Single(b => (string)b["face"] == "bottom");
            Assert.Equal(6500, (double)bottom["drawn_developed_mm"], 0);
            Assert.Equal(250, (double)bottom["hook_end"]["leg_mm"], 0);

            JObject file = (JObject)fromPdf["file"];
            Assert.Equal(64, ((string)file["sha256"]).Length);
            Assert.Equal(1, (int)file["pages_in_file"]);
            Assert.True((bool)fromPdf["writes_nothing"]);
        }

        [Fact]
        public void A_page_displayed_rotated_is_read_as_it_is_displayed()
        {
            SyntheticSheet sheet = ScheduleCardFixtures.BeamSheet();
            JObject upright = Call(WithProposal(ScheduleCardFixtures.Args(new JObject { ["file_path"] = Pdf("upright.pdf", new[] { sheet.ToJson() }) })));
            JObject rotated = Call(WithProposal(ScheduleCardFixtures.Args(new JObject { ["file_path"] = Pdf("rotated.pdf", new[] { sheet.ToJson() }, 90) })));
            Assert.True(rotated["requirement_set"].Type == JTokenType.Object,
                        rotated["requirement_set_not_proposed"] + "\n" + rotated["refusals"] + "\n" + rotated["cards"]);
            Assert.True(JToken.DeepEquals(upright["requirement_set"], rotated["requirement_set"]), rotated.ToString());
            Assert.True(JToken.DeepEquals(upright["checks"], rotated["checks"]));
        }

        [Fact]
        public void A_wall_card_with_rotated_callouts_and_a_table_are_read_from_one_pdf_page()
        {
            var s = new SyntheticSheet();
            ScheduleCardFixtures.WallCard(s, new ScheduleCardFixtures.WallCardSpec());
            ScheduleCardFixtures.Table(s, 20, 20, ScheduleCardFixtures.TableRows());
            JObject r = Call(ScheduleCardFixtures.Args(new JObject
            {
                ["file_path"] = Pdf("wall-and-table.pdf", new[] { s.ToJson() }), ["table"] = ScheduleCardFixtures.TableSpec()
            }));
            Assert.Empty((JArray)r["refusals"]);
            JObject wall = (JObject)r["cards"].Single();
            Assert.Equal("wall", (string)wall["kind"]);
            JObject v = (JObject)wall["vertical_bars"].Single();
            Assert.Equal(4, (int)v["count"]);
            Assert.Equal(2900, (double)v["drawn_developed_mm"], 0);
            Assert.Equal(2500, (double)v["z_top_mm"], 0);
            Assert.Equal(2, ((JArray)r["tables"][0]["rows"]).Count);
            Assert.Equal("pass", (string)r["checks"].Single(c => (string)c["check"] == "table_rows_sum_to_total")["result"]);
            Assert.Equal("read_clean", (string)r["summary"]["verdict"]);
        }

        [Fact]
        public void A_broken_card_in_a_pdf_is_refused_and_its_failed_check_named()
        {
            SyntheticSheet sheet = ScheduleCardFixtures.BeamSheet(c => { c.TopLine = false; c.StirrupTotal = "SON 35"; });
            JObject r = Call(WithProposal(ScheduleCardFixtures.Args(new JObject { ["file_path"] = Pdf("broken.pdf", new[] { sheet.ToJson() }) })));
            Assert.Contains(r["refusals"], x => (string)x["code"] == "callout_without_line");
            Assert.Contains(r["checks"], x => (string)x["check"] == "stirrups_sum_to_stated_total" && (string)x["result"] == "fail");
            Assert.Equal(JTokenType.Null, r["requirement_set"].Type);
            Assert.Equal("checks_failed", (string)r["summary"]["verdict"]);
        }

        [Fact]
        public void Two_zone_callouts_drawn_in_one_text_operation_with_a_gap_are_two_zones()
        {
            // Measured on real sheets: zone callouts written side by side reach the reader as one
            // run of letters, a space's width apart with no space character between them.
            SyntheticSheet sheet = ScheduleCardFixtures.BeamSheet(c =>
            {
                c.Zones = new List<Tuple<string, double, double>>();
                c.StirrupTotal = null;
                c.Extra = (s, k) =>
                {
                    double x0 = k.X(1.0), size = ScheduleCardFixtures.H;
                    // Helvetica: "9.#3@.06" and "3.#3@.20" are each 4.351 em; the gap is 0.4 em.
                    double w = 4.351 * size, gap = 0.4 * size;
                    s.Texts.Add(new JObject
                    {
                        ["text"] = "9.#3@.063.#3@.20", ["at"] = new JArray(x0, k.ZoneY + 0.6), ["height"] = size,
                        ["direction"] = new JArray(1.0, 0.0), ["tj"] = new JArray("9.#3@.06", -400, "3.#3@.20")
                    });
                    double c1 = x0 + w / 2, c2 = x0 + w + gap + w / 2;
                    s.Line(c1 - 4.8, k.ZoneY, c1 + 4.8, k.ZoneY).Line(c2 - 4, k.ZoneY, c2 + 4, k.ZoneY);
                };
            });
            JObject r = Call(ScheduleCardFixtures.Args(new JObject { ["file_path"] = Pdf("gap.pdf", new[] { sheet.ToJson() }) }));
            JArray zones = (JArray)r["cards"].Single()["zones"];
            Assert.Equal(new[] { 9, 3 }, zones.Select(z => (int)z["count"]));
            Assert.All(r["checks"].Where(c => (string)c["check"] == "zone_count_matches_its_length"), c => Assert.Equal("pass", (string)c["result"]));
        }

        [Fact]
        public void A_page_with_no_text_layer_is_refused_by_name_and_not_recognised_by_shape()
        {
            JObject r = Call(ScheduleCardFixtures.Args(new JObject
            {
                ["file_path"] = Pdf("scan.pdf", new[] { ScheduleCardFixtures.BeamSheet().ToJson() }, withText: false)
            }));
            Assert.Contains(r["refusals"], x => (string)x["code"] == "sheet_has_no_text");
            Assert.Empty((JArray)r["cards"]);
        }

        [Fact]
        public void Pages_are_chosen_by_number_and_a_missing_page_refuses_the_call()
        {
            string path = Pdf("two.pdf", new[] { ScheduleCardFixtures.BeamSheet().ToJson(),
                ScheduleCardFixtures.WallCard(new SyntheticSheet(), new ScheduleCardFixtures.WallCardSpec()).ToJson() });
            JObject r = Call(ScheduleCardFixtures.Args(new JObject { ["file_path"] = path, ["pages"] = new JArray(2) }));
            Assert.Equal("wall", (string)r["cards"].Single()["kind"]);
            Assert.Equal(2, (int)r["cards"][0]["page"]);
            Assert.Equal("p2:M-1", (string)r["cards"][0]["id"]);
            Assert.Equal("page_out_of_range", Code(() => Call(ScheduleCardFixtures.Args(new JObject { ["file_path"] = path, ["pages"] = new JArray(3) }))));
        }

        [Fact]
        public void Without_a_scale_nothing_is_read()
        {
            JObject a = ScheduleCardFixtures.Args(new JObject { ["file_path"] = Pdf("noscale.pdf", new[] { ScheduleCardFixtures.BeamSheet().ToJson() }) });
            a.Remove("scale");
            Assert.Equal("scale_not_declared", Code(() => Call(a)));
        }

        [Fact]
        public void Exactly_one_source_is_read()
        {
            Assert.Equal("source_not_declared", Code(() => Call(ScheduleCardFixtures.Args())));
            Assert.Equal("source_not_declared", Code(() => Call(ScheduleCardFixtures.Args(new JObject
            {
                ["primitives"] = ScheduleCardFixtures.BeamSheet().ToJson(), ["cad_entities"] = new JArray()
            }))));
        }

        [Fact]
        public void A_file_that_is_not_a_pdf_or_is_not_there_is_refused()
        {
            string txt = Path.Combine(_dir, "notes.pdf");
            File.WriteAllText(txt, "not a pdf");
            Assert.Equal("pdf_not_readable", Code(() => Call(ScheduleCardFixtures.Args(new JObject { ["file_path"] = txt }))));
            Assert.Equal("file_not_found", Code(() => Call(ScheduleCardFixtures.Args(new JObject { ["file_path"] = Path.Combine(_dir, "missing.pdf") }))));
            Assert.Equal("file_not_usable", Code(() => Call(ScheduleCardFixtures.Args(new JObject { ["file_path"] = "relative.pdf" }))));
        }

        [Fact]
        public void The_dwg_route_reads_horizun_cad_extract_entities_and_wants_the_text_anchor_declared()
        {
            JArray entities = ScheduleCardFixtures.BeamSheet().ToCadEntities();
            JObject r = Call(WithProposal(ScheduleCardFixtures.Args(new JObject { ["cad_entities"] = entities, ["cad_text_anchor"] = "middle" })));
            Assert.Equal(JTokenType.Object, r["requirement_set"].Type);
            Assert.Equal("cad_text_anchor_not_declared", Code(() => Call(ScheduleCardFixtures.Args(new JObject { ["cad_entities"] = entities }))));
        }

        [Fact]
        public void The_tool_is_declared_host_resident_read_only_and_carrying_external_text()
        {
            CommandContract c = Contract.Find("horizun_read_schedule");
            Assert.NotNull(c);
            Assert.Null(c.Command);
            Assert.Equal(ToolEffect.ReadOnly, c.Effect);
            Assert.True(c.ExternalContent);
            Assert.Contains("structure", c.Toolsets);
            JObject props = (JObject)c.InputSchema["properties"];
            foreach (string k in new[] { "file_path", "cad_entities", "cad_text_anchor", "primitives", "scale", "tolerances", "table", "requirement_set", "pages" })
                Assert.NotNull(props[k]);
        }
    }
}
