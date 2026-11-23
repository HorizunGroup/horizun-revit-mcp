using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using static Horizun.Core.Tests.ReliabilityRound13Tests;

namespace Horizun.Core.Tests
{
    public class ReliabilityRound15Tests
    {
        [Fact]
        public void Hiding_a_link_type_in_an_earlier_child_invalidates_the_following_unhide()
        {
            var (doc, view) = LinkVisibilityFixture(typeHidden: false);
            var tool = new ViewVisibilityCommand();
            var plan = new ExecutePlanCommand(name => name == tool.Name ? tool : null);
            JObject request = Graph(tool, UnhideRequest());
            request["actions"][0]["tool"] = tool.Name;
            request["actions"][0]["arguments"] = JObject.Parse("{'operation':'hide','view_id':10,'element_ids':[20]}");

            CommandResult applied = Apply(plan, doc, request);

            Assert.False(applied.Success);
            AssertChildRefusedAndGroupRolledBack(applied.Detail, "view visibility edit");
            Assert.Equal(1, doc.TransactionStarts);
            Assert.Equal(1, view.HideWrites);
            Assert.Equal(0, view.UnhideWrites);
            Assert.False(doc.Assimilated);
            Assert.Equal(new long[] { 101, 103 }, view.HiddenIds.OrderBy(id => id));
        }

