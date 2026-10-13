// -----------------------------------------------------------------------------
// Horizun MCP server — original Horizun code.
//
// The tool table: the single place that declares which MCP tools exist, the
// plugin command each forwards to, and the JSON schema the client sees. It lives
// server-side on purpose — an MCP client calls tools/list at startup, often
// before Revit is even running, so the schemas cannot depend on reaching the
// plugin. As the deep tools are ported onto the plugin, they get one row here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal sealed class ToolDef
    {
        public string Name;
        public string Command;      // plugin command to forward to (null for a host-resident tool)
        public string Description;
        public JObject InputSchema;
        public JObject OutputSchema;
        public Horizun.Contracts.ToolEffect Effect;
        public bool Destructive;
        public bool OpenWorld;

        /// <summary>The toolsets this tool belongs to, as declared in the contract.</summary>
        public string[] Toolsets;

        /// <summary>Its replies carry text authored outside this bridge (model, file, job).</summary>
        public bool ExternalContent;

        // A host-resident tool answers inside the server and never touches Revit. When Host
        // is non-null the server invokes it locally and does NOT forward to the plugin; when
        // it is null the tool forwards to Command over the pipe, exactly as before.
        public Func<JObject, CancellationToken, JObject> Host;
    }

    internal static class Tools
    {
        // The tool table is BUILT from the shared contract, never declared here. It used
        // to be declared in this file and restated in every command, and the two copies
        // drifted twice in one afternoon - a parameter added on one side only, a
        // description updated on one side only. Neither drift was detectable, because
        // the copies never met. Now there is one copy and this binds the server-only
        // half to it: which host function answers a tool that never reaches Revit.
        private static readonly Dictionary<string, Func<JObject, CancellationToken, JObject>> Hosts =
            new Dictionary<string, Func<JObject, CancellationToken, JObject>>(StringComparer.Ordinal)
            {
                { "horizun_job_status",       (a, ct) => JobStatus.Handle(a, ct) },
                { "horizun_catalog_lookup",   (a, ct) => CatalogLookup.Handle(a, ct) },
                { "horizun_project_context",  (a, ct) => ProjectContext.Handle(a, ct) },
                { "horizun_information_container", (a, ct) => InformationContainerTool.Handle(a, ct) },
                { "horizun_exchange",         (a, ct) => ExchangeTool.Handle(a, ct) },
                { "horizun_cde_cloud",        (a, ct) => CdeCloudTool.Handle(a, ct) },
                { "horizun_excel_write_rows", (a, ct) => ExcelWriteRows.Handle(a, ct) },
                { "horizun_excel_read_rows",  (a, ct) => { ct.ThrowIfCancellationRequested(); return ExcelReadRows.Handle(a); } },
                { "horizun_read_schedule",    (a, ct) => ReadScheduleTool.Handle(a, ct) },
                { "horizun_power_bi_push",    (a, ct) => PowerBiPush.Handle(a, ct) },
                { "horizun_budget_compare",   (a, ct) => BudgetCompare.Handle(a, ct) },
                { "horizun_repair_memory",    (a, ct) => RepairMemoryTool.Handle(a, ct) },
                { "horizun_promote_script",   (a, ct) => ScriptPromotionTool.Handle(a, ct) },
                { "horizun_selection_exchange", (a, ct) => PowerBiSelection.Handle(a, ct) },
                { "horizun_target",           (a, ct) => { ct.ThrowIfCancellationRequested(); return Targets.Handle(a); } },
                { "horizun_run_procedure",    (a, ct) => ProcedureRun.Handle(a, ct) }
            };

        /// <summary>
        /// Every tool this server publishes. Internal rather than private so the tests
        /// can hold the published surface against the contract directly, instead of
        /// re-deriving it and comparing two derivations.
        /// </summary>
        internal static readonly List<ToolDef> All = Build();

        private static List<ToolDef> Build()
        {
            var list = new List<ToolDef>();
            foreach (Horizun.Contracts.CommandContract c in Horizun.Contracts.Contract.All)
            {
                Func<JObject, CancellationToken, JObject> host;
                Hosts.TryGetValue(c.Name, out host);

                // A contract with no plugin command and no host function would be a tool
                // that exists and can never answer. Better to know at startup.
                if (string.IsNullOrEmpty(c.Command) && host == null)
                    throw new InvalidOperationException(
                        "Tool '" + c.Name + "' names no plugin command and has no host handler, so nothing could " +
                        "answer it. Either give it a Command in the contract or bind it in Hosts.");

                list.Add(new ToolDef
                {
                    Name = c.Name,
                    Command = c.Command,
                    Description = c.Description,
                    InputSchema = c.InputSchema,
                    OutputSchema = c.OutputSchema,
                    Effect = c.Effect,
                    Destructive = c.Destructive,
                    OpenWorld = c.OpenWorld,
                    Toolsets = c.Toolsets ?? new string[0],
                    ExternalContent = c.ExternalContent,
                    Host = host
                });
            }
            return list;
        }

        /// <summary>
        /// Tools that are switched off are NOT advertised. A client should not see a tool
        /// it will be refused for calling - and a capability that runs arbitrary code
        /// should not appear in a list somebody skims.
        /// </summary>
        private static bool IsEnabled(ToolDef t)
        {
            Horizun.Contracts.CommandContract contract = Horizun.Contracts.Contract.Find(t.Name);
            string reason;
            return Horizun.Revit.Core.Settings.IsToolAllowed(contract, out reason);
        }

        /// <summary>
        /// Enabled with the TOOL-PACK restriction lifted and nothing else. Used only to
        /// measure what the pack selection saves; never to decide whether a call may run.
        /// </summary>
        private static bool IsEnabledIgnoringPacks(ToolDef t)
        {
            Horizun.Contracts.CommandContract contract = Horizun.Contracts.Contract.Find(t.Name);
            string reason;
            return Horizun.Revit.Core.Settings.IsToolAllowedIgnoringPacks(contract, out reason);
        }

        /// <summary>
        /// The add-in this server would route to right now, or null when none is
        /// discovered or the choice is ambiguous. Program installs it at startup; a test
        /// substitutes its own. It must never throw: a tool list that fails because Revit
        /// is busy is worse than one that lists everything.
        /// </summary>
        internal static Func<Discovered> LiveBridge;

        private static Discovered Live()
        {
            Func<Discovered> f = LiveBridge;
            if (f == null) return null;
            try { return f(); } catch { return null; }
        }

        /// <summary>
        /// Why this tool is NOT advertised to a client, or null when it is.
        ///
        /// A TOOL A CLIENT CAN SEE IS A TOOL A CLIENT WILL CALL. The per-call guard
        /// already refuses a command the loaded add-in does not register, and refuses a
        /// server and add-in built from different contracts - but the client had already
        /// been told the tool was there, so the refusal arrives as a surprise in the
        /// middle of somebody's work instead of as an absence they could plan around.
        /// The list now answers the same question the call does.
        ///
        /// THE THREE THINGS THIS IS CAREFUL NOT TO DO:
        ///   - it never withholds a HOST-RESIDENT tool: those are answered in this
        ///     process and need no Revit at all;
        ///   - it never treats UNKNOWN as absent: an add-in that published no command
        ///     list (before schema 3) says nothing about what it has, and a client
        ///     that started before Revit did has no bridge to ask;
        ///   - it keeps ONE source. The set of plugin commands comes from the contract
        ///     and the registration list comes from the add-in's own discovery file;
        ///     there is no third list here to go stale.
        /// </summary>
        internal static string WithheldReason(ToolDef t, Discovered live)
        {
            if (t == null) return null;
            if (t.Host != null) return null;                       // answered here; Revit is not involved
            if (string.IsNullOrEmpty(t.Command)) return null;      // host-resident by contract
            if (live == null) return null;                         // no bridge discovered: unknown, not absent

            // Two builds that disagree about the contract cannot exchange arguments
            // safely, so EVERY plugin tool is unusable until they are redeployed
            // together. Advertising them all and refusing them all one at a time is
            // the surprise this exists to remove.
            if (live.ContractHash != null && live.ContractHash != Horizun.Contracts.Contract.Hash)
                return "the Horizun add-in loaded in Revit " + live.Year + " (version " +
                       (live.AddinVersion ?? "unknown") + ", pid " + live.Pid + ") was built from a DIFFERENT " +
                       "command contract - server " + Horizun.Contracts.Contract.Hash + ", add-in " +
                       live.ContractHash + ". Close Revit and run install.ps1 so both halves move together.";

            bool? supports = live.Supports(t.Command);
            if (supports != false) return null;                    // registered, or the add-in published no list
            return "the Horizun add-in loaded in Revit " + live.Year + " (version " +
                   (live.AddinVersion ?? "unknown") + ", pid " + live.Pid + ") does not register '" + t.Command +
                   "', which is the command this tool needs. The two halves were not built from one tree: close " +
                   "Revit and run install.ps1.";
        }

        /// <summary>Every tool withheld right now, with the reason - the diagnostic half.</summary>
        internal static JArray Withheld()
        {
            Discovered live = Live();
            var arr = new JArray();
            foreach (var t in All)
            {
                string why = WithheldReason(t, live);
                if (why == null) continue;
                arr.Add(new JObject { ["name"] = t.Name, ["command"] = t.Command, ["reason"] = why });
            }
            return arr;
        }

        public static JArray List(bool advertiseTaskSupport = false)
            => Build(advertiseTaskSupport, IsEnabled);

        /// <summary>
        /// The tools List would advertise right now, in contract order, without publishing
        /// them: the SAME enablement and withholding filter, so the schema facade
        /// (SchemaFacade.cs) can never reach a tool the full list would not show.
        /// </summary>
        internal static List<ToolDef> Advertisable()
        {
            Discovered live = Live();
            var list = new List<ToolDef>();
            foreach (var t in All)
                if (IsEnabled(t) && WithheldReason(t, live) == null) list.Add(t);
            return list;
        }

        /// <summary>Why a known tool is withheld from the list right now, or null - the live-bridge half.</summary>
        internal static string WithheldNow(ToolDef t) => WithheldReason(t, Live());

        /// <summary>
        /// The list this server would publish if no tool pack were selected, at the same
        /// permission posture. It is the BASELINE for the discovery-cost measurement in
        /// Protocol/DiscoveryCost.cs and has no other caller: a client never sees it, and
        /// nothing dispatches from it.
        /// </summary>
        public static JArray ListIgnoringPacks(bool advertiseTaskSupport = false)
            => Build(advertiseTaskSupport, IsEnabledIgnoringPacks);

        /// <summary>
        /// The list a session selecting exactly <paramref name="packs"/> (toolsets, with
        /// their dependencies and core) WOULD see, at the current permission posture. It
        /// answers "what would selecting mep cost?" for horizun://session/toolsets; it is
        /// never dispatched and never shown to a client as its tool list.
        /// </summary>
        public static JArray ListForPacks(IEnumerable<string> packs, bool advertiseTaskSupport = false)
        {
            Horizun.Revit.Core.ToolPacks.Resolution selection =
                Horizun.Revit.Core.ToolPacks.Resolve(null, packs, false);
            HashSet<string> visible = selection.Tools();
            return Build(advertiseTaskSupport, t => visible.Contains(t.Name) && IsEnabledIgnoringPacks(t));
        }

        private static JArray Build(bool advertiseTaskSupport, Func<ToolDef, bool> enabled)
        {
            var arr = new JArray();
            Discovered live = Live();
            // DETERMINISTIC ORDER (2026-07-28 asks for it, and prompt caches pay for it).
            // The contract's own declaration order is already stable across processes and
            // across machines - it is a literal list in one file - so publishing in that
            // order costs nothing and makes two calls to tools/list byte-identical while
            // nothing has changed. Sorting by name would also be stable but would scatter
            // related tools, and the contract order groups them the way a reader expects.
            foreach (var t in All)
            {
                if (!enabled(t)) continue;
                if (WithheldReason(t, live) != null) continue;
                arr.Add(Publish(t, advertiseTaskSupport));
            }
            return arr;
        }

        /// <summary>
        /// The one entry tools/list publishes for <paramref name="t"/>, with no enablement
        /// or withholding decision in it. Build decides WHETHER a tool is listed; this decides
        /// WHAT a listed tool looks like. It is separate so the per-tool byte ledger
        /// (ToolsListLedgerTests) can measure every contract row regardless of the posture of
        /// the machine running it.
        /// </summary>
        internal static JObject Publish(ToolDef t, bool advertiseTaskSupport)
        {
            var published = new JObject
            {
                ["name"] = t.Name,
                ["title"] = Title(t.Name),
                ["description"] = CompactDescription(t.Description),
                ["inputSchema"] = CompactSchema(t.Name, t.InputSchema),
                ["outputSchema"] = t.OutputSchema,
                ["annotations"] = Annotations(t)

                // execution/taskSupport is added below only for a negotiated
                // 2025-11-25 session. Down-level clients never see the field. The
                // optional/forbidden decision is the same rule the durable submit
                // queue enforces, through McpTasks.Supports.
            };
            if (advertiseTaskSupport)
                published["execution"] = new JObject
                {
                    ["taskSupport"] = McpTasks.Supports(t) ? "optional" : "forbidden"
                };

            // MCP Apps: a tool declares its interactive view in its own description,
            // through _meta.ui.resourceUri, and the host may preload it before the
            // tool is ever called. Only horizun_clash has one, and only because the
            // app renders that reply and nothing else - an app attached to a tool
            // whose payload it cannot render is a blank panel the user blames their
            // client for.
            if (t.Name == "horizun_clash") published["_meta"] = McpAppResources.ToolUiMeta();
            // The impact preview: the five bulk writes whose rehearsal payload
            // (change_preview / plan_resolved / confirmation_token) the app reads.
            else if (ImpactPreviewApp.Renders(t.Name)) published["_meta"] = ImpactPreviewApp.ToolUiMeta();

            return published;
        }

        private static string Title(string name)
        {
            string raw = name.StartsWith("horizun_", StringComparison.Ordinal) ? name.Substring(8) : name;
            string[] words = raw.Split('_');
            for (int i = 0; i < words.Length; i++)
                if (words[i].Length > 0) words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            return string.Join(" ", words);
        }

        internal static string CompactDescription(string description)
        {
            const int max = 900;
            const string suffix = " Full installed contract: horizun://contract/tools";
            if (string.IsNullOrWhiteSpace(description)) return suffix.Trim();
            string normalized = description.Replace("\r", " ").Replace("\n", " ").Trim();
            if (normalized.Length + suffix.Length <= max) return normalized + suffix;
            // THE ELLIPSIS IS PART OF THE BUDGET. It was not, and the cap this function
            // promises could be exceeded by one or two characters: with the sentence
            // boundary falling exactly on `limit` the result came back at 901, and
            // `cut += 1` could reach 902. Nothing detected it until a description landed
            // on the boundary, because every existing one happened to cut earlier - the
            // quiet kind of off-by-one that waits for the next tool. One character is
            // reserved here, and the two branches below can then only shorten.
            int limit = max - suffix.Length - 1;
            int cut = normalized.LastIndexOf(". ", limit, StringComparison.Ordinal);
            if (cut < Math.Min(160, limit / 2)) cut = limit;
            else cut += 1;
            return normalized.Substring(0, cut).TrimEnd() + "…" + suffix;
        }

        // ARGUMENT DESCRIPTIONS ARE BUDGETED LIKE TOOL DESCRIPTIONS. Measured 2026-09-24:
        // of a 519 KB tools/list, 417 KB were input schemas and 237 KB of those were
        // argument descriptions, some of them essays. A description longer than
        // SchemaDescriptionMax is cut at a sentence boundary and points at the full
        // contract resource, exactly as CompactDescription does one level up. Only the
        // ADVERTISED copy is compacted: argument validation reads t.InputSchema, and
        // horizun://contract/tools serves every word. The copy is computed once per tool.
        // 250 since 2026-09-26: the coordination loop, element kinds and re-read work took
        // tools/list to 526 KB against the 512 KiB budget.
        internal const int SchemaDescriptionMax = 250;
        private const string SchemaDescriptionSuffix = " (full text: horizun://contract/tools)";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JObject> CompactSchemas =
            new System.Collections.Concurrent.ConcurrentDictionary<string, JObject>(StringComparer.Ordinal);

        internal static JObject CompactSchema(string toolName, JObject schema)
        {
            if (schema == null) return null;
            return CompactSchemas.GetOrAdd(toolName, _ =>
            {
                var copy = (JObject)schema.DeepClone();
                // The structural steps run on the FULL text, before the description caps,
                // so "is this annotation identical to the union's" is an exact comparison.
                SubtractBranchDuplicates(copy);
                DropBranchTypeEqualToParent(copy);
                ApplyAdvertisedShortForms(copy);
                CompactSchemaNode(copy);
                return copy;
            });
        }

        // STRUCTURAL SUBTRACTION (2026-09-26). A node that has both a `properties` map (the
        // union of every field) and a oneOf/anyOf/allOf whose branches restate some of those
        // fields was advertising each field's schema twice: once in the union, once more in
        // every branch. Both apply to the SAME instance - JSON Schema evaluates the node's
        // `properties` and the chosen branch's `properties` against the same object - so
        // (union AND branch) == (union AND branch'), where branch' keeps only what the branch
        // adds or tightens. Nothing about what is ACCEPTED changes; the validators read the
        // full contract in any case (ToolInputRules, the command parsers), and the full
        // schema of any branch is served by horizun://contract/tools/{tool}/{variant}.
        //
        // What is never subtracted: const (the discriminator), references and combinators
        // (their meaning depends on the whole subschema), and `type` - kept on every field
        // so a client that infers a missing type (codex-rs sanitize_json_schema coerces an
        // untyped or boolean schema to a string) still sees the right one. Property KEYS
        // are never removed: a fully redundant field becomes {"type": ...}, never {}.
        //
        // MEASURED 2026-09-26 (with DropBranchTypeEqualToParent): tools/list 524,199 ->
        // 465,146 bytes for all 122 tools; horizun_create_elements 81,409 -> ~30.8 KB and
        // horizun_document_session 17,551 -> ~9.1 KB.
        private static readonly string[] Combinators = { "oneOf", "anyOf", "allOf" };
        private static readonly HashSet<string> NeverSubtracted = new HashSet<string>(StringComparer.Ordinal)
        {
            "type", "const", "$ref", "$defs", "definitions", "oneOf", "anyOf", "allOf", "not",
            "unevaluatedProperties", "unevaluatedItems"
        };
        private static readonly HashSet<string> AnnotationKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "description", "default", "title", "examples", "$comment"
        };
        // Keywords that only mean something together (additionalProperties depends on the
        // properties beside it; minItems on the items; then/else on the if). A group is
        // dropped all-or-none, and only when EVERY key in it equals the union's.
        private static readonly string[][] KeywordGroups =
        {
            new[] { "properties", "patternProperties", "additionalProperties", "required", "propertyNames", "minProperties", "maxProperties" },
            new[] { "items", "prefixItems", "additionalItems", "minItems", "maxItems", "uniqueItems", "contains", "minContains", "maxContains" },
            new[] { "if", "then", "else" }
        };

        internal static void SubtractBranchDuplicates(JToken node)
        {
            if (node is JObject o)
            {
                if (o["properties"] is JObject union)
                    foreach (string combinator in Combinators)
                        if (o[combinator] is JArray branches)
                            foreach (JToken branch in branches)
                                if (branch is JObject b && b["properties"] is JObject own)
                                    foreach (JProperty field in own.Properties())
                                        if (field.Value is JObject f && union[field.Name] is JObject u)
                                            field.Value = SubtractField(f, u);
                foreach (JProperty p in o.Properties())
                {
                    if (p.Name == "properties" && p.Value is JObject props)
                        foreach (JProperty arg in props.Properties()) SubtractBranchDuplicates(arg.Value);
                    else
                        SubtractBranchDuplicates(p.Value);
                }
            }
            else if (node is JArray a)
                foreach (JToken item in a) SubtractBranchDuplicates(item);
        }

        private static JObject SubtractField(JObject branch, JObject union)
        {
            var kept = new JObject();
            foreach (JProperty kw in branch.Properties())
            {
                bool keep;
                if (NeverSubtracted.Contains(kw.Name)) keep = true;
                else if (AnnotationKeywords.Contains(kw.Name)) keep = !JToken.DeepEquals(union[kw.Name], kw.Value);
                else
                {
                    string[] group = null;
                    foreach (string[] g in KeywordGroups)
                        if (Array.IndexOf(g, kw.Name) >= 0) { group = g; break; }
                    if (group == null) keep = !JToken.DeepEquals(union[kw.Name], kw.Value);
                    else
                    {
                        keep = false;
                        foreach (string k in group)
                            if (!JToken.DeepEquals(branch[k], union[k])) { keep = true; break; }
                    }
                }
                if (keep) kept.Add(kw.Name, kw.Value.DeepClone());
            }
            // Never an empty schema: {} reads as "anything" to a client that ignores the
            // union. A typed union lends its type (it applies anyway); otherwise the branch
            // field is left exactly as the contract wrote it.
            if (kept.Count == 0 && branch.Count > 0)
                return union["type"] != null ? new JObject { ["type"] = union["type"].DeepClone() } : branch;
            return kept;
        }

        // SHORT FORMS OF TEXT THE CONTRACT REPEATS VERBATIM. The idempotency_key description
        // is attached to every mutating tool (65 occurrences, MEASURED 2026-09-26), 305
        // characters each time. The advertised copy shows a short form that keeps the three
        // things a caller must act on; the contract and horizun://contract/tools keep every
        // word. Keyed by the EXACT full text on purpose: if the contract's wording changes,
        // the short form silently stops applying, nothing breaks, the 250-character cap
        // applies as before, and the tools/list ledger shows the growth.
        //
        // 2026-10-10 (tools/list budget): every argument text repeated verbatim in three or
        // more tools gets one too - target_document, confirmation_token and the four
        // cooperative-reading arguments. ToolsListBudgetTests fails when a new one appears
        // without a short form, and when a short form drops an argument name or REQUIRED.
        // Two of them are the write_params_verified / set_keynote / family_apply wording, which
        // until 2.2.0 was horizun_delete_verified's text copied word for word (contract
        // 5663f2c49479ebbd280926f4 gave them their own); delete_verified alone keeps its text.
        internal static readonly Dictionary<string, string> AdvertisedShortForms = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REQUIRED whenever this call will mutate or change the Revit session. A retry " +
             "with the same key and identical operation returns the recorded result without " +
             "executing twice. Reusing it for different arguments is refused. Generate a new " +
             "UUID for each deliberate operation; keep it unchanged only for retries."] =
                "Required when the call mutates the model or session: a new UUID per deliberate change; " +
                "reuse it only to retry the identical call.",

            // target_document
            ["REQUIRED. Title or full path of the model to change. It must be the document ACTIVE " +
             "in Revit; this never switches documents for you."] =
                "REQUIRED. Title or full path of the ACTIVE document; this never switches documents.",
            ["REQUIRED. Title or full path of the document to write to. It must be the document " +
             "that is ACTIVE in Revit; this command will not switch documents for you. A write aimed " +
             "at whatever window happens to be in front is a write aimed at whatever turns up."] =
                "REQUIRED. Title or full path of the ACTIVE document to act on; this never switches documents.",
            ["REQUIRED. Title or full path of the document to change. It must be the document ACTIVE " +
             "in Revit; this never switches documents for you. Aliases accepted for compatibility: " +
             "expected_document, target_document_title."] =
                "REQUIRED. Title or full path of the ACTIVE document; this never switches documents. " +
                "Aliases: expected_document, target_document_title.",

            // confirmation_token
            ["REQUIRED when dry_run=false. The token this exact request's dry run returned. " +
             "Single-use, expiring, bound to this document and this scope."] =
                "REQUIRED when dry_run=false: the token this exact request's dry run returned. Single-use, expiring.",
            ["REQUIRED when dry_run=false. The token returned by the dry run of this exact request. " +
             "Single-use, expires, and bound to this document and this request - if either changed, " +
             "execution is refused and nothing is written."] =
                "REQUIRED when dry_run=false: the token this exact request's dry run returned. Single-use, " +
                "expiring; refused if the document or request changed.",

            // the cooperative-reading arguments of list_elements, query_model and model_scan
            ["OPT-IN cooperative reading. OMIT IT AND NOTHING CHANGES: this reader behaves exactly as " +
             "it always has, and the reply carries no extra field. Send it when a PARTIAL answer that " +
             "arrives is worth more to you than a complete one nobody waited for - a scan of a large " +
             "model holds Revit's UI thread for as long as it runs, and the person whose Revit it is " +
             "cannot click anything meanwhile. With it, the read asks BETWEEN elements whether to carry " +
             "on: it stops if you have gone away, and it stops at your budget. A result that stopped " +
             "early says PARTIAL and never passes itself off as clean. WHAT THIS DOES NOT PROVE: " +
             "ui_budget_ms bounds how long the command SPENDS, not that Revit's interface stayed " +
             "responsive - the command holds the UI thread for its whole duration, the reply still " +
             "travels back through the pipe, and Revit's own event queue decides when the window " +
             "repaints. Stopping work and releasing the interface are different events, and only a " +
             "sampler watching from outside can measure the second."] =
                "OPT-IN; omit it and nothing changes. With it the read stops if you have gone away or at your " +
                "budget, and a result that stopped early says PARTIAL. ui_budget_ms bounds the work, not Revit's responsiveness.",
            ["How long this read may hold Revit's UI thread before it stops and says so. Twenty seconds " +
             "by default, chosen against what a person does: below about that a frozen window reads as " +
             "'it is working'; past it people start clicking, then killing Revit."] =
                "How long this read may hold Revit's UI thread before it stops and says so. Default 20 seconds.",
            ["A hard bound on elements EXAMINED, independent of the clock. Separate from the budget on " +
             "purpose: a bound in units is reproducible across machines and a bound in milliseconds is not."] =
                "A hard bound on elements EXAMINED, independent of the clock.",
            ["Continue a previous partial read. Opaque, and REFUSED against a different request: resuming " +
             "one query at another query's position produces a page of the wrong elements with nothing to " +
             "show that anything went wrong. Where this command already pages with its own cursor or " +
             "offset, the reply says so and issues none here - two continuation tokens over different " +
             "things is worse than either."] =
                "Continue a previous partial read. Opaque; REFUSED against a different request. Where the command " +
                "pages with its own cursor or offset, use that; the reply says so."
        };

        private static void ApplyAdvertisedShortForms(JToken node)
        {
            if (node is JObject o)
            {
                if (o["description"] is JValue d && d.Type == JTokenType.String &&
                    AdvertisedShortForms.TryGetValue((string)d, out string shortForm))
                    o["description"] = shortForm;
                foreach (JProperty p in o.Properties())
                    if (p.Name != "description") ApplyAdvertisedShortForms(p.Value);
            }
            else if (node is JArray a)
                foreach (JToken item in a) ApplyAdvertisedShortForms(item);
        }

        // A combinator branch restating its parent's `type` adds nothing: the parent's
        // `type` already applies to the same instance. Kept when it is all the branch has,
        // so no branch is ever advertised as {}.
        internal static void DropBranchTypeEqualToParent(JToken node)
        {
            if (node is JObject o)
            {
                JToken parentType = o["type"];
                if (parentType != null)
                    foreach (string combinator in Combinators)
                        if (o[combinator] is JArray branches)
                            foreach (JToken branch in branches)
                                if (branch is JObject b && b.Count > 1 && b["type"] != null && JToken.DeepEquals(b["type"], parentType))
                                    b.Remove("type");
                foreach (JProperty p in o.Properties())
                {
                    if (p.Name == "properties" && p.Value is JObject props)
                        foreach (JProperty arg in props.Properties()) DropBranchTypeEqualToParent(arg.Value);
                    else
                        DropBranchTypeEqualToParent(p.Value);
                }
            }
            else if (node is JArray a)
                foreach (JToken item in a) DropBranchTypeEqualToParent(item);
        }

        // internal, not private: DeepClone + CompactSchemaNode alone IS the advertised copy
        // before the structural steps existed (c56a742), and the equivalence test rebuilds it
        // to prove the abridgement gives the verdicts clients already saw, not only the contract's.
        internal static void CompactSchemaNode(JToken node)
        {
            if (node is JObject o)
            {
                foreach (JProperty p in o.Properties())
                {
                    if (p.Name == "description" && p.Value.Type == JTokenType.String)
                        p.Value = CompactSchemaDescription((string)p.Value);
                    else if (p.Name == "properties" && p.Value is JObject props)
                        foreach (JProperty arg in props.Properties()) CompactSchemaNode(arg.Value); // argument NAMES are never touched
                    else
                        CompactSchemaNode(p.Value);
                }
            }
            else if (node is JArray a)
                foreach (JToken item in a) CompactSchemaNode(item);
        }

        internal static string CompactSchemaDescription(string description)
        {
            if (description == null || description.Length <= SchemaDescriptionMax) return description;
            int limit = SchemaDescriptionMax - SchemaDescriptionSuffix.Length - 1;
            int cut = description.LastIndexOf(". ", limit, StringComparison.Ordinal);
            if (cut < limit / 2) cut = limit;
            else cut += 1;
            return description.Substring(0, cut).TrimEnd() + "…" + SchemaDescriptionSuffix;
        }

        // The task-support rule lives in McpTasks.Supports so the advertised hint and
        // actual task admission cannot drift.

        private static JObject Annotations(ToolDef t)
        {
            // Every hint is READ from the contract. The two that used to be hardcoded
            // lists in this file - destructiveHint and openWorldHint - are declared on the
            // contract next to Effect, so a tool added without touching this file gets the
            // hints its own definition asked for instead of the safe-sounding default.
            bool readOnly = t.Effect == Horizun.Contracts.ToolEffect.ReadOnly;
            bool durable = t.Effect == Horizun.Contracts.ToolEffect.Mutating ||
                           t.Effect == Horizun.Contracts.ToolEffect.MutatingUnlessDryRun ||
                           t.Effect == Horizun.Contracts.ToolEffect.DocumentSession;
            return new JObject
            {
                ["title"] = Title(t.Name),
                ["readOnlyHint"] = readOnly,
                ["destructiveHint"] = t.Destructive,
                ["idempotentHint"] = readOnly || durable,
                ["openWorldHint"] = t.OpenWorld
            };
        }

        /// <summary>Why a known tool is not being offered right now, or null if it is.</summary>
        public static string DisabledReason(string toolName)
        {
            Horizun.Contracts.CommandContract contract = Horizun.Contracts.Contract.Find(toolName);
            string reason;
            if (contract != null && !Horizun.Revit.Core.Settings.IsToolAllowed(contract, out reason)) return reason;
            return null;
        }

        /// <summary>The full ToolDef for a name, or null if no such tool — the caller decides host vs. forward.</summary>
        public static ToolDef Find(string toolName)
        {
            foreach (var t in All)
                if (t.Name == toolName) return t;
            return null;
        }
    }
}
