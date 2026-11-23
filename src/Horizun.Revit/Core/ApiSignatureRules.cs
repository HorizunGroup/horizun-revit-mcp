// Horizun Revit MCP - original Horizun code. Metadata only: never invokes an API member.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Conservative CLR type compatibility, not an IronPython overload binder or a value validator.</summary>
    public static class ApiSignatureRules
    {
        public const int MaxArguments = 32;
        public const int MaxTypeNameLength = 512;

        public static JObject Check(Type target, string member, IList<string> argumentTypes,
            IEnumerable<Type> allowedTypes, bool includeInherited = false, string revitYear = null)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(member) || (member != ".ctor" && !Identifier(member)))
                throw new ArgumentException("member_filter must be an exact method name or .ctor.", nameof(member));
            if (argumentTypes == null || argumentTypes.Count > MaxArguments)
                throw new ArgumentException("argument_types must contain 0..32 CLR type names.", nameof(argumentTypes));
            var universe = (allowedTypes ?? Enumerable.Empty<Type>()).Where(t => t != null && t.IsVisible).Distinct().ToList();
            if (!target.IsVisible || !universe.Contains(target))
                throw new ArgumentException("Target must be a public type in the permitted loaded universe.", nameof(target));
            var unresolved = new JArray();
            var arguments = new Type[argumentTypes.Count];
            for (int i = 0; i < arguments.Length; i++)
            {
                string reason;
                arguments[i] = Resolve(argumentTypes[i], universe, 0, out reason);
                if (arguments[i] == null) unresolved.Add(new JObject { ["argument_index"] = i,
                    ["type_name"] = argumentTypes[i], ["reason"] = reason });
            }
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            if (!includeInherited) flags |= BindingFlags.DeclaredOnly;
            else flags |= BindingFlags.FlattenHierarchy;
            IEnumerable<MethodBase> candidates = member == ".ctor"
                ? target.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Cast<MethodBase>()
                : (includeInherited && target.IsInterface
                    ? new[] { target }.Concat(target.GetInterfaces()).SelectMany(t => t.GetMethods(flags)).Distinct()
                    : target.GetMethods(flags).AsEnumerable())
                    .Where(m => m.Name == member).Cast<MethodBase>();
            var rows = new JArray();
            var matches = new JArray();
            var suggestions = new HashSet<string>(StringComparer.Ordinal);
            bool complete = unresolved.Count == 0;
            foreach (MethodBase method in candidates.OrderBy(Signature, StringComparer.Ordinal))
            {
                var reasons = new JArray();
                var conversions = new JArray();
                var hints = new HashSet<string>(StringComparer.Ordinal);
                ParameterInfo[] parameters = method.GetParameters();
                bool unsupported = method.ContainsGenericParameters || parameters.Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer);
                if (unsupported)
                {
                    complete = false;
                    reasons.Add(Reason("unsupported_signature", "Open generic, pointer or by-ref signatures require binding not performed by this check."));
                }
                bool expandedParams = parameters.Length > 0 && parameters.Last().GetCustomAttributesData()
                    .Any(a => a.AttributeType.FullName == "System.ParamArrayAttribute") &&
                    (arguments.Length != parameters.Length || (arguments.Length > 0 && arguments.Last() != null &&
                        !parameters.Last().ParameterType.IsAssignableFrom(arguments.Last())));
                if (expandedParams)
                {
                    complete = false;
                    reasons.Add(Reason("params_expansion_not_analyzed", "Pass the declared CLR array type; expanded params binding is not analyzed."));
                }
                if (arguments.Length > parameters.Length)
                    reasons.Add(Reason("argument_count", "Too many arguments: expected " + parameters.Length + ", received " + arguments.Length + "."));
                for (int i = arguments.Length; i < parameters.Length; i++)
                {
                    ParameterInfo parameter = parameters[i];
                    if (!parameter.IsOptional)
                        reasons.Add(Reason("missing_argument", "Required parameter '" + parameter.Name + "' is missing.", i));
                    else if (!parameter.HasDefaultValue)
                    {
                        complete = false;
                        reasons.Add(Reason("optional_default_not_analyzed", "Optional parameter has no declared metadata default.", i));
                    }
                    else conversions.Add(new JObject { ["argument_index"] = i, ["conversion"] = "optional_parameter_default",
                        ["to"] = ClrName(parameter.ParameterType) });
                }
                for (int i = 0; i < Math.Min(arguments.Length, parameters.Length); i++)
                {
                    Type from = arguments[i], to = parameters[i].ParameterType;
                    if (from == null)
                        reasons.Add(Reason("unresolved_argument_type", "The argument CLR type was not resolved.", i));
                    else if (!to.IsByRef && !to.IsPointer && !to.ContainsGenericParameters)
                    {
                        string conversion = ImplicitConversion(from, to);
                        if (conversion == null)
                        {
                            MethodInfo userConversion = PossibleUserConversion(from, to);
                            if (userConversion != null) complete = false;
                            var reason = new JObject { ["code"] = userConversion == null ? "no_implicit_conversion" : "user_defined_conversion_not_analyzed", ["argument_index"] = i,
                                ["from"] = ClrName(from), ["to"] = ClrName(to),
                                ["message"] = userConversion == null
                                    ? "No supported implicit CLR conversion; values and explicit casts are not evaluated."
                                    : "A potentially applicable user-defined implicit operator exists; operator binding and execution are not performed." };
                            if (userConversion != null) reason["candidate_conversion"] = Signature(userConversion);
                            reasons.Add(reason);
                        }
                        else if (conversion != "identity") conversions.Add(new JObject { ["argument_index"] = i,
                            ["from"] = ClrName(from), ["to"] = ClrName(to), ["conversion"] = conversion });
                    }
                    AddHint(to, hints);
                }
                foreach (ParameterInfo parameter in parameters) AddHint(parameter.ParameterType, hints);
                if (target.Name == "ElementId" && member == ".ctor")
                    foreach (ParameterInfo p in parameters.Where(p => p.ParameterType.FullName == "System.Int32" || p.ParameterType.FullName == "System.Int64"))
                        hints.Add("IronPython: ElementId(" + p.ParameterType.Name + "(value)); this loaded constructor declares " + ClrName(p.ParameterType) +
                            (method.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.ObsoleteAttribute") ? " and is marked obsolete." : "."));
                foreach (string hint in hints) suggestions.Add(hint);
                string signature = Signature(method);
                bool accepted = reasons.Count == 0;
                var row = new JObject { ["signature"] = signature, ["accepted"] = accepted,
                    ["exact_match"] = accepted && conversions.Count == 0, ["conversions_required"] = conversions,
                    ["reasons"] = reasons, ["ironpython_suggestions"] = new JArray(hints.OrderBy(x => x, StringComparer.Ordinal)) };
                rows.Add(row);
                if (accepted) matches.Add(new JObject { ["signature"] = signature, ["exact_match"] = conversions.Count == 0,
                    ["conversions_required"] = conversions.DeepClone() });
            }
            return new JObject { ["operation"] = "check_signature", ["type_name"] = target.FullName,
                ["member_filter"] = member, ["argument_types"] = new JArray(argumentTypes), ["revit_year"] = revitYear,
                ["candidate_count"] = rows.Count, ["matches"] = matches, ["ambiguous"] = matches.Count > 1,
                ["coverage_complete"] = complete, ["unresolved_types"] = unresolved, ["overloads"] = rows,
                ["ironpython_suggestions"] = new JArray(suggestions.OrderBy(x => x, StringComparer.Ordinal)),
                ["analysis_scope"] = "Declared CLR types only; no invocation, values, explicit/user-defined conversions, generic inference or overload ranking. Compatible types do not prove a call with concrete values is valid." };
        }

        /// <summary>Resolves only supplied public types and their closed generic/array constructions. Never loads assemblies.</summary>
        public static Type ResolveType(string name, IEnumerable<Type> allowedTypes, out string reason)
        {
            return Resolve(name, (allowedTypes ?? Enumerable.Empty<Type>()).Where(t => t != null && t.IsVisible).Distinct().ToList(), 0, out reason);
        }

        private static Type Resolve(string name, IList<Type> universe, int depth, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxTypeNameLength || depth > 8)
            { reason = "Expected a bounded CLR type name (maximum 512 characters and 8 generic levels)."; return null; }
            name = name.Trim();
            if (name.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '+' || c == '`' || c == '[' || c == ']' || c == ',' || c == ' ')))
            { reason = "Only CLR type names are accepted; no code, values, expressions or assembly qualifiers."; return null; }
            var exact = universe.Where(t => !t.ContainsGenericParameters && (t.FullName == name || t.Name == name)).ToList();
            var full = exact.Where(t => t.FullName == name).ToList();
            if (full.Count > 0) exact = full;
            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1) { reason = "Ambiguous public type name; use its full CLR name."; return null; }
            var arraySuffix = System.Text.RegularExpressions.Regex.Match(name, @"\[(?<commas>,*)\]$");
            if (arraySuffix.Success)
            {
                Type element = Resolve(name.Substring(0, arraySuffix.Index), universe, depth + 1, out reason);
                if (element == null) return null;
                int rank = arraySuffix.Groups["commas"].Length + 1;
                try { return rank == 1 ? element.MakeArrayType() : element.MakeArrayType(rank); }
                catch (ArgumentException) { reason = "Invalid CLR array element type or rank."; return null; }
                catch (TypeLoadException) { reason = "CLR array type or rank is not loadable."; return null; }
            }
            int bracket = name.IndexOf('[');
            if (bracket > 0 && name.EndsWith("]", StringComparison.Ordinal))
            {
                string bare = name.Substring(0, bracket);
                var parts = SplitArguments(name.Substring(bracket + 1, name.Length - bracket - 2));
                if (parts == null || parts.Count == 0) { reason = "Malformed closed generic CLR type name."; return null; }
                var definitions = universe.Where(t => t.IsGenericTypeDefinition && t.GetGenericArguments().Length == parts.Count &&
                    (t.FullName == bare || t.Name == bare || StripArity(t.FullName) == bare || StripArity(t.Name) == bare)).ToList();
                var fullDefinitions = definitions.Where(t => t.FullName == bare || StripArity(t.FullName) == bare).ToList();
                if (fullDefinitions.Count > 0) definitions = fullDefinitions;
                if (definitions.Count != 1) { reason = definitions.Count > 1 ? "Ambiguous generic type name; use its full CLR name." : "Generic type definition is not in the permitted loaded universe."; return null; }
                var arguments = new List<Type>();
                foreach (string part in parts)
                {
                    Type argument = Resolve(part, universe, depth + 1, out reason);
                    if (argument == null) return null;
                    arguments.Add(argument);
                }
                try { return definitions[0].MakeGenericType(arguments.ToArray()); }
                catch (ArgumentException) { reason = "Generic arguments do not satisfy the CLR type constraints."; return null; }
                catch (TypeLoadException) { reason = "Closed CLR generic type is not loadable from the permitted types."; return null; }
            }
            reason = "CLR type is not in the permitted loaded universe. Python list/int/null aliases and values are not CLR type declarations.";
            return null;
        }

        private static List<string> SplitArguments(string text)
        {
            var result = new List<string>(); int depth = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '[') depth++;
                if (text[i] == ']' && --depth < 0) return null;
                if (text[i] == ',' && depth == 0) { result.Add(text.Substring(start, i - start).Trim()); start = i + 1; }
            }
            if (depth != 0) return null;
            result.Add(text.Substring(start).Trim());
            return result.Any(string.IsNullOrWhiteSpace) ? null : result;
        }

        private static string ImplicitConversion(Type from, Type to)
        {
            if (from == to) return "identity";
            Type fromNullable = NullableUnderlying(from), toNullable = NullableUnderlying(to);
            if (toNullable != null)
            {
                string inner = ImplicitConversion(fromNullable ?? from, toNullable);
                if (inner == "identity" || inner == "implicit_numeric") return fromNullable == null ? "implicit_nullable" : "lifted_nullable";
                return null;
            }
            if (to.IsAssignableFrom(from)) return from.IsValueType ? fromNullable == null ? "boxing" : "nullable_boxing" : "implicit_reference";
            if (fromNullable != null)
                return !to.IsValueType && to.IsAssignableFrom(fromNullable) ? "nullable_boxing" : null;
            string targets;
            return NumericTargets.TryGetValue(from.FullName ?? "", out targets) && targets.Split(',').Contains(to.FullName)
                ? "implicit_numeric" : null;
        }

        private static readonly Dictionary<string, string> NumericTargets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["System.SByte"] = "System.Int16,System.Int32,System.Int64,System.Single,System.Double,System.Decimal",
            ["System.Byte"] = "System.Int16,System.UInt16,System.Int32,System.UInt32,System.Int64,System.UInt64,System.Single,System.Double,System.Decimal",
            ["System.Int16"] = "System.Int32,System.Int64,System.Single,System.Double,System.Decimal",
            ["System.UInt16"] = "System.Int32,System.UInt32,System.Int64,System.UInt64,System.Single,System.Double,System.Decimal",
            ["System.Int32"] = "System.Int64,System.Single,System.Double,System.Decimal",
            ["System.UInt32"] = "System.Int64,System.UInt64,System.Single,System.Double,System.Decimal",
            ["System.Int64"] = "System.Single,System.Double,System.Decimal",
            ["System.UInt64"] = "System.Single,System.Double,System.Decimal",
            ["System.Char"] = "System.UInt16,System.Int32,System.UInt32,System.Int64,System.UInt64,System.Single,System.Double,System.Decimal",
            ["System.Single"] = "System.Double"
        };

        private static MethodInfo PossibleUserConversion(Type from, Type to)
        {
            Type source = NullableUnderlying(from) ?? from, target = NullableUnderlying(to) ?? to;
            foreach (Type type in new[] { source, target }.Distinct())
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                {
                    if (method.Name != "op_Implicit" || method.ContainsGenericParameters) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 1 && ImplicitConversion(source, parameters[0].ParameterType) != null &&
                        ImplicitConversion(method.ReturnType, target) != null) return method;
                }
            return null;
        }

        private static void AddHint(Type type, ISet<string> hints)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Collections.Generic.ICollection`1")
            {
                string element = IronPythonName(type.GetGenericArguments()[0]);
                hints.Add("IronPython: use System.Collections.Generic.List[" + element + "] for ICollection[" + element + "]; a Python list is not that CLR collection type.");
            }
        }

        private static string IronPythonName(Type type) => type.IsGenericType ? StripArity(type.Name) + "[" + string.Join(", ", type.GetGenericArguments().Select(IronPythonName)) + "]" : type.Name;
        private static Type NullableUnderlying(Type type) => type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Nullable`1" ? type.GetGenericArguments()[0] : null;
        private static string StripArity(string name)
        {
            if (name == null) return null;
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] == '`') { while (i + 1 < name.Length && char.IsDigit(name[i + 1])) i++; }
                else result.Append(name[i]);
            }
            return result.ToString();
        }
        private static bool Identifier(string text) => (char.IsLetter(text[0]) || text[0] == '_') && text.All(c => char.IsLetterOrDigit(c) || c == '_');
        private static JObject Reason(string code, string message, int? index = null)
        { var result = new JObject { ["code"] = code, ["message"] = message }; if (index.HasValue) result["argument_index"] = index.Value; return result; }
        private static string Signature(MethodBase method) => ClrName(method.DeclaringType) + "." + method.Name + "(" +
            string.Join(", ", method.GetParameters().Select(p => ClrName(p.ParameterType) + " " + p.Name + (p.IsOptional ? " [optional]" : ""))) + ")";
        private static string ClrName(Type type) => type.IsByRef ? ClrName(type.GetElementType()) + "&" : type.IsArray ? ClrName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]" :
            type.IsGenericType ? StripArity(type.GetGenericTypeDefinition().FullName) + "[" + string.Join(", ", type.GetGenericArguments().Select(ClrName)) + "]" : type.FullName ?? type.Name;
    }
}
