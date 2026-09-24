// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// horizun_information_container and the Revit-free core it shares with
// horizun_export. Every test runs the real handler against real disposable files
// under a settings root of its own - never the machine's - so the permission rung is
// the test's choice and not the developer's settings.json.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class InformationContainerTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _settingsRoot;
        private readonly string _savedRoot;

        public InformationContainerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-ic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settingsRoot = Path.Combine(Path.GetTempPath(), "hz-ic-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_settingsRoot);
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _settingsRoot);
            Profile("full_write");
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

        private static JObject Container(string number = "0001", string status = "S2", string revision = "P01") => new JObject
        {
            ["fields"] = new JObject
            {
                ["project"] = "HZ01", ["originator"] = "HRZ", ["volume"] = "ZZ", ["level"] = "XX",
                ["type"] = "M3", ["role"] = "A", ["number"] = number
            },
            ["status"] = status,
            ["revision"] = revision,
            ["title"] = "Test container"
        };

        private string WriteFile(string folder, string name, string content)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        private JObject StampReal(string file, JObject container)
            => Run(new JObject { ["operation"] = "stamp", ["file_path"] = file, ["information_container"] = container, ["dry_run"] = false });

        // ---- name --------------------------------------------------------------------

        [Fact]
        public void A_valid_iso_container_composes_its_name_with_the_defaults_declared()
        {
            JObject r = Run(new JObject { ["operation"] = "name", ["information_container"] = Container() });
            Assert.True((bool)r["valid"]);
            Assert.Equal("HZ01-HRZ-ZZ-XX-M3-A-0001", (string)r["name"]);
            Assert.Equal("preliminary", (string)r["revision_kind"]);
            Assert.Equal("shared", (string)r["status_state"]);
            Assert.Equal(InformationContainer.SourceDefault, (string)r["naming"]["status_codes_source"]);
            Assert.Equal(InformationContainer.SourceDefault, (string)r["naming"]["field_order_source"]);
        }

        [Fact]
        public void An_invalid_container_names_every_problem_not_just_the_first()
        {
            JObject c = Container(number: "12", status: "S9", revision: "R1");
            c["fields"]["project"] = "hz01";
            c["fields"]["volume"] = "Z-Z";
            JObject r = Run(new JObject { ["operation"] = "name", ["information_container"] = c });
            Assert.False((bool)r["valid"]);
            var codes = ((JArray)r["problems"]).Select(p => (string)p["field"] + ":" + (string)p["code"]).ToList();
            Assert.Contains("project:pattern_mismatch", codes);
            Assert.Contains("number:pattern_mismatch", codes);
            Assert.Contains("volume:contains_separator", codes);
            Assert.Contains("status:status_unknown", codes);
            Assert.Contains("revision:revision_pattern_mismatch", codes);
        }

        [Fact]
        public void Custom_rules_replace_the_defaults_and_a_misspelled_key_is_refused()
        {
            var c = new JObject
            {
                ["fields"] = new JObject { ["proj"] = "HZ01", ["disc"] = "ARQ", ["num"] = "001" },
                ["field_order"] = new JArray("proj", "disc", "num"),
                ["separator"] = "_",
                ["field_patterns"] = new JObject { ["disc"] = "[A-Z]{3}", ["num"] = "[0-9]{3}" },
                ["status_codes"] = new JObject { ["WIP"] = "work", ["OK"] = "approved" },
                ["revision_patterns"] = new JObject { ["letter"] = "[A-Z]" },
                ["status"] = "OK", ["revision"] = "B"
            };
            JObject r = Run(new JObject { ["operation"] = "name", ["information_container"] = c });
            Assert.True((bool)r["valid"], r.ToString());
            Assert.Equal("HZ01_ARQ_001", (string)r["name"]);
            Assert.Equal("letter", (string)r["revision_kind"]);

            c["revison"] = "C";
            Assert.Throws<ToolRefusal>(() => Run(new JObject { ["operation"] = "name", ["information_container"] = c }));

            // Custom fields and no order: the order of a name is never guessed.
            var unordered = new JObject { ["fields"] = new JObject { ["a"] = "X1", ["b"] = "Y2" } };
            Assert.Throws<ToolRefusal>(() => Run(new JObject { ["operation"] = "name", ["information_container"] = unordered }));
        }

        [Fact]
        public void Revisions_are_ordered_only_within_one_kind()
        {
            Assert.True(InformationContainer.CompareRevisions("P02", "P03") < 0);
            Assert.True(InformationContainer.CompareRevisions("P01.02", "P01.01") > 0);
            Assert.Equal(0, InformationContainer.CompareRevisions("C01", "C01"));
            Assert.Null(InformationContainer.CompareRevisions("P05", "C01"));
            Assert.Null(InformationContainer.CompareRevisions("P0x", "P01"));
        }

        // ---- stamp / verify ------------------------------------------------------------

        [Fact]
        public void Stamp_rehearses_by_default_and_then_round_trips_a_verified_sidecar()
        {
            string file = WriteFile(_dir, "HZ01-HRZ-ZZ-XX-M3-A-0001.pdf", "pdf bytes");
            JObject dry = Run(new JObject { ["operation"] = "stamp", ["file_path"] = file, ["information_container"] = Container() });
            Assert.True((bool)dry["dry_run"]);
            Assert.False((bool)dry["written"]);
            Assert.False(File.Exists(InformationContainer.SidecarPath(file)));

            JObject real = StampReal(file, Container());
            Assert.True((bool)real["written"]);
            Assert.True((bool)real["verified"]);
            Assert.True(File.Exists(InformationContainer.SidecarPath(file)));

            string problem;
            JObject sidecar = InformationContainer.ReadSidecar(InformationContainer.SidecarPath(file), out problem);
            Assert.Null(problem);
            Assert.Equal(InformationContainer.SidecarSchema, (string)sidecar["schema"]);
            Assert.Equal("HZ01-HRZ-ZZ-XX-M3-A-0001", (string)sidecar["name"]);
            Assert.Equal("S2", (string)sidecar["status"]);
            Assert.Equal("P01", (string)sidecar["revision"]);
            Assert.Equal(9L, (long)sidecar["bytes"]);
            long bytes;
            Assert.Equal(InformationContainer.Sha256File(file, out bytes), (string)sidecar["sha256"]);
            // created_utc stays a STRING: a date-promoting parse would compare unequal.
            Assert.Equal(JTokenType.String, sidecar["created_utc"].Type);

            JObject verify = Run(new JObject { ["operation"] = "verify", ["file_path"] = file });
            Assert.Equal("match", (string)verify["verdict"]);
            Assert.True((bool)verify["matches"]);

            // A repeat is recognised, not duplicated and not refused.
            JObject again = StampReal(file, Container());
            Assert.True((bool)again["already_stamped"]);
            Assert.False((bool)again["written"]);
        }

        [Fact]
        public void Verify_detects_a_file_modified_after_sealing()
        {
            string file = WriteFile(_dir, "HZ01-HRZ-ZZ-XX-M3-A-0002.ifc", "ISO-10303-21;");
            StampReal(file, Container("0002"));
            File.AppendAllText(file, "edited later");
            JObject verify = Run(new JObject { ["operation"] = "verify", ["file_path"] = file });
            Assert.Equal("modified", (string)verify["verdict"]);
            Assert.False((bool)verify["matches"]);
            Assert.Contains(((JArray)verify["mismatches"]), m => (string)m["check"] == "sha256");
        }

        [Fact]
        public void Verify_detects_a_renamed_file_and_a_missing_sidecar()
        {
            string file = WriteFile(_dir, "HZ01-HRZ-ZZ-XX-M3-A-0003.pdf", "x");
            StampReal(file, Container("0003"));
            string renamed = Path.Combine(_dir, "HZ01-HRZ-ZZ-XX-M3-A-0004.pdf");
            File.Move(file, renamed);
            File.Move(InformationContainer.SidecarPath(file), InformationContainer.SidecarPath(renamed));
            Assert.Equal("renamed", (string)Run(new JObject { ["operation"] = "verify", ["file_path"] = renamed })["verdict"]);

            string bare = WriteFile(_dir, "HZ01-HRZ-ZZ-XX-M3-A-0005.pdf", "y");
            Assert.Equal("missing_sidecar", (string)Run(new JObject { ["operation"] = "verify", ["file_path"] = bare })["verdict"]);
        }

        [Fact]
        public void Stamp_refuses_a_file_whose_name_is_not_the_container_and_never_overwrites_a_sidecar()
        {
            string wrong = WriteFile(_dir, "model-final.pdf", "z");
            Assert.Throws<ToolRefusal>(() => StampReal(wrong, Container()));
            Assert.False(File.Exists(InformationContainer.SidecarPath(wrong)));

            string file = WriteFile(_dir, "HZ01-HRZ-ZZ-XX-M3-A-0006.pdf", "a");
            StampReal(file, Container("0006"));
            string before = File.ReadAllText(InformationContainer.SidecarPath(file));
            Assert.Throws<ToolRefusal>(() => StampReal(file, Container("0006", "S3", "P02")));
            Assert.Equal(before, File.ReadAllText(InformationContainer.SidecarPath(file)));
        }

        [Theory]
        [InlineData("read_only")]
        [InlineData("safe_write")]
        public void Writing_a_sidecar_needs_full_write_but_the_rehearsal_does_not(string profile)
        {
            Profile(profile);
            string file = WriteFile(_dir, "HZ01-HRZ-ZZ-XX-M3-A-0007.pdf", "b");
            JObject dry = Run(new JObject { ["operation"] = "stamp", ["file_path"] = file, ["information_container"] = Container("0007") });
            Assert.False((bool)dry["written"]);
            ToolRefusal refusal = Assert.Throws<ToolRefusal>(() => StampReal(file, Container("0007")));
            Assert.Contains("permission_profile=" + profile, refusal.Message);
            Assert.False(File.Exists(InformationContainer.SidecarPath(file)));
        }

        // ---- inspect -------------------------------------------------------------------

        private sealed class Tree
        {
            public string Root, Wip, Shared, Published, Archived;
        }

        private Tree MakeTree()
        {
            string root = Path.Combine(_dir, "cde");
            var t = new Tree
            {
                Root = root,
                Wip = Path.Combine(root, "01_WIP"), Shared = Path.Combine(root, "02_SHARED"),
                Published = Path.Combine(root, "03_PUBLISHED"), Archived = Path.Combine(root, "04_ARCHIVE")
            };
            foreach (string f in new[] { t.Wip, t.Shared, t.Published, t.Archived }) Directory.CreateDirectory(f);
            return t;
        }

        private static JObject States() => new JObject
        {
            ["wip"] = "01_WIP", ["shared"] = "02_SHARED", ["published"] = "03_PUBLISHED", ["archived"] = "04_ARCHIVE"
        };

        [Fact]
        public void Inspect_reports_each_kind_of_finding_and_crosses_the_midp()
        {
            Tree t = MakeTree();
            // 0010: shared P03 and published P02 (published below shared).
            StampReal(WriteFile(t.Shared, "HZ01-HRZ-ZZ-XX-M3-A-0010.pdf", "shared p03"), Container("0010", "S2", "P03"));
            StampReal(WriteFile(t.Published, "HZ01-HRZ-ZZ-XX-M3-A-0010.pdf", "published p02"), Container("0010", "A1", "P02"));
            // 0011: one revision, two contents.
            StampReal(WriteFile(t.Wip, "HZ01-HRZ-ZZ-XX-M3-A-0011.rvt", "content one"), Container("0011", "S0", "P01"));
            StampReal(WriteFile(Path.Combine(t.Shared, "ARQ"), "HZ01-HRZ-ZZ-XX-M3-A-0011.rvt", "content two"), Container("0011", "S1", "P01"));
            // 0012: modified after sealing.
            string modified = WriteFile(t.Shared, "HZ01-HRZ-ZZ-XX-M3-A-0012.pdf", "sealed");
            StampReal(modified, Container("0012", "S3", "P01"));
            File.AppendAllText(modified, "!");
            // No sidecar; non-compliant name; orphan sidecar.
            WriteFile(t.Wip, "HZ01-HRZ-ZZ-XX-M3-A-0013.pdf", "unsealed");
            WriteFile(t.Wip, "plano final v2.pdf", "junk");
            string orphan = WriteFile(t.Shared, "HZ01-HRZ-ZZ-XX-M3-A-0014.pdf", "gone");
            StampReal(orphan, Container("0014"));
            File.Delete(orphan);
            // Temp files and our own log are ignored.
            WriteFile(t.Wip, "~$lock.pdf", "lock");
            WriteFile(Path.Combine(t.Root, "03_PUBLISHED", ".horizun"), "note.txt", "ignored");

            var deliverables = new JArray
            {
                new JObject { ["container"] = "HZ01-HRZ-ZZ-XX-M3-A-0010", ["due"] = "2026-01-01", ["required_status"] = "A1", ["format"] = "pdf" },
                new JObject { ["container"] = "HZ01-HRZ-ZZ-XX-M3-A-0099", ["due"] = "2027-12-31", ["required_status"] = "S2" },
                new JObject { ["container"] = "HZ01-HRZ-ZZ-XX-M3-A-0098", ["due"] = "2026-01-01", ["required_status"] = "S2" },
                new JObject { ["container"] = "HZ01-HRZ-ZZ-XX-M3-A-0011", ["due"] = "2027-12-31", ["required_status"] = "S4" }
            };

            JObject r = InformationContainerTool.Inspect(new JObject
            {
                ["operation"] = "inspect", ["root"] = t.Root, ["states"] = States(), ["deliverables"] = deliverables,
                ["as_of"] = "2026-09-24"
            }, CancellationToken.None, DateTime.UtcNow);

            JObject counts = (JObject)r["finding_counts"];
            Assert.Equal(1, (int)counts["revision_incoherent"]);
            Assert.Equal(1, (int)counts["same_revision_different_content"]);
            Assert.Equal(1, (int)counts["hash_mismatch"]);
            Assert.Equal(2, (int)counts["missing_sidecar"]);       // 0013 and the junk-named file
            Assert.Equal(1, (int)counts["name_noncompliant"]);
            Assert.Equal(1, (int)counts["orphan_sidecar"]);
            Assert.Equal(1, (int)counts["deliverable_missing"]);   // 0099, not yet due
            Assert.Equal(1, (int)counts["deliverable_overdue"]);   // 0098, due and absent
            Assert.Equal(1, (int)counts["deliverable_insufficient_state"]); // 0011 reached S1, needs S4
            Assert.Null(counts["deliverable_not_assessable"]);
            Assert.True((bool)r["coverage_complete"]);

            var verdicts = ((JArray)r["deliverables"]).ToDictionary(d => (string)d["container"], d => (string)d["verdict"]);
            Assert.Equal("satisfied", verdicts["HZ01-HRZ-ZZ-XX-M3-A-0010"]);   // published A1 exists
            Assert.Equal("missing", verdicts["HZ01-HRZ-ZZ-XX-M3-A-0099"]);
            Assert.Equal("overdue", verdicts["HZ01-HRZ-ZZ-XX-M3-A-0098"]);
            Assert.Equal("insufficient_state", verdicts["HZ01-HRZ-ZZ-XX-M3-A-0011"]);

            // Ignored entries never become findings.
            Assert.DoesNotContain(((JArray)r["findings"]), f => ((string)f["path"] ?? "").Contains("~$"));
            Assert.DoesNotContain(((JArray)r["findings"]), f => ((string)f["path"] ?? "").Contains(".horizun"));
        }

        [Fact]
        public void Inspect_paginates_with_exact_totals_and_names_an_undeclared_state()
        {
            Tree t = MakeTree();
            for (int i = 0; i < 5; i++) WriteFile(t.Wip, "bad name " + i + ".pdf", "x");
            var states = new JObject { ["wip"] = "01_WIP", ["shared"] = "02_SHARED" };
            JObject first = InformationContainerTool.Inspect(new JObject
            { ["operation"] = "inspect", ["root"] = t.Root, ["states"] = states, ["limit"] = 4 }, CancellationToken.None, DateTime.UtcNow);
            Assert.Equal(10, (int)first["total_findings"]);   // 5 non-compliant + 5 without sidecar
            Assert.Equal(4, ((JArray)first["findings"]).Count);
            Assert.True((bool)first["truncated"]);
            Assert.Equal(4, (int)first["next_offset"]);
            Assert.False((bool)first["coverage_complete"]);   // published/archived were not declared
            JObject last = InformationContainerTool.Inspect(new JObject
            { ["operation"] = "inspect", ["root"] = t.Root, ["states"] = states, ["limit"] = 4, ["offset"] = 8 }, CancellationToken.None, DateTime.UtcNow);
            Assert.Equal(2, ((JArray)last["findings"]).Count);
            Assert.False((bool)last["truncated"]);
        }

        [Fact]
        public void Inspect_reads_states_naming_and_deliverables_from_a_project_context()
        {
            Tree t = MakeTree();
            StampReal(WriteFile(t.Shared, "HZ01-HRZ-ZZ-XX-M3-A-0020.pdf", "c"), Container("0020", "S3", "P01"));
            var ctx = new JObject
            {
                ["schema_version"] = 1,
                ["project"] = new JObject { ["code"] = "HZ01" },
                ["cde"] = new JObject { ["platform"] = "local", ["root"] = t.Root, ["states"] = States() },
                ["naming"] = new JObject
                {
                    ["separator"] = "-",
                    ["fields"] = new JArray(InformationContainer.DefaultFieldOrder.Select(f => new JObject { ["name"] = f }))
                },
                ["deliverables"] = new JArray { new JObject { ["container"] = "HZ01-HRZ-ZZ-XX-M3-A-0020", ["due"] = "2030-01-01", ["required_status"] = "S3" } }
            };
            string ctxPath = Path.Combine(_dir, "project-context.json");
            File.WriteAllText(ctxPath, ctx.ToString());
            JObject r = InformationContainerTool.Inspect(new JObject { ["operation"] = "inspect", ["project_context_path"] = ctxPath },
                                                         CancellationToken.None, DateTime.UtcNow);
            Assert.Equal(0, (int)r["total_findings"]);
            Assert.Equal("satisfied", (string)r["deliverables"][0]["verdict"]);
        }

        // ---- transition ----------------------------------------------------------------

        private JObject TransitionArgs(Tree t, string file, string from, string to, bool dryRun, string status = null,
                                       string revision = null, string approvedBy = null)
        {
            var a = new JObject
            {
                ["operation"] = "transition", ["root"] = t.Root, ["states"] = States(), ["file_path"] = file,
                ["from_state"] = from, ["to_state"] = to, ["dry_run"] = dryRun
            };
            if (status != null) a["status"] = status;
            if (revision != null) a["revision"] = revision;
            if (approvedBy != null) a["approved_by"] = approvedBy;
            return a;
        }

        [Fact]
        public void Transition_rehearses_then_copies_stamps_and_logs_without_touching_the_source()
        {
            Tree t = MakeTree();
            string src = WriteFile(Path.Combine(t.Wip, "ARQ"), "HZ01-HRZ-ZZ-XX-M3-A-0030.rvt", "model bytes");
            StampReal(src, Container("0030", "S0", "P01"));
            string srcSidecarBefore = File.ReadAllText(InformationContainer.SidecarPath(src));

            JObject dry = Run(TransitionArgs(t, src, "wip", "shared", true, "S2"));
            Assert.False((bool)dry["written"]);
            string dest = (string)dry["destination"];
            Assert.Equal(Path.Combine(t.Shared, "ARQ", "HZ01-HRZ-ZZ-XX-M3-A-0030.rvt"), dest);
            Assert.False(File.Exists(dest));
            Assert.False(Directory.Exists(Path.Combine(t.Root, ".horizun")));

            JObject real = Run(TransitionArgs(t, src, "wip", "shared", false, "S2"));
            Assert.True((bool)real["written"]);
            Assert.True((bool)real["verified"], real.ToString());
            Assert.Equal("match", (string)Run(new JObject { ["operation"] = "verify", ["file_path"] = dest })["verdict"]);
            string problem;
            JObject sc = InformationContainer.ReadSidecar(InformationContainer.SidecarPath(dest), out problem);
            Assert.Equal("shared", (string)sc["state"]);
            Assert.Equal("S2", (string)sc["status"]);
            Assert.Equal("wip", (string)sc["transitioned_from"]["state"]);
            Assert.Equal((string)real["source_sha256"], (string)sc["transitioned_from"]["sha256"]);

            // The source is still there, byte for byte, with its own sidecar.
            Assert.Equal("model bytes", File.ReadAllText(src));
            Assert.Equal(srcSidecarBefore, File.ReadAllText(InformationContainer.SidecarPath(src)));

            string log = Path.Combine(t.Root, ".horizun", "cde-transitions.jsonl");
            string[] lines = File.ReadAllLines(log).Where(l => l.Length > 0).ToArray();
            Assert.Single(lines);
            Assert.Equal((string)real["transition_id"], (string)JObject.Parse(lines[0])["transition_id"]);

            // The same transition again replays; nothing is appended.
            JObject replay = Run(TransitionArgs(t, src, "wip", "shared", false, "S2"));
            Assert.True((bool)replay["already_transitioned"]);
            Assert.Single(File.ReadAllLines(log), l => l.Length > 0);

            // A DIFFERENT transition onto the same destination is refused, never overwritten.
            string destBefore = File.ReadAllText(InformationContainer.SidecarPath(dest));
            Assert.Throws<ToolRefusal>(() => Run(TransitionArgs(t, src, "wip", "shared", false, "S3")));
            Assert.Equal(destBefore, File.ReadAllText(InformationContainer.SidecarPath(dest)));
        }

        [Fact]
        public void Publishing_requires_approved_by_and_records_it()
        {
            Tree t = MakeTree();
            string src = WriteFile(t.Shared, "HZ01-HRZ-ZZ-XX-M3-A-0031.pdf", "for approval");
            StampReal(src, Container("0031", "S4", "P02"));

            Assert.Throws<ToolRefusal>(() => Run(TransitionArgs(t, src, "shared", "published", true, "A1", "C01")));

            JObject r = Run(TransitionArgs(t, src, "shared", "published", false, "A1", "C01", "Lead reviewer"));
            Assert.True((bool)r["verified"], r.ToString());
            string problem;
            JObject sc = InformationContainer.ReadSidecar(InformationContainer.SidecarPath((string)r["destination"]), out problem);
            Assert.Equal("Lead reviewer", (string)sc["approved_by"]);
            Assert.Equal("C01", (string)sc["revision"]);
            string line = File.ReadAllLines(Path.Combine(t.Root, ".horizun", "cde-transitions.jsonl")).Single(l => l.Length > 0);
            Assert.Equal("Lead reviewer", (string)JObject.Parse(line)["approved_by"]);
        }

        [Fact]
        public void Transition_refuses_what_it_cannot_carry_forward_honestly()
        {
            Tree t = MakeTree();
            string unsealed = WriteFile(t.Wip, "HZ01-HRZ-ZZ-XX-M3-A-0032.pdf", "u");
            Assert.Throws<ToolRefusal>(() => Run(TransitionArgs(t, unsealed, "wip", "shared", true, "S2")));   // stamp first

            string sealedThenEdited = WriteFile(t.Wip, "HZ01-HRZ-ZZ-XX-M3-A-0033.pdf", "s");
            StampReal(sealedThenEdited, Container("0033", "S0", "P01"));
            File.AppendAllText(sealedThenEdited, "later");
            Assert.Throws<ToolRefusal>(() => Run(TransitionArgs(t, sealedThenEdited, "wip", "shared", true, "S2")));

            string ok = WriteFile(t.Wip, "HZ01-HRZ-ZZ-XX-M3-A-0034.pdf", "o");
            StampReal(ok, Container("0034", "S0", "P01"));
            Assert.Throws<ToolRefusal>(() => Run(TransitionArgs(t, ok, "wip", "shared", true, "A1")));        // A1 is not shared
            Assert.Throws<ToolRefusal>(() => Run(TransitionArgs(t, ok, "wip", "published", true, "A1")));     // skips a state
            Assert.Throws<ToolRefusal>(() => Run(TransitionArgs(t, ok, "shared", "published", true, "A1", null, "x"))); // not in shared

            Profile("safe_write");
            Assert.False((bool)Run(TransitionArgs(t, ok, "wip", "shared", true, "S2"))["written"]);
            Assert.Throws<ToolRefusal>(() => Run(TransitionArgs(t, ok, "wip", "shared", false, "S2")));
            Assert.Empty(Directory.GetFiles(t.Shared, "*", SearchOption.AllDirectories));
        }

        // ---- the contract, and export without the argument ---------------------------------

        [Fact]
        public void The_tool_is_host_resident_and_reaches_outside_only_on_request()
        {
            CommandContract c = Contract.Find("horizun_information_container");
            Assert.NotNull(c);
            Assert.Null(c.Command);
            Assert.Equal(ToolEffect.ExternalSideEffectOnRequest, c.Effect);
            Assert.False(c.Destructive);
            Assert.NotNull(Tools.Find("horizun_information_container")?.Host);
        }

        [Fact]
        public void Export_keeps_its_previous_contract_when_no_container_is_passed()
        {
            CommandContract export = Contract.Find("horizun_export");
            Assert.Equal(new[] { "target_document", "format", "output_path" },
                         ((JArray)export.InputSchema["required"]).Select(t => (string)t).ToArray());
            Assert.NotNull(export.InputSchema["properties"]["information_container"]);

            // The command only enters the container branch when the argument is present, and the
            // effective output path is the requested one otherwise.
            string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Horizun.Revit", "Commands", "ExportCommand.cs"));
            Assert.Contains("if (request[\"information_container\"] != null && request[\"information_container\"].Type != JTokenType.Null)", source);
            Assert.Contains("if (container != null)", source);
        }

        [Fact]
        public void A_container_names_the_export_file_from_the_requested_folder_and_extension()
        {
            ContainerSpec spec = InformationContainer.ParseSpec(Container("0040", "S2", "P01"));
            ContainerValidation v = InformationContainer.Validate(spec, true);
            string requested = Path.Combine(_dir, "whatever.ifc");
            Assert.Equal(Path.Combine(_dir, "HZ01-HRZ-ZZ-XX-M3-A-0040.ifc"), InformationContainer.ContainerOutputPath(requested, v));

            JObject withRevision = Container("0040", "S2", "P01");
            withRevision["file_name"] = "name_status_revision";
            ContainerValidation v2 = InformationContainer.Validate(InformationContainer.ParseSpec(withRevision), true);
            Assert.Equal("HZ01-HRZ-ZZ-XX-M3-A-0040-S2-P01", v2.FileStem);

            // A stamped export needs status and revision.
            JObject bare = Container();
            bare.Remove("status");
            Assert.False(InformationContainer.Validate(InformationContainer.ParseSpec(bare), true).Valid);
        }

        private static string RepositoryRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}
