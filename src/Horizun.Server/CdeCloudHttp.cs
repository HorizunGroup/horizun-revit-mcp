// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// The HTTP half of horizun_cde_cloud: a bounded, READ-ONLY client and the two
// credential resolvers (Autodesk Platform Services and an OpenCDE bearer token).
//
// Rules this file keeps, each one a way a cloud reader lies if it is broken:
//
//   * Credentials never cross tool arguments and never leave through a reply or a
//     log line. They come from fixed environment names or from the user's own token
//     file under the Horizun data root, and they are sent ONLY to hosts the caller
//     cannot redirect: developer.api.autodesk.com for APS, the named OpenCDE server
//     and the Documents API base it itself advertised. A pagination link or a
//     server-provided URL pointing anywhere else is refused before the request.
//   * A call budget is a hard stop, not a hint. When it is spent the reader stops
//     and says coverage is incomplete; it never pretends the rest was empty.
//   * 429 and transient 5xx are retried with backoff (Retry-After honoured, capped);
//     401/403 are not retried - the answer will not change - and are reported.
//   * An error body is never echoed: it may carry tenant data.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    /// <summary>What horizun_cde_cloud reaches outside itself through. Tests replace every member.</summary>
    internal sealed class CdeCloudEnvironment
    {
        public HttpMessageHandler Handler { get; set; }
        public Func<string, string> Variable = Environment.GetEnvironmentVariable;
        public Action<TimeSpan, CancellationToken> Sleep = (d, ct) =>
        {
            ct.WaitHandle.WaitOne(d);
            ct.ThrowIfCancellationRequested();
        };
        public Func<DateTimeOffset> Now = () => DateTimeOffset.UtcNow;
        /// <summary>The 3-legged token file; null means the default under the Horizun data root.</summary>
        public string TokenFilePath { get; set; }

        public string ResolvedTokenFile() => TokenFilePath ?? Path.Combine(HorizunPaths.DataRoot(), "aps-token.json");
    }

    internal sealed class CloudResponse
    {
        public int Status;
        public JObject Body;
        public string Error;
        public bool BudgetExhausted;
        public bool Ok => Error == null && Body != null;
    }

    internal sealed class CdeCloudHttp : IDisposable
    {
        internal const int MaxRetries = 4;
        internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

        private readonly HttpClient _client;
        private readonly CdeCloudEnvironment _env;
        private readonly CancellationToken _ct;
        private readonly HashSet<string> _hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly JArray _errors = new JArray();

        public int Budget { get; }
        public int Calls { get; private set; }
        public int Retries { get; private set; }
        public bool BudgetExhausted { get; private set; }
        public string BearerToken { get; set; }

        public CdeCloudHttp(CdeCloudEnvironment env, int budget, CancellationToken ct)
        {
            _env = env;
            _ct = ct;
            Budget = budget;
            _client = env.Handler != null
                ? new HttpClient(env.Handler, false) { Timeout = TimeSpan.FromSeconds(90) }
                : new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        }

        public void Dispose() => _client.Dispose();

        /// <summary>A host that may receive requests (and, for the credentialed ones, the bearer token).</summary>
        public void AllowHost(string host) { if (!string.IsNullOrEmpty(host)) _hosts.Add(host); }

        public JObject Describe() => new JObject
        {
            ["calls"] = Calls, ["retries"] = Retries, ["budget"] = Budget, ["budget_exhausted"] = BudgetExhausted,
            ["errors"] = _errors.DeepClone()
        };

        public CloudResponse Get(string url, bool withToken = true) =>
            Send(HttpMethod.Get, url, null, withToken ? Bearer() : null);

        public CloudResponse PostJson(string url, JObject body) =>
            Send(HttpMethod.Post, url, () => new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"), Bearer());

        /// <summary>
        /// A token request. Never carries the bearer; the client credential goes in a Basic
        /// header (or, for a public client, only the id in the form). A failure is a refusal
        /// that names the status and nothing else.
        /// </summary>
        public JObject PostForm(string url, Dictionary<string, string> form, string basic)
        {
            CloudResponse r = Send(HttpMethod.Post, url, () => new FormUrlEncodedContent(form),
                basic == null ? null : new AuthenticationHeaderValue("Basic", basic));
            if (!r.Ok)
                throw new ToolRefusal("The token request failed: " + r.Error + ". The response body and the credentials are not " +
                                      "echoed. No project data was read.");
            return r.Body;
        }

        private AuthenticationHeaderValue Bearer() =>
            BearerToken == null ? null : new AuthenticationHeaderValue("Bearer", BearerToken);

        private CloudResponse Send(HttpMethod method, string url, Func<HttpContent> content, AuthenticationHeaderValue auth)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                return Fail(url, 0, "not an absolute https URL; refused before any request");
            if (!_hosts.Contains(uri.Host))
                return Fail(uri.AbsolutePath, 0, "host '" + uri.Host + "' is not one this reader may call; refused before any request " +
                                                 "(credentials are only sent to the provider's own hosts)");
            for (int attempt = 0; ; attempt++)
            {
                _ct.ThrowIfCancellationRequested();
                if (Calls >= Budget)
                {
                    BudgetExhausted = true;
                    var spent = Fail(uri.AbsolutePath, 0, "the call budget (max_calls=" + Budget + ") is spent; not read");
                    spent.BudgetExhausted = true;
                    return spent;
                }
                Calls++;
                HttpResponseMessage response = null;
                string transport = null;
                try
                {
                    using (var message = new HttpRequestMessage(method, uri))
                    {
                        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        if (auth != null) message.Headers.Authorization = auth;
                        if (content != null) message.Content = content();
                        response = _client.SendAsync(message, _ct).GetAwaiter().GetResult();
                    }
                }
                catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is IOException)
                {
                    transport = ex.GetType().Name;
                }

                if (transport != null)
                {
                    if (attempt < MaxRetries) { Backoff(attempt, null); continue; }
                    return Fail(uri.AbsolutePath, 0, "no HTTP answer after " + (attempt + 1) + " attempts (" + transport + ")");
                }

                using (response)
                {
                    int status = (int)response.StatusCode;
                    if (status == 429 || status == 502 || status == 503 || status == 504)
                    {
                        if (attempt < MaxRetries) { Backoff(attempt, RetryAfter(response)); continue; }
                        return Fail(uri.AbsolutePath, status, "HTTP " + status + " after " + (attempt + 1) + " attempts");
                    }
                    if (status == 401 || status == 403)
                        return Fail(uri.AbsolutePath, status, "HTTP " + status + (status == 401
                            ? ": the credential was not accepted (expired or wrong)"
                            : ": the credential has no access to this resource") + "; not retried");
                    if (!response.IsSuccessStatusCode)
                        return Fail(uri.AbsolutePath, status, "HTTP " + status + "; the body is not echoed");
                    string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    try
                    {
                        using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                        {
                            var o = JToken.ReadFrom(reader) as JObject;
                            if (o == null) return Fail(uri.AbsolutePath, status, "a successful answer that is not a JSON object");
                            return new CloudResponse { Status = status, Body = o };
                        }
                    }
                    catch (JsonException) { return Fail(uri.AbsolutePath, status, "a successful answer that is not JSON"); }
                }
            }
        }

        private void Backoff(int attempt, TimeSpan? retryAfter)
        {
            Retries++;
            TimeSpan delay = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > MaxBackoff) delay = MaxBackoff;
            _env.Sleep(delay, _ct);
        }

        private TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            RetryConditionHeaderValue ra = response.Headers.RetryAfter;
            if (ra == null) return null;
            if (ra.Delta.HasValue) return ra.Delta.Value;
            if (ra.Date.HasValue) return ra.Date.Value - _env.Now();
            return null;
        }

        private CloudResponse Fail(string where, int status, string reason)
        {
            _errors.Add(new JObject { ["path"] = where, ["status"] = status == 0 ? JValue.CreateNull() : (JToken)status, ["reason"] = reason });
            return new CloudResponse { Status = status, Error = reason };
        }
    }

    /// <summary>A resolved credential: how it was obtained, never what it is.</summary>
    internal sealed class CloudCredential
    {
        public string Mode;       // access_token | three_legged | two_legged
        public string Source;     // the environment name or the token file path - never the value
        public string Token;
        public JArray Warnings = new JArray();
        public bool TokenFileRefreshed;

        public JObject Describe() => new JObject
        {
            ["mode"] = Mode, ["source"] = Source, ["token_file_refreshed"] = TokenFileRefreshed, ["warnings"] = Warnings
        };
    }

    internal static class ApsAuth
    {
        internal const string Host = "developer.api.autodesk.com";
        internal const string TokenUrl = "https://developer.api.autodesk.com/authentication/v2/token";
        internal const string Scope = "data:read";

        internal static readonly string[] ClientIdNames = { "HORIZUN_APS_CLIENT_ID", "APS_CLIENT_ID" };
        internal static readonly string[] ClientSecretNames = { "HORIZUN_APS_CLIENT_SECRET", "APS_CLIENT_SECRET" };
        internal const string AccessTokenName = "HORIZUN_APS_ACCESS_TOKEN";

        /// <summary>
        /// Is ANY credential configured? Decided without a single request, so a machine
        /// with nothing configured is refused with zero calls.
        /// </summary>
        internal static bool AnyConfigured(CdeCloudEnvironment env)
        {
            if (!string.IsNullOrWhiteSpace(env.Variable(AccessTokenName))) return true;
            if (File.Exists(env.ResolvedTokenFile())) return true;
            return FirstSet(env, ClientIdNames) != null && FirstSet(env, ClientSecretNames) != null;
        }

        internal static string NotConfiguredMessage(CdeCloudEnvironment env) =>
            "Autodesk Platform Services credentials are not configured. Set " + AccessTokenName + " (a short-lived token), " +
            "or keep a 3-legged token file at " + env.ResolvedTokenFile() + " ({access_token, refresh_token, expires_at}), " +
            "or set HORIZUN_APS_CLIENT_ID and HORIZUN_APS_CLIENT_SECRET (APS_CLIENT_ID/APS_CLIENT_SECRET are also read) " +
            "for a 2-legged token, in the MCP server's environment. Credentials are never accepted in tool arguments or in " +
            "project-context.json. No request was made.";

        private static string FirstSet(CdeCloudEnvironment env, string[] names)
        {
            foreach (string n in names)
                if (!string.IsNullOrWhiteSpace(env.Variable(n))) return n;
            return null;
        }

        /// <summary>
        /// Resolves a data:read token. Order: an explicit access token, then the 3-legged
        /// token file (refreshed when it expires and a client id is configured - APS
        /// refresh tokens are replaced on use, so the new pair is written back and read
        /// back), then 2-legged client credentials. Throws ToolRefusal on failure.
        /// </summary>
        internal static CloudCredential Resolve(CdeCloudEnvironment env, CdeCloudHttp http)
        {
            string direct = env.Variable(AccessTokenName);
            if (!string.IsNullOrWhiteSpace(direct))
                return new CloudCredential { Mode = "access_token", Source = AccessTokenName, Token = direct.Trim() };

            string file = env.ResolvedTokenFile();
            if (File.Exists(file)) return FromTokenFile(env, http, file);

            string idName = FirstSet(env, ClientIdNames), secretName = FirstSet(env, ClientSecretNames);
            if (idName == null || secretName == null) throw new ToolRefusal(NotConfiguredMessage(env));
            JObject token = RequestToken(http, env.Variable(idName).Trim(), env.Variable(secretName).Trim(),
                new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = Scope });
            var cred = new CloudCredential { Mode = "two_legged", Source = idName + "/" + secretName, Token = (string)token["access_token"] };
            cred.Warnings.Add("A 2-legged token sees only what the APS app itself was provisioned for in the account " +
                              "(an ACC custom integration); a folder it cannot see is reported as not covered, not as empty.");
            return cred;
        }

        private static CloudCredential FromTokenFile(CdeCloudEnvironment env, CdeCloudHttp http, string file)
        {
            JObject stored;
            try { stored = JObject.Parse(File.ReadAllText(file, Encoding.UTF8)); }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new ToolRefusal("The APS token file " + file + " could not be read as JSON (" + ex.GetType().Name + "). No request was made.");
            }
            string access = (string)stored["access_token"];
            string refresh = (string)stored["refresh_token"];
            DateTimeOffset? expires = ParseExpiry(stored["expires_at"]);
            DateTimeOffset now = env.Now();
            if (!string.IsNullOrWhiteSpace(access) && expires.HasValue && expires.Value > now.AddSeconds(60))
                return new CloudCredential { Mode = "three_legged", Source = file, Token = access };

            string idName = FirstSet(env, ClientIdNames);
            if (string.IsNullOrWhiteSpace(refresh) || idName == null)
                throw new ToolRefusal("The APS token in " + file + " is expired or has no expires_at, and it cannot be refreshed " +
                                      "(" + (string.IsNullOrWhiteSpace(refresh) ? "no refresh_token in the file" : "HORIZUN_APS_CLIENT_ID is not set") +
                                      "). Sign in again to renew the file. No request was made.");
            string secretName = FirstSet(env, ClientSecretNames);
            JObject token = RequestToken(http, env.Variable(idName).Trim(), secretName == null ? null : env.Variable(secretName).Trim(),
                new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["scope"] = Scope });

            var cred = new CloudCredential { Mode = "three_legged", Source = file, Token = (string)token["access_token"] };
            int expiresIn = token["expires_in"] != null && token["expires_in"].Type == JTokenType.Integer ? (int)token["expires_in"] : 3600;
            var updated = new JObject
            {
                ["access_token"] = cred.Token,
                ["refresh_token"] = (string)token["refresh_token"] ?? refresh,
                ["expires_at"] = now.AddSeconds(expiresIn).ToString("o", CultureInfo.InvariantCulture),
                ["token_type"] = (string)token["token_type"] ?? "Bearer",
                ["refreshed_by"] = CdeCloudTool.ToolName
            };
            string problem = WriteBackVerified(file, updated);
            cred.TokenFileRefreshed = problem == null;
            if (problem != null)
                cred.Warnings.Add("The token was refreshed but the token file could not be rewritten (" + problem + "). APS " +
                                  "replaces a refresh token when it is used, so the stored one may no longer work: sign in again.");
            return cred;
        }

        private static DateTimeOffset? ParseExpiry(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer) return DateTimeOffset.FromUnixTimeSeconds((long)t);
            if (t.Type == JTokenType.Date)
            {
                object v = ((JValue)t).Value;
                if (v is DateTimeOffset dto) return dto;
                if (v is DateTime dt) return new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime());
            }
            DateTimeOffset d;
            if (t.Type == JTokenType.String &&
                DateTimeOffset.TryParse((string)t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out d)) return d;
            return null;
        }

        private static string WriteBackVerified(string file, JObject content)
        {
            string tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string text = content.ToString(Formatting.Indented);
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                File.Move(tmp, file, true);
                JObject back = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
                return (string)back["access_token"] == (string)content["access_token"] ? null : "the re-read file does not hold the new token";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return ex.GetType().Name;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>POST to the APS token endpoint. Counted against the call budget like any request.</summary>
        private static JObject RequestToken(CdeCloudHttp http, string clientId, string clientSecret, Dictionary<string, string> form)
        {
            if (clientSecret == null) form["client_id"] = clientId;
            JObject parsed = http.PostForm(TokenUrl, form,
                clientSecret == null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + clientSecret)));
            string access = (string)parsed["access_token"];
            if (string.IsNullOrWhiteSpace(access))
                throw new ToolRefusal("The APS token endpoint answered without access_token. No data was read.");
            return parsed;
        }
    }
}
