// -----------------------------------------------------------------------------
// Horizun MCP server - the error codes, named. Original Horizun code.
//
// 2026-07-28 partitioned the JSON-RPC implementation-defined range and renumbered
// the codes it had introduced in draft: HeaderMismatch moved -32001 -> -32020,
// MissingRequiredClientCapability -32003 -> -32021, UnsupportedProtocolVersion
// -32004 -> -32022, and "resource not found" stopped being -32002 and became
// -32602 (Invalid params).
//
// WHY A FILE AND NOT FIVE LITERALS. The renumbering is exactly the kind of change
// that half-lands: one call site updated, one missed, and the client sees two
// different codes for the same condition depending on which path it took. The
// numbers also carry a rule - a modern implementation MUST NOT emit -32002, and a
// legacy one SHOULD still be able to - so the choice is per-era and belongs next
// to the era, not inside a handler.
//
// -32000..-32019 is legacy and closed to new allocations. -32020..-32099 belongs
// to the spec: nothing Horizun invents may live there. Anything Horizun-specific
// that ever needs a code goes OUTSIDE -32768..-32000, as the policy says.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Server.Protocol
{
    internal static class McpErrorCodes
    {
        // ---- JSON-RPC 2.0, unchanged in every revision -----------------------------
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        // ---- MCP, this revision ----------------------------------------------------
        public const int HeaderMismatch = -32020;
        public const int MissingRequiredClientCapability = -32021;
        public const int UnsupportedProtocolVersion = -32022;

        /// <summary>
        /// Resource-not-found as 2025-11-25 and earlier spelled it. A MODERN reply must
        /// never carry this; a legacy one still may, and clients are told to keep
        /// accepting it from older servers.
        /// </summary>
        public const int LegacyResourceNotFound = -32002;

        /// <summary>Cancellation, as the SDKs have long used it. Unchanged.</summary>
        public const int RequestCancelled = -32800;

        /// <summary>
        /// Which code "that resource does not exist" gets, decided by the era rather
        /// than by whichever handler noticed. Modern: Invalid params. Legacy: -32002.
        /// </summary>
        public static int ResourceNotFound(McpEra era) =>
            era == McpEra.Modern ? InvalidParams : LegacyResourceNotFound;

        /// <summary>
        /// The data block of an UnsupportedProtocolVersionError. The spec shows both
        /// fields and the client needs them: without `supported` it has nothing to
        /// retry with, and a retry loop against a server that cannot say what it speaks
        /// is the failure this error exists to prevent.
        /// </summary>
        public static JObject UnsupportedProtocolVersionData(string requested)
        {
            var supported = new JArray();
            foreach (string r in McpRevision.All) supported.Add(r);
            return new JObject
            {
                ["supported"] = supported,
                ["requested"] = requested == null ? (JToken)JValue.CreateNull() : requested
            };
        }

        /// <summary>
        /// The data for a request that declared NO protocol version at all.
        ///
        /// Not an UnsupportedProtocolVersionError: that type's `requested` is a string, and
        /// there is no requested version here. It is a malformed request - and the list of
        /// versions still travels, because the one caller that hits this path is a
        /// backward-compatibility probe, and a probe that learns nothing has not probed.
        /// </summary>
        public static JObject MissingProtocolVersionData(string field)
        {
            var supported = new JArray();
            foreach (string r in McpRevision.All) supported.Add(r);
            JObject data = MalformedMetadata(field, "one of the protocol versions in 'supported'");
            data["supported"] = supported;
            return data;
        }

        /// <summary>
        /// The data block of a MissingRequiredClientCapabilityError.
        ///
        /// `requiredCapabilities` is typed `ClientCapabilities` in the schema - an OBJECT
        /// with the optional members experimental, roots, sampling, elicitation and
        /// extensions - not a list of names. It is not decoration either: it is how the
        /// client learns WHAT TO DECLARE, and the shape matters because the client is
        /// expected to merge it into its own capabilities and resend. A dotted path inside
        /// a string is something it would have to parse, which is guessing with extra
        /// steps.
        /// </summary>
        public static JObject RequiredCapabilities(JObject capabilities) => new JObject
        {
            ["requiredCapabilities"] = capabilities ?? new JObject()
        };

        /// <summary>
        /// The data for a request that needs an EXTENSION the client did not declare.
        ///
        /// Extensions live in a map keyed by the extension identifier, whose value is that
        /// extension's settings object; empty means "supported, no settings". This is the
        /// exact object the client can merge into
        /// _meta['io.modelcontextprotocol/clientCapabilities'] and send again.
        /// </summary>
        public static JObject RequiredExtension(string extensionId, JObject settings = null) =>
            RequiredCapabilities(new JObject
            {
                ["extensions"] = new JObject
                {
                    [extensionId ?? ""] = settings ?? new JObject()
                }
            });

        /// <summary>
        /// The data for a request whose protocol metadata is MALFORMED - a required `_meta`
        /// field that is absent, or present with the wrong type.
        ///
        /// DELIBERATELY NOT A requiredCapabilities BLOCK. The spec is explicit that a
        /// request missing a required field is malformed and gets -32602; the client's
        /// problem is the field, not a capability it lacks, and telling it to go and
        /// declare a capability sends it to fix something that is not broken.
        /// </summary>
        public static JObject MalformedMetadata(string field, string expected) => new JObject
        {
            ["field"] = field,
            ["expected"] = expected,
            ["means"] = "this is a malformed request, not a missing capability. The named `_meta` field is " +
                        "required on every request in this revision. Nothing was done."
        };
    }

    /// <summary>
    /// An McpError that also carries a JSON-RPC `data` member. The plain McpError has
    /// only a code and a message, and three of the codes above are useless without their
    /// data - the client is meant to READ `supported` and retry, not parse English.
    /// </summary>
    internal sealed class McpDataError : System.Exception
    {
        public int Code { get; }

        /// <summary>
        /// The JSON-RPC `data` member. NOT called Data: System.Exception already has a Data
        /// property - an IDictionary for arbitrary annotations - and a second member of the same
        /// name on a subclass reads as the same thing at every call site while being a different
        /// one. The compiler says so (CS0114) and it is right; `new` would silence the warning and
        /// keep the hazard.
        /// </summary>
        public JObject ErrorData { get; }

        public McpDataError(int code, string message, JObject data) : base(message)
        {
            Code = code;
            ErrorData = data;
        }
    }
}
