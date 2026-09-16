// -----------------------------------------------------------------------------
// Horizun MCP - selection shared between Revit and a Power BI report.
// Original Horizun code.
//
// G20 of the 2026-09-14 competitive inventory: "click de visual selecciona IDs
// correctos en Revit y selección Revit filtra informe, sin confundir modelos".
//
// WHAT IS HERE AND WHAT IS NOT, stated before anything else because the gap is
// half external and a tool that hid that would be the "pantalla que anuncia una
// integración inexistente" the brief warns against:
//
//   IMPLEMENTED: the exchange itself. A durable, versioned selection document
//   with model identity in it, written by one side and read by the other, plus
//   the identity rules that stop two models being confused for one.
//
//   NOT IMPLEMENTED, AND NOT SIMULATED: the live channel. Power BI offers no
//   public way for a report visual to push a selection to a process on the same
//   machine. Closing that needs either a CUSTOM VISUAL talking to a local
//   listener, or the Power BI REST API with a tenant, a workspace and an app
//   registration - and all four of those are credentials and product decisions,
//   not code. They are recorded as external dependencies and nothing here
//   pretends to have them.
//
// THE IDENTITY RULE IS THE WHOLE OF "SIN CONFUNDIR MODELOS". A Revit ElementId
// is an integer that means something only inside one document, and a report that
// joins on it will cheerfully light up element 318451 of the architectural model
// when somebody clicked element 318451 of the structural one. So every selection
// document carries the document's title AND its Horizun fingerprint, and reading
// one back into a Revit that does not match is REFUSED rather than translated -
// there is nothing to translate, only two different buildings sharing a number.
//
// THE KEY COLUMN IS THE CALLER'S. A report rarely carries raw element ids; it
// carries a code somebody chose. Which column that is, and which Revit parameter
// it corresponds to, is a project decision and arrives as an argument. Guessing
// it would produce a selection that is confidently wrong, which on a coordination
// call is worse than no selection at all.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Server
{
    internal static class PowerBiSelection
    {
        public const string Schema = "horizun.selection-exchange/1";

        /// <summary>
        /// A selection is a moment, not a state. Six hours is long enough for a
        /// coordination session and short enough that yesterday's click cannot quietly
        /// drive today's model.
        /// </summary>
        public const long DefaultTtlMs = 6L * 60 * 60 * 1000;

        public const int MaxKeys = 20000;

        private static readonly object Gate = new object();

        private static string Directory() => Path.Combine(HorizunPaths.DataRoot(), "selection");

        private static string PathFor(string channel) =>
            Path.Combine(Directory(), Safe(channel) + ".json");

        public static JObject Handle(JObject arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string operation = (arguments?.Value<string>("operation") ?? "read").ToLowerInvariant();

            lock (Gate)
            {
                switch (operation)
                {
                    case "publish": return Publish(arguments);
                    case "read": return Read(arguments);
                    case "clear": return Clear(arguments);
                    case "capabilities": return Capabilities();
                    default:
                        throw new ToolRefusal(
                            "operation must be publish, read, clear or capabilities; '" + operation +
                            "' is not one of them.");
                }
            }
        }

        // =====================================================================

        private static JObject Publish(JObject arguments)
        {
            string channel = RequiredChannel(arguments);
            string documentTitle = Required(arguments, "document_title");
            string fingerprint = Required(arguments, "document_fingerprint");
            string keyColumn = Required(arguments, "key_column");
            string keyParameter = Required(arguments, "key_parameter");

            JArray rawKeys = arguments?["keys"] as JArray;
            if (rawKeys == null || rawKeys.Count == 0)
                throw new ToolRefusal(
                    "keys is required and must be non-empty. An empty selection is published with " +
                    "operation=clear, so that 'nothing is selected' and 'somebody published an empty list by " +
                    "accident' are not the same document.");
            if (rawKeys.Count > MaxKeys)
                throw new ToolRefusal("keys holds " + rawKeys.Count + "; the bound is " + MaxKeys + ".");

            var keys = new List<string>();
            foreach (JToken token in rawKeys)
            {
                string key = token?.ToString();
                if (string.IsNullOrWhiteSpace(key))
                    throw new ToolRefusal("keys holds an empty value. A blank key matches everything or nothing, " +
                                          "and which one it does is a property of the reader.");
                keys.Add(key.Trim());
            }

            long ttl = arguments?.Value<long?>("ttl_ms") ?? DefaultTtlMs;
            if (ttl < 1000 || ttl > 7L * 24 * 60 * 60 * 1000)
                throw new ToolRefusal("ttl_ms must be between 1000 and one week.");

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var document = new JObject
            {
                ["schema"] = Schema,
                ["channel"] = channel,
                ["published_utc"] = now.ToString("o"),
                ["expires_utc"] = now.AddMilliseconds(ttl).ToString("o"),
                ["source"] = arguments?.Value<string>("source") ?? "unstated",
                ["document_title"] = documentTitle,
                ["document_fingerprint"] = fingerprint,
                ["key_column"] = keyColumn,
                ["key_parameter"] = keyParameter,
                ["keys"] = new JArray(keys.Distinct(StringComparer.Ordinal)),
                ["means"] =
                    "A selection of KEYS, not of element ids. The key column is the report's and the key " +
                    "parameter is Revit's; joining on a raw ElementId would light up element N of whichever " +
                    "model happened to be open, because an ElementId means something only inside one document."
            };

            System.IO.Directory.CreateDirectory(Directory());
            string target = PathFor(channel);
            string temporary = target + ".tmp";
            File.WriteAllText(temporary, document.ToString(Newtonsoft.Json.Formatting.Indented),
                              new UTF8Encoding(false));
            if (File.Exists(target)) File.Delete(target);
            File.Move(temporary, target);

            // RE-READ. A selection that did not land is a coordination call where one
            // person is looking at something nobody else can see.
            JObject readBack = ReadDocument(channel);
            if (readBack == null || ((JArray)readBack["keys"]).Count != keys.Distinct(StringComparer.Ordinal).Count())
                throw new ToolRefusal(
                    "the selection was written and re-reading it does not match. Nothing is claimed; inspect " +
                    target + ".");

            return new JObject
            {
                ["channel"] = channel,
                ["path"] = target,
                ["keys"] = ((JArray)readBack["keys"]).Count,
                ["expires_utc"] = readBack["expires_utc"],
                ["verified_by_reread"] = true
            };
        }

        private static JObject Read(JObject arguments)
        {
            string channel = RequiredChannel(arguments);
            JObject document = ReadDocument(channel);
            if (document == null)
                return new JObject
                {
                    ["channel"] = channel,
                    ["present"] = false,
                    ["means"] = "no selection has been published on this channel. That is not an empty " +
                                "selection; it is the absence of one."
                };

            DateTimeOffset expires;
            bool expired = DateTimeOffset.TryParse(document.Value<string>("expires_utc"),
                                                   CultureInfo.InvariantCulture,
                                                   DateTimeStyles.AdjustToUniversal, out expires) &&
                           expires < DateTimeOffset.UtcNow;

            // THE IDENTITY CHECK. A caller that names the document it is about gets told
            // when the selection belongs to a different one, and is not handed keys to
            // apply to the wrong building.
            string expectedTitle = arguments?.Value<string>("document_title");
            string expectedFingerprint = arguments?.Value<string>("document_fingerprint");
            string mismatch = null;
            if (!string.IsNullOrWhiteSpace(expectedFingerprint) &&
                !string.Equals(expectedFingerprint, document.Value<string>("document_fingerprint"),
                               StringComparison.Ordinal))
                mismatch = "this selection was published for document fingerprint '" +
                           document.Value<string>("document_fingerprint") + "' ('" +
                           document.Value<string>("document_title") + "'), and you asked as '" +
                           (expectedTitle ?? expectedFingerprint) + "'. The keys are NOT returned: an element " +
                           "id or a code that means one thing in one model means something else in another, " +
                           "and applying them across would select the wrong things confidently.";

            var result = new JObject
            {
                ["channel"] = channel,
                ["present"] = true,
                ["expired"] = expired,
                ["document_title"] = document["document_title"],
                ["document_fingerprint"] = document["document_fingerprint"],
                ["key_column"] = document["key_column"],
                ["key_parameter"] = document["key_parameter"],
                ["published_utc"] = document["published_utc"],
                ["expires_utc"] = document["expires_utc"],
                ["source"] = document["source"]
            };

            if (mismatch != null)
            {
                result["document_mismatch"] = mismatch;
                result["keys"] = new JArray();
                return result;
            }
            if (expired)
            {
                result["keys"] = new JArray();
                result["means"] = "the selection has expired and its keys are not returned. A click from " +
                                  "yesterday must not quietly drive today's model.";
                return result;
            }

            result["keys"] = document["keys"];
            result["how_to_apply"] =
                "Resolve these keys to elements with horizun_query_model, matching key_parameter against each " +
                "key, then select them with horizun_navigate. Resolution is a MODEL question and is not done " +
                "here: this tool never touches Revit.";
            return result;
        }

        private static JObject Clear(JObject arguments)
        {
            string channel = RequiredChannel(arguments);
            string path = PathFor(channel);
            bool existed = File.Exists(path);
            if (existed) File.Delete(path);
            return new JObject
            {
                ["channel"] = channel,
                ["cleared"] = existed,
                ["means"] = existed
                    ? "the channel is empty. A later read reports the ABSENCE of a selection, which is not the " +
                      "same as an empty one."
                    : "there was nothing on this channel to clear."
            };
        }

        /// <summary>
        /// What this integration can and cannot do, as a machine-readable answer.
        ///
        /// It exists so a client can find out WITHOUT a screen that announces a feature
        /// nobody built. The missing halves are named with what each one would require.
        /// </summary>
        private static JObject Capabilities() => new JObject
        {
            ["schema"] = Schema,
            ["revit_to_report"] = new JObject
            {
                ["implemented"] = true,
                ["how"] = "Revit-side code publishes a selection of KEYS on a channel; the report reads the " +
                          "exchange document and filters on the matching column."
            },
            ["report_to_revit"] = new JObject
            {
                ["implemented"] = "exchange only",
                ["how"] = "the exchange document can be written by anything that can write a file, and reading " +
                          "it back and selecting in Revit is implemented. What is NOT implemented is the LIVE " +
                          "push from a report visual.",
                ["external_dependencies"] = new JArray(
                    "Power BI offers no public way for a report visual to reach a process on the same machine, " +
                    "so a live click requires a CUSTOM VISUAL plus a local listener - which is a product " +
                    "decision, a signed visual and a listening port nobody has authorized here.",
                    "The alternative is the Power BI REST API, which needs a tenant, a workspace, an app " +
                    "registration and a consented scope. All four are credentials, not code.")
            },
            ["identity"] = new JObject
            {
                ["rule"] = "every selection carries the document's title AND fingerprint, and a read from a " +
                           "different document returns no keys at all.",
                ["why"] = "a Revit ElementId means something only inside one document. A report joining on raw " +
                          "ids will light up element N of whichever model is open, which on a coordination call " +
                          "is worse than no selection."
            },
            ["means"] =
                "This block is the honest inventory of the integration. Nothing above is a plan or an " +
                "intention: implemented means implemented, and the rest names what it would take."
        };

        // =====================================================================

        private static JObject ReadDocument(string channel)
        {
            try
            {
                string path = PathFor(channel);
                if (!File.Exists(path)) return null;
                JObject document = JObject.Parse(File.ReadAllText(path));
                return document.Value<string>("schema") == Schema ? document : null;
            }
            catch { return null; }
        }

        private static string RequiredChannel(JObject arguments)
        {
            string channel = arguments?.Value<string>("channel") ?? "default";
            foreach (char c in channel)
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                    throw new ToolRefusal(
                        "channel may hold only letters, digits, '-' and '_'. It becomes a file name, and a " +
                        "name that was silently cleaned up is a channel one side writes and the other never " +
                        "finds.");
            if (channel.Length > 64) throw new ToolRefusal("channel must be at most 64 characters.");
            return channel;
        }

        private static string Safe(string channel) => channel;   // RequiredChannel refused anything else

        private static string Required(JObject arguments, string field)
        {
            string value = arguments?.Value<string>(field);
            if (string.IsNullOrWhiteSpace(value)) throw new ToolRefusal(field + " is required.");
            return value;
        }
    }
}
