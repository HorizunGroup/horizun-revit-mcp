// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The audit's request shape. It had the SAME two defects the scan had, and now
// reads the SAME rules rather than a second copy of them - two tables of one
// fact is how the two halves of this bridge came to disagree about a parameter
// elsewhere, and that disagreement rolled back every wall with a door in it.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AuditRequestTests
    {
        private static ScanRequestVerdict Audit(string json) =>
            ScanRequestRules.CheckAudit(JObject.Parse(json));

        [Fact]
        public void An_ordinary_audit_request_is_accepted()
        {
            Assert.True(Audit(@"{ ""target_document"": ""M"", ""top"": 20 }").Ok);
            Assert.True(Audit(@"{ ""target_document"": ""M"", ""requirement_set"": {},
                                  ""tolerances"": {}, ""readiness_roles"": [] }").Ok);
            Assert.True(Audit(@"{ ""target_document"": ""M"", ""store_snapshot"": true,
                                  ""health_profile"": { ""weights"": [] } }").Ok);
        }

        [Fact]
        public void A_misspelt_audit_option_is_refused_and_the_real_ones_are_named()
        {
            // v1.1.6: accepted in silence, like the scan's.
            ScanRequestVerdict v = Audit(@"{ ""target_document"": ""M"", ""requirement_sets"": {} }");
            Assert.False(v.Ok);
            Assert.Equal(ScanRequestCodes.UnknownKey, v.Code);
            Assert.Contains("requirement_sets", v.Message);
            Assert.Contains("requirement_set", v.Message);
            Assert.Contains("the audit", v.Message);   // it says which tool
        }

        [Fact]
        public void A_scan_only_option_is_not_silently_accepted_by_the_audit()
        {
            // `sections` belongs to the scan. Accepting it here would look like a
            // scoped audit and be a full one.
            Assert.Equal(ScanRequestCodes.UnknownKey,
                Audit(@"{ ""target_document"": ""M"", ""sections"": [""health""] }").Code);
            Assert.Equal(ScanRequestCodes.UnknownKey,
                Audit(@"{ ""target_document"": ""M"", ""section_limits"": { ""health"": 5 } }").Code);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public void An_audit_top_below_one_is_refused_rather_than_clamped(int n)
        {
            // v1.1.6: Math.Max(1, ...), identical to the scan's.
            ScanRequestVerdict v = Audit(@"{ ""target_document"": ""M"", ""top"": " + n + " }");
            Assert.False(v.Ok);
            Assert.Equal(ScanRequestCodes.BadTop, v.Code);
        }

        [Fact]
        public void An_audit_top_of_the_wrong_type_is_refused_by_name()
        {
            ScanRequestVerdict v = Audit(@"{ ""target_document"": ""M"", ""top"": ""twenty"" }");
            Assert.False(v.Ok);
            Assert.Equal(ScanRequestCodes.BadTop, v.Code);
            Assert.Contains("top", v.Message);
        }

        [Fact]
        public void An_audit_top_above_the_ceiling_is_refused()
        {
            Assert.Equal(ScanRequestCodes.BadTop,
                Audit(@"{ ""target_document"": ""M"", ""top"": 999999 }").Code);
        }

        [Fact]
        public void The_two_tools_share_one_implementation_of_these_rules()
        {
            // The property that matters is not that both refuse, but that they
            // refuse for the SAME reason from the SAME code. Identical messages are
            // the observable consequence of there being one implementation.
            string scan = ScanRequestRules.Check(
                JObject.Parse(@"{ ""target_document_title"": ""M"", ""top"": 0 }"), new[] { "health" }).Message;
            string audit = Audit(@"{ ""target_document"": ""M"", ""top"": 0 }").Message;
            Assert.Equal(scan, audit);
        }

        [Fact]
        public void The_audit_command_checks_the_shape_and_stops_clamping()
        {
            string src = File.ReadAllText(Source("src/Horizun.Revit/Commands/AuditModelCommand.cs"));
            string code = string.Join("\n", src.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            Assert.Contains("ScanRequestRules.CheckAudit(request)", code);
            Assert.Contains("if (!shape.Ok) return CommandResult.Fail(", code);
            Assert.DoesNotContain("Math.Max(1, request.Value<int>(\"top\"))", code);
        }

        [Fact]
        public void Both_tool_schemas_refuse_unknown_properties()
        {
            // Nothing in the server validates arguments against the schema, so this
            // is the declaration rather than the enforcement - the enforcement is
            // CheckUnknownKeys above. Both matter: a well-behaved client should be
            // told before it sends.
            string src = File.ReadAllText(Source("src/Horizun.Contracts/Contract.cs"));
            foreach (string tool in new[] { "horizun_model_scan", "horizun_audit_model" })
            {
                int i = src.IndexOf("Name = \"" + tool + "\"", StringComparison.Ordinal);
                Assert.True(i > 0, tool + " not found");
                int j = src.IndexOf("InputSchema = JObject.Parse(@\"{", i, StringComparison.Ordinal);
                int k = src.IndexOf("}\")", j, StringComparison.Ordinal);
                string schema = src.Substring(j, k - j);
                Assert.Contains("additionalProperties", schema);
            }
        }

        private static string Source(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
