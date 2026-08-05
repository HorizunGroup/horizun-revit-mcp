// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The acceptance test 4.0 wrote for itself, finally executable: "Three
// requirement sets - ISO 19650, IFC/buildingSMART, COBie - one command, no
// standard-specific code. If horizun_check_requirements needs an
// `if (standard == ...)` anywhere, this document is not finished."
//
// The command is still behind the tool freeze, but the claim is about the
// LOADER, and the loader exists. This file is the no-branch proof: one loop,
// three documents that ask entirely different questions - a naming grammar, a
// class mapping, handover completeness - and not one line below knows which is
// which.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ReferenceRequirementSetTests
    {
        private static string SetsDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "docs", "requirement-sets")))
                d = d.Parent;
            Assert.True(d != null, "docs/requirement-sets not found from " + AppContext.BaseDirectory);
            return Path.Combine(d.FullName, "docs", "requirement-sets");
        }

        /// <summary>
        /// ONE loop, no branch on which standard a file is. That absence is the test:
        /// the day someone needs a special case here, the schema has failed its own
        /// acceptance criterion and docs/requirement-set.md says so in its own words.
        /// </summary>
        [Fact]
        public void The_three_reference_standards_load_through_one_code_path()
        {
            var loaded = new List<RequirementSet>();
            foreach (string file in Directory.GetFiles(SetsDir(), "*.json").OrderBy(x => x))
            {
                JObject doc = JObject.Parse(File.ReadAllText(file));
                loaded.Add(RequirementSet.Load(doc, _ => null));
            }

            Assert.True(loaded.Count >= 3, "expected the three reference sets; found " + loaded.Count);
            Assert.Equal(loaded.Count, loaded.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.All(loaded, s => Assert.NotEmpty(s.Rules));
            Assert.All(loaded, s => Assert.False(string.IsNullOrWhiteSpace(s.Version),
                "a finding always cites the version it came from"));

            // The three ask DIFFERENT question shapes - that is the point of the schema.
            var operators = loaded.SelectMany(s => s.Rules).Select(r => r.Operator).Distinct().ToList();
            Assert.Contains("matches", operators);      // naming grammar (ISO 19650)
            Assert.Contains("not_equals", operators);   // class mapping (IFC)
            Assert.Contains("not_empty", operators);    // handover completeness (COBie)
        }

        /// <summary>
        /// Every remediation in the reference sets names a tool the contract actually
        /// ships. A requirement set cannot ask the bridge to do something it has no
        /// verified command for - that would be a standard smuggling in behaviour, and
        /// it is cheaper to catch in the reference documents everyone will copy.
        /// </summary>
        [Fact]
        public void Reference_remediations_name_real_tools()
        {
            var known = new HashSet<string>(Horizun.Contracts.Contract.All.Select(c => c.Name), StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(SetsDir(), "*.json"))
            {
                JObject doc = JObject.Parse(File.ReadAllText(file));
                RequirementSet set = RequirementSet.Load(doc, _ => null);
                foreach (Requirement rule in set.Rules.Where(r => r.RemediationTool != null))
                    Assert.True(known.Contains(rule.RemediationTool),
                        Path.GetFileName(file) + " rule '" + rule.Id + "' names remediation tool '" +
                        rule.RemediationTool + "', which the contract does not ship");
            }
        }
    }
}
