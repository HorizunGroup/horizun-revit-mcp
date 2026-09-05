// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE CONTRACT AND THE REGISTRY ARE TWO LISTS, AND THEY USED TO BE COMPARED BY
// NOBODY. Contract.PluginCommands is the declared answer to "what the add-in must
// register", and it had no caller: a command could be advertised to every MCP
// client and answered by nothing, and the only place that noticed was a caller's
// error message at run time. In the other direction, Dispatcher.Register was an
// indexer assignment, so two commands sharing a name silently kept the second and
// dropped the first, with no test and no log line.
//
// This file is the comparison. It is Revit-free on purpose, so the test suites
// can run it over the real App.cs and the real contract, and so the add-in can
// run it at startup and put the verdict where horizun_health reads it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class RegistryContract
    {
        /// <summary>The verdict of one comparison, with every disagreement named.</summary>
        public sealed class Report
        {
            /// <summary>Commands the contract forwards to Revit that nothing registered.</summary>
            public List<string> Missing = new List<string>();

            /// <summary>Commands registered in the add-in that no contract entry advertises.</summary>
            public List<string> Unadvertised = new List<string>();

            /// <summary>
            /// Names registered more than once. A dictionary cannot hold two, so this is
            /// populated only when the caller hands the REGISTRATION ATTEMPTS rather than the
            /// surviving keys - which the source-level check and Admit both do.
            /// </summary>
            public List<string> Duplicates = new List<string>();

            /// <summary>Registered names that differ from a contract command only by case.</summary>
            public List<string> CaseMismatches = new List<string>();

            public int Registered;
            public int ContractCommands;

            public bool Clean =>
                Missing.Count == 0 && Unadvertised.Count == 0 && Duplicates.Count == 0 && CaseMismatches.Count == 0;

            public JObject ToJson()
            {
                return new JObject
                {
                    ["clean"] = Clean,
                    ["registered"] = Registered,
                    ["contract_commands"] = ContractCommands,
                    ["missing"] = new JArray(Missing),
                    ["unadvertised"] = new JArray(Unadvertised),
                    ["duplicates"] = new JArray(Duplicates),
                    ["case_mismatches"] = new JArray(CaseMismatches),
                    ["means"] = Clean
                        ? "every plugin command the contract advertises is registered exactly once, and nothing is registered that the contract does not name."
                        : "the add-in and the contract disagree. A missing command is advertised to every MCP client and answered by nothing; " +
                          "an unadvertised one runs but no client can call it. Rebuild both halves from one tree."
                };
            }

            public string Describe()
            {
                if (Clean) return "registry matches the contract: " + Registered + " commands.";
                var parts = new List<string>();
                if (Missing.Count > 0) parts.Add("advertised but NOT registered: " + string.Join(", ", Missing));
                if (Duplicates.Count > 0) parts.Add("registered more than once: " + string.Join(", ", Duplicates));
                if (Unadvertised.Count > 0) parts.Add("registered but not in the contract: " + string.Join(", ", Unadvertised));
                if (CaseMismatches.Count > 0) parts.Add("case differs from the contract: " + string.Join(", ", CaseMismatches));
                return "REGISTRY DRIFT - " + string.Join("; ", parts) + ".";
            }
        }

        /// <summary>
        /// The verdict the add-in computed at startup, for horizun_health. Null until
        /// OnStartup has run, and null in a process that never ran it (tests).
        /// </summary>
        public static Report Startup;

        /// <summary>
        /// Compare registration attempts against the contract's plugin commands. Pass the
        /// ATTEMPTS (every name handed to Register, in order), not the dictionary keys, or a
        /// duplicate is invisible by construction.
        /// </summary>
        public static Report Compare(IEnumerable<string> registrationAttempts, IEnumerable<string> contractCommands)
        {
            var r = new Report();
            var attempts = (registrationAttempts ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)).ToList();
            var contract = (contractCommands ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)).ToList();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var seenFold = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in attempts)
            {
                if (!seen.Add(n)) { if (!r.Duplicates.Contains(n)) r.Duplicates.Add(n); continue; }
                string other;
                if (seenFold.TryGetValue(n, out other) && !string.Equals(other, n, StringComparison.Ordinal))
                {
                    // Two spellings of one name. The dispatcher folds case, so the second
                    // would have overwritten the first - report it as a duplicate too.
                    if (!r.Duplicates.Contains(n)) r.Duplicates.Add(n);
                    continue;
                }
                seenFold[n] = n;
            }
            var contractSet = new HashSet<string>(contract, StringComparer.Ordinal);
            var contractFold = new HashSet<string>(contract, StringComparer.OrdinalIgnoreCase);
            foreach (string c in contract)
                if (!seen.Contains(c))
                {
                    if (seenFold.ContainsKey(c)) r.CaseMismatches.Add(c);
                    else r.Missing.Add(c);
                }
            foreach (string n in seen)
                if (!contractSet.Contains(n) && !contractFold.Contains(n)) r.Unadvertised.Add(n);

            r.Missing.Sort(StringComparer.Ordinal);
            r.Unadvertised.Sort(StringComparer.Ordinal);
            r.Duplicates.Sort(StringComparer.Ordinal);
            r.CaseMismatches.Sort(StringComparer.Ordinal);
            r.Registered = seen.Count;
            r.ContractCommands = contractSet.Count;
            return r;
        }

        /// <summary>
        /// Admit one registration into a name set, or throw. Called by Dispatcher.Register
        /// so a duplicate is a startup failure with both names in it, never a silent
        /// overwrite. Case-insensitive, because the dispatcher's lookup is.
        /// </summary>
        public static void Admit(HashSet<string> registered, string name)
        {
            if (registered == null) throw new ArgumentNullException("registered");
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("A command with no name cannot be registered: nothing could call it.");
            if (!registered.Add(name))
                throw new InvalidOperationException(
                    "'" + name + "' is registered twice. The dispatcher holds one handler per name, so the second " +
                    "registration would silently replace the first and the command that answered would not be the " +
                    "one somebody tested. Remove one of the two registrations.");
        }

        // ------------------------------------------------------------- source

        private static readonly Regex RegisterCall =
            new Regex(@"d\.Register\(\s*new\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

        // `string Name => "x"`, `override string Name => "x"`, `Name { get { return "x"; } }`
        // and `Name => ToolName;` with a const beside it - every spelling the tree uses.
        private static readonly Regex NameProperty =
            new Regex(@"string\s+Name\s*(?:=>|\{\s*get\s*\{\s*return)\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*)\s*;)", RegexOptions.Compiled);

        /// <summary>
        /// Every `d.Register(new XCommand(...))` in App.cs, in order, INCLUDING repeats -
        /// the test that fails on a duplicated line needs to see the duplicate.
        /// </summary>
        public static List<string> RegistrationsInSource(string appSource)
        {
            var list = new List<string>();
            if (appSource == null) return list;
            // Comments are not registrations. Strip line comments before matching so a
            // commented-out line neither registers nor duplicates.
            string stripped = Regex.Replace(appSource, @"//[^\r\n]*", "");
            foreach (Match m in RegisterCall.Matches(stripped)) list.Add(m.Groups[1].Value);
            return list;
        }

        // A concrete class deriving from ICommand directly or through RecipeCommand.
        // The abstract base itself declares no name and is not a registration.
        private static readonly Regex ClassDecl =
            new Regex(@"(?<!abstract\s+)class\s+([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?:[A-Za-z_][A-Za-z0-9_.]*\s*,\s*)*(?:Core\.)?(?:ICommand|RecipeCommand)\b", RegexOptions.Compiled);

        /// <summary>
        /// Every ICommand class in one source file with the wire name it declares. A file
        /// may hold more than one command (ScheduleReadCommands.cs does), so this is a map,
        /// and a class whose Name could not be read is mapped to null rather than dropped.
        /// </summary>
        public static Dictionary<string, string> CommandNamesInSource(string commandSource)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (commandSource == null) return map;
            MatchCollection classes = ClassDecl.Matches(commandSource);
            for (int i = 0; i < classes.Count; i++)
            {
                int start = classes[i].Index;
                int end = i + 1 < classes.Count ? classes[i + 1].Index : commandSource.Length;
                string segment = commandSource.Substring(start, end - start);
                Match m = NameProperty.Match(segment);
                string name = null;
                if (m.Success)
                {
                    if (m.Groups[1].Success) name = m.Groups[1].Value;
                    else
                    {
                        // Name => ToolName; the const may sit anywhere in the file.
                        Match c = Regex.Match(commandSource,
                            @"const\s+string\s+" + Regex.Escape(m.Groups[2].Value) + @"\s*=\s*""([^""]+)""");
                        if (c.Success) name = c.Groups[1].Value;
                    }
                }
                map[classes[i].Groups[1].Value] = name;
            }
            return map;
        }

        /// <summary>The health block: the startup verdict, or a named absence.</summary>
        public static JObject HealthBlock()
        {
            if (Startup == null)
                return new JObject
                {
                    ["clean"] = null,
                    ["means"] = "the startup comparison has not run in this process, so nothing here is known."
                };
            return Startup.ToJson();
        }
    }
}
