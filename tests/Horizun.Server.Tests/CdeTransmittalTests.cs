// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// horizun_information_container: transmittal, record_review and register. Every test
// runs the real handler against a real disposable CDE tree under a settings root of its
// own - never the machine's - so the permission rung is the test's choice.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class CdeTransmittalTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _settingsRoot;
        private readonly string _savedRoot;
        private readonly string _root, _wip, _shared, _published, _archived;

        public CdeTransmittalTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-tr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settingsRoot = Path.Combine(Path.GetTempPath(), "hz-tr-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_settingsRoot);
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _settingsRoot);
            Profile("full_write");

            _root = Path.Combine(_dir, "cde");
            _wip = Path.Combine(_root, "01_WIP");
            _shared = Path.Combine(_root, "02_SHARED");
            _published = Path.Combine(_root, "03_PUBLISHED");
            _archived = Path.Combine(_root, "04_ARCHIVE");
            foreach (string f in new[] { _wip, _shared, _published, _archived }) Directory.CreateDirectory(f);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_dir, true); } catch { }
            try { Directory.Delete(_settingsRoot, true); } catch { }
        }

        private void Profile(string profile)
            => File.WriteAllText(HorizunPaths.SettingsPath(), @"{""permission_profile"":""" + profile + @"""}");

        private static JObject Run(JObject args) => InformationContainerTool.Handle(args, CancellationToken.None);

        private static JObject States() => new JObject
        {
            ["wip"] = "01_WIP", ["shared"] = "02_SHARED", ["published"] = "03_PUBLISHED", ["archived"] = "04_ARCHIVE"
        };

        private static JObject Container(string number, string status, string revision = "P01") => new JObject
        {
            ["fields"] = new JObject
            {
                ["project"] = "HZ01", ["originator"] = "HRZ", ["volume"] = "ZZ", ["level"] = "XX",
                ["type"] = "M3", ["role"] = "A", ["number"] = number
            },
            ["status"] = status, ["revision"] = revision, ["title"] = "Container " + number
        };

        private static string Name(string number) => "HZ01-HRZ-ZZ-XX-M3-A-" + number;

        private string Write(string folder, string file, string content)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, file);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        private void Stamp(string file, JObject container)
        {
            JObject r = Run(new JObject { ["operation"] = "stamp", ["file_path"] = file, ["information_container"] = container, ["dry_run"] = false });
            Assert.True((bool)r["verified"], r.ToString());
        }

        /// <summary>Stamp in WIP (S0) and transition to shared with the given status: the normal path.</summary>
        private string Shared(string number, string status = "S3", string content = null)
        {
            string wip = Write(_wip, Name(number) + ".pdf", content ?? "bytes of " + number);
            Stamp(wip, Container(number, "S0"));
            JObject t = Run(new JObject
            {
                ["operation"] = "transition", ["root"] = _root, ["states"] = States(), ["file_path"] = wip,
                ["from_state"] = "wip", ["to_state"] = "shared", ["status"] = status, ["dry_run"] = false
            });
            Assert.True((bool)t["verified"], t.ToString());
            return (string)t["destination"];
        }

        private string Published(string sharedFile, string approvedBy = "Lead appointing party")
        {
            JObject t = Run(new JObject
            {
                ["operation"] = "transition", ["root"] = _root, ["states"] = States(), ["file_path"] = sharedFile,
                ["from_state"] = "shared", ["to_state"] = "published", ["status"] = "A1", ["revision"] = "C01",
                ["approved_by"] = approvedBy, ["dry_run"] = false
            });
            Assert.True((bool)t["verified"], t.ToString());
            return (string)t["destination"];
        }

        private JObject TransmittalArgs(string state, bool dryRun, string purpose, params string[] files) => new JObject
        {
            ["operation"] = "transmittal", ["root"] = _root, ["states"] = States(), ["project"] = "HZ01",
            ["state"] = state, ["purpose"] = purpose, ["dry_run"] = dryRun,
            ["file_paths"] = new JArray(files.Cast<object>().ToArray()),
            ["sender"] = new JObject { ["name"] = "Ana Author", ["organization"] = "Task Team A", ["role"] = "Information author" },
            ["recipients"] = new JArray
            {
                new JObject { ["name"] = "Rui Reviewer", ["organization"] = "Lead Appointed Party", ["role"] = "Information manager" }
            }
        };

        private string TransmittalDir => Path.Combine(_root, ".horizun", "transmittals");

        // ---- transmittal ---------------------------------------------------------------

        [Fact]
        public void A_transmittal_rehearses_then_issues_a_numbered_record_with_markdown_and_csv()
        {
            string a = Shared("0101"), b = Shared("0102");

            JObject dry = Run(TransmittalArgs("shared", true, "S3", a, b));
            Assert.False((bool)dry["written"]);
            Assert.Equal("HZ01-TR-0001", (string)dry["next_id_preview"]);
            Assert.False((bool)dry["id_reserved"]);
            Assert.False(Directory.Exists(TransmittalDir));

            JObject real = Run(TransmittalArgs("shared", false, "S3", a, b));
            Assert.True((bool)real["written"], real.ToString());
            Assert.True((bool)real["verified"], real.ToString());
            Assert.Equal("HZ01-TR-0001", (string)real["id"]);
            Assert.Equal("Suitable for review and comment", (string)real["purpose"]["description"]);

            string json = Path.Combine(TransmittalDir, "HZ01-TR-0001.json");
            string md = Path.Combine(TransmittalDir, "HZ01-TR-0001.md");
            string csv = Path.Combine(TransmittalDir, "HZ01-TR-0001.csv");
            Assert.True(File.Exists(json) && File.Exists(md) && File.Exists(csv));

            JObject doc = JObject.Parse(File.ReadAllText(json));
            Assert.Equal(InformationContainerTool.TransmittalSchema, (string)doc["schema"]);
            Assert.Equal(1, (int)doc["sequence"]);
            Assert.Equal("Task Team A", (string)doc["sender"]["organization"]);
            Assert.Equal("Lead Appointed Party", (string)doc["recipients"][0]["organization"]);
            Assert.Equal("S3", (string)doc["purpose"]["code"]);
            JArray containers = (JArray)doc["containers"];
            Assert.Equal(2, containers.Count);
            Assert.Equal(Name("0101"), (string)containers[0]["name"]);
            Assert.Equal("Container 0101", (string)containers[0]["title"]);
            Assert.Equal("P01", (string)containers[0]["revision"]);
            Assert.Equal("02_SHARED/" + Name("0101") + ".pdf", (string)containers[0]["path"]);
            string sealedSha = (string)InformationContainer.ReadSidecar(InformationContainer.SidecarPath(a), out _)["sha256"];
            Assert.Equal(sealedSha, (string)containers[0]["sha256"]);
            Assert.Equal(new FileInfo(a).Length, (long)containers[0]["bytes"]);

            string mdText = File.ReadAllText(md);
            Assert.Contains("# Transmittal HZ01-TR-0001", mdText);
            Assert.Contains("S3 - Suitable for review and comment", mdText);
            Assert.Contains(sealedSha, mdText);
            byte[] csvBytes = File.ReadAllBytes(csv);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csvBytes.Take(3).ToArray());   // Excel reads accents
            string[] csvLines = File.ReadAllText(csv).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, csvLines.Length);
            Assert.StartsWith("transmittal_id,issued_utc,purpose", csvLines[0].TrimStart('﻿'));
            Assert.StartsWith("HZ01-TR-0001,", csvLines[1]);

            // The files it issued are untouched and still match their seals.
            Assert.Equal("match", (string)Run(new JObject { ["operation"] = "verify", ["file_path"] = a })["verdict"]);

            // The same issue again is recognised; a deliberate re-issue (a new note) takes the next number.
            JObject again = Run(TransmittalArgs("shared", false, "S3", a, b));
            Assert.True((bool)again["already_issued"]);
            Assert.Equal("HZ01-TR-0001", (string)again["id"]);
            Assert.Equal(3 + 1, Directory.GetFiles(TransmittalDir).Length);   // 3 files + the sequence
            JObject reissue = TransmittalArgs("shared", false, "S3", a);
            reissue["note"] = "re-issued after a lost e-mail";
            Assert.Equal("HZ01-TR-0002", (string)Run(reissue)["id"]);
            JObject seq = JObject.Parse(File.ReadAllText(Path.Combine(TransmittalDir, "HZ01.sequence.json")));
            Assert.Equal(2, (int)seq["last"]);
        }

        [Fact]
        public void A_transmittal_refuses_a_file_whose_hash_no_longer_matches_its_sidecar_and_writes_nothing()
        {
            string ok = Shared("0110");
            string edited = Shared("0111");
            File.AppendAllText(edited, "edited after sealing");
            ToolRefusal r = Assert.Throws<ToolRefusal>(() => Run(TransmittalArgs("shared", false, "S3", ok, edited)));
            Assert.Contains("verdict modified", r.Message);
            Assert.Contains("Nothing was written", r.Message);
            Assert.False(Directory.Exists(TransmittalDir));

            // Unsealed, outside the state folder, unknown purpose, WIP: each refused.
            string bare = Write(_shared, Name("0112") + ".pdf", "never stamped");
            Assert.Contains("stamp it first", Assert.Throws<ToolRefusal>(() => Run(TransmittalArgs("shared", true, "S3", bare))).Message);
            string wipFile = Path.Combine(_wip, Name("0110") + ".pdf");
            Assert.Throws<ToolRefusal>(() => Run(TransmittalArgs("shared", true, "S3", wipFile)));
            Assert.Throws<ToolRefusal>(() => Run(TransmittalArgs("shared", true, "S9", ok)));
            Assert.Throws<ToolRefusal>(() => Run(TransmittalArgs("wip", true, "S0", wipFile)));
            JObject noOrg = TransmittalArgs("shared", true, "S3", ok);
            noOrg["recipients"] = new JArray { new JObject { ["name"] = "Someone" } };
            Assert.Throws<ToolRefusal>(() => Run(noOrg));
            JObject unknownKey = TransmittalArgs("shared", true, "S3", ok);
            unknownKey["sender"]["mail"] = "x";
            Assert.Throws<ToolRefusal>(() => Run(unknownKey));
            Assert.False(Directory.Exists(TransmittalDir));
        }

        [Fact]
        public void A_status_that_differs_from_the_purpose_is_a_warning_not_a_refusal()
        {
            string a = Shared("0115", "S2");
            JObject r = Run(TransmittalArgs("shared", true, "S3", a));
            Assert.Contains(((JArray)r["warnings"]), w => (string)w["code"] == "status_differs_from_purpose");
        }

        [Fact]
        public void A_number_never_reuses_a_file_and_a_corrupt_sequence_refuses()
        {
            string a = Shared("0120");
            // A leftover render of number 0001 with no record: 0001 is burnt, never overwritten.
            Directory.CreateDirectory(TransmittalDir);
            File.WriteAllText(Path.Combine(TransmittalDir, "HZ01-TR-0001.md"), "leftover");
            JObject r = Run(TransmittalArgs("shared", false, "S3", a));
            Assert.Equal("HZ01-TR-0002", (string)r["id"]);
            Assert.Equal("leftover", File.ReadAllText(Path.Combine(TransmittalDir, "HZ01-TR-0001.md")));

            File.WriteAllText(Path.Combine(TransmittalDir, "HZ01.sequence.json"), "{ not json");
            JObject next = TransmittalArgs("shared", false, "S3", a);
            next["note"] = "second";
            Assert.Contains("never guessed", Assert.Throws<ToolRefusal>(() => Run(next)).Message);
            Assert.False(File.Exists(Path.Combine(TransmittalDir, "HZ01-TR-0003.json")));
        }

        [Fact]
        public void Concurrent_transmittals_get_distinct_consecutive_numbers()
        {
            string a = Shared("0130");
            const int n = 12;
            var ids = new string[n];
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = n }, i =>
            {
                JObject args = TransmittalArgs("shared", false, "S3", a);
                args["note"] = "issue " + i;
                JObject r = Run(args);
                Assert.True((bool)r["verified"], r.ToString());
                ids[i] = (string)r["id"];
            });
            Assert.Equal(n, ids.Distinct().Count());
            Assert.Equal(Enumerable.Range(1, n).Select(i => "HZ01-TR-" + i.ToString("D4")).ToArray(), ids.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.Equal(n, Directory.GetFiles(TransmittalDir, "HZ01-TR-*.json").Length);
            Assert.Equal(n, (int)JObject.Parse(File.ReadAllText(Path.Combine(TransmittalDir, "HZ01.sequence.json")))["last"]);
            Assert.Empty(Directory.GetFiles(TransmittalDir, "*.tmp"));
        }

        [Fact]
        public void Published_information_is_issued_only_with_an_approval_on_record()
        {
            string approved = Published(Shared("0140", "S4"));
            JObject r = Run(TransmittalArgs("published", false, "A1", approved));
            Assert.True((bool)r["verified"], r.ToString());
            Assert.Equal("Lead appointing party", (string)r["containers"][0]["approved_by"]);

            // A container stamped straight into published carries no approval: refused without approved_by.
            string unapproved = Write(_published, Name("0141") + ".pdf", "no approval");
            Stamp(unapproved, Container("0141", "A1", "C01"));
            Assert.Contains("needs approved_by", Assert.Throws<ToolRefusal>(() => Run(TransmittalArgs("published", true, "A1", unapproved))).Message);
            JObject withApproval = TransmittalArgs("published", true, "A1", unapproved);
            withApproval["approved_by"] = "Project director";
            Assert.False((bool)Run(withApproval)["written"]);
        }

        [Theory]
        [InlineData("read_only")]
        [InlineData("safe_write")]
        public void Issuing_and_recording_need_full_write_but_rehearsals_do_not(string profile)
        {
            string a = Shared("0150");
            Profile(profile);
            Assert.False((bool)Run(TransmittalArgs("shared", true, "S3", a))["written"]);
            Assert.Contains("permission_profile=" + profile, Assert.Throws<ToolRefusal>(() => Run(TransmittalArgs("shared", false, "S3", a))).Message);
            Assert.False(Directory.Exists(TransmittalDir));

            JObject review = ReviewArgs(null, Name("0150"), "accepted", null, dryRun: true);
            Assert.False((bool)Run(review)["written"]);
            review["dry_run"] = false;
            Assert.Throws<ToolRefusal>(() => Run(review));
            Assert.False(File.Exists(Path.Combine(_root, ".horizun", "reviews.jsonl")));
        }

        [Fact]
        public void Renders_escape_markdown_and_neutralise_spreadsheet_formulas()
        {
            var doc = new JObject
            {
                ["id"] = "HZ01-TR-0009", ["project"] = "HZ01", ["issued_utc"] = "2026-09-24T00:00:00.000Z",
                ["sender"] = new JObject { ["name"] = "=HYPERLINK(\"x\")", ["organization"] = "A|B", ["role"] = null },
                ["recipients"] = new JArray { new JObject { ["name"] = "R", ["organization"] = "O, Inc", ["role"] = "IM" } },
                ["purpose"] = new JObject { ["code"] = "S3", ["description"] = "review" }, ["state"] = "shared",
                ["approved_by"] = null, ["note"] = "line1\nline2",
                ["containers"] = new JArray { new JObject { ["name"] = "N", ["title"] = "-2+3", ["status"] = "S3", ["revision"] = "P01", ["file"] = "N.pdf", ["bytes"] = 1L, ["sha256"] = "ab" } }
            };
            string csv = InformationContainerTool.RenderCsv(doc);
            Assert.Contains(",'-2+3,", csv);
            Assert.Contains("\"'=HYPERLINK(\"\"x\"\") (A|B)\"", csv);
            Assert.Contains("\"R (O, Inc, IM)\"", csv);
            string md = InformationContainerTool.RenderMarkdown(doc);
            Assert.Contains("A\\|B", md);
            Assert.Contains("line1 line2", md);
        }

        // ---- record_review -------------------------------------------------------------

        private JObject ReviewArgs(string transmittalId, string container, string outcome, string comments, bool dryRun = false,
                                   string reviewedOn = null)
        {
            var a = new JObject
            {
                ["operation"] = "record_review", ["root"] = _root, ["states"] = States(), ["outcome"] = outcome,
                ["reviewed_by"] = "Rui Reviewer", ["reviewer_organization"] = "Lead Appointed Party", ["dry_run"] = dryRun
            };
            if (transmittalId != null) a["transmittal_id"] = transmittalId;
            if (container != null) a["container"] = container;
            if (comments != null) a["comments"] = comments;
            if (reviewedOn != null) a["reviewed_on"] = reviewedOn;
            return a;
        }

        private string ReviewsLog => Path.Combine(_root, ".horizun", "reviews.jsonl");

        [Fact]
        public void A_review_is_appended_verified_and_a_rejection_moves_nothing()
        {
            string a = Shared("0160"), b = Shared("0161");
            string id = (string)Run(TransmittalArgs("shared", false, "S3", a, b))["id"];
            string[] before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Where(p => !p.Contains(".horizun")).OrderBy(p => p).ToArray();

            Assert.Contains("needs comments", Assert.Throws<ToolRefusal>(() => Run(ReviewArgs(id, Name("0161"), "rejected", null))).Message);

            JObject dry = Run(ReviewArgs(id, Name("0161"), "rejected", "Clash with the structure at grid C", dryRun: true));
            Assert.False((bool)dry["written"]);
            Assert.False(File.Exists(ReviewsLog));

            JObject rejected = Run(ReviewArgs(id, Name("0161"), "rejected", "Clash with the structure at grid C"));
            Assert.True((bool)rejected["verified"], rejected.ToString());
            Assert.False((bool)rejected["changes_state"]);
            Assert.Contains("no state changed", (string)rejected["note"]);

            JObject whole = Run(ReviewArgs(id, null, "accepted_with_comments", "Minor: title block"));
            Assert.True((bool)whole["verified"]);
            Assert.Equal(2, ((JArray)whole["containers"]).Count);   // the whole transmittal

            string[] lines = File.ReadAllLines(ReviewsLog).Where(l => l.Length > 0).ToArray();
            Assert.Equal(2, lines.Length);
            JObject first = JObject.Parse(lines[0]);
            Assert.Equal(InformationContainerTool.ReviewSchema, (string)first["schema"]);
            Assert.Equal("rejected", (string)first["outcome"]);
            Assert.Equal(id, (string)first["transmittal_id"]);
            Assert.Equal(Name("0161"), (string)first["containers"][0]["name"]);

            // Nothing moved, nothing appeared outside .horizun.
            string[] after = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Where(p => !p.Contains(".horizun")).OrderBy(p => p).ToArray();
            Assert.Equal(before, after);
            Assert.Empty(Directory.GetFiles(_published));

            // The same review again is recognised, not duplicated.
            Assert.True((bool)Run(ReviewArgs(id, Name("0161"), "rejected", "Clash with the structure at grid C"))["already_recorded"]);
            Assert.Equal(2, File.ReadAllLines(ReviewsLog).Count(l => l.Length > 0));
        }

        [Fact]
        public void A_review_must_name_something_that_was_issued_or_sealed()
        {
            string a = Shared("0170");
            string id = (string)Run(TransmittalArgs("shared", false, "S3", a))["id"];
            Assert.Throws<ToolRefusal>(() => Run(ReviewArgs("HZ01-TR-0099", null, "accepted", null)));      // no such transmittal
            Assert.Throws<ToolRefusal>(() => Run(ReviewArgs("../../evil", null, "accepted", null)));        // not a number
            Assert.Throws<ToolRefusal>(() => Run(ReviewArgs(id, Name("0999"), "accepted", null)));         // not carried
            Assert.Throws<ToolRefusal>(() => Run(ReviewArgs(null, Name("0999"), "accepted", null)));       // not sealed anywhere
            Assert.Throws<ToolRefusal>(() => Run(ReviewArgs(null, null, "accepted", null)));               // nothing named
            Assert.Throws<ToolRefusal>(() => Run(ReviewArgs(id, null, "approved", null)));                 // not an outcome
            Assert.Throws<ToolRefusal>(() => Run(ReviewArgs(id, null, "accepted", null, reviewedOn: "2999-01-01")));

            // A sealed container can be reviewed outside a transmittal; with two revisions, say which.
            JObject r = Run(ReviewArgs(null, Name("0170"), "accepted", null));
            Assert.True((bool)r["verified"], r.ToString());
            Assert.False(File.Exists(Path.Combine(_published, Name("0170") + ".pdf")));
        }

        // ---- register ------------------------------------------------------------------

        [Fact]
        public void The_register_joins_transitions_transmittals_and_reviews_per_container()
        {
            string a = Shared("0180"), b = Shared("0181");
            string t1 = (string)InformationContainerTool.Transmittal(TransmittalArgs("shared", false, "S3", a, b), CancellationToken.None,
                new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc))["id"];
            InformationContainerTool.RecordReview(ReviewArgs(t1, Name("0180"), "accepted", null, reviewedOn: "2026-09-12"),
                CancellationToken.None, new DateTime(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc));
            InformationContainerTool.RecordReview(ReviewArgs(t1, Name("0181"), "rejected", "Levels wrong", reviewedOn: "2026-09-13"),
                CancellationToken.None, new DateTime(2026, 9, 13, 9, 0, 0, DateTimeKind.Utc));
            string published = Published(a);
            string t2 = (string)InformationContainerTool.Transmittal(TransmittalArgs("published", false, "A1", published), CancellationToken.None,
                new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc))["id"];

            JObject r = Run(new JObject { ["operation"] = "register", ["root"] = _root, ["states"] = States() });
            Assert.Equal(0, (int)r["total_incoherences"]);
            Assert.Equal(2, (int)r["total_containers"]);
            JObject row = ((JArray)r["containers"]).OfType<JObject>().Single(c => (string)c["container"] == Name("0180"));
            Assert.Equal(new[] { "wip", "shared", "published" }, ((JArray)row["states_reached"]).Select(s => (string)s).ToArray());
            Assert.Equal("published", (string)row["last_transition_to"]);
            Assert.Equal(new[] { t1, t2 }, ((JArray)row["transmittals"]).Select(s => (string)s).ToArray());
            Assert.Equal("Lead appointing party", (string)row["approvals"].Single(x => (string)x["to_state"] == "published")["approved_by"]);
            Assert.Equal("accepted", (string)row["latest_review"]["outcome"]);
            List<string> kinds = ((JArray)row["history"]).Select(e => (string)e["kind"]).ToList();
            Assert.Equal(2, kinds.Count(k => k == "transition"));
            Assert.Equal(2, kinds.Count(k => k == "transmittal"));
            Assert.Single(kinds, k => k == "review");
            Assert.Contains("published", ((JArray)row["present_in"]).Select(s => (string)s));

            JObject rejectedRow = ((JArray)r["containers"]).OfType<JObject>().Single(c => (string)c["container"] == Name("0181"));
            Assert.Equal("rejected", (string)rejectedRow["latest_review"]["outcome"]);
            Assert.Equal("shared", (string)rejectedRow["last_transition_to"]);   // the rejection moved nothing

            // Filters: container, state, date window; pagination.
            JObject onlyB = Run(new JObject { ["operation"] = "register", ["root"] = _root, ["container"] = Name("0181") });
            Assert.Equal(1, (int)onlyB["total_containers"]);
            JObject reachedPublished = Run(new JObject { ["operation"] = "register", ["root"] = _root, ["state"] = "published" });
            Assert.Equal(new[] { Name("0180") }, ((JArray)reachedPublished["containers"]).Select(c => (string)c["container"]).ToArray());
            JObject window = Run(new JObject { ["operation"] = "register", ["root"] = _root, ["since"] = "2026-09-11", ["until"] = "2026-09-13" });
            JObject windowRow = ((JArray)window["containers"]).OfType<JObject>().Single(c => (string)c["container"] == Name("0181"));
            Assert.Equal(new[] { "review" }, ((JArray)windowRow["history"]).Select(e => (string)e["kind"]).ToArray());
            JObject page = Run(new JObject { ["operation"] = "register", ["root"] = _root, ["limit"] = 1 });
            Assert.Single((JArray)page["containers"]);
            Assert.True((bool)page["truncated"]);
            Assert.Equal(1, (int)page["next_offset"]);
        }

        [Fact]
        public void The_register_names_every_incoherence_between_the_records()
        {
            string a = Shared("0190");
            string t1 = (string)Run(TransmittalArgs("shared", false, "S3", a))["id"];

            // 1. The issued file changes afterwards.
            File.AppendAllText(a, " - edited after issue");
            // 2. A review of a container nobody issued, and of a transmittal that does not exist.
            string h = Path.Combine(_root, ".horizun");
            File.AppendAllText(Path.Combine(h, "reviews.jsonl"),
                new JObject { ["schema"] = "horizun.review/v1", ["review_id"] = "x1", ["utc"] = "2026-09-01T00:00:00.000Z", ["reviewed_on"] = "2026-09-01",
                              ["transmittal_id"] = null, ["containers"] = new JArray { new JObject { ["name"] = Name("0666") } }, ["outcome"] = "accepted" }
                    .ToString(Newtonsoft.Json.Formatting.None) + "\n" +
                new JObject { ["schema"] = "horizun.review/v1", ["review_id"] = "x2", ["utc"] = "2026-09-01T00:00:00.000Z",
                              ["transmittal_id"] = "HZ01-TR-0077", ["containers"] = new JArray { new JObject { ["name"] = Name("0190") } }, ["outcome"] = "rejected" }
                    .ToString(Newtonsoft.Json.Formatting.None) + "\n");
            // 3. An old log line: a publication without approved_by, and a line that is not JSON.
            File.AppendAllText(Path.Combine(h, "cde-transitions.jsonl"),
                new JObject { ["transition_id"] = "old1", ["utc"] = "2025-01-01T00:00:00.000Z", ["from_state"] = "shared", ["to_state"] = "published",
                              ["name"] = Name("0191"), ["status"] = "A1", ["revision"] = "C01", ["approved_by"] = null }
                    .ToString(Newtonsoft.Json.Formatting.None) + "\n{ truncated line\n");

            JObject r = Run(new JObject { ["operation"] = "register", ["root"] = _root, ["states"] = States() });
            JObject counts = (JObject)r["incoherence_counts"];
            Assert.Equal(1, (int)counts["transmittal_hash_changed"]);
            Assert.Equal(1, (int)counts["review_unknown_container"]);
            Assert.Equal(1, (int)counts["review_unknown_transmittal"]);
            Assert.Equal(1, (int)counts["publication_without_approval"]);
            Assert.Equal(1, (int)counts["log_line_unreadable"]);
            JObject changed = ((JArray)r["incoherences"]).OfType<JObject>().First();
            Assert.Equal("transmittal_hash_changed", (string)changed["kind"]);   // most consequential first
            Assert.Equal(t1, (string)changed["transmittal_id"]);
            Assert.NotEqual((string)changed["issued_sha256"], (string)changed["current_sha256"]);
            Assert.Equal(1, (int)r["sources"]["transitions"]["unreadable"]);

            // A deleted issued file is a finding of its own; a missing register is not an empty one.
            File.Delete(a);
            JObject r2 = Run(new JObject { ["operation"] = "register", ["root"] = _root });
            Assert.Equal(1, (int)r2["incoherence_counts"]["transmittal_file_missing"]);
            string empty = Path.Combine(_dir, "empty-cde");
            Directory.CreateDirectory(empty);
            JObject none = Run(new JObject { ["operation"] = "register", ["root"] = empty });
            Assert.False((bool)none["sources"]["transitions"]["exists"]);
            Assert.False((bool)none["sources"]["transmittals"]["exists"]);
            Assert.Equal(0, (int)none["total_containers"]);
        }

        // ---- the contract ----------------------------------------------------------------

        [Fact]
        public void The_contract_declares_the_three_operations_and_their_arguments()
        {
            CommandContract c = Contract.Find("horizun_information_container");
            var ops = ((JArray)c.InputSchema["properties"]["operation"]["enum"]).Select(t => (string)t).ToList();
            foreach (string op in new[] { "name", "stamp", "verify", "inspect", "transition", "transmittal", "record_review", "register" })
                Assert.Contains(op, ops);
            foreach (string key in new[] { "project", "state", "file_paths", "sender", "recipients", "purpose", "transmittal_id",
                                           "container", "outcome", "comments", "reviewed_by", "reviewer_organization", "reviewed_on",
                                           "since", "until" })
                Assert.NotNull(c.InputSchema["properties"][key]);
            Assert.Equal(new[] { "accepted", "accepted_with_comments", "rejected" },
                         ((JArray)c.InputSchema["properties"]["outcome"]["enum"]).Select(t => (string)t).ToArray());
            Assert.Equal(InformationContainerTool.ReviewOutcomes, ((JArray)c.InputSchema["properties"]["outcome"]["enum"]).Select(t => (string)t).ToArray());
            Assert.Equal(ToolEffect.ExternalSideEffectOnRequest, c.Effect);
            Assert.Contains("transmittal", c.Description);
            Assert.Contains("register", c.Description);
        }
    }
}
