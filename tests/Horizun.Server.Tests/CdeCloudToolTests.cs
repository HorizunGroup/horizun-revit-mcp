// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// horizun_cde_cloud against a FAKE HttpMessageHandler: every answer below is a
// synthetic JSON document shaped like the APS Data Management API and the OpenCDE
// Foundation/Documents APIs. No network, no real credential, no real project.
//
// What is proved: nothing is sent when no credential is configured; credentials go
// only to the provider's hosts and never come back in a reply; pagination is
// followed; 429 is retried with the server's Retry-After; 401/403 are not retried
// and make coverage incomplete; a spent budget stops the walk; state folders are
// matched by EXACT name and the rest are reported unmapped; the MIDP cross is the
// shared core's; a 3-legged token is refreshed and written back.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class CdeCloudToolTests : IDisposable
    {
        private const string Aps = "https://developer.api.autodesk.com";
        private const string Hub = "b.11111111-1111-1111-1111-111111111111";
        private const string Project = "b.22222222-2222-2222-2222-222222222222";
        private const string Secret = "not-a-real-secret-value";
        private const string IssuedToken = "issued-access-token-value";

        private readonly string _dir;

        public CdeCloudToolTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-cde-cloud-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // ---- the fake cloud --------------------------------------------------------------

        private sealed class FakeCloud : HttpMessageHandler
        {
            public readonly List<(string Method, string Url, string Auth, string Body)> Requests = new List<(string, string, string, string)>();
            public readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> Routes =
                new Dictionary<string, Queue<Func<HttpResponseMessage>>>(StringComparer.Ordinal);

            public void On(string method, string url, params Func<HttpResponseMessage>[] answers)
            {
                string key = method + " " + url;
                if (!Routes.TryGetValue(key, out var q)) Routes[key] = q = new Queue<Func<HttpResponseMessage>>();
                foreach (var a in answers) q.Enqueue(a);
            }

            public void Json(string method, string url, JObject body) => On(method, url, () => Reply(HttpStatusCode.OK, body));

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                string body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                Requests.Add((request.Method.Method, request.RequestUri.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
                string key = request.Method.Method + " " + request.RequestUri.AbsoluteUri;
                if (Routes.TryGetValue(key, out var q) && q.Count > 0)
                {
                    var answer = q.Count > 1 ? q.Dequeue() : q.Peek();
                    return Task.FromResult(answer());
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            }
        }

        private static HttpResponseMessage Reply(HttpStatusCode code, JObject body) =>
            new HttpResponseMessage(code) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };

        private static string Contents(string folderId, bool foldersOnly, int? page = null) =>
            Aps + "/data/v1/projects/" + Uri.EscapeDataString(Project) + "/folders/" + Uri.EscapeDataString(folderId) +
            "/contents?page%5Blimit%5D=200" + (foldersOnly ? "&filter%5Btype%5D=folders" : "") + (page.HasValue ? "&page%5Bnumber%5D=" + page : "");

        private static JObject FolderEntry(string id, string name) => new JObject
        {
            ["type"] = "folders", ["id"] = id, ["attributes"] = new JObject { ["displayName"] = name }
        };

        private static JObject ItemEntry(string id, string name, string tip) => new JObject
        {
            ["type"] = "items", ["id"] = id, ["attributes"] = new JObject { ["displayName"] = name },
            ["relationships"] = new JObject { ["tip"] = new JObject { ["data"] = new JObject { ["type"] = "versions", ["id"] = tip } } }
        };

        private static JObject VersionEntry(string id, int number, long size, string modified) => new JObject
        {
            ["type"] = "versions", ["id"] = id,
            ["attributes"] = new JObject { ["versionNumber"] = number, ["storageSize"] = size, ["lastModifiedTime"] = modified, ["fileType"] = "rvt" }
        };

        private static JObject Page(JArray data, JArray included = null, string next = null)
        {
            var o = new JObject { ["data"] = data, ["included"] = included ?? new JArray() };
            if (next != null) o["links"] = new JObject { ["next"] = new JObject { ["href"] = next } };
            return o;
        }

        /// <summary>A project with Project Files/{01_WIP, 02_SHARED, 03_PUBLISHED, 04_archived, 99_Misc} and a Plans top folder.</summary>
        private static FakeCloud Project2Legged()
        {
            var f = new FakeCloud();
            f.Json("POST", Aps + "/authentication/v2/token", new JObject { ["access_token"] = IssuedToken, ["expires_in"] = 3599, ["token_type"] = "Bearer" });
            f.Json("GET", Aps + "/project/v1/hubs", Page(new JArray(new JObject { ["type"] = "hubs", ["id"] = Hub })));
            f.Json("GET", Aps + "/project/v1/hubs/" + Uri.EscapeDataString(Hub) + "/projects/" + Uri.EscapeDataString(Project),
                   new JObject { ["data"] = new JObject { ["type"] = "projects", ["id"] = Project } });
            f.Json("GET", Aps + "/project/v1/hubs/" + Uri.EscapeDataString(Hub) + "/projects/" + Uri.EscapeDataString(Project) + "/topFolders",
                   Page(new JArray(FolderEntry("urn:pf", "Project Files"), FolderEntry("urn:plans", "Plans"))));
            f.Json("GET", Contents("urn:pf", true), Page(new JArray(
                FolderEntry("urn:wip", "01_WIP"), FolderEntry("urn:shared", "02_SHARED"), FolderEntry("urn:pub", "03_PUBLISHED"),
                FolderEntry("urn:arch", "04_archived"), FolderEntry("urn:misc", "99_Misc"))));
            return f;
        }

        private CdeCloudEnvironment Env(FakeCloud cloud, Dictionary<string, string> vars, List<TimeSpan> sleeps = null) => new CdeCloudEnvironment
        {
            Handler = cloud,
            Variable = n => vars != null && vars.TryGetValue(n, out var v) ? v : null,
            Sleep = (d, ct) => sleeps?.Add(d),
            Now = () => new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero),
            TokenFilePath = Path.Combine(_dir, "aps-token.json")
        };

        private static Dictionary<string, string> ClientCredentials() => new Dictionary<string, string>
        {
            ["HORIZUN_APS_CLIENT_ID"] = "test-client-id", ["HORIZUN_APS_CLIENT_SECRET"] = Secret
        };

        private static JObject States() => new JObject
        {
            ["wip"] = "Project Files/01_WIP", ["shared"] = "Project Files/02_SHARED",
            ["published"] = "Project Files/03_PUBLISHED", ["archived"] = "Project Files/04_ARCHIVED"
        };

        private static JObject AccArgs(string op) => new JObject
        {
            ["operation"] = op, ["provider"] = "acc", ["project_id"] = Project, ["states"] = States()
        };

        private static void NoSecretIn(JObject result)
        {
            string text = result.ToString();
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain(IssuedToken, text);
        }

        // ---- credentials -------------------------------------------------------------------

        [Fact]
        public void Acc_list_projects_names_the_hubs_and_projects_the_credential_can_see()
        {
            var cloud = Project2Legged();
            cloud.Routes.Remove("GET " + Aps + "/project/v1/hubs");
            cloud.Json("GET", Aps + "/project/v1/hubs", Page(new JArray(new JObject
            {
                ["type"] = "hubs", ["id"] = Hub,
                ["attributes"] = new JObject { ["name"] = "Demo account", ["region"] = "US", ["extension"] = new JObject { ["type"] = "hubs:autodesk.bim360:Account" } }
            })));
            cloud.Json("GET", Aps + "/project/v1/hubs/" + Uri.EscapeDataString(Hub) + "/projects", Page(new JArray(
                new JObject { ["type"] = "projects", ["id"] = Project, ["attributes"] = new JObject { ["name"] = "Demo tower" } })));
            JObject r = CdeCloudTool.Handle(new JObject { ["operation"] = "list_projects", ["provider"] = "acc" },
                                            CancellationToken.None, Env(cloud, ClientCredentials()));
            Assert.True((bool)r["coverage_complete"]);
            Assert.Equal("Demo account", (string)r["hubs"][0]["name"]);
            Assert.Equal(Project, (string)r["hubs"][0]["projects"][0]["project_id"]);
            Assert.Equal("Demo tower", (string)r["hubs"][0]["projects"][0]["name"]);
            Assert.All(cloud.Requests, q => Assert.Equal(q.Url.Contains("/authentication/") ? "POST" : "GET", q.Method));
        }

        [Fact]
        public void Acc_without_any_credential_is_refused_with_zero_requests()
        {
            var cloud = Project2Legged();
            var ex = Assert.Throws<ToolRefusal>(() => CdeCloudTool.Handle(AccArgs("list_states"), CancellationToken.None, Env(cloud, null)));
            Assert.Contains("HORIZUN_APS_CLIENT_ID", ex.Message);
            Assert.Contains("No request was made", ex.Message);
            Assert.Empty(cloud.Requests);
        }

        [Fact]
        public void OpenCde_inspect_without_a_token_is_refused_with_zero_requests()
        {
            var cloud = new FakeCloud();
            var args = new JObject
            {
                ["operation"] = "inspect", ["provider"] = "opencde", ["server_url"] = "https://cde.example.test",
                ["document_ids"] = new JArray("d1")
            };
            var ex = Assert.Throws<ToolRefusal>(() => CdeCloudTool.Handle(args, CancellationToken.None, Env(cloud, null)));
            Assert.Contains(CdeCloudTool.OpenCdeTokenName, ex.Message);
            Assert.Empty(cloud.Requests);
        }

        [Fact]
        public void A_credential_in_the_arguments_is_an_unknown_argument()
        {
            var args = AccArgs("list_states");
            args["client_secret"] = Secret;
            var cloud = Project2Legged();
            Assert.Throws<ToolRefusal>(() => CdeCloudTool.Handle(args, CancellationToken.None, Env(cloud, ClientCredentials())));
            Assert.Empty(cloud.Requests);
        }

        // ---- list_states -------------------------------------------------------------------

        [Fact]
        public void List_states_maps_by_exact_name_and_reports_the_rest_as_unmapped()
        {
            var cloud = Project2Legged();
            JObject r = CdeCloudTool.Handle(AccArgs("list_states"), CancellationToken.None, Env(cloud, ClientCredentials()));

            Assert.Equal(Hub, (string)r["hub_id"]);
            Assert.True((bool)r["states"]["wip"]["resolved"]);
            Assert.Equal("urn:wip", (string)r["states"]["wip"]["folder_id"]);
            Assert.True((bool)r["states"]["published"]["resolved"]);
            // 04_archived differs only in case from the declared 04_ARCHIVED: never taken.
            Assert.False((bool)r["states"]["archived"]["resolved"]);
            Assert.Contains("differs only in case", (string)r["states"]["archived"]["reason"]);
            Assert.False((bool)r["coverage_complete"]);

            var unmapped = r["unmapped_folders"].Select(u => (string)u["path"]).ToList();
            Assert.Contains("Plans", unmapped);
            Assert.Contains("Project Files/99_Misc", unmapped);
            Assert.Contains("Project Files/04_archived", unmapped);
            Assert.DoesNotContain("Project Files", unmapped);
            Assert.DoesNotContain("Project Files/01_WIP", unmapped);

            // The client secret went only in the Basic header of the token request; every
            // data request carried the bearer; nothing left the provider's host.
            var token = cloud.Requests.Single(q => q.Url.EndsWith("/authentication/v2/token", StringComparison.Ordinal));
            Assert.StartsWith("Basic ", token.Auth);
            Assert.Contains("grant_type=client_credentials", token.Body);
            Assert.Contains("scope=data%3Aread", token.Body);
            Assert.DoesNotContain(Secret, token.Body);
            Assert.All(cloud.Requests.Where(q => q != token), q => Assert.Equal("Bearer " + IssuedToken, q.Auth));
            Assert.All(cloud.Requests, q => Assert.StartsWith(Aps + "/", q.Url));
            Assert.Equal("two_legged", (string)r["auth"]["mode"]);
            NoSecretIn(r);
        }

        [Fact]
        public void A_local_path_in_cde_states_is_not_a_cloud_folder()
        {
            var cloud = Project2Legged();
            var args = AccArgs("list_states");
            args["states"]["wip"] = @"C:\Sync\Project\01_WIP";
            JObject r = CdeCloudTool.Handle(args, CancellationToken.None, Env(cloud, ClientCredentials()));
            Assert.False((bool)r["states"]["wip"]["resolved"]);
            Assert.Contains("local path", (string)r["states"]["wip"]["reason"]);
        }

        [Fact]
        public void An_unauthorized_answer_is_not_retried_and_leaves_coverage_incomplete()
        {
            var cloud = new FakeCloud();
            cloud.Json("POST", Aps + "/authentication/v2/token", new JObject { ["access_token"] = IssuedToken, ["expires_in"] = 3599 });
            cloud.On("GET", Aps + "/project/v1/hubs", () => Reply(HttpStatusCode.Unauthorized, new JObject { ["detail"] = "tenant secret detail" }));
            var sleeps = new List<TimeSpan>();
            var args = AccArgs("list_states");
            JObject r = CdeCloudTool.Handle(args, CancellationToken.None, Env(cloud, ClientCredentials(), sleeps));

            Assert.False((bool)r["coverage_complete"]);
            Assert.Null((string)r["hub_id"]);
            Assert.Single(cloud.Requests, q => q.Url == Aps + "/project/v1/hubs");
            Assert.Empty(sleeps);
            Assert.Contains(r["http"]["errors"], e => (int?)e["status"] == 401);
            Assert.DoesNotContain("tenant secret detail", r.ToString());
        }

        [Fact]
        public void A_429_is_retried_after_the_servers_retry_after()
        {
            var cloud = Project2Legged();
            cloud.Routes.Remove("GET " + Aps + "/project/v1/hubs");
            cloud.On("GET", Aps + "/project/v1/hubs",
                () => { var m = Reply((HttpStatusCode)429, new JObject()); m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2)); return m; },
                () => Reply(HttpStatusCode.OK, Page(new JArray(new JObject { ["type"] = "hubs", ["id"] = Hub }))));
            var sleeps = new List<TimeSpan>();
            JObject r = CdeCloudTool.Handle(AccArgs("list_states"), CancellationToken.None, Env(cloud, ClientCredentials(), sleeps));

            Assert.Equal(Hub, (string)r["hub_id"]);
            Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, sleeps);
            Assert.Equal(1, (int)r["http"]["retries"]);
        }

        [Fact]
        public void A_persistent_429_gives_up_after_bounded_retries()
        {
            var cloud = Project2Legged();
            cloud.Routes.Remove("GET " + Aps + "/project/v1/hubs");
            cloud.On("GET", Aps + "/project/v1/hubs", () => Reply((HttpStatusCode)429, new JObject()));
            var sleeps = new List<TimeSpan>();
            JObject r = CdeCloudTool.Handle(AccArgs("list_states"), CancellationToken.None, Env(cloud, ClientCredentials(), sleeps));

            Assert.False((bool)r["coverage_complete"]);
            Assert.Equal(CdeCloudHttp.MaxRetries, sleeps.Count);
            Assert.Equal(new[] { 1, 2, 4, 8 }.Select(s => TimeSpan.FromSeconds(s)), sleeps);
        }

        [Fact]
        public void A_spent_call_budget_stops_and_says_so()
        {
            var cloud = Project2Legged();
            var args = AccArgs("list_states");
            args["max_calls"] = 3;   // token, hubs, project - then nothing
            JObject r = CdeCloudTool.Handle(args, CancellationToken.None, Env(cloud, ClientCredentials()));
            Assert.False((bool)r["coverage_complete"]);
            Assert.True((bool)r["http"]["budget_exhausted"]);
            Assert.Equal(3, cloud.Requests.Count);
            Assert.False((bool)r["states"]["wip"]["resolved"]);
        }

        [Fact]
        public void A_pagination_link_to_another_host_is_refused_before_the_request()
        {
            var cloud = Project2Legged();
            cloud.Routes.Remove("GET " + Aps + "/project/v1/hubs");
            cloud.Json("GET", Aps + "/project/v1/hubs",
                Page(new JArray(new JObject { ["type"] = "hubs", ["id"] = Hub }), null, "https://collector.example.test/steal"));
            JObject r = CdeCloudTool.Handle(AccArgs("list_states"), CancellationToken.None, Env(cloud, ClientCredentials()));
            Assert.DoesNotContain(cloud.Requests, q => q.Url.Contains("collector.example.test"));
            Assert.Contains(r["http"]["errors"], e => ((string)e["reason"]).Contains("not one this reader may call"));
        }

        // ---- inspect -----------------------------------------------------------------------

        private FakeCloud InspectableProject()
        {
            var cloud = Project2Legged();
            // WIP over TWO pages; the second page is reached only through links.next.
            cloud.Json("GET", Contents("urn:wip", false), Page(
                new JArray(ItemEntry("urn:i1", "HZ01-HRZ-ZZ-XX-M3-A-0001-S0-P01.rvt", "urn:v1"), FolderEntry("urn:wipsub", "Sub")),
                new JArray(VersionEntry("urn:v1", 3, 1048576, "2026-09-20T10:00:00.000Z")),
                "/data/v1/projects/" + Uri.EscapeDataString(Project) + "/folders/urn%3Awip/contents?page%5Blimit%5D=200&page%5Bnumber%5D=1"));
            cloud.Json("GET", Aps + "/data/v1/projects/" + Uri.EscapeDataString(Project) + "/folders/urn%3Awip/contents?page%5Blimit%5D=200&page%5Bnumber%5D=1",
                Page(new JArray(ItemEntry("urn:i2", "notes from site.pdf", "urn:v2")), new JArray(VersionEntry("urn:v2", 1, 2048, "2026-09-21T10:00:00.000Z"))));
            cloud.Json("GET", Contents("urn:wipsub", false), Page(new JArray()));
            cloud.Json("GET", Contents("urn:shared", false), Page(
                new JArray(ItemEntry("urn:i3", "HZ01-HRZ-ZZ-XX-M3-A-0002-S2-P02.rvt", "urn:v3")),
                new JArray(VersionEntry("urn:v3", 2, 4096, "2026-09-22T10:00:00.000Z"))));
            cloud.Json("GET", Contents("urn:pub", false), Page(new JArray()));
            cloud.Json("GET", Contents("urn:arch", false), Page(new JArray()));
            return cloud;
        }

        private static JObject InspectArgs()
        {
            var args = AccArgs("inspect");
            args["states"]["archived"] = "Project Files/04_archived";
            args["naming"] = new JObject { ["file_name"] = "name_status_revision" };
            args["as_of"] = "2026-09-24";
            args["deliverables"] = new JArray
            {
                new JObject { ["container"] = "HZ01-HRZ-ZZ-XX-M3-A-0002", ["required_status"] = "S3", ["due"] = "2026-12-01", ["format"] = "rvt" },
                new JObject { ["container"] = "HZ01-HRZ-ZZ-XX-M3-A-0009", ["required_status"] = "S2", ["due"] = "2026-09-01" },
                new JObject { ["container"] = "HZ01-HRZ-ZZ-XX-M3-A-0001", ["required_status"] = "S0" }
            };
            return args;
        }

        [Fact]
        public void Inspect_follows_pages_and_crosses_the_midp_with_the_shared_core()
        {
            var cloud = InspectableProject();
            JObject r = CdeCloudTool.Handle(InspectArgs(), CancellationToken.None, Env(cloud, ClientCredentials()));

            Assert.True((bool)r["coverage_complete"], r["coverage"].ToString());
            Assert.Equal(3, (int)r["files_walked"]);
            var files = r["files"].ToList();
            JToken wipModel = files.Single(f => (string)f["path"] == "Project Files/01_WIP/HZ01-HRZ-ZZ-XX-M3-A-0001-S0-P01.rvt");
            Assert.Equal(3, (int)wipModel["version_number"]);
            Assert.Equal(1048576, (long)wipModel["size_bytes"]);
            Assert.Equal("S0", (string)wipModel["status"]);
            Assert.Contains(files, f => (string)f["path"] == "Project Files/01_WIP/notes from site.pdf" && !(bool)f["name_compliant"]);

            var kinds = r["findings"].Select(f => (string)f["kind"]).ToList();
            Assert.Contains("name_noncompliant", kinds);
            Assert.Contains("deliverable_insufficient_state", kinds);   // 0002 is S2, S3 required
            Assert.Contains("deliverable_overdue", kinds);              // 0009 never appeared, due 2026-09-01
            var verdicts = r["deliverables"].ToDictionary(d => (string)d["container"], d => (string)d["verdict"]);
            Assert.Equal("insufficient_state", verdicts["HZ01-HRZ-ZZ-XX-M3-A-0002"]);
            Assert.Equal("overdue", verdicts["HZ01-HRZ-ZZ-XX-M3-A-0009"]);
            Assert.Equal("satisfied", verdicts["HZ01-HRZ-ZZ-XX-M3-A-0001"]);
            // The order is the shared core's: overdue first.
            Assert.Equal("deliverable_overdue", (string)r["findings"][0]["kind"]);
            NoSecretIn(r);
        }

        [Fact]
        public void A_forbidden_state_folder_is_not_covered_and_a_missing_deliverable_says_why()
        {
            var cloud = InspectableProject();
            cloud.Routes.Remove("GET " + Contents("urn:pub", false));
            cloud.On("GET", Contents("urn:pub", false), () => Reply(HttpStatusCode.Forbidden, new JObject()));
            JObject r = CdeCloudTool.Handle(InspectArgs(), CancellationToken.None, Env(cloud, ClientCredentials()));

            Assert.False((bool)r["coverage_complete"]);
            JToken pub = r["coverage"].Single(c => (string)c["state"] == "published");
            Assert.False((bool)pub["covered"]);
            Assert.Contains("403", (string)pub["gaps"][0]["reason"]);
            JToken missing = r["deliverables"].Single(d => (string)d["container"] == "HZ01-HRZ-ZZ-XX-M3-A-0009");
            Assert.Contains("coverage is incomplete", (string)missing["caveat"]);
        }

        [Fact]
        public void Inspect_states_status_mismatch_from_the_name()
        {
            var cloud = InspectableProject();
            cloud.Routes.Remove("GET " + Contents("urn:pub", false));
            cloud.Json("GET", Contents("urn:pub", false), Page(
                new JArray(ItemEntry("urn:i4", "HZ01-HRZ-ZZ-XX-M3-A-0003-S2-P01.rvt", "urn:v4")),
                new JArray(VersionEntry("urn:v4", 1, 10, "2026-09-22T10:00:00.000Z"))));
            JObject r = CdeCloudTool.Handle(InspectArgs(), CancellationToken.None, Env(cloud, ClientCredentials()));
            Assert.Contains(r["findings"], f => (string)f["kind"] == "state_status_mismatch" && (string)f["state"] == "published");
        }

        // ---- versions ----------------------------------------------------------------------

        [Fact]
        public void Versions_lists_an_items_history_newest_first()
        {
            var cloud = new FakeCloud();
            cloud.Json("POST", Aps + "/authentication/v2/token", new JObject { ["access_token"] = IssuedToken, ["expires_in"] = 3599 });
            string item = "urn:adsk.wipprod:dm.lineage:abc";
            cloud.Json("GET", Aps + "/data/v1/projects/" + Uri.EscapeDataString(Project) + "/items/" + Uri.EscapeDataString(item) + "/versions",
                Page(new JArray(VersionEntry("urn:v1", 1, 10, "2026-01-01T00:00:00Z"), VersionEntry("urn:v3", 3, 30, "2026-03-01T00:00:00Z"),
                                VersionEntry("urn:v2", 2, 20, "2026-02-01T00:00:00Z"))));
            var args = new JObject { ["operation"] = "versions", ["provider"] = "acc", ["project_id"] = Project, ["item_id"] = item };
            JObject r = CdeCloudTool.Handle(args, CancellationToken.None, Env(cloud, ClientCredentials()));
            Assert.True((bool)r["coverage_complete"]);
            Assert.Equal(new[] { 3, 2, 1 }, r["versions"].Select(v => (int)v["version_number"]));
        }

        // ---- 3-legged token file -----------------------------------------------------------

        [Fact]
        public void An_expired_three_legged_token_is_refreshed_and_written_back()
        {
            var env = Env(new FakeCloud(), ClientCredentials());
            File.WriteAllText(env.TokenFilePath, new JObject
            {
                ["access_token"] = "old-access", ["refresh_token"] = "old-refresh", ["expires_at"] = "2026-09-24T10:00:00Z"
            }.ToString());
            var cloud = (FakeCloud)env.Handler;
            cloud.Json("POST", Aps + "/authentication/v2/token",
                new JObject { ["access_token"] = IssuedToken, ["refresh_token"] = "new-refresh", ["expires_in"] = 3600 });
            cloud.Json("GET", Aps + "/data/v1/projects/" + Uri.EscapeDataString(Project) + "/items/x/versions", Page(new JArray()));
            var args = new JObject { ["operation"] = "versions", ["provider"] = "acc", ["project_id"] = Project, ["item_id"] = "x" };
            JObject r = CdeCloudTool.Handle(args, CancellationToken.None, env);

            Assert.Equal("three_legged", (string)r["auth"]["mode"]);
            Assert.True((bool)r["auth"]["token_file_refreshed"]);
            var token = cloud.Requests.First();
            Assert.Contains("grant_type=refresh_token", token.Body);
            Assert.Contains("refresh_token=old-refresh", token.Body);
            JObject stored = JObject.Parse(File.ReadAllText(env.TokenFilePath));
            Assert.Equal(IssuedToken, (string)stored["access_token"]);
            Assert.Equal("new-refresh", (string)stored["refresh_token"]);
            Assert.Equal("Bearer " + IssuedToken, cloud.Requests.Last().Auth);
            Assert.DoesNotContain("old-refresh", r.ToString());
            Assert.DoesNotContain("new-refresh", r.ToString());
        }

        [Fact]
        public void A_valid_three_legged_token_is_used_without_a_token_request()
        {
            var env = Env(new FakeCloud(), null);
            File.WriteAllText(env.TokenFilePath, new JObject
            {
                ["access_token"] = IssuedToken, ["refresh_token"] = "r", ["expires_at"] = "2026-09-24T18:00:00Z"
            }.ToString());
            var cloud = (FakeCloud)env.Handler;
            cloud.Json("GET", Aps + "/data/v1/projects/" + Uri.EscapeDataString(Project) + "/items/x/versions", Page(new JArray()));
            var args = new JObject { ["operation"] = "versions", ["provider"] = "acc", ["project_id"] = Project, ["item_id"] = "x" };
            JObject r = CdeCloudTool.Handle(args, CancellationToken.None, env);
            Assert.Single(cloud.Requests);
            Assert.Equal("three_legged", (string)r["auth"]["mode"]);
        }

        // ---- OpenCDE -----------------------------------------------------------------------

        private static FakeCloud OpenCdeServer()
        {
            var f = new FakeCloud();
            f.Json("GET", "https://cde.example.test/foundation/versions", new JObject
            {
                ["versions"] = new JArray(
                    new JObject { ["api_id"] = "foundation", ["version_id"] = "1.1", ["api_base_url"] = "https://cde.example.test/foundation/1.1" },
                    new JObject { ["api_id"] = "documents", ["version_id"] = "1.0", ["api_base_url"] = "https://docs.example.test/documents/1.0" })
            });
            f.Json("GET", "https://cde.example.test/foundation/1.1/auth", new JObject
            {
                ["oauth2_token_url"] = "https://cde.example.test/oauth2/token", ["supported_oauth2_flows"] = new JArray("authorization_code_grant")
            });
            return f;
        }

        private static JObject Doc(string id, string name, int index, string versionsUrl) => new JObject
        {
            ["document_id"] = id, ["title"] = name, ["version_number"] = "v" + index, ["version_index"] = index,
            ["creation_date"] = "2026-09-0" + index + "T00:00:00Z",
            ["file_description"] = new JObject { ["name"] = name, ["size_in_bytes"] = 100 * index },
            ["links"] = new JObject { ["document_versions"] = new JObject { ["url"] = versionsUrl } }
        };

        [Fact]
        public void OpenCde_list_states_is_discovery_only_and_says_states_cannot_be_mapped()
        {
            var cloud = OpenCdeServer();
            var args = new JObject { ["operation"] = "list_states", ["provider"] = "opencde", ["server_url"] = "https://cde.example.test" };
            JObject r = CdeCloudTool.Handle(args, CancellationToken.None, Env(cloud, null));
            Assert.False((bool)r["states_mappable"]);
            Assert.False((bool)r["coverage_complete"]);
            Assert.Equal("https://docs.example.test/documents/1.0", (string)r["documents_api"]["base_url"]);
            Assert.All(cloud.Requests, q => Assert.Null(q.Auth));
        }

        [Fact]
        public void OpenCde_inspect_reads_the_given_documents_and_never_claims_complete_coverage()
        {
            var cloud = OpenCdeServer();
            cloud.Json("POST", "https://docs.example.test/documents/1.0/document-versions", new JObject
            {
                ["versions"] = new JArray(Doc("d1", "HZ01-HRZ-ZZ-XX-M3-A-0001.ifc", 2, "https://docs.example.test/documents/1.0/d1/versions"))
            });
            var args = new JObject
            {
                ["operation"] = "inspect", ["provider"] = "opencde", ["server_url"] = "https://cde.example.test",
                ["document_ids"] = new JArray("d1", "d2"), ["as_of"] = "2026-09-24",
                ["deliverables"] = new JArray(new JObject { ["container"] = "HZ01-HRZ-ZZ-XX-M3-A-0001", ["format"] = "ifc" })
            };
            var vars = new Dictionary<string, string> { [CdeCloudTool.OpenCdeTokenName] = IssuedToken };
            JObject r = CdeCloudTool.Handle(args, CancellationToken.None, Env(cloud, vars));

            Assert.False((bool)r["coverage_complete"]);
            Assert.Equal(new[] { "d2" }, r["documents_not_returned"].Select(x => (string)x));
            Assert.True((bool)r["files"][0]["name_compliant"]);
            Assert.Equal("present", (string)r["deliverables"][0]["verdict"]);
            var post = cloud.Requests.Single(q => q.Method == "POST");
            Assert.Equal("Bearer " + IssuedToken, post.Auth);
            Assert.Contains("\"document_ids\"", post.Body);
            // Discovery never carried the token.
            Assert.All(cloud.Requests.Where(q => q.Url.Contains("/foundation/")), q => Assert.Null(q.Auth));
            NoSecretIn(r);
        }

        [Fact]
        public void OpenCde_versions_follows_the_servers_link_by_document_id()
        {
            var cloud = OpenCdeServer();
            string versionsUrl = "https://docs.example.test/documents/1.0/d1/versions";
            cloud.Json("POST", "https://docs.example.test/documents/1.0/document-versions",
                new JObject { ["versions"] = new JArray(Doc("d1", "a.ifc", 2, versionsUrl)) });
            cloud.Json("GET", versionsUrl, new JObject
            {
                ["documents"] = new JArray(Doc("d1", "a.ifc", 1, versionsUrl), Doc("d1", "a.ifc", 2, versionsUrl))
            });
            var args = new JObject { ["operation"] = "versions", ["provider"] = "opencde", ["server_url"] = "https://cde.example.test", ["document_id"] = "d1" };
            var vars = new Dictionary<string, string> { [CdeCloudTool.OpenCdeTokenName] = IssuedToken };
            JObject r = CdeCloudTool.Handle(args, CancellationToken.None, Env(cloud, vars));
            Assert.True((bool)r["coverage_complete"]);
            Assert.Equal(new[] { 2, 1 }, r["versions"].Select(v => (int)v["version_index"]));
        }

        [Fact]
        public void OpenCde_a_versions_link_to_a_foreign_host_is_refused()
        {
            var cloud = OpenCdeServer();
            cloud.Json("POST", "https://docs.example.test/documents/1.0/document-versions",
                new JObject { ["versions"] = new JArray(Doc("d1", "a.ifc", 2, "https://collector.example.test/x")) });
            var args = new JObject { ["operation"] = "versions", ["provider"] = "opencde", ["server_url"] = "https://cde.example.test", ["document_id"] = "d1" };
            var vars = new Dictionary<string, string> { [CdeCloudTool.OpenCdeTokenName] = IssuedToken };
            JObject r = CdeCloudTool.Handle(args, CancellationToken.None, Env(cloud, vars));
            Assert.False((bool)r["coverage_complete"]);
            Assert.DoesNotContain(cloud.Requests, q => q.Url.Contains("collector.example.test"));
        }

        // ---- the contract ------------------------------------------------------------------

        [Fact]
        public void The_contract_declares_a_read_only_open_world_tool_with_external_content()
        {
            CommandContract c = Contract.All.Single(x => x.Name == CdeCloudTool.ToolName);
            Assert.Null(c.Command);
            Assert.Equal(ToolEffect.ReadOnly, c.Effect);
            Assert.True(c.OpenWorld);
            Assert.False(c.Destructive);
            Assert.True(c.ExternalContent);
            Assert.Contains("coordination", c.Toolsets);
            Assert.DoesNotContain("dry_run", ((JObject)c.InputSchema["properties"]).Properties().Select(p => p.Name));
        }
    }
}
