// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// WHAT WAS MISSING BETWEEN "REGISTERED" AND "INVOCABLE".
//
// The promotion registry records a script's generations, their hashes, their
// reviews and which one is active. That is provenance, and provenance is not
// promotion: nothing in it told a caller what to SEND, what would come BACK, or
// how to run it. A registry entry nobody can call is documentation.
//
// This closes that, and closes it WITHOUT opening an execution path:
//
//   THE CONTRACT. A generation declares its inputs and its output, as a small
//   JSON Schema each, and is refused at propose time if it does not. Without it
//   a caller reads the source to find out what the arguments are called, which
//   makes every promoted script a private API.
//
//   ACTIVE VERSION DISCOVERY. `Resolve` answers the one question a caller has:
//   which generation is live right now, what does it take, what does it give
//   back, and what is its hash. That hash is what makes a result attributable
//   afterwards - a run against "the payroll script" is not attributable; a run
//   against generation 4, sha256 9c1f…, is.
//
//   THE INVOCATION. `Build` returns a READY horizun_execute_python request:
//   the existing, authorised route, with the active source and the caller's
//   arguments bound. It runs nothing. The owner's Python grant still decides, the
//   same refusals still apply, and the result comes back labelled self-reported
//   exactly as any other script's does.
//
// THE SHAPE IS THE POINT. This file could have grown a `Run` method that called
// the dispatcher directly, and it would have been shorter and faster. It would
// also have made the promotion registry a way around the machine owner's
// decision about Python - which G17 warns about in as many words, and which no
// amount of checking inside this file could make safe, because the check would
// be the thing an attacker edits. There is no execution path here, and that is
// structural rather than careful.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What a generation takes and what it gives back, declared rather than discovered.</summary>
    public sealed class ScriptContract
    {
        /// <summary>A JSON Schema object for the arguments. Required.</summary>
        public JObject Inputs;

        /// <summary>A JSON Schema object for the structured __output__. Required.</summary>
        public JObject Output;

        /// <summary>
        /// The minimum permission profile a caller needs. Recorded, never enforced HERE:
        /// enforcement belongs to the one admission point, and a second one would be a second
        /// opinion about who may do what.
        /// </summary>
        public string MinimumPermission;

        /// <summary>The workflow-catalogue entry this script belongs to, when it belongs to one.</summary>
        public string WorkflowId;

        public JObject ToJson() => new JObject
        {
            ["inputs"] = Inputs,
            ["output"] = Output,
            ["minimum_permission"] = MinimumPermission,
            ["workflow_id"] = WorkflowId
        };

        public static ScriptContract FromJson(JObject json)
        {
            if (json == null) return null;
            return new ScriptContract
            {
                Inputs = json["inputs"] as JObject,
                Output = json["output"] as JObject,
                MinimumPermission = json.Value<string>("minimum_permission"),
                WorkflowId = json.Value<string>("workflow_id")
            };
        }
    }

    public static class ScriptInvocation
    {
        /// <summary>The permission profiles a promoted script may name, in increasing order.</summary>
        public static readonly string[] Permissions = { "read_only", "safe_write", "unsafe_code" };

        /// <summary>
        /// The name the arguments arrive under inside the script.
        ///
        /// It holds JSON TEXT, not a dict: the script calls json.loads itself. See the
        /// `arguments` description in horizun_execute_python's contract for why one name and
        /// why text.
        /// </summary>
        public const string ArgumentsVariable = "HORIZUN_ARGS_JSON";

        // =====================================================================
        // Validating a declared contract
        // =====================================================================

        /// <summary>Why this contract cannot be accepted, or null.</summary>
        public static string Validate(ScriptContract contract)
        {
            if (contract == null)
                return "a generation must declare its contract: what it takes, what it returns, and " +
                       "the minimum permission a caller needs. Without it, every promoted script is a " +
                       "private API somebody has to read the source of.";

            string problem = ValidateSchema(contract.Inputs, "inputs");
            if (problem != null) return problem;
            problem = ValidateSchema(contract.Output, "output");
            if (problem != null) return problem;

            if (string.IsNullOrWhiteSpace(contract.MinimumPermission))
                return "the contract must name the minimum permission profile this script needs: " +
                       string.Join(", ", Permissions) + ".";
            if (!Permissions.Contains(contract.MinimumPermission, StringComparer.Ordinal))
                return "minimum_permission is '" + contract.MinimumPermission + "'; it must be one of " +
                       string.Join(", ", Permissions) + ".";

            // A PROMOTED SCRIPT IS PYTHON, and Python runs under unsafe_code. Declaring less is
            // not a smaller permission, it is a wrong statement about what running it needs, and
            // a catalogue built from those statements would tell a reader that a read_only
            // session can call this. It cannot.
            if (contract.MinimumPermission != "unsafe_code")
                return "minimum_permission is '" + contract.MinimumPermission + "', and a promoted " +
                       "script is Python: it runs through horizun_execute_python, which needs " +
                       "'unsafe_code' and the machine owner's grant. Declaring less would tell a " +
                       "reader that a lesser session can call this, and it cannot. Promotion buys " +
                       "provenance and review; it buys no rights.";

            return null;
        }

        private static string ValidateSchema(JObject schema, string which)
        {
            if (schema == null)
                return "the contract declares no '" + which + "' schema.";
            string type = schema.Value<string>("type");
            if (type != "object")
                return "the '" + which + "' schema must declare \"type\": \"object\"; it declares " +
                       (type == null ? "nothing" : "'" + type + "'") + ".";
            if (!(schema["properties"] is JObject properties))
                return "the '" + which + "' schema declares no properties object. A schema that " +
                       "describes nothing accepts everything, which is the same as having none.";
            if (properties.Count == 0 && which == "output")
                return "the 'output' schema declares no properties. A script whose output shape is " +
                       "unstated is a script whose result nobody can check, and the whole reason " +
                       "the Python path labels its results self-reported is that somebody has to.";
            return null;
        }

        /// <summary>Why these arguments do not satisfy the declared input schema, or null.</summary>
        public static string CheckArguments(ScriptContract contract, JObject arguments)
        {
            if (contract == null || contract.Inputs == null) return "this generation declares no contract.";
            arguments = arguments ?? new JObject();

            var problems = new List<string>();
            var required = (contract.Inputs["required"] as JArray ?? new JArray())
                .Values<string>().Where(n => n != null).ToList();
            foreach (string name in required)
                if (arguments[name] == null || arguments[name].Type == JTokenType.Null)
                    problems.Add("'" + name + "' is required and was not given");

            var declared = contract.Inputs["properties"] as JObject ?? new JObject();
            bool closed = contract.Inputs["additionalProperties"] != null &&
                          contract.Inputs["additionalProperties"].Type == JTokenType.Boolean &&
                          !contract.Inputs.Value<bool>("additionalProperties");
            foreach (JProperty property in arguments.Properties())
            {
                JObject expected = declared[property.Name] as JObject;
                if (expected == null)
                {
                    if (closed)
                        // AN UNDECLARED ARGUMENT IS NOT HARMLESS. It is silently dropped, the
                        // script runs with a default nobody chose, and the reply looks like a
                        // success. A typo in an argument name is the commonest way that happens.
                        problems.Add("'" + property.Name + "' is not in the declared inputs, and this " +
                                     "contract is closed. An argument nothing reads is a default " +
                                     "nobody chose");
                    continue;
                }
                string wanted = expected.Value<string>("type");
                string actual = JsonTypeName(property.Value);
                if (wanted != null && actual != null && wanted != actual &&
                    !(wanted == "number" && actual == "integer"))
                    problems.Add("'" + property.Name + "' is declared " + wanted + " and arrived " + actual);
            }

            return problems.Count == 0 ? null : string.Join("; ", problems);
        }

        private static string JsonTypeName(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object: return "object";
                case JTokenType.Array: return "array";
                case JTokenType.Integer: return "integer";
                case JTokenType.Float: return "number";
                case JTokenType.String: return "string";
                case JTokenType.Boolean: return "boolean";
                case JTokenType.Null: return "null";
                default: return null;
            }
        }

        // =====================================================================
        // Discovery
        // =====================================================================

        public sealed class Resolution
        {
            public string ScriptId;
            public string Title;
            public int Generation;
            public string Sha256;
            public string ActivatedFromState;
            public ScriptContract Contract;
            public string SourcePath;
            public string Refusal;

            public JObject ToJson() => new JObject
            {
                ["script_id"] = ScriptId,
                ["title"] = Title,
                ["active_generation"] = Generation,
                ["sha256"] = Sha256,
                ["state"] = ActivatedFromState,
                ["contract"] = Contract == null ? null : Contract.ToJson(),
                ["source_path"] = SourcePath,
                ["means"] =
                    "The hash is what makes a later result attributable. A run against 'the script' " +
                    "cannot be traced; a run against generation " + Generation + ", sha256 " +
                    (Sha256 ?? "(none)") + ", can."
            };
        }

        /// <summary>
        /// Which generation is live, what it takes and what it returns.
        ///
        /// A script with NOTHING active resolves to a refusal rather than to its newest
        /// generation. The newest is usually the one somebody is still reviewing, and quietly
        /// serving it would defeat the entire review ladder.
        /// </summary>
        public static Resolution Resolve(PromotedScript script, ScriptContract contract)
        {
            if (script == null)
                return new Resolution { Refusal = "no such promoted script." };

            ScriptGeneration active = script.Active;
            if (active == null)
                return new Resolution
                {
                    ScriptId = script.Id,
                    Title = script.Title,
                    Refusal = "this script has no ACTIVE generation. It holds " +
                              script.Generations.Count + " generation(s), the newest of which is '" +
                              (script.Latest == null ? "none" : script.Latest.State) +
                              "'. Serving the newest instead would defeat the review ladder: " +
                              "propose, review, approve, activate - and nothing is born approved."
                };

            return new Resolution
            {
                ScriptId = script.Id,
                Title = script.Title,
                Generation = active.Number,
                Sha256 = active.Sha256,
                ActivatedFromState = active.State,
                Contract = contract,
                SourcePath = ScriptPromotion.SourceFor(script.Id, active.Number)
            };
        }

        // =====================================================================
        // The invocation
        // =====================================================================

        /// <summary>
        /// A ready horizun_execute_python request, bound to the active generation.
        ///
        /// IT RETURNS A REQUEST. It does not send one, and this file holds no reference to
        /// anything that could. The caller sends it, the owner's Python grant decides, and the
        /// result comes back with the same self-reported labelling every other script gets.
        ///
        /// THE SOURCE TRAVELS AS A PATH, not as a string. A promoted script can be hundreds of
        /// lines; inlining it puts the whole body into the request, the log and the transcript,
        /// and the bridge's own guidance says to send long scripts as code_path.
        /// </summary>
        public static JObject Build(Resolution resolution, JObject arguments, string targetDocument,
                                   out string refusal)
        {
            refusal = null;
            if (resolution == null || resolution.Refusal != null)
            {
                refusal = resolution == null ? "nothing was resolved." : resolution.Refusal;
                return null;
            }
            if (string.IsNullOrWhiteSpace(targetDocument))
            {
                // NOT A DETAIL. horizun_execute_python REQUIRES target_document and matches it
                // against the ACTIVE document, because "the active document" is whatever window
                // was in front when the call arrived - and on a machine with two Revit hosts
                // open that is not a question a script should answer by accident.
                refusal = "target_document is required: horizun_execute_python matches it against " +
                          "the ACTIVE document, and a promoted script pointed at whatever window " +
                          "happened to be in front is the failure this bridge refuses everywhere " +
                          "else. Nothing was built.";
                return null;
            }

            refusal = CheckArguments(resolution.Contract, arguments);
            if (refusal != null)
            {
                refusal = "the arguments do not satisfy generation " + resolution.Generation +
                          "'s declared contract: " + refusal + ". Nothing was built.";
                return null;
            }

            return new JObject
            {
                ["tool"] = "horizun_execute_python",
                ["arguments"] = new JObject
                {
                    ["code_path"] = resolution.SourcePath,
                    ["target_document"] = targetDocument,
                    // The arguments arrive as ONE variable holding JSON TEXT, under the name
                    // this file and the contract both spell once. A script that reads
                    // HORIZUN_ARGS_JSON declares its dependency, and a caller whose argument
                    // happens to be called `doc` cannot shadow the name every Revit script
                    // expects.
                    ["arguments"] = arguments ?? new JObject(),
                    // PREFLIGHT FIRST, ALWAYS, for a promoted script. It has been reviewed and
                    // approved, which says somebody read it - not that it still parses against
                    // the Python this Revit hosts. Those are different facts and the second is
                    // free to check.
                    ["preflight"] = true
                },
                ["provenance"] = new JObject
                {
                    ["script_id"] = resolution.ScriptId,
                    ["generation"] = resolution.Generation,
                    ["sha256"] = resolution.Sha256
                },
                ["expects"] = resolution.Contract == null ? null : resolution.Contract.Output,
                ["means"] =
                    "This is a REQUEST, not a result. horizun_promote_script executes nothing: send " +
                    "this to horizun_execute_python, where the machine owner's Python grant decides " +
                    "as it does for any other script. Promotion buys provenance, review and " +
                    "versions; it buys no rights. What comes back is self-reported by the script, " +
                    "not verified by this bridge, and the 'expects' schema above is what it CLAIMED " +
                    "it would return - checking the reply against it is the caller's step. The " +
                    "arguments arrive inside the script as " + ArgumentsVariable + ", a JSON " +
                    "string it parses itself.",
                ["not_verified"] =
                    "Nothing here checked that the active source still hashes to " +
                    (resolution.Sha256 ?? "(none)") + ". horizun_promote_script operation=source does " +
                    "that check when it reads the file; if the bytes on disk have been edited since " +
                    "approval, that is where it is caught."
            };
        }
    }
}
