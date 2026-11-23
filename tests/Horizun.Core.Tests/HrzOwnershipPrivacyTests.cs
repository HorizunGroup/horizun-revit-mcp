// 5.0.4 privacy: the ownership mark no longer writes the Revit user, the Windows account (which names the machine) or the SID into the
// model. Version 3 carries an anonymous owner id and the MCP version; the rule compares the owner id alone.
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HrzOwnershipPrivacyTests
    {
        private const string A = "0123456789abcdef0123456789abcdef";
        private const string B = "fedcba9876543210fedcba9876543210";

        [Fact]
        public void A_version_3_mark_is_ours_only_with_the_same_owner_id()
        {
            var me = new HrzOwnershipRules.UserRef { User = "pat", Windows = @"PC-1\pat", Sid = "S-1-5-21-1", Owner = A };
            Assert.True(HrzOwnershipRules.SameUser(new HrzOwnershipRules.UserRef { Owner = A.ToUpperInvariant() }, me));
            Assert.False(HrzOwnershipRules.SameUser(new HrzOwnershipRules.UserRef { Owner = B }, me));
        }

        [Fact]
        public void A_version_3_mark_ignores_names_and_fails_closed_without_a_valid_owner()
        {
            // Same user name, same SID in memory: still not ours when the owner differs, is missing or is not an id.
            var me = new HrzOwnershipRules.UserRef { User = "pat", Sid = "S-1-5-21-1", Owner = A };
            Assert.False(HrzOwnershipRules.SameUser(new HrzOwnershipRules.UserRef { Owner = "-", User = "pat", Sid = "S-1-5-21-1" }, me));
            Assert.False(HrzOwnershipRules.SameUser(new HrzOwnershipRules.UserRef { Owner = "pat" }, me));
            Assert.False(HrzOwnershipRules.SameUser(new HrzOwnershipRules.UserRef { Owner = A }, new HrzOwnershipRules.UserRef { User = "pat" }));
        }

        [Fact]
        public void A_version_2_mark_made_before_5_0_4_is_still_compared_by_user()
        {
            var me = new HrzOwnershipRules.UserRef { User = "pat", Sid = "S-1-5-21-1", Owner = A };
            Assert.True(HrzOwnershipRules.SameUser(new HrzOwnershipRules.UserRef { User = "pat", Sid = "S-1-5-21-1" }, me));
        }

        [Fact]
        public void The_owner_id_is_created_once_kept_and_holds_no_name()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-owner-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "owner-id");
            try
            {
                string first = HrzOwnershipRules.OwnerIdFrom(path);
                Assert.True(HrzOwnershipRules.IsOwnerId(first));
                Assert.Equal(first, HrzOwnershipRules.OwnerIdFrom(path));
                Assert.DoesNotContain(Environment.UserName.ToLowerInvariant(), first);

                File.WriteAllText(path, Environment.MachineName + "\\" + Environment.UserName);   // a name is never accepted as an id
                string replaced = HrzOwnershipRules.OwnerIdFrom(path);
                Assert.True(HrzOwnershipRules.IsOwnerId(replaced));
                Assert.NotEqual(first, replaced);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void The_stamp_writes_only_anonymous_fields()
        {
            string src = Source("src", "Horizun.Revit", "Core", "HrzOwnership.cs");
            // Every field a stamp sets on an entity.
            var set = Regex.Matches(src, @"entity\w*\.Set\((F\w+)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.Equal(new[] { "FOwner", "FSession", "FTool", "FUtc", "FVersion" }, set.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            // Version 2 (user, winuser, sid) has no builder any more, so nothing can write it.
            Assert.DoesNotContain("new SchemaBuilder(SchemaGuidV2)", src);
            Assert.DoesNotContain("MachineName", src);
            // A version 2 mark is never echoed with its personal fields.
            Assert.DoesNotContain("[\"winuser\"]", src);
            Assert.DoesNotContain("[\"sid\"]", src);
        }

        [Fact]
        public void Purge_legacy_marks_deletes_only_the_version_2_entity_and_rereads_the_count()
        {
            string src = Source("src", "Horizun.Revit", "Commands", "ManageParametersCommand.Marks.cs");
            string main = Source("src", "Horizun.Revit", "Commands", "ManageParametersCommand.cs");
            string contract = Source("src", "Horizun.Contracts", "Contract.cs");
            // Reachable: listed, built and declared in the contract enum.
            Assert.Contains("\"purge_legacy_marks\"", main);
            Assert.Contains("BuildPurgeLegacyMarks(doc, r, out error)", main);
            Assert.Contains("\"\"global_delete\"\", \"\"purge_legacy_marks\"\"", contract);
            // Only the version 2 schema is looked up and deleted; never an element, never another schema.
            var deletes = Regex.Matches(src, @"DeleteEntity\((\w+)\)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToArray();
            Assert.Equal(new[] { "schema" }, deletes);
            Assert.Contains("Schema schema = Schema.Lookup(HrzOwnership.SchemaGuidV2);", src);
            Assert.DoesNotContain("SchemaGuidV3", src);
            Assert.DoesNotContain(".Delete(", src);
            Assert.DoesNotContain("EraseSchema", src);
            // Counted after the commit: no target left, and only the skipped ones remain.
            // Both compared properties are the ones the check REQUIRES (live 5.0.4: a wrong required name failed every rehearsal).
            Assert.Contains("new PostconditionCheck(\"targets_still_marked\", \"v2_marks_after\")", src);
            Assert.Contains(".Compare(\"targets_still_marked\", 0L, targetsLeft)", src);
            Assert.Contains(".Compare(\"v2_marks_after\", (long)skippedCount, (long)left.Count)", src);
            // Elements owned by another user of a collaborative model are skipped, and nothing personal is echoed.
            Assert.Contains("CheckoutStatus.OwnedByOtherUser", src);
            Assert.DoesNotContain("FUser", src);
            Assert.DoesNotContain("Get<string>", src);
            // Nothing to purge is answered as a read, before any transaction or token.
            int noneAt = main.IndexOf("LegacyMarksNone(d)", StringComparison.Ordinal);
            int gateAt = main.IndexOf("DocumentGate.ForMutation(uiapp, request, Name)", StringComparison.Ordinal);
            Assert.True(noneAt > 0 && noneAt < gateAt);
        }

        private static string Source(params string[] parts)
        {
            DirectoryInfo d = new DirectoryInfo(Here());
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(new[] { d.FullName }.Concat(parts).ToArray()));
        }

        private static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
    }
}