        [Theory]
        [InlineData("type_hidden")]
        [InlineData("sibling_replaced")]
        [InlineData("sibling_already_hidden")]
        [InlineData("unchanged")]
        public void Unhide_binds_the_type_and_siblings_even_when_the_requested_instance_stays_the_same(string change)
        {
            var (doc, view) = LinkVisibilityFixture(typeHidden: change != "type_hidden");
            var tool = new ViewVisibilityCommand();
            var plan = PlanWith(tool, () =>
            {
                if (change == "type_hidden") view.HiddenIds.Add(20);
                if (change == "sibling_replaced")
                {
                    var replacement = new RevitLinkInstance
                        { Id = new ElementId(102), TestTypeId = 20, TestUniqueId = "different-instance" };
                    doc.Elements[102] = replacement;
                    doc.TestLinkDocuments[1] = replacement;
                }
                if (change == "sibling_already_hidden") view.HiddenIds.Add(102);
            });
            JObject request = Graph(tool, UnhideRequest());

            CommandResult applied = Apply(plan, doc, request, preview =>
            {
                var visibility = preview["actions"][1]["data"]["plan"];
                Assert.Equal(101, Assert.Single((JArray)visibility["element_ids"]).Value<long>());
                Assert.Equal(change == "type_hidden" ? new long[] { 20, 101 } : new long[] { 20, 101, 102, 103 },
                    visibility["unhide_targets"].Select(t => t.Value<long>("element_id")));
            });

            if (change == "unchanged")
            {
                Assert.True(applied.Success, applied.Error);
                Assert.Equal(2, view.UnhideWrites);
                Assert.Equal(1, view.HideWrites);
                Assert.Equal(new long[] { 102, 103 }, view.HiddenIds.OrderBy(id => id));
                var checks = Assert.IsType<JArray>(((JObject)applied.Data)["actions"][1]["data"]["postconditions"]["properties"]);
                Assert.Equal(new[] { "element:20", "element:101", "element:102", "element:103" },
                    checks.Select(row => row.Value<string>("property")));
                Assert.All(checks, row => Assert.True(row.Value<bool>("matches")));
                return;
            }

            Assert.False(applied.Success);
            AssertChildRefusedAndGroupRolledBack(applied.Detail, "view visibility edit");
            Assert.Equal(0, view.UnhideWrites);
            Assert.Equal(0, view.HideWrites);
            Assert.Equal(0, doc.TransactionStarts);
            Assert.Equal(change == "type_hidden" ? new long[] { 101, 103 } : new long[] { 20, 101, 103 },
                view.HiddenIds.OrderBy(id => id));
            Assert.Equal("102", doc.Elements[102].UniqueId);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Direct_unhide_preserves_inherited_and_individual_hides_on_unrequested_links(bool requestedIndividuallyHidden)
        {
            var (doc, view) = LinkVisibilityFixture(typeHidden: true);
            view.TestInheritLinkTypeHidden = true;
            if (!requestedIndividuallyHidden) view.HiddenIds.Remove(101);
            var tool = new ViewVisibilityCommand();
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            JObject request = UnhideRequest();
            ConfirmUnhide(tool, app, request);
            Assert.True(doc.Elements[102].IsHidden(view));
            Assert.DoesNotContain(102L, view.HiddenIds);

            CommandResult applied = tool.Execute(app, request.ToString());

            Assert.True(applied.Success, applied.Error);
            Assert.Equal(requestedIndividuallyHidden ? 2 : 1, view.UnhideWrites);
            Assert.Equal(1, view.HideWrites);
            Assert.Equal(new long[] { 102, 103 }, view.HiddenIds.OrderBy(id => id));
            var checks = Assert.IsType<JArray>(((JObject)applied.Data)["postconditions"]["properties"]);
            Assert.Equal(new[] { "element:20", "element:101", "element:102", "element:103" },
                checks.Select(row => row.Value<string>("property")));
            Assert.All(checks, row => Assert.True(row.Value<bool>("matches")));
        }

        [Fact]
        public void A_direct_unhide_token_refuses_a_replaced_sibling_with_the_same_element_id()
        {
            var (doc, view) = LinkVisibilityFixture(typeHidden: true);
            view.TestInheritLinkTypeHidden = true;
            var tool = new ViewVisibilityCommand();
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            JObject request = UnhideRequest();
            ConfirmUnhide(tool, app, request);
            var replacement = new RevitLinkInstance
                { Id = new ElementId(102), TestTypeId = 20, TestUniqueId = "replacement-sibling" };
            doc.Elements[102] = replacement;
            doc.TestLinkDocuments[1] = replacement;

            CommandResult applied = tool.Execute(app, request.ToString());

            Assert.False(applied.Success);
            Assert.Equal("StalePlan", applied.Detail.Value<string>("confirmation_state"));
            Assert.Equal(0, doc.TransactionStarts);
            Assert.Equal(0, view.UnhideWrites);
            Assert.Equal(0, view.HideWrites);
            Assert.Equal(new long[] { 20, 101, 103 }, view.HiddenIds.OrderBy(id => id));
        }

        [Theory]
        [InlineData(20, true)]
        [InlineData(102, false)]
        [InlineData(103, false)]
        public void Unhide_rolls_back_when_the_committed_type_or_sibling_disagrees(long id, bool readsHidden)
        {
            var (doc, view) = LinkVisibilityFixture(typeHidden: true);
            doc.TestAfterCommit = () =>
            {
                if (readsHidden) view.HiddenIds.Add(id);
                else view.HiddenIds.Remove(id);
            };
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var tool = new ViewVisibilityCommand();
            JObject request = UnhideRequest();
            ConfirmUnhide(tool, app, request);

            CommandResult failed = tool.Execute(app, request.ToString());

            Assert.False(failed.Success);
            Assert.False(failed.Detail.Value<bool>("changes_applied"));
            var checks = (JArray)failed.Detail["postconditions"]["properties"];
            var mismatch = Assert.Single(checks, row => !row.Value<bool>("matches"));
            Assert.Equal("element:" + id, mismatch.Value<string>("property"));
            Assert.Equal(readsHidden, mismatch.Value<bool>("found_in_committed_model"));
            Assert.Equal(new long[] { 20, 101, 103 }, view.HiddenIds.OrderBy(hidden => hidden));
        }

        

        private static void ConfirmUnhide(ICommand tool, UIApplication app, JObject request)
        {
            CommandResult preview = tool.Execute(app, request.ToString());
            Assert.True(preview.Success, preview.Error);
            request["confirmation_token"] = ((JObject)preview.Data)["confirmation_token"];
            request["dry_run"] = false;
            request["idempotency_key"] = "confirm-unhide-" + System.Guid.NewGuid();
        }

        private static JObject UnhideRequest() =>
            JObject.Parse("{'operation':'unhide','view_id':10,'element_ids':[101]}");

        private static (Document, View) LinkVisibilityFixture(bool typeHidden)
        {
            var doc = new Document { Title = "Link visibility plan", FixtureConfirmations = new ConfirmationStore() };
            var view = new View { Id = new ElementId(10), Document = doc };
            doc.Elements.Add(10, view);
            doc.Elements.Add(20, new ElementType { Id = new ElementId(20), Name = "Link type" });
            foreach (long id in new long[] { 101, 102, 103 })
            {
                var link = new RevitLinkInstance { Id = new ElementId(id), TestTypeId = 20 };
                doc.Elements.Add(id, link);
                doc.TestLinkDocuments.Add(link);
            }
            view.HiddenIds.UnionWith(new long[] { 101, 103 });
            if (typeHidden) view.HiddenIds.Add(20);
            doc.CaptureFixtureState = () =>
            {
                long[] hidden = view.HiddenIds.ToArray();
                RevitLinkInstance[] links = doc.TestLinkDocuments.ToArray();
                return () =>
                {
                    view.HiddenIds.Clear();
                    view.HiddenIds.UnionWith(hidden);
                    doc.TestLinkDocuments.Clear();
                    doc.TestLinkDocuments.AddRange(links);
                };
            };
            return (doc, view);
        }
    }
}
