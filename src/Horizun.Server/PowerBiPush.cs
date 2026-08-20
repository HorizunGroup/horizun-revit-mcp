// -----------------------------------------------------------------------------
// Horizun MCP - direct, bounded Power BI push semantic-model ingestion.
//
// Credentials never cross MCP arguments. The server reads either a short-lived
// access token or Entra service-principal settings from fixed environment names,
// sends only to Microsoft-owned fixed endpoints, and claims a durable idempotency
// key before the request. A lost HTTP answer becomes in-doubt and is not retried.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Server
{
    internal static class PowerBiPush
    {
        private const string ToolName = "horizun_power_bi_push";
        private const int MaxRows = 10000;
        private const int MaxColumns = 75;
        private const int MaxStringChars = 4000;
        private const int MaxPayloadBytes = 8 * 1024 * 1024;

        public static JObject Handle(JObject request) => Handle(request, CancellationToken.None);

        public static JObject Handle(JObject request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) })
                return Handle(request, client,
                    new DurableCommandLedger(retentionLog: message => Log.Info(message)),
                    Environment.GetEnvironmentVariable, cancellationToken);
        }

        internal static JObject Handle(JObject request, HttpClient client, DurableCommandLedger ledger,
                                       Func<string, string> environment) =>
            Handle(request, client, ledger, environment, CancellationToken.None);

        internal static JObject Handle(JObject request, HttpClient client, DurableCommandLedger ledger,
                                       Func<string, string> environment, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request = request ?? new JObject();
            string datasetId = GuidField(request, "dataset_id", true);
            string workspaceId = GuidField(request, "workspace_id", false);
            string table = request.Value<string>("table");
            if (string.IsNullOrWhiteSpace(table) || table.Length > 512)
                throw new ToolRefusal("table is required and cannot exceed 512 characters.");

            JArray rows = request["rows"] as JArray;
            ValidateRows(rows);
            string payload = new JObject { ["rows"] = rows.DeepClone() }.ToString(Formatting.None);
            int payloadBytes = Encoding.UTF8.GetByteCount(payload);
            if (payloadBytes > MaxPayloadBytes)
                throw new ToolRefusal("The serialized Power BI payload is " + payloadBytes +
                    " bytes; the Horizun safety limit is " + MaxPayloadBytes + ". Split it into smaller calls.");

            string authMode = AuthMode(environment);
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string endpoint = workspaceId == null
                ? "https://api.powerbi.com/v1.0/myorg/datasets/" + datasetId + "/tables/" + Uri.EscapeDataString(table) + "/rows"
                : "https://api.powerbi.com/v1.0/myorg/groups/" + workspaceId + "/datasets/" + datasetId +
                  "/tables/" + Uri.EscapeDataString(table) + "/rows";

            if (dryRun)
                return new JObject
                {
                    ["dry_run"] = true,
                    ["destination"] = new JObject
                    {
                        ["service"] = "Power BI",
                        ["workspace_id"] = workspaceId == null ? JValue.CreateNull() : new JValue(workspaceId),
                        ["dataset_id"] = datasetId,
                        ["table"] = table
                    },
                    ["rows_validated"] = rows.Count,
                    ["payload_bytes"] = payloadBytes,
                    ["auth_mode"] = authMode,
                    ["credentials_configured"] = authMode != "not_configured",
                    ["limits_enforced"] = new JObject
                    {
                        ["rows_per_request"] = MaxRows,
                        ["columns"] = MaxColumns,
                        ["string_characters"] = MaxStringChars,
                        ["payload_bytes"] = MaxPayloadBytes
                    },
                    ["note"] = "No token was requested and no row was sent. Apply with dry_run=false and a new idempotency_key."
                };

            string key = request.Value<string>("idempotency_key");
            if (string.IsNullOrWhiteSpace(key))
                throw new ToolRefusal("idempotency_key is required when dry_run=false. Use a new UUID for deliberate work and keep it only for retries.");

            string fingerprint = RequestFingerprint.OfOperation(ToolName,
                "powerbi:" + (workspaceId ?? "myorg") + ":" + datasetId, request, "idempotency_key");
            DurableCommandDecision decision = ledger.Claim(key, ToolName, fingerprint);
            if (decision.Outcome == DurableCommandOutcome.Replay)
                return Replay(decision.ReplayResult);
            if (!decision.IsFresh)
                throw new ToolRefusal(decision.Message);

            if (authMode == "not_configured")
            {
                string error = "Power BI authentication is not configured. Set HORIZUN_POWER_BI_ACCESS_TOKEN, " +
                    "or set HORIZUN_POWER_BI_TENANT_ID, HORIZUN_POWER_BI_CLIENT_ID and HORIZUN_POWER_BI_CLIENT_SECRET " +
                    "in the MCP server environment. Credentials are never accepted in tool arguments.";
                ledger.Complete(decision, CommandResult.Fail(error));
                throw new ToolRefusal(error);
            }

            // Claim first so a completed retry can replay even if credentials were later
            // rotated or removed. Token acquisition cannot insert rows; any failure here
            // is therefore safe to record as a terminal failure rather than in-doubt.
            string token;
            try { token = ResolveAccessToken(client, environment, authMode, cancellationToken); }
            catch (Exception ex)
            {
                string error = ex is ToolRefusal ? ex.Message :
                    "Power BI authentication failed before row delivery (" + ex.GetType().Name + "). No rows were sent.";
                ledger.Complete(decision, CommandResult.Fail(error));
                throw new ToolRefusal(error);
            }

            HttpResponseMessage response;
            try
            {
                using (var message = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    cancellationToken.ThrowIfCancellationRequested();
                    response = client.SendAsync(message, cancellationToken).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                // Do not append completion. Microsoft may have received the rows even though
                // this process lost the answer. A retry with this key will fail closed.
                throw new ToolRefusal("Power BI delivery ended without a trustworthy HTTP response (" +
                    ex.GetType().Name + "). The durable key is now in_doubt and Horizun will not send these rows again " +
                    "automatically. Inspect the destination before deliberately choosing a new key.");
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    string error = "Power BI returned HTTP " + status + " " + response.ReasonPhrase +
                        ". No success is claimed; the response body is intentionally not echoed because it may contain tenant data.";
                    CommandResult failed = CommandResult.Fail(error);
                    ledger.Complete(decision, failed);
                    throw new ToolRefusal(error);
                }

                var result = new JObject
                {
                    ["dry_run"] = false,
                    ["delivery"] = "accepted_by_power_bi",
                    ["http_status"] = status,
                    ["workspace_id"] = workspaceId == null ? JValue.CreateNull() : new JValue(workspaceId),
                    ["dataset_id"] = datasetId,
                    ["table"] = table,
                    ["rows_sent"] = rows.Count,
                    ["payload_bytes"] = payloadBytes,
                    ["auth_mode"] = authMode,
                    ["idempotency_key"] = key,
                    ["note"] = "Microsoft returned a successful HTTP status. A retry with the same key replays this record and sends nothing."
                };
                ledger.Complete(decision, CommandResult.Ok(result));
                return result;
            }
        }

        private static void ValidateRows(JArray rows)
        {
            if (rows == null || rows.Count < 1 || rows.Count > MaxRows)
                throw new ToolRefusal("rows must contain 1.." + MaxRows + " objects.");
            var allColumns = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                if (!(rows[i] is JObject row)) throw new ToolRefusal("rows[" + i + "] must be an object.");
                if (row.Count == 0 || row.Count > MaxColumns)
                    throw new ToolRefusal("rows[" + i + "] must contain 1.." + MaxColumns + " columns.");
                foreach (JProperty property in row.Properties())
                {
                    if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 512)
                        throw new ToolRefusal("rows[" + i + "] contains an empty or overlong column name.");
                    allColumns.Add(property.Name);
                    JToken value = property.Value;
                    if (value.Type != JTokenType.Null && value.Type != JTokenType.String &&
                        value.Type != JTokenType.Integer && value.Type != JTokenType.Float &&
                        value.Type != JTokenType.Boolean)
                        throw new ToolRefusal("rows[" + i + "]." + property.Name +
                            " must be string, number, boolean or null; nested objects/arrays are refused.");
                    if (value.Type == JTokenType.String && value.Value<string>().Length > MaxStringChars)
                        throw new ToolRefusal("rows[" + i + "]." + property.Name + " exceeds " + MaxStringChars + " characters.");
                    if (value.Type == JTokenType.Float)
                    {
                        double number = value.Value<double>();
                        if (double.IsNaN(number) || double.IsInfinity(number))
                            throw new ToolRefusal("rows[" + i + "]." + property.Name + " must be a finite JSON number.");
                    }
                }
            }
            if (allColumns.Count > MaxColumns)
                throw new ToolRefusal("The union of row columns is " + allColumns.Count + "; Power BI push models support at most " + MaxColumns + ".");
        }

        private static string GuidField(JObject request, string field, bool required)
        {
            string raw = request.Value<string>(field);
            if (string.IsNullOrWhiteSpace(raw))
            {
                if (required) throw new ToolRefusal(field + " is required and must be a GUID.");
                return null;
            }
            if (!Guid.TryParse(raw, out Guid parsed)) throw new ToolRefusal(field + " must be a GUID.");
            return parsed.ToString("D");
        }

        private static string AuthMode(Func<string, string> environment)
        {
            if (!string.IsNullOrWhiteSpace(environment("HORIZUN_POWER_BI_ACCESS_TOKEN"))) return "access_token";
            if (!string.IsNullOrWhiteSpace(environment("HORIZUN_POWER_BI_TENANT_ID")) &&
                !string.IsNullOrWhiteSpace(environment("HORIZUN_POWER_BI_CLIENT_ID")) &&
                !string.IsNullOrWhiteSpace(environment("HORIZUN_POWER_BI_CLIENT_SECRET"))) return "service_principal";
            return "not_configured";
        }

        private static string ResolveAccessToken(HttpClient client, Func<string, string> environment, string authMode,
                                                 CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (authMode == "access_token") return environment("HORIZUN_POWER_BI_ACCESS_TOKEN").Trim();
            string tenant = environment("HORIZUN_POWER_BI_TENANT_ID");
            string clientId = environment("HORIZUN_POWER_BI_CLIENT_ID");
            string secret = environment("HORIZUN_POWER_BI_CLIENT_SECRET");
            if (!Guid.TryParse(tenant, out Guid tenantGuid) || !Guid.TryParse(clientId, out Guid clientGuid))
                throw new ToolRefusal("HORIZUN_POWER_BI_TENANT_ID and HORIZUN_POWER_BI_CLIENT_ID must be GUIDs.");
            string endpoint = "https://login.microsoftonline.com/" + tenantGuid.ToString("D") + "/oauth2/v2.0/token";
            using (var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientGuid.ToString("D"),
                ["client_secret"] = secret,
                ["scope"] = "https://analysis.windows.net/powerbi/api/.default",
                ["grant_type"] = "client_credentials"
            }))
            using (HttpResponseMessage response = client.PostAsync(endpoint, content, cancellationToken).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                    throw new ToolRefusal("Microsoft Entra token acquisition returned HTTP " + (int)response.StatusCode +
                        " " + response.ReasonPhrase + ". The response body and credentials are not logged.");
                string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                JObject parsed;
                try { parsed = JObject.Parse(json); }
                catch { throw new ToolRefusal("Microsoft Entra returned a successful status but not a JSON token response."); }
                string token = parsed.Value<string>("access_token");
                if (string.IsNullOrWhiteSpace(token))
                    throw new ToolRefusal("Microsoft Entra returned a successful status without access_token.");
                return token;
            }
        }

        private static JObject Replay(CommandResult result)
        {
            if (result == null) throw new ToolRefusal("The durable replay record had no result.");
            if (!result.Success) throw new ToolRefusal(result.Error ?? "The recorded Power BI operation failed.");
            if (result.Data is JObject value) return (JObject)value.DeepClone();
            if (result.Data is JToken token) return new JObject { ["result"] = token.DeepClone(), ["replayed"] = true };
            return JObject.FromObject(result.Data ?? new object());
        }
    }
}
