// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// 3.0.0 interoperability: model identity (decision D0), the project registry
// (project-integrations.json) and the horizun.exchange/0 manifest - payload
// first, manifest last, sha256 re-measured, the honesty vocabulary capped by what
// a producer can claim.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ModelIdentityTests
    {
        private const string Creation = "0b5c2f8e-1111-4c3a-9d2b-7e1f00000001";
        private const string Template = "4a1b0000-aaaa-bbbb-cccc-000000000000-0000aaaa"; // a ProjectInformation UniqueId shape

        private static JObject Ev(string creation = null, string central = null, string cloud = null, string unique = null,
                                  string pi = Template, string version = "2026")
            => ModelIdentity.Evidence(creation, central, cloud, null, unique, pi, version, "A.rvt", @"C:\p\A.rvt");

        private static JObject Registered(string modelRef, JObject evidence) =>
            new JObject { ["model_ref"] = modelRef, ["name"] = modelRef, ["evidence"] = evidence };

        [Fact]
        public void The_binding_key_follows_the_D0_order()
        {
            Assert.Equal("creation_guid", ModelIdentity.BindingKey(Ev(Creation, Guid.NewGuid().ToString(), Guid.NewGuid().ToString())));
            Assert.Equal("central_guid", ModelIdentity.BindingKey(Ev(null, Guid.NewGuid().ToString(), Guid.NewGuid().ToString())));
            Assert.Equal("cloud_model_guid", ModelIdentity.BindingKey(Ev(null, null, Guid.NewGuid().ToString(), Guid.NewGuid().ToString())));
            Assert.Equal("unique_document_guid", ModelIdentity.BindingKey(Ev(null, null, null, Guid.NewGuid().ToString())));
            Assert.Null(ModelIdentity.BindingKey(Ev()));
        }

        [Fact]
        public void An_empty_guid_is_not_applicable_never_a_value_two_models_could_share()
        {
            JObject e = Ev(null, Guid.Empty.ToString());
            Assert.Equal(JTokenType.Null, e["central_guid"].Type);
            Assert.Null(ModelIdentity.BindingKey(e));
        }

        [Fact]
        public void Project_information_alone_matches_nothing_even_when_equal()
        {
            JObject m = ModelIdentity.Match(new[] { Registered("mdl-aaaaaaaaaaaa", Ev(Creation)) }, Ev());
            Assert.Equal(ModelIdentity.NoStrongEvidence, (string)m["state"]);
            Assert.Null((string)m["model_ref"]);
            Assert.False((bool)m["identity_proven"]);
        }

        [Fact]
        public void Two_projects_from_one_template_share_project_information_and_do_not_match()
        {
            var reg = new[] { Registered("mdl-aaaaaaaaaaaa", Ev(Creation)) };
            JObject m = ModelIdentity.Match(reg, Ev(Guid.NewGuid().ToString()));
            Assert.Equal(ModelIdentity.NoMatch, (string)m["state"]);
        }

        [Fact]
        public void A_creation_guid_match_on_2024_or_later_is_proven()
        {
            JObject m = ModelIdentity.Match(new[] { Registered("mdl-aaaaaaaaaaaa", Ev(Creation)) }, Ev(Creation.ToUpperInvariant()));
            Assert.Equal(ModelIdentity.Matched, (string)m["state"]);
            Assert.Equal("mdl-aaaaaaaaaaaa", (string)m["model_ref"]);
            Assert.Equal("creation_guid", (string)m["matched_by"]);
            Assert.True((bool)m["identity_proven"]);
        }

        [Fact]
        public void A_reading_on_Revit_2023_is_never_proven_even_with_a_cloud_guid()
        {
            string cloud = Guid.NewGuid().ToString();
            JObject ev = Ev(null, null, cloud, version: "2023");
            Assert.False((bool)ev["identity_proven"]);
            Assert.Contains("2023", (string)ev["identity_note"]);
            JObject m = ModelIdentity.Match(new[] { Registered("mdl-bbbbbbbbbbbb", Ev(null, null, cloud, version: "2023")) }, ev);
            Assert.Equal(ModelIdentity.Matched, (string)m["state"]);
            Assert.False((bool)m["identity_proven"]);
            Assert.Contains(((JArray)m["warnings"]).Select(w => (string)w["code"]), c => c == "identity_unprovable_2023");
        }

        [Fact]
        public void The_decisive_key_rules_out_a_model_a_weaker_key_agrees_with_and_says_so()
        {
            string unique = Guid.NewGuid().ToString();
            var reg = new[] { Registered("mdl-cccccccccccc", Ev(Creation, null, null, unique)) };
            JObject m = ModelIdentity.Match(reg, Ev(Guid.NewGuid().ToString(), null, null, unique));
            Assert.Equal(ModelIdentity.NoMatch, (string)m["state"]);
            Assert.Contains(((JArray)m["warnings"]).Select(w => (string)w["code"]), c => c == "evidence_conflict");
        }

        [Fact]
        public void Two_registered_models_with_the_same_evidence_are_ambiguous_and_match_nothing()
        {
            var reg = new[] { Registered("mdl-dddddddddddd", Ev(Creation)), Registered("mdl-eeeeeeeeeeee", Ev(Creation)) };
            JObject m = ModelIdentity.Match(reg, Ev(Creation));
            Assert.Equal(ModelIdentity.Ambiguous, (string)m["state"]);
            Assert.Null((string)m["model_ref"]);
        }

        [Fact]
        public void Normalize_refuses_unknown_keys_and_non_guids_and_recomputes_what_a_caller_cannot_declare()
        {
            List<string> problems;
            Assert.Null(ModelIdentity.Normalize(new JObject { ["creation_guid"] = "not-a-guid" }, out problems));
            Assert.Single(problems);
            Assert.Null(ModelIdentity.Normalize(new JObject { ["owner"] = "x" }, out problems));
            JObject n = ModelIdentity.Normalize(new JObject { ["project_info_unique_id"] = "pi", ["identity_proven"] = true }, out problems);
            Assert.False((bool)n["identity_proven"]);
        }

        [Fact]
        public void The_registry_refuses_weak_only_models_duplicate_refs_and_shared_bindings()
        {
            var doc = ProjectIntegrations.Empty("P01");
            doc["models"] = new JArray(Registered("mdl-111111111111", Ev(Creation)), Registered("mdl-111111111111", Ev(Guid.NewGuid().ToString())),
                                       Registered("mdl-222222222222", Ev()), Registered("mdl-333333333333", Ev(Creation)));
            List<string> errors = ProjectIntegrations.Validate(doc);
            Assert.Contains(errors, e => e.Contains("appears twice"));
            Assert.Contains(errors, e => e.Contains("no strong key"));
            Assert.Contains(errors, e => e.Contains("same creation_guid"));
            Assert.Empty(ProjectIntegrations.Validate(ProjectIntegrations.Empty("P01")));
        }
    }

    public class ExchangeManifestTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hzx-tests-" + Guid.NewGuid().ToString("N"));
        private readonly string _src;

        public ExchangeManifestTests()
        {
            _src = Path.Combine(_root, "src");
            Directory.CreateDirectory(_src);
            File.WriteAllText(Path.Combine(_src, "ledger.csv"), "issue_id,state\nC-1,open\nC-2,resolved\n");
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        private ExchangeManifest.Request Request(string state = "self_reported_verified", bool host = false) => new ExchangeManifest.Request
        {
            Kind = "coordination.ledger",
            ProjectCode = "P01",
            ProducerApp = "horizun-revit",
            ProducerVersion = "3.0.0",
            ProducerCommit = "abc1234",
            ModelRef = "mdl-aaaaaaaaaaaa",
            ModelEvidence = ModelIdentity.Evidence("0b5c2f8e-1111-4c3a-9d2b-7e1f00000001", null, null, null, null, "pi", "2026", "A", null),
            SpatialRef = new JObject { ["frame"] = "shared", ["length_unit"] = "mm" },
            Files = { new ExchangeManifest.PayloadFile { SourcePath = Path.Combine(_src, "ledger.csv"), Format = "horizun.coordination-ledger.csv/2", Rows = 2 } },
            State = state,
            Method = "reread",
            HostVerified = host,
            Caveats = { "The ledger is the Revit side only; Navisworks state is not in it." }
        };

        private string Root => Path.Combine(_root, "exchange");
        private const string Id = "hzx-20261012T163000Z-3f9a1c2e";

        [Fact]
        public void A_named_model_without_registry_or_strong_evidence_cannot_continue()
        {
            JObject emitted = ExchangeManifest.Emit(Request(), Root, Id);
            string path = (string)emitted["manifest_path"];
            Assert.False((bool)ExchangeManifest.Verify(path, null)["may_continue"]);
            JObject manifest = JObject.Parse(File.ReadAllText(path));
            manifest["source"]["model_evidence"] = new JObject { ["project_info_unique_id"] = "weak" };
            File.WriteAllText(path, manifest.ToString());
            var models = new[] { new JObject { ["model_ref"] = "mdl-aaaaaaaaaaaa", ["evidence"] = Request().ModelEvidence } };
            Assert.False((bool)ExchangeManifest.Verify(path, models)["may_continue"]);
        }

        [Theory]
        [InlineData(".")]
        [InlineData("..")]
        public void Default_project_root_never_escapes_its_project_directory(string code)
        {
            string path = Path.GetFullPath(ExchangeManifest.DefaultRootUnder(_root, code));
            Assert.StartsWith(Path.Combine(_root, "exchange") + Path.DirectorySeparatorChar, path);
        }

        [Fact]
        public void Emit_writes_the_payload_then_the_manifest_and_reads_both_back()
        {
            JObject r = ExchangeManifest.Emit(Request(), Root, Id);
            string dir = Path.Combine(Root, "coordination.ledger", Id);
            Assert.Equal(dir, (string)r["directory"]);
            Assert.True(File.Exists(Path.Combine(dir, "ledger.csv")));
            JObject m = JObject.Parse(File.ReadAllText(Path.Combine(dir, ExchangeManifest.ManifestFileName)));
            Assert.Equal("horizun.exchange/0", (string)m["schema"]);
            Assert.Equal(ExchangeManifest.Sha256File(Path.Combine(_src, "ledger.csv")), (string)m["files"][0]["sha256"]);
            Assert.Equal("ok", (string)r["reread"]["integrity"]);
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
        }

        [Fact]
        public void Verified_needs_host_verification_and_is_refused_without_it()
        {
            Assert.Contains(ExchangeManifest.Check(Request("verified", host: false)), e => e.Contains("host_verified"));
            Assert.Empty(ExchangeManifest.Check(Request("verified", host: true)));
        }

        [Fact]
        public void An_exchange_is_never_written_twice()
        {
            ExchangeManifest.Emit(Request(), Root, Id);
            Assert.Throws<InvalidOperationException>(() => ExchangeManifest.Emit(Request(), Root, Id));
        }

        [Fact]
        public void Payload_without_a_manifest_is_not_delivered_not_an_error()
        {
            string dir = Path.Combine(Root, "coordination.ledger", Id);
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(_src, "ledger.csv"), Path.Combine(dir, "ledger.csv"));
            JObject v = ExchangeManifest.Verify(dir, null);
            Assert.Equal(ExchangeManifest.NotDelivered, (string)v["delivery"]);
            Assert.False((bool)v["may_continue"]);
        }

        [Fact]
        public void A_manifest_whose_payload_has_not_arrived_is_not_delivered()
        {
            ExchangeManifest.Emit(Request(), Root, Id);
            string dir = Path.Combine(Root, "coordination.ledger", Id);
            File.Delete(Path.Combine(dir, "ledger.csv"));
            JObject v = ExchangeManifest.Verify(dir, null);
            Assert.Equal(ExchangeManifest.NotDelivered, (string)v["delivery"]);
            Assert.Null((string)v["effective_state"]);
        }

        [Fact]
        public void A_changed_payload_fails_on_its_sha256()
        {
            ExchangeManifest.Emit(Request(), Root, Id);
            string dir = Path.Combine(Root, "coordination.ledger", Id);
            File.AppendAllText(Path.Combine(dir, "ledger.csv"), "C-3,open\n");
            JObject v = ExchangeManifest.Verify(dir, null);
            Assert.Equal("sha256_mismatch", (string)v["integrity"]);
            Assert.Equal("failed", (string)v["effective_state"]);
            Assert.False((bool)v["may_continue"]);
        }

        [Fact]
        public void A_hand_edited_verified_without_host_verification_reads_as_self_reported()
        {
            ExchangeManifest.Emit(Request(), Root, Id);
            string path = Path.Combine(Root, "coordination.ledger", Id, ExchangeManifest.ManifestFileName);
            JObject m = JObject.Parse(File.ReadAllText(path));
            m["verification"]["state"] = "verified";
            File.WriteAllText(path, m.ToString());
            JObject v = ExchangeManifest.Verify(path, new[] { new JObject { ["model_ref"] = Request().ModelRef, ["evidence"] = Request().ModelEvidence } });
            Assert.Equal("verified", (string)v["producer_state"]);
            Assert.Equal("self_reported_verified", (string)v["effective_state"]);
            Assert.True((bool)v["may_continue"]);
        }

        [Fact]
        public void Manifest_requires_verification_method_host_flag_and_unique_payload_names()
        {
            JObject m = ExchangeManifest.Build(Request(), Id, ExchangeManifest.Sha256File);
            ((JObject)m["verification"]).Remove("method");
            ((JObject)m["verification"]).Remove("host_verified");
            ((JArray)m["files"]).Add(m["files"][0].DeepClone());
            Assert.Contains(ExchangeManifest.Shape(m), e => e.Contains("method"));
            Assert.Contains(ExchangeManifest.Shape(m), e => e.Contains("host_verified"));
            Assert.Contains(ExchangeManifest.Shape(m), e => e.Contains("duplicate"));
        }

        [Fact]
        public void A_partial_producer_stops_the_chain()
        {
            ExchangeManifest.Emit(Request("partial"), Root, Id);
            JObject v = ExchangeManifest.Verify(Path.Combine(Root, "coordination.ledger", Id), null);
            Assert.Equal("ok", (string)v["integrity"]);
            Assert.False((bool)v["may_continue"]);
        }

        [Fact]
        public void Identity_against_the_registry_is_proven_mismatched_or_unknown()
        {
            ExchangeManifest.Emit(Request(), Root, Id);
            string dir = Path.Combine(Root, "coordination.ledger", Id);
            var same = new JObject { ["model_ref"] = "mdl-aaaaaaaaaaaa", ["evidence"] = ModelIdentity.Evidence("0b5c2f8e-1111-4c3a-9d2b-7e1f00000001", null, null, null, null, "pi", "2026", "A", null) };
            var other = new JObject { ["model_ref"] = "mdl-aaaaaaaaaaaa", ["evidence"] = ModelIdentity.Evidence(Guid.NewGuid().ToString(), null, null, null, null, "pi", "2026", "A", null) };
            Assert.Equal("proven", (string)ExchangeManifest.Verify(dir, new[] { same })["identity"]["state"]);
            JObject mismatch = ExchangeManifest.Verify(dir, new[] { other });
            Assert.Equal("mismatch", (string)mismatch["identity"]["state"]);
            Assert.False((bool)mismatch["may_continue"]);
            Assert.Equal("unknown_model_ref", (string)ExchangeManifest.Verify(dir, new JObject[0])["identity"]["state"]);
        }

        [Fact]
        public void The_chain_is_as_strong_as_its_weakest_hop()
        {
            Assert.Equal("verified", ExchangeManifest.Weakest(new[] { "verified", "verified" }));
            Assert.Equal("self_reported_verified", ExchangeManifest.Weakest(new[] { "verified", "self_reported_verified" }));
            Assert.Equal("failed", ExchangeManifest.Weakest(new[] { "verified", "partial", "nonsense" }));
        }

        [Fact]
        public void File_names_that_escape_the_folder_or_shadow_the_manifest_are_refused()
        {
            var r = Request();
            r.Files[0].Name = "..\\evil.csv";
            Assert.NotEmpty(ExchangeManifest.Check(r));
            r.Files[0].Name = ExchangeManifest.ManifestFileName;
            Assert.NotEmpty(ExchangeManifest.Check(r));
        }

        [Fact]
        public void The_default_root_is_local_under_the_users_horizun_folder()
        {
            Assert.Equal(Path.Combine(@"C:\Users\someone", ".horizun", "exchange", "P_01"), ExchangeManifest.DefaultRoot(@"C:\Users\someone", "P/01"));
        }

        [Fact]
        public void The_schema_file_requires_what_the_verifier_requires()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "schemas", "horizun.exchange.v0.schema.json"))) d = d.Parent;
            Assert.True(d != null, "schemas/horizun.exchange.v0.schema.json is missing");
            JObject schema = JObject.Parse(File.ReadAllText(Path.Combine(d.FullName, "schemas", "horizun.exchange.v0.schema.json")));
            Assert.Equal(new[] { "schema", "exchange_id", "kind", "producer", "source", "files", "verification" }.OrderBy(x => x),
                         ((JArray)schema["required"]).Select(t => (string)t).OrderBy(x => x));
            Assert.Equal(ExchangeManifest.Kinds, ((JArray)schema["properties"]["kind"]["enum"]).Select(t => (string)t));
            Assert.Equal(ExchangeManifest.States, ((JArray)schema["properties"]["verification"]["properties"]["state"]["enum"]).Select(t => (string)t));
        }
    }
}
