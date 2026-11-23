using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class ElementIdentityFacts
    {
        public long Id;
        public string UniqueId;
        public string Category;
        /// <summary>The BuiltInCategory name ("OST_Walls"), which does not depend on Revit's language; null when unreadable.</summary>
        public string CategoryBuiltIn;
        /// <summary>The type NAME ("Generic - 200mm").</summary>
        public string Type;
        /// <summary>The family (or system family) the type belongs to ("Basic Wall"); null when unreadable.</summary>
        public string Family;
        /// <summary>
        /// True only when the element carries the Horizun ownership mark. Null or false (no mark, someone else's, or a mark that could
        /// not be read) is treated alike as NOT ours: a category-only identity is refused on it (R2S m-6, fail closed).
        /// </summary>
        public bool? Owned;
    }

    /// <summary>
    /// What the identity check of the CURRENT call established, kept for the rest of the call on the Revit thread:
    /// the digest a confirmation token is bound to (a token issued for a rehearsal that carried an identity does not
    /// authorize an apply that omits it or changes it) and whether any key was checked by category/type alone, which
    /// does not detect a swap inside the same category (the reply says so). Reset by the dispatcher before each call.
    /// </summary>
    public static class IdentityBinding
    {
        [ThreadStatic] private static string _digest;
        [ThreadStatic] private static List<string> _weakKeys;
        [ThreadStatic] private static int _nodes;

        /// <summary>A short hash of every identity the call carried (path and values); null when it carried none.</summary>
        public static string Digest => _digest;
        public static bool Carried => _digest != null;
        public static bool Weak => _weakKeys != null && _weakKeys.Count > 0;
        public static IReadOnlyList<string> WeakKeys => (IReadOnlyList<string>)_weakKeys ?? new string[0];

        public static void Reset() { _digest = null; _weakKeys = null; _nodes = 0; }

        internal static void Set(string digest, List<string> weakKeys, int nodes) { _digest = digest; _weakKeys = weakKeys; _nodes = nodes; }

        /// <summary>The block a reply carries when the call carried an identity; null when it did not.</summary>
        public static JObject Notice()
        {
            if (_digest == null) return null;
            var n = new JObject
            {
                ["identity_strength"] = Weak ? "categoria" : "unique_id",
                ["nodes_checked"] = _nodes
            };
            if (Weak)
            {
                n["keys_without_unique_id"] = new JArray(_weakKeys.ToArray());
                n["note"] = "These keys were checked by expected_category / expected_type only. That detects an id that now names an element of " +
                            "another category or type, NOT a swap between two elements of the same category or type (the field case: text notes). " +
                            "Only unique_id identifies an element; send the unique_id you read.";
            }
            return n;
        }
    }

    // Identity preflight for typed writes that receive element ids.
    //
    // A numeric ElementId read before a synchronize with central can point at a different
    // element afterwards. A call may therefore carry what it expects to find, in two forms:
    //   * BY KEY (preferred): identity: { <id_key>: <unique_id | {unique_id, expected_category,
    //     expected_type}> } on the node that holds the ids. Every key named is verified, so a
    //     node with several ids (a view AND a sheet; a toposolid AND its floors) is checked on
    //     each of them. For a key that holds a list of ids the value is a list aligned with it
    //     (each entry a unique_id or the object form) or {unique_ids, expected_category, expected_type}.
    //   * BARE (compatibility): unique_id / unique_ids / expected_category / expected_type beside the
    //     ids. They are checked against ONE key, the first present of the tool's list in SpecFor
    //     (documented in docs/IDENTITY-GUARD.md and, short, in the schema of the tools that declare them).
    // expected_category accepts the localized name or the BuiltInCategory (OST_Walls); expected_type
    // accepts the bare type name or "Family: Type", compared whole.
    // The whole request is resolved and checked BEFORE any transaction opens; one mismatch refuses
    // the call naming expected versus found, and nothing is written. A request that carries none
    // of those fields is untouched (4.0 behaviour). After a successful check the identity fields
    // are consumed: the id list the command reads is the verified one, so no command parser has
    // to learn the new names and the plan hash is computed over resolved ids.
    //
    // COVERAGE (nothing receives these fields at ANY depth and silently ignores them):
    //   * Verified: every tool in Verified below; every node of the request, however deep, that
    //     carries identity fields is checked, hence also the children of horizun_execute_plan.
    //   * Refused with entrada_invalida: a tool that takes no element ids (family_apply), a tool whose
    //     ids belong to another document (copy_between_documents), a node with identity fields but no
    //     ids to check (a selector, purge_unused or query mode), the top level of annotate and
    //     edit_dimensions (their ids live in actions[]), and - for every other writer - identity fields
    //     at ANY depth of the request, plan children included.
    //   * Not protectable, by construction: horizun_execute_python. A script chooses its own ids
    //     inside code the bridge does not parse; it must verify them itself (unique_id plus
    //     horizun_selection_context operation=validate_ids, in its own __output__).
    public static class ElementIdentityGuard
    {
        public const string Mismatch = "elemento_no_coincide";
        public const string InvalidArgs = "identidad_invalida";
        public const string ArgumentErrorCode = "entrada_invalida";
        public const string MapField = "identity";

        // R2U: the exact text of the refusal of a category / type alone. docs/IDENTITY-GUARD.md and AGENTS.md (EN and ES) repeat these constants
        // word for word, and a test ties the documents to the code and the code to the message a caller really receives.
        public const string WeakRefusalTemplate =
            "expected_category / expected_type alone do not identify an element: {why}. Send the unique_id of the element you mean (identity.{id_key} or unique_id / unique_ids). " +
            "Take it from the read that gave you that id, or find the element again by its content (text, location, view) with horizun_query_model (response_mode full or compact, never summary) or horizun_list_elements. " +
            "Never take it from a lookup by an id you held from before a synchronize: that id may now name another element, and horizun_selection_context operation=validate_ids returns holder_unique_id, " +
            "not an identity, for an id sent without its unique_id. Nothing was written";
        public const string WhyDeletes = "this operation deletes, and a deletion on an id that a synchronize may have renumbered cannot be undone";
        public const string WhyCollaborative = "this is a collaborative (workshared) model: a synchronize with central renumbers ElementIds and other people's elements live in it, " +
                                               "so a category / type cannot tell which element an id names, even on elements Horizun made";
        public const string WhyNotOurs = "element {id} does not carry a Horizun ownership mark of this user (it is somebody else's, an older mark, a copy, or the mark could not be read), " +
                                         "so it may be somebody else's work";


        private const string PlanTool = "horizun_execute_plan";
        private const string PythonTool = "horizun_execute_python";
        private const string CorrectionsTool = "horizun_apply_corrections";

        // The loose identity fields (everything accepted except the keyed map). Private and derived from the contract's one list (R2U m-3).
        private static readonly string[] IdentityFields = IdentityCoverage.AcceptedNames.Where(n => n != MapField).ToArray();

        private sealed class Spec
        {
            public string[] Single = new string[0];
            public string[] List = new string[0];
            public string DefaultSingle;
            public string DefaultList;
        }

        private static readonly string[] OriginalSingle = { "target_id", "element_id", "host_id" };
        private static readonly string[] OriginalList = { "ids", "element_ids", "host_ids" };
        private static readonly string[] GenericSingle = { "target_id", "element_id", "host_id", "instance_id", "view_id", "sheet_id" };
        private static readonly string[] GenericList = { "ids", "element_ids", "host_ids", "wall_ids", "member_ids", "room_ids" };

        // Tools whose identity fields are verified against the ACTIVE document. The list lives in the contract assembly
        // (IdentityCoverage) so that it is part of the contract hash.
        private static readonly HashSet<string> Verified = new HashSet<string>(IdentityCoverage.VerifiedTools, StringComparer.Ordinal);

        // Tools that take no ids of the active document: the fields are refused, never ignored.
        // (Its element_ids, for copy_between_documents, belong to the SOURCE document; the active one cannot vouch for them.) In the contract: IdentityCoverage.NoSubjectTools.
        private static readonly HashSet<string> NoSubject = new HashSet<string>(IdentityCoverage.NoSubjectTools, StringComparer.Ordinal);

        // Tools whose request is a batch of actions[]: an identity at the top level would be consumed
        // without protecting any action, so it is refused with the instruction to send it per action.
        // In the contract: IdentityCoverage.PerActionTools (R2V m-e), so that it is part of the hash.
        private static readonly HashSet<string> PerAction = new HashSet<string>(IdentityCoverage.PerActionTools, StringComparer.Ordinal);

        // The EXACT paths where a property called `identity` is TEXT the tool itself defines (who approved a delivery
        // stage; who signed a gate override). There, and only there, a string is not ours and is left alone: the same
        // word anywhere else in the same tool is an identity map, or an error.
        // In the contract: IdentityCoverage.OwnIdentityTextPaths.
        private static readonly Dictionary<string, string[]> OwnIdentityTextPaths =
            IdentityCoverage.OwnIdentityTextPaths.ToDictionary(k => k.Key, k => k.Value.ToArray(), StringComparer.Ordinal);

        private static bool IsOwnIdentityText(string tool, string nodePath)
        {
            if (tool == null || !OwnIdentityTextPaths.TryGetValue(tool, out string[] paths)) return false;
            string full = nodePath.Length == 0 ? MapField : nodePath + "." + MapField;
            return paths.Contains(full);
        }

        /// <summary>
        /// Properties whose object value is a map keyed by NAMES THE USER CHOSE (parameter names, column names, field names): their keys are
        /// data, never argument names, so neither the misspelling check nor the carrier search looks inside them (a parameter that
        /// happens to be called "identity" or "Identity" is not an identity field). Kept in step with the contract by the test
        /// R2S_every_map_keyed_by_user_names_in_a_covered_schema_is_listed.
        /// </summary>
        private static readonly string[] UserKeyedMapNames =
        {
            "component_fields", "copy_parameters", "map", "parameters", "renumber", "set", "type_fields", "value_columns"
        };
        private static readonly HashSet<string> UserKeyedMapSet = new HashSet<string>(UserKeyedMapNames, StringComparer.Ordinal);

        /// <summary>The names of the maps keyed by names the user chose, read-only (R2U m-3). Only the add-in reads them when it verifies, so they stay outside the hash.</summary>
        public static IReadOnlyList<string> UserKeyedMaps { get; } = Array.AsReadOnly(UserKeyedMapNames);

        private static bool IsUserKeyedMap(JProperty p) => p.Value is JObject && UserKeyedMapSet.Contains(p.Name);

        /// <summary>The names an identity can arrive under. A covered tool accepts them even where its schema does not declare them.</summary>
        public static IReadOnlyList<string> AcceptedNames => IdentityCoverage.AcceptedNames;

        /// <summary>The capture_view options that open a temporary isolation (the only capture that writes anything, then undoes it).</summary>
        public static IReadOnlyList<string> CaptureTemporaryFields => IdentityCoverage.CaptureTemporaryFields;

        /// <summary>
        /// For a tool the guard does NOT cover: the first identity field (spelt exactly) at the top level of the request that the tool's
        /// own schema does not declare. Such a field would be ignored, whether or not the schema is open, so the call is refused
        /// (R2S m-1). The server refuses first; the add-in repeats it as a backstop. Null when there is nothing to object to.
        /// </summary>
        public static JObject RejectUndeclaredTopLevel(JObject request, string tool, ISet<string> declared)
        {
            if (request == null || tool == null || Covers(tool)) return null;
            foreach (JProperty p in request.Properties())
            {
                if (!IsIdentityName(p.Name) || (declared != null && declared.Contains(p.Name))) continue;
                return ArgError(p.Name, 0, "'" + p.Name + "' was sent to " + tool + ", which does not verify element identity and does not declare that argument. " +
                                "It would have been ignored, so the call is refused. Verify the ids first with horizun_selection_context operation=validate_ids");
            }
            return null;
        }

        /// <summary>The argument names a schema declares at the top level, through oneOf / anyOf / allOf / then / else.</summary>
        public static HashSet<string> SchemaNames(JToken schema)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            CollectNames(schema, names);
            return names;
        }

        private static void CollectNames(JToken node, HashSet<string> names)
        {
            if (!(node is JObject o)) return;
            if (o["properties"] is JObject props) foreach (JProperty p in props.Properties()) names.Add(p.Name);
            foreach (string k in new[] { "oneOf", "anyOf", "allOf" })
                if (o[k] is JArray branches) foreach (JToken b in branches) CollectNames(b, names);
            CollectNames(o["then"], names);
            CollectNames(o["else"], names);
        }

        /// <summary>True for an argument name that holds element ids: ids, *_id, *_ids, pairs (join_geometry), element_ids_a / element_ids_b.</summary>
        public static bool IsIdArgumentName(string n) =>
            n != null && (n == "ids" || n == "pairs" || n.EndsWith("_id", StringComparison.Ordinal) || n.EndsWith("_ids", StringComparison.Ordinal) ||
                          n.StartsWith("element_ids_", StringComparison.Ordinal));

        public static bool Covers(string tool) => tool != null && Verified.Contains(tool);

        public static bool Refuses(string tool) => tool != null && NoSubject.Contains(tool);

        /// <summary>True for a name a covered tool accepts as an identity field whether or not its schema declares it.</summary>
        public static bool IsIdentityName(string name) => name != null && AcceptedNames.Contains(name);

        /// <summary>
        /// THE ONE ENTRY the dispatcher uses, before the command parses anything (so no validator of the command ever sees an
        /// identity field): near-miss spellings are refused naming the right field; a call that writes nothing and carries an
        /// identity is refused saying so; otherwise the identity is verified against the model and consumed. Null for a tool the
        /// guard does not cover (ForMutation refuses those later) and for a request that carries no identity.
        /// </summary>
        public static JObject PreCheck(JObject request, string tool, Func<long, ElementIdentityFacts> byId, Func<string, ElementIdentityFacts> byUniqueId,
                                       Func<bool> isCollaborative = null)
        {
            JObject shape = ShapeCheck(request, tool);
            if (shape != null) return shape;
            return NeedsCheck(request, tool) ? ResolveRequest(request, tool, byId, byUniqueId, isCollaborative) : null;
        }

        /// <summary>True when a covered tool's request carries an identity at any depth (so there is something to verify).</summary>
        public static bool NeedsCheck(JObject request, string tool) =>
            request != null && Covers(tool) && FirstCarrier(request, "", tool, out _) != null;

        /// <summary>The part of PreCheck that needs no model: a near-miss spelling, or an identity on a call that writes nothing.</summary>
        public static JObject ShapeCheck(JObject request, string tool)
        {
            if (request == null || !Covers(tool)) return null;
            JObject near = NearMiss(request, tool);
            if (near != null) return near;
            string carrier = FirstCarrier(request, "", tool, out string field);
            if (carrier != null && WritesNothing(tool, request))
                return ArgError(carrier, 0, "'" + field + "' was sent to an operation of " + tool + " that does not write, so there is nothing to protect and it would have been ignored. " +
                                "To check ids you hold, use horizun_selection_context operation=validate_ids; for a write, send the identity with the write");
            return null;
        }

        /// <summary>
        /// True when this call of a covered tool reads and writes nothing, so an identity on it protects nothing:
        /// the read operations of plan_mep and of the architectural editors, and a capture_view with no temporary option.
        /// </summary>
        public static bool WritesNothing(string tool, JObject request)
        {
            if (request == null) return false;
            string op = request["operation"]?.Type == JTokenType.String ? ((string)request["operation"]).Trim().ToLowerInvariant() : null;
            if (tool == "horizun_capture_view") return !IdentityCoverage.CaptureTemporaryFields.Any(f => request[f] != null);
            foreach (KeyValuePair<string, IReadOnlyList<string>> kv in IdentityCoverage.ReadOperations)
                if (kv.Key == tool) return op != null && kv.Value.Contains(op);
            return false;
        }

        // In the contract: IdentityCoverage.NearNames.
        private static readonly Dictionary<string, string> NearNames =
            IdentityCoverage.NearNames.ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);

        /// <summary>
        /// A property that is an identity field spelled another way (uniqueId, UniqueId, unique-id, expectedCategory, Identity...), at
        /// any depth. It would be ignored, and an identity ignored is a check the caller believes happened, so the call is refused
        /// naming the field it should have been.
        /// </summary>
        public static JObject NearMiss(JToken request, string tool)
        {
            if (request == null || tool == PythonTool) return null;
            if (tool == PlanTool) return NearMissInPlan(request as JObject, "");
            string path = FirstNearMiss(request, "", out string wrong, out string right);
            if (path == null) return null;
            return ArgError(path, 0, "'" + wrong + "' is not an identity field and would have been ignored by " + (tool ?? "this tool") + ". The identity fields are " +
                                     string.Join(", ", AcceptedNames) + ". Did you mean '" + right + "'? Nothing was written");
        }

        // A plan's own levels, and the arguments of the children whose tool is covered; the arguments of any other tool are its own business.
        private static JObject NearMissInPlan(JObject plan, string path)
        {
            if (plan == null) return null;
            foreach (JProperty p in plan.Properties())
            {
                string key = p.Name.ToLowerInvariant().Replace("_", "").Replace("-", "");
                if (NearNames.TryGetValue(key, out string canonical) && !string.Equals(p.Name, canonical, StringComparison.Ordinal))
                    return ArgError(path.Length == 0 ? p.Name : path + "." + p.Name, 0, "'" + p.Name + "' is not an identity field and would have been ignored. Did you mean '" + canonical + "'? Nothing was written");
            }
            var actions = plan["actions"] as JArray;
            if (actions == null) return null;
            string prefix = path.Length == 0 ? "" : path + ".";
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i] as JObject;
                string childTool = action?["tool"]?.Type == JTokenType.String ? (string)action["tool"] : null;
                var args = action?["arguments"] as JObject;
                if (args == null) continue;
                JObject hit = childTool == PlanTool ? NearMissInPlan(args, prefix + "actions[" + i + "].arguments")
                            : Covers(childTool) ? NearMiss(args, childTool) : null;
                if (hit != null)
                {
                    string inner = (string)hit["path"];
                    string basePath = prefix + "actions[" + i + "].arguments";
                    hit["path"] = childTool == PlanTool || inner.StartsWith(basePath, StringComparison.Ordinal) ? inner : (inner.Length == 0 ? basePath : basePath + "." + inner);
                    hit["index"] = i;
                    return hit;
                }
            }
            return null;
        }

        private static string FirstNearMiss(JToken node, string path, out string wrong, out string right)
        {
            wrong = right = null;
            if (node is JArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    string hit = FirstNearMiss(arr[i], path + "[" + i + "]", out wrong, out right);
                    if (hit != null) return hit;
                }
                return null;
            }
            var o = node as JObject;
            if (o == null) return null;
            foreach (JProperty p in o.Properties())
            {
                string key = p.Name.ToLowerInvariant().Replace("_", "").Replace("-", "");
                if (NearNames.TryGetValue(key, out string canonical) && !string.Equals(p.Name, canonical, StringComparison.Ordinal))
                { wrong = p.Name; right = canonical; return path.Length == 0 ? p.Name : path + "." + p.Name; }
            }
            foreach (JProperty p in o.Properties())
            {
                if (p.Name == MapField && p.Value is JObject) continue;
                if (IsUserKeyedMap(p)) continue;
                if (!(p.Value is JObject) && !(p.Value is JArray)) continue;
                string hit = FirstNearMiss(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, out wrong, out right);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>Every covered tool, for the schema/doc wiring tests.</summary>
        public static IEnumerable<string> CoveredTools => Verified;

        /// <summary>The id keys (single, then list) a covered tool's bare unique_id / unique_ids can be compared with. Annotate: both of its forms.</summary>
        public static IReadOnlyList<string> DocumentedKeys(string tool, bool lists)
        {
            if (!Covers(tool) || tool == PlanTool) return new string[0];
            var keys = new List<string>();
            foreach (var node in tool == "horizun_annotate"
                ? new[] { new JObject { ["operation"] = "tag" }, new JObject { ["operation"] = "text" } }
                : new[] { new JObject() })
            {
                Spec spec = SpecFor(tool, node);
                foreach (string k in lists ? spec.List : spec.Single) if (!keys.Contains(k)) keys.Add(k);
            }
            return keys;
        }

        /// <summary>The id key a bare unique_id (single) / unique_ids (lists) is FILLED INTO when the node carries no ids. Annotate: both of its forms.</summary>
        public static IReadOnlyList<string> DocumentedDefaults(string tool, bool lists)
        {
            if (!Covers(tool) || tool == PlanTool) return new string[0];
            var keys = new List<string>();
            foreach (var node in tool == "horizun_annotate"
                ? new[] { new JObject { ["operation"] = "tag" }, new JObject { ["operation"] = "text" } }
                : new[] { new JObject() })
            {
                Spec spec = SpecFor(tool, node);
                string k = lists ? spec.DefaultList : spec.DefaultSingle;
                if (k != null && !keys.Contains(k)) keys.Add(k);
            }
            return keys;
        }

        private static Spec SpecFor(string tool, JObject node)
        {
            switch (tool)
            {
                case "horizun_write_params_verified":
                case "horizun_set_keynote":
                case "horizun_transform_elements":
                case "horizun_delete_verified":
                    return new Spec
                    {
                        Single = OriginalSingle, List = OriginalList,
                        DefaultSingle = tool == "horizun_write_params_verified" ? "target_id" : "element_id",
                        DefaultList = tool == "horizun_delete_verified" ? "ids" : "element_ids"
                    };
                case "horizun_annotate":
                    // A tag is about its host element (or the link instance that carries it); every other
                    // annotation is placed in a view, and that view is what a renumbering would swap.
                    // On a tag over a LINK the element_id is the link instance, not the linked element:
                    // docs/IDENTITY-GUARD.md. element_ids of a tag are the FURTHER elements the same tag
                    // labels (IndependentTag.AddReferences): each is checked like element_id (5.2).
                    return string.Equals((string)node["operation"], "tag", StringComparison.Ordinal)
                        ? new Spec { Single = new[] { "element_id", "link_instance_id" }, List = new[] { "element_ids" }, DefaultSingle = "element_id" }
                        : new Spec { Single = new[] { "view_id" }, DefaultSingle = "view_id" };
                case "horizun_manage_views":
                    return new Spec { Single = new[] { "view_id", "sheet_id", "viewport_id" },
                                      List = new[] { "element_ids", "view_ids", "viewport_ids" } };
                case "horizun_fix_planimetry":
                    return new Spec { Single = new[] { "element_id", "viewport_id", "schedule_instance_id", "sheet_id", "view_id" },
                                      List = new[] { "element_ids" } };
                case "horizun_edit_dimensions":
                    return new Spec { Single = new[] { "element_id" }, DefaultSingle = "element_id" };
                case "horizun_detail_2d":
                    // draw_order names the detail elements to restack in element_ids.
                    return new Spec { Single = new[] { "element_id", "view_id" }, List = new[] { "element_ids" } };
                case "horizun_manage_groups":
                    return new Spec { Single = new[] { "type_id" }, List = new[] { "element_ids", "group_ids" } };
                case "horizun_embed_floors_in_toposolid":
                case "horizun_grade_toposolid_around_floors":
                    // The toposolid is what is rewritten (points deleted); the floors are what shapes it.
                    return new Spec { Single = new[] { "toposolid_id" }, List = new[] { "element_ids" } };
                case "horizun_sheet_layout":
                    // items[] rows are {view_id | sheet_id}; the node itself names a sheet, a viewport or viewports.
                    return new Spec { Single = new[] { "viewport_id", "sheet_id", "view_id" }, List = new[] { "viewport_ids" } };
                case "horizun_planimetry_ops":
                    // note_ids are text notes of the host; model_tag_id (clone_tag) is a tag of the host. A notes[] row with
                    // link_instance_id names an element of the LINKED document in element_id: link_instance_id goes first so a
                    // bare unique_id on such a row speaks for the instance, and the element_id is refused (LinkedDocumentKeys).
                    return new Spec { Single = new[] { "view_id", "source_legend_id", "model_tag_id", "link_instance_id", "element_id", "text_note_type_id", "schedule_id" },
                                      List = new[] { "note_ids", "grid_ids", "datum_ids", "view_ids", "viewport_ids" } };
                case "horizun_room_finishes":
                    return new Spec { List = new[] { "room_ids" } };
                case "horizun_pack_sheets":
                    return new Spec { Single = new[] { "sheet_id", "viewport_id", "schedule_instance_id", "view_id", "schedule_id" } };
                case "horizun_manage_revisions":
                    return new Spec { Single = new[] { "revision_id", "view_id" }, List = new[] { "sheet_ids", "remove_sheet_ids" } };
                case "horizun_manage_schedules":
                    return new Spec { Single = new[] { "schedule_id" }, DefaultSingle = "schedule_id" };
                case "horizun_manage_styles":
                    // the colour scheme and the view a scheme or its legend is applied to
                    return new Spec { Single = new[] { "scheme_id", "view_id" }, DefaultSingle = "scheme_id" };
                case "horizun_structural_connections":
                    return new Spec { List = new[] { "member_ids" } };
                case "horizun_export":
                    return new Spec { Single = new[] { "schedule_id", "ifc_filter_view_id" },
                                      List = new[] { "view_ids", "family_ids", "nwc_export_element_ids" } };
                case "horizun_view_visibility":
                case "horizun_manage_worksets":
                    // The view (or views) the change applies to and the elements it hides / moves.
                    return new Spec { Single = GenericSingle, List = new[] { "ids", "element_ids", "host_ids", "wall_ids", "member_ids", "room_ids", "view_ids" } };
                default:
                    return new Spec { Single = GenericSingle, List = GenericList };
            }
        }

        // Parameter-write rows (entry point kept for the row-level tests).
        public static JObject Resolve(JArray rows, Func<long, ElementIdentityFacts> byId,
                                      Func<string, ElementIdentityFacts> byUniqueId)
        {
            if (rows == null) return null;
            return Run(rows, "horizun_write_params_verified", byId, byUniqueId);
        }

        public static JObject ResolveRequest(JObject request, string tool, Func<long, ElementIdentityFacts> byId,
                                             Func<string, ElementIdentityFacts> byUniqueId, Func<bool> isCollaborative = null)
        {
            if (request == null || !Covers(tool)) return null;
            return Run(request, tool, byId, byUniqueId, isCollaborative);
        }

        // `identity` is the keyed identity when it is an object; any other value is an error unless the tool defines
        // an `identity` text of its own (OwnIdentityText).
        private static bool MapCarried(JObject o, string tool, string path) =>
            o[MapField] != null && (o[MapField] is JObject || tool == null || !IsOwnIdentityText(tool, path));

        private static bool Carries(JObject o, string tool, string path) => o != null && (MapCarried(o, tool, path) || IdentityFields.Any(f => o[f] != null));

        /// <summary>
        /// For a writer the guard does not verify: identity fields at ANY depth of its request are an
        /// argument error rather than a silent no-op. Null when there is nothing to object to.
        /// horizun_execute_python is exempt (its arguments are code and data of the script's own).
        /// </summary>
        public static JObject RejectUnsupported(JObject request, string tool)
        {
            if (request == null || tool == PythonTool || Covers(tool)) return null;
            string path = FirstCarrier(request, "", tool, out string field);
            if (path == null) return null;
            string why = "this tool receives no existing-element ids that can be checked";
            return ArgError(path, 0, "'" + field + "' was sent to " + (tool ?? "this tool") + (path.Length == 0 ? "" : " at " + path) + ", but " + why +
                            ". It would have been ignored, so the call is refused; remove the identity fields" +
                            " or verify the ids first with horizun_selection_context operation=validate_ids");
        }

        // The path of the first node, at any depth, that carries an identity field; null when none does.
        private static string FirstCarrier(JToken node, string path, string tool, out string field)
        {
            field = null;
            if (node is JArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    string hit = FirstCarrier(arr[i], path + "[" + i + "]", tool, out field);
                    if (hit != null) return hit;
                }
                return null;
            }
            var o = node as JObject;
            if (o == null) return null;
            if (Carries(o, tool, path))
            {
                field = MapCarried(o, tool, path) ? MapField : IdentityFields.First(f => o[f] != null);
                return path;
            }
            foreach (JProperty p in o.Properties())
            {
                if (IsUserKeyedMap(p)) continue;
                if (!(p.Value is JObject) && !(p.Value is JArray)) continue;
                string hit = FirstCarrier(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, tool, out field);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>Reads whether the document is collaborative; a reader that is missing or throws means "collaborative" (fails closed).</summary>
        public static bool CollaborativeOrUnknown(Func<bool> read)
        {
            if (read == null) return true;
            try { return read(); }
            catch { return true; }
        }

        private sealed class Ctx
        {
            public Func<long, ElementIdentityFacts> ById;
            public Func<string, ElementIdentityFacts> ByUid;
            public Func<bool> IsCollaborative;
            private bool? _collaborative;
            /// <summary>
            /// Whether the document is workshared (IsWorkshared, so also a local file of a central), asked once and only when a category / type alone needs it.
            /// Fails CLOSED: a reader that is missing or throws means "collaborative" (R2U m-2).
            /// </summary>
            public bool Collaborative
            {
                get
                {
                    if (_collaborative == null) _collaborative = CollaborativeOrUnknown(IsCollaborative);
                    return _collaborative.Value;
                }
            }
            public readonly List<Action> Commit = new List<Action>();
            public readonly List<string> Parts = new List<string>();
            public readonly List<string> Weak = new List<string>();
        }

        private static JObject Run(JToken root, string tool, Func<long, ElementIdentityFacts> byId,
                                   Func<string, ElementIdentityFacts> byUid, Func<bool> isCollaborative = null)
        {
            var ctx = new Ctx { ById = byId, ByUid = byUid, IsCollaborative = isCollaborative };
            JObject error = tool == PlanTool ? WalkPlan(root as JObject, "", ctx)
                          : tool == CorrectionsTool && (root as JObject)?["correction_plan"] is JObject ? WalkCorrectionPlan((JObject)root, ctx)
                          : Walk(root, tool, "", 0, ctx, true);
            if (error != null) return error;
            foreach (Action a in ctx.Commit) a();   // nothing is normalized unless the whole call passed
            if (ctx.Parts.Count > 0) IdentityBinding.Set(Digest(ctx.Parts), ctx.Weak.Distinct(StringComparer.Ordinal).ToList(), ctx.Parts.Count);
            return null;
        }

        private static string Digest(IEnumerable<string> parts)
        {
            var sb = new StringBuilder();
            foreach (string p in parts.OrderBy(x => x, StringComparer.Ordinal)) sb.Append(p).Append((char)31);   // order of the request does not matter (m-9)
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())), 0, 12).Replace("-", "").ToLowerInvariant();
        }

        private static JObject WalkPlan(JObject plan, string path, Ctx ctx)
        {
            if (plan == null) return null;
            // The plan's own level (and each action's) is structure, not a node with ids: identity belongs in
            // the child's arguments. Refused rather than ignored.
            if (Carries(plan, PlanTool, path))
                return ArgError(path, 0, "identity fields at the level of the plan itself are ignored by it; send them in the arguments of the action they are about");
            // Anything else that carries identity, at any depth, outside the arguments of an action: it is never executed or checked, so it is refused
            // naming its path rather than ignored (R2T m-2).
            JObject beside = IdentityOutsideExecuted(plan, path, PlanTool, clone =>
            {
                if (clone["actions"] is JArray list)
                    foreach (JToken a in list) if (a is JObject ao && ao["arguments"] != null) ao["arguments"] = new JObject();
            });
            if (beside != null) return beside;
            var actions = plan["actions"] as JArray;
            if (actions == null) return null;
            string prefix = path.Length == 0 ? "" : path + ".";
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i] as JObject;
                if (action != null && Carries(action, PlanTool, prefix + "actions[" + i + "]"))
                    return WithIndex(ArgError(prefix + "actions[" + i + "]", i, "identity fields beside the tool of a plan action are ignored by the plan; send them inside its arguments"), i);
                string childTool = action?["tool"]?.Type == JTokenType.String ? (string)action["tool"] : null;
                string childPath = prefix + "actions[" + i + "].arguments";
                if (childTool == PlanTool)
                {
                    JObject nested = WalkPlan(action["arguments"] as JObject, childPath, ctx);
                    if (nested != null) return nested;
                    continue;
                }
                if (!Covers(childTool))
                {
                    // The refusal the child would give on its own, raised for the whole plan before any child runs.
                    JObject unsupported = RejectUnsupported(action?["arguments"] as JObject, childTool);
                    if (unsupported != null)
                    {
                        string inner = (string)unsupported["path"];
                        unsupported["path"] = inner.Length == 0 ? childPath : childPath + (inner.StartsWith("[", StringComparison.Ordinal) ? "" : ".") + inner;
                        unsupported["index"] = i;
                        return unsupported;
                    }
                    continue;
                }
                JObject error = Walk(action["arguments"], childTool, childPath, i, ctx, true);
                if (error != null) return error;
            }
            return null;
        }

        // apply_corrections with a correction_plan: pasos[{id, herramienta, argumentos}]. Each step is verified with the Spec of ITS OWN
        // tool, exactly as the children of execute_plan are (R2S m-8); a step whose tool the guard does not cover is refused with identity.
        private static JObject WalkCorrectionPlan(JObject request, Ctx ctx)
        {
            if (Carries(request, CorrectionsTool, ""))
                return ArgError("", 0, "identity fields at the level of the correction plan itself are ignored by it; send them in the argumentos of the step they are about");
            JObject beside = IdentityOutsideExecuted(request, "", CorrectionsTool, clone =>
            {
                if ((clone["correction_plan"] as JObject)?["pasos"] is JArray list)
                    foreach (JToken st in list) if (st is JObject so && so["argumentos"] != null) so["argumentos"] = new JObject();
            });
            if (beside != null) return beside;
            var steps = (request["correction_plan"] as JObject)?["pasos"] as JArray;
            if (steps == null) return null;
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i] as JObject;
                string stepPath = "correction_plan.pasos[" + i + "]";
                if (step != null && Carries(step, CorrectionsTool, stepPath))
                    return WithIndex(ArgError(stepPath, i, "identity fields beside the herramienta of a correction step are ignored; send them inside its argumentos"), i);
                string childTool = step?["herramienta"]?.Type == JTokenType.String ? (string)step["herramienta"] : null;
                var args = step?["argumentos"] as JObject;
                if (args == null) continue;
                string childPath = stepPath + ".argumentos";
                if (childTool == PlanTool)
                {
                    JObject nested = WalkPlan(args, childPath, ctx);
                    if (nested != null) return nested;
                    continue;
                }
                if (!Covers(childTool))
                {
                    JObject unsupported = RejectUnsupported(args, childTool);
                    if (unsupported != null)
                    {
                        string inner = (string)unsupported["path"];
                        unsupported["path"] = inner.Length == 0 ? childPath : childPath + (inner.StartsWith("[", StringComparison.Ordinal) ? "" : ".") + inner;
                        unsupported["index"] = i;
                        return unsupported;
                    }
                    continue;
                }
                JObject error = Walk(args, childTool, childPath, i, ctx, true);
                if (error != null) return error;
            }
            return null;
        }

        /// <summary>
        /// The error for the first identity field found anywhere in <paramref name="root"/> once the arguments that ARE executed (and checked, each
        /// with its own tool) were blanked by <paramref name="blank"/>; null when there is none. Such a field would be ignored (R2T m-2).
        /// </summary>
        private static JObject IdentityOutsideExecuted(JObject root, string path, string tool, Action<JObject> blank)
        {
            var clone = (JObject)root.DeepClone();
            blank(clone);
            string found = FirstCarrier(clone, path, tool, out string field);
            if (found == null) return null;
            return ArgError(found, 0, "'" + field + "' was sent at " + (found.Length == 0 ? "the top level" : found) + ", outside the arguments that " + tool +
                            " executes, so it would have been ignored. Send it inside the arguments of the step it is about. Nothing was written");
        }

        private static JObject WithIndex(JObject error, int index) { error["index"] = index; return error; }

        private static JObject Walk(JToken node, string tool, string path, int index, Ctx ctx, bool root, DeleteScope ancestorDeleting = null)
        {
            if (node is JArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    JObject e = Walk(arr[i], tool, path + "[" + i + "]", i, ctx, false, ancestorDeleting);
                    if (e != null) return e;
                }
                return null;
            }
            var o = node as JObject;
            if (o == null) return null;
            JObject err = Check(o, tool, path, index, ctx, root, ancestorDeleting);
            if (err != null) return err;
            DeleteScope deletingHere = DeleteScope.Merge(ancestorDeleting, ScopeOf(tool, o));   // the operation of an ancestor applies to its children (R2T m-3)
            foreach (JProperty p in o.Properties().ToList())
            {
                if (p.Name == MapField && p.Value is JObject) continue;   // the identity map is data of this check, not a node to walk
                if (IsUserKeyedMap(p)) continue;                            // names the user chose, not argument names
                if (p.Value is JObject || p.Value is JArray)
                {
                    JObject e = Walk(p.Value, tool, (path.Length == 0 ? "" : path + ".") + p.Name, 0, ctx, false, deletingHere);
                    if (e != null) return e;
                }
            }
            return null;
        }

        private static string FirstKey(JObject o, string[] keys, bool wantArray)
        {
            foreach (string k in keys)
            {
                JToken t = o[k];
                if (t != null && (wantArray ? t is JArray : !(t is JArray))) return k;
            }
            return null;
        }

        private static bool BlankString(JToken t) => t == null || t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t);

        // One id key of a node and what is expected of every id it holds.
        private sealed class Binding
        {
            public string Key;
            public bool IsList;
            public bool Fill;                 // the id list was not sent: write the resolved ids under Key on commit
            public long?[] Ids;
            public string[] Uids, Categories, Types;
            public int Count => Uids.Length;
        }

        private static JObject Check(JObject o, string tool, string path, int index, Ctx ctx, bool root, DeleteScope ancestorDeleting = null)
        {
            bool loose = IdentityFields.Any(f => o[f] != null);
            JToken mapT = MapCarried(o, tool, path) ? o[MapField] : null;
            if (!loose && mapT == null) return null;

            if (root && PerAction.Contains(tool))
                return ArgError(path, index, "identity fields at the top level of " + tool + " would be consumed without protecting any action; " +
                                "send unique_id / identity inside each action (actions[i])");
            if (o["target"] != null)
                return ArgError(path, index, "an identity cannot be combined with target (Project Information has none)");

            var bindings = new List<Binding>();
            if (loose)
            {
                JObject e = LooseBinding(o, tool, path, index, bindings);
                if (e != null) return e;
            }
            if (mapT != null)
            {
                JObject e = MapBindings(o, mapT, path, index, bindings);
                if (e != null) return e;
            }

            // A node that names a link instance holds ids of the LINKED document (planimetry_ops notes[], tags over a link):
            // the host cannot vouch for them, so the only id of such a node that is checked is the link instance itself. Decided BEFORE
            // any id is looked up in the host: an id that exists in the host by coincidence must not be reported as an element mismatch
            // (R2S m-2).
            if (o["link_instance_id"] != null)
                foreach (Binding lb in bindings)
                    if (lb.Key != "link_instance_id" && LinkedDocumentKeys.Contains(IndexedKey.Match(lb.Key).Success ? IndexedKey.Match(lb.Key).Groups[1].Value : lb.Key))
                        return ArgError(path, index, "'" + lb.Key + "' sits in a node with link_instance_id, so it names an element of the LINKED document, which the active document cannot vouch for. " +
                            "Check only link_instance_id here, and verify the linked element in its own document");

            DeleteScope scope = DeleteScope.Merge(ancestorDeleting, ScopeOf(tool, o));
            var resolvedByBinding = new List<List<long>>();
            foreach (Binding b in bindings)
            {
                var resolved = new List<long>();
                for (int i = 0; i < b.Count; i++)
                {
                    ElementIdentityFacts found = Find(ctx, b.Ids?[i], b.Uids[i]);
                    if (!Matches(found, b.Uids[i], b.Categories[i], b.Types[i]))
                        return Error(path, index, b, i, found);
                    // A category / type alone cannot tell two elements of the same category apart. That is tolerated on the
                    // elements this bridge made (they carry the Horizun mark) and refused where a mistake cannot be undone or the element
                    // is somebody else's (R2S m-6). An unreadable mark is "not ours".
                    if (b.Uids[i] == null)
                    {
                        // R2U: a deletion; or ANY element of a collaborative (workshared) model, where a synchronize renumbers ids and other people's
                        // elements live; or, in a model that is not collaborative, an element that is not ours (user, ledger by UniqueId).
                        string why = scope != null && scope.Includes(b.Key)
                            ? WhyDeletes
                            : ctx.Collaborative
                                ? WhyCollaborative
                                : found.Owned != true
                                    ? WhyNotOurs.Replace("{id}", found.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                    : null;
                        if (why != null)
                            return ArgError(path, index, WeakRefusalTemplate.Replace("{why}", why).Replace("{id_key}", b.Key));
                    }
                    resolved.Add(found.Id);
                }
                resolvedByBinding.Add(resolved);
            }
            var canonical = new StringBuilder(path).Append('|');
            if (mapT != null) canonical.Append(MapField).Append('=').Append(CanonicalMap(mapT)).Append(';');
            foreach (string field in IdentityFields)
                if (o[field] != null) canonical.Append(field).Append('=').Append(o[field].ToString(Newtonsoft.Json.Formatting.None)).Append(';');
            ctx.Parts.Add(canonical.ToString());
            foreach (Binding wb in bindings)
                if (wb.Uids.Any(u => u == null)) ctx.Weak.Add(path.Length == 0 ? wb.Key : path + "." + wb.Key);
            ctx.Commit.Add(() =>
            {
                for (int k = 0; k < bindings.Count; k++)
                {
                    if (!bindings[k].Fill) continue;
                    o[bindings[k].Key] = bindings[k].IsList ? (JToken)new JArray(resolvedByBinding[k]) : new JValue(resolvedByBinding[k][0]);
                }
                foreach (string f in IdentityFields) o.Remove(f);
                o.Remove(MapField);
            });
            return null;
        }

        private static readonly Dictionary<string, HashSet<string>> DeletingOps = ToLookup(IdentityCoverage.DeletingOperations);
        private static readonly Dictionary<string, HashSet<string>> NotDeletingOps = ToLookup(IdentityCoverage.NotDeletingOperations);
        private static readonly Dictionary<string, string[]> DeletingIf = IdentityCoverage.DeletingConditions.ToDictionary(k => k.Key, k => k.Value.ToArray(), StringComparer.Ordinal);

        // tool -> operation -> the id keys that operation really deletes ("add_members=group_ids,type_id"); an operation not listed deletes every id it names.
        private static readonly Dictionary<string, Dictionary<string, HashSet<string>>> DeletingKeys = BuildDeletingKeys();

        private static Dictionary<string, Dictionary<string, HashSet<string>>> BuildDeletingKeys()
        {
            var table = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, IReadOnlyList<string>> row in IdentityCoverage.DeletingKeys)
            {
                var ops = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (string entry in row.Value)
                {
                    int eq = entry.IndexOf('=');
                    ops[entry.Substring(0, eq)] = new HashSet<string>(entry.Substring(eq + 1).Split(','), StringComparer.Ordinal);
                }
                table[row.Key] = ops;
            }
            return table;
        }

        private static Dictionary<string, HashSet<string>> ToLookup(IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> table) =>
            table.ToDictionary(k => k.Key, k => new HashSet<string>(k.Value, StringComparer.Ordinal), StringComparer.Ordinal);

        /// <summary>What a deleting node deletes: every id it names (Keys == null) or only the id keys listed for its operation.</summary>
        private sealed class DeleteScope
        {
            public static readonly DeleteScope Everything = new DeleteScope(null);
            public readonly HashSet<string> Keys;
            public DeleteScope(HashSet<string> keys) { Keys = keys; }
            public bool Includes(string key) => Keys == null || Keys.Contains(key);

            public static DeleteScope Merge(DeleteScope a, DeleteScope b)
            {
                if (a == null) return b;
                if (b == null) return a;
                if (a.Keys == null || b.Keys == null) return Everything;
                return new DeleteScope(new HashSet<string>(a.Keys.Concat(b.Keys), StringComparer.Ordinal));
            }
        }

        /// <summary>
        /// True when this node of this tool deletes what it is given: a covered tool that always does, an operation named like a deletion
        /// (<see cref="IdentityCoverage.DeleteWords"/>) unless the tool lists it in <see cref="IdentityCoverage.NotDeletingOperations"/> (a
        /// "remove" that detaches or un-binds without deleting the element it names), an operation the tool's
        /// <see cref="IdentityCoverage.DeletingOperations"/> row lists, or a node that matches one of its <see cref="IdentityCoverage.DeletingConditions"/>.
        /// This is the node's own test; the walk adds the operation of the ancestors (a child inherits "deletes" from the node that names the operation).
        /// </summary>
        public static bool IsDeleting(string tool, JObject node) => ScopeOf(tool, node) != null;

        private static DeleteScope ScopeOf(string tool, JObject node)
        {
            if (tool != null && IdentityCoverage.AlwaysDeletingTools.Contains(tool)) return DeleteScope.Everything;
            if (node == null) return null;
            DeletingOps.TryGetValue(tool ?? "", out HashSet<string> named);
            NotDeletingOps.TryGetValue(tool ?? "", out HashSet<string> notDeleting);
            Dictionary<string, HashSet<string>> keyed = null;
            if (tool != null) DeletingKeys.TryGetValue(tool, out keyed);
            DeleteScope scope = null;
            // 4.2: a key that detaches or removes what it names whatever the operation is called (manage_revisions remove_sheet_ids): the rule
            // applies to the ids that key holds, not to the revision that stays.
            foreach (string deleteKey in IdentityCoverage.DeleteKeys)
                if (node[deleteKey] != null && node[deleteKey].Type != JTokenType.Null)
                    scope = DeleteScope.Merge(scope, new DeleteScope(new HashSet<string>(new[] { deleteKey }, StringComparer.Ordinal)));
            foreach (string key in IdentityCoverage.OperationKeys)
            {
                if (node[key]?.Type != JTokenType.String) continue;
                string value = ((string)node[key]).Trim().ToLowerInvariant();
                if (named != null && named.Contains(value))
                {
                    HashSet<string> only = null;
                    if (keyed != null) keyed.TryGetValue(value, out only);
                    scope = DeleteScope.Merge(scope, only == null ? DeleteScope.Everything : new DeleteScope(only));
                    continue;
                }
                if (notDeleting != null && notDeleting.Contains(value)) continue;
                foreach (string part in value.Split('_'))
                    if (IdentityCoverage.DeleteWords.Contains(part)) { scope = DeleteScope.Everything; break; }
            }
            if (tool != null && DeletingIf.TryGetValue(tool, out string[] conditions))
                foreach (string condition in conditions)
                {
                    int eq = condition.IndexOf('=');
                    JToken at = node;
                    foreach (string step in condition.Substring(0, eq).Split('.')) at = (at as JObject)?[step];
                    if (at?.Type == JTokenType.String && string.Equals((string)at, condition.Substring(eq + 1), StringComparison.OrdinalIgnoreCase)) return DeleteScope.Everything;
                }
            return scope;
        }

        // The identity map in one canonical text: keys sorted at every level and the string shorthand written out as
        // {"unique_id": ...}, so the same identity sent in another order has the same digest (R2S m-9).
        private static string CanonicalMap(JToken map)
        {
            var o = map as JObject;
            if (o == null) return map.ToString(Newtonsoft.Json.Formatting.None);
            var sorted = new JObject();
            foreach (JProperty p in o.Properties().OrderBy(q => q.Name, StringComparer.Ordinal))
            {
                if (p.Value is JArray list) sorted[p.Name] = new JArray(list.Select(CanonExpectation));
                else sorted[p.Name] = CanonExpectation(p.Value);
            }
            return sorted.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JToken CanonExpectation(JToken t)
        {
            if (t.Type == JTokenType.String) return new JObject { ["unique_id"] = t.DeepClone() };
            if (t is JObject o)
            {
                var sorted = new JObject();
                foreach (JProperty p in o.Properties().OrderBy(q => q.Name, StringComparer.Ordinal))
                    sorted[p.Name] = p.Value.DeepClone();
                return sorted;
            }
            return t.DeepClone();
        }

        // The bare form: unique_id / unique_ids / expected_* beside the ids, checked against ONE key of the tool's list.
        private static JObject LooseBinding(JObject o, string tool, string path, int index, List<Binding> into)
        {
            JToken uidT = o["unique_id"], uidsT = o["unique_ids"], catT = o["expected_category"], typeT = o["expected_type"];
            if ((uidT != null && BlankString(uidT)) || (catT != null && BlankString(catT)) || (typeT != null && BlankString(typeT)))
                return ArgError(path, index, "unique_id, expected_category and expected_type must be non-empty strings");
            if (uidT != null && uidsT != null)
                return ArgError(path, index, "send unique_id or unique_ids, not both");

            Spec spec = SpecFor(tool, o);
            if (spec.Single.Length == 0 && spec.List.Length == 0)
                return ArgError(path, index, "this tool takes no element ids, so there is nothing to check an identity against");

            // m1: a key that should hold the id list but holds something else (a "${step.ids}" reference
            // that is resolved only later) cannot be verified, and must not be overwritten by unique_ids.
            foreach (string k in spec.List)
                if (o[k] != null && o[k].Type != JTokenType.Array && o[k].Type != JTokenType.Null)
                    return ArgError(path, index, k + " must be an array of integer ids; an unresolved reference cannot be checked against an identity");
            string listKey = FirstKey(o, spec.List, true);
            string idKey = FirstKey(o, spec.Single, false);
            string category = (string)catT, type = (string)typeT;

            // INT-9 m-2: the loose form binds ONE id key. A key that removes what it names (remove_sheet_ids) that is present but is not the key bound
            // would go unchecked, so it is refused: name every id key in identity: {<id_key>: ...}. (The keyed form binds each key it names.)
            foreach (string deleteKey in IdentityCoverage.DeleteKeys)
                if (deleteKey != listKey && o[deleteKey] is JArray removed && removed.Count > 0)
                    return ArgError(path, index, deleteKey + " removes what it names and the loose form (unique_id / unique_ids / expected_*) checks only ONE id key (" +
                        (listKey ?? idKey ?? "none") + "). Name each id key in identity: {<id_key>: <unique_id | {unique_id, expected_category, expected_type}>}, " + deleteKey + " included");
            // m5: an identity needs ids to be checked against. A selector, purge or query mode has none.
            if (listKey == null && uidsT == null && uidT == null && (idKey == null || o[idKey].Type == JTokenType.Null))
            {
                string mode = o["selector"] != null ? "a selector" : o["mode"] != null ? "mode '" + o["mode"] + "'" : "this node";
                return ArgError(path, index, "expected_category/expected_type need element ids to check, and " + mode +
                    " carries none (" + string.Join(", ", spec.List.Concat(spec.Single)) + "). Send the ids, or drop the identity fields");
            }

            if (listKey != null || uidsT != null)
            {
                if (uidT != null) return ArgError(path, index, "a list of ids takes unique_ids, not unique_id (to check one id key beside a list, name it in identity: {<id_key>: <unique_id>})");
                if (listKey == null && spec.DefaultList == null)
                    return ArgError(path, index, "unique_ids alone cannot be resolved for this tool; send the ids beside them (" + string.Join(", ", spec.List) + ")");
                JArray ids = listKey != null ? (JArray)o[listKey] : null;
                JArray uids = uidsT as JArray;
                if (uidsT != null && (uids == null || uids.Count == 0 || uids.Any(BlankString)))
                    return ArgError(path, index, "unique_ids must be a non-empty list of non-empty strings");
                if (ids != null && ids.Any(t => t.Type != JTokenType.Integer))
                    return ArgError(path, index, "ids must be integers (an unresolved reference cannot be checked against an identity)");
                if (ids != null && uids != null && ids.Count != uids.Count)
                    return ArgError(path, index, "ids and unique_ids must have the same length");
                int n = ids != null ? ids.Count : uids.Count;
                var b = new Binding
                {
                    Key = listKey ?? spec.DefaultList, IsList = true, Fill = listKey == null,
                    Ids = new long?[n], Uids = new string[n], Categories = new string[n], Types = new string[n]
                };
                for (int i = 0; i < n; i++)
                {
                    b.Ids[i] = ids != null ? (long?)(long)ids[i] : null;
                    b.Uids[i] = uids != null ? (string)uids[i] : null;
                    b.Categories[i] = category; b.Types[i] = type;
                }
                into.Add(b);
                return null;
            }

            JToken idToken = idKey != null ? o[idKey] : null;
            if (idToken != null && idToken.Type != JTokenType.Integer)
                return ArgError(path, index, "the id must be an integer (an unresolved reference cannot be checked against an identity)");
            if (idKey == null && spec.DefaultSingle == null)
                return ArgError(path, index, "unique_id alone cannot be resolved for this tool; send the id beside it (" + string.Join(", ", spec.Single) + ")");
            into.Add(new Binding
            {
                Key = idKey ?? spec.DefaultSingle, IsList = false, Fill = idToken == null,
                Ids = new long?[] { idToken != null ? (long?)(long)idToken : null },
                Uids = new[] { (string)uidT }, Categories = new[] { category }, Types = new[] { type }
            });
            return null;
        }

        // The keyed form: identity: { <id_key>: <unique_id | {unique_id, expected_category, expected_type}> }.
        private static JObject MapBindings(JObject o, JToken mapT, string path, int index, List<Binding> into)
        {
            var map = mapT as JObject;
            if (map == null || !map.HasValues)
                return ArgError(path, index, "identity must be an object {<id_key>: <unique_id | {unique_id, expected_category, expected_type}>}");
            foreach (JProperty p in map.Properties())
            {
                string key = p.Name;
                if (into.Any(b => b.Key == key))
                    return ArgError(path, index, "'" + key + "' is checked twice: once by identity and once by the bare unique_id / unique_ids fields; use one form");
                JToken idTok = IdAt(o, key);
                if (idTok == null || idTok.Type == JTokenType.Null)
                    return ArgError(path, index, "identity names '" + key + "', but this node has no '" + key + "' to check it against (id keys here: " +
                        string.Join(", ", o.Properties().Where(q => IsIdArgumentName(q.Name)).Select(q => q.Name)) + ")");

                if (idTok is JArray ids)
                {
                    if (ids.Count == 0 || ids.Any(t => t.Type != JTokenType.Integer))
                        return ArgError(path, index, key + " must be a non-empty array of integer ids (an unresolved reference cannot be checked against an identity)");
                    int n = ids.Count;
                    var b = new Binding { Key = key, IsList = true, Ids = new long?[n], Uids = new string[n], Categories = new string[n], Types = new string[n] };
                    for (int i = 0; i < n; i++) b.Ids[i] = (long)ids[i];
                    string err;
                    if (p.Value is JArray entries)
                    {
                        if (entries.Count != n) return ArgError(path, index, "identity." + key + " must have the same length as " + key + " (" + n + ")");
                        for (int i = 0; i < n; i++)
                        {
                            err = ParseExpectation(entries[i], false, out b.Uids[i], out b.Categories[i], out b.Types[i]);
                            if (err != null) return ArgError(path, index, "identity." + key + "[" + i + "]: " + err);
                        }
                    }
                    else if (p.Value is JObject shared)
                    {
                        foreach (JProperty q in shared.Properties())
                            if (q.Name != "unique_ids" && q.Name != "expected_category" && q.Name != "expected_type")
                                return ArgError(path, index, "identity." + key + " takes unique_ids, expected_category and expected_type, not '" + q.Name + "'" + Hint(q.Name));
                        JToken u = shared["unique_ids"];
                        if (u != null && (!(u is JArray ua) || ua.Count != n || ua.Any(BlankString)))
                            return ArgError(path, index, "identity." + key + ".unique_ids must be a list of non-empty strings, one per id of " + key + " (" + n + ")");
                        JToken c = shared["expected_category"], t = shared["expected_type"];
                        if ((c != null && BlankString(c)) || (t != null && BlankString(t)))
                            return ArgError(path, index, "identity." + key + ": expected_category and expected_type must be non-empty strings");
                        if (u == null && c == null && t == null) return ArgError(path, index, "identity." + key + " states nothing to check");
                        for (int i = 0; i < n; i++)
                        {
                            b.Uids[i] = u != null ? (string)((JArray)u)[i] : null;
                            b.Categories[i] = (string)c; b.Types[i] = (string)t;
                        }
                    }
                    else return ArgError(path, index, "identity." + key + " must be a list of unique_ids (or objects) aligned with " + key + ", or {unique_ids, expected_category, expected_type}");
                    into.Add(b);
                }
                else if (idTok.Type == JTokenType.Integer)
                {
                    var b = new Binding { Key = key, IsList = false, Ids = new long?[] { (long)idTok }, Uids = new string[1], Categories = new string[1], Types = new string[1] };
                    string err = ParseExpectation(p.Value, true, out b.Uids[0], out b.Categories[0], out b.Types[0]);
                    if (err != null) return ArgError(path, index, "identity." + key + ": " + err);
                    into.Add(b);
                }
                else
                    return ArgError(path, index, key + " must be an integer id or an array of them (an unresolved reference cannot be checked against an identity)");
            }
            return null;
        }

        private static readonly HashSet<string> LinkedDocumentKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "element_id", "element_ids", "ids", "target_id", "host_id", "host_ids", "linked_element_id", "linked_element_ids"
        };

        private static readonly Regex IndexedKey = new Regex(@"^([A-Za-z_][A-Za-z0-9_]*)(\[\d+\])+$", RegexOptions.CultureInvariant);

        // The id a key of the identity map names: a property of the node, or one element of a nested array of it written
        // like "pairs[0][1]" (join_geometry pairs: [[a, b], ...]). Null when there is no such id.
        private static JToken IdAt(JObject node, string key)
        {
            JToken direct = node[key];
            if (direct != null) return direct;
            Match m = IndexedKey.Match(key);
            if (!m.Success) return null;
            JToken t = node[m.Groups[1].Value];
            foreach (Capture c in m.Groups[2].Captures)
            {
                if (!(t is JArray a) || !int.TryParse(c.Value.Trim('[', ']'), out int i) || i >= a.Count) return null;
                t = a[i];
            }
            return t;
        }

        // A unique_id string, or {unique_id, expected_category, expected_type} with at least one of them.
        private static string Hint(string name) =>
            NearNames.TryGetValue(name.ToLowerInvariant().Replace("_", "").Replace("-", ""), out string right) ? ". Did you mean '" + right + "'?" : "";

        private static string ParseExpectation(JToken v, bool single, out string uid, out string category, out string type)
        {
            uid = category = type = null;
            if (v != null && v.Type == JTokenType.String)
            {
                if (BlankString(v)) return "the unique_id must be a non-empty string";
                uid = (string)v;
                return null;
            }
            var o = v as JObject;
            if (o == null) return "expected a unique_id string or {unique_id, expected_category, expected_type}";
            foreach (JProperty q in o.Properties())
                if (q.Name != "unique_id" && q.Name != "expected_category" && q.Name != "expected_type")
                    return "unknown property '" + q.Name + "' (allowed: unique_id, expected_category, expected_type)" + Hint(q.Name);
            if ((o["unique_id"] != null && BlankString(o["unique_id"])) || (o["expected_category"] != null && BlankString(o["expected_category"])) ||
                (o["expected_type"] != null && BlankString(o["expected_type"])))
                return "unique_id, expected_category and expected_type must be non-empty strings";
            if (o["unique_id"] == null && o["expected_category"] == null && o["expected_type"] == null) return "states nothing to check";
            uid = (string)o["unique_id"]; category = (string)o["expected_category"]; type = (string)o["expected_type"];
            return null;
        }

        private static ElementIdentityFacts Find(Ctx ctx, long? id, string uid)
        {
            try { return id.HasValue ? ctx.ById(id.Value) : uid != null ? ctx.ByUid(uid) : null; }
            catch { return null; }
        }

        private static string NormalizeLabel(string s) => Regex.Replace((s ?? "").Trim(), @"\s*:\s*", ": ");

        /// <summary>
        /// expected_type is the bare type name or "Family: Type", compared as a whole: two families may
        /// each have a type called "Standard", and only the second form tells them apart.
        /// </summary>
        public static bool TypeMatches(string expected, ElementIdentityFacts found)
        {
            if (expected == null) return true;
            if (found == null) return false;
            if (string.Equals(expected, found.Type, StringComparison.OrdinalIgnoreCase)) return true;
            return found.Family != null && found.Type != null &&
                   string.Equals(NormalizeLabel(expected), NormalizeLabel(found.Family + ": " + found.Type), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// expected_category is the category name as the model shows it (localized) or its BuiltInCategory
        /// (OST_Walls), which does not change with Revit's language.
        /// </summary>
        public static bool CategoryMatches(string expected, ElementIdentityFacts found)
        {
            if (expected == null) return true;
            if (found == null) return false;
            return string.Equals(expected, found.Category, StringComparison.OrdinalIgnoreCase) ||
                   (found.CategoryBuiltIn != null && string.Equals(expected, found.CategoryBuiltIn, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The category name an identity check and horizun_query_model both report: the element's own
        /// category, else - for a text note TYPE, which Revit gives none - the Text Notes category.
        /// One rule in one place, so an expected_category read from query_model never fails here.
        /// </summary>
        public static string CategoryNameFor(Func<string> ownCategoryName, bool isTextNoteType, Func<string> textNotesCategoryName)
        {
            string own = null;
            try { own = ownCategoryName?.Invoke(); } catch { }
            if (own != null) return own;
            if (!isTextNoteType) return null;
            string fallback = null;
            try { fallback = textNotesCategoryName?.Invoke(); } catch { }
            return fallback ?? "OST_TextNotes";
        }

        /// <summary>The ownership read of an element, failing closed: a reader that is missing or throws means "not ours".</summary>
        public static bool SafeOwned(Func<bool> read)
        {
            if (read == null) return false;
            try { return read(); }
            catch { return false; }
        }

        /// <summary>The BuiltInCategory name of an element; a text note TYPE has none in Revit, so it is OST_TextNotes. Null when unreadable.</summary>
        public static string BuiltInNameFor(Func<string> ownBuiltInName, bool isTextNoteType)
        {
            string own = null;
            try { own = ownBuiltInName?.Invoke(); } catch { own = null; }
            if (own == null && isTextNoteType) return "OST_TextNotes";
            return own;
        }

        private static bool Matches(ElementIdentityFacts found, string uid, string category, string type) =>
            found != null &&
            (uid == null || string.Equals(uid, found.UniqueId, StringComparison.Ordinal)) &&
            CategoryMatches(category, found) &&
            TypeMatches(type, found);

        private static JObject ArgError(string path, int index, string detail) => new JObject
        {
            ["error_code"] = ArgumentErrorCode,
            ["reason"] = InvalidArgs,
            ["index"] = index,
            ["path"] = path,
            ["detail"] = detail,
            ["write_started"] = false
        };

        private static JObject Error(string path, int index, Binding b, int position, ElementIdentityFacts found)
        {
            return new JObject
            {
                ["error_code"] = "precondicion_no_cumplida",
                ["reason"] = Mismatch,
                ["index"] = index,
                ["path"] = path,
                ["key"] = b.Key,
                ["position"] = b.IsList ? (JToken)position : null,
                ["detail"] = null,
                ["expected"] = new JObject { ["target_id"] = b.Ids?[position], ["unique_id"] = b.Uids[position],
                    ["category"] = b.Categories[position], ["type"] = b.Types[position] },
                ["found"] = found == null ? null : new JObject { ["target_id"] = found.Id, ["unique_id"] = found.UniqueId,
                    ["category"] = found.Category, ["type"] = found.Type, ["family"] = found.Family },
                ["write_started"] = false
            };
        }

        // ------------------------------------------------------------------ validate_ids (read-only)

        /// <summary>
        /// The note validate_ids puts in its reply. It lives here (not inside the command) so that a test can hold it against what Validate really answers (R2W):
        /// every field it names must be in the answer it describes, and it must say the same as the refusal (WeakRefusalTemplate) about where a unique_id comes from.
        /// </summary>
        public const string ValidateIdsNote =
            "Read-only, no transaction. Host elements only (an id inside a link is not an id of this document). " +
            "coincide = the same element (unique_id) with the expected category and type; cambio = the id now names " +
            "another element, the unique_id lives under another id (current_id; null when the unique_id no longer exists), or a category/type moved; " +
            "no_existe = nothing answers. Write with the same unique_id/expected_* fields to have the write refuse a mismatch. " +
            "found.unique_id appears ONLY when it is the unique_id you sent. Otherwise (a check sent with an id and NO unique_id, or an id that now names another element, id_reasignado) " +
            "found carries holder_unique_id (the element that holds that id today), identifies=false and a warning, because after a synchronize that id may name another element than " +
            "the one you read; the unique_id to write with is the one you read (or one you get by finding the element again by its content with query_model rows or list_elements), " +
            "never holder_unique_id.";

        public const string StatusMatch = "coincide";
        public const string StatusChanged = "cambio";
        public const string StatusMissing = "no_existe";
        /// <summary>An id with no unique_id and no expected_category/expected_type: nothing to compare, so nothing is proven.</summary>
        public const string StatusNoExpectation = "sin_expectativa";
        /// <summary>The category/type match, but no unique_id was sent: it does not detect a swap inside the same category or type.</summary>
        public const string StatusMatchCategory = "coincide_categoria";
        public const string StatusInvalid = "entrada_invalida";
        public const int MaxValidateChecks = 2000;

        /// <summary>
        /// Answer, for each {id and/or unique_id, expected_category, expected_type}, whether the model still
        /// holds what the caller read: coincide (same element, same expectations), cambio (the id now names
        /// another element, or the unique_id lives under another id, or a category/type moved) or no_existe.
        /// An entry with nothing to compare (an id alone) is sin_expectativa and never counts as a match.
        /// Pure: the lookups are passed in, nothing is written, nothing is normalized.
        /// </summary>
        public static JObject Validate(JArray checks, Func<long, ElementIdentityFacts> byId, Func<string, ElementIdentityFacts> byUniqueId)
        {
            var results = new JArray();
            int match = 0, changed = 0, missing = 0, invalid = 0, noExpectation = 0, matchCategory = 0;
            for (int i = 0; i < checks.Count; i++)
            {
                JObject row = ValidateOne(checks[i] as JObject, i, byId, byUniqueId);
                switch ((string)row["status"])
                {
                    case StatusMatch: match++; break;
                    case StatusMatchCategory: matchCategory++; break;
                    case StatusChanged: changed++; break;
                    case StatusMissing: missing++; break;
                    case StatusNoExpectation: noExpectation++; break;
                    default: invalid++; break;
                }
                results.Add(row);
            }
            return new JObject
            {
                ["summary"] = new JObject
                {
                    ["checked"] = checks.Count, [StatusMatch] = match, [StatusMatchCategory] = matchCategory, [StatusChanged] = changed,
                    [StatusMissing] = missing, [StatusNoExpectation] = noExpectation, [StatusInvalid] = invalid,
                    // Only a unique_id identifies an element: a category/type match alone never makes the set "all match".
                    ["all_match"] = match == checks.Count && checks.Count > 0
                },
                ["results"] = results
            };
        }

        private static JObject ValidateOne(JObject c, int index, Func<long, ElementIdentityFacts> byId, Func<string, ElementIdentityFacts> byUid)
        {
            string sentUid = c != null && c["unique_id"] is JValue sentValue && sentValue.Type == JTokenType.String ? (string)sentValue : null;
            JObject Result(string status, string reason, JObject expected, ElementIdentityFacts found, long? currentId = null, bool currentIdGiven = false)
            {
                var r = new JObject { ["index"] = index, ["status"] = status, ["reason"] = reason, ["expected"] = expected };
                // R2W B-2. found.unique_id is offered ONLY when it is the unique_id the caller sent (the element is the one asked for). Whatever else
                // lives under an id (an id sent alone, or an id reassigned to another element) is the holder: its unique_id is not an identity, because it
                // would be copied into a write and the write would pass the guard on the wrong element.
                bool genuine = found != null && sentUid != null && string.Equals(found.UniqueId, sentUid, StringComparison.Ordinal);
                if (found != null && !genuine)
                {
                    r["found"] = DescribeHolder(found);
                    r["identifies"] = false;
                    r["warning"] = sentUid == null
                        ? "id_sin_revalidar: holder_unique_id is the unique_id of the element that holds this id TODAY. It does not prove that it is the element you read " +
                          "before; a synchronize with central may have renumbered ids. It is not an identity: do not send it in a write. Send the unique_id you read."
                        : "id_reasignado: holder_unique_id is the unique_id of ANOTHER element that holds this id today, not the one you asked for (" + sentUid + "). It is not an identity: " +
                          "do not send it in a write. The element you asked for lives at current_id; current_id is null when its unique_id no longer exists.";
                }
                else r["found"] = found == null ? null : Describe(found);
                // R2Y M-1. current_id is where the element the caller ASKED FOR lives. When the answer says explicitly where that is (currentIdGiven), it is that
                // id, or null when the unique_id no longer exists: never the id of the element that took the old id (a write sent on it would land on the intruder).
                r["current_id"] = currentIdGiven ? (currentId.HasValue ? (JToken)currentId.Value : JValue.CreateNull())
                                : currentId.HasValue ? (JToken)currentId.Value : found == null ? null : (JToken)found.Id;
                return r;
            }
            if (c == null) return Result(StatusInvalid, "each check must be an object", null, null);

            JToken idT = c["id"] ?? c["element_id"], uidT = c["unique_id"], catT = c["expected_category"], typeT = c["expected_type"];
            var expected = new JObject { ["id"] = idT?.DeepClone(), ["unique_id"] = uidT?.DeepClone(),
                                         ["category"] = catT?.DeepClone(), ["type"] = typeT?.DeepClone() };
            if (idT == null && uidT == null) return Result(StatusInvalid, "send id or unique_id", expected, null);
            if (idT != null && idT.Type != JTokenType.Integer) return Result(StatusInvalid, "id must be an integer", expected, null);
            if ((uidT != null && BlankString(uidT)) || (catT != null && BlankString(catT)) || (typeT != null && BlankString(typeT)))
                return Result(StatusInvalid, "unique_id, expected_category and expected_type must be non-empty strings", expected, null);

            long? id = idT != null ? (long?)(long)idT : null;
            string uid = (string)uidT, category = (string)catT, type = (string)typeT;
            ElementIdentityFacts byIdFound = null, byUidFound = null;
            try { if (id.HasValue) byIdFound = byId(id.Value); } catch { }
            try { if (uid != null) byUidFound = byUid(uid); } catch { }

            ElementIdentityFacts element;
            if (id.HasValue && uid != null)
            {
                if (byIdFound != null && string.Equals(byIdFound.UniqueId, uid, StringComparison.Ordinal)) element = byIdFound;
                else if (byIdFound != null)
                    return Result(StatusChanged, byUidFound != null
                        ? "id_reasignado: the id names another element and the unique_id lives under id " + byUidFound.Id
                        : "id_reasignado: the id names another element and the unique_id no longer exists", expected, byIdFound, byUidFound?.Id, currentIdGiven: true);
                else if (byUidFound != null)
                    return Result(StatusChanged, "id_renumerado: nothing at that id now; the unique_id lives under id " + byUidFound.Id, expected, byUidFound, byUidFound.Id);
                else return Result(StatusMissing, "neither the id nor the unique_id exists", expected, null);
            }
            else element = id.HasValue ? byIdFound : byUidFound;

            if (element == null) return Result(StatusMissing, id.HasValue ? "no element has that id" : "no element has that unique_id", expected, null);
            if (uid == null && category == null && type == null)
                return Result(StatusNoExpectation, "sin_expectativa: an id with no unique_id, expected_category or expected_type has nothing to be compared with; " +
                    "an element exists under that id, which says nothing about whether it is the one you read. Send the unique_id you read with it", expected, element);
            var why = new List<string>();
            if (!CategoryMatches(category, element)) why.Add("category is '" + element.Category + "'");
            if (!TypeMatches(type, element)) why.Add("type is '" + (element.Family != null ? element.Family + ": " : "") + element.Type + "'");
            if (why.Count > 0) return Result(StatusChanged, "expectation not met: " + string.Join("; ", why), expected, element);
            return uid != null ? Result(StatusMatch, null, expected, element)
                : Result(StatusMatchCategory, "coincide_categoria: the category / type matches, but without a unique_id this cannot tell the element you read from another one of the same category or type " +
                    "(two text notes swapped by a synchronize both match). Send the unique_id you read", expected, element);
        }

        private static JObject Describe(ElementIdentityFacts f) => new JObject
        {
            ["id"] = f.Id, ["unique_id"] = f.UniqueId, ["category"] = f.Category, ["type"] = f.Type, ["family"] = f.Family
        };

        // The element that holds an id today, for a check that came without a unique_id: named holder_unique_id, never unique_id (R2V B-1).
        private static JObject DescribeHolder(ElementIdentityFacts f) => new JObject
        {
            ["id"] = f.Id, ["holder_unique_id"] = f.UniqueId, ["category"] = f.Category, ["type"] = f.Type, ["family"] = f.Family
        };
    }
}
