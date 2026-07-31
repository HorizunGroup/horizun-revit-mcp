using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>
    /// A confirmation token approves a PLAN. The plan is a hash over named request
    /// fields, and a name that the command does not accept hashes to the empty marker -
    /// silently, for every request, forever. The field then contributes nothing, and
    /// whatever it was meant to pin is free to change between the rehearsal and the
    /// execution.
    ///
    /// Two commands shipped like that. family_apply hashed "parameters", "set_values"
    /// and "remove" - none of which it accepts - so its approval covered the family and
    /// the file and NOTHING about what would be written. Measured live: a token issued
    /// for {"Width": 3.5} was accepted for {"Width": 9, "Manufacturer": ...}.
    /// bind_shared_param hashed "parameter", "binding" and "shared_parameter_file",
    /// leaving WHICH parameter, from WHICH file, as Instance or Type outside the
    /// approval entirely.
    ///
    /// Neither is visible by reading either side alone: the command names look
    /// plausible, and the schema is correct. Only crossing them shows it.
    /// </summary>
    public class PlanHashFieldTests
    {
        private static string SrcDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                d = d.Parent;
            Assert.True(d != null, "Could not locate src/Horizun.Revit/Commands");
            return Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands");
        }

        /// <summary>Every field a command hashes must be one the contract says it takes.</summary>
        [Fact]
        public void Plan_hash_fields_all_exist_in_the_declared_schema()
        {
            var bad = new List<string>();

            foreach (string path in Directory.EnumerateFiles(SrcDir(), "*Command.cs"))
            {
                string text = File.ReadAllText(path);

                // The whole PlanHash(...) call, however many lines it wraps over.
                Match call = Regex.Match(text, @"PlanHash\(\s*request\s*,(?<args>[^;]*?)\)\s*;", RegexOptions.Singleline);
                if (!call.Success) continue;

                var hashed = Regex.Matches(call.Groups["args"].Value, "\"(?<f>[a-z_]+)\"")
                                  .Cast<Match>().Select(m => m.Groups["f"].Value).ToList();
                if (hashed.Count == 0) continue;

                // Which tool is this? Match the command file to a declared tool by the
                // command name it registers.
                Match nameProp = Regex.Match(text, @"Name\s*=>\s*""(?<n>horizun_[a-z_]+)""");
                if (!nameProp.Success) continue;
                string toolName = nameProp.Groups["n"].Value;

                CommandContract spec = Contract.All.FirstOrDefault(t => t.Command == toolName);
                if (spec == null) { bad.Add(toolName + ": hashes a plan but is not in the contract"); continue; }

                var declared = new HashSet<string>(StringComparer.Ordinal);
                var props = spec.InputSchema?["properties"] as JObject;
                if (props != null) foreach (var p in props.Properties()) declared.Add(p.Name);

                foreach (string f in hashed)
                    if (!declared.Contains(f))
                        bad.Add(toolName + " hashes '" + f + "', which it does not accept");
            }

            Assert.True(bad.Count == 0,
                "These plan hashes name request fields that do not exist, so they hash to nothing and pin " +
                "nothing:\n  " + string.Join("\n  ", bad));
        }

        /// <summary>
        /// The inverse, and the one that actually bites: a field that decides WHAT gets
        /// written must be inside the approval. Listing every optional field would make
        /// the token brittle for no gain, so this pins only the payload-bearing ones.
        /// </summary>
        [Fact]
        public void The_fields_that_say_what_to_write_are_inside_the_approval()
        {
            var required = new Dictionary<string, string[]>
            {
                ["FamilyApplyCommand.cs"] = new[] { "values", "remove_params", "add_shared_params", "save" },
                ["BindSharedParamCommand.cs"] = new[] { "param_guid", "param_name", "binding_kind", "spf_path", "categories" },
                ["WriteParamsCommand.cs"] = new[] { "writes" },
                ["SetKeynoteCommand.cs"] = new[] { "element_ids", "keynote" },
            };

            var missing = new List<string>();
            foreach (var kv in required)
            {
                string path = Path.Combine(SrcDir(), kv.Key);
                Assert.True(File.Exists(path), "Missing command file: " + path);

                Match call = Regex.Match(File.ReadAllText(path), @"PlanHash\(\s*request\s*,(?<args>[^;]*?)\)\s*;",
                                         RegexOptions.Singleline);
                Assert.True(call.Success, kv.Key + " no longer builds a plan hash at all");

                string args = call.Groups["args"].Value;
                foreach (string f in kv.Value)
                    if (!Regex.IsMatch(args, "\"" + Regex.Escape(f) + "\""))
                        missing.Add(kv.Key + " does not bind '" + f + "'");
            }

            Assert.True(missing.Count == 0,
                "A token approved without these would authorise a DIFFERENT write from the one rehearsed:\n  " +
                string.Join("\n  ", missing));
        }
    }
}
