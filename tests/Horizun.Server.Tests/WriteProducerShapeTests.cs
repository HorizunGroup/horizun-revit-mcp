using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Horizun.Revit.Transport;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Server.Tests
{
    // These cases come from RESULT PRODUCERS, not from tool names. Each StampApplied
    // call contributes the surrounding JObject's actual root keys, nested requested
    // object (notably ManageCadLinks), counters and their source expressions. The
    // application block is emitted by the REAL production ApplicationOutcome code.
    // Only runtime observations are substituted: three writes, zero/one re-read as
    // requested. Autodesk objects cannot be instantiated in this Revit-free suite.
    public class WriteProducerShapeTests
    {
        private readonly ITestOutputHelper _output;
        public WriteProducerShapeTests(ITestOutputHelper output) { _output = output; }
        private static readonly Lazy<List<SyntaxNode>> AllRoots = new Lazy<List<SyntaxNode>>(() =>
            new[] { "Commands", "Core" }.SelectMany(dir => Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Horizun.Revit", dir), "*.cs"))
                .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()).ToList());
        private static readonly Lazy<Dictionary<string, MethodDeclarationSyntax>> Methods = new Lazy<Dictionary<string, MethodDeclarationSyntax>>(() =>
            AllRoots.Value.SelectMany(r => r.DescendantNodes().OfType<MethodDeclarationSyntax>())
                .GroupBy(m => m.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText + "." + m.Identifier.ValueText)
                .ToDictionary(g => g.Key, g => g.First()));
        private sealed class Shape
        {
            public string Source;
            public JObject Payload;
            public bool Canonical;
            public bool ExplicitApplication;
            public Dictionary<string, ExpressionSyntax> Fields;
            public override string ToString() => Source;
        }

        private static string RepoRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands"))) return d.FullName;
            throw new InvalidOperationException("Repository not found from " + AppContext.BaseDirectory);
        }

        private static string Key(ExpressionSyntax expression)
        {
            var access = expression as ImplicitElementAccessSyntax;
            var indexed = expression as ElementAccessExpressionSyntax;
            var args = access?.ArgumentList.Arguments ?? indexed?.ArgumentList.Arguments;
            return args.HasValue && args.Value.Count == 1 && args.Value[0].Expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null;
        }

        private static Dictionary<string, ExpressionSyntax> Fields(ExpressionSyntax expression)
        {
            if (expression is CastExpressionSyntax cast) return Fields(cast.Expression);
            if (expression is ParenthesizedExpressionSyntax paren) return Fields(paren.Expression);
            if (expression is InvocationExpressionSyntax from && from.Expression.ToString() == "JObject.FromObject" && from.ArgumentList.Arguments.Count == 1)
                return Fields(from.ArgumentList.Arguments[0].Expression);
            if (expression is ObjectCreationExpressionSyntax obj && obj.Type.ToString() == "JObject")
                return (obj.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>() ?? Enumerable.Empty<AssignmentExpressionSyntax>())
                    .Where(a => Key(a.Left) != null).GroupBy(a => Key(a.Left)).ToDictionary(g => g.Key, g => g.Last().Right);
            if (expression is AnonymousObjectCreationExpressionSyntax anon)
                return anon.Initializers.ToDictionary(a => a.NameEquals?.Name.Identifier.ValueText ?? a.Expression.ToString(), a => a.Expression);
            return null;
        }

        private static ExpressionSyntax Resolve(ExpressionSyntax expression, SyntaxNode scope, int before, int depth = 0)
        {
            if (depth > 5) return expression;
            var seen = new HashSet<string>();
            while (expression is IdentifierNameSyntax id && seen.Add(id.Identifier.ValueText))
            {
                var variable = scope.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .LastOrDefault(v => v.SpanStart < before && v.Identifier.ValueText == id.Identifier.ValueText && v.Initializer != null);
                var assignment = scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .LastOrDefault(a => a.SpanStart < before && a.Left is IdentifierNameSyntax target && target.Identifier.ValueText == id.Identifier.ValueText);
                if (assignment != null && (variable == null || assignment.SpanStart > variable.SpanStart)) expression = assignment.Right;
                else if (variable != null) expression = variable.Initializer.Value;
                else
                {
                    var method = scope as MethodDeclarationSyntax;
                    int parameter = method == null ? -1 : method.ParameterList.Parameters.IndexOf(method.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.ValueText == id.Identifier.ValueText));
                    if (parameter >= 0)
                    {
                        string owner = method.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText;
                        var caller = new[] { scope.SyntaxTree.GetRoot() }.Concat(AllRoots.Value).SelectMany(r => r.DescendantNodes().OfType<InvocationExpressionSyntax>())
                            .FirstOrDefault(c => (c.Expression as IdentifierNameSyntax)?.Identifier.ValueText == method.Identifier.ValueText && c.ArgumentList.Arguments.Count > parameter &&
                                c.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == owner);
                        if (caller != null)
                            return Resolve(caller.ArgumentList.Arguments[parameter].Expression, caller.Ancestors().OfType<MethodDeclarationSyntax>().First(), caller.SpanStart, depth + 1);
                    }
                    break;
                }
            }
            if (expression is InvocationExpressionSyntax call && Fields(expression) == null)
            {
                string methodName = (call.Expression as IdentifierNameSyntax)?.Identifier.ValueText
                    ?? (call.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText;
                string className = (call.Expression as MemberAccessExpressionSyntax)?.Expression.ToString()
                    ?? scope.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
                var method = scope.SyntaxTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.ValueText == methodName && m.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == className);
                if (method == null) Methods.Value.TryGetValue(className + "." + methodName, out method);
                if (method != null)
                {
                    var result = method.DescendantNodes().OfType<ReturnStatementSyntax>().Select(r => r.Expression)
                        .Select(e => Resolve(e, method, method.Span.End, depth + 1)).FirstOrDefault(e => Fields(e) != null);
                    if (result != null) return result;
                }
            }
            return expression;
        }

        private static JToken Value(ExpressionSyntax expression, SyntaxNode scope, int before, int depth = 0)
        {
            if (depth > 4) return JValue.CreateNull();
            expression = Resolve(expression, scope, before);
            var fields = Fields(expression);
            if (fields != null)
            {
                var result = new JObject();
                foreach (var field in fields) result[field.Key] = Value(field.Value, scope, before, depth + 1);
                return result;
            }
            if (expression is LiteralExpressionSyntax literal) return literal.Token.Value == null ? JValue.CreateNull() : JToken.FromObject(literal.Token.Value);
            if (expression is ObjectCreationExpressionSyntax array && array.Type.ToString() == "JArray") return new JArray();
            // Non-guard evidence remains present under its source key. Its runtime
            // contents play no part in deciding the transport verdict.
            return JValue.CreateNull();
        }

        private static IEnumerable<Shape> Extract(string source, string filename)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();
            var captured = new HashSet<int>();
            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression.ToString() == "ApplicationOutcome.StampApplied" || i.Expression.ToString() == "ApplicationOutcome.Stamp"))
            {
                var args = call.ArgumentList.Arguments;
                bool direct = call.Expression.ToString() == "ApplicationOutcome.StampApplied";
                Assert.Equal(direct ? 8 : 2, args.Count);
                if (direct && args[2].Expression.ToString() == "0") continue; // a literal no-op/read cannot be a 0-of-N write
                var scope = (SyntaxNode)call.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() ?? root;
                bool returnsPayload = scope is MethodDeclarationSyntax producer && producer.ReturnType.ToString() == "JObject";
                if (!returnsPayload && !scope.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => i.Expression.ToString() == "CommandResult.Ok")) continue;
                var expression = Resolve(args[0].Expression, scope, call.SpanStart);
                var fields = Fields(expression);
                Assert.True(fields != null, filename + ": cannot resolve payload " + args[0]);
                // Include post-initializer fields belonging to this exact payload.
                foreach (var assignment in scope.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.SpanStart > expression.SpanStart && a.SpanStart < call.SpanStart))
                    if (assignment.Left is ElementAccessExpressionSyntax indexed && indexed.Expression.ToString() == args[0].Expression.ToString() && Key(assignment.Left) is string key)
                        fields[key] = assignment.Right;
                var payload = new JObject();
                foreach (var field in fields) payload[field.Key] = Value(field.Value, scope, call.SpanStart);
                // A no-op producer is still inventoried; the counterexample asks
                // what happens when its same output format carries a real write.
                payload.Remove("dry_run");
                payload.Remove("provisional_elements_absent");
                payload["transaction_status"] = "Committed";
                captured.Add(expression.SpanStart);
                yield return new Shape { Source = filename + ":" + (tree.GetLineSpan(call.Span).StartLinePosition.Line + 1), Payload = payload, Canonical = true, Fields = fields };
            }
            // Legacy producers that publish a request/count directly. Nested row
            // evidence and read-only counters are not write outcome declarations.
            foreach (var expression in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (captured.Contains(expression.SpanStart)) continue;
                var fields = Fields(expression);
                var objectScope = (SyntaxNode)expression.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() ?? root;
                if (fields != null && fields.TryGetValue("application", out ExpressionSyntax applicationExpression))
                {
                    var manual = Value(applicationExpression, objectScope, expression.SpanStart) as JObject;
                    if (manual?["verified"] != null && manual["requested"] != null)
                    {
                        var envelope = new JObject();
                        foreach (var field in fields) envelope[field.Key] = Value(field.Value, objectScope, expression.SpanStart);
                        envelope["dry_run"] = false;
                        yield return new Shape { Source = filename + ":" + (tree.GetLineSpan(expression.Span).StartLinePosition.Line + 1), Payload = envelope, ExplicitApplication = true, Fields = fields };
                        continue;
                    }
                }
                if (fields == null || !fields.ContainsKey("requested") || !fields.Any(f => LooksLikeCounter(f.Key) && IsNumeric(f.Value))) continue;
                var scope = (SyntaxNode)expression.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() ?? root;
                var requested = Value(fields["requested"], scope, expression.SpanStart);
                if (requested is JObject || requested is JArray || requested.Type == JTokenType.String || requested.Type == JTokenType.Boolean) continue;
                var payload = new JObject();
                foreach (var field in fields) payload[field.Key] = Value(field.Value, scope, expression.SpanStart);
                payload.Remove("dry_run");
                payload["transaction_status"] = "Committed";
                yield return new Shape { Source = filename + ":" + (tree.GetLineSpan(expression.Span).StartLinePosition.Line + 1), Payload = payload, Fields = fields };
            }
        }

        private static bool LooksLikeCounter(string key) => !key.Contains("unverified") && !key.Contains("not_verified") &&
            (key == "applied" || key == "written" || key == "deleted" || key == "transformed" ||
            key.IndexOf("verified", StringComparison.Ordinal) >= 0 || key.IndexOf("confirmed", StringComparison.Ordinal) >= 0);

        private static bool IsNumeric(ExpressionSyntax expression)
        {
            // This is a counter inventory, not a type checker. An expression whose
            // type we cannot establish must stay visible for review. Only known
            // nonnumeric forms are excluded; new spellings cannot disappear merely
            // because their producer used a different expression to compute them.
            if (expression is ParenthesizedExpressionSyntax parenthesized) return IsNumeric(parenthesized.Expression);
            if (expression is CastExpressionSyntax cast)
                return cast.Type.ToString() == "bool" || cast.Type.ToString() == "string" ? false : IsNumeric(cast.Expression);
            if (expression is LiteralExpressionSyntax literal) return literal.Token.Value is int || literal.Token.Value is long;
            if (expression is PrefixUnaryExpressionSyntax unary)
                return !unary.IsKind(SyntaxKind.LogicalNotExpression) && IsNumeric(unary.Operand);
            if (expression is BinaryExpressionSyntax binary)
            {
                if (new[] { SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression, SyntaxKind.LessThanExpression,
                    SyntaxKind.LessThanOrEqualExpression, SyntaxKind.GreaterThanExpression, SyntaxKind.GreaterThanOrEqualExpression,
                    SyntaxKind.LogicalAndExpression, SyntaxKind.LogicalOrExpression, SyntaxKind.IsExpression, SyntaxKind.AsExpression }
                    .Contains(binary.Kind())) return false;
                return IsNumeric(binary.Left) && IsNumeric(binary.Right);
            }
            if (expression is InvocationExpressionSyntax invocation)
            {
                var name = (invocation.Expression as MemberAccessExpressionSyntax)?.Name
                    ?? invocation.Expression as SimpleNameSyntax;
                if (name is GenericNameSyntax generic && generic.TypeArgumentList.Arguments.Any(a => a.ToString() == "bool" || a.ToString() == "string")) return false;
                if (new[] { "All", "Any", "Contains", "Equals", "SequenceEqual", "StartsWith", "EndsWith", "IsNullOrWhiteSpace", "IsNullOrEmpty" }
                    .Contains(name?.Identifier.ValueText)) return false;
                var declaringClass = expression.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
                if (Methods.Value.TryGetValue(declaringClass + "." + name?.Identifier.ValueText, out MethodDeclarationSyntax factory) &&
                    new[] { "bool", "bool?", "string", "JObject", "JArray" }.Contains(factory.ReturnType.ToString())) return false;
                return true; // Count/LongCount/Sum and unknown counter factories are inventoried.
            }
            if (expression is IdentifierNameSyntax id)
            {
                var method = expression.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
                var variable = method?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .LastOrDefault(v => v.SpanStart < expression.SpanStart && v.Identifier.ValueText == id.Identifier.ValueText);
                string type = (variable?.Parent as VariableDeclarationSyntax)?.Type.ToString();
                if (type == "bool" || type == "string" || type == "JObject" || type == "JArray") return false;
                if (type == "int" || type == "long") return true;
                if (type == "var" && variable.Initializer != null) return IsNumeric(variable.Initializer.Value);
                var parameter = method?.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.ValueText == id.Identifier.ValueText);
                string parameterType = parameter?.Type?.ToString();
                if (parameterType == "bool" || parameterType == "string" || parameterType == "JObject" || parameterType == "JArray") return false;
                return true;
            }
            if (expression is MemberAccessExpressionSyntax access)
                return !new[] { "AllVerified", "FullyVerified", "HostVerified", "HasValue" }.Contains(access.Name.Identifier.ValueText);
            if (expression is ConditionalExpressionSyntax conditional) return IsNumeric(conditional.WhenTrue) && IsNumeric(conditional.WhenFalse);
            if (expression is ObjectCreationExpressionSyntax creation && (creation.Type.ToString() == "JObject" || creation.Type.ToString() == "JArray")) return false;
            return true;
        }

        private static List<Shape> Producers() => Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands"), "*.cs")
            .SelectMany(path => Extract(File.ReadAllText(path), Path.GetFileName(path))).ToList();

        private static JObject Observe(Shape shape, int verified)
        {
            var data = (JObject)shape.Payload.DeepClone();
            foreach (string name in CommandResult.WriteConfirmationCounters)
                if (shape.Fields.ContainsKey(name)) data[name] = verified;
            foreach (string request in new[] { "requested", "writes_requested" })
                if (shape.Fields.ContainsKey(request) && !(data[request] is JObject) && !(data[request] is JArray)) data[request] = 3;
            if (shape.Canonical) ApplicationOutcome.StampApplied(data, "Committed", 3, 3, verified, 0, 3 - verified, 0);
            else if (shape.ExplicitApplication)
            {
                data["application"]["requested"] = 3;
                data["application"]["verified"] = verified;
                data["application"]["state"] = "partial";
                data["application"]["fully_applied"] = false;
            }
            return data;
        }

        [Fact]
        public void Every_real_producer_format_rejects_zero_verified_at_addin_and_server_and_preserves_partial_success()
        {
            var shapes = Producers();
            _output.WriteLine("Actual producer formats: " + shapes.Count + " across " + shapes.Select(s => s.Source.Split(':')[0]).Distinct().Count() + " source files.");
            _output.WriteLine("PRODUCER_INVENTORY_JSON:" + new JArray(shapes.Select(s => new JObject { ["producer"] = s.Source, ["application"] = s.Canonical ? "canonical" : s.ExplicitApplication ? "explicit" : "legacy", ["root_keys"] = new JArray(s.Payload.Properties().Select(p => p.Name)) })).ToString(Newtonsoft.Json.Formatting.None));
            foreach (var shape in shapes) _output.WriteLine(shape.Source + " keys=" + string.Join(",", shape.Payload.Properties().Select(p => p.Name)));
            // Community edition: 59 producer formats without the Pro commands (measured 2026-11-22).
            Assert.True(shapes.Count >= 55, "Production inventory unexpectedly shrank: " + shapes.Count);
            foreach (string required in new[] { "SplitByLevelCommand.cs:", "SplitByLevelCommand.Openings.cs:", "ModelEditRunner.cs:", "CreateElementsCommand.Execution.cs:", "WriteParamsCommand.cs:" })
                Assert.Contains(shapes, s => s.Source.StartsWith(required, StringComparison.Ordinal));
            foreach (var shape in shapes)
            {
                JObject zero = Observe(shape, 0);
                CommandResult command = CommandResult.Ok(zero);
                Assert.False(command.Success, shape.Source + " accepted 0 verified: " + zero);
                Assert.True((bool)McpResult.FromPluginReply(PipeEnvelope.Of(shape.Source, command), null)["isError"], shape.Source);
                JObject legacy = McpResult.FromPluginReply(new JObject { ["success"] = true, ["data"] = zero }, null);
                Assert.True((bool)legacy["isError"], shape.Source + " legacy add-in escaped Server guard");
                JObject partial = Observe(shape, 1);
                Assert.True(CommandResult.Ok(partial).Success, shape.Source + " rejected measured partial success: " + partial);
                var reply = McpResult.Structured(partial, partial.ToString());
                Assert.False((bool)reply["isError"], shape.Source);
                if (shape.Canonical) Assert.Equal("partial", (string)reply["structuredContent"]["application"]["state"]);
            }
        }

        private static IEnumerable<string> UnknownCounters(IEnumerable<Shape> shapes)
        {
            foreach (var shape in shapes)
                foreach (var field in shape.Fields)
                    if (LooksLikeCounter(field.Key) && IsNumeric(field.Value) && !CommandResult.WriteConfirmationCounters.Contains(field.Key))
                    {
                        // External API bools are excluded by their exact producer,
                        // field AND expression, never merely by a reassuring key.
                        string booleanForm = shape.Source.Split(':')[0] + "|" + field.Key + "|" + field.Value.ToString();
                        if (new[] {
                            "CreateScheduleCommand.cs|include_links_verified|verified.Definition.IncludeLinkedFiles",
                            "CreateScheduleCommand.cs|itemized_verified|verified.Definition.IsItemized",
                            "EditDimensionsCommand.cs|rollback_confirmed|rbGroup.Confirmed",
                            "EditDimensionsCommand.cs|rollback_confirmed|rb.Confirmed",
                            "FixPlanimetryCommand.cs|rollback_confirmed|rbGroup.Confirmed",
                            "FixPlanimetryCommand.cs|rollback_confirmed|rb.Confirmed"
                        }.Contains(booleanForm)) continue;
                        // Partition counts and secondary evidence have their own
                        // denominator; zero is legitimate beside verified total >0.
                        if (new[] { "writes_confirmed_against_your_value", "writes_confirmed_by_parse_read_back_only", "params_set_confirmed_by_parse_read_back_only", "params_added_confirmed",
                            "params_set_confirmed_against_your_value", "members_reverified_existing", "cascaded_confirmed_gone", "cascaded_confirmed_surviving", "cover_verified", "system_verified", "bar_type_changes_verified" }.Contains(field.Key)) continue;
                        yield return shape.Source + " unknown confirmation counter " + field.Key;
                    }
        }

        [Fact]
        public void A_new_producer_cannot_introduce_a_confirmation_counter_unknown_to_the_guard()
        {
            var missing = UnknownCounters(Producers()).ToList();
            Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
            const string newProducer = "class NewWriter { void Run() { int verified = 0; var reply = new JObject { [\"new_kind_verified\"] = verified }; ApplicationOutcome.StampApplied(reply, \"Committed\", 1, 1, verified, 0, 1, 0); return CommandResult.Ok(reply); } }";
            var unknown = UnknownCounters(Extract(newProducer, "NewWriter.cs")).ToList();
            Assert.Single(unknown);
            Assert.Contains("new_kind_verified", unknown[0]);
            foreach (string spelling in new[] { "confirmed_count", "new_kind_verified" })
            {
                string writer = "class NewWriter { void Run() { int good = 0; int n = 1; var reply = new JObject { [\"requested\"] = n, [\"" + spelling + "\"] = good }; return CommandResult.Ok(reply); } }";
                Assert.Single(UnknownCounters(Extract(writer, "NewWriter.cs")));
            }
            foreach (string computation in new[] { "good + otherGood", "rows.Count(x => x.Verified)", "ReadUnknownCounter(rows)" })
            {
                string writer = "class NewWriter { void Run() { int good = 0; int otherGood = 0; int n = 1; var reply = new JObject { [\"requested\"] = n, [\"new_kind_verified\"] = " + computation + " }; return CommandResult.Ok(reply); } }";
                Assert.Single(UnknownCounters(Extract(writer, "NewWriter.cs")));
            }
        }
    }
}
