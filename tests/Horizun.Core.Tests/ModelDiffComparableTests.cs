// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Two snapshots are compared only when they are of one project and were taken by
// one reader version (audit 2026-10-08, 6.13).
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ModelDiffComparableTests
    {
        private const string S = DiffSnapshot.SchemaId;

        [Fact]
        public void One_project_one_reader_is_comparable()
        {
            Assert.Null(ModelDiffRules.Incomparable(S, S, "pi-1", "pi-1"));
        }

        [Fact]
        public void Two_projects_are_refused()
        {
            Assert.Contains("DIFFERENT projects", ModelDiffRules.Incomparable(S, S, "pi-1", "pi-2"));
        }

        [Fact]
        public void An_unreadable_project_identity_is_refused()
        {
            Assert.Contains("could not be read", ModelDiffRules.Incomparable(S, S, null, "pi-1"));
        }

        // MEASURED live 2026-10-09 (Revit 2024 and 2027): a project made from Autodesk's template and
        // another model from the same template carry ONE ProjectInformation UniqueId - it is the
        // template's element - so keyed on it, two different projects compared as one.
        [Fact]
        public void Two_projects_from_one_template_are_told_apart_by_their_creation()
        {
            string a = ModelDiffRules.ProjectIdentity("guid-a", "pi-template");
            string b = ModelDiffRules.ProjectIdentity("guid-b", "pi-template");
            Assert.Contains("DIFFERENT projects", ModelDiffRules.Incomparable(S, S, a, b));
        }

        [Fact]
        public void A_copy_of_a_project_is_the_same_project()
        {
            // A copy keeps the document's creation GUID: before and after a Save As are one project.
            Assert.Null(ModelDiffRules.Incomparable(S, S, ModelDiffRules.ProjectIdentity("guid-a", "pi-1"),
                                                         ModelDiffRules.ProjectIdentity("guid-a", "pi-1")));
        }

        [Fact]
        public void Without_a_creation_guid_the_project_information_is_the_identity()
        {
            // Revit 2023 has no Document.CreationGUID.
            Assert.Equal("project_info:pi-1", ModelDiffRules.ProjectIdentity(null, "pi-1"));
            Assert.Equal("creation:guid-a", ModelDiffRules.ProjectIdentity("guid-a", "pi-1"));
            Assert.Null(ModelDiffRules.ProjectIdentity(null, null));
        }

        [Fact]
        public void Identities_of_two_kinds_cannot_be_told_apart_and_are_refused()
        {
            string r = ModelDiffRules.Incomparable(S, S, "creation:guid-a", "project_info:pi-1");
            Assert.Contains("cannot be compared", r);
        }

        [Fact]
        public void A_snapshot_from_the_name_keyed_reader_is_refused()
        {
            Assert.Contains("different reader versions",
                ModelDiffRules.Incomparable("horizun.model-diff-snapshot/1", S, "pi-1", "pi-1"));
            Assert.Equal("horizun.model-diff-snapshot/2", S);
        }

        // Owner decision 2026-10-10 (option B): MEASURED in Revit 2023, two different projects
        // made from one template share their identity (project_info:...) and compare as "no
        // changes". The comparison still runs, but it says the identity is NOT proven.
        [Fact]
        public void A_creation_guid_proves_the_identity()
        {
            JObject e = ModelDiffRules.IdentityEvidence("creation:1c6b", "creation:1c6b");
            Assert.Equal("creation", (string)e["project_identity_kind"]);
            Assert.True((bool)e["identity_proven"]);
            Assert.Empty((JArray)e["warnings"]);
        }

        [Fact]
        public void Project_information_alone_proves_nothing_and_says_so()
        {
            JObject e = ModelDiffRules.IdentityEvidence("project_info:8764c510-0000c160", "project_info:8764c510-0000c160");
            Assert.Equal("project_info", (string)e["project_identity_kind"]);
            Assert.False((bool)e["identity_proven"]);
            JObject w = (JObject)((JArray)e["warnings"]).Single();
            Assert.Equal(ModelDiffRules.WarningIdentityUnprovable, (string)w["code"]);
            Assert.Equal("identity_unprovable_2023", (string)w["code"]);
            Assert.Contains("same template", (string)w["message"]);
        }
    }
}
