using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Server.Tests
{
    // Heuristic, not a C# data-flow analysis: classes identify tools by their Name
    // property, including partial declarations. Only a switch/comparison whose
    // operand is named exactly like an enum property is attributed to that property.
    // Constants, aliases, helper classes and renamed locals may be missed.
    public class R9AContractParityTests
    {
        private readonly ITestOutputHelper output;
        public R9AContractParityTests(ITestOutputHelper output) { this.output = output; }
        private static readonly Dictionary<string, string> IntentionalDifferences = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["horizun_create_elements/kind/ramp"] = "Revit public API explicitly refuses ramp creation.",
        };
        private static string Root([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file), "..", ".."));
        private static string Key(string tool, string argument, string value) => tool + "/" + argument + "/" + value;

        private static void Enums(JToken node, string property, Dictionary<string, HashSet<string>> result)
        {
            if (node is JObject obj)
            {
                if (property != null && obj["enum"] is JArray values)
                {
                    if (!result.TryGetValue(property, out var set)) result[property] = set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var value in values.Where(v => v.Type == JTokenType.String)) set.Add((string)value);
                }
                foreach (var p in obj.Properties())
                {
                    if (p.Name == "properties" && p.Value is JObject props)
                        foreach (var child in props.Properties()) Enums(child.Value, child.Name, result);
                    else if (p.Name != "enum") Enums(p.Value, property, result);
                }
            }
            else if (node is JArray array) foreach (var child in array) Enums(child, property, result);
        }

        private static string Identifier(ExpressionSyntax expression) => (expression as IdentifierNameSyntax)?.Identifier.ValueText;
        private static string Literal(ExpressionSyntax expression) => expression is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression) ? l.Token.ValueText : null;
        private static void Add(Dictionary<string, HashSet<string>> found, string name, string value)
        {
            if (name == null || value == null) return;
            if (!found.TryGetValue(name, out var set)) found[name] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(value);
        }
        private static Dictionary<string, HashSet<string>> Scan(IEnumerable<ClassDeclarationSyntax> classes)
        {
            var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var cls in classes)
            {
                foreach (var sw in cls.DescendantNodes().OfType<SwitchStatementSyntax>())
                    foreach (var label in sw.Sections.SelectMany(s => s.Labels).OfType<CaseSwitchLabelSyntax>())
                        Add(found, Identifier(sw.Expression), Literal(label.Value));
                foreach (var binary in cls.DescendantNodes().OfType<BinaryExpressionSyntax>().Where(b => b.IsKind(SyntaxKind.EqualsExpression)))
                {
                    Add(found, Identifier(binary.Left), Literal(binary.Right));
                    Add(found, Identifier(binary.Right), Literal(binary.Left));
                }
                foreach (var call in cls.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    if (call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Equals" && call.ArgumentList.Arguments.Count > 0)
                        Add(found, Identifier(member.Expression), Literal(call.ArgumentList.Arguments[0].Expression));
            }
            return found;
        }
        [Fact]
        public void Published_enums_cover_source_literals()
        {
            output.WriteLine("Contract hash: " + Horizun.Contracts.Contract.Hash);
            var declarations = Directory.GetFiles(Path.Combine(Root(), "src", "Horizun.Revit"), "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .SelectMany(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p)).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()).ToArray();
            var toolClasses = declarations.SelectMany(c => c.Members.OfType<PropertyDeclarationSyntax>()
                .Where(p => p.Identifier.ValueText == "Name" && p.ExpressionBody != null)
                .Select(p => new { Class = c.Identifier.ValueText, Tool = Literal(p.ExpressionBody.Expression) }))
                .Where(x => x.Tool != null && x.Tool.StartsWith("horizun_", StringComparison.Ordinal)).ToArray();
            var unexpected = new SortedSet<string>(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in Tools.All)
            {
                var enums = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                Enums(Tools.Publish(tool, true)["inputSchema"], null, enums);
                var classNames = toolClasses.Where(x => x.Tool == tool.Name).Select(x => x.Class).ToHashSet(StringComparer.Ordinal);
                var found = Scan(declarations.Where(c => classNames.Contains(c.Identifier.ValueText)));
                foreach (var pair in enums)
                {
                    found.TryGetValue(pair.Key, out var actual);
                    actual = actual ?? new HashSet<string>(StringComparer.Ordinal);
                    foreach (var value in pair.Value.Except(actual).OrderBy(x => x, StringComparer.Ordinal))
                        output.WriteLine("Contrato sin literal detectado: " + Key(tool.Name, pair.Key, value));
                    foreach (var value in actual.Except(pair.Value))
                    {
                        string key = Key(tool.Name, pair.Key, value);
                        if (IntentionalDifferences.TryGetValue(key, out var reason) && !string.IsNullOrWhiteSpace(reason)) used.Add(key);
                        else unexpected.Add(key);
                    }
                }
            }
            var stale = IntentionalDifferences.Keys.Except(used).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.True(unexpected.Count == 0 && stale.Length == 0,
                "No declarados:\n" + string.Join("\n", unexpected) + "\nExcepciones obsoletas:\n" + string.Join("\n", stale));
        }
        [Fact]
        public void Scanner_attributes_only_the_compared_argument()
        {
            var tree = CSharpSyntaxTree.ParseText("class C { void F(string operation, string mode) { switch(operation) { case \"one\": break; } if (mode == \"two\" || mode.Equals(\"three\")) {} } }");
            var found = Scan(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>());
            Assert.Equal(new[] { "one" }, found["operation"].OrderBy(x => x));
            Assert.Equal(new[] { "three", "two" }, found["mode"].OrderBy(x => x));
        }
    }
}


