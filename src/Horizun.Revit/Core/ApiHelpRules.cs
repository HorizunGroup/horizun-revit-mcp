// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_api_help (read-only).
//
// Field data: scripts failed on AttributeError / TypeError against members that do not
// exist in THIS Revit (a property renamed between versions, a method that takes other
// arguments), and a person had to ask "which methods does WallSweep have in this version".
// The answer is in the assemblies Revit has loaded, so it is read by reflection - no
// documentation of another version, no memory. Pure reflection over System.Type, so it is
// tested on .NET types without a Revit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ApiHelpRules
    {
        public const int DefaultMaxMembers = 200;
        public const int MaxSuggestions = 8;

        /// <summary>
        /// Behaviour measured in the field that the signatures do not show, by full type name. Reported for the type and
        /// for every base type it derives from, so TextNote also carries what was measured on TextElement and Element.
        /// </summary>
        private static readonly Dictionary<string, string[]> FieldTrapsByType = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Autodesk.Revit.DB.TextElement"] = new[]
            {
                "Width and Height are PAPER feet, not model feet: multiplying Width by the view scale gave a 1.75 m note (Revit 2024). Validate a width against GetMinimumAllowedWidth/GetMaximumAllowedWidth (paper feet).",
                "Width re-reads what was written even when the note no longer wraps: a box that is too narrow prints ONE long line. Count the printed lines from Height (horizun_annotate operation=text, expected_lines)."
            },
            ["Autodesk.Revit.DB.TextNote"] = new[]
            {
                "A right-aligned note whose leader elbow falls inside the text box is drawn with the arrow from the OTHER side, across the sheet. Choose the side with AddLeader(TNLT_*_L / TNLT_*_R) and keep the elbow outside the box (horizun_annotate leader_side).",
                "In a 3D/isometric view with \"show annotation categories\" off, TextNote.Create succeeds and the note is never visible (View.AreAnnotationCategoriesHidden)."
            },
            ["Autodesk.Revit.DB.ViewSection"] = new[]
            {
                "ElementTransformUtils.MoveElement on a ViewSection does nothing and does not warn: the cut plane is fixed when the view is created. Recreate the section or change its CropBox, then re-read it.",
                "ViewSection.CreateReferenceSection can return null. Check for null and verify the result in the model. Why a mark is not in a view: horizun_query_planimetry mode=section_mark_diagnosis."
            },
            ["Autodesk.Revit.DB.ElementType"] = new[]
            {
                "IronPython resolves the overload of ElementType.Duplicate(string) badly when it is called bound. Call it unbound, passing the type first: ElementType.Duplicate(base, \"name\")."
            },
            ["Autodesk.Revit.DB.Element"] = new[]
            {
                "get_BoundingBox(view) of an ANNOTATION was measured with its coordinates multiplied by the view scale, so every note looked outside the crop. Check the box contains the annotation's own anchor (TextNote.Coord, tag head) before using it; Horizun's own reads divide by the scale when only the divided box contains it."
            }
        };

        /// <summary>The field traps for a type and its base types, most specific first; empty when none are known.</summary>
        public static JArray FieldTraps(Type t)
        {
            var rows = new JArray();
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                string[] traps;
                if (cur.FullName != null && FieldTrapsByType.TryGetValue(cur.FullName, out traps))
                    foreach (string trap in traps) rows.Add(new JObject { ["type"] = cur.FullName, ["trap"] = trap });
            }
            return rows;
        }

        /// <summary>Signature inputs stay declarative even on direct bridge calls.</summary>
        public static string ValidateSignatureRequest(JObject request)
        {
            JToken op = request["operation"];
            if (op != null && (op.Type != JTokenType.String || ((string)op != "describe" && (string)op != "check_signature")))
                return "operation must be describe (default) or check_signature.";
            if ((string)op != "check_signature") return null;
            JToken member = request["member_filter"];
            if (member?.Type != JTokenType.String || !System.Text.RegularExpressions.Regex.IsMatch((string)member,
                @"^(\.ctor|[A-Za-z_][A-Za-z0-9_]*)$"))
                return "check_signature requires member_filter: an exact method name or .ctor.";
            if (!(request["argument_types"] is JArray args) || args.Count > 32)
                return "check_signature requires argument_types: an array of 0..32 CLR type names (use [] for zero arguments).";
            if (args.Any(a => a.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)a) || ((string)a).Length > 512))
                return "argument_types must contain only non-empty CLR type names of at most 512 characters, never values or null.";
            return null;
        }

        /// <summary>Every public type of the assemblies, by simple and full name.</summary>
        public static List<Type> PublicTypes(IEnumerable<Assembly> assemblies)
        {
            var list = new List<Type>();
            foreach (Assembly a in assemblies)
            {
                Type[] types;
                try { types = a.GetExportedTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null && t.IsPublic).ToArray(); }
                catch { continue; }
                list.AddRange(types);
            }
            return list;
        }

        /// <summary>Exact full name, then exact simple name (several = ambiguous), else none.</summary>
        public static List<Type> Find(IList<Type> types, string name)
        {
            string n = (name ?? "").Trim();
            var byFull = types.Where(t => string.Equals(t.FullName, n, StringComparison.Ordinal)).ToList();
            if (byFull.Count > 0) return byFull;
            var bySimple = types.Where(t => string.Equals(t.Name, n, StringComparison.Ordinal)).ToList();
            if (bySimple.Count > 0) return bySimple;
            return types.Where(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(t.FullName, n, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>The closest type names: substring matches first, then edit distance.</summary>
        public static List<string> Suggest(IList<Type> types, string name, int max = MaxSuggestions)
        {
            string n = (name ?? "").Trim();
            string simple = n.Contains(".") ? n.Substring(n.LastIndexOf('.') + 1) : n;
            return types
                .Select(t => new { t.FullName, Score = Score(t.Name, simple) })
                .Where(x => x.FullName != null && x.Score < int.MaxValue)
                .OrderBy(x => x.Score).ThenBy(x => x.FullName, StringComparer.Ordinal)
                .Select(x => x.FullName).Distinct().Take(max).ToList();
        }

        private static int Score(string candidate, string wanted)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(wanted)) return int.MaxValue;
            if (candidate.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) return candidate.Length - wanted.Length;
            int d = Levenshtein(candidate.ToLowerInvariant(), wanted.ToLowerInvariant());
            return d <= Math.Max(2, wanted.Length / 3) ? 100 + d : int.MaxValue;
        }

        public static int Levenshtein(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        /// <summary>A readable C#-style name: List`1[Wall] -> IList&lt;Wall&gt;, nested '+' -> '.'.</summary>
        public static string TypeName(Type t)
        {
            if (t == null) return "void";
            if (t.IsByRef) return TypeName(t.GetElementType()) + "&";
            if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
            if (t.IsGenericType)
            {
                string bare = t.Name.Contains("`") ? t.Name.Substring(0, t.Name.IndexOf('`')) : t.Name;
                return bare + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">";
            }
            switch (t.FullName)
            {
                case "System.Void": return "void";
                case "System.Boolean": return "bool";
                case "System.Int32": return "int";
                case "System.Int64": return "long";
                case "System.Double": return "double";
                case "System.String": return "string";
                case "System.Object": return "object";
            }
            return t.Name;
        }

        /// <summary>
        /// The public members of one type. Property accessors, event add/remove and operators are
        /// folded into their member; overloads are separate rows. filter is a case-insensitive
        /// substring of the member name.
        /// </summary>
        public static JObject Describe(Type t, string filter, bool includeInherited, int maxMembers)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            if (!includeInherited) flags |= BindingFlags.DeclaredOnly;
            Func<string, bool> pass = name => string.IsNullOrEmpty(filter) || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            var rows = new List<JObject>();
            if (t.IsEnum)
            {
                foreach (string name in Enum.GetNames(t).Where(pass))
                    rows.Add(new JObject { ["kind"] = "enum_value", ["name"] = name });
            }
            else
            {
                foreach (ConstructorInfo c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                    if (pass(t.Name)) rows.Add(new JObject { ["kind"] = "constructor", ["name"] = t.Name, ["signature"] = t.Name + "(" + Params(c.GetParameters()) + ")" });
                foreach (PropertyInfo p in Safe(() => t.GetProperties(flags)) ?? new PropertyInfo[0])
                {
                    if (!pass(p.Name)) continue;
                    MethodInfo get = p.GetGetMethod(), set = p.GetSetMethod();
                    MethodInfo any = get ?? set;
                    var row = new JObject
                    {
                        ["kind"] = "property", ["name"] = p.Name, ["type"] = TypeName(p.PropertyType),
                        ["get"] = get != null, ["set"] = set != null, ["static"] = any != null && any.IsStatic
                    };
                    ParameterInfo[] idx = p.GetIndexParameters();
                    if (idx.Length > 0) row["indexer"] = Params(idx);
                    if (p.DeclaringType != t) row["declared_by"] = TypeName(p.DeclaringType);
                    rows.Add(row);
                }
                foreach (MethodInfo m in Safe(() => t.GetMethods(flags)) ?? new MethodInfo[0])
                {
                    if (m.IsSpecialName || !pass(m.Name)) continue;
                    // Equals/GetHashCode/GetType/ToString on every type are noise unless asked for by name.
                    if (m.DeclaringType == typeof(object) && string.IsNullOrEmpty(filter)) continue;
                    var row = new JObject
                    {
                        ["kind"] = "method", ["name"] = m.Name, ["static"] = m.IsStatic,
                        ["signature"] = TypeName(m.ReturnType) + " " + m.Name + "(" + Params(m.GetParameters()) + ")"
                    };
                    if (m.DeclaringType != t) row["declared_by"] = TypeName(m.DeclaringType);
                    rows.Add(row);
                }
                foreach (EventInfo ev in Safe(() => t.GetEvents(flags)) ?? new EventInfo[0])
                    if (pass(ev.Name)) rows.Add(new JObject { ["kind"] = "event", ["name"] = ev.Name, ["type"] = TypeName(ev.EventHandlerType) });
            }
            List<JObject> ordered = rows.OrderBy(r => KindOrder((string)r["kind"])).ThenBy(r => (string)r["name"], StringComparer.Ordinal)
                                        .ThenBy(r => (string)r["signature"] ?? "", StringComparer.Ordinal).ToList();
            var chain = new JArray();
            for (Type b = t.BaseType; b != null && b != typeof(object); b = b.BaseType) chain.Add(b.FullName);
            var result = new JObject
            {
                ["full_name"] = t.FullName,
                ["assembly"] = t.Assembly.GetName().Name,
                ["assembly_version"] = t.Assembly.GetName().Version?.ToString(),
                ["kind"] = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static_class" : t.IsAbstract ? "abstract_class" : "class",
                ["base_types"] = chain,
                ["interfaces"] = new JArray(t.GetInterfaces().Select(i => TypeName(i)).OrderBy(x => x, StringComparer.Ordinal).Take(30)),
                ["members_total"] = ordered.Count,
                ["members_shown"] = Math.Min(ordered.Count, maxMembers),
                ["truncated"] = ordered.Count > maxMembers,
                ["members"] = new JArray(ordered.Take(maxMembers))
            };
            // A misspelling yields no substring matches. Offer bounded candidates
            // from this actual type, preserving their reflected signatures.
            if (ordered.Count == 0 && !string.IsNullOrWhiteSpace(filter) && filter.Length <= 128)
            {
                JObject all = Describe(t, null, includeInherited, int.MaxValue);
                var candidates = ((JArray)all["members"]).OfType<JObject>()
                    .Select(row => new { Row = row, Distance = Score((string)row["name"], filter) })
                    .Where(x => x.Distance < int.MaxValue)
                    .OrderBy(x => x.Distance).ThenBy(x => (string)x.Row["name"], StringComparer.Ordinal)
                    .Take(MaxSuggestions).Select(x => x.Row.DeepClone());
                result["closest_members"] = new JArray(candidates);
            }
            return result;
        }

        private static int KindOrder(string kind)
        {
            switch (kind) { case "enum_value": return 0; case "constructor": return 1; case "property": return 2; case "method": return 3; default: return 4; }
        }

        private static string Params(ParameterInfo[] ps) =>
            string.Join(", ", ps.Select(p => (p.IsOut ? "out " : "") + TypeName(p.IsOut ? p.ParameterType.GetElementType() : p.ParameterType) + " " + p.Name));

        private static T Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
    }
}
