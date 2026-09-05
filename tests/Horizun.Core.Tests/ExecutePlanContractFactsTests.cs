// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE PUBLISHED CONTRACT OF execute_plan, CHECKED AGAINST WHAT IT ACTUALLY DOES.
//
// The three earlier audit passes examined the code and never the description a
// client reads. Two things came out of doing that, and they pull in opposite
// directions:
//
//   * a drift guard that was missing. The `tool` enum in the schema and the
//     `Allowed` HashSet in ExecutePlanCommand are two copies of one fact and
//     nothing made them meet. They agree today; nothing kept them agreeing.
//
//   * a mismatch that is REAL and is NOT fixed here, because fixing it changes a
//     published annotation: horizun_execute_plan is not in the contract's
//     `destructive` set, while its own allowlist contains horizun_delete_verified.
//     A client that gates on destructiveHint treats the composing surface as the
//     safe one. See Destructive_hint_does_not_cover... below.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ExecutePlanContractFactsTests
    {
        private static CommandContract Plan()
            => Contract.All.Single(c => c.Name == "horizun_execute_plan");

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        /// <summary>The allowlist as the COMMAND enforces it, read from its source.</summary>
        private static HashSet<string> EnforcedAllowlist()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit",
                                                       "Commands", "ExecutePlanCommand.cs"));
            int start = src.IndexOf("HashSet<string> Allowed", StringComparison.Ordinal);
            int open = src.IndexOf('{', start);
            int close = src.IndexOf("};", open, StringComparison.Ordinal);
            return new HashSet<string>(
                Regex.Matches(src.Substring(open, close - open), "\"(horizun_[a-z_]+)\"")
                     .Cast<Match>().Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        }

        /// <summary>...and as the SCHEMA advertises it.</summary>
        private static HashSet<string> AdvertisedAllowlist()
        {
            JToken items = Plan().InputSchema["properties"]["actions"]["items"];
            return new HashSet<string>(items["properties"]["tool"]["enum"].Select(t => (string)t),
                                       StringComparer.Ordinal);
        }

        // ---- The drift guard that was missing -----------------------------------

        [Fact]
        public void The_advertised_tool_list_is_exactly_the_one_the_command_enforces()
        {
            HashSet<string> enforced = EnforcedAllowlist();
            HashSet<string> advertised = AdvertisedAllowlist();

            // A tool in the schema but not in the HashSet is a published capability the
            // command refuses at runtime. One in the HashSet but not the schema is a
            // capability no client is told about, and a strict validator rejects.
            Assert.Empty(advertised.Except(enforced));
            Assert.Empty(enforced.Except(advertised));
            // 21 since horizun_manage_links joined: the correction registry's pin
            // could not be composed atomically without it.
            Assert.Equal(21, enforced.Count);
        }

        // ---- The claims the description makes, one by one ------------------------

        [Fact]
        public void The_hundred_action_ceiling_is_the_one_the_schema_publishes()
        {
            JToken actions = Plan().InputSchema["properties"]["actions"];

            Assert.Equal(1, (int)actions["minItems"]);
            Assert.Equal(100, (int)actions["maxItems"]);
            Assert.Contains("up to 100", Plan().Description);
        }

        [Fact]
        public void The_three_things_the_description_says_are_excluded_really_are()
        {
            HashSet<string> advertised = AdvertisedAllowlist();

            foreach (string excluded in new[] { "horizun_document_session", "horizun_export",
                                                "horizun_execute_python" })
                Assert.DoesNotContain(excluded, advertised);
        }

        [Fact]
        public void An_apply_can_be_expressed_within_the_published_schema()
        {
            // The dispatcher REFUSES a mutating call without idempotency_key, and the
            // schema says additionalProperties:false. If the key were not a declared
            // property, a client validating strictly could not send a legal apply at all.
            // It is injected when the contract is built rather than written in the literal,
            // which is why this is asserted on Contract.All and not on the source text.
            JObject properties = (JObject)Plan().InputSchema["properties"];

            Assert.NotNull(properties["idempotency_key"]);
            Assert.Equal("string", (string)properties["idempotency_key"]["type"]);
            Assert.False((bool)Plan().InputSchema["additionalProperties"]);
            Assert.NotNull(properties["confirmation_token"]);
            Assert.NotNull(properties["dry_run"]);
            Assert.Equal(ToolEffect.MutatingUnlessDryRun, Plan().Effect);
        }

        [Fact]
        public void Transaction_name_is_published_as_the_cosmetic_field_the_plan_hash_ignores()
        {
            // PlanConfirmationScopeTests proves the hash ignores it. This proves the schema
            // publishes it, so the pair "declared but not binding" is deliberate and visible
            // rather than an omission somebody has to infer.
            Assert.NotNull(Plan().InputSchema["properties"]["transaction_name"]);
        }

        // ---- Destructive composition and honest authorization --------------------

        [Fact]
        public void Destructive_hint_covers_the_plan_because_it_can_delete()
        {
            Assert.True(Plan().Destructive);
            Assert.True(Contract.All.Single(c => c.Name == "horizun_delete_verified").Destructive);
            Assert.Contains("horizun_delete_verified", AdvertisedAllowlist());
        }

        [Fact]
        public void The_description_says_unrehearsed_references_are_refused()
        {
            Assert.Contains("only when every action and reference resolves", Plan().Description);
            Assert.Contains("currently refused rather than authorised unseen", Plan().Description);
            Assert.DoesNotContain("authorize the complete graph", Plan().Description);
        }
    }
}
