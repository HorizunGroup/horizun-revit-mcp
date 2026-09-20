// -----------------------------------------------------------------------------
// Horizun MCP - the promotion registry's surface. Original Horizun code.
//
// HOST-RESIDENT and READ/WRITE ON FILES ONLY. Nothing here runs anything, and
// that is a deliberate structural fact rather than an omission: G17 warns in as
// many words that a promotion mechanism must never become a way around the owner's
// authorization for Python, and the simplest way to keep that true is that this
// code contains no execution path at all.
//
// A published script is handed BACK to the caller, who runs it through
// horizun_execute_python like any other script - under the machine owner's
// persistent grant, with the same refusals, the same self-reported evidence and
// the same labelling. Promotion buys provenance, review and versions. It buys no
// rights.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Server
{
    internal static class ScriptPromotionTool
    {
        private static readonly object Gate = new object();

        public static JObject Handle(JObject arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string operation = (arguments?.Value<string>("operation") ?? "list").ToLowerInvariant();
            string nowUtc = DateTime.UtcNow.ToString("o");

            lock (Gate)
            {
                switch (operation)
                {
                    case "list": return List();
                    case "show": return Show(arguments);
                    case "propose": return Propose(arguments, nowUtc);
                    case "review": return Review(arguments, nowUtc);
                    case "approve": return Approve(arguments, nowUtc);
                    case "activate": return Activate(arguments);
                    case "deactivate": return Deactivate(arguments, nowUtc);
                    case "source": return Source(arguments);
                    case "resolve": return Resolve(arguments);
                    case "invocation": return Invocation(arguments);
                    default:
                        // The list is the whole list. A refusal that names six of the ten
                        // operations sends the reader to the schema to find out what it left
                        // out, which is the one thing an error message exists to save them.
                        throw new ToolRefusal(
                            "operation must be list, show, propose, review, approve, activate, deactivate, " +
                            "source, resolve or invocation; '" + operation + "' is not one of them.");
                }
            }
        }

        // =====================================================================

        private static JObject List()
        {
            var rows = new JArray();
            foreach (string id in ScriptPromotion.Ids())
            {
                PromotedScript script = ScriptPromotion.Load(id);
                if (script == null) continue;
                rows.Add(new JObject
                {
                    ["id"] = script.Id,
                    ["title"] = script.Title,
                    ["generations"] = script.Generations.Count,
                    ["active_generation"] = script.ActiveGeneration,
                    ["latest_state"] = script.Latest?.State,
                    // WITHDRAWN AND NEVER-PUBLISHED both show active_generation 0 and are
                    // different facts. The row says which.
                    ["status"] = script.IsDeactivated
                        ? "withdrawn"
                        : script.ActiveGeneration > 0 ? "serving" : "nothing published yet",
                    ["withdrawn_utc"] = script.DeactivatedUtc,
                    ["withdrawn_reason"] = script.DeactivationReason
                });
            }
            return new JObject
            {
                ["root"] = ScriptPromotion.Root(),
                ["promotions"] = rows,
                ["means"] =
                    "A promoted script is Python and runs through horizun_execute_python under the machine " +
                    "owner's grant. This registry adds provenance, review and versions; it adds no permission " +
                    "and it executes nothing."
            };
        }

        private static JObject Show(JObject arguments)
        {
            PromotedScript script = Required(arguments);
            return new JObject
            {
                ["id"] = script.Id,
                ["title"] = script.Title,
                ["description"] = script.Description,
                ["created_utc"] = script.CreatedUtc,
                ["active_generation"] = script.ActiveGeneration,
                ["status"] = script.IsDeactivated
                    ? "withdrawn"
                    : script.ActiveGeneration > 0 ? "serving" : "nothing published yet",
                ["withdrawn"] = script.IsDeactivated
                    ? new JObject
                    {
                        ["utc"] = script.DeactivatedUtc,
                        ["by"] = script.DeactivatedBy,
                        ["reason"] = script.DeactivationReason,
                        ["generation_that_was_live"] = script.DeactivatedFromGeneration,
                        ["means"] =
                            "every generation is still on disk and none of them is being served. Activating " +
                            "one puts it back, re-hashed against what was reviewed. This says nothing about " +
                            "the owner's Python grant, which is a separate decision by a separate person."
                    }
                    // The file's own idiom for an absent object. A raw null here relies on
                    // Json.NET converting it, and the rest of this code does not rely on that.
                    : (JToken)JValue.CreateNull(),
                ["generations"] = new JArray(script.Generations.OrderBy(g => g.Number).Select(g => new JObject
                {
                    ["number"] = g.Number,
                    ["state"] = g.State,
                    ["sha256"] = g.Sha256,
                    ["bytes"] = g.Bytes,
                    ["author"] = g.Author,
                    ["created_utc"] = g.CreatedUtc,
                    ["evidence"] = g.Evidence,
                    ["reviewed_by"] = g.ReviewedBy,
                    ["review_note"] = g.ReviewNote,
                    ["approved_by"] = g.ApprovedBy
                }))
            };
        }

        private static JObject Propose(JObject arguments, string nowUtc)
        {
            string id = arguments?.Value<string>("id");
            string idError = ScriptPromotion.ValidateId(id);
            if (idError != null) throw new ToolRefusal(idError);

            PromotedScript script = ScriptPromotion.Load(id);
            if (script == null)
                script = new PromotedScript
                {
                    Id = id,
                    Title = arguments?.Value<string>("title") ?? id,
                    Description = arguments?.Value<string>("description"),
                    CreatedUtc = nowUtc,
                    ActiveGeneration = 0
                };

            ScriptGeneration added;
            string error = ScriptPromotion.AddGeneration(
                script, arguments?.Value<string>("source"), arguments?.Value<string>("author"),
                arguments?.Value<string>("evidence"), arguments?["contract"] as JObject,
                nowUtc, out added);
            if (error != null) throw new ToolRefusal(error);

            ScriptPromotion.Save(script);
            return new JObject
            {
                ["id"] = script.Id,
                ["generation"] = added.Number,
                ["state"] = added.State,
                ["sha256"] = added.Sha256,
                ["active_generation"] = script.ActiveGeneration,
                ["means"] =
                    "recorded as PROPOSED. Nothing is born approved, including a correction to something that " +
                    "was. It becomes reviewable once it records evidence of having been tested, and only " +
                    "somebody other than its author can review it."
            };
        }

        private static JObject Review(JObject arguments, string nowUtc)
        {
            PromotedScript script = Required(arguments);
            ScriptGeneration generation = Generation(script, arguments);
            string error = ScriptPromotion.Review(generation, arguments?.Value<string>("reviewer"),
                                                  arguments?.Value<string>("note"), nowUtc);
            if (error != null) throw new ToolRefusal(error);
            ScriptPromotion.Save(script);
            return new JObject
            {
                ["id"] = script.Id,
                ["generation"] = generation.Number,
                ["state"] = generation.State,
                ["reviewed_by"] = generation.ReviewedBy
            };
        }

        private static JObject Approve(JObject arguments, string nowUtc)
        {
            PromotedScript script = Required(arguments);
            ScriptGeneration generation = Generation(script, arguments);
            string error = ScriptPromotion.Approve(generation, arguments?.Value<string>("approver"), nowUtc);
            if (error != null) throw new ToolRefusal(error);
            ScriptPromotion.Save(script);
            return new JObject
            {
                ["id"] = script.Id,
                ["generation"] = generation.Number,
                ["state"] = generation.State,
                ["approved_by"] = generation.ApprovedBy,
                ["means"] = "approved, and not yet active. Activation is a separate act."
            };
        }

        private static JObject Activate(JObject arguments)
        {
            PromotedScript script = Required(arguments);
            int number = arguments?.Value<int?>("generation") ?? -1;
            int previous;
            string error = ScriptPromotion.Activate(script, number, out previous);
            if (error != null) throw new ToolRefusal(error);
            ScriptPromotion.Save(script);
            return new JObject
            {
                ["id"] = script.Id,
                ["active_generation"] = script.ActiveGeneration,
                ["previous_generation"] = previous,
                ["means"] =
                    "the pointer moved; the previous generation's bytes are still on disk and untouched. " +
                    "Activation checks that the new generation's source still hashes to what was reviewed, so " +
                    "a file edited after approval leaves the old generation active rather than silently " +
                    "publishing code nobody read."
            };
        }

        /// <summary>
        /// Withdraw the script. Keeps every generation; records who and why.
        ///
        /// This is the operation that was missing, and its absence had a shape: the only
        /// way to stop serving a harmful generation was to activate a different one.
        /// </summary>
        private static JObject Deactivate(JObject arguments, string nowUtc)
        {
            PromotedScript script = Required(arguments);
            int previous;
            string refusal = ScriptPromotion.Deactivate(
                script,
                arguments?.Value<string>("by"),
                arguments?.Value<string>("reason"),
                nowUtc,
                out previous);
            if (refusal != null) throw new ToolRefusal(refusal);
            ScriptPromotion.Save(script);

            return new JObject
            {
                ["id"] = script.Id,
                ["withdrawn_generation"] = previous,
                ["active_generation"] = 0,
                ["deactivated_utc"] = script.DeactivatedUtc,
                ["deactivated_by"] = script.DeactivatedBy,
                ["reason"] = script.DeactivationReason,
                ["generations_kept"] = script.Generations.Count,
                ["means"] =
                    "nothing was deleted. source, resolve and invocation now refuse and SAY this was withdrawn, " +
                    "by whom and why - rather than reporting it as never published, which is a different fact " +
                    "about a different situation. Activating a generation puts it back in service and re-hashes " +
                    "it against what was reviewed first.",
                ["not_a_permission_change"] =
                    "this changed what this registry serves. It changed nothing about the machine owner's " +
                    "Python grant, which is the only thing that decides whether any script runs."
            };
        }

        private static JObject Source(JObject arguments)
        {
            PromotedScript script = Required(arguments);
            string reason;
            string source = ScriptPromotion.ActiveSource(script, out reason);
            if (source == null) throw new ToolRefusal(reason);

            ScriptGeneration active = script.Active;
            return new JObject
            {
                ["id"] = script.Id,
                ["generation"] = active.Number,
                ["sha256"] = active.Sha256,
                ["reviewed_by"] = active.ReviewedBy,
                ["approved_by"] = active.ApprovedBy,
                ["source"] = source,
                ["how_to_run"] =
                    "Hand this to horizun_execute_python. It is Python, it needs the machine owner's Python " +
                    "grant exactly as any other script does, and its result comes back self-reported - " +
                    "self_reported_verified at best, never host-verified. Promotion changed none of that.",
                ["means"] =
                    "the bytes were re-hashed against what was reviewed before being returned. A source edited " +
                    "on disk after approval is refused rather than handed back, because a registry whose whole " +
                    "purpose is that somebody read the code must not serve code nobody read."
            };
        }

        // =====================================================================

        private static PromotedScript Required(JObject arguments)
        {
            string id = arguments?.Value<string>("id");
            string idError = ScriptPromotion.ValidateId(id);
            if (idError != null) throw new ToolRefusal(idError);
            PromotedScript script = ScriptPromotion.Load(id);
            if (script == null) throw new ToolRefusal("no promotion with id '" + id + "'.");
            return script;
        }

        /// <summary>
        /// A withdrawn script is refused with the withdrawal, not with silence.
        ///
        /// Without this, ScriptInvocation reports 'no active generation', which is what it
        /// says about a script nobody ever approved. The caller then goes looking for a
        /// review that already happened instead of reading why somebody pulled it.
        /// </summary>
        private static void RefuseIfWithdrawn(PromotedScript script)
        {
            if (script == null || !script.IsDeactivated) return;
            throw new ToolRefusal(
                "'" + script.Id + "' was WITHDRAWN on " + script.DeactivatedUtc + " by " +
                (script.DeactivatedBy ?? "an unrecorded person") + ": " +
                (script.DeactivationReason ?? "no reason was recorded") + ". Generation " +
                script.DeactivatedFromGeneration + " was live until then and is still on disk. Nothing is " +
                "returned while it stays withdrawn; operation=activate puts a generation back in service.");
        }

        private static ScriptGeneration Generation(PromotedScript script, JObject arguments)
        {
            int number = arguments?.Value<int?>("generation") ?? -1;
            ScriptGeneration generation = script.Generations.FirstOrDefault(g => g.Number == number);
            if (generation == null)
                throw new ToolRefusal(
                    "generation is required and must be one this promotion has: " +
                    string.Join(", ", script.Generations.Select(g => g.Number.ToString())) + ".");
            return generation;
        }

        // =====================================================================
        // Discovery and invocation
        // =====================================================================

        /// <summary>
        /// Which generation is LIVE, what it takes, what it returns, and its hash.
        ///
        /// The one question a caller actually has, and the registry could not answer it: it
        /// could list generations and their states, and nothing said "send this shape to
        /// that source". The hash travels because it is what makes a later result
        /// attributable - a run against "the setout script" cannot be traced, and a run
        /// against generation 4, sha256 9c1f..., can.
        /// </summary>
        private static JObject Resolve(JObject arguments)
        {
            string id = arguments?.Value<string>("id");
            string invalid = ScriptPromotion.ValidateId(id);
            if (invalid != null) throw new ToolRefusal(invalid);

            PromotedScript script = ScriptPromotion.Load(id);
            if (script == null) throw new ToolRefusal("no promoted script with id '" + id + "'.");

            RefuseIfWithdrawn(script);

            ScriptGeneration active = script.Active;
            ScriptContract contract = active == null ? null : ScriptContract.FromJson(active.Contract);
            ScriptInvocation.Resolution resolution = ScriptInvocation.Resolve(script, contract);
            if (resolution.Refusal != null) throw new ToolRefusal(resolution.Refusal);

            JObject payload = resolution.ToJson();
            payload["generations"] = script.Generations.Count;
            payload["how_to_call"] =
                "operation=invocation with the same id, your arguments and a target_document " +
                "returns a ready horizun_execute_python request. THIS TOOL RUNS NOTHING: you " +
                "send that request, and the machine owner's Python grant decides, exactly as it " +
                "does for any other script.";
            return payload;
        }

        /// <summary>
        /// A ready horizun_execute_python request for the active generation.
        ///
        /// IT RETURNS A REQUEST AND SENDS NOTHING, and that is structural rather than
        /// careful: this file holds no reference to anything that could execute. G17 warns
        /// that a promotion mechanism must never become a way around the owner's decision
        /// about Python, and the only way to keep that true under pressure is for the path
        /// not to exist.
        /// </summary>
        private static JObject Invocation(JObject arguments)
        {
            string id = arguments?.Value<string>("id");
            string invalid = ScriptPromotion.ValidateId(id);
            if (invalid != null) throw new ToolRefusal(invalid);

            PromotedScript script = ScriptPromotion.Load(id);
            if (script == null) throw new ToolRefusal("no promoted script with id '" + id + "'.");

            RefuseIfWithdrawn(script);

            ScriptGeneration active = script.Active;
            ScriptContract contract = active == null ? null : ScriptContract.FromJson(active.Contract);
            ScriptInvocation.Resolution resolution = ScriptInvocation.Resolve(script, contract);
            if (resolution.Refusal != null) throw new ToolRefusal(resolution.Refusal);

            string refusal;
            JObject request = ScriptInvocation.Build(
                resolution,
                arguments?["arguments"] as JObject,
                arguments?.Value<string>("target_document"),
                out refusal);
            if (request == null) throw new ToolRefusal(refusal);
            return request;
        }

    }
}
